using System.Text.Json;
using Forge.Application;

namespace Forge.Providers.Claude;

/// <summary>
/// The Claude Code CLI integration (ADR 0008): every Claude-specific path, install/update
/// command, and event shape lives here, never in the neutral core.
/// </summary>
public sealed class ClaudeLlmProvider(
    IEnvironmentPaths paths,
    IProcessRunner processRunner,
    IProviderReleaseSource releaseSource,
    IProviderReleaseCache releaseCache,
    IProviderInstallLock installLock,
    IClock clock,
    TimeSpan? versionProbeTimeout = null,
    TimeSpan? installTimeout = null,
    TimeSpan? installLockTimeout = null,
    TimeSpan? authenticationProbeTimeout = null) : ILlmProvider
{
    public static readonly ProviderId ClaudeCode = new("claude_code");

    /// <summary>Every invocation except an explicit install/update (ADR 0008): "Normal Claude
    /// Code execution sets DISABLE_AUTOUPDATER=1 so Forge owns cadence; the variable is not set
    /// for an explicit update." Applied to prompt execution and to every local probe (`--version`,
    /// `auth status`) — any of those can otherwise trigger the vendor's own background update
    /// check — but never to the install/update command itself. Forge does not set
    /// DISABLE_UPDATES.</summary>
    private static readonly IReadOnlyDictionary<string, string> ExecutionEnvironmentVariables =
        new Dictionary<string, string> { ["DISABLE_AUTOUPDATER"] = "1" };

    /// <summary>A Forge-owned working directory for probes that must not pick up a project-local
    /// vendor config file (ADR 0008: "from a Forge-owned probe directory").</summary>
    private readonly string probeDirectory = FileProviderReleaseCache.ProviderStateDirectory(paths);

    /// <summary>
    /// The fully-qualified in-box Windows PowerShell path, never a bare `powershell.exe` (ADR
    /// 0002): a bare name is resolved through `CreateProcess`'s search order, which checks the
    /// calling image's own directory and the current directory before `System32`.
    /// </summary>
    private static readonly string PowerShellExecutable = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");

    private readonly ProviderInstallSpec spec = new(
        ExecutablePath: Path.Combine(paths.UserProfile, ".local", "bin", "claude.exe"),
        InstallExecutable: PowerShellExecutable,
        InstallArguments:
        [
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
            "irm https://claude.ai/install.ps1 | iex",
        ],
        // `claude update` is the vendor-documented manual update path and is lighter than
        // rerunning the full installer.
        UpdateArguments: ["update"],
        // Native Windows support (no WSL) shipped at major version 2; see ADR 0002.
        MinimumVersion: new Version(2, 0, 0));

    public ProviderId Id => ClaudeCode;

    public Task<ProviderStatus> DiscoverAsync(bool bypassReleaseCache, CancellationToken cancellationToken) =>
        ProviderInstallation.DiscoverAsync(
            Id,
            spec,
            processRunner,
            releaseSource,
            releaseCache,
            clock,
            bypassReleaseCache,
            versionProbeTimeout ?? ProviderInstallation.DefaultVersionProbeTimeout,
            cancellationToken,
            ExecutionEnvironmentVariables);

    public Task<ProviderStatus> InstallOrUpdateAsync(CancellationToken cancellationToken) =>
        ProviderInstallation.InstallOrUpdateAsync(
            Id,
            spec,
            processRunner,
            releaseSource,
            releaseCache,
            installLock,
            clock,
            versionProbeTimeout ?? ProviderInstallation.DefaultVersionProbeTimeout,
            installTimeout ?? ProviderInstallation.DefaultInstallTimeout,
            installLockTimeout ?? ProviderInstallation.DefaultInstallLockTimeout,
            cancellationToken,
            ExecutionEnvironmentVariables);

    public Task<string?> ResolveExecutableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(File.Exists(spec.ExecutablePath) ? spec.ExecutablePath : null);

    /// <summary>
    /// `claude auth status --json`'s exact response schema is not published; this checks the
    /// plausible boolean/credential-presence signals in priority order rather than committing to
    /// one guessed field name, and returns <see cref="ProviderAuthenticationStatus.CheckFailed"/>
    /// for any shape it does not recognize rather than guessing Ready or Required. Exit code alone
    /// is deliberately not trusted (the command's own documentation warns against scripting
    /// against it) — only the parsed body decides.
    /// </summary>
    public async Task<ProviderAuthenticationStatus> CheckAuthenticationAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(probeDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return ProviderAuthenticationStatus.CheckFailed;
        }

        string? executable = await ResolveExecutableAsync(cancellationToken).ConfigureAwait(false);
        return await ProviderInstallation.CheckAuthenticationAsync(
            executable,
            processRunner,
            ["auth", "status", "--json"],
            probeDirectory,
            authenticationProbeTimeout ?? ProviderInstallation.DefaultAuthenticationProbeTimeout,
            ParseAuthenticationStatus,
            cancellationToken,
            ExecutionEnvironmentVariables).ConfigureAwait(false);
    }

    /// <summary>
    /// Event shape per Claude Code's `--output-format stream-json` (`claude -p ... --verbose`):
    /// top-level `type` is `system`, `assistant`, `user`, or `result` — there is no top-level
    /// `tool_use` type. `assistant` events wrap an Anthropic Messages API message object, whose
    /// `content` array can mix text blocks with `tool_use` content blocks; text is read from the
    /// `text`-typed blocks in `message.content[]`.
    /// </summary>
    public async Task<ProviderRunResult> RunAsync(
        string prompt,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(prompt);
        string? executable = await ResolveExecutableAsync(cancellationToken).ConfigureAwait(false);
        // `--verbose` is required by `claude -p` whenever `--output-format stream-json` is used.
        // `--` marks the end of options so a prompt starting with `-` can never be parsed as a
        // flag (for example a prompt beginning with `--dangerously-skip-permissions`).
        return await ProviderExecution.RunAsync(
            executable,
            processRunner,
            ["-p", "--output-format", "stream-json", "--verbose", "--", prompt],
            workingDirectory,
            Classify,
            ExtractText,
            cancellationToken,
            ExecutionEnvironmentVariables).ConfigureAwait(false);
    }

    private static ProviderAuthenticationStatus ParseAuthenticationStatus(ProcessResult result)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            JsonElement root = document.RootElement;
            if (TryGetBoolean(root, "authenticated", out bool authenticated) ||
                TryGetBoolean(root, "isAuthenticated", out authenticated) ||
                TryGetBoolean(root, "is_authenticated", out authenticated) ||
                TryGetBoolean(root, "loggedIn", out authenticated) ||
                TryGetBoolean(root, "logged_in", out authenticated))
            {
                return authenticated ? ProviderAuthenticationStatus.Ready : ProviderAuthenticationStatus.Required;
            }

            if (HasNonEmptyProperty(root, "credentials") ||
                HasNonEmptyProperty(root, "activeCredentials") ||
                HasNonEmptyProperty(root, "active_credentials"))
            {
                return ProviderAuthenticationStatus.Ready;
            }
        }
        catch (JsonException)
        {
            // Falls through to CheckFailed below.
        }

        return ProviderAuthenticationStatus.CheckFailed;
    }

    private static bool TryGetBoolean(JsonElement root, string propertyName, out bool value)
    {
        if (root.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        value = false;
        return false;
    }

    private static bool HasNonEmptyProperty(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out JsonElement property) &&
        property.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) &&
        (property.ValueKind != JsonValueKind.Object || property.EnumerateObject().Any());

    private static ProviderEventKind Classify(JsonElement root) => TypeOf(root) switch
    {
        "assistant" => ProviderEventKind.Message,
        "result" => ProviderEventKind.Result,
        _ => ProviderEventKind.Unknown,
    };

    private static string? ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("message", out JsonElement message) ||
            !message.TryGetProperty("content", out JsonElement content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        IEnumerable<string> blocks = content
            .EnumerateArray()
            .Where(block => block.TryGetProperty("type", out JsonElement blockType) &&
                blockType.ValueKind == JsonValueKind.String &&
                blockType.GetString() == "text" &&
                block.TryGetProperty("text", out JsonElement _))
            .Select(block => block.GetProperty("text").GetString() ?? string.Empty);
        string joined = string.Join(string.Empty, blocks);
        return joined.Length > 0 ? joined : null;
    }

    private static string TypeOf(JsonElement root) =>
        root.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String
            ? type.GetString() ?? string.Empty
            : string.Empty;
}
