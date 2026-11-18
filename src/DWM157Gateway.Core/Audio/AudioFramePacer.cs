using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using DWM157Gateway.Core.Configuration;

namespace DWM157Gateway.Core.Audio;

internal sealed class AudioFramePacer : IDisposable
{
    private readonly object _statisticsLock = new();
    private readonly long _intervalTicks;
    private readonly long _minimumSpacingTicks;
    private readonly long _precisionWaitTicks;
    private readonly long _gapWarningTicks;
    private readonly long _burstWarningTicks;
    private readonly bool _preventBurstCatchUp;
    private readonly int _legacyMaximumCatchUpFrames;
    private readonly IDisposable? _timerResolutionLease;
    private readonly WindowsHighResolutionWaiter? _highResolutionWaiter;
    private long _idealDeadline;
    private long _nextDeadline;
    private long _lastTick;
    private long _intervalCount;
    private double _totalIntervalMilliseconds;
    private double _minimumIntervalMilliseconds = double.MaxValue;
    private double _maximumIntervalMilliseconds;
    private long _gapWarnings;
    private long _severeGaps;
    private long _burstWarnings;
    private long _scheduleAdjustments;

    public AudioFramePacer(int frameMilliseconds, AudioPacingConfig? config = null)
    {
        if (frameMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameMilliseconds));
        }

        config ??= new AudioPacingConfig();
        if (config.MinimumPacketSpacingMilliseconds < 0 ||
            config.MinimumPacketSpacingMilliseconds > frameMilliseconds ||
            config.PrecisionWaitMilliseconds < 0 ||
            config.PrecisionWaitMilliseconds >= frameMilliseconds ||
            config.GapWarningMilliseconds < frameMilliseconds ||
            config.BurstWarningMilliseconds < 0 ||
            config.BurstWarningMilliseconds > frameMilliseconds ||
            config.LegacyMaximumCatchUpFrames < 0)
        {
            throw new ArgumentException("Invalid audio pacing configuration.", nameof(config));
        }

        _intervalTicks = MillisecondsToTicks(frameMilliseconds);
        _minimumSpacingTicks = MillisecondsToTicks(config.MinimumPacketSpacingMilliseconds);
        _precisionWaitTicks = MillisecondsToTicks(config.PrecisionWaitMilliseconds);
        _gapWarningTicks = MillisecondsToTicks(config.GapWarningMilliseconds);
        _burstWarningTicks = MillisecondsToTicks(config.BurstWarningMilliseconds);
        _preventBurstCatchUp = config.PreventBurstCatchUp;
        _legacyMaximumCatchUpFrames = config.LegacyMaximumCatchUpFrames;
        _timerResolutionLease = config.RequestOneMillisecondTimerResolution
            ? WindowsTimerResolution.Acquire()
            : null;
        _highResolutionWaiter = config.UseHighResolutionWaitableTimer
            ? WindowsHighResolutionWaiter.TryCreate()
            : null;
        _idealDeadline = Stopwatch.GetTimestamp() + _intervalTicks;
        _nextDeadline = _idealDeadline;
    }

    public ValueTask WaitForNextFrameAsync(CancellationToken cancellationToken)
    {
        if (_highResolutionWaiter is not null)
        {
            WaitForNextFrame(cancellationToken);
            return ValueTask.CompletedTask;
        }

        return WaitForNextFrameFallbackAsync(cancellationToken);
    }

    private void WaitForNextFrame(CancellationToken cancellationToken)
    {
        while (true)
        {
            var now = Stopwatch.GetTimestamp();
            var remainingTicks = _nextDeadline - now;
            if (remainingTicks <= 0)
            {
                CompleteTick(now);
                return;
            }

            _highResolutionWaiter!.Wait(remainingTicks, cancellationToken);
        }
    }

    private async ValueTask WaitForNextFrameFallbackAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var now = Stopwatch.GetTimestamp();
            var remainingTicks = _nextDeadline - now;
            if (remainingTicks <= 0)
            {
                CompleteTick(now);
                return;
            }

            if (remainingTicks > _precisionWaitTicks)
            {
                var coarseWaitTicks = remainingTicks - _precisionWaitTicks;
                var coarseWait = TimeSpan.FromSeconds(coarseWaitTicks / (double)Stopwatch.Frequency);
                await Task.Delay(coarseWait, cancellationToken);
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            Thread.SpinWait(64);
        }
    }

    public void Dispose()
    {
        _highResolutionWaiter?.Dispose();
        _timerResolutionLease?.Dispose();
    }

    public AudioPacingSnapshot GetSnapshot()
    {
        lock (_statisticsLock)
        {
            return new AudioPacingSnapshot(
                _intervalCount + (_lastTick == 0 ? 0 : 1),
                _intervalCount == 0 ? 0 : _totalIntervalMilliseconds / _intervalCount,
                _intervalCount == 0 ? 0 : _minimumIntervalMilliseconds,
                _maximumIntervalMilliseconds,
                _gapWarnings,
                _severeGaps,
                _burstWarnings,
                Interlocked.Read(ref _scheduleAdjustments));
        }
    }

    private void RecordTick(long tick)
    {
        lock (_statisticsLock)
        {
            if (_lastTick != 0)
            {
                var elapsedTicks = tick - _lastTick;
                var elapsedMilliseconds = elapsedTicks * 1000.0 / Stopwatch.Frequency;
                _intervalCount++;
                _totalIntervalMilliseconds += elapsedMilliseconds;
                _minimumIntervalMilliseconds = Math.Min(_minimumIntervalMilliseconds, elapsedMilliseconds);
                _maximumIntervalMilliseconds = Math.Max(_maximumIntervalMilliseconds, elapsedMilliseconds);
                if (elapsedTicks > _gapWarningTicks)
                {
                    _gapWarnings++;
                }

                if (elapsedTicks > _intervalTicks * 2)
                {
                    _severeGaps++;
                }

                if (elapsedTicks < _burstWarningTicks)
                {
                    _burstWarnings++;
                }
            }

            _lastTick = tick;
        }
    }

    private void CompleteTick(long actualTick)
    {
        _nextDeadline = CalculateNextDeadline(
            _idealDeadline,
            actualTick,
            _intervalTicks,
            _minimumSpacingTicks,
            _preventBurstCatchUp,
            _legacyMaximumCatchUpFrames,
            out _idealDeadline,
            out var adjustments);
        Interlocked.Add(ref _scheduleAdjustments, adjustments);
        RecordTick(actualTick);
    }

    private static long MillisecondsToTicks(int milliseconds) =>
        Math.Max(0, Stopwatch.Frequency * milliseconds / 1000);

    internal static long CalculateNextDeadline(
        long currentIdealDeadline,
        long actualTick,
        long intervalTicks,
        long minimumSpacingTicks,
        bool preventBurstCatchUp,
        int legacyMaximumCatchUpFrames,
        out long nextIdealDeadline,
        out long adjustments)
    {
        nextIdealDeadline = currentIdealDeadline + intervalTicks;
        adjustments = 0;

        var maximumCatchUpTicks = intervalTicks * legacyMaximumCatchUpFrames;
        if (legacyMaximumCatchUpFrames > 0 &&
            actualTick - currentIdealDeadline > maximumCatchUpTicks)
        {
            adjustments = Math.Max(1, (actualTick - currentIdealDeadline) / intervalTicks);
            nextIdealDeadline = actualTick + intervalTicks;
            return nextIdealDeadline;
        }

        if (preventBurstCatchUp)
        {
            var earliestNext = actualTick + minimumSpacingTicks;
            if (nextIdealDeadline < earliestNext)
            {
                adjustments = 1;
                return earliestNext;
            }
        }

        return nextIdealDeadline;
    }
}

internal sealed class WindowsHighResolutionWaiter : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitFailed = 0xffffffff;
    private const uint Infinite = 0xffffffff;
    private readonly SafeWaitHandle _timerHandle;

    private WindowsHighResolutionWaiter(SafeWaitHandle timerHandle)
    {
        _timerHandle = timerHandle;
    }

    public static WindowsHighResolutionWaiter? TryCreate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var timerHandle = CreateWaitableTimerEx(
            IntPtr.Zero,
            null,
            CreateWaitableTimerHighResolution,
            TimerAllAccess);
        if (timerHandle.IsInvalid)
        {
            timerHandle.Dispose();
            return null;
        }

        return new WindowsHighResolutionWaiter(timerHandle);
    }

    public void Wait(long stopwatchTicks, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dueTime100Nanoseconds = -Math.Max(
            1,
            (long)Math.Ceiling(stopwatchTicks * 10_000_000.0 / Stopwatch.Frequency));
        if (!SetWaitableTimer(
                _timerHandle,
                ref dueTime100Nanoseconds,
                0,
                IntPtr.Zero,
                IntPtr.Zero,
                false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not arm the high-resolution audio timer.");
        }

        var handles = new[]
        {
            _timerHandle.DangerousGetHandle(),
            cancellationToken.WaitHandle.SafeWaitHandle.DangerousGetHandle()
        };
        var waitResult = WaitForMultipleObjects((uint)handles.Length, handles, false, Infinite);
        if (waitResult == WaitObject0 + 1)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (waitResult == WaitFailed)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not wait for the high-resolution audio timer.");
        }
    }

    public void Dispose() => _timerHandle.Dispose();

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateWaitableTimerEx(
        IntPtr timerAttributes,
        string? timerName,
        uint flags,
        uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(
        SafeWaitHandle timerHandle,
        ref long dueTime,
        int periodMilliseconds,
        IntPtr completionRoutine,
        IntPtr completionRoutineArgument,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForMultipleObjects(
        uint handleCount,
        IntPtr[] handles,
        [MarshalAs(UnmanagedType.Bool)] bool waitAll,
        uint milliseconds);
}

internal static class WindowsTimerResolution
{
    private static readonly object Sync = new();
    private static int _leaseCount;
    private static bool _enabled;

    public static IDisposable? Acquire()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        lock (Sync)
        {
            if (_leaseCount == 0)
            {
                _enabled = TimeBeginPeriod(1) == 0;
            }

            _leaseCount++;
        }

        return new TimerResolutionLease();
    }

    private sealed class TimerResolutionLease : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            lock (Sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _leaseCount--;
                if (_leaseCount == 0 && _enabled)
                {
                    TimeEndPeriod(1);
                    _enabled = false;
                }
            }
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint periodMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint periodMilliseconds);
}

public sealed record AudioPacingSnapshot(
    long Frames,
    double AverageIntervalMilliseconds,
    double MinimumIntervalMilliseconds,
    double MaximumIntervalMilliseconds,
    long GapsOverWarning,
    long GapsOverTwoFrames,
    long BurstsUnderWarning,
    long ScheduleAdjustments);
