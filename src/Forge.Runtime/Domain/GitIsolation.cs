namespace Forge.Domain;

/// <summary>An isolated Git working tree Stage 7 owns: the sprint's single integration worktree,
/// or one worktree scoped to exactly one node's current write attempt.</summary>
public enum WorktreeKind
{
    Integration,
    Attempt,
}

/// <summary>How a completed provider call failed, so routing can tell a transient outage (retry,
/// may trip a breaker) apart from an authentication or policy failure (never retried, never counted
/// toward a breaker trip — see the architecture overview's "never disguised as transient").</summary>
public enum FailureClass
{
    Transient,
    Auth,
    Policy,
}

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen,
}

/// <summary>Identifies one routable target: a provider's model on a surface (e.g. interactive vs.
/// batch). Circuit breaker and retry-budget state are keyed by the canonical string form of this
/// tuple, never by free text.</summary>
public sealed record HealthKey(string Provider, string Model, string Surface)
{
    public string Canonical => $"{Provider}|{Model}|{Surface}";
}

/// <summary>Durable breaker state for one <see cref="HealthKey"/>, scoped to the owning sprint (see
/// <c>RoutingLedger</c> remarks for why breakers are sprint-scoped at MVP). <see cref="CircuitState.Open"/>
/// blocks routing until <see cref="CooldownUntil"/> elapses, at which point the next decision is
/// evaluated as <see cref="CircuitState.HalfOpen"/> — a single trial the caller's outcome then
/// either closes (success) or reopens with a fresh cooldown (failure).</summary>
public sealed record CircuitBreakerRecord(
    HealthKey Key,
    CircuitState State,
    int ConsecutiveFailures,
    DateTimeOffset? OpenedAt,
    DateTimeOffset? CooldownUntil,
    DateTimeOffset UpdatedAt);

/// <summary>One retry budget shared by every node/attempt in a sprint, so a flapping provider
/// cannot retry without bound even if each node's own attempt limit is respected individually.</summary>
public sealed record RetryBudgetRecord(SprintId SprintId, int Total, int Consumed)
{
    public int Remaining => Math.Max(0, Total - Consumed);
}

public enum RouteOutcome
{
    Routed,
    Succeeded,
    Failed,
    CircuitOpen,
    BudgetExhausted,
    Excluded,
}

/// <summary>An immutable record of one routing decision — reproducible, so a fallback sequence can
/// be explained after the fact from durable state alone rather than trusted from memory.</summary>
public sealed record RouteDecision(
    Guid DecisionId,
    SprintId SprintId,
    string NodeId,
    AttemptId AttemptId,
    HealthKey Key,
    RouteOutcome Outcome,
    FailureClass? FailureClass,
    DateTimeOffset DecidedAt);
