using Forge.Application;
using Forge.Domain;
using Forge.Providers;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class ExecutionProfilePolicyTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(NodeRole.Planning, ExecutionPhase.Planning)]
    [InlineData(NodeRole.Implementation, ExecutionPhase.Implementation)]
    [InlineData(NodeRole.Review, ExecutionPhase.Review)]
    public void PhaseForMapsModelRolesToTheirPhase(NodeRole role, ExecutionPhase expected)
    {
        Assert.Equal(expected, ExecutionProfilePolicy.PhaseFor(role));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(NodeRole.Generic)]
    [InlineData(NodeRole.Intake)]
    [InlineData(NodeRole.Confirmation)]
    [InlineData(NodeRole.TestWork)]
    [InlineData(NodeRole.HumanApproval)]
    [InlineData(NodeRole.Finalization)]
    public void PhaseForReturnsNullForNonModelRoles(NodeRole role)
    {
        Assert.Null(ExecutionProfilePolicy.PhaseFor(role));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SelectReviewProviderPrefersAnIndependentLineageWhenOneExists()
    {
        (string provider, bool achievedIndependence) =
            ExecutionProfilePolicy.SelectReviewProvider(["claude_code", "codex"], "claude_code");

        Assert.Equal("codex", provider);
        Assert.True(achievedIndependence);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SelectReviewProviderFallsBackToTheSameProviderWhenNoneIsIndependent()
    {
        (string provider, bool achievedIndependence) =
            ExecutionProfilePolicy.SelectReviewProvider(["claude_code"], "claude_code");

        Assert.Equal("claude_code", provider);
        Assert.False(achievedIndependence);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FreezeProducesExactlyThreePhasesWithReviewLineageEvidence()
    {
        ProviderCatalog catalog = new(
            [
                new FakeLlmProvider(new ProviderId("claude_code"), ProviderState.Ready, "1.0.0"),
                new FakeLlmProvider(new ProviderId("codex"), ProviderState.Ready, "1.0.0"),
            ]);

        IReadOnlyDictionary<ExecutionPhase, ExecutionProfile> profiles = ExecutionProfilePolicy.Freeze(
            ["claude_code", "codex"],
            await ExecutionProfilePolicy.ResolveModelsAsync(
                ["claude_code", "codex"], catalog, TestContext.Current.CancellationToken));

        Assert.Equal(3, profiles.Count);
        Assert.Equal("claude_code", profiles[ExecutionPhase.Planning].Provider);
        Assert.Equal("claude_code-fake-model", profiles[ExecutionPhase.Planning].Model);
        Assert.Equal("claude_code", profiles[ExecutionPhase.Implementation].Provider);
        Assert.Equal("claude_code-fake-model", profiles[ExecutionPhase.Implementation].Model);
        Assert.Equal("codex", profiles[ExecutionPhase.Review].Provider);
        Assert.Equal("codex-fake-model", profiles[ExecutionPhase.Review].Model);
        Assert.Null(profiles[ExecutionPhase.Planning].Lineage);
        Assert.NotNull(profiles[ExecutionPhase.Review].Lineage);
        Assert.True(profiles[ExecutionPhase.Review].Lineage!.AchievedIndependence);
        Assert.Equal("claude_code", profiles[ExecutionPhase.Review].Lineage!.ImplementationProvider);
        Assert.Equal("claude_code-fake-model", profiles[ExecutionPhase.Review].Lineage!.ImplementationModel);
        Assert.All(
            profiles.Values,
            profile => Assert.Equal(
                [ContextCapabilityIds.GitShow, ContextCapabilityIds.GitGrep], profile.CapabilityAllowlist));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FreezeThrowsForAnEmptyProviderList()
    {
        ProviderCatalog catalog = new([]);
        IReadOnlyDictionary<string, string> models =
            await ExecutionProfilePolicy.ResolveModelsAsync([], catalog, TestContext.Current.CancellationToken);

        Assert.Throws<ArgumentException>(() => ExecutionProfilePolicy.Freeze([], models));
    }

    /// <summary>ADR 0063 made <see cref="ILlmProvider.DefaultModel"/> resolvable at runtime, so it can
    /// legitimately return a different value on two consecutive reads within one process. This freezes
    /// against a provider that changes on EVERY read: all four places one freeze records a model —
    /// three profiles plus the review lineage — must agree, because they are one decision about one
    /// sprint, and a lineage claiming the implementation ran on a model the implementation profile
    /// does not name is unreadable evidence.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task FreezeReadsEachProviderSModelOnceSoOneSprintCannotRecordTwoDifferentModels()
    {
        ProviderCatalog catalog = new([new ShiftingModelProvider(new ProviderId("codex"))]);

        IReadOnlyDictionary<ExecutionPhase, ExecutionProfile> profiles = ExecutionProfilePolicy.Freeze(
            ["codex"],
            await ExecutionProfilePolicy.ResolveModelsAsync(
                ["codex"], catalog, TestContext.Current.CancellationToken));

        Assert.Single(profiles.Values.Select(profile => profile.Model).Distinct());
        Assert.Equal(
            profiles[ExecutionPhase.Implementation].Model,
            profiles[ExecutionPhase.Review].Lineage!.ImplementationModel);
    }

    /// <summary>ADR 0063's unresolved-model sentinel is an ordinary non-empty model string as far as
    /// this policy is concerned, so a sprint created before any successful vendor probe still freezes a
    /// profile that satisfies `execution-profile.schema.json`'s `minLength: 1` on `model` (and on the
    /// lineage's `implementation_model`) — the reason the sentinel is a word rather than an empty
    /// string.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task FreezeProducesASchemaValidModelEvenWhenTheProviderHasNotResolvedOneYet()
    {
        ProviderCatalog catalog = new([new FixedModelProvider(new ProviderId("codex"), "vendor-default")]);

        IReadOnlyDictionary<ExecutionPhase, ExecutionProfile> profiles = ExecutionProfilePolicy.Freeze(
            ["codex"],
            await ExecutionProfilePolicy.ResolveModelsAsync(
                ["codex"], catalog, TestContext.Current.CancellationToken));

        Assert.All(profiles.Values, profile => Assert.NotEmpty(profile.Model));
        Assert.NotEmpty(profiles[ExecutionPhase.Review].Lineage!.ImplementationModel);
    }

    /// <summary>Round 3 review of PR #120: resolution must be triggered BY the sprint-creation path,
    /// not assumed to have already happened on a provider-capability pass — the Forge Host creates
    /// every Desktop and remote sprint and runs no such pass, so its own adapter instances are never
    /// refreshed by anything else. Against a provider that reports the unresolved sentinel until it
    /// is asked to refresh, the frozen model must be the resolved one, which is only possible if
    /// <see cref="ExecutionProfilePolicy.ResolveModelsAsync"/> refreshes before it reads.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveModelsRefreshesEachProviderBeforeReadingItsModel()
    {
        RefreshableModelProvider provider = new(new ProviderId("codex"));
        ProviderCatalog catalog = new([provider]);

        IReadOnlyDictionary<string, string> models = await ExecutionProfilePolicy.ResolveModelsAsync(
            ["codex", "codex"], catalog, TestContext.Current.CancellationToken);

        Assert.Equal("gpt-resolved", models["codex"]);
        // Once per DISTINCT provider, not once per frozen-candidate entry.
        Assert.Equal(1, provider.RefreshCalls);
        // Sprint creation honours the adapter's own throttle; only `forge models --refresh` bypasses it.
        Assert.False(provider.LastBypassedCache);
    }

    /// <summary>Reports a different model on every single read — the pathological end of what ADR 0063
    /// makes possible, so a single re-read anywhere in <see cref="ExecutionProfilePolicy.ResolveModelsAsync"/>
    /// plus <see cref="ExecutionProfilePolicy.Freeze"/> is caught rather than depending on a race
    /// actually occurring.</summary>
    private sealed class ShiftingModelProvider(ProviderId id) : StubProvider(id)
    {
        private int reads;

        public override string DefaultModel => $"model-{Interlocked.Increment(ref reads)}";
    }

    private sealed class FixedModelProvider(ProviderId id, string model) : StubProvider(id)
    {
        public override string DefaultModel => model;
    }

    /// <summary>Models a real adapter's shape: unresolved until something asks it to resolve, exactly
    /// like a <c>CodexLlmProvider</c> instance in a process that never ran a provider check.</summary>
    private sealed class RefreshableModelProvider(ProviderId id) : StubProvider(id)
    {
        private string model = "vendor-default";

        public int RefreshCalls { get; private set; }

        public bool LastBypassedCache { get; private set; } = true;

        public override string DefaultModel => model;

        public override Task RefreshDefaultModelAsync(bool bypassCache, CancellationToken cancellationToken)
        {
            RefreshCalls++;
            LastBypassedCache = bypassCache;
            model = "gpt-resolved";
            return Task.CompletedTask;
        }
    }

    /// <summary>Only <see cref="ILlmProvider.Id"/>, <see cref="ILlmProvider.DefaultModel"/>, and
    /// <see cref="ILlmProvider.RefreshDefaultModelAsync"/> matter to
    /// <see cref="ExecutionProfilePolicy"/>; it performs no other I/O at all.</summary>
    private abstract class StubProvider(ProviderId id) : ILlmProvider
    {
        public ProviderId Id => id;

        public abstract string DefaultModel { get; }

        public virtual Task RefreshDefaultModelAsync(bool bypassCache, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<ProviderStatus> DiscoverAsync(bool bypassReleaseCache, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProviderStatus> InstallOrUpdateAsync(bool bypassReleaseCache, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> ResolveExecutableAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProviderAuthenticationStatus> CheckAuthenticationAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProviderRunResult> RunAsync(
            string prompt,
            string workingDirectory,
            string? model,
            string? effort,
            CancellationToken cancellationToken,
            Func<AttemptActivityKind, CancellationToken, Task>? onActivity = null) =>
            throw new NotSupportedException();
    }
}
