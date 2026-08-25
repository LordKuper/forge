using System.Globalization;
using Forge.Application;
using Forge.Configuration;
using Forge.Desktop.Presentation;
using Forge.Domain;
using Forge.Localization;
using Forge.Providers;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class SidebarViewModelTests
{
    private static SurfaceTextProvider Text() => new(new ResourceLocalizationCatalog(), "en");

    private static SurfaceTextProvider TextRu() => new(new ResourceLocalizationCatalog(), "ru");

    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadAsyncOrdersActiveSprintsByThePlanSection41Rule()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ForgeApplication application = environment.Application;

        // Two running sprints and one still in Draft (rank 4, sorts last): the running sprints
        // must sort ahead of the draft one and, within that bucket, by descending creation
        // sequence. The full bucket ordering (attention/running/paused/blocked-or-failed/other) is
        // covered directly and exhaustively by SprintOrderingRankTests; this proves
        // SidebarViewModel actually wires that rule to real backend state.
        Guid running1 = await CreateAndRunAsync(application, environment.ProjectRoot, cancellationToken);
        Guid running2 = await CreateAndRunAsync(application, environment.ProjectRoot, cancellationToken);
        Guid draft = (await application.CreateSprintAsync(environment.ProjectRoot, cancellationToken)).SprintId!.Value;

        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        SidebarViewModel viewModel =
            new(catalog, application, new FakeFolderPicker(), Text(), new HostConnectivityMonitor());

        SidebarSnapshot snapshot = await viewModel.LoadAsync(cancellationToken);

        SidebarProjectItem project = Assert.Single(snapshot.Projects);
        Assert.Equal(
            [running2, running1, draft], [.. project.ActiveSprints.Select(sprint => sprint.SprintId)]);
    }

    private static async Task<Guid> CreateAndRunAsync(
        ForgeApplication application, string root, CancellationToken cancellationToken)
    {
        CreateSprintResult created = await application.CreateSprintAsync(root, cancellationToken);
        Guid sprintId = created.SprintId!.Value;
        await application.RunSprintAsync(root, sprintId, cancellationToken);
        await application.RunSprintAsync(root, sprintId, cancellationToken);
        return sprintId;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadAsyncCountsTerminalSprintsAsHistoryWithoutListingThemAsActive()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        CreateSprintResult created = await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid()), cancellationToken);
        SprintSnapshot sprint = (await orchestrator.GetSprintAsync(
            environment.ProjectRoot, created.SprintId!, cancellationToken))!;
        await orchestrator.CancelSprintAsync(
            new(environment.ProjectRoot, created.SprintId!, sprint.Version,
                SprintOrchestrator.CancelSprintKey(sprint)),
            cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        SidebarViewModel viewModel =
            new(catalog, environment.Application, new FakeFolderPicker(), Text(), new HostConnectivityMonitor());

        SidebarSnapshot snapshot = await viewModel.LoadAsync(cancellationToken);

        SidebarProjectItem project = Assert.Single(snapshot.Projects);
        Assert.Empty(project.ActiveSprints);
        Assert.Equal(1, project.HistoryCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddProjectAsyncUsesTheManualPathWithoutInvokingThePicker()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        FakeFolderPicker picker = new();
        SidebarViewModel viewModel = new(
            environment.Resolve<ProjectCatalogStore>(),
            environment.Application,
            picker,
            Text(),
            new HostConnectivityMonitor());

        AddProjectResult result = await viewModel.AddProjectAsync(environment.ProjectRoot, cancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(0, picker.Calls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddProjectAsyncFallsBackToThePickerWhenNoManualPathIsGiven()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        FakeFolderPicker picker = new(environment.ProjectRoot);
        SidebarViewModel viewModel = new(
            environment.Resolve<ProjectCatalogStore>(),
            environment.Application,
            picker,
            Text(),
            new HostConnectivityMonitor());

        AddProjectResult result = await viewModel.AddProjectAsync(null, cancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(1, picker.Calls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddProjectAsyncReportsCancelledWhenThePickerIsDismissed()
    {
        using TestEnvironment environment = new();
        FakeFolderPicker picker = new(nextResult: null);
        SidebarViewModel viewModel = new(
            environment.Resolve<ProjectCatalogStore>(),
            environment.Application,
            picker,
            Text(),
            new HostConnectivityMonitor());

        AddProjectResult result = await viewModel.AddProjectAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(AddProjectResult.Cancelled, result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveProjectAsyncRemovesTheCatalogEntry()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        ProjectCatalogResult added = await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        SidebarViewModel viewModel =
            new(catalog, environment.Application, new FakeFolderPicker(), Text(), new HostConnectivityMonitor());

        string diagnosticCode = await viewModel.RemoveProjectAsync(added.Entry!.ProjectId, cancellationToken);

        Assert.Equal(DiagnosticCodes.None, diagnosticCode);
        Assert.Empty((await catalog.ListAsync(cancellationToken)).Entries);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadAsyncLocalizesTheStatusRowAndAccessibleProjectNameInsteadOfHardcodedEnglish()
    {
        // PR #98 review finding 8: "available"/"unavailable", "active sprints", "need attention",
        // and both provider-ready phrases were hardcoded English literals in this neutral view-model
        // -- under language.ui = ru they must resolve through the Russian catalog instead.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        SurfaceTextProvider ru = TextRu();
        SidebarViewModel viewModel =
            new(catalog, environment.Application, new FakeFolderPicker(), ru, new HostConnectivityMonitor());

        SidebarSnapshot snapshot = await viewModel.LoadAsync(cancellationToken);

        SidebarProjectItem project = Assert.Single(snapshot.Projects);
        string expectedAvailability =
            ru.Resolve(project.Available ? MessageKeys.SidebarProjectAvailable : MessageKeys.SidebarProjectUnavailable);
        Assert.Contains(expectedAvailability, project.AccessibleName, StringComparison.Ordinal);
        Assert.Contains(ru.Resolve(MessageKeys.SidebarActiveSprintsLabel), project.AccessibleName, StringComparison.Ordinal);
        Assert.Contains(ru.Resolve(MessageKeys.SidebarAttentionNeededLabel), project.AccessibleName, StringComparison.Ordinal);
        Assert.DoesNotContain("available", project.AccessibleName, StringComparison.Ordinal);
        Assert.DoesNotContain("active sprints", project.AccessibleName, StringComparison.Ordinal);
        Assert.DoesNotContain("need attention", project.AccessibleName, StringComparison.Ordinal);
        Assert.DoesNotContain("providers ready", snapshot.Status.ProviderSummaryText, StringComparison.Ordinal);
        Assert.DoesNotContain("providers are ready", snapshot.Status.ProviderAccessibleText, StringComparison.Ordinal);
    }

    /// <summary>Plan 12.6: the quota state must have both text and an accessible name, never color
    /// alone. ADR 0052 found no provider integration exposes a verified quota signal, so this must
    /// truthfully resolve to the "unknown" state rather than fabricating readiness.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadAsyncReportsQuotaAsUnknownWithBothTextAndAnAccessibleName()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        SurfaceTextProvider en = Text();
        SidebarViewModel viewModel =
            new(catalog, environment.Application, new FakeFolderPicker(), en, new HostConnectivityMonitor());

        SidebarSnapshot snapshot = await viewModel.LoadAsync(cancellationToken);

        Assert.Equal(en.Resolve(MessageKeys.QuotaStatusUnknown), snapshot.Status.QuotaStatusText);
        Assert.Equal(en.Resolve(MessageKeys.QuotaStatusUnknownAccessible), snapshot.Status.QuotaAccessibleText);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Status.QuotaAccessibleText));
    }

    /// <summary>ADR 0050 addendum: the workspace shell's whole-sidebar collapse toggle defaults to
    /// expanded, matching the fixed layout every prior release shipped.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadAsyncDefaultsToAnExpandedSidebar()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        SidebarViewModel viewModel = new(
            environment.Resolve<ProjectCatalogStore>(),
            environment.Application,
            new FakeFolderPicker(),
            Text(),
            new HostConnectivityMonitor());

        SidebarSnapshot snapshot = await viewModel.LoadAsync(cancellationToken);

        Assert.False(snapshot.Collapsed);
    }

    /// <summary>Plan section 2's "collapsible sidebar" requirement (docs/plans/desktop-workspace-redesign.md)
    /// includes surviving a restart. <see cref="SidebarViewModel.SetCollapsedAsync"/> writes through
    /// the real, local user-scope configuration store (never a project's Host -- ADR 0050), so a
    /// second, independently constructed <see cref="SidebarViewModel"/> standing in for a fresh app
    /// process must observe the first instance's write.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetCollapsedAsyncPersistsAcrossANewViewModelInstanceSimulatingARestart()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        SidebarViewModel first = new(
            environment.Resolve<ProjectCatalogStore>(),
            environment.Application,
            new FakeFolderPicker(),
            Text(),
            new HostConnectivityMonitor());

        ConfigurationWriteResult result = await first.SetCollapsedAsync(true, cancellationToken);

        Assert.True(result.Succeeded);
        SidebarViewModel second = new(
            environment.Resolve<ProjectCatalogStore>(),
            environment.Application,
            new FakeFolderPicker(),
            Text(),
            new HostConnectivityMonitor());
        SidebarSnapshot snapshot = await second.LoadAsync(cancellationToken);
        Assert.True(snapshot.Collapsed);
    }

    /// <summary>Same persistence path as the restart test above, proving the toggle is reversible
    /// (expand after collapse), not a one-way latch.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetCollapsedAsyncCanExpandAnAlreadyCollapsedSidebar()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        SidebarViewModel viewModel = new(
            environment.Resolve<ProjectCatalogStore>(),
            environment.Application,
            new FakeFolderPicker(),
            Text(),
            new HostConnectivityMonitor());
        await viewModel.SetCollapsedAsync(true, cancellationToken);

        await viewModel.SetCollapsedAsync(false, cancellationToken);

        SidebarSnapshot snapshot = await viewModel.LoadAsync(cancellationToken);
        Assert.False(snapshot.Collapsed);
    }

    /// <summary>PR #103 review finding 1: <c>WorkspaceShellPage</c>'s collapse toggle used to discard
    /// this method's <see cref="ConfigurationWriteResult"/> and always re-render as if the write had
    /// succeeded, leaving a failed toggle silently inert. This proves the signal it now checks is
    /// real and load-bearing rather than always-success: the same real, file-backed failure
    /// technique already used elsewhere in this suite (<c>ScopedConfigurationTests</c>'s
    /// <c>AMalformedUserConfigurationFileDegradesToOmittedInsteadOfThrowing</c>,
    /// <c>ProjectCatalogStoreTests</c>'s <c>AFutureSchemaVersionCatalogFailsClosedInsteadOfBeingSilentlyDowngraded</c>)
    /// -- a malformed existing user configuration file -- makes the write genuinely fail with a
    /// non-<see cref="DiagnosticCodes.None"/> diagnostic instead of a mock standing in for one.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetCollapsedAsyncReportsFailureWhenTheUserConfigurationFileIsUnreadable()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        string path = ConfigurationStoreFactory.UserPath(environment);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "not json", cancellationToken);
        SidebarViewModel viewModel = new(
            environment.Resolve<ProjectCatalogStore>(),
            environment.Application,
            new FakeFolderPicker(),
            Text(),
            new HostConnectivityMonitor());

        ConfigurationWriteResult result = await viewModel.SetCollapsedAsync(true, cancellationToken);

        Assert.False(result.Succeeded);
        Assert.NotEqual(DiagnosticCodes.None, result.DiagnosticCode);
    }

    /// <summary>PR #100 review finding 1: <c>GetProviderQuotaStatusAsync</c> issues its own fresh,
    /// uncached <c>ProviderToolchainManager.CheckAsync</c> probe (a `--version` child process plus an
    /// authentication probe per enabled provider). Calling it a second time from
    /// <see cref="SidebarViewModel.LoadAsync"/> -- on top of the <c>EnsureReadyAsync</c> check
    /// <see cref="ForgeApplication.GetWorkspaceSummaryAsync"/> already ran once per project in the
    /// same render -- would spawn redundant provider child processes on every sidebar render for a
    /// value ADR 0052 guarantees is always "unknown" regardless. This proves the toolchain's own
    /// <c>CheckAsync</c> is never called by a sidebar load: the quota row must be projected from the
    /// <see cref="Forge.Providers.ProviderHealthEntry"/> set the same render pass already
    /// collected.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadAsyncNeverIssuesASecondToolchainProbeToComputeTheQuotaRow()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeProviderToolchainManager toolchain = new();
        using TestEnvironment environment = new(providers: toolchain);
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        SidebarViewModel viewModel =
            new(catalog, environment.Application, new FakeFolderPicker(), Text(), new HostConnectivityMonitor());

        SidebarSnapshot snapshot = await viewModel.LoadAsync(cancellationToken);

        Assert.Equal(0, toolchain.CheckCalls);
        Assert.NotEmpty(snapshot.Projects);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Status.QuotaStatusText));
    }

    /// <summary>Plan 12.6: the status row must distinguish provider health from authentication and
    /// model availability, not conflate them. A provider whose toolchain install is ready but whose
    /// authentication is missing must show as toolchain-healthy (this provider counts toward
    /// <see cref="SidebarStatusRow.ProviderSummaryText"/>'s ready count) while still being reported
    /// as unauthenticated and unavailable for real model work -- proving these are independent
    /// signals, not the same "ready" bit rendered under two labels.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadAsyncDistinguishesToolchainHealthFromAuthenticationAndModelAvailability()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ProviderId codex = new("codex");
        ProviderToolchainStatus status =
            new([ProviderStatus.Ready(codex, "1.0.0") with { Authentication = ProviderAuthenticationStatus.Required }]);
        using TestEnvironment environment = new(providers: new FakeProviderToolchainManager(status));
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        SurfaceTextProvider en = Text();
        SidebarViewModel viewModel =
            new(catalog, environment.Application, new FakeFolderPicker(), en, new HostConnectivityMonitor());

        SidebarSnapshot snapshot = await viewModel.LoadAsync(cancellationToken);

        Assert.Equal(
            string.Format(CultureInfo.InvariantCulture, en.Resolve(MessageKeys.SidebarProvidersReadyStatus), 1, 1),
            snapshot.Status.ProviderSummaryText);
        Assert.Equal(en.Resolve(MessageKeys.AuthenticationStatusRequired), snapshot.Status.AuthenticationStatusText);
        Assert.Equal(
            en.Resolve(MessageKeys.AuthenticationStatusRequiredAccessible), snapshot.Status.AuthenticationAccessibleText);
        Assert.Equal(
            string.Format(CultureInfo.InvariantCulture, en.Resolve(MessageKeys.SidebarModelsAvailableStatus), 0, 1),
            snapshot.Status.ModelAvailabilityText);
        Assert.True(snapshot.Status.AnyModelUnavailable);
    }

    /// <summary>The counterpart to the previous test: once a provider is BOTH toolchain-ready and
    /// authenticated, model availability must report it as actually usable, not merely "installed."
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadAsyncReportsAModelAsAvailableOnlyWhenReadyAndAuthenticated()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ProviderId codex = new("codex");
        ProviderToolchainStatus status =
            new([ProviderStatus.Ready(codex, "1.0.0") with { Authentication = ProviderAuthenticationStatus.Ready }]);
        using TestEnvironment environment = new(providers: new FakeProviderToolchainManager(status));
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        SurfaceTextProvider en = Text();
        SidebarViewModel viewModel =
            new(catalog, environment.Application, new FakeFolderPicker(), en, new HostConnectivityMonitor());

        SidebarSnapshot snapshot = await viewModel.LoadAsync(cancellationToken);

        Assert.Equal(en.Resolve(MessageKeys.AuthenticationStatusReady), snapshot.Status.AuthenticationStatusText);
        Assert.Equal(
            string.Format(CultureInfo.InvariantCulture, en.Resolve(MessageKeys.SidebarModelsAvailableStatus), 1, 1),
            snapshot.Status.ModelAvailabilityText);
        Assert.False(snapshot.Status.AnyModelUnavailable);
    }

    /// <summary>The authentication indicator reports the single worst state across every enabled
    /// provider (mirroring <c>SurfaceFormatting.QuotaStatusSummary</c>'s own worst-case shape): a
    /// broken authentication probe on one provider must not be hidden behind another provider that
    /// merely needs login.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadAsyncReportsTheWorstAuthenticationStateAcrossEveryEnabledProvider()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ProviderId codex = new("codex");
        ProviderId claudeCode = new("claude_code");
        ProviderToolchainStatus status = new([
            ProviderStatus.Ready(codex, "1.0.0") with { Authentication = ProviderAuthenticationStatus.Required },
            ProviderStatus.Ready(claudeCode, "1.0.0") with { Authentication = ProviderAuthenticationStatus.CheckFailed },
        ]);
        using TestEnvironment environment = new(providers: new FakeProviderToolchainManager(status));
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        SurfaceTextProvider en = Text();
        SidebarViewModel viewModel =
            new(catalog, environment.Application, new FakeFolderPicker(), en, new HostConnectivityMonitor());

        SidebarSnapshot snapshot = await viewModel.LoadAsync(cancellationToken);

        Assert.Equal(en.Resolve(MessageKeys.AuthenticationStatusCheckFailed), snapshot.Status.AuthenticationStatusText);
    }

    /// <summary>Plan 12.6: the status row must have a Host-connectivity indicator. Before any
    /// mutation has been attempted this process, <see cref="IHostConnectivityMonitor.LastObserved(Guid)"/>
    /// is <see langword="null"/> -- the sidebar must report that honestly as "not yet checked," never
    /// fabricate a connected/disconnected guess (the same "unknown, never inferred" discipline plan
    /// 12.6 already requires of quota).</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadAsyncReportsHostConnectivityAsUnknownBeforeAnyMutationIsAttempted()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        SurfaceTextProvider en = Text();
        SidebarViewModel viewModel = new(
            catalog, environment.Application, new FakeFolderPicker(), en, connectivityMonitor: new HostConnectivityMonitor());

        SidebarSnapshot snapshot = await viewModel.LoadAsync(cancellationToken);

        Assert.Equal(en.Resolve(MessageKeys.HostConnectivityUnknown), snapshot.Status.HostConnectivityText);
        Assert.Equal(en.Resolve(MessageKeys.HostConnectivityUnknownAccessible), snapshot.Status.HostConnectivityAccessibleText);
    }

    /// <summary>Wires <c>ForgeHostClient.IsConnected</c>'s real outcome (via
    /// <see cref="IHostConnectivityMonitor.Report(Guid, bool, DateTimeOffset)"/>, the same call <c>RemoteForgeMutations</c> makes
    /// after a real mutation attempt -- see its own remarks) through to the status row's real, load
    /// -bearing text: a connected reading and a disconnected reading must render as different,
    /// distinguishable states. PR #106 review finding 5 changed <see cref="IHostConnectivityMonitor"/>
    /// to key readings by project id, so this now reports and reads back the SAME project's reading
    /// (<c>selectedProjectId</c> below) -- see
    /// <see cref="LoadAsyncScopesHostConnectivityToTheSelectedProjectNotToAnyOtherCatalogedProject"/>
    /// for the actual cross-project isolation this scoping exists to guarantee.</summary>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(true, MessageKeys.HostConnectivityConnected, MessageKeys.HostConnectivityConnectedAccessible)]
    [InlineData(false, MessageKeys.HostConnectivityDisconnected, MessageKeys.HostConnectivityDisconnectedAccessible)]
    public async Task LoadAsyncReportsTheMonitorsLastObservedConnectivity(
        bool connected, string expectedTextKey, string expectedAccessibleKey)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        SurfaceTextProvider en = Text();
        Guid selectedProjectId = Guid.NewGuid();
        HostConnectivityMonitor monitor = new();
        monitor.Report(selectedProjectId, connected, DateTimeOffset.UtcNow);
        SidebarViewModel viewModel =
            new(catalog, environment.Application, new FakeFolderPicker(), en, connectivityMonitor: monitor);

        SidebarSnapshot snapshot = await viewModel.LoadAsync(cancellationToken, selectedProjectId);

        Assert.Equal(en.Resolve(expectedTextKey), snapshot.Status.HostConnectivityText);
        Assert.Equal(en.Resolve(expectedAccessibleKey), snapshot.Status.HostConnectivityAccessibleText);
    }

    /// <summary>PR #106 review finding 5: a process-global "last mutation from any project" reading
    /// let a successful mutation against project A's Host render "Connected to Host." while project
    /// B's Host might be unreachable, never started, or crashed -- a materially misleading status
    /// indicator whenever more than one project is cataloged. <see cref="IHostConnectivityMonitor"/>
    /// now keys readings by project id (matching <c>ForgeHostClient</c>'s own per-project pipe
    /// scoping), and <see cref="SidebarViewModel.LoadAsync"/> takes the CURRENTLY SELECTED project's
    /// id and must render only that project's own reading. This is the regression test: project A is
    /// disconnected, project B is connected, and each load must show only the project it was asked
    /// about, never the other's (or "whichever was reported last," the pre-fix behavior).</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadAsyncScopesHostConnectivityToTheSelectedProjectNotToAnyOtherCatalogedProject()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        SurfaceTextProvider en = Text();
        Guid projectA = Guid.NewGuid();
        Guid projectB = Guid.NewGuid();
        HostConnectivityMonitor monitor = new();
        // Project B reported LAST -- a last-writer-wins global field would show B's "connected" for
        // both projects, including when project A is the one actually selected.
        monitor.Report(projectA, false, DateTimeOffset.UtcNow);
        monitor.Report(projectB, true, DateTimeOffset.UtcNow);
        SidebarViewModel viewModel =
            new(catalog, environment.Application, new FakeFolderPicker(), en, connectivityMonitor: monitor);

        SidebarSnapshot snapshotForA = await viewModel.LoadAsync(cancellationToken, projectA);
        SidebarSnapshot snapshotForB = await viewModel.LoadAsync(cancellationToken, projectB);

        Assert.Equal(en.Resolve(MessageKeys.HostConnectivityDisconnected), snapshotForA.Status.HostConnectivityText);
        Assert.Equal(en.Resolve(MessageKeys.HostConnectivityConnected), snapshotForB.Status.HostConnectivityText);
    }

    /// <summary>Plan 12.6's "stale data" indicator: a Host-connectivity reading old enough that it
    /// can no longer be trusted as current must be reported as its own distinct "stale" state --
    /// never silently presented as if it were a fresh "connected" reading. Uses a fixed
    /// <see cref="IClock"/> instead of a real sleep, so the test is deterministic and fast.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadAsyncReportsHostConnectivityAsStaleOnceTheLastObservedReadingIsTooOld()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        SurfaceTextProvider en = Text();
        Guid selectedProjectId = Guid.NewGuid();
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        HostConnectivityMonitor monitor = new();
        monitor.Report(selectedProjectId, true, observedAt);
        FixedClock clock = new(observedAt + SidebarViewModel.HostConnectivityStaleAfter + TimeSpan.FromSeconds(1));
        SidebarViewModel viewModel = new(
            catalog, environment.Application, new FakeFolderPicker(), en, clock: clock, connectivityMonitor: monitor);

        SidebarSnapshot snapshot = await viewModel.LoadAsync(cancellationToken, selectedProjectId);

        Assert.Equal(en.Resolve(MessageKeys.HostConnectivityStale), snapshot.Status.HostConnectivityText);
        Assert.Equal(en.Resolve(MessageKeys.HostConnectivityStaleAccessible), snapshot.Status.HostConnectivityAccessibleText);
    }

    /// <summary>The same reading, read just BEFORE the staleness threshold elapses, must still be
    /// reported as the ordinary connected state -- proving the threshold is a real boundary, not
    /// merely always-stale or always-fresh.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadAsyncStillReportsAFreshReadingAsConnectedJustBeforeTheStaleThreshold()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        SurfaceTextProvider en = Text();
        Guid selectedProjectId = Guid.NewGuid();
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        HostConnectivityMonitor monitor = new();
        monitor.Report(selectedProjectId, true, observedAt);
        FixedClock clock = new(observedAt + SidebarViewModel.HostConnectivityStaleAfter - TimeSpan.FromSeconds(1));
        SidebarViewModel viewModel = new(
            catalog, environment.Application, new FakeFolderPicker(), en, clock: clock, connectivityMonitor: monitor);

        SidebarSnapshot snapshot = await viewModel.LoadAsync(cancellationToken, selectedProjectId);

        Assert.Equal(en.Resolve(MessageKeys.HostConnectivityConnected), snapshot.Status.HostConnectivityText);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
