using System.Text.Json;
using DWM157Gateway.Core.Diagnostics;

namespace DWM157Gateway.Core.Tests;

public sealed class MonitoringSnapshotWriterTests
{
    [Fact]
    public void WritePublishesCompleteCamelCaseSnapshotWithoutTemporaryFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dwm157-monitoring-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "gateway-status.json");
        try
        {
            var writer = new MonitoringSnapshotWriter(path);
            writer.Write(new GatewayMonitoringSnapshot(
                DateTimeOffset.Parse("2026-08-27T12:00:00Z"),
                new MonitoringGateway("Ready", "0.2.11", true, 2, null),
                new MonitoringModem(true, true, true, 21, null),
                new MonitoringCall("Idle", false, null, null, null, null, null)));

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("Ready", document.RootElement.GetProperty("gateway").GetProperty("state").GetString());
            Assert.Equal(21, document.RootElement.GetProperty("modem").GetProperty("signalQuality").GetInt32());
            Assert.False(document.RootElement.GetProperty("call").GetProperty("connected").GetBoolean());
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("+CSQ: 21,99", 21)]
    [InlineData("+CSQ: 0,0", 0)]
    [InlineData(null, null)]
    [InlineData("invalid", null)]
    public void ParseSignalQualityHandlesModemResponses(string? value, int? expected)
    {
        Assert.Equal(expected, MonitoringSnapshotWriter.ParseSignalQuality(value));
    }
}
