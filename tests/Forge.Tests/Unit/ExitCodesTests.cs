using Forge.Application;
using Forge.Cli;

namespace Forge.UnitTests;

/// <summary>Round 2 review of PR #87: `ExitCodes.For`'s two ADR 0042 arms
/// (`ModelPolicyViolation`/`ModelPolicyProviderUnknown`) had no direct test -- deleting either arm
/// left the whole suite green, since nothing but `docs/contracts/v1/README.md` pinned the claimed
/// exit code. A pure function switching on a string constant needs no CLI/TestEnvironment
/// plumbing to verify directly.</summary>
public sealed class ExitCodesTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ModelPolicyViolationMapsToTheWorkflowExitCode() =>
        Assert.Equal(ExitCodes.Workflow, ExitCodes.For(DiagnosticCodes.ModelPolicyViolation));

    [Fact]
    [Trait("Category", "Unit")]
    public void ModelPolicyProviderUnknownMapsToTheConfigurationExitCode() =>
        Assert.Equal(ExitCodes.Configuration, ExitCodes.For(DiagnosticCodes.ModelPolicyProviderUnknown));
}
