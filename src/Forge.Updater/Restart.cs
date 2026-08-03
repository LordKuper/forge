using System.Security.Cryptography;
using System.Text.Json;

namespace Forge.Updater;

public sealed class RestartTokenService(IRestartTokenStore store) : IRestartTokenService
{
    private readonly IRestartTokenStore store = store ?? throw new ArgumentNullException(nameof(store));

    public RestartContext Create(UpdateRequest request, RestartIdentity expectedIdentity)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (!store.TryCreate(token, expectedIdentity))
        {
            throw new InvalidOperationException("Restart token collision.");
        }

        return new RestartContext(token, request.ExecutablePath, request.Arguments, request.WorkingDirectory, expectedIdentity);
    }

    public bool Consume(string token, RestartIdentity actualIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(actualIdentity);
        return store.TryConsume(token, actualIdentity);
    }

    public void Revoke(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        store.Revoke(token);
    }
}

public sealed class FileRestartTokenStore : IRestartTokenStore
{
    private const string FileExtension = ".restart-token.json";
    private readonly string directory;

    public FileRestartTokenStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = Path.GetFullPath(directory);
    }

    public bool TryCreate(string token, RestartIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(identity);
        try
        {
            Directory.CreateDirectory(directory);
            using FileStream stream = new(
                GetTokenPath(token)!,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            JsonSerializer.Serialize(stream, PersistedRestartIdentity.From(identity));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public bool Exists(string token) =>
        TryGetTokenPath(token, out string? tokenPath) &&
        (File.Exists(tokenPath) || ClaimPaths(token).Any());

    public bool TryConsume(string token, RestartIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(identity);
        if (!TryGetTokenPath(token, out string? tokenPath))
        {
            return false;
        }
        string claimPath = Path.Combine(directory, $".{token}.{Guid.NewGuid():N}.claim");
        try
        {
            File.Move(tokenPath!, claimPath);
        }
        catch (IOException)
        {
            return false;
        }

        try
        {
            PersistedRestartIdentity? persisted = JsonSerializer.Deserialize<PersistedRestartIdentity>(File.ReadAllText(claimPath));
            if (persisted is not null && persisted.Matches(identity))
            {
                File.Delete(claimPath);
                return true;
            }

            File.Move(claimPath, tokenPath!);
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public void Revoke(string token)
    {
        if (TryGetTokenPath(token, out string? tokenPath))
        {
            File.Delete(tokenPath!);
            foreach (string claimPath in ClaimPaths(token))
            {
                File.Delete(claimPath);
            }
        }
    }

    private string? GetTokenPath(string token) =>
        TryGetTokenPath(token, out string? path)
            ? path
            : throw new ArgumentException("Restart token must be a 32-byte hexadecimal value.", nameof(token));

    private bool TryGetTokenPath(string token, out string? path)
    {
        if (token.Length != 64 || !token.All(Uri.IsHexDigit))
        {
            path = null;
            return false;
        }

        path = Path.Combine(directory, $"{token}{FileExtension}");
        return true;
    }

    private IEnumerable<string> ClaimPaths(string token) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, $".{token}.*.claim")
            : [];

    private sealed record PersistedRestartIdentity(
        string Version,
        string OperatingSystem,
        string Architecture,
        string Packaging,
        UpdateSurface Surface)
    {
        public static PersistedRestartIdentity From(RestartIdentity identity) =>
            new(
                identity.Version.ToString(),
                identity.Target.OperatingSystem,
                identity.Target.Architecture,
                identity.Target.Packaging,
                identity.Surface);

        public bool Matches(RestartIdentity identity) =>
            string.Equals(Version, identity.Version.ToString(), StringComparison.Ordinal) &&
            string.Equals(OperatingSystem, identity.Target.OperatingSystem, StringComparison.Ordinal) &&
            string.Equals(Architecture, identity.Target.Architecture, StringComparison.Ordinal) &&
            string.Equals(Packaging, identity.Target.Packaging, StringComparison.Ordinal) &&
            Surface == identity.Surface;
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
