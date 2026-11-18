using System.Net;
using DWM157Gateway.Core.Audio;
using DWM157Gateway.Core.Configuration;
using DWM157Gateway.Core.Diagnostics;
using DWM157Gateway.Core.Modem;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace DWM157Gateway.Core.Sip;

public sealed class IncomingCellularSipCallCoordinator : IAsyncDisposable
{
    private readonly CellularCallController _cellularCall;
    private readonly SipGatewayHost _sipHost;
    private readonly string _servicePort;
    private readonly int _baudRate;
    private readonly PcmFormat _pcmFormat;
    private readonly SipConfig _sipConfig;
    private readonly AudioConfig _audioConfig;
    private readonly IncomingCallConfig _incomingConfig;
    private readonly Action<string>? _logMessage;
    private readonly CallHistoryStore? _callHistory;
    private readonly SemaphoreSlim _callGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts;
    private Task? _activeCallTask;

    public IncomingCellularSipCallCoordinator(
        CellularCallController cellularCall,
        SipGatewayHost sipHost,
        string servicePort,
        int baudRate,
        PcmFormat pcmFormat,
        SipConfig sipConfig,
        AudioConfig audioConfig,
        IncomingCallConfig incomingConfig,
        CancellationToken gatewayCancellationToken,
        Action<string>? logMessage = null,
        CallHistoryStore? callHistory = null)
    {
        _cellularCall = cellularCall;
        _sipHost = sipHost;
        _servicePort = servicePort;
        _baudRate = baudRate;
        _pcmFormat = pcmFormat;
        _sipConfig = sipConfig;
        _audioConfig = audioConfig;
        _incomingConfig = incomingConfig;
        _logMessage = logMessage;
        _callHistory = callHistory;
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(gatewayCancellationToken);
        _cellularCall.IncomingCallReceived += OnIncomingCallReceived;
    }

    private void OnIncomingCallReceived(object? sender, IncomingCellularCall call)
    {
        if (!_incomingConfig.Enabled)
        {
            _logMessage?.Invoke("Incoming cellular call detected, but incoming routing is disabled.");
            return;
        }

        if (_activeCallTask is { IsCompleted: false })
        {
            _logMessage?.Invoke("Ignored duplicate incoming cellular ring while routing is already active.");
            return;
        }

        _activeCallTask = RouteIncomingCallAsync(call, _lifetimeCts.Token);
        _ = ObserveActiveCallAsync(_activeCallTask);
    }

    private async Task ObserveActiveCallAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logMessage?.Invoke($"Incoming call routing failed: {ex.Message}");
        }
    }

    private async Task RouteIncomingCallAsync(IncomingCellularCall incomingCall, CancellationToken cancellationToken)
    {
        await _callGate.WaitAsync(cancellationToken);
        var history = _callHistory?.Begin(
            CallDirection.Incoming,
            incomingCall.CallerId,
            null,
            incomingCall.DetectedAt);
        var connected = false;
        var cancelled = false;
        string? endOrigin = null;
        string? endReason = null;
        UpdateCallHistory(history, record =>
        {
            record.StartTimestamp = incomingCall.DetectedAt;
            record.CellularOutcome = "ringing";
            record.SipOutcome = "locating contacts";
        });
        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var branches = new List<IncomingSipBranch>();
        VoiceSerialPcmTransport? voiceTransport = null;
        IncomingSipBranch? winner = null;
        SipDtmfBridge? dtmfBridge = null;
        Action<byte, int>? dtmfHandler = null;
        try
        {
            if (_cellularCall.State is not CellularCallState.IncomingRinging)
            {
                return;
            }

            var contacts = _sipHost.GetRegisteredContacts();
            if (contacts.Count == 0)
            {
                var waitSeconds = Math.Clamp(_incomingConfig.SipContactWaitSeconds, 0, 60);
                _logMessage?.Invoke($"Incoming cellular call has no routable SIP contact yet; waiting up to {waitSeconds}s for a client.");
                contacts = await WaitForSipContactsAsync(TimeSpan.FromSeconds(waitSeconds), callCts.Token);
                if (contacts.Count == 0)
                {
                    endOrigin = "gateway";
                    endReason = "no reachable SIP contact";
                    _logMessage?.Invoke("Incoming cellular call could not be routed because no SIP client became reachable before the contact wait expired.");
                    UpdateCallHistory(history, record =>
                    {
                        record.Status = CallStatus.Unanswered;
                        record.CellularOutcome = "released unanswered";
                        record.SipOutcome = "no reachable contact";
                    });
                    return;
                }
            }

            var availableBranches = Math.Max(1, (_sipConfig.RtpPortEnd - _sipConfig.RtpPortStart + 1) / 2);
            contacts = contacts.Take(availableBranches).ToArray();
            var callerIdWait = TimeSpan.FromMilliseconds(Math.Clamp(_incomingConfig.CallerIdWaitMilliseconds, 0, 3000));
            var callerId = await _cellularCall.WaitForIncomingCallerIdAsync(callerIdWait, callCts.Token);
            var callerIdentity = CreateCallerIdentity(callerId);
            UpdateCallHistory(history, record =>
            {
                record.RemoteNumber = callerId ?? incomingCall.CallerId;
                record.SipOutcome = "ringing contacts";
            });
            _logMessage?.Invoke($"Forking incoming cellular call to {contacts.Count} registered SIP contact(s).");
            _logMessage?.Invoke($"Incoming SIP caller identity available={callerIdentity.IsAvailable}.");

            for (var index = 0; index < contacts.Count; index++)
            {
                var contact = contacts[index];
                var deferredTransport = new DeferredVoicePcmTransport();
                var endpoint = new Dwm157AudioEndPoint(
                    deferredTransport,
                    _pcmFormat,
                    _sipConfig,
                    _audioConfig.JitterBuffer,
                    _audioConfig.ModemToRtpBuffer,
                    _audioConfig.Pacing,
                    _audioConfig.ModemToRtpGainDb);
                var mediaSession = new VoIPMediaSession(
                    endpoint.ToMediaEndPoints(),
                    IPAddress.Parse(_sipConfig.BindAddress),
                    _sipConfig.RtpPortStart + index * 2);
                mediaSession.AcceptRtpFromAny = false;
                RtpReceiveStatistics? rtpStatistics = null;
                mediaSession.OnRtpPacketReceived += (_, mediaType, packet) =>
                {
                    if (mediaType != SDPMediaTypesEnum.audio)
                    {
                        return;
                    }

                    rtpStatistics ??= new RtpReceiveStatistics(
                        endpoint.SelectedRtpClockRate > 0 ? endpoint.SelectedRtpClockRate : _pcmFormat.SampleRateHz);
                    rtpStatistics.Record(packet.Header.SequenceNumber, packet.Header.Timestamp, packet.Payload.Length);
                };
                var userAgent = _sipHost.CreateUserAgent();
                var callDescriptor = new SIPCallDescriptor(contact.DestinationUri, null);
                callDescriptor.SetGeneralFromHeaderFields(
                    callerIdentity.DisplayName,
                    callerIdentity.Username,
                    _sipConfig.BindAddress);
                var callTask = userAgent.Call(
                    callDescriptor,
                    mediaSession,
                    _incomingConfig.SipRingTimeoutSeconds);
                branches.Add(new IncomingSipBranch(
                    contact,
                    userAgent,
                    mediaSession,
                    endpoint,
                    deferredTransport,
                    callTask,
                    () => rtpStatistics?.GetSnapshot() ?? new RtpReceiveSnapshot(0, 0, 0, 0, 0)));
                _logMessage?.Invoke($"Ringing registered SIP contact {contact.PeerAddress}:{contact.Port}.");
            }

            var cellularEnded = WaitForCellularEndAsync(callCts.Token);
            var pending = branches.ToDictionary(branch => branch.CallTask);
            while (pending.Count > 0 && winner is null)
            {
                var completed = await Task.WhenAny(pending.Keys.Cast<Task>().Append(cellularEnded));
                if (completed == cellularEnded)
                {
                    await cellularEnded;
                    endOrigin = "cellular";
                    endReason = "caller ended before SIP answer";
                    UpdateCallHistory(history, record =>
                    {
                        record.Status = CallStatus.Unanswered;
                        record.CellularOutcome = "caller ended";
                        record.SipOutcome = "unanswered";
                    });
                    return;
                }

                var callTask = (Task<bool>)completed;
                var branch = pending[callTask];
                pending.Remove(callTask);
                try
                {
                    if (await callTask)
                    {
                        winner = branch;
                    }
                }
                catch (Exception ex)
                {
                    _logMessage?.Invoke($"SIP contact {branch.Contact.PeerAddress}:{branch.Contact.Port} failed: {ex.Message}");
                }
            }

            if (winner is null)
            {
                endOrigin = "gateway";
                endReason = "no SIP client answered";
                _logMessage?.Invoke("No registered softphone answered the incoming cellular call.");
                UpdateCallHistory(history, record =>
                {
                    record.Status = CallStatus.Unanswered;
                    record.CellularOutcome = "released unanswered";
                    record.SipOutcome = "unanswered";
                });
                await _cellularCall.HangupAsync(CancellationToken.None);
                return;
            }

            UpdateCallHistory(history, record =>
            {
                record.SipPeer = $"{winner.Contact.PeerAddress}:{winner.Contact.Port}";
                record.SipOutcome = "answered";
                record.Codec = winner.Endpoint.SelectedCodec;
            });
            _logMessage?.Invoke($"SIP contact {winner.Contact.PeerAddress}:{winner.Contact.Port} answered first; cancelling other branches.");
            foreach (var branch in branches.Where(branch => branch != winner))
            {
                CancelBranch(branch);
            }

            voiceTransport = new VoiceSerialPcmTransport(_servicePort, _baudRate, _pcmFormat);
            await voiceTransport.OpenAsync(callCts.Token);
            winner.DeferredTransport.Attach(voiceTransport, callCts.Token);

            dtmfBridge = new SipDtmfBridge(_cellularCall, callCts.Token, _logMessage);
            dtmfHandler = (eventId, durationMilliseconds) => dtmfBridge.TryQueue(eventId, durationMilliseconds);
            winner.UserAgent.OnDtmfTone += dtmfHandler;

            _logMessage?.Invoke("Softphone answered; answering the cellular call with ATA.");
            await _cellularCall.AnswerIncomingAsync(callCts.Token);
            await WaitForConnectedAsync(callCts.Token);
            connected = true;
            UpdateCallHistory(history, record =>
            {
                record.ConnectTimestamp = DateTimeOffset.UtcNow;
                record.Status = CallStatus.Connected;
                record.CellularOutcome = "connected";
                record.SipOutcome = "connected";
                record.Codec = winner.Endpoint.SelectedCodec;
            });
            _logMessage?.Invoke(
                $"Incoming audio bridge started: codec={winner.Endpoint.SelectedCodec}, RTP-clock={winner.Endpoint.SelectedRtpClockRate}Hz, PCM={_pcmFormat.SampleRateHz}Hz/{_pcmFormat.BitsPerSample}bit/{_pcmFormat.Channels}ch.");

            var sipEnded = WaitForSipHangupAsync(winner.UserAgent, callCts.Token);
            var completedEnd = await Task.WhenAny(sipEnded, cellularEnded);
            await completedEnd;
            endOrigin = completedEnd == cellularEnded ? "cellular" : "sip";
            endReason = "normal hangup";
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            endOrigin = "gateway";
            endReason = "operation cancelled";
            throw;
        }
        catch (Exception ex)
        {
            endOrigin = "gateway";
            endReason = ex.Message;
            UpdateCallHistory(history, record =>
            {
                record.Status = connected ? CallStatus.Failed : CallStatus.FailedBeforeConnect;
                record.CellularOutcome = connected ? "failed" : "failed before connect";
                record.SipOutcome = "failed";
            });
            throw;
        }
        finally
        {
            callCts.Cancel();
            if (dtmfHandler is not null && winner is not null)
            {
                winner.UserAgent.OnDtmfTone -= dtmfHandler;
            }

            if (dtmfBridge is not null)
            {
                await dtmfBridge.DisposeAsync();
                var dtmf = dtmfBridge.GetSnapshot();
                _logMessage?.Invoke(
                    $"Incoming DTMF bridge: received={dtmf.ReceivedEvents}, forwarded={dtmf.ForwardedEvents}, failed={dtmf.FailedEvents}, dropped={dtmf.DroppedEvents}.");
            }

            if (_cellularCall.State is not CellularCallState.Idle)
            {
                await _cellularCall.HangupAsync(CancellationToken.None);
            }

            foreach (var branch in branches)
            {
                CancelBranch(branch);
                branch.MediaSession.Close("Incoming cellular call ended");
                branch.UserAgent.Close();
                await branch.DeferredTransport.DisposeAsync();
            }

            CallMediaSummary? mediaSummary = null;
            if (winner is not null)
            {
                var buffers = winner.Endpoint.GetBufferSnapshot();
                _logMessage?.Invoke(
                    $"Incoming audio buffers: modem->RTP dropped={buffers.ModemToRtpDroppedFrames}, underrun={buffers.ModemToRtpUnderrunFrames}, target={buffers.ModemToRtpTargetMilliseconds}ms; " +
                    $"RTP->modem dropped={buffers.RtpToModemDroppedFrames}, underrun={buffers.RtpToModemUnderrunFrames}, target={buffers.RtpToModemTargetMilliseconds}ms.");
                var uplinkLevel = winner.Endpoint.GetRtpToModemLevelSnapshot();
                _logMessage?.Invoke(
                    $"Incoming audio level RTP->modem: active-rms={FormatDbfs(uplinkLevel.ActiveInputRmsDbfs)}, peak={uplinkLevel.InputPeakDbfs:F1}dBFS, " +
                    $"active-frames={uplinkLevel.ActiveFrames}/{uplinkLevel.ProcessedFrames}.");
                var recovery = winner.Endpoint.GetOpusRecoverySnapshot();
                _logMessage?.Invoke(
                    $"Incoming OPUS receive recovery: missing={recovery.MissingPackets}, FEC/PLC-from-next={recovery.FecRecoveryFrames}, " +
                    $"PLC={recovery.PlcRecoveryFrames}, skipped={recovery.SkippedRecoveryFrames}, duplicate={recovery.DuplicatePackets}, late={recovery.LatePackets}.");
                var rtp = winner.GetRtpSnapshot();
                mediaSummary = new CallMediaSummary(
                    rtp.ReceivedPackets,
                    rtp.ExpectedPackets,
                    rtp.LostPackets,
                    rtp.OutOfOrderPackets,
                    rtp.JitterMilliseconds,
                    null,
                    null,
                    null,
                    uplinkLevel.ActiveInputRmsDbfs,
                    uplinkLevel.InputPeakDbfs,
                    uplinkLevel.ActiveFrames,
                    uplinkLevel.ProcessedFrames,
                    buffers.ModemToRtpDroppedFrames,
                    buffers.RtpToModemDroppedFrames);
            }

            UpdateCallHistory(history, record =>
            {
                record.EndTimestamp = DateTimeOffset.UtcNow;
                if (cancelled)
                {
                    record.Status = CallStatus.Cancelled;
                    record.CellularOutcome = connected ? "cancelled after connect" : "cancelled before connect";
                    record.SipOutcome = "cancelled";
                }
                else if (record.Status == CallStatus.Connected)
                {
                    record.Status = CallStatus.Completed;
                    record.CellularOutcome = "completed";
                    record.SipOutcome = "completed";
                }
                else if (record.Status == CallStatus.Requested)
                {
                    record.Status = CallStatus.Unanswered;
                    record.CellularOutcome = "ended before connect";
                    record.SipOutcome = "unanswered";
                }

                record.EndOrigin = endOrigin ?? "gateway";
                record.EndReason = endReason ?? (connected ? "normal hangup" : "ended before connect");
                record.Media = mediaSummary;
            });

            if (voiceTransport is not null)
            {
                await voiceTransport.DisposeAsync();
            }

            _callGate.Release();
        }
    }

    private static void CancelBranch(IncomingSipBranch branch)
    {
        if (branch.UserAgent.IsCallActive && !branch.UserAgent.IsHangingUp)
        {
            branch.UserAgent.Hangup();
        }
        else if (branch.UserAgent.IsCalling || branch.UserAgent.IsRinging)
        {
            branch.UserAgent.Cancel();
        }
    }

    internal static SipCallerIdentity CreateCallerIdentity(string? callerId)
    {
        var trimmed = callerId?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return new SipCallerIdentity("Private caller", "anonymous", false);
        }

        var digits = new string(trimmed.Where(character => character is >= '0' and <= '9').ToArray());
        if (digits.Length == 0)
        {
            return new SipCallerIdentity("Private caller", "anonymous", false);
        }

        var normalized = trimmed.StartsWith('+') ? $"+{digits}" : digits;
        return new SipCallerIdentity(normalized, normalized, true);
    }

    private async Task WaitForConnectedAsync(CancellationToken cancellationToken)
    {
        if (_cellularCall.State == CellularCallState.Connected)
        {
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, CellularCallState state)
        {
            if (state == CellularCallState.Connected)
            {
                completion.TrySetResult();
            }
            else if (state is CellularCallState.Failed or CellularCallState.Idle)
            {
                completion.TrySetException(new InvalidOperationException($"Incoming cellular call ended before connection: {state}."));
            }
        }

        _cellularCall.StateChanged += Handler;
        try
        {
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(_incomingConfig.CellularAnswerTimeoutSeconds), cancellationToken);
        }
        finally
        {
            _cellularCall.StateChanged -= Handler;
        }
    }

    private async Task<IReadOnlyList<RegisteredSipContact>> WaitForSipContactsAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (_cellularCall.State == CellularCallState.IncomingRinging && DateTimeOffset.UtcNow <= deadline)
        {
            var contacts = _sipHost.GetRegisteredContacts();
            if (contacts.Count > 0)
            {
                return contacts;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        return [];
    }

    private Task WaitForCellularEndAsync(CancellationToken cancellationToken)
    {
        if (_cellularCall.State is CellularCallState.Idle or CellularCallState.Failed)
        {
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, CellularCallState state)
        {
            if (state is CellularCallState.Idle or CellularCallState.Failed)
            {
                completion.TrySetResult();
            }
        }

        _cellularCall.StateChanged += Handler;
        return WaitAsync();

        async Task WaitAsync()
        {
            try
            {
                await completion.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                _cellularCall.StateChanged -= Handler;
            }
        }
    }

    private static Task WaitForSipHangupAsync(SIPUserAgent userAgent, CancellationToken cancellationToken)
    {
        if (!userAgent.IsCallActive)
        {
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(SIPDialogue _) => completion.TrySetResult();
        userAgent.OnCallHungup += Handler;
        return WaitAsync();

        async Task WaitAsync()
        {
            try
            {
                await completion.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                userAgent.OnCallHungup -= Handler;
            }
        }
    }

    private static string FormatDbfs(double? value) => value is { } dbfs ? $"{dbfs:F1}dBFS" : "unavailable";

    private void UpdateCallHistory(CallHistoryRecord? record, Action<CallHistoryRecord> update)
    {
        if (record is null || _callHistory is null)
        {
            return;
        }

        try
        {
            _callHistory.Update(record.CorrelationId, update);
        }
        catch (Exception ex)
        {
            _logMessage?.Invoke($"Call history update failed for {record.CorrelationId}: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cellularCall.IncomingCallReceived -= OnIncomingCallReceived;
        _lifetimeCts.Cancel();
        if (_activeCallTask is not null)
        {
            try
            {
                await _activeCallTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _lifetimeCts.Dispose();
        _callGate.Dispose();
    }

    private sealed record IncomingSipBranch(
        RegisteredSipContact Contact,
        SIPUserAgent UserAgent,
        VoIPMediaSession MediaSession,
        Dwm157AudioEndPoint Endpoint,
        DeferredVoicePcmTransport DeferredTransport,
        Task<bool> CallTask,
        Func<RtpReceiveSnapshot> GetRtpSnapshot);

    internal sealed record SipCallerIdentity(string DisplayName, string Username, bool IsAvailable);
}
