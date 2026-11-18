using DWM157Gateway.Core.Audio;
using DWM157Gateway.Core.Configuration;

namespace DWM157Gateway.Core.Tests;

public sealed class AudioFramePacerTests
{
    [Fact]
    public void CalculateNextDeadline_PreservesAbsoluteTimelineWhenSlightlyLate()
    {
        var next = AudioFramePacer.CalculateNextDeadline(
            currentIdealDeadline: 100,
            actualTick: 103,
            intervalTicks: 20,
            minimumSpacingTicks: 15,
            preventBurstCatchUp: true,
            legacyMaximumCatchUpFrames: 10,
            out var nextIdeal,
            out var adjustments);

        Assert.Equal(120, next);
        Assert.Equal(120, nextIdeal);
        Assert.Equal(0, adjustments);
    }

    [Fact]
    public void CalculateNextDeadline_UsesMinimumSpacingWithoutSkippingMediaSlot()
    {
        var next = AudioFramePacer.CalculateNextDeadline(
            currentIdealDeadline: 100,
            actualTick: 110,
            intervalTicks: 20,
            minimumSpacingTicks: 15,
            preventBurstCatchUp: true,
            legacyMaximumCatchUpFrames: 10,
            out var nextIdeal,
            out var adjustments);

        Assert.Equal(125, next);
        Assert.Equal(120, nextIdeal);
        Assert.Equal(1, adjustments);

        var recovered = AudioFramePacer.CalculateNextDeadline(
            currentIdealDeadline: nextIdeal,
            actualTick: next,
            intervalTicks: 20,
            minimumSpacingTicks: 15,
            preventBurstCatchUp: true,
            legacyMaximumCatchUpFrames: 10,
            out var recoveredIdeal,
            out var recoveryAdjustments);

        Assert.Equal(140, recovered);
        Assert.Equal(140, recoveredIdeal);
        Assert.Equal(0, recoveryAdjustments);
    }

    [Fact]
    public async Task HighResolutionWaiterMaintainsTwentyMillisecondMediaClock()
    {
        using var pacer = new AudioFramePacer(20, new AudioPacingConfig
        {
            PreventBurstCatchUp = true,
            MinimumPacketSpacingMilliseconds = 15,
            UseHighResolutionWaitableTimer = true
        });

        for (var index = 0; index < 30; index++)
        {
            await pacer.WaitForNextFrameAsync(CancellationToken.None);
        }

        var snapshot = pacer.GetSnapshot();
        Assert.InRange(snapshot.AverageIntervalMilliseconds, 18, 22);
        Assert.Equal(0, snapshot.BurstsUnderWarning);
    }
}
