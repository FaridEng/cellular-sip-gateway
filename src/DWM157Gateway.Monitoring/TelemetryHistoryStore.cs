using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace DWM157Gateway.Monitoring;

public sealed class TelemetryHistoryStore : BackgroundService
{
    private readonly MonitoringOptions _options;
    private readonly StatusCollector _collector;
    private readonly ILogger<TelemetryHistoryStore> _logger;
    private readonly SemaphoreSlim _databaseLock = new(1, 1);

    public TelemetryHistoryStore(
        MonitoringOptions options,
        StatusCollector collector,
        ILogger<TelemetryHistoryStore> logger)
    {
        _options = options;
        _collector = collector;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await InitializeAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.HistorySampleIntervalSeconds));
        do
        {
            try
            {
                await AppendAsync(_collector.Collect(), stoppingToken);
            }
            catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Monitoring history sample could not be stored.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<HistoryResponse> ReadAsync(string? requestedRange, int requestedMaxPoints, CancellationToken cancellationToken)
    {
        var (rangeName, range) = ParseRange(requestedRange);
        var to = DateTimeOffset.UtcNow;
        var from = to - range;
        var maxPoints = Math.Clamp(requestedMaxPoints, 10, 2000);
        await InitializeAsync(cancellationToken);
        await _databaseLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT timestamp_utc, overall, readiness_score, gateway_running, signal_quality,
                       s25_rtt_ms, s25_reachable, internet_rtt_ms, internet_reachable,
                       tailscale_path, active_route, disk_free_bytes, call_loss_percent,
                       call_jitter_ms, call_rtt_ms, a07_battery_percent, a07_temperature_c
                FROM telemetry
                WHERE timestamp_utc >= $from
                ORDER BY timestamp_utc ASC;
                """;
            command.Parameters.AddWithValue("$from", from.ToString("O", CultureInfo.InvariantCulture));
            var points = new List<HistoryPoint>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) points.Add(ReadPoint(reader));
            return new(from, to, rangeName, Downsample(points, maxPoints));
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public static string ToCsv(HistoryResponse history)
    {
        var csv = new StringBuilder("timestampUtc,overall,readinessScore,gatewayRunning,signalQuality,s25RttMs,s25Reachable,internetRttMs,internetReachable,tailscalePath,activeRoute,diskFreeBytes,callLossPercent,callJitterMs,callRttMs,a07BatteryPercent,a07TemperatureC\r\n");
        foreach (var point in history.Points)
        {
            csv.Append(point.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                .Append(Escape(point.Overall)).Append(',').Append(point.ReadinessScore).Append(',')
                .Append(point.GatewayRunning).Append(',').Append(Number(point.SignalQuality)).Append(',')
                .Append(Number(point.S25RttMilliseconds)).Append(',').Append(point.S25Reachable).Append(',')
                .Append(Number(point.InternetRttMilliseconds)).Append(',').Append(point.InternetReachable).Append(',')
                .Append(Escape(point.TailscalePath)).Append(',').Append(Escape(point.ActiveRoute)).Append(',')
                .Append(Number(point.DiskFreeBytes)).Append(',').Append(Number(point.CallLossPercent)).Append(',')
                .Append(Number(point.CallJitterMilliseconds)).Append(',').Append(Number(point.CallRttMilliseconds)).Append(',')
                .Append(Number(point.A07BatteryPercent)).Append(',').Append(Number(point.A07TemperatureC)).Append("\r\n");
        }
        return csv.ToString();
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_options.HistoryDatabasePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        await _databaseLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS telemetry (
                    timestamp_utc TEXT PRIMARY KEY, overall TEXT NOT NULL, readiness_score INTEGER NOT NULL,
                    gateway_running INTEGER NOT NULL, signal_quality INTEGER NULL, s25_rtt_ms REAL NULL,
                    s25_reachable INTEGER NOT NULL, internet_rtt_ms REAL NULL, internet_reachable INTEGER NOT NULL,
                    tailscale_path TEXT NOT NULL, active_route TEXT NOT NULL, disk_free_bytes INTEGER NULL,
                    call_loss_percent REAL NULL, call_jitter_ms REAL NULL, call_rtt_ms REAL NULL,
                    a07_battery_percent INTEGER NULL, a07_temperature_c REAL NULL);
                CREATE INDEX IF NOT EXISTS ix_telemetry_timestamp ON telemetry(timestamp_utc);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    private async Task AppendAsync(MonitoringSummary summary, CancellationToken cancellationToken)
    {
        await _databaseLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR REPLACE INTO telemetry VALUES
                ($timestamp, $overall, $readiness, $gateway, $signal, $s25rtt, $s25ok, $internetrtt,
                 $internetok, $path, $route, $disk, $loss, $jitter, $callrtt, $battery, $temperature);
                DELETE FROM telemetry WHERE timestamp_utc < $retention;
                """;
            Add(command, "$timestamp", summary.TimestampUtc.ToString("O", CultureInfo.InvariantCulture));
            Add(command, "$overall", summary.Overall);
            Add(command, "$readiness", summary.Readiness.Score);
            Add(command, "$gateway", summary.Gateway.Running ? 1 : 0);
            Add(command, "$signal", summary.Modem.SignalQuality);
            Add(command, "$s25rtt", summary.Network.Quality.S25ToGatewayPc.RttMilliseconds);
            Add(command, "$s25ok", summary.Network.Quality.S25ToGatewayPc.Success ? 1 : 0);
            Add(command, "$internetrtt", summary.Network.Quality.Internet.RttMilliseconds);
            Add(command, "$internetok", summary.Network.Quality.Internet.Success ? 1 : 0);
            Add(command, "$path", summary.Network.Quality.S25ToGatewayPc.Path);
            Add(command, "$route", summary.Network.ActiveRoute);
            Add(command, "$disk", summary.System.SystemDriveFreeBytes);
            Add(command, "$loss", summary.Call.RtpLossPercent);
            Add(command, "$jitter", summary.Call.JitterMilliseconds);
            Add(command, "$callrtt", summary.Call.RttMilliseconds);
            Add(command, "$battery", summary.Network.Quality.A07.BatteryPercent);
            Add(command, "$temperature", summary.Network.Quality.A07.TemperatureC);
            Add(command, "$retention", (summary.TimestampUtc - TimeSpan.FromDays(_options.HistoryRetentionDays)).ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    private SqliteConnection OpenConnection() => new($"Data Source={_options.HistoryDatabasePath};Mode=ReadWriteCreate;Cache=Shared");
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static string Number(object? value) => value is null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    private static string Escape(string value) => value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\"";

    private static (string Name, TimeSpan Value) ParseRange(string? value) => value?.ToLowerInvariant() switch
    {
        "1h" => ("1h", TimeSpan.FromHours(1)),
        "7d" => ("7d", TimeSpan.FromDays(7)),
        "30d" => ("30d", TimeSpan.FromDays(30)),
        _ => ("24h", TimeSpan.FromDays(1))
    };

    private static IReadOnlyList<HistoryPoint> Downsample(List<HistoryPoint> points, int maxPoints)
    {
        if (points.Count <= maxPoints) return points;
        var output = new List<HistoryPoint>(maxPoints);
        for (var index = 0; index < maxPoints; index++)
        {
            var source = (int)Math.Round(index * (points.Count - 1d) / (maxPoints - 1d));
            output.Add(points[source]);
        }
        return output;
    }

    private static HistoryPoint ReadPoint(SqliteDataReader reader) => new(
        DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture), reader.GetString(1), reader.GetInt32(2),
        reader.GetBoolean(3), reader.IsDBNull(4) ? null : reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetDouble(5),
        reader.GetBoolean(6), reader.IsDBNull(7) ? null : reader.GetDouble(7), reader.GetBoolean(8), reader.GetString(9),
        reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetInt64(11), reader.IsDBNull(12) ? null : reader.GetDouble(12),
        reader.IsDBNull(13) ? null : reader.GetDouble(13), reader.IsDBNull(14) ? null : reader.GetDouble(14),
        reader.IsDBNull(15) ? null : reader.GetInt32(15), reader.IsDBNull(16) ? null : reader.GetDouble(16));
}
