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

    /// <summary>The default-model probe runs the vendor's own diagnostic command, which is
    /// materially heavier than a `--version` or authentication probe — Codex 0.149.1's `codex doctor
    /// --json` runs two dozen checks including network reachability and measured 1.7-7.4 seconds.
    /// Sized for that, not for the 15-second probes above (ADR 0063).</summary>
    public static readonly TimeSpan DefaultModelProbeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A resolved model id becomes both a vendor command-line argument and durable sprint
    /// state, so it is bounded before it is ever used. 64 characters comfortably exceeds every slug
    /// either vendor publishes.</summary>
    private const int MaxModelNameLength = 64;

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
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        (ProviderStatus local, Version? localVersion) = await DiscoverLocalAsync(
            id,
            spec,
            processRunner,
            versionProbeTimeout,
            cancellationToken,
            environmentVariables).ConfigureAwait(false);
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
    /// update comparison. An already-usable install is updated only when a release check confirms
    /// a newer version — throttled to once per 24 hours on success and once per hour after a
    /// failed check unless <paramref name="bypassReleaseCache"/> is <see langword="true"/>
    /// (`forge models --refresh`), matching <see cref="DiscoverAsync"/>'s own cache policy. Either
    /// mutation is protected by <paramref name="installLock"/> and followed by a local-only
    /// recheck — never another network release check (ADR 0008).
    /// </summary>
    public static async Task<ProviderStatus> InstallOrUpdateAsync(
        ProviderId id,
        ProviderInstallSpec spec,
        IProcessRunner processRunner,
        IProviderReleaseSource releaseSource,
        IProviderReleaseCache cache,
        IProviderInstallLock installLock,
        IClock clock,
        bool bypassReleaseCache,
        TimeSpan versionProbeTimeout,
        TimeSpan installTimeout,
        TimeSpan installLockTimeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        (ProviderStatus local, Version? localVersion) = await DiscoverLocalAsync(
            id,
            spec,
            processRunner,
            versionProbeTimeout,
            cancellationToken,
            environmentVariables).ConfigureAwait(false);
        // DiscoverLocalAsync only ever returns Ready alongside a successfully parsed version.
        // targetVersion is not null exactly when this is a legitimate update of an already-Ready
        // install; null covers install/repair (missing, corrupt, or below the minimum version),
        // which skips the release comparison entirely (ADR 0008).
        Version? targetVersion = null;
        if (local.State == ProviderState.Ready && localVersion is { } version)
        {
            ProviderReleaseLookupResult lookup = await ResolveLatestReleaseAsync(
                id, releaseSource, cache, clock, bypassReleaseCache, cancellationToken).ConfigureAwait(false);
            if (!lookup.Succeeded)
            {
                // A release-check failure never blocks an otherwise-usable installed version.
                return local with { UpdateAvailable = null };
            }

            if (lookup.Version is null || lookup.Version <= version)
            {
                return local with { UpdateAvailable = false };
            }

            targetVersion = lookup.Version;
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

        // Another process may have already finished installing, repairing, or updating while we
        // waited on the lock — re-read local state once (no second network check for the update
        // case; the already-fetched targetVersion is still the comparison target) and skip a now-
        // redundant mutation for either the update path or the install/repair path.
        (ProviderStatus underLock, Version? lockedVersion) = await DiscoverLocalAsync(
            id,
            spec,
            processRunner,
            versionProbeTimeout,
            cancellationToken,
            environmentVariables).ConfigureAwait(false);
        if (underLock.State == ProviderState.Ready &&
            (targetVersion is null || (lockedVersion is { } current && targetVersion <= current)))
        {
            return underLock with { UpdateAvailable = false };
        }

        // Only a genuine update of an already-Ready install uses the vendor's lighter `update`
        // command; install/repair (targetVersion is null) always reruns the full installer, even
        // if a corrupt or unsupported executable happens to already exist at the target path.
        ProcessRequest request = targetVersion is not null && spec.UpdateArguments is { } updateArguments
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
                cancellationToken,
                environmentVariables).ConfigureAwait(false);
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
            cancellationToken,
            environmentVariables).ConfigureAwait(false);
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
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        if (executablePath is null)
        {
            return ProviderAuthenticationStatus.CheckFailed;
        }

        ProcessResult? result = await RunWithTimeoutAsync(
            processRunner,
            new(executablePath, arguments, probeDirectory, environmentVariables),
            timeout,
            cancellationToken).ConfigureAwait(false);
        return result is null ? ProviderAuthenticationStatus.CheckFailed : parseResult(result);
    }

    /// <summary>
    /// Asks one vendor which model a run started right now would actually use, throttled through
    /// <paramref name="cache"/> on the same 24h/1h cadence as the release check (ADR 0063). Returns
    /// the resolved model id, or <see langword="null"/> for every failure mode there is: no install,
    /// a non-zero exit, a timeout, an output shape <paramref name="parseResult"/> does not recognize,
    /// or a value that is not a usable model name. A cached failure is honoured within its own window
    /// rather than respawning the vendor process, exactly as a cached release-check failure is; a
    /// cached success is honoured only while its recorded model still passes
    /// <see cref="NormalizeModelName"/>.
    ///
    /// Only <paramref name="parseResult"/> ever sees raw process output, matching
    /// <see cref="CheckAuthenticationAsync"/> — a vendor diagnostic command's output routinely
    /// contains local paths and account detail, and none of it survives past that delegate.
    /// </summary>
    public static async Task<string?> ResolveDefaultModelAsync(
        ProviderId id,
        string? executablePath,
        IProcessRunner processRunner,
        IReadOnlyList<string> arguments,
        string probeDirectory,
        IProviderDefaultModelCache cache,
        IClock clock,
        bool bypassCache,
        TimeSpan timeout,
        Func<ProcessResult, string?> parseResult,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(parseResult);
        if (executablePath is null)
        {
            // Nothing to probe and nothing to learn: the cache is neither read nor written, so an
            // uninstalled provider never poisons a later, installed one's window.
            return null;
        }

        ProviderDefaultModelCacheEntry? entry = bypassCache
            ? null
            : await cache.ReadAsync(id, cancellationToken).ConfigureAwait(false);
        if (entry is not null && !IsStale(entry.CheckedAt, entry.Succeeded, clock.UtcNow))
        {
            // Revalidated on the way out as well as on the way in: the cache is an ordinary file a
            // user or another process can edit, and it must not be a path around the same checks a
            // freshly probed value passes.
            string? cached = entry.Succeeded ? NormalizeModelName(entry.Model) : null;
            if (cached is not null || !entry.Succeeded)
            {
                return cached;
            }

            // An entry claiming success whose model does NOT pass validation is corrupt, not a
            // recorded answer, so it does not earn the success window's 24 hours of silence either:
            // fall through to a fresh probe whose result overwrites it. That is what makes a
            // hand-edited or truncated file self-healing on the very next check instead of pinning
            // the provider to "unresolved" for a day.
        }

        ProcessResult? result = await RunWithTimeoutAsync(
            processRunner,
            new(executablePath, arguments, probeDirectory, environmentVariables),
            timeout,
            cancellationToken).ConfigureAwait(false);
        string? model = result is { ExitCode: 0 } ? NormalizeModelName(parseResult(result)) : null;
        await cache.WriteAsync(id, new(clock.UtcNow, model is not null, model), cancellationToken)
            .ConfigureAwait(false);
        return model;
    }

    /// <summary>
    /// The one gate every model id a vendor reports must pass before Forge puts it on a command line
    /// or freezes it into durable sprint state (ADR 0063). A model id is a single opaque token: it is
    /// trimmed, then required to be non-empty, free of embedded whitespace, no longer than
    /// <see cref="MaxModelNameLength"/>, and printable ASCII. Anything else is treated as a failed
    /// probe rather than passed through — there is no shell between here and the child process, so
    /// this is data hygiene on a durable value, not injection defence.
    /// </summary>
    public static string? NormalizeModelName(string? model)
    {
        if (model is null)
        {
            return null;
        }

        string trimmed = model.Trim();
        if (trimmed.Length is 0 or > MaxModelNameLength)
        {
            return null;
        }

        foreach (char character in trimmed)
        {
            if (character is < ' ' or > '~')
            {
                return null;
            }

            if (char.IsWhiteSpace(character))
            {
                return null;
            }
        }

        return trimmed;
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
        ProviderReleaseLookupResult lookup = await ResolveLatestReleaseAsync(
            id, releaseSource, cache, clock, bypassReleaseCache, cancellationToken).ConfigureAwait(false);
        return lookup.Succeeded && lookup.Version is not null ? lookup.Version > localVersion : null;
    }

    /// <summary>
    /// A recent cache entry is reused as-is (a cached failure is reported as failure, never
    /// retried within its own window); a missing, stale, or bypassed entry triggers one fresh
    /// network lookup, whose result is written back through <see cref="FetchAndCacheAsync"/> for
    /// the next caller. Shared by both the read-only availability check
    /// (<see cref="CheckUpdateAvailableAsync"/>) and <see cref="InstallOrUpdateAsync"/>'s own
    /// pre-mutation comparison, so both honor the same 24h/1h cache windows (ADR 0008).
    /// </summary>
    private static async Task<ProviderReleaseLookupResult> ResolveLatestReleaseAsync(
        ProviderId id,
        IProviderReleaseSource releaseSource,
        IProviderReleaseCache cache,
        IClock clock,
        bool bypassReleaseCache,
        CancellationToken cancellationToken)
    {
        ProviderReleaseCacheEntry? entry = bypassReleaseCache
            ? null
            : await cache.ReadAsync(id, cancellationToken).ConfigureAwait(false);
        if (entry is not null && !IsStale(entry, clock.UtcNow))
        {
            return new(
                entry.Succeeded,
                entry.Succeeded && Version.TryParse(entry.LatestVersion, out Version? cachedLatest)
                    ? cachedLatest
                    : null);
        }

        return await FetchAndCacheAsync(id, releaseSource, cache, clock, cancellationToken).ConfigureAwait(false);
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

    private static bool IsStale(ProviderReleaseCacheEntry entry, DateTimeOffset now) =>
        IsStale(entry.CheckedAt, entry.Succeeded, now);

    /// <summary>Shared verbatim by the release check and the default-model probe
    /// (<see cref="ResolveDefaultModelAsync"/>) — one cadence, deliberately, not an oversight
    /// (ADR 0063: both answer "what does the vendor say today", both are refreshed by the same
    /// provider-capability pass, and both are cheap to be a day stale, so a second pair of windows
    /// would only be a second thing for a user to reason about).</summary>
    private static bool IsStale(DateTimeOffset checkedAt, bool succeeded, DateTimeOffset now) =>
        now - checkedAt >= (succeeded ? ReleaseCheckSuccessWindow : ReleaseCheckFailureWindow);

    /// <summary>Reads a fixed vendor-owned path and runs `--version`. Never touches the network.</summary>
    private static async Task<(ProviderStatus Status, Version? Version)> DiscoverLocalAsync(
        ProviderId id,
        ProviderInstallSpec spec,
        IProcessRunner processRunner,
        TimeSpan versionProbeTimeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!File.Exists(spec.ExecutablePath))
        {
            return (new(id, ProviderState.Missing, null, ProviderDiagnosticCodes.Missing), null);
        }

        ProcessResult? result = await RunWithTimeoutAsync(
            processRunner,
            new(spec.ExecutablePath, ["--version"], Path.GetDirectoryName(spec.ExecutablePath)!, environmentVariables),
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
