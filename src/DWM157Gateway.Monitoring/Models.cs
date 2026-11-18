namespace DWM157Gateway.Monitoring;

public sealed record MonitoringSummary(
    string ApiVersion,
    DateTimeOffset TimestampUtc,
    string Overall,
    GatewayStatus Gateway,
    ModemStatus Modem,
    NetworkStatus Network,
    SystemStatus System,
    CallStatus Call,
    ReadinessStatus Readiness,
    TailscaleVersionInfo? TailscaleVersion = null);

public sealed record TailscaleVersionInfo(
    string? InstalledVersion, string? RunningVersion, DateTimeOffset ObservedAtUtc, string? Error = null);

public sealed record ReadinessStatus(
    int Score,
    string State,
    IReadOnlyList<string> Reasons);

public sealed record GatewayStatus(
    bool Running,
    string State,
    string? Version,
    bool? SipListening,
    int? RegisteredPeers,
    bool SnapshotAvailable,
    bool SnapshotStale,
    DateTimeOffset? SnapshotTimestampUtc,
    string? LastError);

public sealed record ModemStatus(
    bool? Detected,
    bool? SimReady,
    bool? CellularRegistered,
    int? SignalQuality,
    string? OperatorName);

public sealed record NetworkStatus(
    bool NetworkAvailable,
    string ActiveRoute,
    bool TailscaleAvailable,
    string? TailscaleAddress,
    IReadOnlyList<NetworkInterfaceStatus> Interfaces,
    InternetTestResult? LastLightTest,
    NetworkQualitySnapshot Quality);

public sealed record NetworkQualitySnapshot(
    PeerProbeStatus S25ToGatewayPc,
    InternetProbeStatus Internet,
    string CurrentRoute,
    string? PreviousRoute,
    DateTimeOffset? RouteChangedAtUtc,
    A07UsbStatus A07);

public sealed record PeerProbeStatus(
    bool Success,
    double? RttMilliseconds,
    string Path,
    string? DerpRegion,
    DateTimeOffset TimestampUtc,
    string? Error);

public sealed record InternetProbeStatus(
    bool Success,
    double? RttMilliseconds,
    DateTimeOffset TimestampUtc,
    string? Error);

public sealed record A07UsbStatus(
    bool AdbAvailable,
    bool Connected,
    string State,
    int? BatteryPercent = null,
    bool? Charging = null,
    double? TemperatureC = null,
    string? Model = null,
    string? AndroidVersion = null,
    long? StorageFreeBytes = null,
    long? TotalMemoryBytes = null);

public sealed record NetworkInterfaceStatus(
    string Name,
    string Description,
    string Type,
    string Status,
    IReadOnlyList<string> Addresses);

public sealed record SystemStatus(
    string MachineName,
    string OsDescription,
    long UptimeSeconds,
    long? SystemDriveFreeBytes,
    int ProcessorCount);

public sealed record CallStatus(
    string State,
    bool Connected,
    long DurationSeconds,
    string? Codec,
    double? RtpLossPercent,
    double? JitterMilliseconds,
    double? RttMilliseconds);

public sealed record InternetTestResult(
    bool Success,
    double? RttMilliseconds,
    DateTimeOffset TimestampUtc,
    string? Error);

public sealed record CallHistoryResponse(
    DateTimeOffset TimestampUtc,
    bool SourceAvailable,
    IReadOnlyList<MonitoringCallRecord> Calls);

public sealed record MonitoringCallRecord(
    string Direction,
    string? RemoteNumber,
    string? SipPeer,
    string Status,
    DateTimeOffset RequestTimestampUtc,
    DateTimeOffset? ConnectTimestampUtc,
    DateTimeOffset? EndTimestampUtc,
    long? DurationSeconds,
    string? EndReason,
    string? Codec,
    CallMediaStatus? Media);

public sealed record CallMediaStatus(
    long? ReceivedPackets,
    long? ExpectedPackets,
    long? LostPackets,
    long? OutOfOrderPackets,
    double? LossPercent,
    double? JitterMilliseconds,
    double? RemoteLossPercent,
    double? RemoteJitterMilliseconds,
    double? RttMilliseconds,
    long? ModemToRtpDroppedFrames,
    long? RtpToModemDroppedFrames);

public sealed record HistoryResponse(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string Range,
    IReadOnlyList<HistoryPoint> Points);

public sealed record HistoryPoint(
    DateTimeOffset TimestampUtc,
    string Overall,
    int ReadinessScore,
    bool GatewayRunning,
    int? SignalQuality,
    double? S25RttMilliseconds,
    bool S25Reachable,
    double? InternetRttMilliseconds,
    bool InternetReachable,
    string TailscalePath,
    string ActiveRoute,
    long? DiskFreeBytes,
    double? CallLossPercent,
    double? CallJitterMilliseconds,
    double? CallRttMilliseconds,
    int? A07BatteryPercent,
    double? A07TemperatureC);
