using System.Diagnostics;

namespace DWM157Gateway.Core.Audio;

public sealed class RtpReceiveStatistics
{
    private readonly object _sync = new();
    private readonly int _clockRate;
    private bool _initialized;
    private ushort _baseSequence;
    private ushort _maxSequence;
    private long _sequenceCycles;
    private long _receivedPackets;
    private long _outOfOrderPackets;
    private long _payloadBytes;
    private int _minimumPayloadBytes = int.MaxValue;
    private int _maximumPayloadBytes;
    private long? _previousArrivalTimestamp;
    private double _maximumArrivalGapMilliseconds;
    private long _arrivalGapsOver25Milliseconds;
    private long _arrivalGapsOver40Milliseconds;
    private long? _firstArrivalTimestamp;
    private long? _lastArrivalTimestamp;
    private long? _previousTransit;
    private double _jitter;

    public RtpReceiveStatistics(int clockRate)
    {
        if (clockRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clockRate));
        }

        _clockRate = clockRate;
    }

    public void Record(ushort sequenceNumber, uint timestamp, int payloadBytes = 0)
    {
        if (payloadBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadBytes));
        }

        var arrivalTimestamp = Stopwatch.GetTimestamp();
        var arrival = arrivalTimestamp * (double)_clockRate / Stopwatch.Frequency;

        lock (_sync)
        {
            if (_previousArrivalTimestamp.HasValue)
            {
                var arrivalGapMilliseconds =
                    (arrivalTimestamp - _previousArrivalTimestamp.Value) * 1000.0 / Stopwatch.Frequency;
                _maximumArrivalGapMilliseconds = Math.Max(_maximumArrivalGapMilliseconds, arrivalGapMilliseconds);
                if (arrivalGapMilliseconds > 25)
                {
                    _arrivalGapsOver25Milliseconds++;
                }

                if (arrivalGapMilliseconds > 40)
                {
                    _arrivalGapsOver40Milliseconds++;
                }
            }

            _previousArrivalTimestamp = arrivalTimestamp;
            _firstArrivalTimestamp ??= arrivalTimestamp;
            _lastArrivalTimestamp = arrivalTimestamp;
            _payloadBytes += payloadBytes;
            _minimumPayloadBytes = Math.Min(_minimumPayloadBytes, payloadBytes);
            _maximumPayloadBytes = Math.Max(_maximumPayloadBytes, payloadBytes);

            if (!_initialized)
            {
                _initialized = true;
                _baseSequence = sequenceNumber;
                _maxSequence = sequenceNumber;
            }
            else if (sequenceNumber != _maxSequence)
            {
                var forwardDistance = (ushort)(sequenceNumber - _maxSequence);
                if (forwardDistance < 0x8000)
                {
                    if (sequenceNumber < _maxSequence)
                    {
                        _sequenceCycles += ushort.MaxValue + 1L;
                    }

                    _maxSequence = sequenceNumber;
                }
                else
                {
                    _outOfOrderPackets++;
                }
            }
            else if (_receivedPackets > 0)
            {
                _outOfOrderPackets++;
            }

            _receivedPackets++;

            var transit = (long)arrival - timestamp;
            if (_previousTransit.HasValue)
            {
                var difference = Math.Abs(transit - _previousTransit.Value);
                _jitter += (difference - _jitter) / 16.0;
            }

            _previousTransit = transit;
        }
    }

    public RtpReceiveSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            if (!_initialized)
            {
                return new RtpReceiveSnapshot(0, 0, 0, 0, 0);
            }

            var expectedPackets = _sequenceCycles + _maxSequence - _baseSequence + 1;
            var lostPackets = Math.Max(0, expectedPackets - _receivedPackets);
            var elapsedSeconds = _firstArrivalTimestamp.HasValue && _lastArrivalTimestamp > _firstArrivalTimestamp
                ? (_lastArrivalTimestamp.Value - _firstArrivalTimestamp.Value) / (double)Stopwatch.Frequency
                : 0;
            return new RtpReceiveSnapshot(
                _receivedPackets,
                expectedPackets,
                lostPackets,
                _outOfOrderPackets,
                _jitter * 1000.0 / _clockRate,
                _payloadBytes,
                _receivedPackets == 0 ? 0 : _payloadBytes / (double)_receivedPackets,
                _receivedPackets == 0 ? 0 : _minimumPayloadBytes,
                _maximumPayloadBytes,
                elapsedSeconds > 0 ? _payloadBytes * 8.0 / elapsedSeconds : 0,
                _maximumArrivalGapMilliseconds,
                _arrivalGapsOver25Milliseconds,
                _arrivalGapsOver40Milliseconds);
        }
    }
}

public sealed record RtpReceiveSnapshot(
    long ReceivedPackets,
    long ExpectedPackets,
    long LostPackets,
    long OutOfOrderPackets,
    double JitterMilliseconds,
    long PayloadBytes = 0,
    double AveragePayloadBytes = 0,
    int MinimumPayloadBytes = 0,
    int MaximumPayloadBytes = 0,
    double PayloadBitrateBitsPerSecond = 0,
    double MaximumArrivalGapMilliseconds = 0,
    long ArrivalGapsOver25Milliseconds = 0,
    long ArrivalGapsOver40Milliseconds = 0);
