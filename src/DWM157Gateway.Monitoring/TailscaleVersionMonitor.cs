using System.Diagnostics;
using System.Text.Json;

namespace DWM157Gateway.Monitoring;

public sealed class TailscaleVersionMonitor(MonitoringOptions options) : BackgroundService
{
    private TailscaleVersionInfo _current = new(null, null, DateTimeOffset.MinValue, "Not measured");
    public TailscaleVersionInfo Current => Volatile.Read(ref _current);

    public static TailscaleVersionInfo Parse(string json, DateTimeOffset observedAt)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string? Get(string name) => root.TryGetProperty(name, out var value) ? value.GetString() : null;
        return new(Get("short") ?? Get("majorMinorPatch"), Get("daemonLong"), observedAt);
    }

    public static async Task<TailscaleVersionInfo> ReadAsync(string executable, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var process = new Process
        {
            StartInfo = new(executable, "version --json --daemon")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        try
        {
            if (!process.Start()) throw new IOException("Version probe could not start");
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            await stderr;
            if (process.ExitCode != 0 || output.Length > 32768) throw new IOException("Version probe failed");
            return Parse(output, DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception
                                          or OperationCanceledException or JsonException or InvalidOperationException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            return new(null, null, DateTimeOffset.UtcNow, "Tailscale version/daemon could not be queried");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var measured = await ReadAsync(options.TailscaleExecutable, stoppingToken);
            if (measured.InstalledVersion is null && _current.InstalledVersion is not null)
                measured = _current with { Error = measured.Error }; // retain original observation time
            Volatile.Write(ref _current, measured);
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
