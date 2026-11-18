using System.Threading.Channels;
using DWM157Gateway.Core.Modem;

namespace DWM157Gateway.Core.Sip;

internal sealed class SipDtmfBridge : IAsyncDisposable
{
    private const int QueueCapacity = 32;
    private readonly CellularCallController _cellularCall;
    private readonly Action<string>? _logMessage;
    private readonly Channel<DtmfRequest> _queue;
    private readonly CancellationTokenSource _cancellation;
    private readonly Task _worker;
    private long _received;
    private long _forwarded;
    private long _failed;
    private long _dropped;

    public SipDtmfBridge(
        CellularCallController cellularCall,
        CancellationToken cancellationToken,
        Action<string>? logMessage = null)
    {
        _cellularCall = cellularCall;
        _logMessage = logMessage;
        _queue = Channel.CreateBounded<DtmfRequest>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _worker = ProcessAsync(_cancellation.Token);
    }

    public bool TryQueue(byte eventId, int durationMilliseconds)
    {
        Interlocked.Increment(ref _received);
        if (_queue.Writer.TryWrite(new DtmfRequest(eventId, Math.Max(0, durationMilliseconds))))
        {
            _logMessage?.Invoke("DTMF telephone-event received and queued.");
            return true;
        }

        Interlocked.Increment(ref _dropped);
        _logMessage?.Invoke("DTMF telephone-event dropped because the command queue is full or closed.");
        return false;
    }

    public DtmfBridgeSnapshot GetSnapshot() => new(
        Interlocked.Read(ref _received),
        Interlocked.Read(ref _forwarded),
        Interlocked.Read(ref _failed),
        Interlocked.Read(ref _dropped));

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        _cancellation.Cancel();
        try
        {
            await _worker;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _cancellation.Dispose();
        }
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await _cellularCall.SendDtmfAsync(request.EventId, cancellationToken);
                    Interlocked.Increment(ref _forwarded);
                    _logMessage?.Invoke($"DTMF forwarded to the modem with AT+VTS; telephone-event duration={request.DurationMilliseconds}ms.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failed);
                    _logMessage?.Invoke($"DTMF forwarding failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private sealed record DtmfRequest(byte EventId, int DurationMilliseconds);
}

internal sealed record DtmfBridgeSnapshot(
    long ReceivedEvents,
    long ForwardedEvents,
    long FailedEvents,
    long DroppedEvents);
