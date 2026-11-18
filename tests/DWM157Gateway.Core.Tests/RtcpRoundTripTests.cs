using DWM157Gateway.Core.Sip;

namespace DWM157Gateway.Core.Tests;

public sealed class RtcpRoundTripTests
{
    [Fact]
    public void CalculatesRoundTripAfterRemovingRemoteReportDelay()
    {
        const uint lastSenderReport = 100_000;
        const uint remoteDelay = 65_536;
        const uint halfSecondRoundTrip = 32_768;
        var arrival = lastSenderReport + remoteDelay + halfSecondRoundTrip;

        var roundTrip = CellularSipCallCoordinator.CalculateRtcpRoundTripMilliseconds(
            lastSenderReport,
            remoteDelay,
            arrival);

        Assert.Equal(500, roundTrip);
    }
}
