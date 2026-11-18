using System.Net;

namespace DWM157Gateway.Monitoring;

public sealed class MonitoringOptions
{
    public string BindAddress { get; set; } = IPAddress.Loopback.ToString();
    public int Port { get; set; } = 5088;
    public string ApiToken { get; set; } = string.Empty;
    public List<string> AllowedRemoteAddresses { get; set; } = [];
    public string GatewaySnapshotPath { get; set; } = @"C:\DWM157Gateway\data\monitoring\gateway-status.json";
    public string CallHistoryPath { get; set; } = @"C:\DWM157Gateway\data\call-history.json";
    public string HistoryDatabasePath { get; set; } = @"C:\DWM157Gateway\data\monitoring\history.db";
    public int HistorySampleIntervalSeconds { get; set; } = 60;
    public int HistoryRetentionDays { get; set; } = 30;
    public int SnapshotStaleSeconds { get; set; } = 15;
    public string InternetProbeHost { get; set; } = "1.1.1.1";
    public int InternetProbePort { get; set; } = 443;
    public int InternetProbeTimeoutMilliseconds { get; set; } = 2500;
    public int QualityProbeIntervalMilliseconds { get; set; } = 5000;
    public string S25TailscaleAddress { get; set; } = "100.64.0.14";
    public string TailscaleExecutable { get; set; } = @"C:\Program Files\Tailscale\tailscale.exe";
    public string AdbExecutable { get; set; } = @"C:\Tools\platform-tools\adb.exe";
    public int AdbServerPort { get; set; } = 5038;

    public IPAddress GetValidatedBindAddress()
    {
        if (!IPAddress.TryParse(BindAddress, out var address))
        {
            throw new InvalidOperationException("Monitoring.BindAddress must be an explicit IP address.");
        }

        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            throw new InvalidOperationException("Monitoring refuses wildcard bind addresses.");
        }

        if (!IPAddress.IsLoopback(address) && string.IsNullOrWhiteSpace(ApiToken))
        {
            throw new InvalidOperationException("A monitoring token is required for a non-loopback bind.");
        }

        if (!IPAddress.IsLoopback(address) && AllowedRemoteAddresses.Count == 0)
        {
            throw new InvalidOperationException("At least one monitoring peer is required for a non-loopback bind.");
        }

        foreach (var remoteAddress in AllowedRemoteAddresses)
        {
            if (!IPAddress.TryParse(remoteAddress, out _))
            {
                throw new InvalidOperationException($"Monitoring peer '{remoteAddress}' is not a valid IP address.");
            }
        }

        if (Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("Monitoring.Port is outside the valid TCP port range.");
        }

        if (HistorySampleIntervalSeconds is < 10 or > 3600)
            throw new InvalidOperationException("Monitoring.HistorySampleIntervalSeconds must be between 10 and 3600.");
        if (HistoryRetentionDays is < 1 or > 365)
            throw new InvalidOperationException("Monitoring.HistoryRetentionDays must be between 1 and 365.");

        return address;
    }

    public bool IsRemoteAddressAllowed(IPAddress? remoteAddress)
    {
        if (IPAddress.IsLoopback(IPAddress.Parse(BindAddress)) && AllowedRemoteAddresses.Count == 0)
        {
            return true;
        }

        if (remoteAddress is null)
        {
            return false;
        }

        var normalizedRemote = remoteAddress.IsIPv4MappedToIPv6 ? remoteAddress.MapToIPv4() : remoteAddress;
        return AllowedRemoteAddresses.Any(candidate =>
        {
            var parsed = IPAddress.Parse(candidate);
            var normalizedCandidate = parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed;
            return normalizedCandidate.Equals(normalizedRemote);
        });
    }
}
