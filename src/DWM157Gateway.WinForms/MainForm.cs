using DWM157Gateway.Core;

namespace DWM157Gateway.WinForms;

public sealed class MainForm : Form
{
    private readonly TextBox _log = new() { Multiline = true, Dock = DockStyle.Fill, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Label _status = new() { AutoSize = true, Text = "Stopped" };
    private readonly Button _hangup = new() { Text = "Hangup", AutoSize = true, Enabled = false };
    private GatewayRuntime? _runtime;

    public MainForm(bool autoStart = false)
    {
        Text = $"DWM157Gateway {AppVersion.Version}";
        Width = 900;
        Height = 600;

        var start = new Button { Text = "Start Gateway", AutoSize = true };
        var stop = new Button { Text = "Stop Gateway", AutoSize = true, Enabled = false };

        start.Click += async (_, _) => await StartGatewayAsync(start, stop);
        stop.Click += async (_, _) => await StopGatewayAsync(start, stop);
        _hangup.Click += async (_, _) =>
        {
            if (_runtime is not null)
            {
                await _runtime.HangupAsync();
            }
        };

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(8) };
        top.Controls.AddRange([start, stop, _hangup, _status]);
        Controls.Add(_log);
        Controls.Add(top);

        if (autoStart)
        {
            Shown += async (_, _) => await StartGatewayAsync(start, stop);
        }
    }

    private async Task StartGatewayAsync(Button start, Button stop)
    {
        try
        {
            _runtime = new GatewayRuntime(Path.Combine(AppContext.BaseDirectory, "config.json"));
            _runtime.LogMessage += Append;
            await _runtime.StartAsync();

            start.Enabled = false;
            stop.Enabled = true;
            _hangup.Enabled = true;
            _status.Text = "SIP listening";
        }
        catch (Exception ex)
        {
            _status.Text = "Start failed";
            Append(ex.Message);
            await StopGatewayAsync(start, stop);
        }
    }

    private async Task StopGatewayAsync(Button start, Button stop)
    {
        if (_runtime is not null)
        {
            _runtime.LogMessage -= Append;
            await _runtime.DisposeAsync();
            _runtime = null;
        }

        start.Enabled = true;
        stop.Enabled = false;
        _hangup.Enabled = false;
        _status.Text = "Stopped";
        Append("Gateway stopped.");
    }

    private void Append(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Append(message));
            return;
        }

        var line = $"[{DateTimeOffset.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        _log.AppendText(line);
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "gateway.log"), line);
        }
        catch
        {
            // The UI log remains available if the deployment directory is read-only.
        }
    }
}
