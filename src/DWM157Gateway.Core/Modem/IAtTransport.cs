namespace DWM157Gateway.Core.Modem;

public interface IAtTransport : IAsyncDisposable
{
    event EventHandler<string>? LineReceived;
    Task OpenAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> SendCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken);
}
