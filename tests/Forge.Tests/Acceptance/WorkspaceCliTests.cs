using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Forge.Application;
using Forge.Cli;
using Forge.Domain;
using Forge.Localization;
using Forge.Providers;
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

    /// <summary>ADR 0059, proven at the actual rendered CLI surface in both modes: the plain-text
    /// render must show the localized one-line summary (never the raw
    /// <c>workflow.attempt_diff_recorded</c> journal key), and `--json` must carry the full structured
    /// payload -- the per-file rows are the whole reason the envelope gained a `payload` object, and
    /// they are the one thing the text line deliberately does not show.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task SprintTimelineRendersTheDiffSummaryAsTextAndCarriesItsStructuredPayloadInJsonMode()
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
        await store.AppendAttemptDiffRecordedAsync(
            environment.ProjectRoot,
            sprintId,
            started.AttemptId!,
            new DiffPayload(
                2,
                12,
                3,
                [
                    new DiffFileStat("src/Widget.cs", 12, 3, DiffChangeKinds.Modified),
                    new DiffFileStat("assets/icon.png", 0, 0, DiffChangeKinds.Binary),
                ],
                0),
            cancellationToken);

        StringWriter textOutput = new(CultureInfo.InvariantCulture);
        RootCommand textRoot = CliApplication.CreateRootCommand(Text(), textOutput, environment.Application);
        int textExitCode = await textRoot
            .Parse(["sprint", "timeline", sprintId.Value.ToString(), "--project-root", environment.ProjectRoot])
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
        string text = textOutput.ToString();
        Assert.DoesNotContain(MessageKeys.WorkflowAttemptDiffRecorded, text, StringComparison.Ordinal);
        Assert.Contains("Changed 2 file(s): +12/-3 lines.", text, StringComparison.Ordinal);

        using JsonDocument json = JsonDocument.Parse(jsonOutput.ToString());
        JsonElement diff = json.RootElement
            .GetProperty("items")
            .EnumerateArray()
            .Single(item => item.GetProperty("type").GetString() == WorkflowEvent.AttemptDiffRecordedType)
            .GetProperty("payload")
            .GetProperty("diff");
        Assert.Equal(2, diff.GetProperty("files_changed").GetInt32());
        Assert.Equal(12, diff.GetProperty("insertions").GetInt32());
        Assert.Equal(3, diff.GetProperty("deletions").GetInt32());
        Assert.Equal(0, diff.GetProperty("elided_files").GetInt32());
        JsonElement[] files = [.. diff.GetProperty("files").EnumerateArray()];
        Assert.Equal(2, files.Length);
        Assert.Equal("src/Widget.cs", files[0].GetProperty("path").GetString());
        Assert.Equal(DiffChangeKinds.Binary, files[1].GetProperty("change_kind").GetString());
    }

    /// <summary>ADR 0060, at the same rendered CLI surface and for the same reason: the slice adds no
    /// CLI code at all, so this is what proves the claim -- the localized one-line summary appears in
    /// the plain-text render and the structured per-call rows ride out through `--json`, both purely
    /// by virtue of the generic timeline rendering that already existed. Also pins the nullable
    /// per-call fields' shape on THIS surface: `--json` writes them as explicit nulls, so a consumer
    /// always sees one complete object shape. That is the opposite of the durable journal line, which
    /// omits an absent field -- a different serializer on a different path, pinned by
    /// `SprintEventStoreTests.AnAttemptToolUsePayloadSurvivesTheJournalRoundTripWithItsPerCallRowsIntact`.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task SprintTimelineRendersTheToolUseSummaryAsTextAndCarriesItsStructuredPayloadInJsonMode()
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
        await store.AppendAttemptToolUseRecordedAsync(
            environment.ProjectRoot,
            sprintId,
            started.AttemptId!,
            new ToolUsePayload(
                3,
                2,
                1,
                [
                    new ToolCallStat(ProviderToolCallKinds.Command, null, 812, 0, true),
                    new ToolCallStat(ProviderToolCallKinds.Command, null, 91, 137, false),
                    new ToolCallStat(ProviderToolCallKinds.Edit, "src/Widget.cs", 40, null, null),
                ],
                0,
                0),
            cancellationToken);

        StringWriter textOutput = new(CultureInfo.InvariantCulture);
        RootCommand textRoot = CliApplication.CreateRootCommand(Text(), textOutput, environment.Application);
        int textExitCode = await textRoot
            .Parse(["sprint", "timeline", sprintId.Value.ToString(), "--project-root", environment.ProjectRoot])
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
        string text = textOutput.ToString();
        Assert.DoesNotContain(MessageKeys.WorkflowAttemptToolUseRecorded, text, StringComparison.Ordinal);
        Assert.Contains("Used 3 tool call(s): 2 command(s), 1 file edit(s).", text, StringComparison.Ordinal);

        using JsonDocument json = JsonDocument.Parse(jsonOutput.ToString());
        JsonElement toolUse = json.RootElement
            .GetProperty("items")
            .EnumerateArray()
            .Single(item => item.GetProperty("type").GetString() == WorkflowEvent.AttemptToolUseRecordedType)
            .GetProperty("payload")
            .GetProperty("tool_use");
        Assert.Equal(3, toolUse.GetProperty("tool_calls").GetInt32());
        Assert.Equal(2, toolUse.GetProperty("commands").GetInt32());
        Assert.Equal(1, toolUse.GetProperty("edits").GetInt32());
        Assert.Equal(0, toolUse.GetProperty("elided_calls").GetInt32());
        Assert.Equal(0, toolUse.GetProperty("unmapped_items").GetInt32());
        JsonElement[] calls = [.. toolUse.GetProperty("calls").EnumerateArray()];
        Assert.Equal(3, calls.Length);
        Assert.Equal(ProviderToolCallKinds.Command, calls[0].GetProperty("kind").GetString());
        // Explicit, not omitted: `StatusJson` serializes with JsonIgnoreCondition.Never. See the
        // summary above for the contrast with the journal's own shape.
        Assert.Equal(JsonValueKind.Null, calls[0].GetProperty("target").ValueKind);
        Assert.Equal(137, calls[1].GetProperty("exit_code").GetInt32());
        Assert.False(calls[1].GetProperty("succeeded").GetBoolean());
        Assert.Equal("src/Widget.cs", calls[2].GetProperty("target").GetString());
        Assert.Equal(JsonValueKind.Null, calls[2].GetProperty("exit_code").ValueKind);
    }

    /// <summary>ADR 0061's "no CLI code change" claim, kept honest the same way ADR 0059/0060's were:
    /// a third payload family reaches both CLI surfaces through the generic timeline rendering that
    /// already existed. Also pins the absent-vs-zero distinction as it appears on THIS surface —
    /// `--json` writes an unreported field as an explicit null (`StatusJson` uses
    /// `JsonIgnoreCondition.Never`), so a consumer always sees one complete object shape and can still
    /// tell a null from a 0. That is the opposite of the durable journal line, which omits an absent
    /// field entirely; a different serializer on a different path, pinned by
    /// `SprintEventStoreTests.AnAttemptUsagePayloadSurvivesTheJournalRoundTripKeepingAbsentAndZeroDistinct`.
    /// </summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task SprintTimelineRendersTheTokenUsageSummaryAsTextAndCarriesItsStructuredPayloadInJsonMode()
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
        await store.AppendAttemptUsageRecordedAsync(
            environment.ProjectRoot,
            sprintId,
            started.AttemptId!,
            new UsagePayload(88_641, 544, 72_448, 0, null),
            cancellationToken);

        StringWriter textOutput = new(CultureInfo.InvariantCulture);
        RootCommand textRoot = CliApplication.CreateRootCommand(Text(), textOutput, environment.Application);
        int textExitCode = await textRoot
            .Parse(["sprint", "timeline", sprintId.Value.ToString(), "--project-root", environment.ProjectRoot])
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
        string text = textOutput.ToString();
        Assert.DoesNotContain(MessageKeys.WorkflowAttemptUsageRecorded, text, StringComparison.Ordinal);
        // Asserted with the whole render as the failure message rather than through Assert.Contains,
        // which truncates it: this assertion is what caught the argument keys originally being named
        // `total_tokens`/`input_tokens`/`output_tokens`, which SecretRedactor rewrote to
        // `[REDACTED:token]` on every surface because it matches `token` anywhere in a KEY NAME (see
        // WorkflowEvent.UsageTotalTokensArgument's remarks). Seeing the actual line is the difference
        // between diagnosing that in a minute and in an hour.
        Assert.True(text.Contains("Used 89185 token(s): 88641 in, 544 out.", StringComparison.Ordinal), text);

        using JsonDocument json = JsonDocument.Parse(jsonOutput.ToString());
        JsonElement usage = json.RootElement
            .GetProperty("items")
            .EnumerateArray()
            .Single(item => item.GetProperty("type").GetString() == WorkflowEvent.AttemptUsageRecordedType)
            .GetProperty("payload")
            .GetProperty("usage");
        Assert.Equal(88_641, usage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(544, usage.GetProperty("output_tokens").GetInt32());
        Assert.Equal(72_448, usage.GetProperty("cache_read_tokens").GetInt32());
        // Reported as zero, and rendered as zero -- not conflated with the unreported field below it.
        Assert.Equal(0, usage.GetProperty("cache_creation_tokens").GetInt32());
        // Codex publishes no context window; absence stays absent rather than becoming a guessed number.
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("context_window").ValueKind);
    }

    private static SurfaceText Text() => new(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture);
}
