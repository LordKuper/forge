using Forge.Host.Client;

// Exercises the exact production ILocalControlTransport (NamedPipeControlTransport,
// PipeOptions.CurrentUserOnly) that ForgeHostClient/ControlPlaneHostedService use, so a same-user
// isolation test proves the real transport enforces it — not a hand-rolled stand-in.
//
// Usage: Forge.PipeIsolationProbe <listen|connect> <endpointName> <timeoutSeconds>
// Exit 0 on success (accepted/connected); non-zero on failure or timeout. Prints one word to
// stdout describing the outcome, for a caller script to log without parsing exception text.
if (args.Length != 3 ||
    args[0] is not ("listen" or "connect") ||
    !int.TryParse(args[2], out int timeoutSeconds))
{
    Console.Error.WriteLine("Usage: Forge.PipeIsolationProbe <listen|connect> <endpointName> <timeoutSeconds>");
    return 64;
}

string mode = args[0];
string endpointName = args[1];
using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(timeoutSeconds));
NamedPipeControlTransport transport = new();

try
{
    if (mode == "listen")
    {
        await using ILocalControlListener listener = transport.CreateListener(endpointName);
        await using ILocalControlConnection connection = await listener
            .AcceptAsync(cancellation.Token)
            .ConfigureAwait(false);
        Console.WriteLine("accepted");
        return 0;
    }

    await using ILocalControlConnection client = await transport
        .ConnectAsync(endpointName, TimeSpan.FromSeconds(timeoutSeconds), cancellation.Token)
        .ConfigureAwait(false);
    Console.WriteLine("connected");
    return 0;
}
catch (OperationCanceledException)
{
    Console.WriteLine("timeout");
    return 1;
}
catch (Exception exception)
{
    // Any other failure — including the access-denied/unavailable outcome a same-user isolation
    // test expects from a different-user connect attempt — is reported by type, not swallowed.
    Console.WriteLine(exception.GetType().Name);
    return 1;
}
