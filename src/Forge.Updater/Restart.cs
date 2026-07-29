using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Forge.Updater;

public sealed class RestartTokenService : IRestartTokenService
{
    private readonly ConcurrentDictionary<string, byte> unusedTokens = new(StringComparer.Ordinal);

    public RestartContext Create(UpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (!unusedTokens.TryAdd(token, 0))
        {
            throw new InvalidOperationException("Restart token collision.");
        }

        return new RestartContext(token, request.ExecutablePath, request.Arguments, request.WorkingDirectory);
    }

    public bool Consume(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return unusedTokens.TryRemove(token, out _);
    }
}

public sealed class StartupHandshake(IRestartTokenService tokens)
{
    private readonly IRestartTokenService tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));

    public UpdateDiagnostic Confirm(string token) =>
        tokens.Consume(token)
            ? UpdateDiagnostic.None
            : new(UpdateDiagnosticCode.HandshakeFailed, "The restart token is missing, expired, or was already consumed.");
}

public sealed class RejectingRestartCoordinator : IRestartCoordinator
{
    public ValueTask<UpdateDiagnostic> RestartAsync(
        RestartContext restart,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(restart);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new UpdateDiagnostic(
            UpdateDiagnosticCode.RestartFailed,
            "No platform restart coordinator is registered."));
    }
}
