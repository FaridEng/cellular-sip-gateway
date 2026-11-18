using DWM157Gateway.Monitoring;

namespace DWM157Gateway.Monitoring.Tests;

public sealed class NetworkQualityMonitorTests
{
    [Fact]
    public void ParsesGalaxyA07BatteryAndDeviceMetrics()
    {
        var status = NetworkQualityMonitor.ParseA07Status(
            "status: 2\nlevel: 83\ntemperature: 341\n",
            "SM-A075F\n16\n/dev/block 1000 200 800 20% /data\nMemTotal: 4096000 kB\n");

        Assert.True(status.Connected);
        Assert.Equal(83, status.BatteryPercent);
        Assert.True(status.Charging);
        Assert.Equal(34.1, status.TemperatureC);
        Assert.Equal("SM-A075F", status.Model);
        Assert.Equal(800 * 1024, status.StorageFreeBytes);
    }

    [Fact]
    public void ParsesDerpPing()
    {
        var result = NetworkQualityMonitor.ParseTailscalePing(
            "pong from phone (100.64.0.14) via DERP(fra) in 369ms", DateTimeOffset.UtcNow);
        Assert.True(result.Success);
        Assert.Equal(369, result.RttMilliseconds);
        Assert.Equal("DERP", result.Path);
        Assert.Equal("fra", result.DerpRegion);
    }

    [Fact]
    public void ParsesDirectPing()
    {
        var result = NetworkQualityMonitor.ParseTailscalePing(
            "pong from phone (100.64.0.14) via 192.0.2.10:41641 in 18ms", DateTimeOffset.UtcNow);
        Assert.True(result.Success);
        Assert.Equal("Direct", result.Path);
        Assert.Null(result.DerpRegion);
    }
}
