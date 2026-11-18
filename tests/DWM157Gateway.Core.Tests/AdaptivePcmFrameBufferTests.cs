using System.Diagnostics;
using DWM157Gateway.Core.Audio;
using DWM157Gateway.Core.Configuration;

namespace DWM157Gateway.Core.Tests;

public sealed class AdaptivePcmFrameBufferTests
{
    [Fact]
    public async Task HoldsFramesUntilTargetBufferIsReady()
    {
        var buffer = new AdaptivePcmFrameBuffer(20, new JitterBufferConfig
        {
            MinBufferMilliseconds = 20,
            TargetBufferMilliseconds = 40,
            MaxBufferMilliseconds = 60
        });

        var firstRead = buffer.ReadAsync(CancellationToken.None).AsTask();
        buffer.Enqueue([1]);

        await Task.Delay(30);
        Assert.False(firstRead.IsCompleted);

        buffer.Enqueue([2]);
        Assert.Equal([1], await firstRead);
        Assert.Equal([2], await buffer.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TrimsStaleBacklogToTargetAtMaximumAndKeepsQueueSynchronized()
    {
        var buffer = new AdaptivePcmFrameBuffer(20, new JitterBufferConfig
        {
            MinBufferMilliseconds = 20,
            TargetBufferMilliseconds = 40,
            MaxBufferMilliseconds = 60
        });

        buffer.Enqueue([1]);
        buffer.Enqueue([2]);
        buffer.Enqueue([3]);
        buffer.Enqueue([4]);

        Assert.Equal([3], await buffer.ReadAsync(CancellationToken.None));
        Assert.Equal([4], await buffer.ReadAsync(CancellationToken.None));
        Assert.Equal(2, buffer.DroppedFrames);
        Assert.Equal(40, buffer.TargetBufferMilliseconds);
    }

    [Fact]
    public void RejectsInvalidRange()
    {
        Assert.Throws<ArgumentException>(() => new AdaptivePcmFrameBuffer(20, new JitterBufferConfig
        {
            MinBufferMilliseconds = 500,
            TargetBufferMilliseconds = 400,
            MaxBufferMilliseconds = 500
        }));
    }

    [Fact]
    public async Task TryReadReportsUnderrunWithoutStoppingTheAudioClock()
    {
        var buffer = new AdaptivePcmFrameBuffer(20, new JitterBufferConfig
        {
            MinBufferMilliseconds = 20,
            TargetBufferMilliseconds = 20,
            MaxBufferMilliseconds = 60
        });

        buffer.Enqueue([1]);
        await buffer.WaitUntilStartedAsync(CancellationToken.None);

        Assert.True(buffer.TryRead(out var frame));
        Assert.Equal([1], frame);
        Assert.False(buffer.TryRead(out _));

        buffer.ReportUnderrun();
        Assert.Equal(1, buffer.UnderrunFrames);
        Assert.Equal(40, buffer.TargetBufferMilliseconds);

        buffer.ReportUnderrun();
        Assert.Equal(2, buffer.UnderrunFrames);
        Assert.Equal(40, buffer.TargetBufferMilliseconds);

        buffer.Enqueue([2]);
        Assert.False(buffer.TryRead(out _));
        buffer.Enqueue([3]);
        Assert.True(buffer.TryRead(out var recoveredFrame));
        Assert.Equal([2], recoveredFrame);
    }

    [Fact]
    public void ReportsActualFrameQueueAge()
    {
        long now = 0;
        var buffer = new AdaptivePcmFrameBuffer(20, new JitterBufferConfig
        {
            MinBufferMilliseconds = 20,
            TargetBufferMilliseconds = 20,
            MaxBufferMilliseconds = 60
        }, () => now);

        buffer.Enqueue([1]);
        now += Stopwatch.Frequency * 75 / 1000;

        Assert.True(buffer.TryRead(out _));
        var timing = buffer.GetTimingSnapshot();

        Assert.Equal(75, timing.AverageQueueAgeMilliseconds, 1);
        Assert.Equal(75, timing.LatestQueueAgeMilliseconds, 1);
        Assert.Equal(75, timing.MaximumQueueAgeMilliseconds, 1);
    }

    [Fact]
    public async Task CanContinueImmediatelyAfterUnderrunWithoutRebuffering()
    {
        var buffer = new AdaptivePcmFrameBuffer(20, new JitterBufferConfig
        {
            MinBufferMilliseconds = 20,
            TargetBufferMilliseconds = 20,
            MaxBufferMilliseconds = 60
        }, rebufferAfterUnderrun: false);

        buffer.Enqueue([1]);
        await buffer.WaitUntilStartedAsync(CancellationToken.None);
        Assert.True(buffer.TryRead(out _));

        buffer.ReportUnderrun();
        buffer.Enqueue([2]);

        Assert.True(buffer.TryRead(out var recoveredFrame));
        Assert.Equal([2], recoveredFrame);
        Assert.Equal(40, buffer.TargetBufferMilliseconds);
    }
}
