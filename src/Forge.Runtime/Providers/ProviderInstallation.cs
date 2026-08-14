using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Forge.Application;

namespace Forge.Providers;

/// <summary>
/// Everything a provider needs to discover and install/update using the vendor's own recommended
/// mechanism (ADR 0002). Forge never re-implements a vendor's download or verification; it shells
/// out to the vendor's own installer/update command and then checks the vendor-owned executable
/// path. <paramref name="InstallExecutable"/> is the adapter's own resolved installer path (e.g.
/// the OS shell used to run an install script) — the core has no opinion on what that is.
/// </summary>
public sealed record ProviderInstallSpec(
    string ExecutablePath,
    string InstallExecutable,
    IReadOnlyList<string> InstallArguments,
    IReadOnlyList<string>? UpdateArguments,
    Version? MinimumVersion);

/// <summary>
/// Generic provider install/discovery orchestration (ADR 0008: "generic... startup, retry...
/// policy" stays in the core) — fixed-path discovery, version parsing, and native install/update,
/// parameterized entirely by a vendor-supplied <see cref="ProviderInstallSpec"/> so no vendor path,
/// URL, or command lives here.
/// </summary>
public static partial class ProviderInstallation
{
    public static readonly TimeSpan DefaultVersionProbeTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DefaultInstallTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Reads a fixed vendor-owned path and runs `--version`. Never touches the network.</summary>
    public static async Task<ProviderStatus> DiscoverAsync(
        ProviderId id,
        ProviderInstallSpec spec,
        IProcessRunner processRunner,
        TimeSpan versionProbeTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!File.Exists(spec.ExecutablePath))
        {
            return new(id, ProviderState.Missing, null, ProviderDiagnosticCodes.Missing);
        }

        ProcessResult? result = await RunWithTimeoutAsync(
            processRunner,
            new(spec.ExecutablePath, ["--version"], Path.GetDirectoryName(spec.ExecutablePath)!),
            versionProbeTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result is not { ExitCode: 0 })
        {
            return new(id, ProviderState.Failed, null, ProviderDiagnosticCodes.UpdateFailed);
        }

        if (!TryParseVersion(result.StandardOutput, out Version? version))
        {
            return new(id, ProviderState.Failed, null, ProviderDiagnosticCodes.UpdateFailed);
        }

        return spec.MinimumVersion is { } minimum && version < minimum
            ? new(id, ProviderState.Failed, version.ToString(), ProviderDiagnosticCodes.VersionUnsupported)
            : ProviderStatus.Ready(id, version.ToString());
    }

    public static async Task<ProviderStatus> InstallOrUpdateAsync(
        ProviderId id,
        ProviderInstallSpec spec,
        IProcessRunner processRunner,
        TimeSpan versionProbeTimeout,
        TimeSpan installTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        bool alreadyInstalled = File.Exists(spec.ExecutablePath);
        ProcessRequest request = alreadyInstalled && spec.UpdateArguments is { } updateArguments
            ? new(spec.ExecutablePath, updateArguments, Path.GetDirectoryName(spec.ExecutablePath)!)
            : new(spec.InstallExecutable, spec.InstallArguments, Path.GetTempPath());
        ProcessResult? result = await RunWithTimeoutAsync(processRunner, request, installTimeout, cancellationToken)
            .ConfigureAwait(false);
        return result is { ExitCode: 0 }
            ? await DiscoverAsync(id, spec, processRunner, versionProbeTimeout, cancellationToken)
                .ConfigureAwait(false)
            : new(id, ProviderState.Failed, null, ProviderDiagnosticCodes.UpdateFailed);
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
