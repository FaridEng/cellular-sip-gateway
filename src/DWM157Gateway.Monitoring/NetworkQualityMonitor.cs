using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace DWM157Gateway.Monitoring;

public sealed partial class NetworkQualityMonitor : BackgroundService
{
    private readonly MonitoringOptions _options;
    private readonly object _gate = new();
    private NetworkQualitySnapshot _current = new(
        new(false, null, "Unknown", null, DateTimeOffset.MinValue, "Not measured"),
        new(false, null, DateTimeOffset.MinValue, "Not measured"),
        "Unknown", null, null,
        new(false, false, "Unknown"));

    public NetworkQualityMonitor(MonitoringOptions options) => _options = options;

    public NetworkQualitySnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = DateTimeOffset.UtcNow;
            var route = StatusCollector.FindDefaultInternetRoute(System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces());
            var peerTask = ProbePeerAsync(stoppingToken);
            var internetTask = ProbeInternetAsync(stoppingToken);
            var adbTask = ProbeAdbAsync(stoppingToken);
            await Task.WhenAll(peerTask, internetTask, adbTask);

            lock (_gate)
            {
                var changed = !string.Equals(_current.CurrentRoute, route, StringComparison.OrdinalIgnoreCase);
                _current = new NetworkQualitySnapshot(
                    peerTask.Result,
                    internetTask.Result,
                    route,
                    changed && _current.CurrentRoute != "Unknown" ? _current.CurrentRoute : _current.PreviousRoute,
                    changed ? started : _current.RouteChangedAtUtc,
                    adbTask.Result);
            }

            var elapsed = DateTimeOffset.UtcNow - started;
            var delay = TimeSpan.FromMilliseconds(Math.Max(2000, _options.QualityProbeIntervalMilliseconds)) - elapsed;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task<PeerProbeStatus> ProbePeerAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.TailscaleExecutable))
            return new(false, null, "Unknown", null, DateTimeOffset.UtcNow, "tailscale.exe not found");
        try
        {
            var output = await RunProcessAsync(_options.TailscaleExecutable,
                $"ping -c 1 --timeout 3s {_options.S25TailscaleAddress}", cancellationToken);
            return ParseTailscalePing(output, DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return new(false, null, "Offline", null, DateTimeOffset.UtcNow, exception.Message);
        }
    }

    private async Task<InternetProbeStatus> ProbeInternetAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Math.Clamp(_options.InternetProbeTimeoutMilliseconds, 250, 10000));
            using var client = new TcpClient();
            await client.ConnectAsync(_options.InternetProbeHost, _options.InternetProbePort, timeout.Token);
            return new(true, Stopwatch.GetElapsedTime(started).TotalMilliseconds, DateTimeOffset.UtcNow, null);
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            return new(false, null, DateTimeOffset.UtcNow, exception.Message);
        }
    }

    private async Task<A07UsbStatus> ProbeAdbAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.AdbExecutable)) return new(false, false, "ADB unavailable");
        try
        {
            var output = await RunProcessAsync(_options.AdbExecutable, $"-P {_options.AdbServerPort} devices", cancellationToken);
            var connected = output.Split('\n').Skip(1).Any(line => line.TrimEnd().EndsWith("\tdevice", StringComparison.Ordinal));
            if (!connected) return new(true, false, "Disconnected");
            var battery = await RunProcessAsync(_options.AdbExecutable, $"-P {_options.AdbServerPort} shell dumpsys battery", cancellationToken);
            var properties = await RunProcessAsync(_options.AdbExecutable,
                $"-P {_options.AdbServerPort} shell \"getprop ro.product.model; getprop ro.build.version.release; df -k /data | tail -1; head -1 /proc/meminfo\"",
                cancellationToken);
            return ParseA07Status(battery, properties);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return new(true, false, "ADB error");
        }
    }

    internal static A07UsbStatus ParseA07Status(string batteryOutput, string propertiesOutput)
    {
        int? level = ParseInteger(batteryOutput, @"level:\s*(\d+)");
        int? status = ParseInteger(batteryOutput, @"status:\s*(\d+)");
        int? temperatureTenths = ParseInteger(batteryOutput, @"temperature:\s*(\d+)");
        var lines = propertiesOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()).ToArray();
        var model = lines.ElementAtOrDefault(0);
        var android = lines.ElementAtOrDefault(1);
        long? storageFree = null;
        var diskParts = lines.ElementAtOrDefault(2)?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (diskParts?.Length >= 4 && long.TryParse(diskParts[3], out var freeKb)) storageFree = freeKb * 1024;
        long? totalMemory = null;
        var memoryMatch = Regex.Match(lines.ElementAtOrDefault(3) ?? "", @"MemTotal:\s*(\d+)\s*kB", RegexOptions.IgnoreCase);
        if (memoryMatch.Success && long.TryParse(memoryMatch.Groups[1].Value, out var memoryKb)) totalMemory = memoryKb * 1024;
        return new(true, true, "Online", level, status is 2 or 5,
            temperatureTenths / 10d, model, android, storageFree, totalMemory);
    }

    private static int? ParseInteger(string value, string pattern)
    {
        var match = Regex.Match(value, pattern, RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var result) ? result : null;
    }

    internal static PeerProbeStatus ParseTailscalePing(string output, DateTimeOffset timestamp)
    {
        var match = PongRegex().Match(output);
        if (!match.Success) return new(false, null, "Offline", null, timestamp, output.Trim());
        var via = match.Groups["via"].Value;
        var derp = DerpRegex().Match(via);
        return new(true, double.Parse(match.Groups["rtt"].Value, System.Globalization.CultureInfo.InvariantCulture),
            derp.Success ? "DERP" : "Direct", derp.Success ? derp.Groups["region"].Value : null, timestamp, null);
    }

    private static async Task<string> RunProcessAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Probe process did not start.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = (await outputTask) + (await errorTask);
        if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output)) throw new IOException($"Probe exited with {process.ExitCode}.");
        return output;
    }

    [GeneratedRegex(@"pong from .+ via (?<via>.+) in (?<rtt>[0-9.]+)ms", RegexOptions.IgnoreCase)]
    private static partial Regex PongRegex();

    [GeneratedRegex(@"DERP\((?<region>[^)]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex DerpRegex();
}
