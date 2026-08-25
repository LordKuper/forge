using System.CommandLine;
using System.Globalization;
using Forge.Application;
using Forge.Cli;
using Forge.Domain;
using Forge.Localization;
using Forge.Tests.Support;

namespace Forge.AcceptanceTests;

/// <summary>Plan sections 6.2-6.4's reserved `workspace.summary`/`sprint.timeline`/
/// `workspace.available_actions` queries, wired to `forge workspace summary`, `forge sprint
/// timeline`, and `forge workspace actions`. Each stays reserved (ADR 0043/0049: no Desktop control
/// yet, so none enters <c>CapabilityIds.Implemented</c>) but ships a real, tested CLI half.</summary>
public sealed class WorkspaceCliTests
{
    private static readonly IReadOnlyList<NodeDefinition> OneNodeGraph = [new("a", NodeKind.Work, [])];

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task WorkspaceSummaryAggregatesEveryCatalogedProject()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        string secondRoot = Path.Combine(environment.Root, "second-project");
        Directory.CreateDirectory(secondRoot);
        await environment.InitializeAsync(secondRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        await catalog.AddAsync(secondRoot, cancellationToken);
        StringWriter output = new(CultureInfo.InvariantCulture);
        RootCommand root = CliApplication.CreateRootCommand(
            Text(), output, environment.Application, catalog: catalog);

        int exitCode = await root.Parse(["workspace", "summary", "--json"])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.Equal(0, exitCode);
        string json = output.ToString();
        Assert.Contains(environment.ProjectRoot.Replace("\\", "\\\\", StringComparison.Ordinal), json, StringComparison.Ordinal);
        Assert.Contains(secondRoot.Replace("\\", "\\\\", StringComparison.Ordinal), json, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task WorkspaceActionsListsProjectLevelActionsWhenNoSprintIsGiven()
    {
        using TestEnvironment environment = new();
        StringWriter output = new(CultureInfo.InvariantCulture);
        RootCommand root = CliApplication.CreateRootCommand(Text(), output, environment.Application);

        int exitCode = await root
            .Parse(["workspace", "actions", "--project-root", environment.ProjectRoot, "--json"])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains(ForgeApplication.InitializeProjectAction, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task SprintTimelineReadsAndPagesThroughTheProjectedJournal()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        StringWriter output = new(CultureInfo.InvariantCulture);
        RootCommand root = CliApplication.CreateRootCommand(Text(), output, environment.Application);

        int exitCode = await root
            .Parse([
                "sprint", "timeline", sprintId.Value.ToString(), "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.Equal(0, exitCode);
        Assert.False(string.IsNullOrWhiteSpace(output.ToString()));
    }

    /// <summary>Plan section 12.3's timeline localization closure: before this, <c>WriteTimeline</c>
    /// rendered <c>item.MessageKey</c> verbatim (the raw `workflow.*` journal key), never resolved
    /// through the localization catalog. Proves the fix at the actual rendered CLI surface in both
    /// registered languages, not merely that <see cref="TimelineMessageFormatter"/> resolves the key
    /// in isolation.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task SprintTimelineRendersLocalizedTextInsteadOfTheRawMessageKey()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;

        StringWriter englishOutput = new(CultureInfo.InvariantCulture);
        RootCommand englishRoot = CliApplication.CreateRootCommand(Text(), englishOutput, environment.Application);
        await englishRoot
            .Parse(["sprint", "timeline", sprintId.Value.ToString(), "--project-root", environment.ProjectRoot])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        StringWriter russianOutput = new(CultureInfo.InvariantCulture);
        RootCommand russianRoot = CliApplication.CreateRootCommand(
            new SurfaceText(new ResourceLocalizationCatalog(), new CultureInfo("ru-RU")),
            russianOutput,
            environment.Application);
        await russianRoot
            .Parse(["sprint", "timeline", sprintId.Value.ToString(), "--project-root", environment.ProjectRoot])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        string english = englishOutput.ToString();
        string russian = russianOutput.ToString();
        Assert.DoesNotContain(MessageKeys.WorkflowSprintCreated, english, StringComparison.Ordinal);
        Assert.DoesNotContain(MessageKeys.WorkflowSprintCreated, russian, StringComparison.Ordinal);
        Assert.Contains("Sprint created.", english, StringComparison.Ordinal);
        Assert.Contains("Спринт создан.", russian, StringComparison.Ordinal);
    }

    /// <summary>Plan 12.3's redaction guarantee, proven at the actual rendered CLI surface (the
    /// second, independent pass — see <c>CliApplication.WriteTimeline</c>): a raw credential-like
    /// string recorded in a human-authored supersession instruction must never reach stdout, whether
    /// rendered as text or as `--json`.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task ARawCredentialNeverReachesTheRenderedTimelineInTextOrJsonMode()
    {
        using TestEnvironment environment = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: OneNodeGraph), cancellationToken)).SprintId!;
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, 1, SprintOrchestrator.RunSprintKey(
                (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!)),
            cancellationToken);
        await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, toReady.Sprint!.Version,
                SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);
        StartAttemptResult started =
            await scheduler.StartAttemptAsync(environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        const string secret = "authorization: Bearer sk-live-1234567890ABCDEFGH";
        await store.AppendAttemptSupersededAsync(
            environment.ProjectRoot, sprintId, started.AttemptId!, $"Instruction with {secret}", cancellationToken);

        StringWriter textOutput = new(CultureInfo.InvariantCulture);
        RootCommand textRoot = CliApplication.CreateRootCommand(Text(), textOutput, environment.Application);
        int textExitCode = await textRoot
            .Parse([
                "sprint", "timeline", sprintId.Value.ToString(), "--project-root", environment.ProjectRoot,
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        StringWriter jsonOutput = new(CultureInfo.InvariantCulture);
        RootCommand jsonRoot = CliApplication.CreateRootCommand(Text(), jsonOutput, environment.Application);
        int jsonExitCode = await jsonRoot
            .Parse([
                "sprint", "timeline", sprintId.Value.ToString(), "--project-root", environment.ProjectRoot, "--json",
            ])
            .InvokeAsync(new InvocationConfiguration(), cancellationToken);

        Assert.Equal(0, textExitCode);
        Assert.Equal(0, jsonExitCode);
        Assert.DoesNotContain("sk-live-1234567890ABCDEFGH", textOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live-1234567890ABCDEFGH", jsonOutput.ToString(), StringComparison.Ordinal);
    }

    private static SurfaceText Text() => new(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture);
}
