using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DWM157Gateway.Monitoring;

public sealed class StatusCollector
{
    private readonly GatewaySnapshotReader _snapshotReader;
    private readonly MonitoringOptions _options;
    private readonly NetworkQualityMonitor _qualityMonitor;
    private readonly TailscaleVersionMonitor _versionMonitor;
    private readonly object _testLock = new();
    private InternetTestResult? _lastLightTest;

    public StatusCollector(GatewaySnapshotReader snapshotReader, MonitoringOptions options, NetworkQualityMonitor qualityMonitor,
        TailscaleVersionMonitor versionMonitor)
    {
        _snapshotReader = snapshotReader;
        _options = options;
        _qualityMonitor = qualityMonitor;
        _versionMonitor = versionMonitor;
    }

    public MonitoringSummary Collect()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = _snapshotReader.TryRead();
        var processRunning = IsGatewayProcessRunning();
        var stale = snapshot is not null &&
                    now - snapshot.TimestampUtc > TimeSpan.FromSeconds(_options.SnapshotStaleSeconds);

        var gateway = new GatewayStatus(
            processRunning,
            snapshot?.Gateway?.State ?? (processRunning ? "Unknown" : "Stopped"),
            snapshot?.Gateway?.Version,
            snapshot?.Gateway?.SipListening,
            snapshot?.Gateway?.RegisteredPeers,
            snapshot is not null,
            stale,
            snapshot?.TimestampUtc,
            snapshot?.Gateway?.LastError);

        var modem = new ModemStatus(
            snapshot?.Modem?.Detected,
            snapshot?.Modem?.SimReady,
            snapshot?.Modem?.CellularRegistered,
            snapshot?.Modem?.SignalQuality,
            snapshot?.Modem?.OperatorName);

        var callSnapshot = snapshot?.Call;
        var connected = callSnapshot?.Connected == true;
        var duration = connected && callSnapshot?.ConnectedAtUtc is not null
            ? Math.Max(0, (long)(now - callSnapshot.ConnectedAtUtc.Value).TotalSeconds)
            : 0;
        var call = new CallStatus(
            callSnapshot?.State ?? "Unknown",
            connected,
            duration,
            callSnapshot?.Codec,
            callSnapshot?.RtpLossPercent,
            callSnapshot?.JitterMilliseconds,
            callSnapshot?.RttMilliseconds);

        var network = CollectNetworkStatus();
        var system = CollectSystemStatus();
        var overall = GetOverall(gateway, modem, network);
        var readiness = ReadinessEvaluator.Evaluate(gateway, modem, network, system);
        return new MonitoringSummary("1", now, overall, gateway, modem, network, system, call, readiness, _versionMonitor.Current);
    }

    public async Task<InternetTestResult> RunLightTestAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Math.Clamp(_options.InternetProbeTimeoutMilliseconds, 250, 10000));
            await client.ConnectAsync(_options.InternetProbeHost, _options.InternetProbePort, timeout.Token);
            var result = new InternetTestResult(
                true,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                DateTimeOffset.UtcNow,
                null);
            lock (_testLock) _lastLightTest = result;
            return result;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            var result = new InternetTestResult(false, null, DateTimeOffset.UtcNow, exception.Message);
            lock (_testLock) _lastLightTest = result;
            return result;
        }
    }

    private NetworkStatus CollectNetworkStatus()
    {
        var systemInterfaces = NetworkInterface.GetAllNetworkInterfaces();
        var interfaces = systemInterfaces
            .Where(item => item.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
            .Select(item => new NetworkInterfaceStatus(
                item.Name,
                item.Description,
                item.NetworkInterfaceType.ToString(),
                item.OperationalStatus.ToString(),
                item.GetIPProperties().UnicastAddresses
                    .Select(address => address.Address)
                    .Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    .Select(address => address.ToString())
                    .ToArray()))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var tailscale = interfaces.FirstOrDefault(item =>
            item.Name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) ||
            item.Description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase));
        var activeRoute = FindDefaultInternetRoute(systemInterfaces);
        InternetTestResult? lastTest;
        lock (_testLock) lastTest = _lastLightTest;

        return new NetworkStatus(
            NetworkInterface.GetIsNetworkAvailable(),
            activeRoute,
            tailscale?.Status == OperationalStatus.Up.ToString(),
            tailscale?.Addresses.FirstOrDefault(IsTailscaleAddress),
            interfaces,
            lastTest,
            _qualityMonitor.Current);
    }

    private static SystemStatus CollectSystemStatus()
    {
        long? freeBytes = null;
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (!string.IsNullOrWhiteSpace(root)) freeBytes = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (IOException)
        {
            // A missing disk metric must not fail monitoring.
        }

        return new SystemStatus(
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            Math.Max(0, Environment.TickCount64 / 1000),
            freeBytes,
            Environment.ProcessorCount);
    }

    private static bool IsGatewayProcessRunning()
    {
        var processes = Process.GetProcesses();
        try
        {
            return processes.Any(process =>
                process.ProcessName.Equals("DWM157Gateway.Service", StringComparison.OrdinalIgnoreCase) ||
                process.ProcessName.Equals("DWM157Gateway.WinForms", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    internal static string FindDefaultInternetRoute(IEnumerable<NetworkInterface> interfaces)
    {
        var destination = BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes(), 0);
        if (GetBestInterface(destination, out var interfaceIndex) != 0) return "Unknown";
        var selected = interfaces.FirstOrDefault(item =>
        {
            try
            {
                var properties = item.GetIPProperties().GetIPv4Properties();
                return properties is not null && (uint)properties.Index == interfaceIndex;
            }
            catch (NetworkInformationException)
            {
                return false;
            }
        });
        if (selected is null) return "Unknown";
        if (selected.Name.Contains("USB", StringComparison.OrdinalIgnoreCase) ||
            selected.Description.Contains("Remote NDIS", StringComparison.OrdinalIgnoreCase)) return "UsbTethering";
        if (selected.NetworkInterfaceType == NetworkInterfaceType.Ethernet) return "Lan";
        if (selected.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) return "Wifi";
        if (selected.Name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase)) return "Tailscale";
        return selected.Name;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetBestInterface(uint destinationAddress, out uint bestInterfaceIndex);

    private static bool IsTailscaleAddress(string value)
    {
        return IPAddress.TryParse(value, out var address) &&
               address.AddressFamily == AddressFamily.InterNetwork &&
               (address.GetAddressBytes()[0] == 100 && (address.GetAddressBytes()[1] & 0xC0) == 0x40);
    }

    private static string GetOverall(GatewayStatus gateway, ModemStatus modem, NetworkStatus network)
    {
        if (!gateway.Running || !network.NetworkAvailable) return "critical";
        if (!gateway.SnapshotAvailable || gateway.SnapshotStale ||
            modem.Detected != true || modem.SimReady != true || modem.CellularRegistered != true ||
            gateway.SipListening != true)
            return "warning";
        return "healthy";
    }
}
