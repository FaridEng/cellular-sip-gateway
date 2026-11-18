using DWM157Gateway.Core.Audio;

namespace DWM157Gateway.Core.Tests;

public sealed class RtpReceiveStatisticsTests
{
    [Fact]
    public void CountsSequenceGapsAndOutOfOrderPackets()
    {
        var statistics = new RtpReceiveStatistics(8000);

        statistics.Record(100, 0);
        statistics.Record(101, 160);
        statistics.Record(103, 480);
        statistics.Record(102, 320);

        var snapshot = statistics.GetSnapshot();

        Assert.Equal(4, snapshot.ReceivedPackets);
        Assert.Equal(4, snapshot.ExpectedPackets);
        Assert.Equal(0, snapshot.LostPackets);
        Assert.Equal(1, snapshot.OutOfOrderPackets);
    }

    [Fact]
    public void HandlesSequenceNumberWrapAround()
    {
        var statistics = new RtpReceiveStatistics(8000);

        statistics.Record(ushort.MaxValue, 0);
        statistics.Record(0, 160);
        statistics.Record(1, 320);

        var snapshot = statistics.GetSnapshot();

        Assert.Equal(3, snapshot.ExpectedPackets);
        Assert.Equal(0, snapshot.LostPackets);
    }

    [Fact]
    public void ReportsPayloadSizeAndBitrate()
    {
        var statistics = new RtpReceiveStatistics(48000);

        statistics.Record(1, 0, 40);
        Thread.Sleep(25);
        statistics.Record(2, 960, 60);

        var snapshot = statistics.GetSnapshot();

        Assert.Equal(100, snapshot.PayloadBytes);
        Assert.Equal(50, snapshot.AveragePayloadBytes);
        Assert.Equal(40, snapshot.MinimumPayloadBytes);
        Assert.Equal(60, snapshot.MaximumPayloadBytes);
        Assert.True(snapshot.PayloadBitrateBitsPerSecond > 0);
        Assert.True(snapshot.MaximumArrivalGapMilliseconds >= 20);
    }
}
