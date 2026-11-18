using System.Diagnostics;

namespace DWM157Gateway.Core.Audio;

public sealed class EncodedPayloadStatistics
{
    private readonly object _sync = new();
    private long _packets;
    private long _bytes;
    private int _minimumBytes = int.MaxValue;
    private int _maximumBytes;
    private long? _firstTimestamp;
    private long? _lastTimestamp;

    public void Record(int payloadBytes)
    {
        if (payloadBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadBytes));
        }

        var now = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            _packets++;
            _bytes += payloadBytes;
            _minimumBytes = Math.Min(_minimumBytes, payloadBytes);
            _maximumBytes = Math.Max(_maximumBytes, payloadBytes);
            _firstTimestamp ??= now;
            _lastTimestamp = now;
        }
    }

    public EncodedPayloadSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var elapsedSeconds = _firstTimestamp.HasValue && _lastTimestamp > _firstTimestamp
                ? (_lastTimestamp.Value - _firstTimestamp.Value) / (double)Stopwatch.Frequency
                : 0;
            return new EncodedPayloadSnapshot(
                _packets,
                _bytes,
                _packets == 0 ? 0 : _bytes / (double)_packets,
                _packets == 0 ? 0 : _minimumBytes,
                _maximumBytes,
                elapsedSeconds > 0 ? _bytes * 8.0 / elapsedSeconds : 0);
        }
    }
}

public sealed record EncodedPayloadSnapshot(
    long Packets,
    long Bytes,
    double AverageBytes,
    int MinimumBytes,
    int MaximumBytes,
    double BitrateBitsPerSecond);
