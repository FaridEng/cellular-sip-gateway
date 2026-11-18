using System.Net;
using DWM157Gateway.Core.Audio;
using DWM157Gateway.Core.Configuration;
using DWM157Gateway.Core.Diagnostics;
using DWM157Gateway.Core.Dialing;
using DWM157Gateway.Core.Modem;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace DWM157Gateway.Core.Sip;

public sealed class CellularSipCallCoordinator
{
    private readonly CellularCallController _cellularCall;
    private readonly string _servicePort;
    private readonly int _baudRate;
    private readonly PcmFormat _pcmFormat;
    private readonly string _bindAddress;
    private readonly int _rtpPort;
    private readonly SipConfig _sipConfig;
    private readonly JitterBufferConfig _jitterBuffer;
    private readonly JitterBufferConfig _modemToRtpBuffer;
    private readonly AudioPacingConfig _pacingConfig;
    private readonly double _modemToRtpGainDb;
    private readonly Action<string>? _logMessage;
    private readonly CallHistoryStore? _callHistory;

    public CellularSipCallCoordinator(
        CellularCallController cellularCall,
        string servicePort,
        int baudRate,
        PcmFormat pcmFormat,
        string bindAddress,
        int rtpPort)
        : this(
            cellularCall,
            servicePort,
            baudRate,
            pcmFormat,
            bindAddress,
            rtpPort,
            new SipConfig(),
            new JitterBufferConfig(),
            new AudioConfig().ModemToRtpBuffer,
            new AudioPacingConfig(),
            new AudioConfig().ModemToRtpGainDb)
    {
    }

    public CellularSipCallCoordinator(
        CellularCallController cellularCall,
        string servicePort,
        int baudRate,
        PcmFormat pcmFormat,
        string bindAddress,
        int rtpPort,
        SipConfig sipConfig,
        JitterBufferConfig jitterBuffer,
        JitterBufferConfig modemToRtpBuffer,
        AudioPacingConfig pacingConfig,
        double modemToRtpGainDb,
        Action<string>? logMessage = null,
        CallHistoryStore? callHistory = null)
    {
        _cellularCall = cellularCall;
        _servicePort = servicePort;
        _baudRate = baudRate;
        _pcmFormat = pcmFormat;
        _bindAddress = bindAddress;
        _rtpPort = rtpPort;
        _sipConfig = sipConfig;
        _jitterBuffer = jitterBuffer;
        _modemToRtpBuffer = modemToRtpBuffer;
        _pacingConfig = pacingConfig;
        _modemToRtpGainDb = modemToRtpGainDb;
        _logMessage = logMessage;
        _callHistory = callHistory;
    }

    public async Task HandleInviteAsync(
        SIPUserAgent userAgent,
        SIPServerUserAgent serverUserAgent,
        SIPRequest request,
        RegisteredSipContact sipPeer,
        CancellationToken cancellationToken)
    {
        var history = _callHistory?.Begin(
            CallDirection.Outgoing,
            request.URI.User,
            $"{sipPeer.PeerAddress}:{sipPeer.Port}");
        var connected = false;
        var cancelled = false;
        CallEndOrigin? endOrigin = null;
        string? failureReason = null;
        var offeredCodecs = GetOfferedAudioCodecs(request);
        _logMessage?.Invoke(
            $"SIP audio offer: {(offeredCodecs.Count == 0 ? "none" : string.Join(", ", offeredCodecs.Select(codec => codec.Description)))}.");
        _logMessage?.Invoke($"SIP media offer: {GetAudioOfferDiagnostics(request.Body)}.");
        _logMessage?.Invoke($"SIP DTMF offer: telephone-event={(HasTelephoneEventOffer(request) ? "yes" : "no")}.");
        if (_sipConfig.ForcePreferredCodec &&
            !offeredCodecs.Any(codec => string.Equals(codec.Name, _sipConfig.PreferredCodec, StringComparison.OrdinalIgnoreCase)))
        {
            var reason = $"Required audio codec {_sipConfig.PreferredCodec} was not offered.";
            _logMessage?.Invoke(reason);
            serverUserAgent.Reject(SIPResponseStatusCodesEnum.NotAcceptableHere, reason);
            userAgent.Close();
            UpdateCallHistory(history, record =>
            {
                record.EndTimestamp = DateTimeOffset.UtcNow;
                record.Status = CallStatus.Rejected;
                record.CellularOutcome = "not dialed";
                record.SipOutcome = "rejected";
                record.EndOrigin = "gateway";
                record.EndReason = reason;
            });
            return;
        }

        DialDecision decision;
        try
        {
            decision = await _cellularCall.DialAsync(request.URI.User, cancellationToken);
        }
        catch (Exception ex)
        {
            UpdateCallHistory(history, record =>
            {
                record.EndTimestamp = DateTimeOffset.UtcNow;
                record.Status = CallStatus.FailedBeforeConnect;
                record.CellularOutcome = "dial failed";
                record.SipOutcome = "failed";
                record.EndOrigin = "gateway";
                record.EndReason = ex.Message;
            });
            throw;
        }

        if (!decision.IsAllowed)
        {
            serverUserAgent.Reject(SIPResponseStatusCodesEnum.Forbidden, decision.Reason);
            userAgent.Close();
            UpdateCallHistory(history, record =>
            {
                record.RemoteNumber = decision.NormalizedNumber ?? request.URI.User;
                record.EndTimestamp = DateTimeOffset.UtcNow;
                record.Status = CallStatus.Rejected;
                record.CellularOutcome = "not dialed";
                record.SipOutcome = "rejected";
                record.EndOrigin = "gateway";
                record.EndReason = decision.Reason;
            });
            return;
        }

        UpdateCallHistory(history, record =>
        {
            record.RemoteNumber = decision.NormalizedNumber ?? request.URI.User;
            record.StartTimestamp = DateTimeOffset.UtcNow;
            record.CellularOutcome = "dialing";
            record.SipOutcome = "accepted";
        });

        VoiceSerialPcmTransport? voiceTransport = null;
        VoIPMediaSession? mediaSession = null;
        Dwm157AudioEndPoint? endpoint = null;
        RtpReceiveStatistics? rtpStatistics = null;
        SipDtmfBridge? dtmfBridge = null;
        Action<byte, int>? dtmfHandler = null;
        var rtcpLock = new object();
        RtcpRemoteReceptionSnapshot? remoteReception = null;
        try
        {
            await WaitForConnectedAsync(cancellationToken);
            connected = true;
            UpdateCallHistory(history, record =>
            {
                record.ConnectTimestamp = DateTimeOffset.UtcNow;
                record.Status = CallStatus.Connected;
                record.CellularOutcome = "connected";
            });

            voiceTransport = new VoiceSerialPcmTransport(_servicePort, _baudRate, _pcmFormat);
            await voiceTransport.OpenAsync(cancellationToken);

            endpoint = new Dwm157AudioEndPoint(
                voiceTransport,
                _pcmFormat,
                _sipConfig,
                _jitterBuffer,
                _modemToRtpBuffer,
                _pacingConfig,
                _modemToRtpGainDb);
            mediaSession = new VoIPMediaSession(
                endpoint.ToMediaEndPoints(),
                IPAddress.Parse(_bindAddress),
                _rtpPort);
            mediaSession.AcceptRtpFromAny = false;
            dtmfBridge = new SipDtmfBridge(_cellularCall, cancellationToken, _logMessage);
            dtmfHandler = (eventId, durationMilliseconds) =>
                dtmfBridge.TryQueue(eventId, durationMilliseconds);
            userAgent.OnDtmfTone += dtmfHandler;
            mediaSession.OnRtpPacketReceived += (_, mediaType, packet) =>
            {
                if (mediaType == SDPMediaTypesEnum.audio)
                {
                    rtpStatistics?.Record(packet.Header.SequenceNumber, packet.Header.Timestamp, packet.Payload.Length);
                }
            };
            mediaSession.OnReceiveReport += (_, mediaType, report) =>
            {
                if (mediaType != SDPMediaTypesEnum.audio)
                {
                    return;
                }

                var reception = report.ReceiverReport?.ReceptionReports?.FirstOrDefault()
                    ?? report.SenderReport?.ReceptionReports?.FirstOrDefault();
                if (reception is null)
                {
                    return;
                }

                lock (rtcpLock)
                {
                    var roundTripMilliseconds = CalculateRtcpRoundTripMilliseconds(
                        reception.LastSenderReportTimestamp,
                        reception.DelaySinceLastSenderReport,
                        DateTimeOffset.UtcNow);
                    remoteReception = new RtcpRemoteReceptionSnapshot(
                        (remoteReception?.ReportCount ?? 0) + 1,
                        reception.FractionLost,
                        reception.PacketsLost,
                        reception.Jitter,
                        reception.ExtendedHighestSequenceNumber,
                        Math.Max(remoteReception?.MaximumFractionLost ?? 0, reception.FractionLost),
                        Math.Max(remoteReception?.MaximumJitterRtpUnits ?? 0, reception.Jitter),
                        roundTripMilliseconds ?? remoteReception?.LatestRoundTripMilliseconds,
                        Math.Max(remoteReception?.MaximumRoundTripMilliseconds ?? 0, roundTripMilliseconds ?? 0));
                }
            };

            var answered = await userAgent.Answer(
                serverUserAgent,
                mediaSession,
                IPAddress.Parse(_bindAddress));
            if (!answered)
            {
                throw new InvalidOperationException("SIP answer failed.");
            }

            rtpStatistics = new RtpReceiveStatistics(endpoint.SelectedRtpClockRate);
            await mediaSession.Start();
            UpdateCallHistory(history, record => record.Codec = endpoint.SelectedCodec);
            _logMessage?.Invoke($"Audio bridge started: codec={endpoint.SelectedCodec}, RTP-clock={endpoint.SelectedRtpClockRate}Hz, PCM={_pcmFormat.SampleRateHz}Hz/{_pcmFormat.BitsPerSample}bit/{_pcmFormat.Channels}ch, frame={_pcmFormat.FrameMilliseconds}ms, modem->RTP gain={_modemToRtpGainDb:F1}dB.");
            using var callEndCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                var sipEnded = WaitForHangupAsync(userAgent, callEndCts.Token);
                var cellularEnded = WaitForCellularEndAsync(callEndCts.Token);
                endOrigin = await WaitForCallEndAsync(sipEnded, cellularEnded);
                if (endOrigin == CallEndOrigin.Cellular)
                {
                    _logMessage?.Invoke("Cellular call ended; sending SIP BYE to the softphone.");
                    if (userAgent.IsCallActive && !userAgent.IsHangingUp)
                    {
                        userAgent.Hangup();
                    }
                }
            }
            finally
            {
                callEndCts.Cancel();
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            failureReason = "operation cancelled";
            userAgent.Hangup();
        }
        catch (Exception ex)
        {
            failureReason = ex.Message;
            if (!userAgent.IsHangingUp)
            {
                userAgent.Hangup();
            }

            throw;
        }
        finally
        {
            if (dtmfHandler is not null)
            {
                userAgent.OnDtmfTone -= dtmfHandler;
            }

            mediaSession?.Close("SIP call ended");
            if (dtmfBridge is not null)
            {
                await dtmfBridge.DisposeAsync();
                var dtmf = dtmfBridge.GetSnapshot();
                _logMessage?.Invoke(
                    $"DTMF bridge: received={dtmf.ReceivedEvents}, forwarded={dtmf.ForwardedEvents}, failed={dtmf.FailedEvents}, dropped={dtmf.DroppedEvents}.");
            }

            if (endpoint is not null)
            {
                var buffers = endpoint.GetBufferSnapshot();
                _logMessage?.Invoke(
                    $"Audio buffers: modem->RTP dropped={buffers.ModemToRtpDroppedFrames}, underrun={buffers.ModemToRtpUnderrunFrames}, target={buffers.ModemToRtpTargetMilliseconds}ms, remaining={buffers.ModemToRtpBufferedFrames}; " +
                    $"RTP->modem dropped={buffers.RtpToModemDroppedFrames}, underrun={buffers.RtpToModemUnderrunFrames}, target={buffers.RtpToModemTargetMilliseconds}ms, remaining={buffers.RtpToModemBufferedFrames}.");
                _logMessage?.Invoke(
                    $"Audio queue age: modem->RTP avg={buffers.ModemToRtpTiming.AverageQueueAgeMilliseconds:F1}ms, max={buffers.ModemToRtpTiming.MaximumQueueAgeMilliseconds:F1}ms, " +
                    $"latest={buffers.ModemToRtpTiming.LatestQueueAgeMilliseconds:F1}ms, oldest-buffered={buffers.ModemToRtpTiming.OldestBufferedFrameAgeMilliseconds:F1}ms; " +
                    $"RTP->modem avg={buffers.RtpToModemTiming.AverageQueueAgeMilliseconds:F1}ms, max={buffers.RtpToModemTiming.MaximumQueueAgeMilliseconds:F1}ms, " +
                    $"latest={buffers.RtpToModemTiming.LatestQueueAgeMilliseconds:F1}ms, oldest-buffered={buffers.RtpToModemTiming.OldestBufferedFrameAgeMilliseconds:F1}ms.");
                var pacing = endpoint.GetPacingSnapshot();
                _logMessage?.Invoke(FormatPacingSnapshot("RTP send pacing", pacing.RtpSend));
                _logMessage?.Invoke(FormatPacingSnapshot("Modem write pacing", pacing.ModemWrite));
                var gain = endpoint.GetModemToRtpGainSnapshot();
                _logMessage?.Invoke(
                    $"Audio level modem->RTP: gain={gain.GainDb:F1}dB, active-input-rms={FormatDbfs(gain.ActiveInputRmsDbfs)}, " +
                    $"active-output-rms={FormatDbfs(gain.ActiveOutputRmsDbfs)}, input-peak={gain.InputPeakDbfs:F1}dBFS, output-peak={gain.OutputPeakDbfs:F1}dBFS, " +
                    $"clipped={gain.ClippedSamples}/{gain.ProcessedSamples} ({gain.ClippedSamplePercent:F4}%).");
                var uplinkLevel = endpoint.GetRtpToModemLevelSnapshot();
                _logMessage?.Invoke(
                    $"Audio level RTP->modem: active-rms={FormatDbfs(uplinkLevel.ActiveInputRmsDbfs)}, peak={uplinkLevel.InputPeakDbfs:F1}dBFS, " +
                    $"active-frames={uplinkLevel.ActiveFrames}/{uplinkLevel.ProcessedFrames}.");
                var recovery = endpoint.GetOpusRecoverySnapshot();
                _logMessage?.Invoke(
                    $"OPUS receive recovery: missing={recovery.MissingPackets}, FEC/PLC-from-next={recovery.FecRecoveryFrames}, " +
                    $"PLC={recovery.PlcRecoveryFrames}, skipped={recovery.SkippedRecoveryFrames}, duplicate={recovery.DuplicatePackets}, late={recovery.LatePackets}.");
                var sentPayload = endpoint.GetEncodedPayloadSnapshot();
                _logMessage?.Invoke(
                    $"RTP send payload: packets={sentPayload.Packets}, bytes={sentPayload.Bytes}, avg={sentPayload.AverageBytes:F1}B, " +
                    $"min={sentPayload.MinimumBytes}B, max={sentPayload.MaximumBytes}B, bitrate={sentPayload.BitrateBitsPerSecond / 1000.0:F1}kbit/s.");
            }

            var rtp = rtpStatistics?.GetSnapshot() ?? new RtpReceiveSnapshot(0, 0, 0, 0, 0);
            _logMessage?.Invoke(
                $"RTP receive: received={rtp.ReceivedPackets}, expected={rtp.ExpectedPackets}, lost={rtp.LostPackets}, out-of-order={rtp.OutOfOrderPackets}, jitter={rtp.JitterMilliseconds:F1}ms.");
            _logMessage?.Invoke(
                $"RTP receive payload: bytes={rtp.PayloadBytes}, avg={rtp.AveragePayloadBytes:F1}B, min={rtp.MinimumPayloadBytes}B, max={rtp.MaximumPayloadBytes}B, " +
                $"bitrate={rtp.PayloadBitrateBitsPerSecond / 1000.0:F1}kbit/s, max-arrival-gap={rtp.MaximumArrivalGapMilliseconds:F1}ms, " +
                $"gaps>25ms={rtp.ArrivalGapsOver25Milliseconds}, gaps>40ms={rtp.ArrivalGapsOver40Milliseconds}.");
            RtcpRemoteReceptionSnapshot? remoteSnapshot;
            lock (rtcpLock)
            {
                remoteSnapshot = remoteReception;
            }

            if (remoteSnapshot is not null)
            {
                var clockRate = endpoint?.SelectedRtpClockRate ?? _pcmFormat.SampleRateHz;
                var remoteJitterMilliseconds = remoteSnapshot.JitterRtpUnits * 1000.0 / clockRate;
                var maximumRemoteJitterMilliseconds = remoteSnapshot.MaximumJitterRtpUnits * 1000.0 / clockRate;
                var roundTrip = remoteSnapshot.LatestRoundTripMilliseconds is { } latestRoundTrip
                    ? $", latest-rtt={latestRoundTrip:F1}ms, max-rtt={remoteSnapshot.MaximumRoundTripMilliseconds:F1}ms"
                    : ", rtt=unavailable";
                _logMessage?.Invoke(
                    $"RTCP remote receive (gateway->softphone): reports={remoteSnapshot.ReportCount}, latest-fraction-lost={remoteSnapshot.FractionLost * 100.0 / 256.0:F2}%, " +
                    $"max-fraction-lost={remoteSnapshot.MaximumFractionLost * 100.0 / 256.0:F2}%, reported-lost={remoteSnapshot.ReportedPacketsLost}, " +
                    $"highest-sequence={remoteSnapshot.ExtendedHighestSequenceNumber}, latest-jitter={remoteJitterMilliseconds:F1}ms, max-jitter={maximumRemoteJitterMilliseconds:F1}ms{roundTrip}.");
            }
            else
            {
                _logMessage?.Invoke("RTCP remote receive (gateway->softphone): no receiver report was received.");
            }

            CallMediaSummary? mediaSummary = null;
            if (endpoint is not null)
            {
                var buffers = endpoint.GetBufferSnapshot();
                var uplinkLevel = endpoint.GetRtpToModemLevelSnapshot();
                var clockRate = endpoint.SelectedRtpClockRate > 0 ? endpoint.SelectedRtpClockRate : _pcmFormat.SampleRateHz;
                mediaSummary = new CallMediaSummary(
                    rtp.ReceivedPackets,
                    rtp.ExpectedPackets,
                    rtp.LostPackets,
                    rtp.OutOfOrderPackets,
                    rtp.JitterMilliseconds,
                    remoteSnapshot is null ? null : remoteSnapshot.FractionLost * 100.0 / 256.0,
                    remoteSnapshot is null ? null : remoteSnapshot.JitterRtpUnits * 1000.0 / clockRate,
                    remoteSnapshot?.LatestRoundTripMilliseconds,
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
                record.Status = cancelled
                    ? CallStatus.Cancelled
                    : failureReason is not null
                        ? connected ? CallStatus.Failed : CallStatus.FailedBeforeConnect
                        : connected ? CallStatus.Completed : CallStatus.FailedBeforeConnect;
                record.CellularOutcome = cancelled
                    ? connected ? "cancelled after connect" : "cancelled before connect"
                    : connected ? "completed" : "failed before connect";
                record.SipOutcome = cancelled ? "cancelled" : failureReason is null ? "completed" : "failed";
                record.EndOrigin = endOrigin?.ToString().ToLowerInvariant() ?? "gateway";
                record.EndReason = failureReason ?? "normal hangup";
                record.Media = mediaSummary;
            });

            if (voiceTransport is not null)
            {
                var serial = voiceTransport.GetSnapshot();
                _logMessage?.Invoke(
                    $"Voice serial: received={serial.ReceivedFrames}, written={serial.WrittenFrames}, average-rx={serial.AverageReceivedFramesPerSecond:F2}fps, " +
                    $"max-rx-gap={serial.MaximumReceiveGapMilliseconds:F1}ms, gaps>2frames={serial.ReceiveGapsOverTwoFrames}, bursts<quarter-frame={serial.ReceiveBurstsUnderQuarterFrame}.");
            }
            if (_cellularCall.State is not CellularCallState.Idle)
            {
                await _cellularCall.HangupAsync(CancellationToken.None);
            }

            if (voiceTransport is not null)
            {
                await voiceTransport.DisposeAsync();
            }

            userAgent.Close();
        }
    }

    private async Task WaitForConnectedAsync(CancellationToken cancellationToken)
    {
        if (_cellularCall.State == CellularCallState.Connected)
        {
            return;
        }

        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, CellularCallState state)
        {
            if (state == CellularCallState.Connected)
            {
                connected.TrySetResult();
            }
            else if (state is CellularCallState.Failed or CellularCallState.Idle)
            {
                connected.TrySetException(new InvalidOperationException($"Cellular call ended before connection: {state}."));
            }
        }

        _cellularCall.StateChanged += Handler;
        try
        {
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
        }
        finally
        {
            _cellularCall.StateChanged -= Handler;
        }
    }

    private static Task WaitForHangupAsync(SIPUserAgent userAgent, CancellationToken cancellationToken)
    {
        if (!userAgent.IsCallActive)
        {
            return Task.CompletedTask;
        }

        var hungup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(SIPDialogue _) => hungup.TrySetResult();
        userAgent.OnCallHungup += Handler;

        return WaitAsync();

        async Task WaitAsync()
        {
            try
            {
                await hungup.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                userAgent.OnCallHungup -= Handler;
            }
        }
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

    internal static async Task<CallEndOrigin> WaitForCallEndAsync(Task sipEnded, Task cellularEnded)
    {
        ArgumentNullException.ThrowIfNull(sipEnded);
        ArgumentNullException.ThrowIfNull(cellularEnded);
        var completed = await Task.WhenAny(sipEnded, cellularEnded);
        await completed;
        return completed == cellularEnded ? CallEndOrigin.Cellular : CallEndOrigin.Sip;
    }

    private static List<OfferedAudioCodec> GetOfferedAudioCodecs(SIPRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Body))
        {
            return [];
        }

        try
        {
            var sdp = SDP.ParseSDPDescription(request.Body);
            var audio = sdp.Media.FirstOrDefault(media => media.Media == SDPMediaTypesEnum.audio);
            if (audio is null)
            {
                return [];
            }

            return audio.MediaFormats.Values
                .Where(format => !string.Equals(format.Name(), SDP.TELEPHONE_EVENT_ATTRIBUTE, StringComparison.OrdinalIgnoreCase))
                .Select(format => new OfferedAudioCodec(
                    format.Name() ?? $"payload-{format.ID}",
                    format.Rtpmap ?? $"{format.Name()}/{format.ClockRate()}/{Math.Max(1, format.Channels())}"))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static bool HasTelephoneEventOffer(SIPRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Body))
        {
            return false;
        }

        try
        {
            var sdp = SDP.ParseSDPDescription(request.Body);
            var audio = sdp.Media.FirstOrDefault(media => media.Media == SDPMediaTypesEnum.audio);
            return audio?.MediaFormats.Values.Any(format =>
                string.Equals(format.Name(), SDP.TELEPHONE_EVENT_ATTRIBUTE, StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch
        {
            return false;
        }
    }

    internal static string GetAudioOfferDiagnostics(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "none";
        }

        var lines = body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var audioStart = Array.FindIndex(lines, line => line.StartsWith("m=audio ", StringComparison.OrdinalIgnoreCase));
        if (audioStart < 0)
        {
            return "no-audio-media";
        }

        var audioEnd = Array.FindIndex(lines, audioStart + 1, line => line.StartsWith("m=", StringComparison.OrdinalIgnoreCase));
        if (audioEnd < 0)
        {
            audioEnd = lines.Length;
        }

        var audioLines = lines[audioStart..audioEnd];
        var opusPayload = audioLines
            .Select(line => line.StartsWith("a=rtpmap:", StringComparison.OrdinalIgnoreCase) ? line["a=rtpmap:".Length..] : null)
            .FirstOrDefault(value => value?.Contains(" opus/", StringComparison.OrdinalIgnoreCase) == true)?
            .Split(' ', 2)[0];
        var opusFmtp = opusPayload is null
            ? "none"
            : GetSafeOpusFmtp(audioLines.FirstOrDefault(line =>
                line.StartsWith($"a=fmtp:{opusPayload} ", StringComparison.OrdinalIgnoreCase)));
        var ptime = GetAttributeValue(audioLines, "a=ptime:");
        var maxPtime = GetAttributeValue(audioLines, "a=maxptime:");
        var direction = audioLines.FirstOrDefault(line =>
            line is "a=sendrecv" or "a=sendonly" or "a=recvonly" or "a=inactive")?[2..] ?? "unspecified";
        var bandwidth = audioLines.FirstOrDefault(line =>
            line.StartsWith("b=AS:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("b=TIAS:", StringComparison.OrdinalIgnoreCase))?[2..] ?? "unspecified";
        var candidateCount = audioLines.Count(line => line.StartsWith("a=candidate:", StringComparison.OrdinalIgnoreCase));
        var hasIce = audioLines.Any(line => line.StartsWith("a=ice-ufrag:", StringComparison.OrdinalIgnoreCase));
        var hasRtcpMux = audioLines.Any(line => line.Equals("a=rtcp-mux", StringComparison.OrdinalIgnoreCase));

        return $"ptime={ptime}, maxptime={maxPtime}, opus-fmtp={opusFmtp}, ice={(hasIce ? "yes" : "no")}, " +
               $"candidates={candidateCount}, rtcp-mux={(hasRtcpMux ? "yes" : "no")}, direction={direction}, bandwidth={bandwidth}";
    }

    private static string GetAttributeValue(string[] lines, string prefix) =>
        lines.FirstOrDefault(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..]
        ?? "unspecified";

    private static string GetSafeOpusFmtp(string? fmtpLine)
    {
        if (string.IsNullOrWhiteSpace(fmtpLine))
        {
            return "none";
        }

        var separator = fmtpLine.IndexOf(' ');
        if (separator < 0 || separator == fmtpLine.Length - 1)
        {
            return "none";
        }

        string[] allowedKeys =
        [
            "useinbandfec", "maxaveragebitrate", "minptime", "maxplaybackrate", "stereo", "sprop-stereo", "usedtx"
        ];
        var safeParameters = fmtpLine[(separator + 1)..]
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(parameter =>
            {
                var equals = parameter.IndexOf('=');
                var key = equals < 0 ? parameter : parameter[..equals];
                return allowedKeys.Contains(key, StringComparer.OrdinalIgnoreCase);
            });
        var safe = string.Join(';', safeParameters);
        return string.IsNullOrWhiteSpace(safe) ? "none" : safe;
    }

    private string FormatPacingSnapshot(string label, AudioPacingSnapshot pacing) =>
        $"{label}: frames={pacing.Frames}, average={pacing.AverageIntervalMilliseconds:F2}ms, min={pacing.MinimumIntervalMilliseconds:F2}ms, " +
        $"max={pacing.MaximumIntervalMilliseconds:F2}ms, gaps>{_pacingConfig.GapWarningMilliseconds}ms={pacing.GapsOverWarning}, " +
        $"gaps>{_pcmFormat.FrameMilliseconds * 2}ms={pacing.GapsOverTwoFrames}, bursts<{_pacingConfig.BurstWarningMilliseconds}ms={pacing.BurstsUnderWarning}, " +
        $"schedule-adjustments={pacing.ScheduleAdjustments}.";

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

    internal static double? CalculateRtcpRoundTripMilliseconds(
        uint lastSenderReportTimestamp,
        uint delaySinceLastSenderReport,
        DateTimeOffset arrivalTime)
    {
        if (lastSenderReportTimestamp == 0)
        {
            return null;
        }

        const long ntpEpochOffsetSeconds = 2_208_988_800;
        var utcTicks = arrivalTime.UtcDateTime.Ticks;
        var ntpSeconds = arrivalTime.ToUnixTimeSeconds() + ntpEpochOffsetSeconds;
        var ticksWithinSecond = utcTicks % TimeSpan.TicksPerSecond;
        var ntpFraction = (ulong)ticksWithinSecond * (1UL << 32) / TimeSpan.TicksPerSecond;
        var arrivalMiddle32 = (uint)(((ulong)ntpSeconds & 0xffff) << 16 | (ntpFraction >> 16));
        return CalculateRtcpRoundTripMilliseconds(
            lastSenderReportTimestamp,
            delaySinceLastSenderReport,
            arrivalMiddle32);
    }

    internal static double? CalculateRtcpRoundTripMilliseconds(
        uint lastSenderReportTimestamp,
        uint delaySinceLastSenderReport,
        uint arrivalMiddle32)
    {
        if (lastSenderReportTimestamp == 0)
        {
            return null;
        }

        var roundTripUnits = unchecked(arrivalMiddle32 - lastSenderReportTimestamp - delaySinceLastSenderReport);
        var roundTripMilliseconds = roundTripUnits * 1000.0 / 65_536.0;
        return roundTripMilliseconds <= TimeSpan.FromMinutes(1).TotalMilliseconds
            ? roundTripMilliseconds
            : null;
    }

    private sealed record OfferedAudioCodec(string Name, string Description);

    internal enum CallEndOrigin
    {
        Sip,
        Cellular
    }

    private sealed record RtcpRemoteReceptionSnapshot(
        long ReportCount,
        byte FractionLost,
        int ReportedPacketsLost,
        uint JitterRtpUnits,
        uint ExtendedHighestSequenceNumber,
        byte MaximumFractionLost,
        uint MaximumJitterRtpUnits,
        double? LatestRoundTripMilliseconds,
        double MaximumRoundTripMilliseconds);
}
