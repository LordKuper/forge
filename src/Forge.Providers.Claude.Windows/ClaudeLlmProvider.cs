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
    TimeSpan? versionProbeTimeout = null,
    TimeSpan? installTimeout = null) : ILlmProvider
{
    public static readonly ProviderId ClaudeCode = new("claude_code");

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
            cancellationToken).ConfigureAwait(false);
    }

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
