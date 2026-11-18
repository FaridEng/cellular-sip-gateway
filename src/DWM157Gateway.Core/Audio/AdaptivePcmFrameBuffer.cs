using System.Diagnostics;
using DWM157Gateway.Core.Configuration;

namespace DWM157Gateway.Core.Audio;

public sealed class AdaptivePcmFrameBuffer
{
    private readonly object _sync = new();
    private readonly Queue<BufferedFrame> _frames = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly int _frameMilliseconds;
    private readonly int _minFrames;
    private readonly int _maxFrames;
    private readonly int _initialTargetFrames;
    private readonly int _stableFramesBeforeShrink;
    private readonly bool _rebufferAfterUnderrun;
    private readonly Func<long> _timestampProvider;
    private int _targetFrames;
    private int _stableFrames;
    private bool _started;
    private long _droppedFrames;
    private long _underrunFrames;
    private long _dequeuedFrames;
    private double _totalQueueAgeMilliseconds;
    private double _maximumQueueAgeMilliseconds;
    private double _latestQueueAgeMilliseconds;

    public AdaptivePcmFrameBuffer(
        int frameMilliseconds,
        JitterBufferConfig config,
        bool rebufferAfterUnderrun = true)
        : this(frameMilliseconds, config, Stopwatch.GetTimestamp, rebufferAfterUnderrun)
    {
    }

    internal AdaptivePcmFrameBuffer(
        int frameMilliseconds,
        JitterBufferConfig config,
        Func<long> timestampProvider,
        bool rebufferAfterUnderrun = true)
    {
        if (frameMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameMilliseconds));
        }

        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(timestampProvider);
        if (config.MinBufferMilliseconds < 0 ||
            config.TargetBufferMilliseconds < config.MinBufferMilliseconds ||
            config.MaxBufferMilliseconds < config.TargetBufferMilliseconds)
        {
            throw new ArgumentException("Jitter buffer milliseconds must satisfy min <= target <= max.", nameof(config));
        }

        _frameMilliseconds = frameMilliseconds;
        _minFrames = MillisecondsToFrames(config.MinBufferMilliseconds);
        _targetFrames = MillisecondsToFrames(config.TargetBufferMilliseconds);
        _initialTargetFrames = _targetFrames;
        _maxFrames = Math.Max(_targetFrames, MillisecondsToFrames(config.MaxBufferMilliseconds));
        _stableFramesBeforeShrink = Math.Max(1, config.StableFramesBeforeShrink);
        _timestampProvider = timestampProvider;
        _rebufferAfterUnderrun = rebufferAfterUnderrun;
    }

    public int BufferedFrames
    {
        get
        {
            lock (_sync)
            {
                return _frames.Count;
            }
        }
    }

    public int TargetBufferMilliseconds
    {
        get
        {
            lock (_sync)
            {
                return _targetFrames * _frameMilliseconds;
            }
        }
    }

    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);
    public long UnderrunFrames => Interlocked.Read(ref _underrunFrames);

    public void Enqueue(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_sync)
        {
            if (_frames.Count >= _maxFrames)
            {
                var retainedFrames = Math.Max(0, _targetFrames - 1);
                var droppedFrames = 0;
                while (_frames.Count > retainedFrames)
                {
                    _frames.Dequeue();
                    _available.Wait(0);
                    droppedFrames++;
                }

                Interlocked.Add(ref _droppedFrames, droppedFrames);
            }

            _frames.Enqueue(new BufferedFrame(frame, _timestampProvider()));
            _available.Release();
        }
    }

    public async ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken)
    {
        await WaitUntilStartedAsync(cancellationToken);

        await _available.WaitAsync(cancellationToken);
        lock (_sync)
        {
            if (_frames.Count == 0)
            {
                IncreaseTarget();
                throw new InvalidOperationException("Jitter buffer semaphore and queue became unsynchronized.");
            }

            var frame = _frames.Dequeue();
            RecordQueueAge(frame.EnqueuedTimestamp);
            UpdateAfterRead();
            return frame.Data;
        }
    }

    public async Task WaitUntilStartedAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_started)
            {
                return;
            }
        }

        await WaitForInitialTargetAsync(cancellationToken);
    }

    public bool TryRead(out byte[]? frame)
    {
        lock (_sync)
        {
            if (!_started)
            {
                if (_frames.Count < _targetFrames)
                {
                    frame = null;
                    return false;
                }

                _started = true;
            }

            if (_frames.Count == 0)
            {
                frame = null;
                return false;
            }

            var bufferedFrame = _frames.Dequeue();
            frame = bufferedFrame.Data;
            RecordQueueAge(bufferedFrame.EnqueuedTimestamp);
            _available.Wait(0);
            UpdateAfterRead();
            return true;
        }
    }

    public void ReportUnderrun()
    {
        lock (_sync)
        {
            if (_started)
            {
                IncreaseTarget();
                if (_rebufferAfterUnderrun)
                {
                    _started = false;
                }
            }

            Interlocked.Increment(ref _underrunFrames);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _frames.Clear();
            _targetFrames = _initialTargetFrames;
            _stableFrames = 0;
            _started = false;
            Interlocked.Exchange(ref _droppedFrames, 0);
            Interlocked.Exchange(ref _underrunFrames, 0);
            _dequeuedFrames = 0;
            _totalQueueAgeMilliseconds = 0;
            _maximumQueueAgeMilliseconds = 0;
            _latestQueueAgeMilliseconds = 0;
            while (_available.Wait(0))
            {
            }
        }
    }

    public PcmFrameBufferTimingSnapshot GetTimingSnapshot()
    {
        lock (_sync)
        {
            var oldestBufferedAgeMilliseconds = _frames.Count == 0
                ? 0
                : TicksToMilliseconds(_timestampProvider() - _frames.Peek().EnqueuedTimestamp);
            return new PcmFrameBufferTimingSnapshot(
                _dequeuedFrames,
                _dequeuedFrames == 0 ? 0 : _totalQueueAgeMilliseconds / _dequeuedFrames,
                _maximumQueueAgeMilliseconds,
                _latestQueueAgeMilliseconds,
                oldestBufferedAgeMilliseconds);
        }
    }

    private async Task WaitForInitialTargetAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (_sync)
            {
                if (_frames.Count >= _targetFrames)
                {
                    _started = true;
                    return;
                }
            }

            await Task.Delay(_frameMilliseconds, cancellationToken);
        }
    }

    private void IncreaseTarget()
    {
        _targetFrames = Math.Min(_maxFrames, _targetFrames + 1);
        _stableFrames = 0;
    }

    private void UpdateAfterRead()
    {
        if (_frames.Count + 1 >= _targetFrames)
        {
            _stableFrames++;
            if (_stableFrames >= _stableFramesBeforeShrink)
            {
                _targetFrames = Math.Max(_minFrames, _targetFrames - 1);
                _stableFrames = 0;
            }
        }
        else
        {
            _stableFrames = 0;
        }
    }

    private void RecordQueueAge(long enqueuedTimestamp)
    {
        var ageMilliseconds = TicksToMilliseconds(_timestampProvider() - enqueuedTimestamp);
        _dequeuedFrames++;
        _totalQueueAgeMilliseconds += ageMilliseconds;
        _maximumQueueAgeMilliseconds = Math.Max(_maximumQueueAgeMilliseconds, ageMilliseconds);
        _latestQueueAgeMilliseconds = ageMilliseconds;
    }

    private static double TicksToMilliseconds(long ticks) =>
        Math.Max(0, ticks * 1000.0 / Stopwatch.Frequency);

    private int MillisecondsToFrames(int milliseconds)
    {
        return Math.Max(1, (int)Math.Ceiling(milliseconds / (double)_frameMilliseconds));
    }

    private sealed record BufferedFrame(byte[] Data, long EnqueuedTimestamp);
}

public sealed record PcmFrameBufferTimingSnapshot(
    long DequeuedFrames,
    double AverageQueueAgeMilliseconds,
    double MaximumQueueAgeMilliseconds,
    double LatestQueueAgeMilliseconds,
    double OldestBufferedFrameAgeMilliseconds);
