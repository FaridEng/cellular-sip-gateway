using DWM157Gateway.Core.Audio;

namespace DWM157Gateway.Core.Tests;

public sealed class EncodedPayloadStatisticsTests
{
    [Fact]
    public void ReportsPacketSizesAndBitrate()
    {
        var statistics = new EncodedPayloadStatistics();

        statistics.Record(30);
        Thread.Sleep(20);
        statistics.Record(50);

        var snapshot = statistics.GetSnapshot();

        Assert.Equal(2, snapshot.Packets);
        Assert.Equal(80, snapshot.Bytes);
        Assert.Equal(40, snapshot.AverageBytes);
        Assert.Equal(30, snapshot.MinimumBytes);
        Assert.Equal(50, snapshot.MaximumBytes);
        Assert.True(snapshot.BitrateBitsPerSecond > 0);
    }
}
