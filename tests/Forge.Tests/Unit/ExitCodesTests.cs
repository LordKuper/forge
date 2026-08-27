using Forge.Application;
using Forge.Cli;

namespace Forge.UnitTests;

/// <summary>Round 2 review of PR #87: `ExitCodes.For`'s new ADR 0042 arm (`ModelPolicyViolation`)
/// had no direct test -- deleting it left the whole suite green, since nothing but
/// `docs/contracts/v1/README.md` pinned the claimed exit code. A pure function switching on a
/// string constant needs no CLI/TestEnvironment plumbing to verify directly.</summary>
public sealed class ExitCodesTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ModelPolicyViolationMapsToTheWorkflowExitCode() =>
        Assert.Equal(ExitCodes.Workflow, ExitCodes.For(DiagnosticCodes.ModelPolicyViolation));

    /// <summary>Round 3 review of PR #87: round 1 added a `ModelPolicyProviderUnknown => Configuration`
    /// arm and a matching README row while the check was still `Failed`; round 2 correctly changed
    /// the check to `Blocked` (an unmatched policy entry is legitimate, not an error) but never
    /// revisited the now-unreachable exit-code claim -- `forge eval`'s `CreateEvaluateCommand` only
    /// ever calls `Report` for a `Failed` check, so this code can never reach `ExitCodes.For` in
    /// production. Removed the dead arm and README row; this pins the honest current behavior (the
    /// generic `Internal` fallback) so a future reintroduction of that arm is a deliberate, reviewed
    /// choice rather than a silent reversion.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void ModelPolicyProviderUnknownIsNeverReportedSoItFallsBackToInternal() =>
        Assert.Equal(ExitCodes.Internal, ExitCodes.For(DiagnosticCodes.ModelPolicyProviderUnknown));

    /// <summary>Round 2 review of PR #96: the diagnostic a caller sees while a sprint's in-flight
    /// rewind has not yet converged (blocking further moves and finalization) is a workflow-state
    /// condition, the same category as <see cref="DiagnosticCodes.NoActiveOperation"/>/
    /// <see cref="DiagnosticCodes.ActiveOperationChanged"/>, not a generic internal error.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void StageTransitionRewindInProgressMapsToTheWorkflowExitCode() =>
        Assert.Equal(ExitCodes.Workflow, ExitCodes.For(DiagnosticCodes.StageTransitionRewindInProgress));

    /// <summary>Round 1 review of PR #102: <see cref="DiagnosticCodes.CapabilityNotSupported"/> (ADR
    /// 0053) had no `ExitCodes.For` case, so it fell through to `Internal` (13, "sanitized unexpected
    /// failure") instead of the client/Host-compatibility family `docs/contracts/v1/README.md` already
    /// reserves exit 14 for.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void CapabilityNotSupportedMapsToTheCompatibilityExitCode() =>
        Assert.Equal(ExitCodes.Compatibility, ExitCodes.For(DiagnosticCodes.CapabilityNotSupported));

    /// <summary>Round 1 review of PR #114: ADR 0057's <see cref="DiagnosticCodes.SprintTitleTooLong"/>
    /// had no `ExitCodes.For` case, so a rejected `--title` exited 13 ("sanitized unexpected failure")
    /// instead of the usage family every sibling bounded-input diagnostic
    /// (<see cref="DiagnosticCodes.UserMessageTooLong"/>,
    /// <see cref="DiagnosticCodes.SupersessionInstructionTooLong"/>) already maps to.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void SprintTitleTooLongMapsToTheUsageExitCode() =>
        Assert.Equal(ExitCodes.Usage, ExitCodes.For(DiagnosticCodes.SprintTitleTooLong));
}
