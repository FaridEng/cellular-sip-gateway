using DWM157Gateway.Core.Audio;
using DWM157Gateway.Core.Configuration;
using DWM157Gateway.Core.Dialing;
using DWM157Gateway.Core.Diagnostics;
using DWM157Gateway.Core.Modem;
using DWM157Gateway.Core.Sip;

namespace DWM157Gateway.Core;

public sealed class GatewayRuntime : IAsyncDisposable
{
    private readonly string _configPath;
    private CancellationTokenSource? _lifetimeCts;
    private SerialAtTransport? _controlTransport;
    private CellularCallController? _cellularCall;
    private SipGatewayHost? _sipHost;
    private IncomingCellularSipCallCoordinator? _incomingCoordinator;
    private Task? _incomingPollTask;
    private Task? _monitoringTask;
    private ModemPreflightResult? _monitoringPreflight;
    private DateTimeOffset? _connectedAtUtc;
    private TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public GatewayRuntime(string configPath)
    {
        _configPath = Path.GetFullPath(configPath);
    }

    public bool IsRunning => _sipHost?.IsRunning == true;
    public Task Completion => _completion.Task;
    public GatewayConfig? Config { get; private set; }
    public event Action<string>? LogMessage;

    public async Task<GatewayStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("The Gateway is already running.");
        }

        try
        {
            _completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Config = ConfigLoader.LoadOrCreate(_configPath);
            Log($"DWM157Gateway {AppVersion.Version} starting.");
            Log($"Loaded config from {_configPath}");

            var discovery = new PnpComPortDiscovery();
            var ports = discovery.Discover(Config.Modem);
            Log($"Control port: {ports.ControlPort.PortName} ({ports.ControlPort.FriendlyName})");
            Log($"Service port: {ports.ServicePort.PortName} ({ports.ServicePort.FriendlyName})");
            Log($"SIP bind target: {Config.Sip.BindAddress}:{Config.Sip.SipPort}");
            Log($"Dial policy: allow-unlisted={Config.DialPolicy.AllowUnlistedNumbers}, blocked={Config.DialPolicy.BlockedNumbers.Count}");
            Log($"Audio codecs: preferred={Config.Sip.PreferredCodec}, force={Config.Sip.ForcePreferredCodec}, enabled={string.Join(",", Config.Sip.EnabledCodecs)}, OPUS bitrate={Config.Sip.OpusBitrateBps}, FEC={Config.Sip.OpusFec}, expected-loss={Config.Sip.OpusExpectedPacketLossPercent}%");
            Log($"Incoming calls: enabled={Config.IncomingCalls.Enabled}, poll={Config.IncomingCalls.PollIntervalMilliseconds}ms, caller-ID wait={Config.IncomingCalls.CallerIdWaitMilliseconds}ms, SIP ring timeout={Config.IncomingCalls.SipRingTimeoutSeconds}s");
            Log($"Audio gain: modem->RTP={Config.Audio.ModemToRtpGainDb:F1}dB");
            Log($"Modem-to-RTP buffer: enabled={Config.Audio.ModemToRtpBuffer.Enabled}, target={Config.Audio.ModemToRtpBuffer.TargetBufferMilliseconds}ms, max={Config.Audio.ModemToRtpBuffer.MaxBufferMilliseconds}ms");
            Log($"Adaptive jitter buffer: enabled={Config.Audio.JitterBuffer.Enabled}, target={Config.Audio.JitterBuffer.TargetBufferMilliseconds}ms, max={Config.Audio.JitterBuffer.MaxBufferMilliseconds}ms");
            Log($"Audio pacing: prevent-burst={Config.Audio.Pacing.PreventBurstCatchUp}, minimum-spacing={Config.Audio.Pacing.MinimumPacketSpacingMilliseconds}ms, precision-wait={Config.Audio.Pacing.PrecisionWaitMilliseconds}ms, 1ms-timer={Config.Audio.Pacing.RequestOneMillisecondTimerResolution}, waitable-timer={Config.Audio.Pacing.UseHighResolutionWaitableTimer}");

            _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _controlTransport = new SerialAtTransport(ports.ControlPort.PortName, Config.Modem.BaudRate);
            await _controlTransport.OpenAsync(_lifetimeCts.Token);
            _cellularCall = new CellularCallController(_controlTransport, new DialPolicy(Config.DialPolicy));
            _cellularCall.StateChanged += OnCellularStateChanged;
            var preflight = await _cellularCall.PreflightAsync(_lifetimeCts.Token);
            LogModemPreflight(preflight);
            if (!preflight.AtOk || !preflight.SimReady)
            {
                throw new InvalidOperationException("Modem preflight did not reach a safe ready state.");
            }

            if (!preflight.Registered)
            {
                preflight = await WaitForModemRegistrationAsync(preflight, Config.Recovery, _lifetimeCts.Token);
                LogModemPreflight(preflight);
            }
            _monitoringPreflight = preflight;

            var pcmFormat = new PcmFormat(
                Config.Audio.SampleRateHz,
                Config.Audio.Channels,
                Config.Audio.BitsPerSample,
                Config.Audio.FrameMilliseconds);
            var callHistoryPath = Path.IsPathRooted(Config.Logging.CallHistoryFile)
                ? Config.Logging.CallHistoryFile
                : Path.Combine(Path.GetDirectoryName(_configPath)!, Config.Logging.CallHistoryFile);
            var callHistory = new CallHistoryStore(
                callHistoryPath,
                Config.Logging.MaxCallHistoryRecords,
                Log);
            Log($"Structured call history: {callHistoryPath}; retention={Math.Clamp(Config.Logging.MaxCallHistoryRecords, 10, 10_000)} records.");
            var outgoingCoordinator = new CellularSipCallCoordinator(
                _cellularCall,
                ports.ServicePort.PortName,
                Config.Modem.BaudRate,
                pcmFormat,
                Config.Sip.BindAddress,
                Config.Sip.RtpPortStart,
                Config.Sip,
                Config.Audio.JitterBuffer,
                Config.Audio.ModemToRtpBuffer,
                Config.Audio.Pacing,
                Config.Audio.ModemToRtpGainDb,
                Log,
                callHistory);

            _sipHost = new SipGatewayHost(
                Config.Sip.BindAddress,
                Config.Sip.SipPort,
                Config.Sip.Username,
                Config.Sip.Password,
                Config.Sip.AllowedTailscaleIPs,
                Config.Sip.AuthenticatedPeerContactLifetimeSeconds,
                Path.IsPathRooted(Config.Sip.ContactCacheFile)
                    ? Config.Sip.ContactCacheFile
                    : Path.Combine(Path.GetDirectoryName(_configPath)!, Config.Sip.ContactCacheFile));
            _sipHost.LogMessage += Log;
            _sipHost.InviteReceived += (userAgent, serverUserAgent, request, contact) =>
                outgoingCoordinator.HandleInviteAsync(userAgent, serverUserAgent, request, contact, _lifetimeCts.Token);
            _sipHost.Start();

            _incomingCoordinator = new IncomingCellularSipCallCoordinator(
                _cellularCall,
                _sipHost,
                ports.ServicePort.PortName,
                Config.Modem.BaudRate,
                pcmFormat,
                Config.Sip,
                Config.Audio,
                Config.IncomingCalls,
                _lifetimeCts.Token,
                Log,
                callHistory);
            _incomingPollTask = PollIncomingCallsAsync(Config.IncomingCalls, _lifetimeCts.Token);
            var monitoringPath = Path.Combine(
                Path.GetDirectoryName(_configPath)!,
                "monitoring",
                "gateway-status.json");
            _monitoringTask = PublishMonitoringSnapshotsAsync(
                new MonitoringSnapshotWriter(monitoringPath),
                _lifetimeCts.Token);
            Log($"Monitoring snapshot: {monitoringPath}");

            return new GatewayStartResult(ports.ControlPort, ports.ServicePort, preflight);
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public async Task HangupAsync(CancellationToken cancellationToken = default)
    {
        if (_cellularCall is not null && _cellularCall.State is not CellularCallState.Idle)
        {
            await _cellularCall.HangupAsync(cancellationToken);
            Log("Gateway hangup completed.");
        }
    }

    private async Task PollIncomingCallsAsync(IncomingCallConfig config, CancellationToken cancellationToken)
    {
        if (!config.Enabled)
        {
            return;
        }

        var interval = TimeSpan.FromMilliseconds(Math.Max(500, config.PollIntervalMilliseconds));
        using var timer = new PeriodicTimer(interval);
        var consecutiveFailures = 0;
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                if (_cellularCall is not null)
                {
                    await _cellularCall.PollForIncomingCallAsync(cancellationToken);
                    consecutiveFailures = 0;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Incoming call poll failed: {ex.Message}");
                consecutiveFailures++;
                if (consecutiveFailures >= 3)
                {
                    var failure = new IOException("The modem call-state poll failed three consecutive times.", ex);
                    _completion.TrySetException(failure);
                    return;
                }
            }
        }
    }

    public async Task StopAsync()
    {
        _lifetimeCts?.Cancel();

        if (_monitoringTask is not null)
        {
            try
            {
                await _monitoringTask;
            }
            catch (OperationCanceledException)
            {
            }

            _monitoringTask = null;
        }

        if (_incomingCoordinator is not null)
        {
            await _incomingCoordinator.DisposeAsync();
            _incomingCoordinator = null;
        }

        if (_incomingPollTask is not null)
        {
            try
            {
                await _incomingPollTask;
            }
            catch (OperationCanceledException)
            {
            }

            _incomingPollTask = null;
        }

        if (_cellularCall is not null && _cellularCall.State is not CellularCallState.Idle)
        {
            try
            {
                await _cellularCall.HangupAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log($"Cellular cleanup warning: {ex.Message}");
            }
        }

        if (_sipHost is not null)
        {
            _sipHost.LogMessage -= Log;
            await _sipHost.DisposeAsync();
            _sipHost = null;
        }

        if (_controlTransport is not null)
        {
            await _controlTransport.DisposeAsync();
            _controlTransport = null;
        }

        if (_cellularCall is not null)
        {
            _cellularCall.StateChanged -= OnCellularStateChanged;
            _cellularCall = null;
        }
        _monitoringPreflight = null;
        _connectedAtUtc = null;
        _lifetimeCts?.Dispose();
        _lifetimeCts = null;
        _completion.TrySetResult();
        Log("Gateway stopped.");
    }

    private void Log(string message) => LogMessage?.Invoke(message);

    private async Task PublishMonitoringSnapshotsAsync(
        MonitoringSnapshotWriter writer,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                writer.Write(CreateMonitoringSnapshot());
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log($"Monitoring snapshot write failed: {exception.Message}");
            }

            if (!await timer.WaitForNextTickAsync(cancellationToken)) break;
        }
    }

    private GatewayMonitoringSnapshot CreateMonitoringSnapshot()
    {
        var callState = _cellularCall?.State ?? CellularCallState.Idle;
        var connected = callState is CellularCallState.Connected;
        var preflight = _monitoringPreflight;
        return new GatewayMonitoringSnapshot(
            DateTimeOffset.UtcNow,
            new MonitoringGateway(
                IsRunning ? callState is CellularCallState.Idle ? "Ready" : callState.ToString() : "Stopped",
                AppVersion.Version,
                IsRunning,
                _sipHost?.GetRegisteredContacts().Count ?? 0,
                null),
            new MonitoringModem(
                preflight?.AtOk == true,
                preflight?.SimReady == true,
                preflight?.Registered == true,
                MonitoringSnapshotWriter.ParseSignalQuality(preflight?.SignalQualityLine),
                null),
            new MonitoringCall(
                callState.ToString(),
                connected,
                connected ? _connectedAtUtc : null,
                null,
                null,
                null,
                null));
    }

    private void OnCellularStateChanged(object? sender, CellularCallState state)
    {
        if (state is CellularCallState.Connected)
        {
            _connectedAtUtc ??= DateTimeOffset.UtcNow;
        }
        else if (state is CellularCallState.Idle or CellularCallState.Failed)
        {
            _connectedAtUtc = null;
        }
    }

    private async Task<ModemPreflightResult> WaitForModemRegistrationAsync(
        ModemPreflightResult preflight,
        RecoveryConfig recovery,
        CancellationToken cancellationToken)
    {
        if (!recovery.AutoRecovery || _cellularCall is null)
        {
            throw new InvalidOperationException("The modem is not registered and automatic recovery is disabled.");
        }

        var retryInterval = recovery.ReconnectInterval > TimeSpan.Zero
            ? recovery.ReconnectInterval
            : TimeSpan.FromSeconds(2);
        var maximumDuration = recovery.MaxReconnectDuration > TimeSpan.Zero
            ? recovery.MaxReconnectDuration
            : TimeSpan.FromMinutes(2);
        var startedAt = DateTimeOffset.UtcNow;
        var nextProgressLog = TimeSpan.Zero;
        string? previousRegistrationLine = null;
        var attempt = 0;

        Log($"Modem network registration is not ready; keeping the Control Port open and retrying for up to {maximumDuration.TotalSeconds:F0}s.");
        while (DateTimeOffset.UtcNow - startedAt < maximumDuration)
        {
            await Task.Delay(retryInterval, cancellationToken);
            var status = await _cellularCall.GetRegistrationStatusAsync(cancellationToken);
            attempt++;
            var elapsed = DateTimeOffset.UtcNow - startedAt;

            if (status.Registered)
            {
                Log($"Modem network registration became ready after {elapsed.TotalSeconds:F1}s and {attempt} attempt(s).");
                return preflight with
                {
                    Registered = true,
                    RegistrationLine = status.RegistrationLine,
                    SignalQualityLine = status.SignalQualityLine
                };
            }

            if (!string.Equals(previousRegistrationLine, status.RegistrationLine, StringComparison.OrdinalIgnoreCase)
                || elapsed >= nextProgressLog)
            {
                Log($"Waiting for modem network registration: state={status.RegistrationLine ?? "unknown"}, signal={status.SignalQualityLine ?? "unknown"}, elapsed={elapsed.TotalSeconds:F0}s.");
                previousRegistrationLine = status.RegistrationLine;
                nextProgressLog = elapsed + TimeSpan.FromSeconds(15);
            }
        }

        throw new TimeoutException($"The modem did not register within {maximumDuration.TotalSeconds:F0} seconds while the Control Port remained open.");
    }

    private void LogModemPreflight(ModemPreflightResult preflight) =>
        Log($"Modem preflight: AT={preflight.AtOk}, SIM={preflight.SimReady}, registered={preflight.Registered}, DTMF={preflight.DtmfSupported}, signal={preflight.SignalQualityLine ?? "unknown"}, network={preflight.RegistrationLine ?? "unknown"}");

    public async ValueTask DisposeAsync() => await StopAsync();
}

public sealed record GatewayStartResult(
    ComPortInfo ControlPort,
    ComPortInfo ServicePort,
    ModemPreflightResult Preflight);
