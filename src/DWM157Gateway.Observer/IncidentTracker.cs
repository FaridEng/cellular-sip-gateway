namespace DWM157Gateway.Observer;

public sealed record ProbeOutcome(string Code, string Message, DateTimeOffset ObservedAtUtc)
{
    public bool Healthy => Code == "healthy";
}
public sealed record AlertEvent(string Id, long Sequence, string Kind, string IncidentId,
    string Code, string Title, string Message, DateTimeOffset TimestampUtc, string SourceId = "");
public sealed class IncidentState
{
    public string? IncidentId { get; set; }
    public DateTimeOffset? FailureSinceUtc { get; set; }
    public int Failures { get; set; }
    public int Successes { get; set; }
    public long Sequence { get; set; }
    public AlertEvent? PendingAlert { get; set; }
    public string LastCode { get; set; } = "unknown";
}

public sealed class IncidentTracker(ObserverOptions options, IncidentState state)
{
    public IncidentState State => state;

    public AlertEvent? Observe(ProbeOutcome outcome)
    {
        state.LastCode = outcome.Code;
        if (outcome.Healthy)
        {
            state.Failures = 0;
            state.Successes++;
            if (state.IncidentId is null) { state.FailureSinceUtc = null; return null; }
            if (state.Successes < options.RecoveryThreshold) return null;
            var incident = state.IncidentId;
            state.IncidentId = null;
            state.FailureSinceUtc = null;
            return Create("recovered", incident, outcome, "Gateway connection restored");
        }
        state.Successes = 0;
        state.Failures++;
        state.FailureSinceUtc ??= outcome.ObservedAtUtc;
        if (state.IncidentId is not null || state.Failures < options.FailureThreshold ||
            outcome.ObservedAtUtc - state.FailureSinceUtc < TimeSpan.FromSeconds(options.FailureDurationSeconds)) return null;
        if (options.MaintenanceUntilUtc is { } until && outcome.ObservedAtUtc < until) return null;
        state.IncidentId = Guid.NewGuid().ToString("N");
        return Create("outage", state.IncidentId, outcome, outcome.Code switch
        {
            "gateway_stopped" => "Gateway service is stopped",
            "telemetry_stale" => "Gateway telemetry is stale",
            "authentication_failed" or "access_denied" => "Monitoring access needs attention",
            _ => "Gateway access interrupted"
        });
    }

    private AlertEvent Create(string kind, string incident, ProbeOutcome outcome, string title)
    {
        var result = new AlertEvent(Guid.NewGuid().ToString("N"), ++state.Sequence, kind, incident,
            outcome.Code, title, outcome.Message, outcome.ObservedAtUtc);
        state.PendingAlert = result; // recovery replaces an unsent outage; never deliver obsolete alarms later
        return result;
    }
}
