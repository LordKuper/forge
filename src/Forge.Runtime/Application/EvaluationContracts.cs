namespace Forge.Application;

/// <summary>ADR 0042's `forge eval` (`evaluation-result.schema.json`): a pass/fail report over the
/// updater, provider, bootstrap, and workflow subsystems plus the project model-policy gate. Every
/// check reuses an existing command's own logic (<see cref="StartupPipeline"/> for the first three
/// areas) rather than a second probing code path -- see <see cref="ForgeApplication.RunEvaluationAsync"/>.
/// </summary>
public sealed record EvaluationReport(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    EvaluationState State,
    IReadOnlyList<EvaluationCheck> Checks)
{
    public const string ContractVersion = "1.0.0";
}

public enum EvaluationArea
{
    Updater,
    Provider,
    Bootstrap,
    Workflow,
    ModelPolicy,
}

public enum EvaluationState
{
    Passed,
    Skipped,
    Blocked,
    Failed,
}

public sealed record EvaluationCheck(EvaluationArea Area, string Name, EvaluationState State, string DiagnosticCode)
{
    public static EvaluationCheck Passed(EvaluationArea area, string name) =>
        new(area, name, EvaluationState.Passed, DiagnosticCodes.None);
}
