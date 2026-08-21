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
}
