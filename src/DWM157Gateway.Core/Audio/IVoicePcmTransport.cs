using System.Threading.Channels;

namespace DWM157Gateway.Core.Audio;

public interface IVoicePcmTransport
{
    ChannelReader<byte[]> RxFrames { get; }
    Task WriteFrameAsync(byte[] s16leFrame, CancellationToken cancellationToken);
}

public sealed class DeferredVoicePcmTransport : IVoicePcmTransport, IAsyncDisposable
{
    private readonly Channel<byte[]> _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(500)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true
    });
    private readonly CancellationTokenSource _lifetimeCts = new();
    private IVoicePcmTransport? _attachedTransport;
    private Task? _pumpTask;

    public ChannelReader<byte[]> RxFrames => _frames.Reader;

    public void Attach(IVoicePcmTransport transport, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _attachedTransport, transport, null) is not null)
        {
            throw new InvalidOperationException("A voice transport is already attached.");
        }

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token, cancellationToken);
        _pumpTask = Task.Run(async () =>
        {
            using (linkedCts)
            {
                try
                {
                    await foreach (var frame in transport.RxFrames.ReadAllAsync(linkedCts.Token))
                    {
                        await _frames.Writer.WriteAsync(frame, linkedCts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    _frames.Writer.TryComplete();
                }
            }
        }, CancellationToken.None);
    }

    public Task WriteFrameAsync(byte[] s16leFrame, CancellationToken cancellationToken)
    {
        var transport = Volatile.Read(ref _attachedTransport);
        return transport is null
            ? Task.CompletedTask
            : transport.WriteFrameAsync(s16leFrame, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetimeCts.Cancel();
        _frames.Writer.TryComplete();
        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _lifetimeCts.Dispose();
    }
}
