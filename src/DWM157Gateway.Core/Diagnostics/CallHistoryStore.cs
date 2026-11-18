using System.Text.Json;
using System.Text.Json.Serialization;

namespace DWM157Gateway.Core.Diagnostics;

public sealed class CallHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _sync = new();
    private readonly string _path;
    private readonly int _maxRecords;
    private readonly Action<string>? _logMessage;
    private List<CallHistoryRecord> _records;

    public CallHistoryStore(string path, int maxRecords, Action<string>? logMessage = null)
    {
        _path = Path.GetFullPath(path);
        _maxRecords = Math.Clamp(maxRecords, 10, 10_000);
        _logMessage = logMessage;
        _records = Load();
    }

    public CallHistoryRecord Begin(
        CallDirection direction,
        string? remoteNumber,
        string? sipPeer,
        DateTimeOffset? requestedAt = null)
    {
        var record = new CallHistoryRecord
        {
            CorrelationId = Guid.NewGuid().ToString("N"),
            Direction = direction,
            RemoteNumber = NormalizeOptional(remoteNumber),
            SipPeer = NormalizeOptional(sipPeer),
            RequestTimestamp = requestedAt ?? DateTimeOffset.UtcNow,
            Status = CallStatus.Requested,
            CellularOutcome = "pending",
            SipOutcome = "pending"
        };

        lock (_sync)
        {
            _records.Add(record);
            TrimLocked();
            TryPersistLocked();
        }

        return record;
    }

    public void Update(string correlationId, Action<CallHistoryRecord> update)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentNullException.ThrowIfNull(update);
        lock (_sync)
        {
            var record = _records.FirstOrDefault(item => item.CorrelationId == correlationId);
            if (record is null)
            {
                throw new InvalidOperationException($"Call history record '{correlationId}' was not found.");
            }

            update(record);
            TryPersistLocked();
        }
    }

    public IReadOnlyList<CallHistoryRecord> ReadRecent(int count)
    {
        lock (_sync)
        {
            return _records
                .OrderByDescending(record => record.RequestTimestamp)
                .Take(Math.Clamp(count, 1, _maxRecords))
                .Select(record => record.Clone())
                .ToArray();
        }
    }

    private List<CallHistoryRecord> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            var records = JsonSerializer.Deserialize<List<CallHistoryRecord>>(File.ReadAllText(_path), JsonOptions) ?? [];
            return records
                .Where(record => !string.IsNullOrWhiteSpace(record.CorrelationId))
                .OrderBy(record => record.RequestTimestamp)
                .TakeLast(_maxRecords)
                .ToList();
        }
        catch (Exception ex)
        {
            _logMessage?.Invoke($"Call history could not be read and will be rebuilt: {ex.Message}");
            return [];
        }
    }

    private void TrimLocked()
    {
        if (_records.Count > _maxRecords)
        {
            _records.RemoveRange(0, _records.Count - _maxRecords);
        }
    }

    private void PersistLocked()
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_records, JsonOptions));
        File.Move(temporaryPath, _path, true);
    }

    private void TryPersistLocked()
    {
        try
        {
            PersistLocked();
        }
        catch (Exception ex)
        {
            _logMessage?.Invoke($"Call history write failed: {ex.Message}");
        }
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class CallHistoryRecord
{
    public string CorrelationId { get; set; } = "";
    public CallDirection Direction { get; set; }
    public string? RemoteNumber { get; set; }
    public string? SipPeer { get; set; }
    public DateTimeOffset RequestTimestamp { get; set; }
    public DateTimeOffset? StartTimestamp { get; set; }
    public DateTimeOffset? ConnectTimestamp { get; set; }
    public DateTimeOffset? EndTimestamp { get; set; }
    public CallStatus Status { get; set; }
    public string CellularOutcome { get; set; } = "pending";
    public string SipOutcome { get; set; } = "pending";
    public string? EndOrigin { get; set; }
    public string? EndReason { get; set; }
    public string? Codec { get; set; }
    public CallMediaSummary? Media { get; set; }

    internal CallHistoryRecord Clone() => (CallHistoryRecord)MemberwiseClone();
}

public sealed record CallMediaSummary(
    long RtpReceivedPackets,
    long RtpExpectedPackets,
    long RtpLostPackets,
    long RtpOutOfOrderPackets,
    double RtpJitterMilliseconds,
    double? RemoteLossPercent,
    double? RemoteJitterMilliseconds,
    double? RoundTripMilliseconds,
    double? RtpToModemActiveRmsDbfs,
    double? RtpToModemPeakDbfs,
    long? RtpToModemActiveFrames,
    long? RtpToModemProcessedFrames,
    long? ModemToRtpDroppedFrames,
    long? RtpToModemDroppedFrames);

public enum CallDirection
{
    Incoming,
    Outgoing
}

public enum CallStatus
{
    Requested,
    Rejected,
    FailedBeforeConnect,
    Failed,
    Unanswered,
    Cancelled,
    Connected,
    Completed
}
