using Forge.Desktop.Presentation;
using Forge.Localization;

namespace Forge.UnitTests;

public sealed class SurfaceTextProviderTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ResolveUsesTheInitialLanguage()
    {
        SurfaceTextProvider provider = new(new ResourceLocalizationCatalog(), "en");

        Assert.Equal("Forge is ready.", provider.Resolve(MessageKeys.StatusReady));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SetLanguageUpdatesCurrentAndRaisesChangedWithoutARestart()
    {
        SurfaceTextProvider provider = new(new ResourceLocalizationCatalog(), "en");
        int changedCalls = 0;
        provider.Changed += (_, _) => changedCalls++;

        provider.SetLanguage("ru");

        Assert.Equal(1, changedCalls);
        Assert.Equal("Forge готов.", provider.Resolve(MessageKeys.StatusReady));
        Assert.Equal("ru", provider.Current.Culture.TwoLetterISOLanguageName);
    }
}
