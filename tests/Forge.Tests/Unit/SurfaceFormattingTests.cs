using System.Globalization;
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
}
