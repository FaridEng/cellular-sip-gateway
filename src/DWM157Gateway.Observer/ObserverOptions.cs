using DWM157Gateway.Monitoring;

namespace DWM157Gateway.Observer;

public sealed class ObserverOptions
{
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5090;
    public string ApiToken { get; set; } = "";
    public List<string> AllowedRemoteAddresses { get; set; } = [];
    public string GatewayUrl { get; set; } = "http://100.64.0.12:5088";
    public string GatewayApiToken { get; set; } = "";
    public string Name { get; set; } = "NUC observer";
    public string DataDirectory { get; set; } = @"C:\DWM157Observer\data";
    public string TailscaleExecutable { get; set; } = @"C:\Program Files\Tailscale\tailscale.exe";
    public int ProbeIntervalSeconds { get; set; } = 15;
    public int FailureThreshold { get; set; } = 3;
    public int FailureDurationSeconds { get; set; } = 45;
    public int RecoveryThreshold { get; set; } = 2;
    public int SnapshotStaleSeconds { get; set; } = 20;
    public string? MinimumWindowsVersion { get; set; }
    public string FirebaseProjectId { get; set; } = "";
    public string FirebaseServiceAccountPath { get; set; } = "";
    public string TailscaleApiCredentialPath { get; set; } = "";
    public string Tailnet { get; set; } = "-";
    public string WindowsReporterToken { get; set; } = "";
    public List<string> ReporterAddresses { get; set; } = [];
    public List<string> TrackedAddresses { get; set; } = [];
    public Dictionary<string, string> TrackedDeviceNames { get; set; } = [];
    public Dictionary<string, string> DeviceTracks { get; set; } = [];
    public DateTimeOffset? MaintenanceUntilUtc { get; set; }

    public MonitoringOptions AccessOptions => new()
    { BindAddress = BindAddress, Port = Port, ApiToken = ApiToken, AllowedRemoteAddresses = AllowedRemoteAddresses };

    public void Validate()
    {
        AccessOptions.GetValidatedBindAddress();
        if (!System.Net.IPAddress.IsLoopback(System.Net.IPAddress.Parse(BindAddress)) &&
            (ApiToken.Length < 32 || ApiToken.Contains("REPLACE", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A strong independent observer token is required");
        if (!Uri.TryCreate(GatewayUrl, UriKind.Absolute, out var uri) ||
            !PrivateEndpoint.IsAllowed(uri) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("GatewayUrl must be a private/Tailscale HTTP or HTTPS URL");
        if (ProbeIntervalSeconds is < 5 or > 300 || FailureThreshold is < 1 or > 20 ||
            FailureDurationSeconds is < 0 or > 1800 || RecoveryThreshold is < 1 or > 10)
            throw new InvalidOperationException("Observer thresholds are outside the supported range");
        if (string.IsNullOrWhiteSpace(GatewayApiToken)) throw new InvalidOperationException("Gateway API token is required");
    }
}

public static class PrivateEndpoint
{
    public static bool IsAllowed(Uri uri)
    {
        if (uri.Scheme != "http" && uri.Scheme != "https") return false;
        if (!System.Net.IPAddress.TryParse(uri.Host, out var ip)) return uri.Scheme == "https";
        var bytes = ip.GetAddressBytes();
        return System.Net.IPAddress.IsLoopback(ip) || uri.Scheme == "https" ||
            (bytes.Length == 4 && (bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 ||
            bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 100 && bytes[1] is >= 64 and <= 127));
    }
}
