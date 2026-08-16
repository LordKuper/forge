using System.CommandLine;
using System.Globalization;
using Forge.Application;
using Forge.Cli;
using Forge.Configuration;
using Forge.Domain;
using Forge.Localization;
using Forge.Providers;
using Forge.Tests.Support;
using Forge.Updater;

namespace Forge.AcceptanceTests;

public sealed class CliTests
{
    [Fact]
    [Trait("Category", "Acceptance")]
    public void RootCommandOmitsInstallAndUpdateWhenNoPlatformIsComposed()
    {
        using TestEnvironment environment = new();
        ResourceLocalizationCatalog catalog = new();

        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog),
            new StringWriter(CultureInfo.InvariantCulture),
            environment.Application);

        Assert.DoesNotContain(root.Subcommands, command => command.Name is "install" or "update");
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task ModelsCommandReportsReadyProviders()
    {
        using TestEnvironment environment = new(
            providers: new FakeProviderToolchainManager(FakeProviderToolchainManager.Ready));
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(Text(catalog), output, environment.Application);

        int exitCode = await root
            .Parse(["models"])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("codex enabled ready 0.146.0 - ready none", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("claude_code enabled ready 2.1.221 - ready none", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task ModelsCommandListsARegisteredButDisabledProviderAsReadOnly()
    {
        // The toolchain manager only ever reports "codex" (the user's enabled selection); a
        // second registered provider the catalog knows about but the status never probed must
        // still surface, read-only, as ADR 0008's "listed as disabled without probing it."
        ProviderToolchainStatus enabledOnly = new(
        [
            ProviderStatus.Ready(new ProviderId("codex"), "0.146.0") with
            {
                Authentication = ProviderAuthenticationStatus.Ready,
            },
        ]);
        using TestEnvironment environment = new(
            providers: new FakeProviderToolchainManager(enabledOnly),
            llmProviders:
            [
                new FakeLlmProvider(new ProviderId("codex"), ProviderState.Ready, "0.146.0"),
                new FakeLlmProvider(new ProviderId("claude_code"), ProviderState.Ready, "2.1.221"),
            ]);
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(Text(catalog), output, environment.Application);

        int exitCode = await root
            .Parse(["models"])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("claude_code disabled - - - - provider_disabled", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task ModelsCommandJsonIncludesRegisteredAndEnabledFieldsForEveryProvider()
    {
        ProviderToolchainStatus enabledOnly = new(
        [
            ProviderStatus.Ready(new ProviderId("codex"), "0.146.0") with
            {
                Authentication = ProviderAuthenticationStatus.Ready,
            },
        ]);
        using TestEnvironment environment = new(
            providers: new FakeProviderToolchainManager(enabledOnly),
            llmProviders:
            [
                new FakeLlmProvider(new ProviderId("codex"), ProviderState.Ready, "0.146.0"),
                new FakeLlmProvider(new ProviderId("claude_code"), ProviderState.Ready, "2.1.221"),
            ]);
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(Text(catalog), output, environment.Application);

        int exitCode = await root
            .Parse(["models", "--json"])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        string json = output.ToString();
        // Regression: the JSON branch used to serialize the raw ProviderToolchainStatus (missing
        // registered/enabled) as a bare array (missing the schema_version envelope), so
        // `forge models --json` never actually matched provider-health.schema.json.
        Assert.Contains("\"schema_version\": \"1.1.0\"", json, StringComparison.Ordinal);
        Assert.Contains("\"registered\": true", json, StringComparison.Ordinal);
        Assert.Contains("\"enabled\": false", json, StringComparison.Ordinal);
        Assert.Contains("\"diagnostic_code\": \"provider_disabled\"", json, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task ModelsCommandDefaultsToReadOnlyDiscovery()
    {
        using TestEnvironment environment = new();
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog),
            output,
            environment.Application,
            diagnostics);

        int exitCode = await root
            .Parse(["models", "--json"])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Provider, exitCode);
        Assert.Contains("\"missing\"", output.ToString(), StringComparison.Ordinal);
        // The provider id must serialize as a plain string (matching every other provider-facing
        // contract), never as a nested object.
        Assert.Contains("\"id\": \"codex\"", output.ToString(), StringComparison.Ordinal);
        Assert.Equal($"provider_preflight_pending{Environment.NewLine}", diagnostics.ToString());
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task ModelsCommandRefreshReportsUpdateFailedWhenRepairIsNeeded()
    {
        ProviderToolchainStatus failed = new([
            new(new ProviderId("codex"), ProviderState.Failed, null, ProviderDiagnosticCodes.UpdateFailed),
            ProviderStatus.Ready(new ProviderId("claude_code"), "2.1.221"),
        ]);
        using TestEnvironment environment = new(providers: new FakeProviderToolchainManager(failed));
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog),
            output,
            environment.Application,
            diagnostics);

        int exitCode = await root
            .Parse(["models", "--refresh", "--json"])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Provider, exitCode);
        Assert.Equal($"provider_update_failed{Environment.NewLine}", diagnostics.ToString());
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task StatusCommandUsesSharedLocalizationCatalog()
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru");
            using TestEnvironment environment = new();
            StringWriter output = new(CultureInfo.InvariantCulture);
            ResourceLocalizationCatalog catalog = new();

            int exitCode = await CliApplication
                .CreateRootCommand(Text(catalog), output, environment.Application)
                .Parse(["status"])
                .InvokeAsync(
                    new InvocationConfiguration(),
                    TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Contains(
                "Запуск заблокирован; работа со спринтами недоступна.",
                output.ToString(),
                StringComparison.Ordinal);
            Assert.Contains(
                "Проект не инициализирован.",
                output.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task HelpOptionIsHandledByCliParser()
    {
        using TestEnvironment environment = new();
        StringWriter output = new(CultureInfo.InvariantCulture);
        ILocalizationCatalog catalog = environment.Resolve<ILocalizationCatalog>();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog),
            output,
            environment.Application);

        int exitCode = await root
            .Parse(["--help"])
            .InvokeAsync(
                new InvocationConfiguration { Output = output },
                TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains(
            catalog.Resolve(MessageKeys.AppDescription),
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task InstallCommandUsesTheInstalledReleaseFlow()
    {
        using TestEnvironment environment = new();
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog),
            output,
            environment.Application,
            install: _ => ValueTask.FromResult(
                new InstallationResult(true, "C:\\Forge", UpdateDiagnostic.None)));

        int exitCode = await root
            .Parse(["install"])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal($"{catalog.Resolve(MessageKeys.InstallCompleted)}{Environment.NewLine}", output.ToString());
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task UpdateCommandUsesTheSharedUpdateFlow()
    {
        using TestEnvironment environment = new();
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog),
            output,
            environment.Application,
            update: _ => ValueTask.FromResult(new UpdateResult(
                UpdateLifecycleState.RestartRequested,
                UpdateDiagnostic.None)));

        int exitCode = await root
            .Parse(["update"])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal($"{catalog.Resolve(MessageKeys.UpdateCompleted)}{Environment.NewLine}", output.ToString());
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task InstallCommandLocalizesFailureOutput()
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru");
            using TestEnvironment environment = new();
            StringWriter output = new(CultureInfo.InvariantCulture);
            ResourceLocalizationCatalog catalog = new();
            RootCommand root = CliApplication.CreateRootCommand(
                Text(catalog),
                output,
                environment.Application,
                install: _ => ValueTask.FromResult(InstallationResult.Failure(new(
                    UpdateDiagnosticCode.ReleaseUnavailable,
                    "The release endpoint could not be reached."))));

            int exitCode = await root
                .Parse(["install"])
                .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

            Assert.Equal(ExitCodes.Update, exitCode);
            Assert.Equal($"{catalog.Resolve(MessageKeys.InstallFailed)}{Environment.NewLine}", output.ToString());
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task StatusCommandWithFullDetailPrintsTheSprintAndItsNode()
    {
        using TestEnvironment environment = new();
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            TestContext.Current.CancellationToken)).SprintId!;
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(Text(catalog), output, environment.Application);

        int exitCode = await root
            .Parse(["status", "--project-root", environment.ProjectRoot, "--detail", "full"])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        string text = output.ToString();
        Assert.Contains(sprintId.Value.ToString(), text, StringComparison.Ordinal);
        Assert.Contains("draft", text, StringComparison.Ordinal);
        Assert.Contains("a ready", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task EventsCommandReportsTheSprintCreationEventAndAReusableCursor()
    {
        using TestEnvironment environment = new();
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            TestContext.Current.CancellationToken)).SprintId!;
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(Text(catalog), output, environment.Application);

        int exitCode = await root
            .Parse(["events", "--project-root", environment.ProjectRoot])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains(sprintId.Value.ToString(), output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task StatusCommandReportsSprintNotFoundForAMalformedSprintId()
    {
        using TestEnvironment environment = new();
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(Text(catalog), output, environment.Application, diagnostics);

        int exitCode = await root
            .Parse(["status", "--project-root", environment.ProjectRoot, "--sprint", "not-a-guid"])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Equal($"{DiagnosticCodes.SprintNotFound}{Environment.NewLine}", diagnostics.ToString());
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task StatusCommandReportsSprintNotFoundForAnUnknownSprintId()
    {
        using TestEnvironment environment = new();
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(Text(catalog), output, environment.Application, diagnostics);

        int exitCode = await root
            .Parse(["status", "--project-root", environment.ProjectRoot, "--sprint", Guid.NewGuid().ToString(), "--json"])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Equal($"{DiagnosticCodes.SprintNotFound}{Environment.NewLine}", diagnostics.ToString());
        // The machine contract itself still comes back well-formed (no details section) rather than
        // being replaced by an error payload.
        Assert.DoesNotContain("\"details\"", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task EventsCommandReportsProjectNotInitializedInsteadOfLookingCaughtUp()
    {
        using TestEnvironment environment = new();
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(Text(catalog), output, environment.Application, diagnostics);

        int exitCode = await root
            .Parse(["events", "--project-root", environment.ProjectRoot, "--json"])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Project, exitCode);
        Assert.Equal($"{DiagnosticCodes.ProjectNotInitialized}{Environment.NewLine}", diagnostics.ToString());
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task EventsCommandFollowStopsOnCancellationAfterAtLeastOnePoll()
    {
        using TestEnvironment environment = new();
        InitializeProjectResult init = await environment.InitializeAsync(
            environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        Assert.True(init.Succeeded);
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        // Captured explicitly (not read again after InvokeAsync) so this assertion can never race a
        // different, parallel test's ambient CultureInfo.CurrentUICulture mutation.
        CultureInfo culture = CultureInfo.CurrentUICulture;
        RootCommand root = CliApplication.CreateRootCommand(
            new SurfaceText(catalog, culture), output, environment.Application);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(1500));

        // System.CommandLine surfaces a canceled invocation as a non-zero exit rather than throwing
        // out of InvokeAsync; the behavior under test is that the loop polled at least once and
        // stopped cleanly on cancellation instead of hanging or crashing.
        await root
            .Parse(["events", "--project-root", environment.ProjectRoot, "--follow"])
            .InvokeAsync(new InvocationConfiguration(), cancellation.Token);

        // At least the first (immediate, pre-delay) poll ran and printed its "no events yet" output.
        Assert.Contains(catalog.Resolve(MessageKeys.NoEvents, culture), output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task ConfigProjectSetRoutesThroughTheResolvedMutationsInsteadOfTheLocalApplication()
    {
        // ADR 0005: every `.forge/` mutation routes through the project's Host once one is
        // reachable — `mutations` here stands in for a real Host connection.
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        FakeForgeMutations mutations = new();
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog),
            output,
            environment.Application,
            resolveMutations: (_, _) => Task.FromResult<IForgeMutations>(mutations));

        int exitCode = await root
            .Parse([
                "config", "project", "artifacts.language.user_facing", "\"ru\"",
                "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, mutations.SetConfigurationCalls);
        Assert.Equal(ConfigurationScope.Project, mutations.LastScope);
        // Still the key's registered default ("en"), never actually written locally — the fake
        // never touches durable state, and a real ForgeApplication call would have overwritten it
        // to "ru" (proving the write really left this process instead of landing here).
        ConfigurationView project = await environment.Application.GetProjectConfigurationAsync(
            environment.ProjectRoot,
            TestContext.Current.CancellationToken);
        EffectiveConfigurationValue value = Assert.Single(
            project.Values,
            item => item.Key == "artifacts.language.user_facing");
        Assert.Equal("\"en\"", value.Value.GetRawText());
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task ConfigProjectSetResolvesMutationsUsingTheCommandsOwnProjectRoot()
    {
        // Regression coverage: the resolver must see THIS command's own `--project-root`, never a
        // root fixed before argument parsing (the bug an architecture audit found: a Host
        // connection bound once at startup silently ignored a per-command `--project-root`). The
        // supplied root deliberately differs from `environment.ProjectRoot` (== CWD in this test
        // harness) — asserting equality against `environment.ProjectRoot` here would pass under the
        // pre-fix, CWD-fixed resolution too, and so would not actually catch that regression.
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, TestContext.Current.CancellationToken);
        string otherRoot = Path.Combine(Path.GetTempPath(), $"forge-other-{Guid.NewGuid():N}");
        FakeForgeMutations mutations = new();
        string? capturedRoot = "unset";
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog),
            output,
            environment.Application,
            resolveMutations: (mutationRoot, _) =>
            {
                capturedRoot = mutationRoot;
                return Task.FromResult<IForgeMutations>(mutations);
            });

        await root
            .Parse(["config", "project", "artifacts.language.user_facing", "ru", "--project-root", otherRoot])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(otherRoot, capturedRoot);
        Assert.NotEqual(environment.ProjectRoot, capturedRoot);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task ConfigUserSetNeverRoutesThroughTheResolvedMutations()
    {
        // User-scope configuration is not `.forge/` project state (ADR 0005 protects the latter),
        // so it stays local even when a Host connection is available for project mutations.
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog),
            output,
            environment.Application,
            resolveMutations: (_, _) => Task.FromResult<IForgeMutations>(mutations));

        int exitCode = await root
            .Parse(["config", "user", "interaction.confirm_destructive", "false"])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, mutations.SetConfigurationCalls);
        ConfigurationView user = await environment.Application.GetUserConfigurationAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains(
            user.Values,
            value => value.Key == "interaction.confirm_destructive" && value.Value.GetBoolean() == false);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task DoctorRecoverRoutesThroughTheResolvedMutations()
    {
        using TestEnvironment environment = new();
        FakeForgeMutations mutations = new();
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog),
            output,
            environment.Application,
            resolveMutations: (_, _) => Task.FromResult<IForgeMutations>(mutations));

        await root
            .Parse(["doctor", "--recover", "--yes"])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(1, mutations.RecoverStartupCalls);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task DoctorRecoverResolvesMutationsUsingTheCommandsOwnProjectRoot()
    {
        // Same regression this covers for `config project set` — `doctor --recover` is the other
        // command the audited bug affected, and it had no coverage distinguishing a per-command
        // root from a CWD-fixed one.
        using TestEnvironment environment = new();
        string otherRoot = Path.Combine(Path.GetTempPath(), $"forge-other-{Guid.NewGuid():N}");
        FakeForgeMutations mutations = new();
        string? capturedRoot = "unset";
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog),
            output,
            environment.Application,
            resolveMutations: (mutationRoot, _) =>
            {
                capturedRoot = mutationRoot;
                return Task.FromResult<IForgeMutations>(mutations);
            });

        await root
            .Parse(["doctor", "--recover", "--yes", "--project-root", otherRoot])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(otherRoot, capturedRoot);
        Assert.NotEqual(environment.ProjectRoot, capturedRoot);
    }

    private static SurfaceText Text(ILocalizationCatalog catalog) =>
        new(catalog, CultureInfo.CurrentUICulture);

    private sealed class FakeForgeMutations : IForgeMutations
    {
        public int RecoverStartupCalls { get; private set; }

        public int SetConfigurationCalls { get; private set; }

        public ConfigurationScope? LastScope { get; private set; }

        public Task<RecoverStartupResult> RecoverStartupAsync(
            string? projectRoot,
            bool confirmed,
            CancellationToken cancellationToken)
        {
            RecoverStartupCalls++;
            return Task.FromResult(new RecoverStartupResult(true, null, DiagnosticCodes.None));
        }

        public Task<ConfigurationWriteResult> SetConfigurationAsync(
            ConfigurationScope scope,
            string? projectRoot,
            string key,
            string? rawValue,
            CancellationToken cancellationToken)
        {
            SetConfigurationCalls++;
            LastScope = scope;
            return Task.FromResult(ConfigurationWriteResult.Success);
        }
    }
}
