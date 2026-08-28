namespace Forge.Providers;

/// <summary>
/// One provider/model quota reading's normalized, presentation-safe state (plan section 6.5). Every
/// value beyond <see cref="Unknown"/> requires a verified signal from the provider's own CLI/API --
/// see ADR 0052: no provider integration in this codebase exposes one, and none can appear without a
/// vendor publishing a structured quota API first, so <see cref="ProviderQuotaProjector"/>'s
/// <c>Project</c> overloads only ever produce <see cref="Unknown"/> (see that member's remarks: this
/// is a terminal reading, not an unfinished one). The
/// remaining members exist so the projection, the CLI row, and the Desktop status row all handle
/// every state the plan requires from the start, rather than special-casing "unknown" as the only
/// code path and leaving the others to be invented later under review pressure.
/// </summary>
public enum ProviderQuotaAvailability
{
    /// <summary>
    /// No quota limit data exists for this provider: neither shipped integration exposes a quota
    /// signal to read, and the projection issues no probe of its own -- nothing is ever asked, on
    /// this or any other pass (ADR 0052's investigation, re-confirmed by ADR 0061).
    /// <para>
    /// This is a TERMINAL state, not a pending one. It does not mean "not measured yet", "still
    /// loading", or "wiring incomplete", and nothing in this codebase will ever replace it with a
    /// different value while both providers remain as they are -- <see cref="ProviderQuotaProjector"/>
    /// has exactly one production factory and it hardcodes this member. A surface that reports
    /// "no limit data available" for it is therefore CORRECT and final: it must never render a
    /// spinner, a placeholder awaiting a value, a retry affordance, or wording that promises a later
    /// reading (the pre-ADR-0068 <c>QuotaStatusUnknown</c> text, "Quota status not yet available.",
    /// was exactly that defect). Only a provider vendor publishing a structured quota API -- which
    /// would extend the projector, not this contract -- can make any other member reachable.
    /// </para>
    /// </summary>
    Unknown,

    /// <summary>Verified remaining quota is comfortably above the provider's own warning threshold.</summary>
    Ready,

    /// <summary>Verified remaining quota is low enough that requests may be delayed or throttled.</summary>
    Limited,

    /// <summary>Verified quota is exhausted; the provider is not usable until it resets.</summary>
    Unavailable,

    /// <summary>A previously verified reading exists but is too old to trust as current (see
    /// <see cref="ProviderQuotaSnapshot.ObservedAt"/>).</summary>
    Stale,
}

/// <summary>
/// One provider/model's quota reading (plan section 6.5, ADR 0043/0052) -- distinct from
/// <see cref="ProviderHealthEntry"/> (toolchain install/authentication readiness) and from a
/// sprint's own retry budget (<c>RoutingStatus.RetryRemaining</c>): plan section 6.5's explicit
/// anti-requirement is "sprint retry budget is never presented as account quota," and this type
/// carries no relationship to routing/retry state at all. <see cref="RemainingAmount"/> and
/// <see cref="Unit"/> are only meaningful when a real signal exists; both are <see langword="null"/>
/// for <see cref="ProviderQuotaAvailability.Unknown"/>, matching the plan's "unknown quota is
/// rendered as unknown, never inferred."
/// </summary>
public sealed record ProviderQuotaSnapshot(
    string ProviderId,
    string? Model,
    ProviderQuotaAvailability Availability,
    double? RemainingAmount,
    string? Unit,
    DateTimeOffset? ResetAt,
    DateTimeOffset ObservedAt,
    string DiagnosticCode);

/// <summary>The `provider.quota_status` root envelope, matching every other versioned machine
/// contract's own schema-version-plus-list shape (<see cref="ProviderHealth"/>,
/// <c>ProjectSnapshot</c>).</summary>
public sealed record ProviderQuotaStatus(string SchemaVersion, IReadOnlyList<ProviderQuotaSnapshot> Providers)
{
    public const string ContractVersion = "1.0.0";
}

/// <summary>
/// Projects a toolchain status plus a provider catalog onto the quota contract, purely and without
/// any new probe -- mirroring <see cref="ProviderHealthProjector"/>'s own shape exactly. ADR 0052
/// records the investigation this projector's behavior follows from: neither Claude Code's CLI
/// (<c>claude auth status --json</c>, an undocumented shape already treated as unreliable by
/// <c>ClaudeLlmProvider.ParseAuthenticationStatus</c>) nor Codex's CLI (<c>codex login status</c>,
/// scriptable by exit code only) exposes structured account/model quota data. The only existing
/// quota-shaped signal in this codebase, <see cref="ProviderExecution"/>'s best-effort keyword match
/// over a failed run's stderr text, classifies a failure after the fact -- it is not a verified
/// remaining-amount/unit/reset-time reading and is deliberately not used here (fabricating a number
/// from it would violate the plan's own "unknown quota is rendered as unknown, never inferred").
/// <para>
/// Consumer contract: every snapshot this class produces reports
/// <see cref="ProviderQuotaAvailability.Unknown"/> with no remaining amount, unit, or reset time,
/// and that is the terminal, expected reading for both shipped providers -- see that member's own
/// remarks. There is one production factory (<c>Unverified</c>) and no other production code path
/// constructs a <see cref="ProviderQuotaSnapshot"/> at all, so a consumer needs no "still loading"
/// branch and must not invent one.
/// </para>
/// </summary>
public static class ProviderQuotaProjector
{
    /// <summary>Every enabled provider (from <paramref name="status"/>) plus every registered-but-disabled
    /// provider (from <paramref name="catalog"/>) always projects as <see cref="ProviderQuotaAvailability.Unknown"/>
    /// (see the type's own remarks). <paramref name="observedAt"/> is the caller's current time
    /// (<c>IClock.UtcNow</c>) -- this method takes no clock dependency itself, keeping it as pure as
    /// <see cref="ProviderHealthProjector.Project"/>. Issues no probe of its own, but a fresh
    /// <paramref name="status"/> requires one from the caller (<see cref="IProviderToolchainManager.CheckAsync"/>)
    /// -- a caller that already holds a merged <see cref="ProviderHealthEntry"/> set from an earlier
    /// probe this render pass (e.g. <c>WorkspaceSummaryProjector.CreateAsync</c>) should call this
    /// class's other <c>Project</c> overload (taking an <see cref="IReadOnlyCollection{T}"/> of
    /// <see cref="ProviderHealthEntry"/>) instead, to avoid re-probing (PR #100 review).</summary>
    public static IReadOnlyList<ProviderQuotaSnapshot> Project(
        ProviderToolchainStatus status, ProviderCatalog catalog, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(catalog);
        HashSet<string> discovered = new(status.Providers.Select(provider => provider.Id.Value), StringComparer.Ordinal);
        return
        [
            .. status.Providers.Select(provider =>
                Unverified(provider.Id, ResolveModel(catalog, provider.Id), observedAt)),
            .. catalog.Providers
                .Where(provider => !discovered.Contains(provider.Id.Value))
                .Select(provider => Unverified(provider.Id, provider.DefaultModel, observedAt)),
        ];
    }

    /// <summary>Same projection as this class's other <c>Project</c> overload, sourced from an
    /// already-computed <see cref="ProviderHealthEntry"/> set (itself
    /// <see cref="ProviderHealthProjector.Project"/>'s own enabled-plus-disabled union) instead of a
    /// fresh <see cref="ProviderToolchainStatus"/> probe -- for a caller that already paid for one
    /// toolchain check this render pass and must not issue a second (PR #100 review finding 1:
    /// <c>SidebarViewModel.LoadAsync</c> previously called <see cref="ProviderToolchainManager.CheckAsync"/>
    /// a second time here on every render, on top of the one <c>WorkspaceSummaryProjector.CreateAsync</c>
    /// already ran). <paramref name="catalog"/> still fills in any registered provider missing from
    /// <paramref name="providers"/> (e.g. an empty <paramref name="providers"/> set), matching the
    /// other overload's own disabled-provider fallback.</summary>
    public static IReadOnlyList<ProviderQuotaSnapshot> Project(
        IReadOnlyCollection<ProviderHealthEntry> providers, ProviderCatalog catalog, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(catalog);
        HashSet<string> discovered = new(providers.Select(provider => provider.Id), StringComparer.Ordinal);
        return
        [
            .. providers.Select(provider =>
                Unverified(new ProviderId(provider.Id), ResolveModel(catalog, new ProviderId(provider.Id)), observedAt)),
            .. catalog.Providers
                .Where(provider => !discovered.Contains(provider.Id.Value))
                .Select(provider => Unverified(provider.Id, provider.DefaultModel, observedAt)),
        ];
    }

    private static string? ResolveModel(ProviderCatalog catalog, ProviderId id) =>
        catalog.TryGet(id, out ILlmProvider? provider) ? provider.DefaultModel : null;

    private static ProviderQuotaSnapshot Unverified(ProviderId id, string? model, DateTimeOffset observedAt) =>
        new(id.Value, model, ProviderQuotaAvailability.Unknown, null, null, null, observedAt, ProviderDiagnosticCodes.QuotaUnknown);
}

/// <summary>
/// Aggregates a multi-provider quota reading into the single worst-case state the sidebar/status-row
/// (plan section 4.1) reports, so one degraded provider's quota is never hidden behind an otherwise
/// unremarkable or merely-unknown majority. Shared by <c>SurfaceFormatting.QuotaStatusSummary</c>
/// (Desktop/CLI text) and <c>CliApplication</c>'s own diagnostic reporting for `forge models quota`.
/// </summary>
public static class ProviderQuotaAggregation
{
    // Every named ProviderQuotaAvailability member has its own explicit arm; the compiler cannot
    // make an enum switch exhaustive against a genuinely new member (enums are not closed types at
    // the CLR level -- see SurfaceFormatting.QuotaStatusSummary's own remarks), so the fallback
    // throws instead of silently ranking an unmapped value as least-severe (PR #100 review,
    // non-blocking finding).
    private static int Severity(ProviderQuotaAvailability availability) => availability switch
    {
        ProviderQuotaAvailability.Unavailable => 4,
        ProviderQuotaAvailability.Limited => 3,
        ProviderQuotaAvailability.Stale => 2,
        ProviderQuotaAvailability.Unknown => 1,
        ProviderQuotaAvailability.Ready => 0,
        _ => throw new ArgumentOutOfRangeException(
            nameof(availability), availability, "Unmapped ProviderQuotaAvailability value."),
    };

    /// <summary>The most severe <see cref="ProviderQuotaAvailability"/> present, or
    /// <see cref="ProviderQuotaAvailability.Unknown"/> for an empty list (no provider registered at
    /// all is itself an unknown quota picture, not a "ready" one).</summary>
    public static ProviderQuotaAvailability Worst(IReadOnlyList<ProviderQuotaSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        return snapshots.Count == 0
            ? ProviderQuotaAvailability.Unknown
            : snapshots.Select(snapshot => snapshot.Availability).OrderByDescending(Severity).First();
    }

    /// <summary>The diagnostic code of the first entry matching <see cref="Worst"/> -- reused rather
    /// than recomputed, so the reported code always names one real entry that produced the aggregate
    /// state.</summary>
    public static string WorstDiagnosticCode(IReadOnlyList<ProviderQuotaSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0)
        {
            return ProviderDiagnosticCodes.QuotaUnknown;
        }

        ProviderQuotaAvailability worst = Worst(snapshots);
        return snapshots.First(snapshot => snapshot.Availability == worst).DiagnosticCode;
    }
}
