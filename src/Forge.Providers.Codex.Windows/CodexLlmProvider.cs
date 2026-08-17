using System.Text.Json;
using Forge.Application;

namespace Forge.Providers.Codex;

/// <summary>
/// The Codex CLI integration (ADR 0008): every Codex-specific path, install/update command, and
/// event shape lives here, never in the neutral core.
/// </summary>
public sealed class CodexLlmProvider(
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
    public static readonly ProviderId Codex = new("codex");

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
        ExecutablePath: Path.Combine(
            paths.LocalApplicationData,
            "Programs",
            "OpenAI",
            "Codex",
            "bin",
            "codex.exe"),
        InstallExecutable: PowerShellExecutable,
        InstallArguments:
        [
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
            "$env:CODEX_NON_INTERACTIVE = '1'; irm https://chatgpt.com/codex/install.ps1 | iex",
        ],
        // No documented standalone update subcommand; the installer script itself compares the
        // installed version and is safe to rerun (see ADR 0002).
        UpdateArguments: null,
        MinimumVersion: null);

    public ProviderId Id => Codex;

    // ponytail: fixed MVP default, not a per-project model choice — nothing selects a model yet
    // (Stage 11, P11.13-P11.20). Revisit once real per-project model configuration exists.
    public string DefaultModel => "gpt-5";

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
            cancellationToken);

    public Task<ProviderStatus> InstallOrUpdateAsync(bool bypassReleaseCache, CancellationToken cancellationToken) =>
        ProviderInstallation.InstallOrUpdateAsync(
            Id,
            spec,
            processRunner,
            releaseSource,
            releaseCache,
            installLock,
            clock,
            bypassReleaseCache,
            versionProbeTimeout ?? ProviderInstallation.DefaultVersionProbeTimeout,
            installTimeout ?? ProviderInstallation.DefaultInstallTimeout,
            installLockTimeout ?? ProviderInstallation.DefaultInstallLockTimeout,
            cancellationToken);

    public Task<string?> ResolveExecutableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(File.Exists(spec.ExecutablePath) ? spec.ExecutablePath : null);

    /// <summary>`codex login status` is documented to be scriptable by exit code alone: 0 means
    /// authenticated, 1 means not — no output parsing needed or attempted.</summary>
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
            ["login", "status"],
            probeDirectory,
            authenticationProbeTimeout ?? ProviderInstallation.DefaultAuthenticationProbeTimeout,
            result => result.ExitCode == 0 ? ProviderAuthenticationStatus.Ready : ProviderAuthenticationStatus.Required,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Event shape per `developers.openai.com/codex` (`type`: `thread.started`, `turn.started`,
    /// `turn.completed`, `turn.failed`, `item.*`). Item subtypes are documented only in prose, so
    /// text extraction stays conservative rather than guessing field names.
    /// </summary>
    public async Task<ProviderRunResult> RunAsync(
        string prompt,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(prompt);
        string? executable = await ResolveExecutableAsync(cancellationToken).ConfigureAwait(false);
        // `--` marks the end of options so a prompt starting with `-` (or `--`) can never be
        // parsed as a flag by the vendor CLI.
        return await ProviderExecution.RunAsync(
            executable,
            processRunner,
            ["exec", "--json", "--", prompt],
            workingDirectory,
            Classify,
            _ => null,
            cancellationToken).ConfigureAwait(false);
    }

    private static ProviderEventKind Classify(JsonElement root)
    {
        string type = TypeOf(root);
        if (type.StartsWith("turn.", StringComparison.Ordinal))
        {
            return ProviderEventKind.Result;
        }

        return type.StartsWith("item.", StringComparison.Ordinal)
            ? ProviderEventKind.ToolUse
            : ProviderEventKind.Unknown;
    }

    private static string TypeOf(JsonElement root) =>
        root.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String
            ? type.GetString() ?? string.Empty
            : string.Empty;
}
