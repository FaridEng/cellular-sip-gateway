using System.Threading.Channels;
using DWM157Gateway.Core.Audio;

namespace DWM157Gateway.Core.Tests;

public sealed class DeferredVoicePcmTransportTests
{
    [Fact]
    public async Task DropsWritesBeforeAttachAndForwardsBothDirectionsAfterAttach()
    {
        await using var deferred = new DeferredVoicePcmTransport();
        var actual = new FakeVoiceTransport();

        await deferred.WriteFrameAsync([1, 2], CancellationToken.None);
        Assert.Empty(actual.Writes);

        deferred.Attach(actual, CancellationToken.None);
        actual.Emit([3, 4]);
        var received = await deferred.RxFrames.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        await deferred.WriteFrameAsync([5, 6], CancellationToken.None);

        Assert.Equal([3, 4], received);
        Assert.Equal(new byte[] { 5, 6 }, Assert.Single(actual.Writes));
    }

    [Fact]
    public async Task RejectsASecondAttachment()
    {
        using var cancellation = new CancellationTokenSource();
        await using var deferred = new DeferredVoicePcmTransport();
        deferred.Attach(new FakeVoiceTransport(), cancellation.Token);

        Assert.Throws<InvalidOperationException>(() => deferred.Attach(new FakeVoiceTransport(), cancellation.Token));
        cancellation.Cancel();
    }

    private sealed class FakeVoiceTransport : IVoicePcmTransport
    {
        private readonly Channel<byte[]> _frames = Channel.CreateUnbounded<byte[]>();
        public ChannelReader<byte[]> RxFrames => _frames.Reader;
        public List<byte[]> Writes { get; } = [];

        public void Emit(byte[] frame) => _frames.Writer.TryWrite(frame);

        public Task WriteFrameAsync(byte[] s16leFrame, CancellationToken cancellationToken)
        {
            Writes.Add(s16leFrame);
            return Task.CompletedTask;
        }
    }
}
