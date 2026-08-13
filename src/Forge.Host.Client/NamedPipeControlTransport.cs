using System.IO.Pipes;

namespace Forge.Host.Client;

/// <summary>
/// The one production <see cref="ILocalControlTransport"/>: asynchronous <see cref="System.IO.Pipes"/> in byte
/// mode with <see cref="PipeOptions.CurrentUserOnly"/>. The .NET runtime maps this to Windows named pipes on
/// Windows and Unix-domain sockets on Linux/macOS; Forge supplies only the short, hashed endpoint name.
/// </summary>
public sealed class NamedPipeControlTransport : ILocalControlTransport
{
    public async Task<ILocalControlConnection> ConnectAsync(
        string endpointName,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);
        NamedPipeClientStream pipe = new(
            ".",
            endpointName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using CancellationTokenSource deadlineSource = new(deadline);
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineSource.Token);
            try
            {
                await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
            }
            // Checking which token actually fired (not the caller's live state, which the caller could flip on
            // another thread in the same instant) avoids misattributing a genuine cancellation as "unavailable".
            catch (OperationCanceledException) when (deadlineSource.IsCancellationRequested)
            {
                throw new ControlProtocolException(
                    ControlDiagnosticCode.Unavailable,
                    "No Host is listening on this endpoint.");
            }

            return new PipeControlConnection(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ILocalControlListener CreateListener(string endpointName) => new NamedPipeControlListener(endpointName);
}

internal sealed class NamedPipeControlListener(string endpointName) : ILocalControlListener
{
    private readonly string endpointName = endpointName;

    public async Task<ILocalControlConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        NamedPipeServerStream pipe = new(
            endpointName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            return new PipeControlConnection(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // Intentionally a no-op: this listener holds no state of its own between AcceptAsync calls (each call owns
    // its own NamedPipeServerStream, disposed on its own connection or on failure above). The accept loop's own
    // cancellation token — not this Dispose — is what stops AcceptAsync from waiting for a new connection.
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class PipeControlConnection(PipeStream pipe) : ILocalControlConnection
{
    public async Task SendAsync(ReadOnlyMemory<byte> message, TimeSpan deadline, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadlineSource = new(deadline);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineSource.Token);
        try
        {
            await ControlMessageFraming.WriteMessageAsync(pipe, message, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadlineSource.IsCancellationRequested)
        {
            throw new ControlProtocolException(ControlDiagnosticCode.Timeout, "Writing the message exceeded its deadline.");
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            throw new ControlProtocolException(ControlDiagnosticCode.ConnectionClosed, "The connection is no longer usable.");
        }
    }

    public async Task<byte[]> ReceiveAsync(TimeSpan deadline, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadlineSource = new(deadline);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineSource.Token);
        try
        {
            return await ControlMessageFraming.ReadMessageAsync(pipe, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadlineSource.IsCancellationRequested)
        {
            throw new ControlProtocolException(ControlDiagnosticCode.Timeout, "Reading the message exceeded its deadline.");
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            throw new ControlProtocolException(ControlDiagnosticCode.ConnectionClosed, "The connection is no longer usable.");
        }
    }

    public ValueTask DisposeAsync() => pipe.DisposeAsync();
}
