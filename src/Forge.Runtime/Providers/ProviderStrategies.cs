using System.ComponentModel;
using System.Runtime.InteropServices;
using Forge.Application;

namespace Forge.Providers;

public sealed class CodexProviderStrategy(
    GitHubProviderInstaller installer,
    IProcessRunner processRunner) : IProviderStrategy
{
    private static readonly ProviderInstallSpec Spec = new(
        DirectoryName: "codex",
        Owner: "openai",
        Repo: "codex",
        ExecutableFileName: "codex.exe",
        AssetName: architecture => architecture == "arm64"
            ? "codex-aarch64-pc-windows-msvc.exe"
            : "codex-x86_64-pc-windows-msvc.exe",
        AssetIsZip: false,
        // No documented minimum-version floor for Codex CLI; see ADR 0002.
        MinimumVersion: null);

    private readonly GitHubProviderInstaller installer = installer ?? throw new ArgumentNullException(nameof(installer));
    private readonly IProcessRunner processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));

    public ProviderKind Kind => ProviderKind.Codex;

    public Task<ProviderStatus> DiscoverAsync(CancellationToken cancellationToken) =>
        ProviderDiscovery.DiscoverAsync(Kind, Spec, installer, processRunner, cancellationToken);

    public Task<ProviderStatus> InstallOrUpdateAsync(CancellationToken cancellationToken) =>
        installer.InstallOrUpdateAsync(Kind, Spec, ProviderArchitecture.Current, cancellationToken);

    public Task<string?> ResolveExecutableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ProviderDiscovery.ResolveExecutable(Spec, installer));
}

public sealed class ClaudeCodeProviderStrategy(
    GitHubProviderInstaller installer,
    IProcessRunner processRunner) : IProviderStrategy
{
    private static readonly ProviderInstallSpec Spec = new(
        DirectoryName: "claude-code",
        Owner: "anthropics",
        Repo: "claude-code",
        ExecutableFileName: "claude.exe",
        AssetName: architecture => architecture == "arm64"
            ? "claude-win32-arm64.zip"
            : "claude-win32-x64.zip",
        AssetIsZip: true,
        // Native Windows support (no WSL) shipped at major version 2; see ADR 0002.
        MinimumVersion: new Version(2, 0, 0));

    private readonly GitHubProviderInstaller installer = installer ?? throw new ArgumentNullException(nameof(installer));
    private readonly IProcessRunner processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));

    public ProviderKind Kind => ProviderKind.ClaudeCode;

    public Task<ProviderStatus> DiscoverAsync(CancellationToken cancellationToken) =>
        ProviderDiscovery.DiscoverAsync(Kind, Spec, installer, processRunner, cancellationToken);

    public Task<ProviderStatus> InstallOrUpdateAsync(CancellationToken cancellationToken) =>
        installer.InstallOrUpdateAsync(Kind, Spec, ProviderArchitecture.Current, cancellationToken);

    public Task<string?> ResolveExecutableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ProviderDiscovery.ResolveExecutable(Spec, installer));
}

internal static class ProviderArchitecture
{
    public static string Current => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
}

/// <summary>
/// Shared by every strategy: read the local pointer and confirm the pinned executable still
/// runs. Never calls the network, so it is safe on every startup pass.
/// </summary>
internal static class ProviderDiscovery
{
    public static string? ResolveExecutable(ProviderInstallSpec spec, GitHubProviderInstaller installer)
    {
        string? version = installer.ReadCurrentVersion(spec.DirectoryName);
        if (version is null)
        {
            return null;
        }

        string executable = installer.ExecutablePath(spec.DirectoryName, version, spec.ExecutableFileName);
        return File.Exists(executable) ? executable : null;
    }

    public static async Task<ProviderStatus> DiscoverAsync(
        ProviderKind kind,
        ProviderInstallSpec spec,
        GitHubProviderInstaller installer,
        IProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        string? version = installer.ReadCurrentVersion(spec.DirectoryName);
        if (version is null)
        {
            return new(kind, ProviderState.Missing, null, ProviderDiagnosticCodes.Missing);
        }

        string executable = installer.ExecutablePath(spec.DirectoryName, version, spec.ExecutableFileName);
        if (!File.Exists(executable))
        {
            return new(kind, ProviderState.Failed, version, ProviderDiagnosticCodes.UpdateFailed);
        }

        try
        {
            ProcessResult result = await processRunner
                .RunAsync(
                    new(executable, ["--version"], Path.GetDirectoryName(executable)!),
                    cancellationToken)
                .ConfigureAwait(false);
            return result.ExitCode == 0
                ? ProviderStatus.Ready(kind, version)
                : new(kind, ProviderState.Failed, version, ProviderDiagnosticCodes.UpdateFailed);
        }
        catch (Exception exception) when (exception is IOException or Win32Exception)
        {
            return new(kind, ProviderState.Failed, version, ProviderDiagnosticCodes.UpdateFailed);
        }
    }
}
