using DWM157Gateway.Core.Sip;

namespace DWM157Gateway.Core.Tests;

public sealed class SipMediaOfferDiagnosticsTests
{
    [Fact]
    public void ReportsSafeAudioAttributesWithoutIceSecretsOrAddresses()
    {
        const string offer = """
            v=0
            m=audio 7078 RTP/AVP 111 101
            b=AS:38
            a=rtpmap:111 opus/48000/2
            a=fmtp:111 useinbandfec=1;maxaveragebitrate=38000;stereo=0;secret=value
            a=ptime:20
            a=ice-ufrag:sensitive
            a=ice-pwd:more-sensitive
            a=candidate:1 1 UDP 1 192.0.2.1 7078 typ host
            a=rtcp-mux
            a=sendrecv
            """;

        var result = CellularSipCallCoordinator.GetAudioOfferDiagnostics(offer);

        Assert.Contains("ptime=20", result);
        Assert.Contains("maxaveragebitrate=38000", result);
        Assert.Contains("bandwidth=AS:38", result);
        Assert.Contains("ice=yes", result);
        Assert.Contains("candidates=1", result);
        Assert.DoesNotContain("sensitive", result);
        Assert.DoesNotContain("192.0.2.1", result);
        Assert.DoesNotContain("secret", result);
    }
}
