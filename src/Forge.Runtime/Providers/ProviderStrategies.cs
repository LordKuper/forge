using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Forge.Application;

namespace Forge.Providers;

/// <summary>
/// Everything a provider strategy needs to discover and install/update using the vendor's own
/// recommended Windows mechanism (ADR 0002). Forge never re-implements a vendor's download or
/// verification; it shells out to the vendor's own installer or update command and then checks
/// the vendor-owned executable path.
/// </summary>
public sealed record ProviderInstallSpec(
    string ExecutablePath,
    IReadOnlyList<string> InstallArguments,
    IReadOnlyList<string>? UpdateArguments,
    Version? MinimumVersion);

public sealed class CodexProviderStrategy(IEnvironmentPaths paths, IProcessRunner processRunner) : IProviderStrategy
{
    private readonly ProviderInstallSpec spec = new(
        ExecutablePath: Path.Combine(
            paths.LocalApplicationData,
            "Programs",
            "OpenAI",
            "Codex",
            "bin",
            "codex.exe"),
        InstallArguments:
        [
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
            "$env:CODEX_NON_INTERACTIVE = '1'; irm https://chatgpt.com/codex/install.ps1 | iex",
        ],
        // No documented standalone update subcommand; the installer script itself compares the
        // installed version and is safe to rerun (see ADR 0002).
        UpdateArguments: null,
        MinimumVersion: null);

    public ProviderKind Kind => ProviderKind.Codex;

    public Task<ProviderStatus> DiscoverAsync(CancellationToken cancellationToken) =>
        ProviderDiscovery.DiscoverAsync(Kind, spec, processRunner, cancellationToken);

    public Task<ProviderStatus> InstallOrUpdateAsync(CancellationToken cancellationToken) =>
        ProviderDiscovery.InstallOrUpdateAsync(Kind, spec, processRunner, cancellationToken);

    public Task<string?> ResolveExecutableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(File.Exists(spec.ExecutablePath) ? spec.ExecutablePath : null);
}

public sealed class ClaudeCodeProviderStrategy(IEnvironmentPaths paths, IProcessRunner processRunner)
    : IProviderStrategy
{
    private readonly ProviderInstallSpec spec = new(
        ExecutablePath: Path.Combine(paths.UserProfile, ".local", "bin", "claude.exe"),
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

    public ProviderKind Kind => ProviderKind.ClaudeCode;

    public Task<ProviderStatus> DiscoverAsync(CancellationToken cancellationToken) =>
        ProviderDiscovery.DiscoverAsync(Kind, spec, processRunner, cancellationToken);

    public Task<ProviderStatus> InstallOrUpdateAsync(CancellationToken cancellationToken) =>
        ProviderDiscovery.InstallOrUpdateAsync(Kind, spec, processRunner, cancellationToken);

    public Task<string?> ResolveExecutableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(File.Exists(spec.ExecutablePath) ? spec.ExecutablePath : null);
}

/// <summary>Shared by every strategy: fixed-path discovery, version parsing, and native install/update.</summary>
internal static partial class ProviderDiscovery
{
    private static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Reads a fixed vendor-owned path and runs `--version`. Never touches the network.</summary>
    public static async Task<ProviderStatus> DiscoverAsync(
        ProviderKind kind,
        ProviderInstallSpec spec,
        IProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(spec.ExecutablePath))
        {
            return new(kind, ProviderState.Missing, null, ProviderDiagnosticCodes.Missing);
        }

        ProcessResult? result = await RunWithTimeoutAsync(
            processRunner,
            new(spec.ExecutablePath, ["--version"], Path.GetDirectoryName(spec.ExecutablePath)!),
            VersionProbeTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result is not { ExitCode: 0 })
        {
            return new(kind, ProviderState.Failed, null, ProviderDiagnosticCodes.UpdateFailed);
        }

        if (!TryParseVersion(result.StandardOutput, out Version? version))
        {
            return new(kind, ProviderState.Failed, null, ProviderDiagnosticCodes.UpdateFailed);
        }

        return spec.MinimumVersion is { } minimum && version < minimum
            ? new(kind, ProviderState.Failed, version.ToString(), ProviderDiagnosticCodes.VersionUnsupported)
            : ProviderStatus.Ready(kind, version.ToString());
    }

    public static async Task<ProviderStatus> InstallOrUpdateAsync(
        ProviderKind kind,
        ProviderInstallSpec spec,
        IProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        bool alreadyInstalled = File.Exists(spec.ExecutablePath);
        ProcessRequest request = alreadyInstalled && spec.UpdateArguments is { } updateArguments
            ? new(spec.ExecutablePath, updateArguments, Path.GetDirectoryName(spec.ExecutablePath)!)
            : new("powershell.exe", spec.InstallArguments, Path.GetTempPath());
        ProcessResult? result = await RunWithTimeoutAsync(processRunner, request, InstallTimeout, cancellationToken)
            .ConfigureAwait(false);
        return result is { ExitCode: 0 }
            ? await DiscoverAsync(kind, spec, processRunner, cancellationToken).ConfigureAwait(false)
            : new(kind, ProviderState.Failed, null, ProviderDiagnosticCodes.UpdateFailed);
    }

    private static async Task<ProcessResult?> RunWithTimeoutAsync(
        IProcessRunner processRunner,
        ProcessRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = new(timeout);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await processRunner.RunAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static bool TryParseVersion(string standardOutput, [NotNullWhen(true)] out Version? version)
    {
        Match match = VersionPattern().Match(standardOutput);
        if (match.Success && Version.TryParse(match.Value, out Version? parsed))
        {
            version = parsed;
            return true;
        }

        version = null;
        return false;
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionPattern();
}
