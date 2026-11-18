using System.Net;
using System.Security.Cryptography;
using System.Text;
using DWM157Gateway.Observer;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("observer.json", optional: true, reloadOnChange: false);
var options = builder.Configuration.GetSection("Observer").Get<ObserverOptions>() ?? new();
options.Validate();
builder.WebHost.ConfigureKestrel(server =>
{
    server.Limits.MaxRequestBodySize = 16384;
    server.Listen(IPAddress.Parse(options.BindAddress), options.Port);
});
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) });
builder.Services.AddSingleton<GatewayProbe>();
builder.Services.AddSingleton<VersionInventory>();
builder.Services.AddSingleton<FcmPushSender>();
builder.Services.AddSingleton<ObserverWorker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ObserverWorker>());
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
var app = builder.Build();
app.Use(async (context, next) =>
{
    bool Match(string expected, string supplied) => CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
    var remote = context.Connection.RemoteIpAddress;
    if (remote?.IsIPv4MappedToIPv6 == true) remote = remote.MapToIPv4();
    var reporter = context.Request.Path == "/api/v1/versions/report" &&
        !string.IsNullOrEmpty(options.WindowsReporterToken) && options.ReporterAddresses.Contains(remote?.ToString() ?? "") &&
        Match(options.WindowsReporterToken, context.Request.Headers["X-Reporter-Token"].ToString());
    if (reporter) context.Items["windowsReporter"] = true;
    else
    {
        if (!options.AccessOptions.IsRemoteAddressAllowed(remote)) { context.Response.StatusCode = 403; return; }
        var localDevelopment = IPAddress.IsLoopback(IPAddress.Parse(options.BindAddress)) && options.ApiToken.Length == 0;
        if (!localDevelopment && !Match(options.ApiToken, context.Request.Headers["X-Monitor-Token"].ToString()))
        { context.Response.StatusCode = 401; return; }
    }
    await next(context);
});
app.MapGet("/api/v1/observer", (ObserverWorker worker) => worker.Snapshot);
app.MapGet("/api/v1/health", () => new { status = "healthy", role = "independent-observer", timestampUtc = DateTimeOffset.UtcNow });
app.MapPost("/api/v1/push/register", async (PushRegistration registration, ObserverWorker worker, CancellationToken token) =>
{
    try { await worker.RegisterAsync(registration.Token, token); return Results.Ok(new { registered = true }); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_registration" }); }
});
app.MapPost("/api/v1/push/test", async (ObserverWorker worker, CancellationToken token) =>
    await worker.SendTestAsync(token) ? Results.Ok(new { acceptedByProvider = true })
        : Results.Conflict(new { error = "push_not_ready_or_provider_failed" }));
app.MapPost("/api/v1/versions/report", (VersionReport report, HttpContext context, VersionInventory inventory) =>
{
    var windows = context.Items.ContainsKey("windowsReporter");
    var address = context.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "unknown";
    if (string.IsNullOrWhiteSpace(report.Name) || string.IsNullOrWhiteSpace(report.InstalledVersion) ||
        report.Name.Length > 100 || report.InstalledVersion.Length > 150 || report.RunningVersion?.Length > 150 ||
        VersionInventory.Normalize(report.InstalledVersion) is null)
        return Results.BadRequest(new { error = "invalid_version_report" });
    inventory.Put(new(address, report.Name, windows ? "windows" : "android",
        report.InstalledVersion, windows ? report.RunningVersion : null, DateTimeOffset.UtcNow,
        windows ? "point-a-reporter" : "android-package", true));
    return Results.Ok(new { recorded = true });
});
app.Run();

public sealed record PushRegistration(string Token);
public sealed record VersionReport(string Name, string InstalledVersion, string? RunningVersion);
