using System.Globalization;
using Forge.Application;
using Forge.Domain;
using Forge.Localization;
using Forge.Providers;

namespace Forge.UnitTests;

/// <summary>PR #100 review finding 5: <see cref="ProviderQuotaAvailability.Unknown"/> ("no verified
/// signal yet") and <see cref="ProviderQuotaAvailability.Unavailable"/> ("quota is exhausted") are
/// easy to conflate by name, and the <c>Unknown</c> arm used to be spelled `_` -- hiding the two
/// vocabularies' one meeting point behind a wildcard instead of naming it. This proves
/// <see cref="SurfaceFormatting.QuotaStatusSummary"/> resolves every named
/// <see cref="ProviderQuotaAvailability"/> value to its own distinct, correctly paired
/// (text, accessible) message pair -- most importantly that <c>Unknown</c> resolves to
/// <see cref="MessageKeys.QuotaStatusUnknown"/>/<see cref="MessageKeys.QuotaStatusUnknownAccessible"/>,
/// never to the differently-named <see cref="MessageKeys.QuotaStatusDepleted"/> pair reserved for
/// <c>Unavailable</c>.</summary>
public sealed class SurfaceFormattingTests
{
    private static readonly SurfaceText Text = new(new ResourceLocalizationCatalog(), CultureInfo.InvariantCulture);

    private static ProviderQuotaSnapshot Snapshot(ProviderQuotaAvailability availability) =>
        new("codex", "codex-model", availability, null, null, null, DateTimeOffset.UnixEpoch, "diagnostic");

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(ProviderQuotaAvailability.Unknown, MessageKeys.QuotaStatusUnknown, MessageKeys.QuotaStatusUnknownAccessible)]
    [InlineData(ProviderQuotaAvailability.Ready, MessageKeys.QuotaStatusReady, MessageKeys.QuotaStatusReadyAccessible)]
    [InlineData(ProviderQuotaAvailability.Limited, MessageKeys.QuotaStatusLimited, MessageKeys.QuotaStatusLimitedAccessible)]
    [InlineData(ProviderQuotaAvailability.Unavailable, MessageKeys.QuotaStatusDepleted, MessageKeys.QuotaStatusDepletedAccessible)]
    [InlineData(ProviderQuotaAvailability.Stale, MessageKeys.QuotaStatusStale, MessageKeys.QuotaStatusStaleAccessible)]
    public void QuotaStatusSummaryResolvesEveryAvailabilityToItsOwnDistinctMessagePair(
        ProviderQuotaAvailability availability, string expectedTextKey, string expectedAccessibleKey)
    {
        (string text, string accessible) = SurfaceFormatting.QuotaStatusSummary(Text, [Snapshot(availability)]);

        Assert.Equal(Text.Resolve(expectedTextKey), text);
        Assert.Equal(Text.Resolve(expectedAccessibleKey), accessible);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UnknownAndUnavailableNeverResolveToTheSameMessagePair()
    {
        // The two concepts this finding is about must never collapse onto one another: "no signal
        // yet" (Unknown) must read differently from "exhausted" (Unavailable), in both the visible
        // text and the accessible name.
        (string unknownText, string unknownAccessible) =
            SurfaceFormatting.QuotaStatusSummary(Text, [Snapshot(ProviderQuotaAvailability.Unknown)]);
        (string unavailableText, string unavailableAccessible) =
            SurfaceFormatting.QuotaStatusSummary(Text, [Snapshot(ProviderQuotaAvailability.Unavailable)]);

        Assert.NotEqual(unknownText, unavailableText);
        Assert.NotEqual(unknownAccessible, unavailableAccessible);
    }

    private static ControlEventsPage OneEventPage(string messageKey, IReadOnlyDictionary<string, string?> arguments) =>
        new(
            [
                new ControlEventRecord(
                    Guid.NewGuid(),
                    new WorkflowEvent(
                        Guid.NewGuid(),
                        0,
                        DateTimeOffset.UnixEpoch,
                        WorkflowEvent.AttemptSupersededType,
                        new AggregateRef(AggregateKind.Attempt, "attempt-1", 1),
                        messageKey,
                        arguments)),
            ],
            "cursor",
            DiagnosticCodes.None);

    /// <summary>PR #107 round 2 review finding 1 (security regression): unlike the sprint-timeline
    /// render path (three independent <see cref="Infrastructure.SecretRedactor"/> passes), the raw
    /// journal arguments <see cref="TimelineMessageFormatter.Format"/> substitutes into
    /// <see cref="SurfaceFormatting.EventLines"/>'s output went through no redaction pass at all --
    /// a credential-shaped supersession instruction reached `forge events`/Desktop's events view
    /// verbatim even though the exact same string could never reach `forge sprint timeline`
    /// (<c>WorkspaceCliTests.ARawCredentialNeverReachesTheRenderedTimelineInTextOrJsonMode</c>).
    /// Proves the fix: the credential no longer appears anywhere in <see cref="SurfaceFormatting.EventLines"/>'s
    /// rendered output.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void EventLinesRedactsACredentialShapedSupersessionInstruction()
    {
        const string secret = "authorization: Bearer sk-live-1234567890ABCDEFGH";
        Dictionary<string, string?> arguments = new(StringComparer.Ordinal)
        {
            [WorkflowEvent.SupersessionInstructionArgument] = $"Instruction with {secret}",
        };
        ControlEventsPage page = OneEventPage(MessageKeys.WorkflowAttemptSupersededInstruction, arguments);

        IReadOnlyList<string> lines = SurfaceFormatting.EventLines(Text, page);

        string rendered = string.Join('\n', lines);
        Assert.DoesNotContain("sk-live-1234567890ABCDEFGH", rendered, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:authorization]", rendered, StringComparison.Ordinal);
    }

    /// <summary>PR #107 round 2 review finding 2: the free text <see cref="TimelineMessageFormatter.Format"/>
    /// substitutes into <see cref="SurfaceFormatting.EventLines"/>'s output was bounded in length but
    /// not in newline content, so an embedded newline could split one event across multiple physical
    /// lines -- breaking the "one ordered line list" contract every other <see cref="SurfaceFormatting"/>
    /// method upholds. Proves embedded line breaks are collapsed to spaces so exactly one line is
    /// produced per event.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void EventLinesCollapsesEmbeddedNewlinesInFreeTextToASingleLine()
    {
        Dictionary<string, string?> arguments = new(StringComparer.Ordinal)
        {
            [WorkflowEvent.SupersessionInstructionArgument] = "line one\nline two\r\nline three",
        };
        ControlEventsPage page = OneEventPage(MessageKeys.WorkflowAttemptSupersededInstruction, arguments);

        IReadOnlyList<string> lines = SurfaceFormatting.EventLines(Text, page);

        // Title line + exactly one line for the one event -- never more.
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.DoesNotContain('\n', line));
        Assert.All(lines, line => Assert.DoesNotContain('\r', line));
        Assert.Contains("line one line two line three", lines[1], StringComparison.Ordinal);
    }
}
