using DWM157Gateway.Core.Configuration;
using DWM157Gateway.Core.Dialing;
using DWM157Gateway.Core.Fakes;
using DWM157Gateway.Core.Modem;
using DWM157Gateway.Core.Sip;

namespace DWM157Gateway.Core.Tests;

public sealed class SipDtmfBridgeTests
{
    [Fact]
    public async Task BridgeForwardsTelephoneEventsInOrderAndTracksTelemetry()
    {
        var transport = new FakeAtTransport();
        var controller = CreateConnectedController(transport);
        var logs = new List<string>();
        await using var bridge = new SipDtmfBridge(controller, CancellationToken.None, logs.Add);

        Assert.True(bridge.TryQueue(1, 160));
        Assert.True(bridge.TryQueue(2, 180));
        Assert.True(bridge.TryQueue(10, 200));
        await WaitForAsync(() => bridge.GetSnapshot().ForwardedEvents == 3);

        Assert.Equal(["AT+VTS=1", "AT+VTS=2", "AT+VTS=*"], transport.Commands);
        Assert.Equal(new DtmfBridgeSnapshot(3, 3, 0, 0), bridge.GetSnapshot());
        Assert.DoesNotContain(logs, line => line.Contains("AT+VTS=1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BridgeContainsARejectedEventAndContinuesWithTheNextTone()
    {
        var transport = new FakeAtTransport();
        var controller = CreateConnectedController(transport);
        await using var bridge = new SipDtmfBridge(controller, CancellationToken.None);

        Assert.True(bridge.TryQueue(16, 100));
        Assert.True(bridge.TryQueue(3, 100));
        await WaitForAsync(() => bridge.GetSnapshot().FailedEvents == 1 && bridge.GetSnapshot().ForwardedEvents == 1);

        Assert.Equal(["AT+VTS=3"], transport.Commands);
        Assert.Equal(new DtmfBridgeSnapshot(2, 1, 1, 0), bridge.GetSnapshot());
    }

    private static CellularCallController CreateConnectedController(FakeAtTransport transport)
    {
        var policy = new DialPolicy(new DialPolicyConfig { AllowedDialNumbers = ["12345"] });
        var controller = new CellularCallController(transport, policy);
        transport.Emit("^CONN: 1,0");
        return controller;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
