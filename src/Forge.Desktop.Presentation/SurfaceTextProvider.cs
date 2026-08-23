using Forge.Localization;

namespace Forge.Desktop.Presentation;

/// <summary>
/// Plan section 5.1's "UI-language changes apply without restart." <see cref="SurfaceText"/> itself
/// is an immutable snapshot bound to one culture (by design: every existing CLI/Desktop surface
/// resolves its whole run against one fixed language). This provider is the one addition the
/// workspace shell needs on top of it: a mutable holder every new view-model shares, so saving
/// `language.ui` can swap <see cref="Current"/> and raise <see cref="Changed"/> once, and every
/// view-model that already re-resolves its own display strings from the new value picks up the
/// change immediately, with no process restart and no second localization mechanism.
/// </summary>
public sealed class SurfaceTextProvider
{
    private readonly ILocalizationCatalog catalog;

    public SurfaceTextProvider(ILocalizationCatalog catalog, string initialLanguageTag)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Current = SurfaceText.For(catalog, initialLanguageTag);
    }

    public SurfaceText Current { get; private set; }

    /// <summary>Raised after <see cref="Current"/> changes -- the workspace shell's own pages
    /// subscribe once and re-render their bound strings, matching the reactive-refresh shape plan
    /// section 5.1 asks for without introducing a second binding framework.</summary>
    public event EventHandler? Changed;

    public string Resolve(string key) => Current.Resolve(key);

    public void SetLanguage(string languageTag)
    {
        Current = SurfaceText.For(catalog, languageTag);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
