using System.IO.Ports;
using System.Text;

namespace DWM157Gateway.Core.Modem;

public sealed class SerialAtTransport : IAtTransport
{
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly StringBuilder _lineBuffer = new();
    private SerialPort? _serialPort;

    public event EventHandler<string>? LineReceived;

    public SerialAtTransport(string portName, int baudRate)
    {
        _portName = portName;
        _baudRate = baudRate;
    }

    public Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _serialPort = new SerialPort(_portName, _baudRate)
        {
            NewLine = "\r\n",
            Encoding = Encoding.ASCII,
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = 250,
            WriteTimeout = 1000
        };
        _serialPort.DataReceived += OnDataReceived;
        _serialPort.Open();
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<string>> SendCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var responses = new List<string>();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout);

        void Handler(object? _, string line)
        {
            lock (responses)
            {
                responses.Add(line);
            }
        }

        LineReceived += Handler;
        try
        {
            _serialPort?.Write(command + "\r");
            while (!linkedCts.IsCancellationRequested)
            {
                await Task.Delay(25, cancellationToken);
                lock (responses)
                {
                    if (responses.Any(IsTerminalLine))
                    {
                        return responses.ToArray();
                    }
                }
            }
        }
        finally
        {
            LineReceived -= Handler;
        }

        throw new TimeoutException($"Timed out waiting for modem response to '{command}'.");
    }

    public ValueTask DisposeAsync()
    {
        if (_serialPort is not null)
        {
            _serialPort.DataReceived -= OnDataReceived;
            _serialPort.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private static bool IsTerminalLine(string line)
    {
        return line.Equals("OK", StringComparison.OrdinalIgnoreCase)
            || line.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
            || line.Equals("NO CARRIER", StringComparison.OrdinalIgnoreCase);
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_serialPort is null)
        {
            return;
        }

        var text = _serialPort.ReadExisting();
        foreach (var ch in text)
        {
            if (ch is '\r' or '\n')
            {
                FlushLine();
            }
            else
            {
                _lineBuffer.Append(ch);
            }
        }
    }

    private void FlushLine()
    {
        if (_lineBuffer.Length == 0)
        {
            return;
        }

        var line = _lineBuffer.ToString().Trim();
        _lineBuffer.Clear();
        if (line.Length > 0)
        {
            LineReceived?.Invoke(this, line);
        }
    }
}
