using System.Text.Json.Serialization;

namespace Forge.Domain;

/// <summary>Capability ids a `ContextQueryPlan` operation requires in an execution profile's
/// `capability_allowlist` (ADR 0012). A distinct, dotted namespace from
/// <c>Forge.Presentation.CapabilityIds</c> — that class names Host-protocol capabilities
/// (`project.snapshot`, ...), not a model node's own sandboxed read capabilities.</summary>
public static class ContextCapabilityIds
{
    public const string GitShow = "context.git_show";
    public const string GitGrep = "context.git_grep";
}

/// <summary>One admitted manifest item: a document or query result, its content digest, the
/// tokens it was estimated to cost, and why it was selected (ADR 0012).</summary>
public sealed record ContextManifestItem(
    string RelativePath,
    string Digest,
    int EstimatedTokens,
    string Rationale);

/// <summary>One item dropped because it did not fit the remaining token budget (ADR 0012). Not an
/// error — a project with more rules/knowledge than its budget allows degrades by truncation, not
/// failure.</summary>
public sealed record ContextManifestTruncatedItem(
    string RelativePath,
    int EstimatedTokens,
    string Reason);

/// <summary>The four context-assembly layers (`docs/architecture/overview.md` "Context
/// assembly"). <see cref="SprintSpecifications"/> and <see cref="Handoffs"/> are always empty in
/// the MVP — Stage 11 first produces their content (ADR 0009, ADR 0012).</summary>
public sealed record ContextManifestLayers(
    IReadOnlyList<ContextManifestItem> Rules,
    IReadOnlyList<ContextManifestItem> SprintSpecifications,
    IReadOnlyList<ContextManifestItem> Knowledge,
    IReadOnlyList<ContextManifestItem> Handoffs,
    IReadOnlyList<ContextManifestItem> QueryResults);

/// <summary>A frozen, content-addressed, reproducible context manifest for one sprint (ADR 0012,
/// `context-manifest.schema.json`). <see cref="ManifestDigest"/> is a pure function of every other
/// field — never a timestamp or generator version — so an identical sprint state and token budget
/// always rebuilds the identical digest.</summary>
public sealed record ContextManifest(
    string SchemaVersion,
    // Raw `Guid`, not the strongly-typed `SprintId` wrapper every sprint-scoped domain record
    // (`NodeResult`, `Handoff`, `Finding`) otherwise uses — `ContextManifest` is serialized
    // directly via `StatusJson` rather than through a hand-written wire DTO, and `SprintId` has no
    // JSON converter registered, so the bare wrapper would serialize as a nested object instead of
    // the plain string `context-manifest.schema.json` expects. Matches `ProjectSnapshot.SprintId`'s
    // own precedent for a directly-serialized contract type.
    Guid SprintId,
    string SourceCommit,
    string Workflow,
    string WorkflowVersion,
    int TokenBudget,
    int AllocatedTokens,
    ContextManifestLayers Layers,
    IReadOnlyList<ContextManifestTruncatedItem> Truncated,
    string ManifestDigest)
{
    public const string ContractVersion = "1.0.0";
}

public enum ContextQueryOperationKind
{
    GitShow,
    GitGrep,
}

/// <summary>One bounded, read-only Git operation pinned to a plan's `source_commit` (ADR 0012).
/// <see cref="Path"/> is required for <see cref="ContextQueryOperationKind.GitShow"/>;
/// <see cref="Pattern"/> is required for <see cref="ContextQueryOperationKind.GitGrep"/>
/// (<see cref="PathScope"/> optionally narrows the grep to a subtree).</summary>
public sealed record ContextQueryOperation(
    string OperationId,
    ContextQueryOperationKind Kind,
    // Absent (not null) when serialized — `context-query-plan.schema.json` gives each of these no
    // `null` schema variant, matching `ExecutionProfile.Lineage`'s precedent for an optional field
    // outside `StatusJson`'s default "always write null" behavior.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Path = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Pattern = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PathScope = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxResultBytes = null);

/// <summary>A model-proposed, versioned, declarative plan of bounded read-only Git operations
/// (ADR 0012, `context-query-plan.schema.json`). Never a shell pipeline or model-authored script —
/// Forge validates and executes only these two operation kinds.</summary>
public sealed record ContextQueryPlan(
    string SchemaVersion,
    string SourceCommit,
    IReadOnlyList<ContextQueryOperation> Operations)
{
    public const string ContractVersion = "1.0.0";
}

/// <summary>Why a plan was rejected before any Git process ran (ADR 0012). One invalid or
/// unauthorized operation rejects the whole plan.</summary>
public enum ContextQueryPlanDiagnostic
{
    None,
    SchemaInvalid,
    DuplicateOperationId,
    PathUnsafe,
    CapabilityDenied,
}

/// <summary>Why one operation's own result carries no content (ADR 0012). Distinct from
/// <see cref="ContextQueryPlanDiagnostic"/>: this is a per-operation outcome of a validated,
/// executed plan, not a reason the plan itself was refused.</summary>
public enum ContextQueryOperationDiagnostic
{
    None,
    NotFound,
    Binary,
    ProcessFailed,
}

/// <summary>One operation's outcome. <see cref="Content"/> is populated only when
/// <see cref="Diagnostic"/> is <see cref="ContextQueryOperationDiagnostic.None"/> — it is
/// in-memory only and never part of the durable `context-result-bundle.schema.json` wire shape,
/// which records only <see cref="ContentDigest"/> and metadata (ADR 0012, matching
/// `handoff.schema.json`'s existing digest-only artifact shape).</summary>
public sealed record ContextQueryResult(
    string OperationId,
    ContextQueryOperationDiagnostic Diagnostic,
    [property: JsonIgnore] string? Content,
    string? ContentDigest,
    int ByteCount,
    bool Truncated);

/// <summary>The reproducible outcome of executing a validated <see cref="ContextQueryPlan"/> (ADR
/// 0012, `context-result-bundle.schema.json`). Replaying the same plan against the same
/// <see cref="SourceCommit"/> always reproduces byte-identical <see cref="ContextQueryResult"/>
/// content — Git's own content-addressed object store guarantees it — so only the plan and commit
/// need to be kept durably, not the content itself.</summary>
public sealed record ContextResultBundle(
    string SchemaVersion,
    string PlanDigest,
    string SourceCommit,
    IReadOnlyList<ContextQueryResult> Results)
{
    public const string ContractVersion = "1.0.0";
}

/// <summary>The outcome of validating a <see cref="ContextQueryPlan"/> (ADR 0012).
/// <see cref="Bundle"/> is populated only when <see cref="Diagnostic"/> is
/// <see cref="ContextQueryPlanDiagnostic.None"/>; validation never throws for an expected content
/// problem.</summary>
public sealed record ContextQueryPlanResult(ContextResultBundle? Bundle, ContextQueryPlanDiagnostic Diagnostic, string? Detail = null)
{
    public static ContextQueryPlanResult Rejected(ContextQueryPlanDiagnostic diagnostic, string detail) =>
        new(null, diagnostic, detail);
}
