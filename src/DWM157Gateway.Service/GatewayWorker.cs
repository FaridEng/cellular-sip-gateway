using DWM157Gateway.Core;

namespace DWM157Gateway.Service;

public sealed class GatewayWorker : BackgroundService
{
    private readonly ServiceOptions _options;
    private readonly ILogger<GatewayWorker> _logger;
    private readonly object _logLock = new();

    public GatewayWorker(ServiceOptions options, ILogger<GatewayWorker> logger)
    {
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_options.LogPath)!);
        Append($"DWM157Gateway Service starting; config={_options.ConfigPath}");

        while (!stoppingToken.IsCancellationRequested)
        {
            var runtime = new GatewayRuntime(_options.ConfigPath);
            runtime.LogMessage += Append;
            try
            {
                await runtime.StartAsync(stoppingToken);
                Append("Gateway runtime started.");
                await runtime.Completion.WaitAsync(stoppingToken);
                if (!stoppingToken.IsCancellationRequested)
                {
                    Append("Gateway runtime stopped unexpectedly; restarting in 5 seconds.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gateway runtime failed.");
                Append($"Gateway runtime failed: {ex.Message}; restarting in 5 seconds.");
            }
            finally
            {
                await runtime.DisposeAsync();
                runtime.LogMessage -= Append;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Append("DWM157Gateway Service stopped.");
    }

    private void Append(string message)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {message}{Environment.NewLine}";
        lock (_logLock)
        {
            File.AppendAllText(_options.LogPath, line);
        }
    }
}
