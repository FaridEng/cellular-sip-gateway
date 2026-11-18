using System.Text.Json;

namespace DWM157Gateway.Core.Diagnostics;

public sealed class MonitoringSnapshotWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _path;

    public MonitoringSnapshotWriter(string path)
    {
        _path = Path.GetFullPath(path);
    }

    public void Write(GatewayMonitoringSnapshot snapshot)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Monitoring snapshot path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, snapshot, JsonOptions);
                stream.Flush(true);
            }

            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static int? ParseSignalQuality(string? signalQualityLine)
    {
        if (string.IsNullOrWhiteSpace(signalQualityLine)) return null;
        var colon = signalQualityLine.IndexOf(':');
        var comma = signalQualityLine.IndexOf(',', colon + 1);
        if (colon < 0) return null;
        var value = comma > colon
            ? signalQualityLine[(colon + 1)..comma]
            : signalQualityLine[(colon + 1)..];
        return int.TryParse(value.Trim(), out var parsed) ? parsed : null;
    }
}

public sealed record GatewayMonitoringSnapshot(
    DateTimeOffset TimestampUtc,
    MonitoringGateway Gateway,
    MonitoringModem Modem,
    MonitoringCall Call);

public sealed record MonitoringGateway(
    string State,
    string Version,
    bool SipListening,
    int RegisteredPeers,
    string? LastError);

public sealed record MonitoringModem(
    bool Detected,
    bool SimReady,
    bool CellularRegistered,
    int? SignalQuality,
    string? OperatorName);

public sealed record MonitoringCall(
    string State,
    bool Connected,
    DateTimeOffset? ConnectedAtUtc,
    string? Codec,
    double? RtpLossPercent,
    double? JitterMilliseconds,
    double? RttMilliseconds);
