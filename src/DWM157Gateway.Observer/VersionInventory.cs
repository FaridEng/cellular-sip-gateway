using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using DWM157Gateway.Monitoring;

namespace DWM157Gateway.Observer;

public sealed record DeviceVersion(string Id, string Name, string Os, string? InstalledVersion,
    string? RunningVersion, DateTimeOffset ObservedAtUtc, string Source, bool? Online = null,
    bool? UpdateAvailable = null, string Warning = "", string Track = "stable");

public sealed class VersionInventory(ObserverOptions options, HttpClient client)
{
    private readonly Dictionary<string, DeviceVersion> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private DateTimeOffset _nextLocalQuery, _nextApiQuery;
    public string? ApiError { get; private set; }

    public DeviceVersion[] Snapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            var devices = _devices.Values.Concat(options.TrackedAddresses.Where(address => !_devices.ContainsKey(address))
                .Select(address => new DeviceVersion(address, options.TrackedDeviceNames.GetValueOrDefault(address, address),
                    "unknown", null, null, DateTimeOffset.MinValue, "not-observed"))).ToArray();
            return devices.Select(device => device with
            { Warning = WarningFor(device, devices, now, options.MinimumWindowsVersion) }).OrderBy(d => d.Name).ToArray();
        }
    }

    public void Put(DeviceVersion device)
    {
        lock (_gate)
        {
            device = device with { Track = options.DeviceTracks.GetValueOrDefault(device.Id, device.Track) };
            if (device.Source == "tailscale-api-last-reported" && _devices.TryGetValue(device.Id, out var existing) &&
                existing.Source != "tailscale-api-last-reported" && DateTimeOffset.UtcNow - existing.ObservedAtUtc < TimeSpan.FromHours(24)) return;
            _devices[device.Id] = device;
        }
    }

    public async Task RefreshAsync(JsonElement? gatewaySummary, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (gatewaySummary is { } summary && summary.TryGetProperty("tailscaleVersion", out var version) && version.ValueKind == JsonValueKind.Object)
        {
            string? Get(string key) => version.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (version.TryGetProperty("observedAtUtc", out var at) && at.ValueKind == JsonValueKind.String && at.TryGetDateTimeOffset(out var observed) && Get("installedVersion") is { } installed)
                Put(new(new Uri(options.GatewayUrl).Host, "Gateway PC", "windows", installed, Get("runningVersion"), observed, "gateway-local-cli", true));
        }
        if (now >= _nextLocalQuery)
        {
            _nextLocalQuery = now.AddMinutes(5);
            var local = await TailscaleVersionMonitor.ReadAsync(options.TailscaleExecutable, cancellationToken);
            if (local.InstalledVersion is not null)
                Put(new(options.BindAddress, options.Name, "windows", local.InstalledVersion, local.RunningVersion,
                    local.ObservedAtUtc, "observer-local-cli", true));
        }
        if (now < _nextApiQuery) return;
        _nextApiQuery = now.AddMinutes(30);
        if (string.IsNullOrWhiteSpace(options.TailscaleApiCredentialPath))
        {
            ApiError = "Tailnet version inventory needs a read-only API credential or Point A reporters";
            return;
        }
        try
        {
            // Credential file: { accessToken } or { clientId, clientSecret }; never returned by the API.
            using var credentials = JsonDocument.Parse(await File.ReadAllTextAsync(options.TailscaleApiCredentialPath, cancellationToken));
            var root = credentials.RootElement;
            string accessToken;
            if (root.TryGetProperty("accessToken", out var token)) accessToken = token.GetString() ?? "";
            else
            {
                using var tokenResponse = await client.PostAsync("https://api.tailscale.com/api/v2/oauth/token",
                    new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "client_credentials",
                        ["client_id"] = root.GetProperty("clientId").GetString()!,
                        ["client_secret"] = root.GetProperty("clientSecret").GetString()!,
                        ["scope"] = "devices:core:read"
                    }), cancellationToken);
                tokenResponse.EnsureSuccessStatusCode();
                using var tokenDocument = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(cancellationToken));
                accessToken = tokenDocument.RootElement.GetProperty("access_token").GetString()!;
            }
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.tailscale.com/api/v2/tailnet/" + Uri.EscapeDataString(options.Tailnet) + "/devices?fields=all");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            foreach (var item in document.RootElement.GetProperty("devices").EnumerateArray())
            {
                var addresses = item.GetProperty("addresses").EnumerateArray().Select(a => a.GetString()!).ToArray();
                if (options.TrackedAddresses.Count > 0 && !addresses.Any(options.TrackedAddresses.Contains)) continue;
                var lastSeen = item.TryGetProperty("lastSeen", out var seen) && seen.TryGetDateTimeOffset(out var date) ? date : DateTimeOffset.MinValue;
                var identity = addresses.FirstOrDefault(options.TrackedAddresses.Contains) ?? addresses.FirstOrDefault() ?? item.GetProperty("id").GetString()!;
                Put(new(identity, options.TrackedDeviceNames.GetValueOrDefault(identity, item.GetProperty("name").GetString() ?? "Device"),
                    item.GetProperty("os").GetString() ?? "unknown", null,
                    item.TryGetProperty("clientVersion", out var clientVersion) ? clientVersion.GetString() : null,
                    lastSeen, "tailscale-api-last-reported", now - lastSeen < TimeSpan.FromMinutes(10),
                    item.TryGetProperty("updateAvailable", out var update) ? update.GetBoolean() : null));
            }
            ApiError = null;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or KeyNotFoundException or OperationCanceledException or InvalidOperationException or FormatException)
        { ApiError = "Tailnet API inventory could not be refreshed; retained entries keep their original timestamps"; }
    }

    public static Version? Normalize(string? value)
    {
        if (value is null) return null;
        var match = Regex.Match(value, @"^(\d+)\.(\d+)\.(\d+)(?:\D|$)");
        return match.Success && Version.TryParse(match.Groups[1].Value + "." + match.Groups[2].Value + "." + match.Groups[3].Value, out var version) ? version : null;
    }

    public static string WarningFor(DeviceVersion device, IEnumerable<DeviceVersion> all, DateTimeOffset now, string? minimum)
    {
        var warnings = new List<string>();
        if (device.Source == "not-observed") return "Version has not been observed";
        if (now - device.ObservedAtUtc > TimeSpan.FromHours(24) || device.ObservedAtUtc > now.AddMinutes(2))
            return "Last observed version is stale; current version is unknown";
        var installed = Normalize(device.InstalledVersion);
        var running = Normalize(device.RunningVersion);
        if (device.Online == false) warnings.Add("Device is offline; showing its last reported version");
        if (running is null) warnings.Add("Running daemon version is unknown");
        if (installed is not null && running is not null && installed != running)
            warnings.Add("Installed and running versions differ; review daemon restart");
        if (device.UpdateAvailable == true) warnings.Add("Tailscale reports an available update");
        if (!device.Os.Equals("windows", StringComparison.OrdinalIgnoreCase) || device.Track != "stable")
            return string.Join("; ", warnings);
        if (running is not null && Normalize(minimum) is { } min && running < min)
            warnings.Add("Below the configured minimum Windows version");
        if (running is not null && all.Any(other => other.Id != device.Id && other.Os.Equals("windows", StringComparison.OrdinalIgnoreCase) &&
            other.Track == "stable" && other.ObservedAtUtc <= now.AddMinutes(2) && now - other.ObservedAtUtc <= TimeSpan.FromHours(24) && Normalize(other.RunningVersion) is { } reference && reference > running))
            warnings.Add("Older than another observed Windows Stable peer; mismatch does not prove incompatibility");
        return string.Join("; ", warnings);
    }
}
