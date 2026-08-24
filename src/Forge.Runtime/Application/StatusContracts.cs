using Forge.Domain;
using Forge.Providers;

namespace Forge.Application;

public enum AttentionPriority
{
    StartupBlocked,
    AwaitingHuman,
    Blocked,
    Failed,
    ReadyToFinalize,
    Resumable,
    NoSprints,
    Informational,
}

public enum SafetyClass
{
    Read,
    ConfirmMutation,
    HumanApproval,
}

public enum StaleBehavior
{
    RejectWithoutSideEffect,
    RefreshThenRead,
}

public sealed record ActionTarget(string Kind, string Id);

public sealed record ActionCommand(
    string Name,
    IReadOnlyDictionary<string, string> Arguments,
    Guid IdempotencyKey);

public sealed record SuggestedAction(
    string SchemaVersion,
    string ActionId,
    int Rank,
    string RationaleKey,
    IReadOnlyDictionary<string, string> RationaleArguments,
    IReadOnlyList<string> Preconditions,
    SafetyClass SafetyClass,
    ActionTarget Target,
    ActionCommand Command,
    long ExpectedStateVersion,
    StaleBehavior StaleBehavior);

public sealed record ProjectDescriptor(string Root, bool Initialized);

public sealed record SprintStatus(
    Guid Id,
    int CreationSequence,
    SprintState State,
    string Workflow,
    string BaseSha);

/// <summary>Matches `detail` in docs/contracts/v1/schemas/project-snapshot.schema.json. `Full`
/// requests the one named sprint's <see cref="SprintDetails"/> section (the active sprint when no
/// <c>sprint_id</c> is given); an explicit <c>sprint_id</c> attaches that section regardless of
/// this value.</summary>
public enum SnapshotDetail
{
    Summary,
    Full,
}

/// <summary>Matches `$defs.entity`: a small, uniform shape for the node/attempt/finding/gate/artifact
/// rows inside <see cref="SprintDetails"/>, so one presentation code path renders every kind.
/// <see cref="LastActivityAt"/> is only ever set for an attempt row (ADR 0006's throttled
/// activity heartbeat) — every other kind leaves it <see langword="null"/>. <see cref="Provider"/>/
/// <see cref="Model"/> are likewise only ever set for an attempt row (plan section 12.3's sticky
/// header), from <see cref="Forge.Domain.AttemptSnapshot.Provider"/>/<c>.Model</c> — every other
/// kind leaves both <see langword="null"/>.</summary>
public sealed record EntityStatus(
    string Id,
    string State,
    string? OwnerId = null,
    string? Kind = null,
    string? Severity = null,
    DateTimeOffset? UpdatedAt = null,
    DateTimeOffset? LastActivityAt = null,
    string? Provider = null,
    string? Model = null);

public sealed record RoutingStatus(int RetryRemaining, DateTimeOffset? ResumeNotBefore);

/// <summary>
/// The optional detail section for one sprint. `Gates` and `Artifacts` are always empty until
/// Stage 11 introduces human gates and an addressable artifact store; the schema already allows
/// (does not require) either array to hold entries. `RoutingStatus.ResumeNotBefore` is always
/// <see langword="null"/> until P8.42-P8.47 adds durable rate-limit scheduling.
/// </summary>
public sealed record SprintDetails(
    Guid SprintId,
    IReadOnlyList<EntityStatus> Nodes,
    IReadOnlyList<EntityStatus> Attempts,
    IReadOnlyList<EntityStatus> Findings,
    IReadOnlyList<EntityStatus> Gates,
    IReadOnlyList<EntityStatus> Artifacts,
    RoutingStatus Routing);

public sealed record ProjectSnapshot(
    string SchemaVersion,
    long StateVersion,
    DateTimeOffset GeneratedAt,
    ProjectDescriptor Project,
    StartupState Startup,
    Guid? ActiveSprintId,
    IReadOnlyList<SprintStatus> Sprints,
    IReadOnlyList<Guid> Attention,
    IReadOnlyList<SuggestedAction> SuggestedActions,
    // ADR 0005's "startup/provider... status... attached to their owners": both are always
    // present (never detail-gated) since StatusAdvisor already has them from the same startup
    // pass every snapshot request runs.
    IReadOnlyList<StartupCheck> StartupChecks,
    IReadOnlyList<ProviderHealthEntry> Providers,
    SnapshotDetail Detail = SnapshotDetail.Summary,
    // Unlike ActiveSprintId (which the schema declares nullable), `details` has no `null` variant —
    // only absent or a full object — so this one property must be omitted rather than written as
    // `null`, overriding StatusJson's otherwise-deliberate "always write nulls" policy.
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SprintDetails? Details = null);
