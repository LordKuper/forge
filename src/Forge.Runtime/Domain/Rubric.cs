namespace Forge.Domain;

public enum RubricCategory
{
    Threat,
    Rule,
}

/// <summary>
/// One fixed catalog entry an intake or planning node assesses a change against — plain data, not
/// a persona or role. See docs/architecture/ai-agentic-software-development-workflow.md's
/// "evaluated patterns" table, which names importing a large role catalog as a rejected pattern;
/// the plan's own Stage 11 item text echoes it directly: "Use behavior nodes and rubric data, not
/// a seven-role catalog."
/// </summary>
public sealed record RubricItem(string Id, RubricCategory Category, string Description);

/// <summary>Whether one <see cref="RubricItem"/> applies to a specific change, and why.</summary>
public sealed record RubricAssessment(string ItemId, bool Applicable, string Rationale);

/// <summary>
/// Forge's built-in `implementation-critical` threat/rule rubric. Nothing evaluates this against a
/// real change yet — that needs an intake/planning node's executor, which lands with Stage 11's
/// execution-profile and provider-attempt work. This is the catalog it will assess against, the
/// same "shape now, producer later" precedent ADR 0009 used for <see cref="Handoff"/> and ADR 0012
/// used for context-manifest layers 2 and 3.
/// </summary>
public static class BuiltInRubric
{
    public static IReadOnlyList<RubricItem> Items { get; } =
    [
        new(
            "secret_exposure",
            RubricCategory.Threat,
            "Change could expose secrets, credentials, or tokens in logs, errors, or generated files."),
        new(
            "destructive_action",
            RubricCategory.Threat,
            "Change could delete, overwrite, or irreversibly alter user data or shared state."),
        new(
            "untrusted_input",
            RubricCategory.Threat,
            "Change parses or executes untrusted project text or provider output as if it were trusted."),
        new(
            "dependency_risk",
            RubricCategory.Threat,
            "Change adds or upgrades a dependency with a known critical vulnerability or unclear license."),
        new(
            "scope_creep",
            RubricCategory.Threat,
            "Change grows beyond the agreed scope or definition of done."),
        new(
            "portability",
            RubricCategory.Rule,
            "Neutral code stays cross-platform; OS-specific code stays in its named adapter boundary."),
        new(
            "implementation_first_testing",
            RubricCategory.Rule,
            "Implementation is confirmed against its definition of done before new tests are selected or authored."),
        new(
            "commit_and_version",
            RubricCategory.Rule,
            "Commits follow Conventional Commits and the branch's version bump matches the change."),
    ];
}
