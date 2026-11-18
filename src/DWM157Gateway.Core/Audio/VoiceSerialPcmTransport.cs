using System.IO.Ports;
using System.Diagnostics;
using System.Threading.Channels;

namespace DWM157Gateway.Core.Audio;

public sealed class VoiceSerialPcmTransport : IVoicePcmTransport, IAsyncDisposable
{
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly PcmFormat _format;
    private readonly Channel<byte[]> _rxFrames;
    private SerialPort? _serialPort;
    private long _receivedFrames;
    private long _writtenFrames;
    private long _firstReceiveTimestamp;
    private long _lastReceiveTimestamp;
    private long _maximumReceiveGapTicks;
    private long _receiveGapsOverTwoFrames;
    private long _receiveBurstsUnderQuarterFrame;

    public VoiceSerialPcmTransport(string portName, int baudRate, PcmFormat format)
    {
        _portName = portName;
        _baudRate = baudRate;
        _format = format;
        _rxFrames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
    }

    public ChannelReader<byte[]> RxFrames => _rxFrames.Reader;

    public Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _serialPort = new SerialPort(_portName, _baudRate)
        {
            DataBits = 8,
            Parity = Parity.None,
            StopBits = StopBits.One,
            Handshake = Handshake.None,
            ReadTimeout = 100,
            WriteTimeout = 1000,
            ReadBufferSize = 131072,
            WriteBufferSize = 131072,
            DtrEnable = true,
            RtsEnable = true
        };
        _serialPort.Open();
        _ = Task.Run(() => ReadLoopAsync(cancellationToken), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task WriteFrameAsync(byte[] s16leFrame, CancellationToken cancellationToken)
    {
        if (s16leFrame.Length != _format.FrameBytes)
        {
            throw new ArgumentException($"Expected {_format.FrameBytes} byte PCM frame.", nameof(s16leFrame));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_serialPort?.IsOpen != true)
        {
            throw new InvalidOperationException("The DWM-157 voice port is not open.");
        }

        await _serialPort.BaseStream.WriteAsync(s16leFrame, cancellationToken);
        Interlocked.Increment(ref _writtenFrames);
    }

    public VoiceSerialPcmSnapshot GetSnapshot()
    {
        var received = Interlocked.Read(ref _receivedFrames);
        var first = Interlocked.Read(ref _firstReceiveTimestamp);
        var last = Interlocked.Read(ref _lastReceiveTimestamp);
        var elapsedSeconds = first > 0 && last > first
            ? (last - first) / (double)Stopwatch.Frequency
            : 0;

        return new VoiceSerialPcmSnapshot(
            received,
            Interlocked.Read(ref _writtenFrames),
            elapsedSeconds > 0 ? (received - 1) / elapsedSeconds : 0,
            Interlocked.Read(ref _maximumReceiveGapTicks) * 1000.0 / Stopwatch.Frequency,
            Interlocked.Read(ref _receiveGapsOverTwoFrames),
            Interlocked.Read(ref _receiveBurstsUnderQuarterFrame));
    }

    public ValueTask DisposeAsync()
    {
        _rxFrames.Writer.TryComplete();
        _serialPort?.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[_format.FrameBytes];
        try
        {
            while (!cancellationToken.IsCancellationRequested && _serialPort?.IsOpen == true)
            {
                var offset = 0;
                while (offset < buffer.Length)
                {
                    var read = await _serialPort.BaseStream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    offset += read;
                }

                if (offset == buffer.Length)
                {
                    RecordReceivedFrame();
                    await _rxFrames.Writer.WriteAsync(buffer.ToArray(), cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _rxFrames.Writer.TryComplete();
        }
    }

    private void RecordReceivedFrame()
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Exchange(ref _lastReceiveTimestamp, now);
        if (previous == 0)
        {
            Interlocked.CompareExchange(ref _firstReceiveTimestamp, now, 0);
        }
        else
        {
            var gap = now - previous;
            var observedMaximum = Interlocked.Read(ref _maximumReceiveGapTicks);
            while (gap > observedMaximum)
            {
                var original = Interlocked.CompareExchange(ref _maximumReceiveGapTicks, gap, observedMaximum);
                if (original == observedMaximum)
                {
                    break;
                }

                observedMaximum = original;
            }

            var frameTicks = Stopwatch.Frequency * _format.FrameMilliseconds / 1000;
            if (gap > frameTicks * 2)
            {
                Interlocked.Increment(ref _receiveGapsOverTwoFrames);
            }
            else if (gap < frameTicks / 4)
            {
                Interlocked.Increment(ref _receiveBurstsUnderQuarterFrame);
            }
        }

        Interlocked.Increment(ref _receivedFrames);
    }
}

public sealed record VoiceSerialPcmSnapshot(
    long ReceivedFrames,
    long WrittenFrames,
    double AverageReceivedFramesPerSecond,
    double MaximumReceiveGapMilliseconds,
    long ReceiveGapsOverTwoFrames,
    long ReceiveBurstsUnderQuarterFrame);
