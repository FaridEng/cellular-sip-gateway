using System.Net;
using System.Text.Json;
using DWM157Gateway.Observer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DWM157Gateway.Observer.Tests;

public sealed class ObserverIntegrationTests
{
    [Fact]
    public async Task RealHttpProbeRecordsOneOutageAndRecoveryWithoutFirebaseOrRealGateway()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        var healthy = false;
        app.MapGet("/api/v1/summary", () => Results.Json(new
        {
            gateway = new { running = healthy, snapshotAvailable = healthy, snapshotStale = false,
                snapshotTimestampUtc = DateTimeOffset.UtcNow, sipListening = healthy }
        }));
        await app.StartAsync();
        var endpoint = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var directory = Path.Combine(Path.GetTempPath(), "dwm157-observer-test-" + Guid.NewGuid().ToString("N"));
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var options = new ObserverOptions { GatewayUrl = endpoint, GatewayApiToken = "test-only", DataDirectory = directory,
            ProbeIntervalSeconds = 1, FailureThreshold = 2, FailureDurationSeconds = 0, RecoveryThreshold = 2,
            TailscaleExecutable = Path.Combine(directory, "not-installed.exe") };
        var inventory = new VersionInventory(options, client);
        using var worker = new ObserverWorker(options, new GatewayProbe(client, options), inventory, new(options, client));
        try
        {
            await worker.StartAsync(CancellationToken.None);
            await WaitUntil(() => worker.Snapshot.IncidentId is not null);
            Assert.Equal("gateway_stopped", worker.Snapshot.Probe.Code);
            Assert.Single(worker.Snapshot.RecentEvents);
            Assert.False(worker.Snapshot.Push.Configured);
            healthy = true;
            await WaitUntil(() => worker.Snapshot.IncidentId is null && worker.Snapshot.Probe.Healthy);
            Assert.Equal(new[] { "recovered", "outage" }, worker.Snapshot.RecentEvents.Select(e => e.Kind));
            await worker.StopAsync(CancellationToken.None);
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "observer-state.json")));
            Assert.Equal(2, saved.RootElement.GetProperty("incident").GetProperty("sequence").GetInt64());
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            await app.StopAsync();
            var resolved = Path.GetFullPath(directory);
            if (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
                Directory.Delete(resolved, true);
        }
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!predicate() && DateTime.UtcNow < until) await Task.Delay(50);
        Assert.True(predicate(), "Observer state transition did not arrive before the integration-test timeout");
    }
}
