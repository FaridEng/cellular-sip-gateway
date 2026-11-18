using System.Text.Json;

namespace DWM157Gateway.Monitoring;

public sealed class CallHistoryReader
{
    private const long MaximumFileBytes = 5 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly MonitoringOptions _options;

    public CallHistoryReader(MonitoringOptions options) => _options = options;

    public CallHistoryResponse ReadRecent(int requestedCount)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var count = Math.Clamp(requestedCount, 1, 10);
        if (!File.Exists(_options.CallHistoryPath))
        {
            return new(timestamp, false, []);
        }

        try
        {
            using var stream = new FileStream(
                _options.CallHistoryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaximumFileBytes)
            {
                return new(timestamp, false, []);
            }

            var records = JsonSerializer.Deserialize<List<StoredCallRecord>>(stream, JsonOptions) ?? [];
            var result = records
                .Where(record => record.RequestTimestamp != default)
                .OrderByDescending(record => record.RequestTimestamp)
                .Take(count)
                .Select(ToMonitoringRecord)
                .ToArray();
            return new(timestamp, true, result);
        }
        catch (JsonException)
        {
            return new(timestamp, false, []);
        }
        catch (IOException)
        {
            return new(timestamp, false, []);
        }
        catch (UnauthorizedAccessException)
        {
            return new(timestamp, false, []);
        }
    }

    private static MonitoringCallRecord ToMonitoringRecord(StoredCallRecord record)
    {
        long? duration = record.ConnectTimestamp is not null && record.EndTimestamp is not null
            ? Math.Max(0, (long)(record.EndTimestamp.Value - record.ConnectTimestamp.Value).TotalSeconds)
            : null;
        return new(
            Normalize(record.Direction, "Unknown"),
            NormalizeOptional(record.RemoteNumber),
            NormalizeOptional(record.SipPeer),
            Normalize(record.Status, "Unknown"),
            record.RequestTimestamp.ToUniversalTime(),
            record.ConnectTimestamp?.ToUniversalTime(),
            record.EndTimestamp?.ToUniversalTime(),
            duration,
            NormalizeOptional(record.EndReason),
            NormalizeOptional(record.Codec),
            ToMedia(record.Media));
    }

    private static CallMediaStatus? ToMedia(StoredCallMedia? media)
    {
        if (media is null) return null;
        double? loss = media.RtpExpectedPackets is > 0 && media.RtpLostPackets is not null
            ? media.RtpLostPackets.Value * 100d / media.RtpExpectedPackets.Value
            : null;
        return new(media.RtpReceivedPackets, media.RtpExpectedPackets, media.RtpLostPackets,
            media.RtpOutOfOrderPackets, loss, media.RtpJitterMilliseconds, media.RemoteLossPercent,
            media.RemoteJitterMilliseconds, media.RoundTripMilliseconds,
            media.ModemToRtpDroppedFrames, media.RtpToModemDroppedFrames);
    }

    private static string Normalize(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class StoredCallRecord
    {
        public string? Direction { get; set; }
        public string? RemoteNumber { get; set; }
        public string? SipPeer { get; set; }
        public DateTimeOffset RequestTimestamp { get; set; }
        public DateTimeOffset? ConnectTimestamp { get; set; }
        public DateTimeOffset? EndTimestamp { get; set; }
        public string? Status { get; set; }
        public string? EndReason { get; set; }
        public string? Codec { get; set; }
        public StoredCallMedia? Media { get; set; }
    }

    private sealed class StoredCallMedia
    {
        public long? RtpReceivedPackets { get; set; }
        public long? RtpExpectedPackets { get; set; }
        public long? RtpLostPackets { get; set; }
        public long? RtpOutOfOrderPackets { get; set; }
        public double? RtpJitterMilliseconds { get; set; }
        public double? RemoteLossPercent { get; set; }
        public double? RemoteJitterMilliseconds { get; set; }
        public double? RoundTripMilliseconds { get; set; }
        public long? ModemToRtpDroppedFrames { get; set; }
        public long? RtpToModemDroppedFrames { get; set; }
    }
}
