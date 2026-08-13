namespace Forge.Host.Client;

/// <summary>One open duplex connection between a client and the Host, framed per <see cref="ControlMessageFraming"/>.</summary>
public interface ILocalControlConnection : IAsyncDisposable
{
    Task SendAsync(ReadOnlyMemory<byte> message, TimeSpan deadline, CancellationToken cancellationToken);

    Task<byte[]> ReceiveAsync(TimeSpan deadline, CancellationToken cancellationToken);
}

/// <summary>Accepts incoming connections on one endpoint. The Host owns exactly one listener per project.</summary>
public interface ILocalControlListener : IAsyncDisposable
{
    Task<ILocalControlConnection> AcceptAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The protocol/test boundary ADR 0005 describes: one production implementation
/// (<see cref="NamedPipeControlTransport"/>) backed by <see cref="System.IO.Pipes"/>, no OS branch or fallback.
/// </summary>
public interface ILocalControlTransport
{
    Task<ILocalControlConnection> ConnectAsync(string endpointName, TimeSpan deadline, CancellationToken cancellationToken);

    ILocalControlListener CreateListener(string endpointName);
}
