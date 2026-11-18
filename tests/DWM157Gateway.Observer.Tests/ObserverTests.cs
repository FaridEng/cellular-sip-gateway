using System.Net;
using System.Text.Json;
using DWM157Gateway.Observer;
using DWM157Gateway.Monitoring;
using Xunit;

namespace DWM157Gateway.Observer.Tests;

public sealed class ObserverTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-27T20:00:00Z");
    private static ProbeOutcome Failure(int seconds) => new("unreachable", "API unreachable", Now.AddSeconds(seconds));
    private static ProbeOutcome Success(int seconds) => new("healthy", "API working", Now.AddSeconds(seconds));

    [Fact]
    public void SingleFailureAndShortBurstsDoNotCreateOutage()
    {
        var tracker = new IncidentTracker(new(), new());
        Assert.Null(tracker.Observe(Failure(0)));
        Assert.Null(tracker.Observe(Success(15)));
        Assert.Null(tracker.Observe(Failure(30)));
        Assert.Null(tracker.Observe(Failure(45)));
        Assert.Null(tracker.Observe(Failure(60)));
        Assert.Null(tracker.State.IncidentId);
    }

    [Fact]
    public void SustainedFailureAlertsOnceAndRecoveryRequiresTwoSuccesses()
    {
        var state = new IncidentState();
        var tracker = new IncidentTracker(new(), state);
        tracker.Observe(Failure(0)); tracker.Observe(Failure(15)); tracker.Observe(Failure(30));
        var outage = tracker.Observe(Failure(45));
        Assert.Equal("outage", outage?.Kind);
        Assert.Null(tracker.Observe(Failure(60)));
        Assert.Null(tracker.Observe(Success(75)));
        var recovery = tracker.Observe(Success(90));
        Assert.Equal("recovered", recovery?.Kind);
        Assert.Equal(outage!.IncidentId, recovery!.IncidentId);
        Assert.Equal(recovery, state.PendingAlert); // unsent outage replaced
        Assert.Null(tracker.Observe(Success(105)));
    }

    [Fact]
    public void PersistedIncidentDoesNotAlertAgainAfterObserverRestart()
    {
        var state = new IncidentState { IncidentId = "existing-incident", Sequence = 7, Failures = 10, FailureSinceUtc = Now.AddMinutes(-10) };
        var saved = JsonSerializer.Deserialize<IncidentState>(JsonSerializer.Serialize(state))!;
        var tracker = new IncidentTracker(new(), saved);
        Assert.Null(tracker.Observe(Failure(0)));
        Assert.Equal(7, tracker.State.Sequence);
        tracker.Observe(Success(15));
        Assert.Equal(8, tracker.Observe(Success(30))?.Sequence);
    }

    [Fact]
    public void MaintenanceSuppressesOutageOnlyUntilItExpires()
    {
        var tracker = new IncidentTracker(new() { MaintenanceUntilUtc = Now.AddSeconds(60) }, new());
        tracker.Observe(Failure(0)); tracker.Observe(Failure(15)); tracker.Observe(Failure(30));
        Assert.Null(tracker.Observe(Failure(45)));
        Assert.Equal("outage", tracker.Observe(Failure(60))?.Kind);
    }

    [Theory]
    [InlineData("{\"gateway\":{\"running\":false}}", "gateway_stopped")]
    [InlineData("{\"gateway\":{\"running\":true,\"snapshotAvailable\":false}}", "telemetry_stale")]
    [InlineData("{\"gateway\":{\"running\":true,\"snapshotAvailable\":true,\"snapshotTimestampUtc\":null}}", "telemetry_stale")]
    [InlineData("{\"gateway\":{\"state\":\"Ready\"}}", "monitor_error")]
    [InlineData("[]", "monitor_error")]
    public void ClassifierDoesNotConfuseMissingTelemetryAndStoppedProcess(string json, string expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, GatewayProbe.Classify(document.RootElement, Now, 20).Code);
    }

    [Fact]
    public void OldTimestampIsStaleEvenIfServerFlagIsFalse()
    {
        var root = JsonSerializer.SerializeToElement(new { gateway = new { running = true, snapshotAvailable = true,
            snapshotStale = false, snapshotTimestampUtc = Now.AddMinutes(-2), sipListening = true } });
        Assert.Equal("telemetry_stale", GatewayProbe.Classify(root, Now, 20).Code);
    }

    [Theory]
    [InlineData(401, "authentication_failed")]
    [InlineData(403, "access_denied")]
    [InlineData(500, "monitor_error")]
    [InlineData(302, "monitor_error")]
    public async Task HttpStatusIsNotMisreportedAsNetworkFailure(int status, string expected)
    {
        using var client = new HttpClient(new ResponseHandler((HttpStatusCode)status));
        var probe = new GatewayProbe(client, new() { GatewayApiToken = "test-only" });
        Assert.Equal(expected, (await probe.CheckAsync(CancellationToken.None)).Code);
    }

    [Fact]
    public void VersionComparisonIsNumericAndDoesNotCompareAndroidToWindows()
    {
        var old = new DeviceVersion("a", "Windows A", "windows", "1.98.2", "1.98.2-tabc", Now, "test");
        var newer = old with { Id = "b", RunningVersion = "1.104.2" };
        Assert.Contains("Older than", VersionInventory.WarningFor(old, [old, newer], Now, null));
        var android = newer with { Os = "android" };
        Assert.DoesNotContain("Older than", VersionInventory.WarningFor(old, [old, android], Now, null));
    }

    [Fact]
    public void StalePeerVersionDoesNotBecomeLiveReference()
    {
        var current = new DeviceVersion("a", "A", "windows", "1.102.3", "1.102.3", Now, "test");
        var stale = current with { Id = "b", RunningVersion = "1.104.2", ObservedAtUtc = Now.AddDays(-2) };
        Assert.DoesNotContain("Older than", VersionInventory.WarningFor(current, [current, stale], Now, null));
        Assert.Contains("stale", VersionInventory.WarningFor(stale, [current, stale], Now, null));
    }

    [Fact]
    public void InstalledDaemonMismatchIsReportedSeparately()
    {
        var version = new DeviceVersion("a", "A", "windows", "1.104.2", "1.102.3-tabc", Now, "test");
        Assert.Contains("Installed and running", VersionInventory.WarningFor(version, [version], Now, null));
        var read = TailscaleVersionMonitor.Parse("{\"short\":\"1.104.2\",\"daemonLong\":\"1.102.3-tabc\"}", Now);
        Assert.Equal("1.102.3-tabc", read.RunningVersion);
    }

    private sealed class ResponseHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
    }
}
