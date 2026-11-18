using DWM157Gateway.Core.Dialing;
using System.Text.RegularExpressions;

namespace DWM157Gateway.Core.Modem;

public sealed class CellularCallController
{
    private static readonly Regex ClipRegex = new("^\\+CLIP:\\s*\\\"(?<number>[^\\\"]*)\\\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ClccRegex = new("^\\+CLCC:\\s*\\d+,(?<direction>[01]),(?<status>[0-5])(?:,[^,]*){2}(?:,\\\"(?<number>[^\\\"]*)\\\")?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CregRegex = new("^\\+CREG:\\s*(?:\\d+\\s*,\\s*)?(?<status>\\d+)(?:\\s*,|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly IAtTransport _transport;
    private readonly DialPolicy _dialPolicy;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CellularCallState State { get; private set; } = CellularCallState.Idle;
    public IncomingCellularCall? IncomingCall { get; private set; }
    public event EventHandler<CellularCallState>? StateChanged;
    public event EventHandler<IncomingCellularCall>? IncomingCallReceived;
    public event EventHandler<IncomingCellularCall>? IncomingCallUpdated;

    public CellularCallController(IAtTransport transport, DialPolicy dialPolicy)
    {
        _transport = transport;
        _dialPolicy = dialPolicy;
        _transport.LineReceived += OnLineReceived;
    }

    public async Task<ModemPreflightResult> PreflightAsync(CancellationToken cancellationToken)
    {
        var at = await _transport.SendCommandAsync("AT", TimeSpan.FromSeconds(2), cancellationToken);
        var cpin = await _transport.SendCommandAsync("AT+CPIN?", TimeSpan.FromSeconds(2), cancellationToken);
        var creg = await _transport.SendCommandAsync("AT+CREG?", TimeSpan.FromSeconds(2), cancellationToken);
        var csq = await _transport.SendCommandAsync("AT+CSQ", TimeSpan.FromSeconds(2), cancellationToken);
        await _transport.SendCommandAsync("AT+CLIP=1", TimeSpan.FromSeconds(2), cancellationToken);
        await _transport.SendCommandAsync("AT+CLCC", TimeSpan.FromSeconds(2), cancellationToken);
        var dtmf = await _transport.SendCommandAsync("AT+VTS=?", TimeSpan.FromSeconds(2), cancellationToken);
        var registrationLine = GetRegistrationLine(creg);

        return new ModemPreflightResult(
            HasOk(at),
            cpin.Any(l => l.Contains("READY", StringComparison.OrdinalIgnoreCase)),
            registrationLine is not null && IsRegistered(registrationLine),
            csq.FirstOrDefault(l => l.StartsWith("+CSQ", StringComparison.OrdinalIgnoreCase)),
            HasOk(dtmf),
            registrationLine);
    }

    public async Task<ModemRegistrationStatus> GetRegistrationStatusAsync(CancellationToken cancellationToken)
    {
        var creg = await _transport.SendCommandAsync("AT+CREG?", TimeSpan.FromSeconds(2), cancellationToken);
        var csq = await _transport.SendCommandAsync("AT+CSQ", TimeSpan.FromSeconds(2), cancellationToken);
        var registrationLine = GetRegistrationLine(creg);

        return new ModemRegistrationStatus(
            registrationLine is not null && IsRegistered(registrationLine),
            registrationLine,
            csq.FirstOrDefault(l => l.StartsWith("+CSQ", StringComparison.OrdinalIgnoreCase)));
    }

    public async Task<DialDecision> DialAsync(string requestedNumber, CancellationToken cancellationToken)
    {
        var decision = _dialPolicy.Validate(requestedNumber);
        if (!decision.IsAllowed)
        {
            return decision;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (State is not CellularCallState.Idle)
            {
                return DialDecision.Blocked("A cellular call is already active or pending.");
            }

            SetState(CellularCallState.Dialing);
            await _transport.SendCommandAsync("AT+SSAM=5", TimeSpan.FromSeconds(2), cancellationToken);
            await _transport.SendCommandAsync($"ATD{decision.DialString};", TimeSpan.FromSeconds(5), cancellationToken);
            return decision;
        }
        catch
        {
            SetState(CellularCallState.Failed);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PollForIncomingCallAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (State is not CellularCallState.Idle)
            {
                return;
            }

            var response = await _transport.SendCommandAsync("AT+CLCC", TimeSpan.FromSeconds(2), cancellationToken);
            foreach (var line in response)
            {
                ProcessLine(line);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AnswerIncomingAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (State is not CellularCallState.IncomingRinging)
            {
                throw new InvalidOperationException("There is no incoming cellular call to answer.");
            }

            SetState(CellularCallState.Answering);
            var response = await _transport.SendCommandAsync("ATA", TimeSpan.FromSeconds(5), cancellationToken);
            if (!HasOk(response))
            {
                SetState(CellularCallState.Failed);
                throw new InvalidOperationException("The modem rejected the incoming cellular call answer command.");
            }
        }
        catch
        {
            if (State is CellularCallState.Answering)
            {
                SetState(CellularCallState.Failed);
            }

            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> WaitForIncomingCallerIdAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var currentCallerId = IncomingCall?.CallerId;
        if (!string.IsNullOrWhiteSpace(currentCallerId) || timeout <= TimeSpan.Zero)
        {
            return currentCallerId;
        }

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, IncomingCellularCall call)
        {
            if (!string.IsNullOrWhiteSpace(call.CallerId))
            {
                completion.TrySetResult(call.CallerId);
            }
        }

        IncomingCallUpdated += Handler;
        try
        {
            currentCallerId = IncomingCall?.CallerId;
            if (!string.IsNullOrWhiteSpace(currentCallerId))
            {
                return currentCallerId;
            }

            try
            {
                return await completion.Task.WaitAsync(timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                return IncomingCall?.CallerId;
            }
        }
        finally
        {
            IncomingCallUpdated -= Handler;
        }
    }

    public async Task HangupAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (State is CellularCallState.Idle)
            {
                return;
            }

            SetState(CellularCallState.HangingUp);
            await _transport.SendCommandAsync("ATH", TimeSpan.FromSeconds(3), cancellationToken);
            SetState(CellularCallState.Idle);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SendDtmfAsync(byte eventId, CancellationToken cancellationToken)
    {
        if (!TryMapDtmfEvent(eventId, out var symbol))
        {
            throw new ArgumentOutOfRangeException(nameof(eventId), "The RTP event is not a standard DTMF tone.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (State is not CellularCallState.Connected)
            {
                throw new InvalidOperationException("DTMF can only be sent while the cellular call is connected.");
            }

            var response = await _transport.SendCommandAsync(
                $"AT+VTS={symbol}",
                TimeSpan.FromSeconds(2),
                cancellationToken);
            if (!HasOk(response))
            {
                throw new InvalidOperationException("The modem rejected the DTMF command.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool HasOk(IEnumerable<string> lines) => lines.Any(l => l.Equals("OK", StringComparison.OrdinalIgnoreCase));

    private static bool IsRegistered(string line)
    {
        var match = CregRegex.Match(line);
        return match.Success && match.Groups["status"].Value is "1" or "5";
    }

    private static string? GetRegistrationLine(IEnumerable<string> lines) =>
        lines.FirstOrDefault(line => line.StartsWith("+CREG", StringComparison.OrdinalIgnoreCase));

    internal static bool TryMapDtmfEvent(byte eventId, out char symbol)
    {
        const string symbols = "0123456789*#ABCD";
        if (eventId < symbols.Length)
        {
            symbol = symbols[eventId];
            return true;
        }

        symbol = default;
        return false;
    }

    private void OnLineReceived(object? sender, string line)
    {
        ProcessLine(line);
    }

    private void ProcessLine(string line)
    {
        var clip = ClipRegex.Match(line);
        if (clip.Success)
        {
            var number = NormalizeCallerId(clip.Groups["number"].Value);
            IncomingCall = new IncomingCellularCall(number, IncomingCall?.DetectedAt ?? DateTimeOffset.UtcNow);
            IncomingCallUpdated?.Invoke(this, IncomingCall);
            return;
        }

        var clcc = ClccRegex.Match(line);
        if (clcc.Success)
        {
            var direction = clcc.Groups["direction"].Value;
            var status = clcc.Groups["status"].Value;
            if (direction == "1" && status is "4" or "5")
            {
                var number = NormalizeCallerId(clcc.Groups["number"].Value);
                SignalIncomingCall(number);
                return;
            }

            if (status == "0")
            {
                SetState(CellularCallState.Connected);
                return;
            }
        }

        if (line.Equals("RING", StringComparison.OrdinalIgnoreCase))
        {
            SignalIncomingCall(IncomingCall?.CallerId);
        }
        else
        if (line.Contains("^CONN: 1,0", StringComparison.OrdinalIgnoreCase))
        {
            SetState(CellularCallState.Connected);
        }
        else if (line.Contains("NO CARRIER", StringComparison.OrdinalIgnoreCase) ||
                 line.StartsWith("^CEND", StringComparison.OrdinalIgnoreCase))
        {
            IncomingCall = null;
            SetState(CellularCallState.Idle);
        }
        else if (line.StartsWith("^CONF", StringComparison.OrdinalIgnoreCase))
        {
            SetState(CellularCallState.Alerting);
        }
    }

    private void SignalIncomingCall(string? callerId)
    {
        var wasRinging = State is CellularCallState.IncomingRinging;
        IncomingCall = new IncomingCellularCall(callerId, IncomingCall?.DetectedAt ?? DateTimeOffset.UtcNow);
        SetState(CellularCallState.IncomingRinging);
        IncomingCallUpdated?.Invoke(this, IncomingCall);
        if (!wasRinging)
        {
            IncomingCallReceived?.Invoke(this, IncomingCall);
        }
    }

    private static string? NormalizeCallerId(string? callerId)
    {
        var trimmed = callerId?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private void SetState(CellularCallState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, state);
    }
}

public enum CellularCallState
{
    Idle,
    IncomingRinging,
    Answering,
    Dialing,
    Alerting,
    Connected,
    HangingUp,
    Failed
}

public sealed record IncomingCellularCall(string? CallerId, DateTimeOffset DetectedAt);

public sealed record ModemPreflightResult(
    bool AtOk,
    bool SimReady,
    bool Registered,
    string? SignalQualityLine,
    bool DtmfSupported,
    string? RegistrationLine = null);

public sealed record ModemRegistrationStatus(
    bool Registered,
    string? RegistrationLine,
    string? SignalQualityLine);
