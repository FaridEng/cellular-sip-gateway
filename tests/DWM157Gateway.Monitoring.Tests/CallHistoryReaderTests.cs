using System.Text.Json;
using DWM157Gateway.Monitoring;

namespace DWM157Gateway.Monitoring.Tests;

public sealed class CallHistoryReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"dwm157-monitor-tests-{Guid.NewGuid():N}");

    [Fact]
    public void ReturnsNewestTenAndCalculatesConnectedDuration()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "calls.json");
        var records = Enumerable.Range(0, 12).Select(index => new
        {
            direction = index % 2 == 0 ? "Incoming" : "Outgoing",
            remoteNumber = $"test-{index}",
            sipPeer = index == 11 ? "100.64.0.15:5060" : null,
            requestTimestamp = DateTimeOffset.Parse("2026-08-01T00:00:00Z").AddMinutes(index),
            connectTimestamp = index == 11 ? DateTimeOffset.Parse("2026-08-01T00:11:10Z") : (DateTimeOffset?)null,
            endTimestamp = index == 11 ? DateTimeOffset.Parse("2026-08-01T00:12:10Z") : (DateTimeOffset?)null,
            status = index == 11 ? "Completed" : "Unanswered",
            endReason = index == 11 ? "normal hangup" : "not answered",
            codec = index == 11 ? "OPUS" : null
        });
        File.WriteAllText(path, JsonSerializer.Serialize(records));

        var result = new CallHistoryReader(new MonitoringOptions { CallHistoryPath = path }).ReadRecent(50);

        Assert.True(result.SourceAvailable);
        Assert.Equal(10, result.Calls.Count);
        Assert.Equal("test-11", result.Calls[0].RemoteNumber);
        Assert.Equal(60, result.Calls[0].DurationSeconds);
        Assert.Equal("100.64.0.15:5060", result.Calls[0].SipPeer);
    }

    [Fact]
    public void MissingOrMalformedSourceReturnsNoRecords()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "calls.json");
        var reader = new CallHistoryReader(new MonitoringOptions { CallHistoryPath = path });
        Assert.False(reader.ReadRecent(10).SourceAvailable);

        File.WriteAllText(path, "not-json");
        var malformed = reader.ReadRecent(10);
        Assert.False(malformed.SourceAvailable);
        Assert.Empty(malformed.Calls);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
