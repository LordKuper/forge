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
/// Generic provider install/discovery/maintenance orchestration (ADR 0008: "generic... startup,
/// retry... policy" stays in the core) — fixed-path discovery, throttled release-availability
/// comparison, native install/update behind a per-user lock, and authentication probing, all
/// parameterized entirely by vendor-supplied specs and delegates so no vendor path, URL, command,
/// or response format lives here.
/// </summary>
public static partial class ProviderInstallation
{
    public static readonly TimeSpan DefaultVersionProbeTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DefaultInstallTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DefaultInstallLockTimeout = TimeSpan.FromMinutes(10);

    /// <summary>ADR 0008: "Forge checks authentication... at every startup. The commands run with
    /// a 15-second deadline."</summary>
    public static readonly TimeSpan DefaultAuthenticationProbeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>ADR 0008: "a small per-user cache limits the network update-availability check to
    /// once per 24 hours."</summary>
    public static readonly TimeSpan ReleaseCheckSuccessWindow = TimeSpan.FromHours(24);

    /// <summary>ADR 0008: "a failed check or update is retried after one hour."</summary>
    public static readonly TimeSpan ReleaseCheckFailureWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// The local bounded probe, plus — only for an already-usable install — a throttled or
    /// explicit release-availability comparison. Never installs or updates anything; a release
    /// check failure leaves <see cref="ProviderStatus.UpdateAvailable"/> unknown rather than
    /// touching <see cref="ProviderStatus.State"/> (ADR 0008: "A release-check failure does not
    /// block an otherwise-usable installed version").
    /// </summary>
    public static async Task<ProviderStatus> DiscoverAsync(
        ProviderId id,
        ProviderInstallSpec spec,
        IProcessRunner processRunner,
        IProviderReleaseSource releaseSource,
        IProviderReleaseCache cache,
        IClock clock,
        bool bypassReleaseCache,
        TimeSpan versionProbeTimeout,
        CancellationToken cancellationToken)
    {
        (ProviderStatus local, Version? localVersion) = await DiscoverLocalAsync(
            id,
            spec,
            processRunner,
            versionProbeTimeout,
            cancellationToken).ConfigureAwait(false);
        if (local.State != ProviderState.Ready || localVersion is null)
        {
            return local;
        }

        bool? updateAvailable = await CheckUpdateAvailableAsync(
            id,
            localVersion,
            releaseSource,
            cache,
            clock,
            bypassReleaseCache,
            cancellationToken).ConfigureAwait(false);
        return local with { UpdateAvailable = updateAvailable };
    }

    /// <summary>
    /// A missing, corrupt, or unsupported install is installed/repaired directly, skipping the
    /// update comparison. An already-usable install is updated only when a fresh (never cached)
    /// release check confirms a newer version. Either mutation is protected by
    /// <paramref name="installLock"/> and followed by a local-only recheck — never another network
    /// release check (ADR 0008).
    /// </summary>
    public static async Task<ProviderStatus> InstallOrUpdateAsync(
        ProviderId id,
        ProviderInstallSpec spec,
        IProcessRunner processRunner,
        IProviderReleaseSource releaseSource,
        IProviderReleaseCache cache,
        IProviderInstallLock installLock,
        IClock clock,
        TimeSpan versionProbeTimeout,
        TimeSpan installTimeout,
        TimeSpan installLockTimeout,
        CancellationToken cancellationToken)
    {
        (ProviderStatus local, Version? localVersion) = await DiscoverLocalAsync(
            id,
            spec,
            processRunner,
            versionProbeTimeout,
            cancellationToken).ConfigureAwait(false);
        // DiscoverLocalAsync only ever returns Ready alongside a successfully parsed version.
        if (local.State == ProviderState.Ready && localVersion is { } version)
        {
            ProviderReleaseLookupResult lookup =
                await FetchAndCacheAsync(id, releaseSource, cache, clock, cancellationToken).ConfigureAwait(false);
            if (!lookup.Succeeded)
            {
                // A release-check failure never blocks an otherwise-usable installed version.
                return local with { UpdateAvailable = null };
            }

            if (lookup.Version is null || lookup.Version <= version)
            {
                return local with { UpdateAvailable = false };
            }
        }

        await using IProviderInstallLease? lease = await installLock
            .TryAcquireAsync(installLockTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (lease is null)
        {
            // Could not acquire the lock (another process is already installing/updating). Every
            // case reports its own real, unaltered status — nothing was actually attempted here —
            // rather than synthesizing a generic failure: a previously-usable version stays
            // usable, and a missing/broken one keeps its own diagnostic (Missing,
            // VersionUnsupported, ...) instead of being mislabeled as an update failure.
            return local.State == ProviderState.Ready ? local with { UpdateAvailable = true } : local;
        }

        bool alreadyInstalled = File.Exists(spec.ExecutablePath);
        ProcessRequest request = alreadyInstalled && spec.UpdateArguments is { } updateArguments
            ? new(spec.ExecutablePath, updateArguments, Path.GetDirectoryName(spec.ExecutablePath)!)
            : new(spec.InstallExecutable, spec.InstallArguments, Path.GetTempPath());
        ProcessResult? result = await RunWithTimeoutAsync(processRunner, request, installTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (result is not { ExitCode: 0 })
        {
            (ProviderStatus recheck, _) = await DiscoverLocalAsync(
                id,
                spec,
                processRunner,
                versionProbeTimeout,
                cancellationToken).ConfigureAwait(false);
            // ADR 0008: "An update failure blocks only when the installed provider is no longer
            // usable" — a still-usable prior install after a failed update/install attempt stays
            // reported as ready rather than failed.
            return recheck.State == ProviderState.Ready
                ? recheck
                : new(id, ProviderState.Failed, null, ProviderDiagnosticCodes.UpdateFailed);
        }

        (ProviderStatus rechecked, _) = await DiscoverLocalAsync(
            id,
            spec,
            processRunner,
            versionProbeTimeout,
            cancellationToken).ConfigureAwait(false);
        return rechecked.State == ProviderState.Ready ? rechecked with { UpdateAvailable = false } : rechecked;
    }

    /// <summary>
    /// Runs a vendor's local, non-network authentication-status command (ADR 0008: "Forge checks
    /// authentication on the final executable at every startup... never persists or logs raw
    /// status output, identity fields, authentication method, or credential material"). Only the
    /// caller-supplied <paramref name="parseResult"/> ever sees the raw process output; its return
    /// value is the only thing that survives.
    /// </summary>
    public static async Task<ProviderAuthenticationStatus> CheckAuthenticationAsync(
        string? executablePath,
        IProcessRunner processRunner,
        IReadOnlyList<string> arguments,
        string probeDirectory,
        TimeSpan timeout,
        Func<ProcessResult, ProviderAuthenticationStatus> parseResult,
        CancellationToken cancellationToken)
    {
        if (executablePath is null)
        {
            return ProviderAuthenticationStatus.CheckFailed;
        }

        ProcessResult? result = await RunWithTimeoutAsync(
            processRunner,
            new(executablePath, arguments, probeDirectory),
            timeout,
            cancellationToken).ConfigureAwait(false);
        return result is null ? ProviderAuthenticationStatus.CheckFailed : parseResult(result);
    }

    private static async Task<bool?> CheckUpdateAvailableAsync(
        ProviderId id,
        Version localVersion,
        IProviderReleaseSource releaseSource,
        IProviderReleaseCache cache,
        IClock clock,
        bool bypassReleaseCache,
        CancellationToken cancellationToken)
    {
        ProviderReleaseCacheEntry? entry = bypassReleaseCache
            ? null
            : await cache.ReadAsync(id, cancellationToken).ConfigureAwait(false);
        if (entry is not null && !bypassReleaseCache && !IsStale(entry, clock.UtcNow))
        {
            return entry.Succeeded && Version.TryParse(entry.LatestVersion, out Version? cachedLatest)
                ? cachedLatest > localVersion
                : null;
        }

        ProviderReleaseLookupResult lookup =
            await FetchAndCacheAsync(id, releaseSource, cache, clock, cancellationToken).ConfigureAwait(false);
        return lookup.Succeeded && lookup.Version is not null ? lookup.Version > localVersion : null;
    }

    private static async Task<ProviderReleaseLookupResult> FetchAndCacheAsync(
        ProviderId id,
        IProviderReleaseSource releaseSource,
        IProviderReleaseCache cache,
        IClock clock,
        CancellationToken cancellationToken)
    {
        ProviderReleaseLookupResult lookup =
            await releaseSource.FetchLatestVersionAsync(cancellationToken).ConfigureAwait(false);
        await cache.WriteAsync(
            id,
            new(clock.UtcNow, lookup.Succeeded, lookup.Version?.ToString()),
            cancellationToken).ConfigureAwait(false);
        return lookup;
    }

    private static bool IsStale(ProviderReleaseCacheEntry entry, DateTimeOffset now)
    {
        TimeSpan window = entry.Succeeded ? ReleaseCheckSuccessWindow : ReleaseCheckFailureWindow;
        return now - entry.CheckedAt >= window;
    }

    /// <summary>Reads a fixed vendor-owned path and runs `--version`. Never touches the network.</summary>
    private static async Task<(ProviderStatus Status, Version? Version)> DiscoverLocalAsync(
        ProviderId id,
        ProviderInstallSpec spec,
        IProcessRunner processRunner,
        TimeSpan versionProbeTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!File.Exists(spec.ExecutablePath))
        {
            return (new(id, ProviderState.Missing, null, ProviderDiagnosticCodes.Missing), null);
        }

        ProcessResult? result = await RunWithTimeoutAsync(
            processRunner,
            new(spec.ExecutablePath, ["--version"], Path.GetDirectoryName(spec.ExecutablePath)!),
            versionProbeTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result is not { ExitCode: 0 })
        {
            return (new(id, ProviderState.Failed, null, ProviderDiagnosticCodes.UpdateFailed), null);
        }

        if (!TryParseVersion(result.StandardOutput, out Version? version))
        {
            return (new(id, ProviderState.Failed, null, ProviderDiagnosticCodes.UpdateFailed), null);
        }

        return spec.MinimumVersion is { } minimum && version < minimum
            ? (new(id, ProviderState.Failed, version.ToString(), ProviderDiagnosticCodes.VersionUnsupported), null)
            : (ProviderStatus.Ready(id, version.ToString()), version);
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
