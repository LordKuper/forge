using System.Text.Json;
using Forge.Application;
using Forge.Configuration;
using Forge.Domain;
using Forge.Providers;
using Forge.Providers.Codex;
using Forge.Tests.Support;
using Forge.UnitTests;

namespace Forge.ProviderAdapterTests;

public sealed class CodexLlmProviderTests
{
    /// <summary>An allowlist naming ONLY the model `codex doctor --json` resolves, so
    /// <c>ModelPolicyGate</c> refuses creation unless resolution actually happened first.</summary>
    private static readonly string[] ResolvedModelPolicy = ["codex:gpt-5.6-sol"];


    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverReportsMissingWithoutTheVendorExecutable()
    {
        using TestPaths paths = new();
        CodexLlmProvider provider = CreateProvider(paths, _ => new(0, "0.146.0", string.Empty));

        ProviderStatus status = await provider.DiscoverAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Missing, status.State);
        Assert.Equal(ProviderDiagnosticCodes.Missing, status.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverReportsReadyWhenThePinnedExecutableRunsSuccessfully()
    {
        using TestPaths paths = new();
        string executable = WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(paths, request =>
        {
            Assert.Equal(executable, request.FileName);
            // ADR 0063 added a second, independent probe to the same discovery pass; both run on
            // the same pinned executable and neither is a shell invocation.
            Assert.True(
                request.Arguments is ["--version"] or ["doctor", "--json"],
                $"Unexpected discovery probe: {string.Join(' ', request.Arguments)}");
            return new(0, "codex-cli 0.146.0", string.Empty);
        });

        ProviderStatus status = await provider.DiscoverAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Ready, status.State);
        Assert.Equal("0.146.0", status.Version);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverReportsFailedWhenThePinnedExecutableExitsNonZero()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(paths, _ => new(1, string.Empty, "boom"));

        ProviderStatus status = await provider.DiscoverAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
        Assert.Equal(ProviderDiagnosticCodes.UpdateFailed, status.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverReportsFailedWhenTheVersionOutputHasNoParsableVersion()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(paths, _ => new(0, "not a version", string.Empty));

        ProviderStatus status = await provider.DiscoverAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverReportsUpdateAvailableWhenTheReleaseSourceReportsANewerVersion()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(
            paths,
            _ => new(0, "0.146.0", string.Empty),
            releaseSource: new FakeReleaseSource(new(true, new Version(0, 147, 0))));

        ProviderStatus status = await provider.DiscoverAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Ready, status.State);
        Assert.True(status.UpdateAvailable);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverLeavesUpdateAvailableUnknownWhenTheReleaseCheckFails()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(paths, _ => new(0, "0.146.0", string.Empty));

        ProviderStatus status = await provider.DiscoverAsync(false, TestContext.Current.CancellationToken);

        // A release-check failure never blocks an otherwise-usable installed version.
        Assert.Equal(ProviderState.Ready, status.State);
        Assert.Null(status.UpdateAvailable);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateRunsTheNativeInstallerWhenMissing()
    {
        using TestPaths paths = new();
        bool ranInstaller = false;
        CodexLlmProvider provider = CreateProvider(paths, request =>
        {
            if (!ranInstaller)
            {
                Assert.Equal("powershell.exe", Path.GetFileName(request.FileName));
                Assert.True(Path.IsPathFullyQualified(request.FileName), "PowerShell must be launched by full path.");
                Assert.Contains("chatgpt.com/codex/install.ps1", request.Arguments[^1], StringComparison.Ordinal);
                Assert.Contains("CODEX_NON_INTERACTIVE", request.Arguments[^1], StringComparison.Ordinal);
                ranInstaller = true;
                // The real installer would have created the executable; the stub does the same.
                WriteCodexExecutable(paths);
                return new(0, string.Empty, string.Empty);
            }

            return new(0, "0.146.0", string.Empty);
        });

        ProviderStatus status = await provider.InstallOrUpdateAsync(
            bypassReleaseCache: true, TestContext.Current.CancellationToken);

        Assert.True(ranInstaller);
        Assert.Equal(ProviderState.Ready, status.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateFailsWhenTheInstallerExitsNonZero()
    {
        using TestPaths paths = new();
        CodexLlmProvider provider = CreateProvider(paths, _ => new(1, string.Empty, "install failed"));

        ProviderStatus status = await provider.InstallOrUpdateAsync(
            bypassReleaseCache: true, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
        Assert.Equal(ProviderDiagnosticCodes.UpdateFailed, status.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateSkipsWorkWhenAlreadyOnTheLatestVersion()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(
            paths,
            request =>
            {
                // The version probe is the only INSTALL-related process an already-current install
                // may spawn; ADR 0063's default-model probe rides the same pass and is excluded by
                // name rather than by count, so it can never mask a real install/update regression.
                Assert.True(
                    request.Arguments is ["--version"] or ["doctor", "--json"],
                    "No install/update process should run when already current.");
                return new(0, "0.146.0", string.Empty);
            },
            releaseSource: new FakeReleaseSource(new(true, new Version(0, 146, 0))));

        ProviderStatus status = await provider.InstallOrUpdateAsync(
            bypassReleaseCache: true, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Ready, status.State);
        Assert.False(status.UpdateAvailable);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateRunsTheInstallerAgainWhenANewerVersionIsAvailable()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        int processCalls = 0;
        CodexLlmProvider provider = CreateProvider(
            paths,
            request =>
            {
                // ADR 0063's default-model probe rides the same pass but is counted separately —
                // this test is about the install/update sequence, and folding an unrelated probe
                // into its count would make the count stop meaning anything.
                if (request.Arguments is ["doctor", "--json"])
                {
                    return new(0, string.Empty, string.Empty);
                }

                processCalls++;
                // 1: the initial local probe. 2: the re-probe taken right after the lock is
                // acquired (still OLD — no concurrent process updated it first). Both must report
                // the OLD version so a newer release actually looks newer. 3: the install script
                // rerun (exit code only matters). 4: the post-update recheck, reporting the new
                // version.
                return new(0, processCalls <= 2 ? "0.146.0" : "0.147.0", string.Empty);
            },
            releaseSource: new FakeReleaseSource(new(true, new Version(0, 147, 0))));

        ProviderStatus status = await provider.InstallOrUpdateAsync(
            bypassReleaseCache: true, TestContext.Current.CancellationToken);

        Assert.Equal(4, processCalls);
        Assert.Equal(ProviderState.Ready, status.State);
        Assert.False(status.UpdateAvailable);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateReportsUsableWithoutMutatingWhenTheLockCannotBeAcquired()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        int processCalls = 0;
        CodexLlmProvider provider = CreateProvider(
            paths,
            request =>
            {
                // See the sibling test: ADR 0063's probe is deliberately outside this count.
                if (request.Arguments is ["doctor", "--json"])
                {
                    return new(0, string.Empty, string.Empty);
                }

                processCalls++;
                return new(0, "0.146.0", string.Empty);
            },
            releaseSource: new FakeReleaseSource(new(true, new Version(0, 147, 0))),
            installLock: new FakeInstallLock(acquires: false));

        ProviderStatus status = await provider.InstallOrUpdateAsync(
            bypassReleaseCache: true, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Ready, status.State);
        // Exactly the local probe that established a newer release exists — never the actual
        // install/update command, since the lock could not be acquired.
        Assert.Equal(1, processCalls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverPropagatesAnAlreadyCancelledTokenRatherThanReportingFailed()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(paths, _ => new(0, "0.146.0", string.Empty));
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.DiscoverAsync(false, cancelled.Token));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverReportsFailedWhenTheVersionProbeExceedsItsTimeout()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = new(
            paths,
            new HangingProcessRunner(),
            new FakeReleaseSource(ProviderReleaseLookupResult.Failed),
            new FakeReleaseCache(),
            new FakeDefaultModelCache(),
            new FakeModelCatalogCache(),
            new FakeInstallLock(),
            new FakeClock(),
            versionProbeTimeout: TimeSpan.FromMilliseconds(50),
            defaultModelProbeTimeout: TimeSpan.FromMilliseconds(50));

        ProviderStatus status = await provider.DiscoverAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
        Assert.Equal(ProviderDiagnosticCodes.UpdateFailed, status.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task InstallOrUpdateReportsFailedWhenTheInstallerExceedsItsTimeout()
    {
        using TestPaths paths = new();
        CodexLlmProvider provider = new(
            paths,
            new HangingProcessRunner(),
            new FakeReleaseSource(ProviderReleaseLookupResult.Failed),
            new FakeReleaseCache(),
            new FakeDefaultModelCache(),
            new FakeModelCatalogCache(),
            new FakeInstallLock(),
            new FakeClock(),
            installTimeout: TimeSpan.FromMilliseconds(50),
            defaultModelProbeTimeout: TimeSpan.FromMilliseconds(50));

        ProviderStatus status = await provider.InstallOrUpdateAsync(
            bypassReleaseCache: true, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Failed, status.State);
        Assert.Equal(ProviderDiagnosticCodes.UpdateFailed, status.DiagnosticCode);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task CheckAuthenticationReflectsTheLoginStatusExitCode(int exitCode, bool expectedReady)
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(paths, request =>
        {
            Assert.Equal(["login", "status"], request.Arguments);
            return new(exitCode, string.Empty, string.Empty);
        });

        ProviderAuthenticationStatus status =
            await provider.CheckAuthenticationAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            expectedReady ? ProviderHealthAuthentication.Ready : ProviderHealthAuthentication.Required,
            status.State);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CheckAuthenticationReportsCheckFailedWithoutSpawningAProcessWhenNotInstalled()
    {
        bool spawned = false;
        using TestPaths paths = new();
        CodexLlmProvider provider = CreateProvider(paths, _ =>
        {
            spawned = true;
            return new(0, string.Empty, string.Empty);
        });

        ProviderAuthenticationStatus status =
            await provider.CheckAuthenticationAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderHealthAuthentication.CheckFailed, status.State);
        Assert.False(spawned);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncParsesDocumentedEventTypesWithoutShellInvocation()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        string jsonl = ReadFixture("codex-exec-json.jsonl");
        CodexLlmProvider provider = CreateProvider(paths, request =>
        {
            Assert.Equal("codex.exe", Path.GetFileName(request.FileName));
            Assert.Equal(["exec", "--json"], request.Arguments);
            Assert.Equal("list open bugs", request.StandardInput);
            return new(0, jsonl, string.Empty);
        });

        ProviderRunResult result = await provider.RunAsync(
            "list open bugs",
            "C:\\work",
            model: null,
            effort: null,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(4, result.Events.Count);
        Assert.Equal(ProviderEventKind.Unknown, result.Events[0].Kind);
        Assert.Equal(ProviderEventKind.Unknown, result.Events[1].Kind);
        Assert.Equal(ProviderEventKind.ToolUse, result.Events[2].Kind);
        Assert.Equal(ProviderEventKind.Result, result.Events[3].Kind);
        Assert.NotNull(result.TerminalResult);
    }

    [Theory]
    [Trait("Category", "Unit")]
    // ADR 0062. Codex takes its effort as a config override, not a dedicated flag.
    [InlineData("high", new[] { "-c", "model_reasoning_effort=high" })]
    [InlineData("medium", new[] { "-c", "model_reasoning_effort=medium" })]
    // No catalogued Codex model offers a tier below `low` or above `xhigh`, so both ends clamp into
    // the range every one of them accepts.
    [InlineData("none", new[] { "-c", "model_reasoning_effort=low" })]
    [InlineData("max", new[] { "-c", "model_reasoning_effort=xhigh" })]
    // Codex validates nothing here -- an unrecognized level reaches its API verbatim -- so an
    // unrecognized level must produce no override at all rather than a value the run would fail on.
    [InlineData("aggressive", new string[0])]
    [InlineData(null, new string[0])]
    public async Task RunAsyncAppliesTheFrozenEffortAsAConfigOverride(string? effort, string[] expectedTail)
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        IReadOnlyList<string>? capturedArguments = null;
        CodexLlmProvider provider = CreateProvider(paths, request =>
        {
            capturedArguments = request.Arguments;
            return new(0, """{"type":"turn.completed"}""", string.Empty);
        });

        await provider.RunAsync("prompt", "C:\\work", model: null, effort, TestContext.Current.CancellationToken);

        Assert.Equal(["exec", "--json", .. expectedTail], capturedArguments);
    }

    /// <summary>
    /// ADR 0063 replaces ADR 0062's "never send Codex a model flag" contract, and this test replaces
    /// the one that pinned it (`RunAsyncNeverSendsAModelFlagBecauseCodexHasNoStableModelNameToSend`).
    /// That contract existed only because <c>DefaultModel</c> was a hardcoded slug Codex rejects; now
    /// that the value is resolved from the user's own Codex configuration, the frozen model is sent as
    /// `-m` and the recorded profile becomes a fact rather than a prediction.
    ///
    /// The two suppressed values are the point of the theory. `vendor-default` means no probe has
    /// succeeded, and `gpt-5` is the placeholder every release up to v0.84.1 froze into Codex sprints
    /// and which Codex 0.149.1 rejects with `400 invalid_request_error` — sending either would fail a
    /// run that otherwise works. Both degrade to the exact pre-ADR-0063 command line.
    /// </summary>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("gpt-5.6-sol", new[] { "-m", "gpt-5.6-sol" })]
    [InlineData("  gpt-5.6-sol  ", new[] { "-m", "gpt-5.6-sol" })]
    [InlineData("vendor-default", new string[0])]
    [InlineData("gpt-5", new string[0])]
    [InlineData("", new string[0])]
    [InlineData(null, new string[0])]
    public async Task RunAsyncSendsTheFrozenModelExceptTheTwoValuesCodexCannotBeGiven(
        string? model, string[] expectedModelFlag)
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        IReadOnlyList<string>? capturedArguments = null;
        CodexLlmProvider provider = CreateProvider(paths, request =>
        {
            capturedArguments = request.Arguments;
            return new(0, """{"type":"turn.completed"}""", string.Empty);
        });

        await provider.RunAsync("prompt", "C:\\work", model, "high", TestContext.Current.CancellationToken);

        // The model flag precedes the effort override, and the effort override is unaffected either way.
        Assert.Equal(
            ["exec", "--json", .. expectedModelFlag, "-c", "model_reasoning_effort=high"], capturedArguments);
    }

    /// <summary>ADR 0063, against a real captured `codex doctor --json` (Codex CLI 0.149.1, with local
    /// paths replaced by placeholders): the resolved model is the one Codex reports at
    /// `checks["config.load"].details.model` — the CONFIG-resolved model, which is what a run started
    /// now would actually use — and it then reaches the run as `-m`, closing the loop this ADR
    /// exists for.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task DiscoverResolvesTheDefaultModelFromARealCodexDoctorCaptureAndSendsItOnTheNextRun()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        string doctor = ReadFixture("codex-doctor.json");
        IReadOnlyList<string>? capturedArguments = null;
        CodexLlmProvider provider = CreateProvider(paths, request => request.Arguments switch
        {
            ["doctor", "--json"] => new(0, doctor, string.Empty),
            ["--version"] => new(0, "codex-cli 0.149.1", string.Empty),
            _ => Capture(request),
        });

        Assert.Equal("vendor-default", provider.DefaultModel);
        await provider.DiscoverAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal("gpt-5.6-sol", provider.DefaultModel);

        await provider.RunAsync(
            "prompt", "C:\\work", provider.DefaultModel, "medium", TestContext.Current.CancellationToken);

        Assert.Equal(
            ["exec", "--json", "-m", "gpt-5.6-sol", "-c", "model_reasoning_effort=medium"], capturedArguments);

        ProcessResult Capture(ProcessRequest request)
        {
            capturedArguments = request.Arguments;
            return new(0, """{"type":"turn.completed"}""", string.Empty);
        }
    }

    /// <summary>
    /// Round 3 review of PR #120's own regression test, and the reason the interface gained
    /// <see cref="ILlmProvider.RefreshDefaultModelAsync"/>. The resolved model is per-INSTANCE
    /// in-memory state, and the Forge Host — the process that creates every Desktop and remote
    /// sprint — runs no provider-capability pass at all, so its own adapter instance is never touched
    /// by <see cref="CodexLlmProvider.DiscoverAsync"/> or
    /// <see cref="CodexLlmProvider.InstallOrUpdateAsync"/>. Resolution therefore has to be triggered
    /// by the sprint-creation path itself; every other test in this file resolves and freezes through
    /// one container that ran discovery first, which is exactly why none of them could catch this.
    ///
    /// So: an adapter instance nothing has ever discovered, a toolchain manager that can never touch
    /// it, and a direct <c>SprintOrchestrator.CreateSprintAsync</c> call. The frozen profiles must
    /// name the model `codex doctor --json` reports, and the allowlist deliberately names only that
    /// model — the unresolved sentinel would be refused by <c>ModelPolicyGate</c>, so creation
    /// succeeding proves the gate saw the resolved value too, not just the freeze.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task SprintCreationResolvesTheModelItselfOnAProviderNoDiscoveryPassEverTouched()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        string doctor = ReadFixture("codex-doctor.json");
        int probes = 0;
        CodexLlmProvider provider = CreateProvider(paths, request =>
        {
            Assert.Equal(["doctor", "--json"], request.Arguments);
            probes++;
            return new(0, doctor, string.Empty);
        });
        using TestEnvironment environment = new(
            providers: new FakeProviderToolchainManager(FakeProviderToolchainManager.Ready),
            llmProviders: [provider],
            providerEnablement: new FakeProviderEnablementSource(["codex"]));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, cancellationToken);
        Assert.True(init.Succeeded);
        ConfigurationWriteResult configured = await environment.Application.SetConfigurationAsync(
            ConfigurationScope.Project,
            environment.ProjectRoot,
            "models.allowed_models",
            JsonSerializer.SerializeToElement(ResolvedModelPolicy),
            cancellationToken);
        Assert.True(configured.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        // Nothing has probed this instance, and nothing in this container can: the sentinel here is
        // the Forge Host's own steady state, not a setup detail.
        Assert.Equal("vendor-default", provider.DefaultModel);
        Assert.Equal(0, probes);

        CreateSprintResult result = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken);

        // Asserted before the definition is loaded: without the refresh this is a
        // `model_policy_violation` and there is no sprint to load, and that diagnostic is the
        // readable failure rather than a null id further down.
        Assert.Equal(DiagnosticCodes.None, result.DiagnosticCode);
        Assert.True(result.Succeeded);
        Assert.Equal(1, probes);
        SprintDefinition? definition = await orchestrator.GetDefinitionAsync(
            environment.ProjectRoot, result.SprintId!, cancellationToken);
        Assert.All(definition!.ExecutionProfiles.Values, profile => Assert.Equal("gpt-5.6-sol", profile.Model));
        Assert.Equal(
            "gpt-5.6-sol",
            definition.ExecutionProfiles[ExecutionPhase.Review].Lineage!.ImplementationModel);
    }

    /// <summary>ADR 0063's safe-degradation regression test. Every way the vendor probe can fail — a
    /// non-zero exit, output that is not JSON, a missing or wrongly-typed node at each of the four
    /// levels of `checks.config.load.details.model`, and a value that is not a usable model id — must
    /// leave <c>DefaultModel</c> at the unresolved sentinel and leave the run's command line byte-for-byte
    /// identical to what it was before this ADR. A vendor shape surprise degrades; it never throws out
    /// of a routine provider check, and it never puts an unusable value on a command line or into
    /// durable sprint state.</summary>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(1, """{"checks":{"config.load":{"details":{"model":"gpt-5.6-sol"}}}}""")]
    [InlineData(0, "not json at all")]
    [InlineData(0, "")]
    [InlineData(0, "[]")]
    [InlineData(0, """{"codexVersion":"0.149.1"}""")]
    [InlineData(0, """{"checks":42}""")]
    [InlineData(0, """{"checks":{"auth.credentials":{}}}""")]
    [InlineData(0, """{"checks":{"config.load":"ok"}}""")]
    [InlineData(0, """{"checks":{"config.load":{"status":"ok"}}}""")]
    [InlineData(0, """{"checks":{"config.load":{"details":{"model provider":"openai"}}}}""")]
    [InlineData(0, """{"checks":{"config.load":{"details":{"model":null}}}}""")]
    [InlineData(0, """{"checks":{"config.load":{"details":{"model":123}}}}""")]
    [InlineData(0, """{"checks":{"config.load":{"details":{"model":""}}}}""")]
    [InlineData(0, """{"checks":{"config.load":{"details":{"model":"   "}}}}""")]
    [InlineData(0, """{"checks":{"config.load":{"details":{"model":"gpt 5.6 sol"}}}}""")]
    [InlineData(0, """{"checks":{"config.load":{"details":{"model":"gpt-5.6-sol\nrm -rf /"}}}}""")]
    [InlineData(
        0,
        """{"checks":{"config.load":{"details":{"model":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}}}""")]
    public async Task AnUnusableDoctorResponseLeavesTheModelUnresolvedAndTheRunUnchanged(
        int exitCode, string doctorOutput)
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        IReadOnlyList<string>? capturedArguments = null;
        CodexLlmProvider provider = CreateProvider(paths, request => request.Arguments switch
        {
            ["doctor", "--json"] => new(exitCode, doctorOutput, string.Empty),
            ["--version"] => new(0, "codex-cli 0.149.1", string.Empty),
            _ => Capture(request),
        });

        await provider.DiscoverAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal("vendor-default", provider.DefaultModel);

        await provider.RunAsync(
            "prompt", "C:\\work", provider.DefaultModel, "high", TestContext.Current.CancellationToken);

        Assert.Equal(["exec", "--json", "-c", "model_reasoning_effort=high"], capturedArguments);

        ProcessResult Capture(ProcessRequest request)
        {
            capturedArguments = request.Arguments;
            return new(0, """{"type":"turn.completed"}""", string.Empty);
        }
    }

    /// <summary>ADR 0063: with no vendor executable there is nothing to ask, so the probe is not
    /// attempted at all — not spawned and not cached, so an uninstalled provider can never write a
    /// failure entry that then throttles the first real probe after an install.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task TheDefaultModelProbeIsNeverAttemptedWithoutAnInstalledExecutable()
    {
        using TestPaths paths = new();
        bool spawned = false;
        FakeDefaultModelCache cache = new();
        CodexLlmProvider provider = CreateProvider(
            paths,
            _ =>
            {
                spawned = true;
                return new(0, string.Empty, string.Empty);
            },
            defaultModelCache: cache);

        await provider.DiscoverAsync(false, TestContext.Current.CancellationToken);

        Assert.False(spawned);
        Assert.Null(await cache.ReadAsync(CodexLlmProvider.Codex, TestContext.Current.CancellationToken));
        Assert.Equal("vendor-default", provider.DefaultModel);
    }

    /// <summary>ADR 0063's throttle, which is the entire reason the probe may ride every provider check:
    /// a fresh cached success is reused without spawning anything, a cached failure is honoured for its
    /// own shorter window rather than retried, a stale entry of either kind triggers exactly one fresh
    /// probe, and `--refresh` (`bypassCache`) always probes. The windows are deliberately the same
    /// 24h/1h pair the release check uses.</summary>
    [Theory]
    [Trait("Category", "Unit")]
    // A cached success inside its 24h window: reused, nothing spawned.
    [InlineData(true, "cached-model", 1, false, 0, "cached-model")]
    // The same entry past 24h: one fresh probe, which wins.
    [InlineData(true, "cached-model", 25, false, 1, "gpt-5.6-sol")]
    // A cached failure inside its 1h window: honoured, nothing spawned, still unresolved.
    [InlineData(false, null, 0, false, 0, "vendor-default")]
    // The same failure past 1h: retried exactly once.
    [InlineData(false, null, 2, false, 1, "gpt-5.6-sol")]
    // `--refresh` ignores a perfectly fresh entry.
    [InlineData(true, "cached-model", 1, true, 1, "gpt-5.6-sol")]
    public async Task TheDefaultModelProbeIsThrottledOnTheSameCadenceAsTheReleaseCheck(
        bool cachedSuccess,
        string? cachedModel,
        int hoursSinceCached,
        bool bypassCache,
        int expectedProbes,
        string expectedModel)
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        FakeClock clock = new() { UtcNow = DateTimeOffset.UnixEpoch.AddDays(30) };
        FakeDefaultModelCache cache = new();
        await cache.WriteAsync(
            CodexLlmProvider.Codex,
            new(clock.UtcNow.AddHours(-hoursSinceCached), cachedSuccess, cachedModel),
            TestContext.Current.CancellationToken);

        int probes = 0;
        CodexLlmProvider provider = CreateProvider(
            paths,
            request =>
            {
                if (request.Arguments is not ["doctor", "--json"])
                {
                    return new(0, "codex-cli 0.149.1", string.Empty);
                }

                probes++;
                return new(0, """{"checks":{"config.load":{"details":{"model":"gpt-5.6-sol"}}}}""", string.Empty);
            },
            defaultModelCache: cache,
            clock: clock);

        await provider.RefreshDefaultModelAsync(bypassCache, TestContext.Current.CancellationToken);

        Assert.Equal(expectedProbes, probes);
        Assert.Equal(expectedModel, provider.DefaultModel);
    }

    /// <summary>ADR 0063's "validated on the way out of the cache as well as on the way in", proven
    /// rather than claimed. The cache is an ordinary JSON file that a user, another process, or a
    /// truncated write can leave claiming success while carrying a model id that is not usable — every
    /// shape the live probe's own failure-mode test already rejects. Such an entry must be rejected on
    /// the same terms (sentinel, no `-m`, never trusted for its flag alone) AND must not earn the 24h
    /// success window a genuine answer earns: it is re-probed once and overwritten, so a corrupt file
    /// self-heals on the next check instead of pinning the provider to the sentinel for a day.</summary>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(null, false, "vendor-default")]
    [InlineData("", false, "vendor-default")]
    [InlineData("   ", false, "vendor-default")]
    [InlineData("gpt 5.6 sol", false, "vendor-default")]
    [InlineData("gpt-5.6-sol\nrm -rf /", false, "vendor-default")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false, "vendor-default")]
    // The same corrupt entry when the vendor does answer: the one fresh probe replaces it outright.
    [InlineData("", true, "gpt-5.6-sol")]
    public async Task ACachedSuccessWhoseModelIsUnusableIsRejectedAndReProbedRatherThanTrusted(
        string? cachedModel, bool probeSucceeds, string expectedModel)
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        FakeClock clock = new() { UtcNow = DateTimeOffset.UnixEpoch.AddDays(30) };
        FakeDefaultModelCache cache = new();
        // One hour old: comfortably inside the 24h window a genuine cached success would be honoured for.
        await cache.WriteAsync(
            CodexLlmProvider.Codex,
            new(clock.UtcNow.AddHours(-1), true, cachedModel),
            TestContext.Current.CancellationToken);

        int probes = 0;
        IReadOnlyList<string>? capturedArguments = null;
        CodexLlmProvider provider = CreateProvider(
            paths,
            request =>
            {
                switch (request.Arguments)
                {
                    case ["doctor", "--json"]:
                        probes++;
                        return probeSucceeds
                            ? new(
                                0,
                                """{"checks":{"config.load":{"details":{"model":"gpt-5.6-sol"}}}}""",
                                string.Empty)
                            : new(1, string.Empty, string.Empty);
                    case ["--version"]:
                        return new(0, "codex-cli 0.149.1", string.Empty);
                    default:
                        capturedArguments = request.Arguments;
                        return new(0, """{"type":"turn.completed"}""", string.Empty);
                }
            },
            defaultModelCache: cache,
            clock: clock);

        await provider.RefreshDefaultModelAsync(bypassCache: false, TestContext.Current.CancellationToken);

        Assert.Equal(1, probes);
        Assert.Equal(expectedModel, provider.DefaultModel);

        // Overwritten with this probe's own real outcome, so a failure now retries on the SHORT window
        // rather than the corrupt entry's remaining 23 hours of undeserved grace.
        ProviderDefaultModelCacheEntry? rewritten =
            await cache.ReadAsync(CodexLlmProvider.Codex, TestContext.Current.CancellationToken);
        Assert.NotNull(rewritten);
        Assert.Equal(clock.UtcNow, rewritten.CheckedAt);
        Assert.Equal(probeSucceeds, rewritten.Succeeded);

        await provider.RunAsync(
            "prompt", "C:\\work", provider.DefaultModel, "high", TestContext.Current.CancellationToken);

        string[] expectedArguments = probeSucceeds
            ? ["exec", "--json", "-m", "gpt-5.6-sol", "-c", "model_reasoning_effort=high"]
            : ["exec", "--json", "-c", "model_reasoning_effort=high"];
        Assert.Equal(expectedArguments, capturedArguments);
    }

    /// <summary>ADR 0063: a probe that fails AFTER one has succeeded must not un-resolve the model.
    /// Retry cadence is the cache's job; the last known-good value stays in force for the process
    /// lifetime, so one flaky vendor invocation cannot silently drop a whole session back to the
    /// unresolved sentinel — and back to sending no `-m`.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ALaterFailedProbeKeepsTheLastSuccessfullyResolvedModel()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        bool firstProbe = true;
        CodexLlmProvider provider = CreateProvider(paths, request =>
        {
            if (request.Arguments is not ["doctor", "--json"])
            {
                return new(0, "codex-cli 0.149.1", string.Empty);
            }

            if (firstProbe)
            {
                firstProbe = false;
                return new(0, """{"checks":{"config.load":{"details":{"model":"gpt-5.6-sol"}}}}""", string.Empty);
            }

            return new(1, string.Empty, "doctor exploded");
        });

        await provider.RefreshDefaultModelAsync(bypassCache: true, TestContext.Current.CancellationToken);
        Assert.Equal("gpt-5.6-sol", provider.DefaultModel);

        await provider.RefreshDefaultModelAsync(bypassCache: true, TestContext.Current.CancellationToken);

        Assert.Equal("gpt-5.6-sol", provider.DefaultModel);
    }

    /// <summary>ADR 0066, against the real thing: `codex-debug-models.json` is a verbatim
    /// `codex debug models` catalog recorded from Codex CLI 0.149.1, with each entry's bulky
    /// prompt-template payload removed and nothing else changed. The mapping is pinned by it: only the
    /// entries Codex itself marks `"visibility": "list"` are offered, the two `hide` entries
    /// (`gpt-reserve`, `codex-auto-review`) are not, and the vendor's own order survives — nothing is
    /// re-sorted on `priority`, which ADR 0063 already established is not a key.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListModelsReturnsTheListedSlugsFromARealRecordedCatalogInVendorOrder()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        string catalog = ReadFixture("codex-debug-models.json");
        CodexLlmProvider provider = CreateProvider(paths, request =>
        {
            Assert.Equal(["debug", "models"], request.Arguments);
            return new(0, catalog, string.Empty);
        });

        IReadOnlyList<string> models = await provider.ListModelsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.5", "gpt-5.4", "gpt-5.4-mini"], models);
    }

    /// <summary>ADR 0066's safe-degradation rule, the enumeration counterpart of
    /// <see cref="AnUnusableDoctorResponseLeavesTheModelUnresolvedAndTheRunUnchanged"/>. Every way the
    /// catalog probe can fail — a non-zero exit, output that is not JSON, a missing or wrongly-typed
    /// `models` node, entries that are not objects or carry no usable `slug`/`visibility`, and a
    /// catalog whose only listed slug fails model-name validation — must yield an EMPTY list. Empty is
    /// the contract's "could not be enumerated", which callers fall through on rather than treating as
    /// "this provider offers nothing"; a vendor shape surprise never throws out of an enumeration.</summary>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(1, """{"models":[{"slug":"gpt-5.6-sol","visibility":"list"}]}""")]
    [InlineData(0, "not json at all")]
    [InlineData(0, "")]
    [InlineData(0, "[]")]
    [InlineData(0, """{"codexVersion":"0.149.1"}""")]
    [InlineData(0, """{"models":42}""")]
    [InlineData(0, """{"models":[]}""")]
    [InlineData(0, """{"models":["gpt-5.6-sol"]}""")]
    [InlineData(0, """{"models":[{"slug":"gpt-5.6-sol"}]}""")]
    [InlineData(0, """{"models":[{"visibility":"list"}]}""")]
    [InlineData(0, """{"models":[{"slug":42,"visibility":"list"}]}""")]
    [InlineData(0, """{"models":[{"slug":"gpt-5.6-sol","visibility":"hide"}]}""")]
    [InlineData(0, """{"models":[{"slug":"gpt 5.6 sol","visibility":"list"}]}""")]
    [InlineData(0, """{"models":[{"slug":"","visibility":"list"}]}""")]
    public async Task AnUnusableCatalogResponseLeavesTheModelListEmpty(int exitCode, string catalogOutput)
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(paths, _ => new(exitCode, catalogOutput, string.Empty));

        Assert.Empty(await provider.ListModelsAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>ADR 0066: with no vendor executable there is nothing to ask, so the probe is not
    /// attempted at all — not spawned and not cached, matching the default-model probe exactly, so an
    /// uninstalled provider can never write a failure entry that throttles the first real enumeration
    /// after an install.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task TheCatalogProbeIsNeverAttemptedWithoutAnInstalledExecutable()
    {
        using TestPaths paths = new();
        bool spawned = false;
        FakeModelCatalogCache cache = new();
        CodexLlmProvider provider = CreateProvider(
            paths,
            _ =>
            {
                spawned = true;
                return new(0, string.Empty, string.Empty);
            },
            modelCatalogCache: cache);

        Assert.Empty(await provider.ListModelsAsync(TestContext.Current.CancellationToken));

        Assert.False(spawned);
        Assert.Null(await cache.ReadAsync(CodexLlmProvider.Codex, TestContext.Current.CancellationToken));
    }

    /// <summary>ADR 0066 reuses ADR 0063's throttle wholesale, and that is what makes a repeatedly
    /// opened picker cheap: a fresh cached success is reused without spawning anything, a cached
    /// failure is honoured for its own shorter window, a stale entry of either kind triggers exactly
    /// one fresh probe, and a cached "success" whose slugs do not survive validation is corrupt rather
    /// than an answer — re-probed once and overwritten instead of earning 24 hours of silence.</summary>
    [Theory]
    [Trait("Category", "Unit")]
    // A cached success inside its 24h window: reused verbatim, nothing spawned.
    [InlineData(true, new[] { "cached-model" }, 1, 0, new[] { "cached-model" })]
    // The same entry past 24h: one fresh probe, which wins.
    [InlineData(true, new[] { "cached-model" }, 25, 1, new[] { "gpt-5.6-sol" })]
    // A cached failure inside its 1h window: honoured, nothing spawned, still empty.
    [InlineData(false, null, 0, 0, new string[0])]
    // The same failure past 1h: retried exactly once.
    [InlineData(false, null, 2, 1, new[] { "gpt-5.6-sol" })]
    // A fresh entry claiming success whose only slug is unusable: corrupt, so re-probed anyway.
    [InlineData(true, new[] { "cached model" }, 1, 1, new[] { "gpt-5.6-sol" })]
    public async Task TheCatalogProbeIsThrottledOnTheSameCadenceAsTheDefaultModelProbe(
        bool cachedSuccess,
        string[]? cachedModels,
        int hoursSinceCached,
        int expectedProbes,
        string[] expectedModels)
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        FakeClock clock = new() { UtcNow = DateTimeOffset.UnixEpoch.AddDays(30) };
        FakeModelCatalogCache cache = new();
        await cache.WriteAsync(
            CodexLlmProvider.Codex,
            new(clock.UtcNow.AddHours(-hoursSinceCached), cachedSuccess, cachedModels),
            TestContext.Current.CancellationToken);

        int probes = 0;
        CodexLlmProvider provider = CreateProvider(
            paths,
            _ =>
            {
                probes++;
                return new(0, """{"models":[{"slug":"gpt-5.6-sol","visibility":"list"}]}""", string.Empty);
            },
            modelCatalogCache: cache,
            clock: clock);

        IReadOnlyList<string> models = await provider.ListModelsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expectedProbes, probes);
        Assert.Equal(expectedModels, models);
    }

    /// <summary>ADR 0060, against the real thing: `codex-exec-json-tool-calls.jsonl` is a verbatim
    /// `codex exec --json` stream recorded from Codex CLI 0.149.1 driving a throwaway worktree
    /// (read a file, run a command, edit a file), with only the captured absolute path replaced by a
    /// placeholder. The entire mapping was built from this capture and nothing else, so this test is
    /// what actually pins it: the two `agent_message` items must be recognized and ignored (never
    /// counted as drift), both `command_execution` completions must produce a targetless command row
    /// carrying its exit code, and the `file_change` completion must produce an edit row whose
    /// absolute vendor path has been relativized against the attempt's working directory.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncCapturesToolCallsFromARealRecordedCodexStream()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        string jsonl = ReadFixture("codex-exec-json-tool-calls.jsonl");
        CodexLlmProvider provider = CreateProvider(paths, _ => new(0, jsonl, string.Empty));

        ProviderRunResult result = await provider.RunAsync(
            "append a line",
            CapturedWorktree,
            model: null,
            effort: null,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        // Two agent_message items rode in the same stream; neither may be counted as drift, or a
        // perfectly healthy run would look broken.
        Assert.Equal(0, result.UnmappedItemCount);
        Assert.Equal(3, result.ToolCalls.Count);

        ProviderToolCall[] commands =
            [.. result.ToolCalls.Where(call => call.Kind == ProviderToolCallKinds.Command)];
        Assert.Equal(2, commands.Length);
        Assert.All(commands, call =>
        {
            Assert.Null(call.Target);
            Assert.Equal(0, call.ExitCode);
            Assert.True(call.Succeeded);
            // Paired against its own `item.started`, so a Forge-observed duration exists.
            Assert.NotNull(call.DurationMilliseconds);
        });

        ProviderToolCall edit = Assert.Single(
            result.ToolCalls, call => call.Kind == ProviderToolCallKinds.Edit);
        Assert.Equal("sample.txt", edit.Target);
        Assert.Null(edit.ExitCode);
        Assert.Null(edit.Succeeded);
    }

    /// <summary>ADR 0060's three-way split, stated as one table: a mapped tool-call subtype produces a
    /// row, a recognized-but-not-a-tool-call subtype produces nothing at all, and only a genuinely
    /// unrecognized shape increments the drift counter. The malformed cases must not throw out of the
    /// sink either -- tool-call capture is optional enrichment and fails open.</summary>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("""{"type":"item.completed","item":{"id":"i","type":"agent_message","text":"hi"}}""", 0, 0)]
    [InlineData("""{"type":"item.completed","item":{"id":"i","type":"reasoning","text":"hmm"}}""", 0, 0)]
    [InlineData("""{"type":"item.completed","item":{"id":"i","type":"totally_unknown_thing"}}""", 0, 1)]
    [InlineData("""{"type":"item.completed","item":{"id":"i","type":"web_search","query":"x"}}""", 0, 1)]
    [InlineData("""{"type":"item.completed","id":"i"}""", 0, 1)]
    [InlineData("""{"type":"item.completed","item":42}""", 0, 1)]
    [InlineData("""{"type":"item.command_execution","command":"echo hi"}""", 0, 1)]
    [InlineData(
        """{"type":"item.completed","item":{"id":"i","type":"file_change","changes":[]}}""", 0, 1)]
    [InlineData(
        """{"type":"item.completed","item":{"id":"i","type":"command_execution","exit_code":0,"status":"completed"}}""",
        1,
        0)]
    public async Task RunAsyncSeparatesMappedToolCallsFromNarrationAndFromGenuineDrift(
        string line, int expectedToolCalls, int expectedUnmapped)
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(paths, _ => new(
            0, line + "\n" + """{"type":"turn.completed"}""", string.Empty));

        ProviderRunResult result = await provider.RunAsync(
            "prompt", CapturedWorktree, model: null, effort: null, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(expectedToolCalls, result.ToolCalls.Count);
        Assert.Equal(expectedUnmapped, result.UnmappedItemCount);
    }

    /// <summary>Regression test (PR #117 review): every entry of one `file_change` completion belongs
    /// to the same logical operation, which started and completed together — so ADR 0060 and
    /// `ExtractFileChanges` both promise the entries share the item's correlation id "and therefore
    /// its duration". The duration used to be resolved inside the per-entry loop, which consumed the
    /// pending start on the first entry and left every sibling null. Asserted on the durable
    /// <see cref="ToolCallStat"/> rows rather than the in-memory records, since that is where a reader
    /// would have seen the inconsistency.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncGivesEveryEntryOfOneFileChangeCompletionTheSameObservedDuration()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        // Three entries under one `item.id`, in the exact wrapper shape the recorded fixture holds
        // (`changes` rides on both the start and the completion line).
        const string changes = "[{\"path\":\"" + CapturedWorktreeJson + "\\\\a.txt\",\"kind\":\"update\"}," +
            "{\"path\":\"" + CapturedWorktreeJson + "\\\\b.txt\",\"kind\":\"add\"}," +
            "{\"path\":\"" + CapturedWorktreeJson + "\\\\c.txt\",\"kind\":\"delete\"}]";
        string stream = string.Join(
            '\n',
            "{\"type\":\"item.started\",\"item\":{\"id\":\"item_0\",\"type\":\"file_change\",\"changes\":" +
                changes + ",\"status\":\"in_progress\"}}",
            "{\"type\":\"item.completed\",\"item\":{\"id\":\"item_0\",\"type\":\"file_change\",\"changes\":" +
                changes + ",\"status\":\"completed\"}}",
            """{"type":"turn.completed"}""");
        CodexLlmProvider provider = CreateProvider(paths, _ => new(0, stream, string.Empty));

        ProviderRunResult result = await provider.RunAsync(
            "prompt", CapturedWorktree, model: null, effort: null, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.UnmappedItemCount);
        ToolUsePayload payload = Assert.IsType<ToolUsePayload>(ProviderToolUse.ToPayload(result));
        Assert.Equal(["a.txt", "b.txt", "c.txt"], payload.Calls.Select(stat => stat.Target));
        Assert.All(payload.Calls, stat => Assert.NotNull(stat.DurationMilliseconds));
        Assert.Single(payload.Calls.Select(stat => stat.DurationMilliseconds).Distinct());
    }

    /// <summary>Regression test (PR #117 review): `changes` is an array of arbitrary length and every
    /// entry becomes its own row, so ONE stream line can fan out into many tool calls — which is why the
    /// sink's list was never bounded by `MaxEventCount` the way its doc comment once claimed. It is now
    /// capped at <see cref="ProviderExecution.MaxRetainedToolCalls"/>, and the point of this test is
    /// that the cap costs the durable record nothing: the totals and the elision count are taken from
    /// counters that see every observed call, so a fanned-out line is reported at its true size with the
    /// remainder elided, never quietly shrunk to whatever survived in memory.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncCapsTheRowsOneFannedOutStreamLineRetainsWithoutShrinkingItsDurableTotals()
    {
        const int entries = ProviderExecution.MaxRetainedToolCalls + 17;
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        string changes = string.Join(
            ',',
            Enumerable.Range(0, entries).Select(index =>
                "{\"path\":\"" + CapturedWorktreeJson + "\\\\f" + index + ".txt\",\"kind\":\"update\"}"));
        string stream = string.Join(
            '\n',
            "{\"type\":\"item.completed\",\"item\":{\"id\":\"item_0\",\"type\":\"file_change\",\"changes\":[" +
                changes + "],\"status\":\"completed\"}}",
            """{"type":"turn.completed"}""");
        CodexLlmProvider provider = CreateProvider(paths, _ => new(0, stream, string.Empty));

        ProviderRunResult result = await provider.RunAsync(
            "prompt", CapturedWorktree, model: null, effort: null, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(ProviderExecution.MaxRetainedToolCalls, result.ToolCalls.Count);
        Assert.Equal(new ProviderToolCallTotals(entries, 0, entries), result.ToolCallTotals);

        ToolUsePayload payload = Assert.IsType<ToolUsePayload>(ProviderToolUse.ToPayload(result));
        Assert.Equal(entries, payload.ToolCalls);
        Assert.Equal(entries, payload.Edits);
        Assert.Equal(0, payload.Commands);
        Assert.Equal(ProviderToolUseBudget.MaxCalls, payload.Calls.Count);
        Assert.Equal(entries - ProviderToolUseBudget.MaxCalls, payload.ElidedCalls);
        // The rows that did survive are ordinary, fully normalized rows, not truncated stand-ins.
        Assert.Equal("f0.txt", payload.Calls[0].Target);
        Assert.Equal(0, result.UnmappedItemCount);
    }

    /// <summary>Regression test (PR #117 review): `unmapped_items` is a durable, versioned, publicly
    /// documented field counted in ITEMS, but Codex describes one logical item across TWO stream lines
    /// — `item.started` then `item.completed`, sharing `item.id`, exactly as the recorded fixture shows
    /// for both `command_execution` and `file_change`. An unrecognized subtype following that same
    /// lifecycle was therefore counted twice for one item. It is now deduplicated by correlation id,
    /// reusing the pairing the duration already relies on. A line that carries no id, or a fresh id
    /// each time, still counts on its own: there is nothing to deduplicate on, and under-counting real
    /// drift would be a worse failure than the double count this fixes.</summary>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(
        """{"type":"item.started","item":{"id":"item_0","type":"totally_unknown_thing"}}""",
        """{"type":"item.completed","item":{"id":"item_0","type":"totally_unknown_thing"}}""",
        1)]
    [InlineData(
        """{"type":"item.started","item":{"id":"item_0","type":"file_change","changes":[]}}""",
        """{"type":"item.completed","item":{"id":"item_0","type":"file_change","changes":[]}}""",
        1)]
    [InlineData(
        """{"type":"item.started","item":{"type":"totally_unknown_thing"}}""",
        """{"type":"item.completed","item":{"type":"totally_unknown_thing"}}""",
        2)]
    [InlineData(
        """{"type":"item.started","item":{"id":"item_0","type":"totally_unknown_thing"}}""",
        """{"type":"item.completed","item":{"id":"item_1","type":"totally_unknown_thing"}}""",
        2)]
    public async Task RunAsyncCountsAnUnrecognizedItemOncePerItemNotOncePerStreamLine(
        string startLine, string completionLine, int expectedUnmapped)
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(paths, _ => new(
            0,
            string.Join('\n', startLine, completionLine, """{"type":"turn.completed"}"""),
            string.Empty));

        ProviderRunResult result = await provider.RunAsync(
            "prompt", CapturedWorktree, model: null, effort: null, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Empty(result.ToolCalls);
        Assert.Equal(expectedUnmapped, result.UnmappedItemCount);
    }

    /// <summary>The invariant this whole slice hangs on (ADR 0006 via ADR 0060): a
    /// `command_execution` item carries the full command line and its full stdout, both of which
    /// routinely contain secrets. Neither field is ever read into anything durable. Asserted against
    /// the SERIALIZED journal line, not only the typed objects, so a field added to any layer between
    /// here and disk cannot reintroduce the leak unnoticed.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncNeverCarriesCommandTextOrCommandOutputIntoTheDurableRecord()
    {
        const string commandText = "curl -H 'Authorization: Bearer sk-live-fake123' https://example.test";
        const string outputText = "leaked-output-token-9f3c21";
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        // Assembled by concatenation rather than interpolation: the JSON's own trailing `}}` collides
        // with a raw interpolated literal's closing braces.
        string escapedCommand = commandText.Replace("\"", "\\\"", StringComparison.Ordinal);
        string stream = string.Join(
            '\n',
            "{\"type\":\"item.started\",\"item\":{\"id\":\"item_0\",\"type\":\"command_execution\"," +
                "\"command\":\"" + escapedCommand + "\",\"aggregated_output\":\"\",\"exit_code\":null," +
                "\"status\":\"in_progress\"}}",
            "{\"type\":\"item.completed\",\"item\":{\"id\":\"item_0\",\"type\":\"command_execution\"," +
                "\"command\":\"" + escapedCommand + "\",\"aggregated_output\":\"" + outputText + "\"," +
                "\"exit_code\":0,\"status\":\"completed\"}}",
            """{"type":"turn.completed"}""");
        CodexLlmProvider provider = CreateProvider(paths, _ => new(0, stream, string.Empty));

        ProviderRunResult result = await provider.RunAsync(
            "prompt", CapturedWorktree, model: null, effort: null, TestContext.Current.CancellationToken);

        ProviderToolCall call = Assert.Single(result.ToolCalls);
        Assert.Equal(ProviderToolCallKinds.Command, call.Kind);
        Assert.Null(call.Target);

        // All the way to the real journal file on disk, not merely to the typed record: everything
        // between the adapter and that line (payload mapping, codec, schema) is exercised, so a field
        // added at any of those layers cannot reintroduce the leak unnoticed.
        ToolUsePayload payload = Assert.IsType<ToolUsePayload>(ProviderToolUse.ToPayload(result));
        using TestRoot root = new();
        FileSprintEventLog log = new(new FakeClock());
        SprintId sprintId = SprintId.New();
        AttemptId attemptId = AttemptId.New();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Sprint, sprintId.Value.ToString("D"), "SprintChanged",
            "workflow.sprint_created", "draft", 0, Guid.NewGuid(), cancellationToken);
        await log.AppendTransitionAsync(
            root.Path, sprintId, AggregateKind.Attempt, attemptId.Value.ToString("D"), "AttemptChanged",
            "workflow.attempt_created", "created", 0, Guid.NewGuid(), cancellationToken);
        await log.AppendAttemptToolUseRecordedAsync(root.Path, sprintId, attemptId, payload, cancellationToken);

        string journal = string.Concat(
            Directory.EnumerateFiles(root.Path, "*.jsonl", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        Assert.Contains(WorkflowEvent.AttemptToolUseRecordedType, journal, StringComparison.Ordinal);
        foreach (string serialized in new[] { JsonSerializer.Serialize(call, StatusJson.Options), journal })
        {
            Assert.DoesNotContain("Authorization", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-live-fake123", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("curl", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(outputText, serialized, StringComparison.Ordinal);
        }
    }

    /// <summary>The placeholder worktree root the recorded fixture's absolute `file_change` path sits
    /// under, so relativizing it produces a real, in-worktree relative target.</summary>
    private const string CapturedWorktree = @"C:\Users\example\codex-worktree";

    /// <summary><see cref="CapturedWorktree"/> as it appears INSIDE a JSON string, where every
    /// separator is escaped — the exact form the recorded fixture's `path` values use.</summary>
    private const string CapturedWorktreeJson = @"C:\\Users\\example\\codex-worktree";

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncFailsClosedWhenNoTerminalResultEventIsEmitted()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(paths, _ => new(
            0, """{"type":"thread.started","thread_id":"t1"}""" + "\n", string.Empty));

        ProviderRunResult result = await provider.RunAsync(
            "prompt", "C:\\work", model: null, effort: null, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ProviderFailureKind.MissingTerminalResult, result.Failure);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncReturnsMalformedOutputWhenAnEventLineIsNotJson()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(paths, _ => new(0, "not-json", string.Empty));

        ProviderRunResult result = await provider.RunAsync(
            "prompt",
            "C:\\work",
            model: null,
            effort: null,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ProviderFailureKind.MalformedOutput, result.Failure);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncClassifiesANonZeroExitAsAuthenticationAndRedactsTheDetail()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(paths, _ => new(
            1,
            string.Empty,
            "Error: not logged in. api_key=sk-live-abcdef1234567890"));

        ProviderRunResult result = await provider.RunAsync(
            "prompt",
            "C:\\work",
            model: null,
            effort: null,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ProviderFailureKind.Authentication, result.Failure);
        Assert.DoesNotContain("sk-live-abcdef1234567890", result.Detail, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:", result.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("HTTP 429 Too Many Requests", ProviderFailureKind.RateLimited)]
    [InlineData("You have exceeded your monthly quota", ProviderFailureKind.QuotaExceeded)]
    [InlineData("Request blocked by content filter policy", ProviderFailureKind.Policy)]
    [InlineData("connect ECONNRESET", ProviderFailureKind.Transient)]
    [InlineData("something unexpected happened", ProviderFailureKind.Unknown)]
    public async Task RunAsyncClassifiesKnownFailureTextIntoStableCategories(
        string stderr,
        ProviderFailureKind expected)
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(paths, _ => new(1, string.Empty, stderr));

        ProviderRunResult result = await provider.RunAsync(
            "prompt",
            "C:\\work",
            model: null,
            effort: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Failure);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncReturnsNotReadyWithoutSpawningAProcessWhenNotInstalled()
    {
        bool spawned = false;
        using TestPaths paths = new();
        CodexLlmProvider provider = CreateProvider(paths, _ =>
        {
            spawned = true;
            return new(0, string.Empty, string.Empty);
        });

        ProviderRunResult result = await provider.RunAsync(
            "prompt",
            "C:\\work",
            model: null,
            effort: null,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ProviderFailureKind.NotReady, result.Failure);
        Assert.False(spawned);
    }

    /// <summary>ADR 0061, against the same real capture ADR 0060's mapping was built from: its last
    /// line is `turn.completed`, whose `usage` carries the run's token counts. The context window is
    /// asserted ABSENT deliberately — Codex's usage object has no such field anywhere, so a `ctx X / Y`
    /// reading has no honest `Y` for this provider, and reporting null is the point rather than a gap
    /// waiting to be filled by a hardcoded per-model table.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsyncCapturesTokenUsageFromARealRecordedCodexStreamWithNoContextWindow()
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        string jsonl = ReadFixture("codex-exec-json-tool-calls.jsonl");
        CodexLlmProvider provider = CreateProvider(paths, _ => new(0, jsonl, string.Empty));

        ProviderRunResult result = await provider.RunAsync(
            "append a line", CapturedWorktree, model: null, effort: null, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        ProviderUsage usage = Assert.IsType<ProviderUsage>(result.Usage);
        Assert.Equal(88_641, usage.InputTokens);
        Assert.Equal(544, usage.OutputTokens);
        Assert.Null(usage.ContextWindow);
        // Codex's own cache counters are deliberately unmapped: whether they mean what Claude's two
        // cache fields mean is not something one capture per vendor establishes (ADR 0061).
        Assert.Null(usage.CacheReadTokens);
        Assert.Null(usage.CacheCreationTokens);
    }

    /// <summary>ADR 0061: a vendor number that is not a non-negative 32-bit integer is treated as
    /// not-reported rather than coerced. The durable contract declares each count a non-negative
    /// integer, and a negative, fractional, out-of-range, or non-numeric value is not a clamped
    /// version of the truth. A `turn.failed` terminal event reports nothing at all — a failed turn's
    /// work never reaches the integration branch.</summary>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("""{"type":"turn.completed","usage":{"input_tokens":5,"output_tokens":7}}""", 5, 7)]
    [InlineData("""{"type":"turn.completed","usage":{"output_tokens":7}}""", null, 7)]
    [InlineData("""{"type":"turn.completed","usage":{"input_tokens":-3,"output_tokens":7}}""", null, 7)]
    [InlineData("""{"type":"turn.completed","usage":{"input_tokens":1.5,"output_tokens":7}}""", null, 7)]
    [InlineData("""{"type":"turn.completed","usage":{"input_tokens":99999999999,"output_tokens":7}}""", null, 7)]
    [InlineData("""{"type":"turn.completed","usage":{"input_tokens":"5","output_tokens":7}}""", null, 7)]
    [InlineData("""{"type":"turn.completed","usage":[]}""", null, null)]
    [InlineData("""{"type":"turn.completed"}""", null, null)]
    [InlineData("""{"type":"turn.failed","usage":{"input_tokens":5,"output_tokens":7}}""", null, null)]
    public async Task UnusableVendorTokenCountsAreReportedAsAbsentRatherThanCoerced(
        string line, int? expectedInput, int? expectedOutput)
    {
        using TestPaths paths = new();
        WriteCodexExecutable(paths);
        CodexLlmProvider provider = CreateProvider(paths, _ => new(0, line + "\n", string.Empty));

        ProviderRunResult result = await provider.RunAsync(
            "prompt", CapturedWorktree, model: null, effort: null, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(expectedInput, result.Usage?.InputTokens);
        Assert.Equal(expectedOutput, result.Usage?.OutputTokens);
    }

    private static string ReadFixture(string name) => File.ReadAllText(
        Path.Combine(RepositoryRoot.Find(), "tests", "Forge.Tests", "Unit", "fixtures", "providers", name));

    private static CodexLlmProvider CreateProvider(
        TestPaths paths,
        Func<ProcessRequest, ProcessResult> respond,
        IProviderReleaseSource? releaseSource = null,
        IProviderInstallLock? installLock = null,
        IProviderDefaultModelCache? defaultModelCache = null,
        IProviderModelCatalogCache? modelCatalogCache = null,
        IClock? clock = null) =>
        new(
            paths,
            new StubProcessRunner(respond),
            releaseSource ?? new FakeReleaseSource(ProviderReleaseLookupResult.Failed),
            new FakeReleaseCache(),
            defaultModelCache ?? new FakeDefaultModelCache(),
            modelCatalogCache ?? new FakeModelCatalogCache(),
            installLock ?? new FakeInstallLock(),
            clock ?? new FakeClock());

    private static string WriteCodexExecutable(TestPaths paths)
    {
        string executable = Path.Combine(
            paths.LocalApplicationData,
            "Programs",
            "OpenAI",
            "Codex",
            "bin",
            "codex.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "stub");
        return executable;
    }

    /// <summary>An isolated, self-cleaning provider root; adapters only read pinned local state.</summary>
    internal sealed class TestPaths : IEnvironmentPaths, IDisposable
    {
        public TestPaths()
        {
            Root = Path.Combine(Path.GetTempPath(), $"forge-codex-provider-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        private string Root { get; }

        public string LocalApplicationData => Path.Combine(Root, "local");

        public string UserProfile => Path.Combine(Root, "userprofile");

        public string CurrentDirectory => Root;

        public string InstanceId => "forge-test";

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
    }

    /// <summary>Simulates the real <c>ProcessRunner</c>'s streaming contract: feeds the stubbed
    /// response's output to the output sink line by line, the way the actual process pipe reader
    /// does, so <c>ProviderExecution</c>'s sink-driven parsing sees the same events a real run
    /// would.</summary>
    private sealed class StubProcessRunner(Func<ProcessRequest, ProcessResult> respond) : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(
            ProcessRequest request, IProcessOutputSink? outputSink, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessResult result = respond(request);
            if (outputSink is not null)
            {
                foreach (string line in result.StandardOutput.Split('\n'))
                {
                    await outputSink.OnStandardOutputLineAsync(line, cancellationToken).ConfigureAwait(false);
                }

                foreach (string line in result.StandardError.Split('\n'))
                {
                    await outputSink.OnStandardErrorLineAsync(line, cancellationToken).ConfigureAwait(false);
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Never completes on its own, mirroring how the real <c>ProcessRunner</c> behaves against a
    /// hung child process: it only ends when the caller's token is cancelled.
    /// </summary>
    private sealed class HangingProcessRunner : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(
            ProcessRequest request, IProcessOutputSink? outputSink, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Unreachable: Task.Delay(Infinite) only returns via cancellation.");
        }
    }

}
