using DWM157Gateway.Monitoring;

namespace DWM157Gateway.Monitoring.Tests;

public sealed class ReadinessEvaluatorTests
{
    [Fact]
    public void ReadySystemScoresOneHundred()
    {
        var result = ReadinessEvaluator.Evaluate(
            new(true, "Ready", "1", true, 1, true, false, DateTimeOffset.UtcNow, null),
            new(true, true, true, 20, "carrier"),
            Network(true, true, "Direct"),
            new("Gateway PC", "Windows", 1, 10L * 1024 * 1024 * 1024, 4));

        Assert.Equal(100, result.Score);
        Assert.Equal("Ready", result.State);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void MissingGatewayAndModemIsNotReadyWithReasons()
    {
        var result = ReadinessEvaluator.Evaluate(
            new(false, "Stopped", null, false, 0, false, false, null, null),
            new(false, false, false, null, null),
            Network(false, false, "Offline"),
            new("Gateway PC", "Windows", 1, 100, 4));

        Assert.Equal("NotReady", result.State);
        Assert.Equal(0, result.Score);
        Assert.Contains(result.Reasons, reason => reason.Contains("Gateway process"));
        Assert.Contains(result.Reasons, reason => reason.Contains("SIM"));
    }

    private static NetworkStatus Network(bool available, bool probeSuccess, string path) => new(
        available, "Lan", available, "100.64.0.15", [], null,
        new(new(probeSuccess, probeSuccess ? 20 : null, path, null, DateTimeOffset.UtcNow, null),
            new(probeSuccess, probeSuccess ? 10 : null, DateTimeOffset.UtcNow, null),
            "Lan", null, null, new(true, true, "Online")));
}
