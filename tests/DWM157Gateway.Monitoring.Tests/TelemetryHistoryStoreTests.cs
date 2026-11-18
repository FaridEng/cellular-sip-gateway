using DWM157Gateway.Monitoring;

namespace DWM157Gateway.Monitoring.Tests;

public sealed class TelemetryHistoryStoreTests
{
    [Fact]
    public void CsvExportEscapesTextAndKeepsMetrics()
    {
        var point = new HistoryPoint(DateTimeOffset.Parse("2026-08-28T00:00:00Z"), "healthy,live", 95,
            true, 18, 22.5, true, 12.1, true, "Direct", "USB,Tether", 42, 0.2, 3.4, 50, 80, 34.5);
        var csv = TelemetryHistoryStore.ToCsv(new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "1h", [point]));

        Assert.Contains("\"healthy,live\"", csv);
        Assert.Contains("\"USB,Tether\"", csv);
        Assert.Contains("22.5", csv);
    }
}
