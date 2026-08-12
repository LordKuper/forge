using Forge.Domain;

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

public sealed record ProjectSnapshot(
    string SchemaVersion,
    long StateVersion,
    DateTimeOffset GeneratedAt,
    ProjectDescriptor Project,
    StartupState Startup,
    Guid? ActiveSprintId,
    IReadOnlyList<SprintStatus> Sprints,
    IReadOnlyList<Guid> Attention,
    IReadOnlyList<SuggestedAction> SuggestedActions);
