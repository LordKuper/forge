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
    TimeSpan? versionProbeTimeout = null,
    TimeSpan? installTimeout = null) : ILlmProvider
{
    public static readonly ProviderId Codex = new("codex");

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

    public Task<ProviderStatus> DiscoverAsync(CancellationToken cancellationToken) =>
        ProviderInstallation.DiscoverAsync(
            Id,
            spec,
            processRunner,
            versionProbeTimeout ?? ProviderInstallation.DefaultVersionProbeTimeout,
            cancellationToken);

    public Task<ProviderStatus> InstallOrUpdateAsync(CancellationToken cancellationToken) =>
        ProviderInstallation.InstallOrUpdateAsync(
            Id,
            spec,
            processRunner,
            versionProbeTimeout ?? ProviderInstallation.DefaultVersionProbeTimeout,
            installTimeout ?? ProviderInstallation.DefaultInstallTimeout,
            cancellationToken);

    public Task<string?> ResolveExecutableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(File.Exists(spec.ExecutablePath) ? spec.ExecutablePath : null);

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
