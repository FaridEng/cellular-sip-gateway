using System.Diagnostics;
using DWM157Gateway.Core.Audio;

namespace DWM157Gateway.Core.Tests;

public sealed class PcmGainProcessorTests
{
    [Fact]
    public void AppliesSixDecibelsInPlace()
    {
        var processor = new PcmGainProcessor(6);
        short[] samples = [1000, -1000, 10_000, -10_000];

        processor.ApplyInPlace(samples);

        Assert.InRange(samples[0], 1994, 1996);
        Assert.InRange(samples[1], -1996, -1994);
        Assert.InRange(samples[2], 19_952, 19_954);
        Assert.InRange(samples[3], -19_954, -19_952);
    }

    [Fact]
    public void SaturatesAndReportsClipping()
    {
        var processor = new PcmGainProcessor(6);
        short[] samples = [20_000, -20_000, 1_000];

        processor.ApplyInPlace(samples);
        var snapshot = processor.GetSnapshot();

        Assert.Equal(short.MaxValue, samples[0]);
        Assert.Equal(short.MinValue, samples[1]);
        Assert.Equal(2, snapshot.ClippedSamples);
        Assert.Equal(3, snapshot.ProcessedSamples);
        Assert.Equal(66.6667, snapshot.ClippedSamplePercent, 4);
    }

    [Fact]
    public void ReportsActiveRmsAndPeakLevels()
    {
        var processor = new PcmGainProcessor(6);
        var samples = Enumerable.Repeat((short)8_000, 160).ToArray();

        processor.ApplyInPlace(samples);
        var snapshot = processor.GetSnapshot();

        Assert.Equal(1, snapshot.ActiveFrames);
        Assert.NotNull(snapshot.ActiveInputRmsDbfs);
        Assert.NotNull(snapshot.ActiveOutputRmsDbfs);
        Assert.Equal(6, snapshot.ActiveOutputRmsDbfs!.Value - snapshot.ActiveInputRmsDbfs!.Value, 1);
        Assert.Equal(0, snapshot.ClippedSamples);
    }

    [Fact]
    public void ProcessesMoreThanAnHourOfFramesFasterThanRealTimeWithoutPerFrameAllocation()
    {
        var processor = new PcmGainProcessor(6);
        var samples = Enumerable.Repeat((short)1_000, 160).ToArray();
        processor.ApplyInPlace(samples);
        var stopwatch = new Stopwatch();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        stopwatch.Start();

        for (var frame = 0; frame < 180_000; frame++)
        {
            processor.ApplyInPlace(samples);
        }

        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Console.WriteLine($"GAIN_BENCHMARK frames=180000 elapsed={stopwatch.Elapsed.TotalMilliseconds:F1}ms allocated={allocated}bytes");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(0, allocated);
    }
}
