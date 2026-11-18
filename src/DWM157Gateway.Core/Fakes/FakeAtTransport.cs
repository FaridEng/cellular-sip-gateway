using DWM157Gateway.Core.Modem;

namespace DWM157Gateway.Core.Fakes;

public sealed class FakeAtTransport : IAtTransport
{
    private readonly Queue<IReadOnlyList<string>> _responses = new();
    public List<string> Commands { get; } = [];
    public event EventHandler<string>? LineReceived;

    public void EnqueueResponse(params string[] lines) => _responses.Enqueue(lines);
    public void Emit(string line) => LineReceived?.Invoke(this, line);

    public Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<string>> SendCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Commands.Add(command);
        return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : ["OK"]);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
