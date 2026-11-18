namespace DWM157Gateway.Core.Configuration;

public sealed class GatewayConfig
{
    public string Version { get; set; } = "0.2.11";
    public SipConfig Sip { get; set; } = new();
    public ModemConfig Modem { get; set; } = new();
    public DialPolicyConfig DialPolicy { get; set; } = new();
    public IncomingCallConfig IncomingCalls { get; set; } = new();
    public AudioConfig Audio { get; set; } = new();
    public RecoveryConfig Recovery { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
}

public sealed class IncomingCallConfig
{
    public bool Enabled { get; set; } = true;
    public int PollIntervalMilliseconds { get; set; } = 1500;
    public int CallerIdWaitMilliseconds { get; set; } = 1000;
    public int SipContactWaitSeconds { get; set; } = 20;
    public int SipRingTimeoutSeconds { get; set; } = 35;
    public int CellularAnswerTimeoutSeconds { get; set; } = 15;
}

public sealed class SipConfig
{
    public string BindAddress { get; set; } = "100.64.0.12";
    public int SipPort { get; set; } = 5060;
    public int RtpPortStart { get; set; } = 40000;
    public int RtpPortEnd { get; set; } = 40020;
    public string Username { get; set; } = "dwm157";
    public string Password { get; set; } = "";
    public List<string> AllowedTailscaleIPs { get; set; } = [];
    public string PreferredCodec { get; set; } = "OPUS";
    public List<string> EnabledCodecs { get; set; } = ["OPUS", "PCMU", "PCMA"];
    public bool ForcePreferredCodec { get; set; }
    public int OpusBitrateBps { get; set; } = 16000;
    public bool OpusFec { get; set; } = true;
    public int OpusExpectedPacketLossPercent { get; set; } = 10;
    public bool OpusDtx { get; set; }
    public int OpusComplexity { get; set; } = 10;
    public bool OpusVbr { get; set; } = true;
    public int AuthenticatedPeerContactLifetimeSeconds { get; set; } = 86400;
    public string ContactCacheFile { get; set; } = "sip-contacts.json";
}

public sealed class ModemConfig
{
    public string ControlFriendlyName { get; set; } = "D-Link HSPADatacard Control Port";
    public string ServiceFriendlyName { get; set; } = "D-Link HSPADatacard Service Port";
    public string? ControlPortOverride { get; set; }
    public string? ServicePortOverride { get; set; }
    public int BaudRate { get; set; } = 115200;
    public bool AutoCloseConnectionManager { get; set; }
}

public sealed class DialPolicyConfig
{
    public bool AllowUnlistedNumbers { get; set; }
    public string? TestNumber { get; set; }
    public List<string> AllowedDialNumbers { get; set; } = [];
    public List<string> BlockedNumbers { get; set; } = ["911", "112", "999", "000"];
    public DialPlanConfig DialPlan { get; set; } = new();
}

public sealed class DialPlanConfig
{
    public string? RemovePrefix { get; set; }
    public string? AddPrefix { get; set; }
}

public sealed class AudioConfig
{
    public int SampleRateHz { get; set; } = 8000;
    public int Channels { get; set; } = 1;
    public int BitsPerSample { get; set; } = 16;
    public int FrameMilliseconds { get; set; } = 20;
    public double ModemToRtpGainDb { get; set; } = 6;
    public bool EnableAudioCapture { get; set; }
    public JitterBufferConfig ModemToRtpBuffer { get; set; } = new()
    {
        MinBufferMilliseconds = 200,
        TargetBufferMilliseconds = 300,
        MaxBufferMilliseconds = 600,
        StableFramesBeforeShrink = 250
    };
    public JitterBufferConfig JitterBuffer { get; set; } = new();
    public AudioPacingConfig Pacing { get; set; } = new();
}

public sealed class AudioPacingConfig
{
    public bool PreventBurstCatchUp { get; set; } = true;
    public int MinimumPacketSpacingMilliseconds { get; set; } = 15;
    public int PrecisionWaitMilliseconds { get; set; } = 2;
    public bool RequestOneMillisecondTimerResolution { get; set; } = true;
    public bool UseHighResolutionWaitableTimer { get; set; }
    public int GapWarningMilliseconds { get; set; } = 25;
    public int BurstWarningMilliseconds { get; set; } = 15;
    public int LegacyMaximumCatchUpFrames { get; set; } = 10;
}

public sealed class JitterBufferConfig
{
    public bool Enabled { get; set; } = true;
    public int MinBufferMilliseconds { get; set; } = 80;
    public int TargetBufferMilliseconds { get; set; } = 120;
    public int MaxBufferMilliseconds { get; set; } = 300;
    public int StableFramesBeforeShrink { get; set; } = 100;
}

public sealed class RecoveryConfig
{
    public bool AutoRecovery { get; set; } = true;
    public TimeSpan NetworkDisruptionTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxReconnectDuration { get; set; } = TimeSpan.FromMinutes(2);
}

public sealed class LoggingConfig
{
    public string Level { get; set; } = "Information";
    public string CallHistoryFile { get; set; } = "call-history.json";
    public int MaxCallHistoryRecords { get; set; } = 500;
}
