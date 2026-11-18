using System.Net;
using System.Text.Json;

namespace DWM157Gateway.Observer;

public sealed class GatewayProbe(HttpClient client, ObserverOptions options)
{
    public JsonElement? LastSummary { get; private set; }
    public DateTimeOffset? LastSuccessUtc { get; private set; }

    public async Task<ProbeOutcome> CheckAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, options.GatewayUrl.TrimEnd('/') + "/api/v1/summary");
        request.Headers.Add("X-Monitor-Token", options.GatewayApiToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var now = DateTimeOffset.UtcNow;
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new("authentication_failed", "Monitoring API rejected its token (HTTP 401)", now);
            if (response.StatusCode == HttpStatusCode.Forbidden) return new("access_denied", "Monitoring API rejected this observer peer (HTTP 403)", now);
            if (!response.IsSuccessStatusCode) return new("monitor_error", $"Monitoring API returned HTTP {(int)response.StatusCode}", now);
            if (response.Content.Headers.ContentLength > 1048576) return new("monitor_error", "Monitoring response exceeded its size limit", now);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var limited = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (limited.Length + read > 1048576) return new("monitor_error", "Monitoring response exceeded its size limit", now);
                limited.Write(buffer, 0, read);
            }
            using var document = JsonDocument.Parse(limited.ToArray());
            var outcome = Classify(document.RootElement, now, options.SnapshotStaleSeconds);
            LastSummary = document.RootElement.Clone();
            LastSuccessUtc = now;
            return outcome;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException or JsonException)
        {
            return new("unreachable", "Gateway PC monitoring API is unreachable from this observer; PC, network, Tailscale or monitoring host may be unavailable", now);
        }
    }

    public static ProbeOutcome Classify(JsonElement root, DateTimeOffset now, int staleSeconds)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("gateway", out var gateway) || gateway.ValueKind != JsonValueKind.Object ||
            !gateway.TryGetProperty("running", out var running) || running.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return new("monitor_error", "Monitoring response has no valid Gateway process status", now);
        if (!running.GetBoolean()) return new("gateway_stopped", "Gateway PC responds, but its Gateway process is stopped", now);
        bool Flag(string key) => gateway.TryGetProperty(key, out var flag) && flag.ValueKind == JsonValueKind.True;
        if (!Flag("snapshotAvailable") || Flag("snapshotStale") ||
            !gateway.TryGetProperty("snapshotTimestampUtc", out var timestamp) ||
            timestamp.ValueKind != JsonValueKind.String || !timestamp.TryGetDateTimeOffset(out var observed) || now - observed > TimeSpan.FromSeconds(staleSeconds) || observed - now > TimeSpan.FromMinutes(2))
            return new("telemetry_stale", "Gateway PC responds, but Gateway telemetry is unavailable or stale", now);
        if (!Flag("sipListening")) return new("gateway_unready", "Gateway is running but its SIP listener is not ready", now);
        return new("healthy", "Gateway responds with fresh telemetry and a ready SIP listener", now);
    }
}
