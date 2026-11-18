using DWM157Gateway.Core.Diagnostics;

namespace DWM157Gateway.Core.Tests;

public sealed class CallHistoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"dwm157-call-history-{Guid.NewGuid():N}");

    [Fact]
    public void PersistsStructuredCallAndReloadsIt()
    {
        var path = Path.Combine(_directory, "calls.json");
        var store = new CallHistoryStore(path, 50);
        var call = store.Begin(CallDirection.Outgoing, "123", "100.64.0.14:5060");

        store.Update(call.CorrelationId, record =>
        {
            record.StartTimestamp = record.RequestTimestamp;
            record.ConnectTimestamp = record.RequestTimestamp.AddSeconds(2);
            record.EndTimestamp = record.RequestTimestamp.AddSeconds(10);
            record.Status = CallStatus.Completed;
            record.CellularOutcome = "completed";
            record.SipOutcome = "completed";
            record.EndOrigin = "sip";
            record.EndReason = "normal hangup";
            record.Codec = "OPUS";
            record.Media = new CallMediaSummary(400, 400, 0, 0, 3.2, 0, 4.1, 250, -24, -5, 200, 400, 0, 0);
        });

        var reloaded = new CallHistoryStore(path, 50).ReadRecent(1).Single();

        Assert.Equal(call.CorrelationId, reloaded.CorrelationId);
        Assert.Equal(CallStatus.Completed, reloaded.Status);
        Assert.Equal("123", reloaded.RemoteNumber);
        Assert.Equal("100.64.0.14:5060", reloaded.SipPeer);
        Assert.Equal("OPUS", reloaded.Codec);
        Assert.Equal(0, reloaded.Media!.RtpLostPackets);
    }

    [Fact]
    public void RetainsOnlyConfiguredMaximum()
    {
        var path = Path.Combine(_directory, "calls.json");
        var store = new CallHistoryStore(path, 10);

        for (var index = 0; index < 14; index++)
        {
            store.Begin(CallDirection.Incoming, index.ToString(), null, DateTimeOffset.UtcNow.AddSeconds(index));
        }

        var records = new CallHistoryStore(path, 10).ReadRecent(20);
        Assert.Equal(10, records.Count);
        Assert.DoesNotContain(records, record => record.RemoteNumber == "0");
        Assert.Contains(records, record => record.RemoteNumber == "13");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }
}
