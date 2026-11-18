using System.Net;
using System.Security.Cryptography;
using System.Text;
using DWM157Gateway.Monitoring;

var builder = WebApplication.CreateBuilder(args);
var options = builder.Configuration.GetSection("Monitoring").Get<MonitoringOptions>() ?? new MonitoringOptions();
options.ApiToken = Environment.GetEnvironmentVariable("DWM157_MONITOR_TOKEN") ?? options.ApiToken;
var bindAddress = options.GetValidatedBindAddress();

builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(bindAddress, options.Port));
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<GatewaySnapshotReader>();
builder.Services.AddSingleton<CallHistoryReader>();
builder.Services.AddSingleton<NetworkQualityMonitor>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<NetworkQualityMonitor>());
builder.Services.AddSingleton<StatusCollector>();
builder.Services.AddSingleton<TailscaleVersionMonitor>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<TailscaleVersionMonitor>());
builder.Services.AddSingleton<TelemetryHistoryStore>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<TelemetryHistoryStore>());

var app = builder.Build();

app.Use(async (context, next) =>
{
    if (!options.IsRemoteAddressAllowed(context.Connection.RemoteIpAddress))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "peer_not_allowed" });
        return;
    }

    if (IPAddress.IsLoopback(bindAddress) && string.IsNullOrWhiteSpace(options.ApiToken))
    {
        await next(context);
        return;
    }

    var supplied = context.Request.Headers["X-Monitor-Token"].ToString();
    var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(options.ApiToken));
    var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
    if (!CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
        return;
    }

    await next(context);
});

app.MapGet("/api/v1/health", () => Results.Ok(new
{
    apiVersion = "1",
    status = "healthy",
    timestampUtc = DateTimeOffset.UtcNow
}));

app.MapGet("/api/v1/summary", (StatusCollector collector) => collector.Collect());
app.MapGet("/api/v1/gateway", (StatusCollector collector) => collector.Collect().Gateway);
app.MapGet("/api/v1/modem", (StatusCollector collector) => collector.Collect().Modem);
app.MapGet("/api/v1/network", (StatusCollector collector) => collector.Collect().Network);
app.MapGet("/api/v1/system", (StatusCollector collector) => collector.Collect().System);
app.MapGet("/api/v1/call", (StatusCollector collector) => collector.Collect().Call);
app.MapGet("/api/v1/calls", (CallHistoryReader reader, int limit = 10) => reader.ReadRecent(limit));
app.MapGet("/api/v1/history", async (TelemetryHistoryStore history, string? range, int? maxPoints, CancellationToken token) =>
    await history.ReadAsync(range, maxPoints ?? 720, token));
app.MapGet("/api/v1/history/export", async (TelemetryHistoryStore history, string? range, CancellationToken token) =>
{
    var response = await history.ReadAsync(range, 2000, token);
    return Results.Text(TelemetryHistoryStore.ToCsv(response), "text/csv; charset=utf-8", Encoding.UTF8);
});
app.MapGet("/api/v1/diagnostics", async (StatusCollector collector, CallHistoryReader calls, TelemetryHistoryStore history, CancellationToken token) => new
{
    generatedAtUtc = DateTimeOffset.UtcNow,
    summary = collector.Collect(),
    recentCalls = calls.ReadRecent(10),
    recentHistory = await history.ReadAsync("1h", 60, token)
});
app.MapPost("/api/v1/network/light-test", async (StatusCollector collector, CancellationToken cancellationToken) =>
    await collector.RunLightTestAsync(cancellationToken));

app.Run();
