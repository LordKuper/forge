using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.Application;
using Forge.Compiler;
using Forge.Domain;

namespace Forge.Providers;

/// <summary>A provider-neutral identifier (ADR 0008: "the core contains no provider enum, [or]
/// concrete provider identifier"). Each adapter owns its own id value (e.g. "codex",
/// "claude_code"); the core never hardcodes one.</summary>
[JsonConverter(typeof(ProviderIdJsonConverter))]
public sealed record ProviderId(string Value);

/// <summary>Serializes/deserializes <see cref="ProviderId"/> as a plain JSON string (its
/// <see cref="ProviderId.Value"/>), matching the flat `id` shape every other provider-facing
/// contract (e.g. <c>ProviderHealthEntry.Id</c>) already uses instead of a nested object.</summary>
public sealed class ProviderIdJsonConverter : JsonConverter<ProviderId>
{
    public override ProviderId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? throw new JsonException("A provider id must be a non-null string."));

    public override void Write(Utf8JsonWriter writer, ProviderId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

/// <summary>
/// Declares the same states as the `provider_toolchain` state machine in
/// `docs/contracts/v1/state-machines.json`. Install/update today is a single synchronous call
/// with no event bus to observe mid-flight, so only <see cref="Missing"/>, <see cref="Ready"/>,
/// and <see cref="Failed"/> are ever produced; <see cref="Installing"/>, <see cref="Updating"/>,
/// and <see cref="Rechecking"/> are reserved for future asynchronous progress reporting.
/// </summary>
public enum ProviderState
{
    Missing,
    Installing,
    Updating,
    Rechecking,
    Ready,
    Failed,
}

public static class ProviderDiagnosticCodes
{
    public const string None = "none";
    public const string Missing = "provider_missing";
    public const string VersionUnsupported = "provider_version_unsupported";
    public const string UpdateFailed = "provider_update_failed";

    /// <summary>ADR 0008: "Missing authentication blocks model work with
    /// `provider_authentication_required`." Produced by <see cref="ProviderAuthenticationStatus.Required"/>.</summary>
    public const string AuthenticationRequired = "provider_authentication_required";

    /// <summary>ADR 0008: "a probe failure uses `provider_authentication_check_failed`." Produced
    /// by <see cref="ProviderAuthenticationStatus.CheckFailed"/>.</summary>
    public const string AuthenticationCheckFailed = "provider_authentication_check_failed";

    /// <summary>A registered provider the user's `providers.enabled` selection excludes. ADR 0008:
    /// "a disabled provider is listed as disabled without probing it." Produced only by
    /// <see cref="ProviderHealthProjector.Project"/> for a catalog entry that never reached
    /// <see cref="ProviderToolchainStatus.Providers"/> — never by <see cref="ILlmProvider"/> itself,
    /// since a disabled provider is never probed.</summary>
    public const string Disabled = "provider_disabled";

    /// <summary>ADR 0006: "The durable outcome distinguishes provider_idle_timeout,
    /// provider_session_timeout, user cancellation, and ordinary provider failure." Produced when
    /// an <see cref="Forge.Application.AttemptSupervisor"/>'s idle deadline fires -- reserved in
    /// `docs/contracts/v1/README.md` since Stage 8, implemented here.</summary>
    public const string IdleTimeout = "provider_idle_timeout";

    /// <summary>See <see cref="IdleTimeout"/>; produced when the absolute session deadline fires
    /// instead.</summary>
    public const string SessionTimeout = "provider_session_timeout";

    /// <summary>ADR 0006's durable rate-limit wait (Stage 11, P11.48-P11.55): produced when an
    /// attempt is abandoned as a retryable rate limit (<see cref="ProviderFailureKind.RateLimited"/>)
    /// rather than an ordinary provider failure.</summary>
    public const string RateLimited = "provider_rate_limited";

    /// <summary>A model-bearing node executor's run-time mapping of the remaining
    /// <see cref="ProviderFailureKind"/> values (Stage 11's planning executor is the first
    /// production caller of <see cref="ILlmProvider.RunAsync"/>) — one durable diagnostic code per
    /// kind, so a recorded <see cref="Forge.Domain.NodeDiagnostic"/> names which failure actually
    /// happened rather than collapsing every non-timeout, non-rate-limit failure into one generic
    /// code.</summary>
    public const string RunNotReady = "provider_run_not_ready";

    public const string QuotaExceeded = "provider_quota_exceeded";

    /// <summary>ADR 0052: no provider integration in this codebase exposes a verified account/model
    /// quota signal today, so every <see cref="ProviderQuotaSnapshot"/> this codebase produces
    /// carries this code -- distinct from <see cref="QuotaExceeded"/>, which classifies an actual
    /// run failure's stderr text, not a queried quota reading.</summary>
    public const string QuotaUnknown = "provider_quota_unknown";

    public const string RunPolicyViolation = "provider_run_policy_violation";

    public const string RunTransientFailure = "provider_run_transient_failure";

    public const string RunMalformedOutput = "provider_run_malformed_output";

    public const string MissingTerminalResult = "provider_missing_terminal_result";

    public const string DuplicateTerminalResult = "provider_duplicate_terminal_result";

    public const string RunUnknownFailure = "provider_run_unknown_failure";

    /// <summary>The provider reported success (zero exit, exactly one terminal-result event) but
    /// that event's own extracted text was empty or whitespace-only — a schema-valid run with
    /// nothing a caller can actually use as a <see cref="Forge.Domain.Handoff"/> summary
    /// (`handoff.schema.json` requires `minLength: 1`). Distinct from
    /// <see cref="MissingTerminalResult"/>, which means no terminal event was ever emitted at
    /// all.</summary>
    public const string EmptyTerminalSummary = "provider_empty_terminal_summary";

    /// <summary>The review node executor's own outcome: the provider reported a schema-valid
    /// success with real terminal text, but that text's own last non-blank line was neither of the
    /// two verdict markers the review prompt requires (`APPROVED`/`CHANGES_REQUESTED`) — the
    /// provider did not follow the required output contract. Distinct from
    /// <see cref="EmptyTerminalSummary"/>, which means there was no usable text at all.</summary>
    public const string ReviewVerdictUnparseable = "provider_review_verdict_unparseable";
}

/// <summary>
/// <paramref name="UpdateAvailable"/> is populated only when the local probe succeeded and a
/// throttled or explicit release check actually ran; a release-check failure or skip (ADR 0008:
/// a release-check failure never blocks an otherwise-usable installed version) leaves it
/// <see langword="null"/> rather than guessing. <paramref name="Authentication"/> is
/// <see langword="null"/> only when no authentication check has run yet.
/// </summary>
public sealed record ProviderStatus(
    ProviderId Id,
    ProviderState State,
    string? Version,
    string DiagnosticCode,
    bool? UpdateAvailable = null,
    ProviderAuthenticationStatus? Authentication = null)
{
    public static ProviderStatus Ready(ProviderId id, string version) =>
        new(id, ProviderState.Ready, version, ProviderDiagnosticCodes.None);
}

/// <summary>
/// One provider's local authentication readiness (ADR 0008: "every enabled provider must report
/// local authentication readiness"). This proves provider-reported local readiness, not server
/// acceptance — a later live authentication failure during execution is a separate, routing-level
/// concern this type does not describe.
/// </summary>
public sealed record ProviderAuthenticationStatus(ProviderHealthAuthentication State, string DiagnosticCode)
{
    public static readonly ProviderAuthenticationStatus Ready =
        new(ProviderHealthAuthentication.Ready, ProviderDiagnosticCodes.None);

    public static readonly ProviderAuthenticationStatus Required =
        new(ProviderHealthAuthentication.Required, ProviderDiagnosticCodes.AuthenticationRequired);

    public static readonly ProviderAuthenticationStatus CheckFailed =
        new(ProviderHealthAuthentication.CheckFailed, ProviderDiagnosticCodes.AuthenticationCheckFailed);
}

public sealed record ProviderToolchainStatus(IReadOnlyList<ProviderStatus> Providers)
{
    /// <summary>ADR 0008: "Every enabled provider must report local authentication readiness" —
    /// a provider whose executable is ready but whose authentication is missing, unchecked, or
    /// unresolved does not count as ready for model work.</summary>
    public bool Ready => Providers.Count > 0 && Providers.All(provider =>
        provider.State == ProviderState.Ready &&
        provider.Authentication?.State == ProviderHealthAuthentication.Ready);

    public string DiagnosticCode =>
        Providers.FirstOrDefault(provider => provider.State != ProviderState.Ready)?.DiagnosticCode ??
            Providers.FirstOrDefault(provider =>
                provider.State == ProviderState.Ready &&
                provider.Authentication?.State is
                    ProviderHealthAuthentication.Required or ProviderHealthAuthentication.CheckFailed)
                ?.Authentication?.DiagnosticCode ??
            ProviderDiagnosticCodes.None;

    /// <summary>Maps to the shared <see cref="DiagnosticCodes"/> used by startup checks and CLI reporting.</summary>
    public string SharedDiagnosticCode
    {
        get
        {
            if (Ready)
            {
                return DiagnosticCodes.None;
            }

            bool needsRepair = Providers.Any(provider =>
                provider.DiagnosticCode is ProviderDiagnosticCodes.UpdateFailed or
                    ProviderDiagnosticCodes.VersionUnsupported);
            if (needsRepair)
            {
                return DiagnosticCodes.ProviderUpdateFailed;
            }

            // Authentication is only ever the blocker for a provider whose executable is
            // otherwise usable — an install/repair-needed provider already reported above.
            bool authenticationRequired = Providers.Any(provider =>
                provider.State == ProviderState.Ready &&
                provider.Authentication?.State == ProviderHealthAuthentication.Required);
            if (authenticationRequired)
            {
                return DiagnosticCodes.ProviderAuthenticationRequired;
            }

            bool authenticationCheckFailed = Providers.Any(provider =>
                provider.State == ProviderState.Ready &&
                provider.Authentication?.State == ProviderHealthAuthentication.CheckFailed);
            return authenticationCheckFailed
                ? DiagnosticCodes.ProviderAuthenticationCheckFailed
                : DiagnosticCodes.ProviderPreflightPending;
        }
    }
}

/// <summary>
/// One complete provider integration (ADR 0008): local discovery, install/update, and bounded
/// execution. Implementations never read project configuration — provider location, version, and
/// executable path are user/Forge-owned only — and never leak vendor identity beyond
/// <see cref="Id"/>.
/// </summary>
public interface ILlmProvider
{
    ProviderId Id { get; }

    /// <summary>The vendor's own default model id for an unattended, non-interactive run —
    /// vendor-owned, like <see cref="Id"/>; never chosen by neutral code (ADR 0008). Used to
    /// freeze an <c>ExecutionProfile.Model</c> for a sprint's chosen provider (Stage 11,
    /// P11.13-P11.20) — a fixed MVP default until per-project model selection exists.</summary>
    string DefaultModel { get; }

    /// <summary>
    /// Reads the fixed, vendor-owned install path and runs `--version`. When the local probe
    /// finds a usable install, also checks release-update availability: throttled to once per 24
    /// hours on success and once per hour after a failed check unless
    /// <paramref name="bypassReleaseCache"/> is <see langword="true"/> (`forge models --refresh`),
    /// which always fetches fresh but still only reports availability — it never installs
    /// anything (ADR 0008: "`forge models --refresh` bypasses the time limit but still checks
    /// availability before invoking an updater").
    /// </summary>
    Task<ProviderStatus> DiscoverAsync(bool bypassReleaseCache, CancellationToken cancellationToken);

    /// <summary>
    /// Ensures the provider is installed and current. A missing, corrupt, or unsupported install
    /// is an install/repair case and skips the update comparison; an already-usable install is
    /// updated only when a release check confirms a newer version — throttled to once per 24
    /// hours on success and once per hour after a failed check unless
    /// <paramref name="bypassReleaseCache"/> is <see langword="true"/> (`forge models --refresh`),
    /// matching <see cref="DiscoverAsync"/>'s own cache policy. Either path is protected by a
    /// per-user interprocess lock and followed by a local-only recheck — never another network
    /// release check.
    /// </summary>
    Task<ProviderStatus> InstallOrUpdateAsync(bool bypassReleaseCache, CancellationToken cancellationToken);

    /// <summary>The absolute path to the vendor-installed executable, or null when not installed.</summary>
    Task<string?> ResolveExecutableAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Checks local authentication readiness on the current executable (ADR 0008: "Forge checks
    /// authentication on the final executable at every startup"). Never initiates authentication
    /// and never persists or returns raw command output, identity, or credential material — only
    /// the normalized state.
    /// </summary>
    Task<ProviderAuthenticationStatus> CheckAuthenticationAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs one bounded, non-interactive prompt and returns its parsed, redacted result.
    /// <paramref name="onActivity"/>, when supplied, is invoked once per parsed provider event so
    /// the caller can record a safe, throttled attempt-activity update (ADR 0006) without this
    /// contract depending on how or how often that update is persisted.
    ///
    /// <paramref name="model"/> and <paramref name="effort"/> are the caller's already-frozen
    /// <c>ExecutionProfile.Model</c> and <c>ExecutionProfile.Effort</c> for this attempt (ADR 0062).
    /// Both are deliberately required rather than defaulted: a call site that forgets them would
    /// silently reintroduce the defect ADR 0062 fixes — a profile Forge froze, recorded, and showed
    /// the user, but never actually applied to the run. Pass <see langword="null"/> only for a run
    /// that genuinely has no frozen profile, which means "leave the vendor's own default alone";
    /// an adapter then sends no flag at all rather than an empty one. An adapter honours what its
    /// vendor can actually accept and never forwards a value verbatim on trust.
    /// </summary>
    Task<ProviderRunResult> RunAsync(
        string prompt,
        string workingDirectory,
        string? model,
        string? effort,
        CancellationToken cancellationToken,
        Func<AttemptActivityKind, CancellationToken, Task>? onActivity = null);
}

/// <summary>Aggregates every enabled provider into one toolchain-wide status (ADR 0008: a
/// disabled-but-registered provider is excluded before any probe, never merely left un-updated —
/// see <see cref="ProviderToolchainManager"/>).</summary>
public interface IProviderToolchainManager
{
    /// <summary>Cheap, read-only, offline. Never installs, updates, or repairs anything.</summary>
    Task<ProviderToolchainStatus> CheckAsync(CancellationToken cancellationToken);

    /// <summary>Re-checks every enabled provider and installs, repairs, or updates only when that
    /// check finds a missing/broken install or (subject to <paramref name="bypassReleaseCache"/>'s
    /// same 24h/1h cache policy as <see cref="ILlmProvider.InstallOrUpdateAsync"/>) a newer
    /// release, then rechecks authentication for all of them. Forge Host calls this with
    /// <see langword="false"/> on routine startup; `forge models --refresh` calls it with
    /// <see langword="true"/>.</summary>
    Task<ProviderToolchainStatus> EnsureReadyAsync(bool bypassReleaseCache, CancellationToken cancellationToken);
}

/// <summary>
/// The user's ordered `providers.enabled` selection (ADR 0008), read fresh on every call so a
/// runtime configuration change takes effect on the next probe. Narrow by design:
/// <see cref="ProviderToolchainManager"/> depends on exactly this one value, not the whole
/// configuration surface. <see langword="null"/> means the key was omitted (selects every
/// registered provider); a non-null, possibly-empty list is the user's exact enabled set.
/// </summary>
public interface IProviderEnablementSource
{
    Task<IReadOnlyList<string>?> GetEnabledIdsAsync(CancellationToken cancellationToken);
}

/// <summary>The outcome of asking a vendor for its latest published release version. A failure
/// (network error, unexpected response shape) is never fatal — the caller keeps the existing
/// installed version usable (ADR 0008).</summary>
public sealed record ProviderReleaseLookupResult(bool Succeeded, Version? Version)
{
    public static readonly ProviderReleaseLookupResult Failed = new(false, null);
}

/// <summary>
/// Fetches the latest published release version from one vendor's own release metadata (ADR
/// 0008: "Codex reads the vendor release metadata used by its own updater; Claude Code reads the
/// selected vendor channel metadata"). Each adapter owns its own endpoint and response format;
/// the core never touches a vendor URL.
/// </summary>
public interface IProviderReleaseSource
{
    Task<ProviderReleaseLookupResult> FetchLatestVersionAsync(CancellationToken cancellationToken);
}

/// <summary><paramref name="CheckedAt"/> anchors the 24-hour success / one-hour failure retry
/// windows (ADR 0008). <paramref name="LatestVersion"/> is only meaningful when
/// <paramref name="Succeeded"/>.</summary>
public sealed record ProviderReleaseCacheEntry(DateTimeOffset CheckedAt, bool Succeeded, string? LatestVersion);

/// <summary>A small per-user cache of the last release-availability check for one provider (ADR
/// 0008), so routine startup does not hit the network on every call. Read/write failures degrade
/// to "no cache" rather than throwing — a missing or corrupt cache just means the next check runs
/// fresh.</summary>
public interface IProviderReleaseCache
{
    Task<ProviderReleaseCacheEntry?> ReadAsync(ProviderId id, CancellationToken cancellationToken);

    Task WriteAsync(ProviderId id, ProviderReleaseCacheEntry entry, CancellationToken cancellationToken);
}

/// <summary>One generated provider-native integration file's content and metadata (ADR 0010).
/// <paramref name="SourceDigest"/> and <paramref name="PolicySnapshotHash"/> are copied verbatim
/// from the <see cref="Forge.Compiler.CanonicalIntegrationSource"/> that produced this artifact —
/// every provider's artifact for one generation pass shares the same two hashes, since they
/// describe the canonical source, not this vendor's specific file.</summary>
public sealed record GeneratedArtifact(
    ProviderId ProviderId,
    string RelativePath,
    string Content,
    string MediaType,
    string Audience,
    string? Language,
    string SourceDigest,
    string PolicySnapshotHash,
    string GeneratorVersion)
{
    /// <summary>Drift detection (ADR 0010): content-addressing already gives an exact answer, so
    /// this is the only comparison needed — unequal digests mean the canonical `.forge/` content
    /// or resolved policy changed since <paramref name="previousSourceDigest"/> was recorded.</summary>
    public bool HasDrifted(string previousSourceDigest) =>
        !string.Equals(SourceDigest, previousSourceDigest, StringComparison.Ordinal);
}

/// <summary>
/// Generates one vendor's native integration file from the provider-agnostic
/// <see cref="Forge.Compiler.CanonicalIntegrationSource"/> (ADR 0010). Each adapter owns exactly
/// one fact the core must not know: its vendor's well-known instructions-file name and whether
/// that file embeds the full canonical content or imports it — the same "core knows only the
/// neutral contract" split ADR 0008 established for <see cref="ILlmProvider"/>.
/// </summary>
public interface IProviderIntegrationGenerator
{
    ProviderId ProviderId { get; }

    GeneratedArtifact Generate(CanonicalIntegrationSource source);
}

/// <summary>A held install/update lock lease. Disposing releases it.</summary>
public interface IProviderInstallLease : IAsyncDisposable;

/// <summary>
/// The per-user interprocess lock ADR 0008 requires around provider install/update work, so two
/// concurrent Forge processes never run a vendor installer against the same executable at once.
/// One lock covers every provider — installs are already serialized within one process by
/// <see cref="ProviderToolchainManager"/>'s sequential loop, so this only ever contends across
/// processes, not across providers within one.
/// </summary>
public interface IProviderInstallLock
{
    Task<IProviderInstallLease?> TryAcquireAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
