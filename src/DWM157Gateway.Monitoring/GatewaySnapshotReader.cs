using System.Text.Json;

namespace DWM157Gateway.Monitoring;

public sealed class GatewaySnapshotReader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly MonitoringOptions _options;
    private readonly ILogger<GatewaySnapshotReader> _logger;

    public GatewaySnapshotReader(MonitoringOptions options, ILogger<GatewaySnapshotReader> logger)
    {
        _options = options;
        _logger = logger;
    }

    public GatewaySnapshot? TryRead()
    {
        try
        {
            if (!File.Exists(_options.GatewaySnapshotPath)) return null;
            using var stream = new FileStream(
                _options.GatewaySnapshotPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<GatewaySnapshot>(stream, SerializerOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning("Gateway status snapshot could not be read: {Reason}", exception.Message);
            return null;
        }
    }
}

public sealed record GatewaySnapshot(
    DateTimeOffset TimestampUtc,
    SnapshotGateway? Gateway,
    SnapshotModem? Modem,
    SnapshotCall? Call);

public sealed record SnapshotGateway(
    string? State,
    string? Version,
    bool? SipListening,
    int? RegisteredPeers,
    string? LastError);

public sealed record SnapshotModem(
    bool? Detected,
    bool? SimReady,
    bool? CellularRegistered,
    int? SignalQuality,
    string? OperatorName);

public sealed record SnapshotCall(
    string? State,
    bool? Connected,
    DateTimeOffset? ConnectedAtUtc,
    string? Codec,
    double? RtpLossPercent,
    double? JitterMilliseconds,
    double? RttMilliseconds);
