using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Forge.Domain;
using Forge.Localization;

namespace Forge.UnitTests;

public sealed class LocalizationCatalogTests
{
    /// <summary>Delegates to <paramref name="inner"/> for every key except <paramref name="blockedKey"/>,
    /// which it throws <see cref="MissingManifestResourceException"/> for -- simulates a genuinely
    /// unmapped catalog entry for a key that is, in the real <c>Messages.resx</c> pair, actually
    /// mapped (PR #107 round 2 review finding 3's "hypothetical unmapped label").</summary>
    private sealed class KeyBlockingCatalog(ILocalizationCatalog inner, string blockedKey) : ILocalizationCatalog
    {
        public IReadOnlyCollection<string> SupportedCultures => inner.SupportedCultures;

        public string Resolve(string key, CultureInfo? culture = null) =>
            string.Equals(key, blockedKey, StringComparison.Ordinal)
                ? throw new MissingManifestResourceException($"Simulated unmapped key '{key}'.")
                : inner.Resolve(key, culture);
    }

    /// <summary>Delegates to <paramref name="inner"/> for every key except <paramref name="overriddenKey"/>,
    /// which it resolves to <paramref name="template"/> instead -- lets a test force
    /// <see cref="TimelineMessageFormatter.Format"/>'s <c>string.Format</c> call to see a
    /// deliberately malformed template (PR #107 round 2 review finding 4) without needing an actual
    /// resx typo.</summary>
    private sealed class TemplateOverridingCatalog(ILocalizationCatalog inner, string overriddenKey, string template)
        : ILocalizationCatalog
    {
        public IReadOnlyCollection<string> SupportedCultures => inner.SupportedCultures;

        public string Resolve(string key, CultureInfo? culture = null) =>
            string.Equals(key, overriddenKey, StringComparison.Ordinal) ? template : inner.Resolve(key, culture);
    }
    [Fact]
    [Trait("Category", "Unit")]
    public void CatalogResolvesEnglishAndRussian()
    {
        ResourceLocalizationCatalog catalog = new();

        Assert.Equal("Forge is ready.", catalog.Resolve(MessageKeys.StatusReady, new("en-US")));
        Assert.Equal("Forge готов.", catalog.Resolve(MessageKeys.StatusReady, new("ru-RU")));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CatalogResolvesThePausedSprintStateLabelInEnglishAndRussian()
    {
        ResourceLocalizationCatalog catalog = new();

        Assert.Equal("Paused", catalog.Resolve(MessageKeys.SprintStatePaused, new("en-US")));
        Assert.Equal("Приостановлено", catalog.Resolve(MessageKeys.SprintStatePaused, new("ru-RU")));
    }

    /// <summary>Plan section 12.3: a static timeline key (no durable argument to substitute) still
    /// resolves through the catalog rather than being returned verbatim.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void TimelineMessageFormatterResolvesAStaticWorkflowKeyInEnglishAndRussian()
    {
        SurfaceText english = new(new ResourceLocalizationCatalog(), new("en-US"));
        SurfaceText russian = new(new ResourceLocalizationCatalog(), new("ru-RU"));
        Dictionary<string, string?> noArguments = new(StringComparer.Ordinal);

        Assert.Equal(
            "Sprint completed.",
            TimelineMessageFormatter.Format(english, MessageKeys.WorkflowSprintCompleted, noArguments));
        Assert.Equal(
            "Спринт завершён.",
            TimelineMessageFormatter.Format(russian, MessageKeys.WorkflowSprintCompleted, noArguments));
    }

    /// <summary>Plan section 12.3: a key that carries a durable, user-authored argument (here, the
    /// bounded free-text message on <c>workflow.user_message_posted</c>) substitutes that argument's
    /// exact value into the resolved template in both languages -- the dynamic content is never lost,
    /// even though the raw key alone carries no hint of it.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void TimelineMessageFormatterSubstitutesADurableArgumentIntoTheLocalizedTemplate()
    {
        SurfaceText english = new(new ResourceLocalizationCatalog(), new("en-US"));
        SurfaceText russian = new(new ResourceLocalizationCatalog(), new("ru-RU"));
        Dictionary<string, string?> arguments = new(StringComparer.Ordinal)
        {
            ["message_text"] = "stale finding, rewinding to replan",
        };

        Assert.Equal(
            "Message: stale finding, rewinding to replan",
            TimelineMessageFormatter.Format(english, MessageKeys.WorkflowUserMessagePosted, arguments));
        Assert.Equal(
            "Сообщение: stale finding, rewinding to replan",
            TimelineMessageFormatter.Format(russian, MessageKeys.WorkflowUserMessagePosted, arguments));
    }

    /// <summary>PR #118 review finding 1 (regression): the token-usage summary line renders the whole
    /// attempt's footprint, not the sliver of it that was fresh input and output. Fed with exactly what
    /// the committed Claude capture reports (`tests/Forge.Tests/Unit/fixtures/providers/claude-stream-json-usage.jsonl`:
    /// 6 in, 265 out, 75,666 cache-read, 38,581 cache-creation), a template summing only input and
    /// output rendered `Used 271 token(s)` for an attempt that consumed 114,518 — the defect this pins,
    /// and not an edge case, since cache tokens dominate every real Claude attempt. The four counters
    /// are rendered beside the total in both languages because they are priced differently: the line
    /// reports a footprint and its composition, never an implied price. The arguments below are the
    /// exact ones <c>FileSprintEventLog.AppendAttemptUsageRecordedAsync</c> derives from that payload
    /// (asserted there and in
    /// <c>ImplementationExecutionHostedServiceTests.ASuccessfullyIntegratedAttemptRecordsExactlyOneTokenUsageSummaryFromItsProviderRun</c>).
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void TimelineMessageFormatterRendersEveryTokenAnAttemptSpentIncludingItsCacheCounters()
    {
        SurfaceText english = new(new ResourceLocalizationCatalog(), new("en-US"));
        SurfaceText russian = new(new ResourceLocalizationCatalog(), new("ru-RU"));
        Dictionary<string, string?> arguments = new(StringComparer.Ordinal)
        {
            [WorkflowEvent.UsageTotalTokensArgument] = "114518",
            [WorkflowEvent.UsageInputTokensArgument] = "6",
            [WorkflowEvent.UsageOutputTokensArgument] = "265",
            [WorkflowEvent.UsageCacheReadTokensArgument] = "75666",
            [WorkflowEvent.UsageCacheCreationTokensArgument] = "38581",
        };

        Assert.Equal(
            "Used 114518 token(s): 6 in, 265 out, 75666 cache read, 38581 cache creation.",
            TimelineMessageFormatter.Format(english, MessageKeys.WorkflowAttemptUsageRecorded, arguments));
        Assert.Equal(
            "Израсходовано токенов: 114518; на входе: 6; на выходе: 265; чтение кэша: 75666; " +
                "создание кэша: 38581.",
            TimelineMessageFormatter.Format(russian, MessageKeys.WorkflowAttemptUsageRecorded, arguments));
    }

    /// <summary>PR #107 review finding 1 (regression): <see cref="TimelineMessageFormatter.Format"/>
    /// used to call <see cref="SurfaceText.Resolve"/> unguarded, so any `workflow.`/`routing.`
    /// message key without a <c>Messages.resx</c> entry threw <see cref="System.Resources.MissingManifestResourceException"/>
    /// and crashed the whole timeline render. That is genuinely reachable: <c>ISprintStore.AppendTransitionAsync</c>
    /// accepts an arbitrary string with no closed-set validation, and <c>SprintTimelineRedaction.Apply</c>
    /// rewrites <c>MessageKey</c> through <c>SecretRedactor</c> right before it reaches this method,
    /// guaranteeing it will not resolve. Proves the fix: an unmapped key now renders as the raw key
    /// itself -- the pre-this-feature behavior -- instead of throwing.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void TimelineMessageFormatterFallsBackToTheRawKeyForAnUnmappedMessageKeyInsteadOfThrowing()
    {
        SurfaceText text = new(new ResourceLocalizationCatalog(), new("en-US"));
        Dictionary<string, string?> noArguments = new(StringComparer.Ordinal);

        string rendered = TimelineMessageFormatter.Format(text, "workflow.not_a_registered_key", noArguments);

        Assert.Equal("workflow.not_a_registered_key", rendered);
    }

    /// <summary>PR #107 review finding 3: `workflow.sprint_blocked`'s `{0}` used to be the raw
    /// snake_case `blocked_reason` code (e.g. "review_convergence"), a machine-only token inside
    /// otherwise-localized prose. Proves the specific representative value from the review comment
    /// now renders a real translated phrase in both languages instead of the raw code.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void TimelineMessageFormatterLocalizesTheSprintBlockedReasonCodeInsteadOfInterpolatingItRaw()
    {
        SurfaceText english = new(new ResourceLocalizationCatalog(), new("en-US"));
        SurfaceText russian = new(new ResourceLocalizationCatalog(), new("ru-RU"));
        Dictionary<string, string?> arguments = new(StringComparer.Ordinal)
        {
            [WorkflowEvent.BlockedReasonArgument] = "review_convergence",
        };

        string englishText = TimelineMessageFormatter.Format(english, MessageKeys.WorkflowSprintBlocked, arguments);
        string russianText = TimelineMessageFormatter.Format(russian, MessageKeys.WorkflowSprintBlocked, arguments);

        Assert.Equal("Sprint blocked (review convergence).", englishText);
        Assert.Equal("Спринт заблокирован (сведение результатов проверки).", russianText);
        Assert.DoesNotContain("review_convergence", englishText, StringComparison.Ordinal);
        Assert.DoesNotContain("review_convergence", russianText, StringComparison.Ordinal);
    }

    /// <summary>PR #107 review finding 4: `workflow.attempt_transitioned`'s `{0}` used to be the raw
    /// snake_case <see cref="AttemptState"/> value -- the timeline's highest-frequency entry. Proves
    /// the specific representative value from the review comment ("validating") now renders a real
    /// translated phrase in both languages instead of the raw state name.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void TimelineMessageFormatterLocalizesTheAttemptToStateInsteadOfInterpolatingItRaw()
    {
        SurfaceText english = new(new ResourceLocalizationCatalog(), new("en-US"));
        SurfaceText russian = new(new ResourceLocalizationCatalog(), new("ru-RU"));
        Dictionary<string, string?> arguments = new(StringComparer.Ordinal)
        {
            [WorkflowEvent.ToStateArgument] = "validating",
        };

        string englishText = TimelineMessageFormatter.Format(english, MessageKeys.WorkflowAttemptTransitioned, arguments);
        string russianText = TimelineMessageFormatter.Format(russian, MessageKeys.WorkflowAttemptTransitioned, arguments);

        Assert.Equal("Attempt transitioned to \"Validating\".", englishText);
        Assert.Equal("Попытка перешла в состояние «Проверяется».", russianText);
        Assert.DoesNotContain("validating", englishText, StringComparison.Ordinal);
        Assert.DoesNotContain("validating", russianText, StringComparison.Ordinal);
    }

    /// <summary>PR #107 review finding 5: `routing.decision_recorded`'s `{2}` used to be the raw
    /// snake_case <see cref="RouteOutcome"/> value, the only part of that sentence carrying actual
    /// meaning (`{0}`/`{1}`, provider/model ids, correctly stay verbatim). Proves the specific
    /// representative outcome now renders a real translated phrase in both languages instead of the
    /// raw outcome code.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void TimelineMessageFormatterLocalizesTheRoutingOutcomeInsteadOfInterpolatingItRaw()
    {
        SurfaceText english = new(new ResourceLocalizationCatalog(), new("en-US"));
        SurfaceText russian = new(new ResourceLocalizationCatalog(), new("ru-RU"));
        Dictionary<string, string?> arguments = new(StringComparer.Ordinal)
        {
            ["provider"] = "claude",
            ["model"] = "sonnet",
            ["outcome"] = "budget_exhausted",
        };

        string englishText = TimelineMessageFormatter.Format(english, MessageKeys.RoutingDecisionRecorded, arguments);
        string russianText = TimelineMessageFormatter.Format(russian, MessageKeys.RoutingDecisionRecorded, arguments);

        Assert.Equal("Routed to claude/sonnet: budget exhausted.", englishText);
        Assert.Equal("Маршрутизировано на claude/sonnet: бюджет исчерпан.", russianText);
        Assert.DoesNotContain("budget_exhausted", englishText, StringComparison.Ordinal);
        Assert.DoesNotContain("budget_exhausted", russianText, StringComparison.Ordinal);
    }

    /// <summary>PR #107 round 2 review finding 3 (regression): <c>BlockedReasonLabel</c>,
    /// <c>AttemptStateLabel</c>, and <c>RoutingOutcomeLabel</c> used to call <see cref="SurfaceText.Resolve"/>
    /// unguarded for their own label keys -- reintroducing round 1 finding 1's exact crash class,
    /// just for the 20 PascalCase label keys instead of the `workflow.`/`routing.` snake_case ones.
    /// Simulates a genuinely unmapped label key via <see cref="KeyBlockingCatalog"/> for each of the
    /// three helpers and proves none of them throw -- each falls back to the raw, un-localized code
    /// instead.</summary>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(
        MessageKeys.WorkflowSprintBlocked, WorkflowEvent.BlockedReasonArgument, "node",
        MessageKeys.SprintBlockedReasonNode)]
    [InlineData(
        MessageKeys.WorkflowAttemptTransitioned, WorkflowEvent.ToStateArgument, "succeeded",
        MessageKeys.AttemptStateSucceeded)]
    public void TimelineMessageFormatterFallsBackToTheRawCodeWhenItsLabelKeyIsUnmapped(
        string messageKey, string argumentKey, string rawCode, string labelKey)
    {
        SurfaceText text = new(new KeyBlockingCatalog(new ResourceLocalizationCatalog(), labelKey), new("en-US"));
        Dictionary<string, string?> arguments = new(StringComparer.Ordinal) { [argumentKey] = rawCode };

        string rendered = TimelineMessageFormatter.Format(text, messageKey, arguments);

        Assert.Contains(rawCode, rendered, StringComparison.Ordinal);
    }

    /// <summary>Same regression as <see cref="TimelineMessageFormatterFallsBackToTheRawCodeWhenItsLabelKeyIsUnmapped"/>,
    /// covering <c>RoutingOutcomeLabel</c> separately since it needs three arguments rather than
    /// one.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void TimelineMessageFormatterFallsBackToTheRawRoutingOutcomeWhenItsLabelKeyIsUnmapped()
    {
        SurfaceText text = new(
            new KeyBlockingCatalog(new ResourceLocalizationCatalog(), MessageKeys.RoutingOutcomeRouted),
            new("en-US"));
        Dictionary<string, string?> arguments = new(StringComparer.Ordinal)
        {
            ["provider"] = "claude",
            ["model"] = "sonnet",
            ["outcome"] = "routed",
        };

        string rendered = TimelineMessageFormatter.Format(text, MessageKeys.RoutingDecisionRecorded, arguments);

        Assert.Contains("routed", rendered, StringComparison.Ordinal);
    }

    /// <summary>PR #107 round 2 review finding 4 (regression): <see cref="TimelineMessageFormatter.Format"/>'s
    /// unmapped-key guard did not cover <c>string.Format</c> itself throwing <see cref="FormatException"/>
    /// on a mismatched placeholder -- not hypothetical, since `workflow.stage_revision_recorded`'s
    /// own resx template already uses out-of-order placeholders in both EN and RU. Forces that exact
    /// failure shape via <see cref="TemplateOverridingCatalog"/> (a placeholder index one past the
    /// three supplied arguments) and proves it falls back to the raw key instead of throwing.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void TimelineMessageFormatterFallsBackToTheRawKeyWhenTheResolvedTemplateHasAMismatchedPlaceholder()
    {
        SurfaceText text = new(
            new TemplateOverridingCatalog(
                new ResourceLocalizationCatalog(), MessageKeys.WorkflowStageRevisionRecorded, "{0} {1} {2} {3}"),
            new("en-US"));
        Dictionary<string, string?> arguments = new(StringComparer.Ordinal)
        {
            [WorkflowEvent.TargetStageIdArgument] = "stage-1",
            [WorkflowEvent.RewindReasonArgument] = "stale finding",
            [WorkflowEvent.RevisionArgument] = "3",
        };

        string rendered =
            TimelineMessageFormatter.Format(text, MessageKeys.WorkflowStageRevisionRecorded, arguments);

        Assert.Equal(MessageKeys.WorkflowStageRevisionRecorded, rendered);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void BuiltInCatalogsHaveIdenticalKeys()
    {
        string root = RepositoryRoot.Find();
        string resources = Path.Combine(root, "src", "Forge.Runtime", "Localization", "Resources");
        HashSet<string> english = ReadKeys(Path.Combine(resources, "Messages.resx"));
        HashSet<string> russian = ReadKeys(Path.Combine(resources, "Messages.ru.resx"));

        Assert.Equal(english, russian);
    }

    /// <summary>Plan section 12.6 closure: <see cref="BuiltInCatalogsHaveIdenticalKeys"/> only proves
    /// every key exists on both sides -- it would pass just as well if a future regression copied an
    /// English value into <c>Messages.ru.resx</c> verbatim, leaving that one key silently
    /// un-translated. This asserts every key present in both files has a byte-different value,
    /// except <see cref="AllowedIdenticalKeys"/> -- keys legitimately expected to match. That
    /// allow-list was built by scanning the current resx pair for every key whose English and Russian
    /// values already happen to be identical today (one match, found and reviewed for this PR:
    /// <c>AppTitle</c> = "Forge", the product's own brand name, which must never be translated) --
    /// never guessed blind. A key added here without a real justification defeats the whole test, so
    /// each entry is enumerated individually (not, say, a prefix or regex allowance) and the assertion
    /// below fails loudly if the resx pair ever contains an identical-value key this list does not
    /// name.</summary>
    private static readonly Dictionary<string, string> AllowedIdenticalKeys = new(StringComparer.Ordinal)
    {
        ["AppTitle"] = "Forge's own product/brand name (\"Forge\") -- a proper noun, never translated.",
        ["SprintStatusHeaderProviderModelText"] =
            "A bare \"{0} / {1}\" placeholder template with no natural-language content -- the " +
            "surrounding label already carries the translated meaning; there is nothing here to translate.",
    };

    [Fact]
    [Trait("Category", "Architecture")]
    public void BuiltInCatalogsHaveDistinctEnglishAndRussianValuesExceptTheDocumentedAllowList()
    {
        string root = RepositoryRoot.Find();
        string resources = Path.Combine(root, "src", "Forge.Runtime", "Localization", "Resources");
        Dictionary<string, string> english = ReadValues(Path.Combine(resources, "Messages.resx"));
        Dictionary<string, string> russian = ReadValues(Path.Combine(resources, "Messages.ru.resx"));

        List<string> untranslated = [];
        foreach ((string key, string englishValue) in english)
        {
            if (!russian.TryGetValue(key, out string? russianValue))
            {
                continue;
            }

            if (string.Equals(englishValue, russianValue, StringComparison.Ordinal) &&
                !AllowedIdenticalKeys.ContainsKey(key))
            {
                untranslated.Add(key);
            }
        }

        Assert.True(
            untranslated.Count == 0,
            $"Key(s) with an identical English/Russian value and no allow-list justification: " +
                $"{string.Join(", ", untranslated)}");

        // Every allow-listed key must still exist and still actually be identical -- an entry that
        // stops matching (someone translated it) or stops existing (the key was removed) must be
        // pruned from the list rather than silently left stale, so the list only ever documents real,
        // current exceptions.
        foreach ((string key, string _) in AllowedIdenticalKeys)
        {
            Assert.True(english.TryGetValue(key, out string? englishValue), $"Allow-listed key '{key}' no longer exists in Messages.resx.");
            Assert.True(russian.TryGetValue(key, out string? russianValue), $"Allow-listed key '{key}' no longer exists in Messages.ru.resx.");
            Assert.Equal(englishValue, russianValue);
        }
    }

    /// <summary>PR #107 review finding 2: <see cref="BuiltInCatalogsHaveIdenticalKeys"/> only proves
    /// the two resx files agree with each other -- nothing guarded the property the whole
    /// timeline-localization feature rests on, that every `workflow.`/`routing.`
    /// <see cref="WorkflowEvent.MessageKey"/> literal anywhere in this repository actually has a
    /// resx entry. Five such literals (`workflow.sprint_failed`, `workflow.attempt_cancelled`,
    /// `workflow.attempt_preparing`, `workflow.attempt_running`, `workflow.attempt_validating`) were
    /// found unmapped by grepping the repository for this review -- exercised only by test fixtures
    /// today (<c>NotificationProjectorTests</c>/<c>SprintEventStoreTests</c>, both calling
    /// <c>ISprintStore.AppendTransitionAsync</c> directly, which accepts an arbitrary string with no
    /// closed-set validation), but exactly the shape a future production key would take. Scans every
    /// <c>.cs</c> file under <c>src/</c> and <c>tests/</c> for a quoted `workflow.`/`routing.` string
    /// literal and asserts each one (excluding <see cref="NonMessageKeyWorkflowLiterals"/> -- a
    /// different, deliberately unregistered vocabulary) has an entry in both resx files.</summary>
    private static readonly HashSet<string> NonMessageKeyWorkflowLiterals = new(StringComparer.Ordinal)
    {
        // Forge.Presentation.PresentationContracts.CapabilityIds: capability identifiers, never a
        // durable WorkflowEvent.MessageKey the journal actually persists.
        "workflow.review",
        "workflow.confirm",
        "workflow.test_work",
        "workflow.finalize",
        "workflow.stop_operation",
        "workflow.assess_stage_transition",

        // TimelineMessageFormatterFallsBackToTheRawKeyForAnUnmappedMessageKeyInsteadOfThrowing's own
        // deliberately-unregistered fixture key (PR #107 review finding 1's regression test) --
        // never a real WorkflowEvent.MessageKey, must never gain a resx entry.
        "workflow.not_a_registered_key",
    };

    private static readonly Regex MessageKeyLiteralPattern =
        new("\"((?:workflow|routing)\\.[a-z][a-z0-9_]*)\"", RegexOptions.Compiled);

    [Fact]
    [Trait("Category", "Architecture")]
    public void EveryWorkflowAndRoutingMessageKeyLiteralInTheRepositoryIsRegisteredInBothCatalogs()
    {
        string root = RepositoryRoot.Find();
        string resources = Path.Combine(root, "src", "Forge.Runtime", "Localization", "Resources");
        HashSet<string> english = ReadKeys(Path.Combine(resources, "Messages.resx"));
        HashSet<string> russian = ReadKeys(Path.Combine(resources, "Messages.ru.resx"));

        List<string> missing =
        [
            .. FindMessageKeyLiterals(root)
                .Where(key => !english.Contains(key) || !russian.Contains(key))
                .OrderBy(key => key, StringComparer.Ordinal),
        ];

        Assert.True(
            missing.Count == 0,
            "workflow./routing. message key literal(s) found in the repository with no " +
                $"Messages.resx/Messages.ru.resx entry: {string.Join(", ", missing)}");
    }

    private static HashSet<string> FindMessageKeyLiterals(string root)
    {
        HashSet<string> literals = new(StringComparer.Ordinal);
        foreach (string relativeDirectory in new[] { "src", "tests" })
        {
            string directory = Path.Combine(root, relativeDirectory);
            foreach (string file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                foreach (Match match in MessageKeyLiteralPattern.Matches(File.ReadAllText(file)))
                {
                    string key = match.Groups[1].Value;
                    if (!NonMessageKeyWorkflowLiterals.Contains(key))
                    {
                        literals.Add(key);
                    }
                }
            }
        }

        return literals;
    }

    private static HashSet<string> ReadKeys(string path) =>
        XDocument.Load(path)
            .Root!
            .Elements("data")
            .Select(item => (string)item.Attribute("name")!)
            .ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, string> ReadValues(string path) =>
        XDocument.Load(path)
            .Root!
            .Elements("data")
            .ToDictionary(
                item => (string)item.Attribute("name")!,
                item => (string?)item.Element("value") ?? string.Empty,
                StringComparer.Ordinal);
}
