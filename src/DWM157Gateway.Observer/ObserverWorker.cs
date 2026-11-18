using System.Text.Json;

namespace DWM157Gateway.Observer;

public sealed record PushStatus(bool Configured, int RegisteredDevices, DateTimeOffset? LastSuccessUtc, string? Error, bool Pending);
public sealed record ObserverSnapshot(string ApiVersion, string Observer, DateTimeOffset TimestampUtc,
    ProbeOutcome Probe, string? IncidentId, DateTimeOffset? FailureSinceUtc, DateTimeOffset? LastGatewayResponseUtc,
    JsonElement? LastGatewaySummary, DeviceVersion[] Versions, string? VersionInventoryError,
    PushStatus Push, AlertEvent[] RecentEvents, DateTimeOffset? MaintenanceUntilUtc, string? StorageError);

public sealed class SavedObserverState
{
    public string ObserverId { get; set; } = Guid.NewGuid().ToString("N");
    public IncidentState Incident { get; set; } = new();
    public List<string> Registrations { get; set; } = [];
    public List<DeviceVersion> Versions { get; set; } = [];
    public List<AlertEvent> Events { get; set; } = [];
}

public sealed class ObserverWorker : BackgroundService
{
    private readonly ObserverOptions _options;
    private readonly GatewayProbe _probe;
    private readonly VersionInventory _versions;
    private readonly FcmPushSender _push;
    private readonly IncidentTracker _tracker;
    private readonly SavedObserverState _saved;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private readonly string _statePath;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private DateTimeOffset? _lastPushSuccess;
    private string? _pushError, _storageError;
    private ObserverSnapshot _snapshot;
    public ObserverSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public ObserverWorker(ObserverOptions options, GatewayProbe probe, VersionInventory versions, FcmPushSender push)
    {
        _options = options; _probe = probe; _versions = versions; _push = push;
        Directory.CreateDirectory(options.DataDirectory);
        _statePath = Path.Combine(options.DataDirectory, "observer-state.json");
        try { _saved = File.Exists(_statePath) ? JsonSerializer.Deserialize<SavedObserverState>(File.ReadAllText(_statePath), Json) ?? new() : new(); }
        catch (Exception exception) when (exception is IOException or JsonException)
        { _saved = new(); _storageError = "Observer state could not be loaded"; }
        foreach (var item in _saved.Versions) versions.Put(item);
        _tracker = new(options, _saved.Incident);
        _snapshot = BuildSnapshot(new("unknown", "Waiting for first probe", DateTimeOffset.UtcNow));
    }

    public async Task RegisterAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 || token.Any(char.IsWhiteSpace)) throw new ArgumentException("Invalid push registration");
        lock (_gate)
        {
            // One S25 installation is authorized by the peer allowlist. Replace a rotated FCM token.
            _saved.Registrations.Clear();
            _saved.Registrations.Add(token);
        }
        await PersistAsync(cancellationToken);
    }

    public async Task<bool> SendTestAsync(CancellationToken cancellationToken)
    {
        string[] registrations; AlertEvent alert;
        lock (_gate)
        {
            registrations = _saved.Registrations.ToArray();
            if (!_push.Configured || registrations.Length == 0) return false;
            alert = new(Guid.NewGuid().ToString("N"), ++_saved.Incident.Sequence, "test", "test", "observer_test",
                "Gateway observer test", "Test Push from " + _options.Name + "; this does not simulate or prove a Gateway outage", DateTimeOffset.UtcNow, _saved.ObserverId);
        }
        await PersistAsync(cancellationToken);
        var accepted = true;
        foreach (var registration in registrations) accepted &= await _push.SendAsync(alert, registration, cancellationToken);
        return accepted;
    }

    private ObserverSnapshot BuildSnapshot(ProbeOutcome result)
    {
        lock (_gate)
            return new("1", _options.Name, DateTimeOffset.UtcNow, result, _saved.Incident.IncidentId,
                _saved.Incident.FailureSinceUtc, _probe.LastSuccessUtc, _probe.LastSummary,
                _versions.Snapshot(DateTimeOffset.UtcNow), _versions.ApiError,
                new(_push.Configured, _saved.Registrations.Count, _lastPushSuccess,
                    _pushError ?? (!_push.Configured ? "Firebase push is not configured" :
                        _saved.Registrations.Count == 0 ? "No Android installation has registered for push" : null), _saved.Incident.PendingAlert is not null),
                _saved.Events.TakeLast(30).Reverse().ToArray(), _options.MaintenanceUntilUtc, _storageError);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var outcome = await _probe.CheckAsync(stoppingToken);
            await _versions.RefreshAsync(_probe.LastSummary, stoppingToken);
            lock (_gate)
            {
                if (_tracker.Observe(outcome) is { } alert)
                {
                    alert = alert with { SourceId = _saved.ObserverId };
                    _saved.Incident.PendingAlert = alert;
                    _saved.Events.Add(alert);
                    if (_saved.Events.Count > 100) _saved.Events.RemoveAt(0);
                }
            }
            // Persist the event before delivery. Retrying after a crash retains the same event ID/sequence.
            await PersistAsync(stoppingToken);
            AlertEvent? pending; string[] registrations;
            lock (_gate) { pending = _saved.Incident.PendingAlert; registrations = _saved.Registrations.ToArray(); }
            if (pending is not null)
            {
                if (!_push.Configured) _pushError = "Firebase push is not configured";
                else if (registrations.Length == 0) _pushError = "No Android installation has registered for push";
                else if (DateTimeOffset.UtcNow - pending.TimestampUtc > TimeSpan.FromMinutes(30))
                {
                    lock (_gate) _saved.Incident.PendingAlert = null;
                    _pushError = "An expired alert was retained in history and was not delivered late";
                }
                else
                {
                    var success = true;
                    foreach (var registration in registrations)
                        success &= await _push.SendAsync(pending, registration, stoppingToken);
                    _pushError = success ? null : "Push delivery could not be confirmed; will retry the same event";
                    if (success)
                    {
                        _lastPushSuccess = DateTimeOffset.UtcNow;
                        lock (_gate) _saved.Incident.PendingAlert = null;
                    }
                }
            }
            await PersistAsync(stoppingToken);
            Volatile.Write(ref _snapshot, BuildSnapshot(outcome));
            await Task.Delay(TimeSpan.FromSeconds(_options.ProbeIntervalSeconds), stoppingToken);
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        await _fileGate.WaitAsync(cancellationToken);
        try
        {
            byte[] contents;
            lock (_gate)
            {
                _saved.Versions = _versions.Snapshot(DateTimeOffset.UtcNow).ToList();
                contents = JsonSerializer.SerializeToUtf8Bytes(_saved, Json);
            }
            var temp = _statePath + ".tmp";
            await File.WriteAllBytesAsync(temp, contents, cancellationToken);
            File.Move(temp, _statePath, true);
            _storageError = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { _storageError = "Observer persistence failed; notification deduplication across restart is not guaranteed"; }
        finally { _fileGate.Release(); }
    }
}
