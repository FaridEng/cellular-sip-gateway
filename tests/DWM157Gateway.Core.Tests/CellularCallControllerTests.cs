using DWM157Gateway.Core.Configuration;
using DWM157Gateway.Core.Dialing;
using DWM157Gateway.Core.Fakes;
using DWM157Gateway.Core.Modem;

namespace DWM157Gateway.Core.Tests;

public sealed class CellularCallControllerTests
{
    [Fact]
    public async Task PreflightChecksExpectedAtCommands()
    {
        var transport = new FakeAtTransport();
        transport.EnqueueResponse("OK");
        transport.EnqueueResponse("+CPIN: READY", "OK");
        transport.EnqueueResponse("+CREG: 1,1", "OK");
        transport.EnqueueResponse("+CSQ: 20,99", "OK");
        transport.EnqueueResponse("OK");
        transport.EnqueueResponse("OK");
        transport.EnqueueResponse("(0-9,*,#,A,B,C,D)", "OK");

        var controller = CreateController(transport, "12345");
        var result = await controller.PreflightAsync(CancellationToken.None);

        Assert.True(result.AtOk);
        Assert.True(result.SimReady);
        Assert.True(result.Registered);
        Assert.True(result.DtmfSupported);
        Assert.Equal("+CREG: 1,1", result.RegistrationLine);
        Assert.Contains("AT+CLCC", transport.Commands);
        Assert.Contains("AT+VTS=?", transport.Commands);
    }

    [Fact]
    public async Task RegistrationStatusRecognizesRoamingAndReturnsSignal()
    {
        var transport = new FakeAtTransport();
        transport.EnqueueResponse("+CREG: 2,5,\"30B6\",\"0901364E\",6", "OK");
        transport.EnqueueResponse("+CSQ: 8,99", "OK");
        var controller = CreateController(transport, "12345");

        var status = await controller.GetRegistrationStatusAsync(CancellationToken.None);

        Assert.True(status.Registered);
        Assert.Equal("+CREG: 2,5,\"30B6\",\"0901364E\",6", status.RegistrationLine);
        Assert.Equal("+CSQ: 8,99", status.SignalQualityLine);
        Assert.Equal(["AT+CREG?", "AT+CSQ"], transport.Commands);
    }

    [Fact]
    public async Task RegistrationStatusPreservesSearchingState()
    {
        var transport = new FakeAtTransport();
        transport.EnqueueResponse("+CREG: 2,2,\"30B6\",\"0901364E\",6", "OK");
        transport.EnqueueResponse("+CSQ: 7,99", "OK");
        var controller = CreateController(transport, "12345");

        var status = await controller.GetRegistrationStatusAsync(CancellationToken.None);

        Assert.False(status.Registered);
        Assert.Equal("+CREG: 2,2,\"30B6\",\"0901364E\",6", status.RegistrationLine);
        Assert.Equal("+CSQ: 7,99", status.SignalQualityLine);
    }

    [Fact]
    public async Task RegistrationStatusRecognizesVerboseHomeRegistration()
    {
        var transport = new FakeAtTransport();
        transport.EnqueueResponse("+CREG: 2,1,\"30B6\",\"0901364E\",6", "OK");
        transport.EnqueueResponse("+CSQ: 7,99", "OK");
        var controller = CreateController(transport, "12345");

        var status = await controller.GetRegistrationStatusAsync(CancellationToken.None);

        Assert.True(status.Registered);
        Assert.Equal("+CREG: 2,1,\"30B6\",\"0901364E\",6", status.RegistrationLine);
    }

    [Fact]
    public async Task DialSendsSsamBeforeAtdAndWaitsForConnEvent()
    {
        var transport = new FakeAtTransport();
        var controller = CreateController(transport, "12345");

        var decision = await controller.DialAsync("12345", CancellationToken.None);
        transport.Emit("^CONN: 1,0");

        Assert.True(decision.IsAllowed);
        Assert.Equal(["AT+SSAM=5", "ATD12345;"], transport.Commands);
        Assert.Equal(CellularCallState.Connected, controller.State);
    }

    [Fact]
    public async Task DialDoesNotSendAtCommandForBlockedNumber()
    {
        var transport = new FakeAtTransport();
        var controller = CreateController(transport, "12345");

        var decision = await controller.DialAsync("54321", CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.Empty(transport.Commands);
    }

    [Fact]
    public void RingRaisesOneIncomingCallAndClipUpdatesCallerId()
    {
        var transport = new FakeAtTransport();
        var controller = CreateController(transport, "12345");
        var calls = new List<IncomingCellularCall>();
        controller.IncomingCallReceived += (_, call) => calls.Add(call);

        transport.Emit("+CLIP: \"05550000000\",129");
        transport.Emit("RING");
        transport.Emit("RING");

        Assert.Single(calls);
        Assert.Equal("05550000000", calls[0].CallerId);
        Assert.Equal(CellularCallState.IncomingRinging, controller.State);
    }

    [Fact]
    public async Task WaitForIncomingCallerIdReturnsClipReceivedAfterRing()
    {
        var transport = new FakeAtTransport();
        var controller = CreateController(transport, "12345");
        transport.Emit("RING");

        var callerIdTask = controller.WaitForIncomingCallerIdAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        transport.Emit("+CLIP: \"+15550000000\",145");

        Assert.Equal("+15550000000", await callerIdTask);
    }

    [Fact]
    public async Task WaitForIncomingCallerIdReturnsNullAfterTimeout()
    {
        var transport = new FakeAtTransport();
        var controller = CreateController(transport, "12345");
        transport.Emit("RING");

        var callerId = await controller.WaitForIncomingCallerIdAsync(TimeSpan.FromMilliseconds(10), CancellationToken.None);

        Assert.Null(callerId);
    }

    [Fact]
    public async Task ClccIncomingCanBePolledAndAnsweredWithAta()
    {
        var transport = new FakeAtTransport();
        transport.EnqueueResponse("+CLCC: 1,1,4,0,0,\"05550000000\",129", "OK");
        transport.EnqueueResponse("OK");
        var controller = CreateController(transport, "12345");

        await controller.PollForIncomingCallAsync(CancellationToken.None);
        await controller.AnswerIncomingAsync(CancellationToken.None);
        transport.Emit("+CLCC: 1,1,0,0,0,\"05550000000\",129");

        Assert.Equal(["AT+CLCC", "ATA"], transport.Commands);
        Assert.Equal(CellularCallState.Connected, controller.State);
    }

    [Fact]
    public async Task IncomingCallBlocksOutgoingDialUntilHangup()
    {
        var transport = new FakeAtTransport();
        var controller = CreateController(transport, "12345");
        transport.Emit("RING");

        var decision = await controller.DialAsync("12345", CancellationToken.None);
        await controller.HangupAsync(CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.Equal(["ATH"], transport.Commands);
        Assert.Equal(CellularCallState.Idle, controller.State);
    }

    [Fact]
    public async Task DtmfMapsAllStandardTelephoneEventsToSafeAtCommands()
    {
        var transport = new FakeAtTransport();
        var controller = CreateController(transport, "12345");
        transport.Emit("^CONN: 1,0");

        for (byte eventId = 0; eventId < 16; eventId++)
        {
            await controller.SendDtmfAsync(eventId, CancellationToken.None);
        }

        Assert.Equal(
            "0123456789*#ABCD".Select(symbol => $"AT+VTS={symbol}"),
            transport.Commands);
    }

    [Fact]
    public async Task DtmfRejectsUnsupportedEventWithoutSendingAtCommand()
    {
        var transport = new FakeAtTransport();
        var controller = CreateController(transport, "12345");
        transport.Emit("^CONN: 1,0");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => controller.SendDtmfAsync(16, CancellationToken.None));

        Assert.Empty(transport.Commands);
    }

    [Fact]
    public async Task DtmfRequiresConnectedCellularCall()
    {
        var transport = new FakeAtTransport();
        var controller = CreateController(transport, "12345");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.SendDtmfAsync(1, CancellationToken.None));

        Assert.Empty(transport.Commands);
    }

    [Fact]
    public async Task DtmfReportsModemRejection()
    {
        var transport = new FakeAtTransport();
        transport.EnqueueResponse("ERROR");
        var controller = CreateController(transport, "12345");
        transport.Emit("^CONN: 1,0");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.SendDtmfAsync(1, CancellationToken.None));

        Assert.Equal(["AT+VTS=1"], transport.Commands);
    }

    private static CellularCallController CreateController(FakeAtTransport transport, string allowedNumber)
    {
        var policy = new DialPolicy(new DialPolicyConfig { AllowedDialNumbers = [allowedNumber] });
        return new CellularCallController(transport, policy);
    }
}
