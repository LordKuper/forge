using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Forge.Updater;

public sealed class RestartTokenService : IRestartTokenService
{
    private readonly ConcurrentDictionary<string, RestartIdentity> unusedTokens = new(StringComparer.Ordinal);

    public RestartContext Create(UpdateRequest request, RestartIdentity expectedIdentity)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (!unusedTokens.TryAdd(token, expectedIdentity))
        {
            throw new InvalidOperationException("Restart token collision.");
        }

        return new RestartContext(token, request.ExecutablePath, request.Arguments, request.WorkingDirectory, expectedIdentity);
    }

    public bool Consume(string token, RestartIdentity actualIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(actualIdentity);
        if (!unusedTokens.TryGetValue(token, out RestartIdentity? expectedIdentity) || expectedIdentity != actualIdentity)
        {
            return false;
        }

        return unusedTokens.TryRemove(token, out _);
    }
}

public sealed class StartupHandshake(IRestartTokenService tokens)
{
    private readonly IRestartTokenService tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));

    public UpdateDiagnostic Confirm(string token, RestartIdentity actualIdentity) =>
        tokens.Consume(token, actualIdentity)
            ? UpdateDiagnostic.None
            : new(UpdateDiagnosticCode.HandshakeFailed, "The restart token is missing, expired, replayed, or bound to a different release identity.");
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
