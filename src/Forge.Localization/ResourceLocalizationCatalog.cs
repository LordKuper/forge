using System.Globalization;
using System.Resources;

namespace Forge.Localization;

public sealed class ResourceLocalizationCatalog : ILocalizationCatalog
{
    private static readonly CultureInfo FallbackCulture = CultureInfo.GetCultureInfo("en");
    private readonly ResourceManager resourceManager =
        new("Forge.Localization.Resources.Messages", typeof(ResourceLocalizationCatalog).Assembly);

    public IReadOnlyCollection<string> SupportedCultures { get; } = ["en", "ru"];

    public string Resolve(string key, CultureInfo? culture = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        CultureInfo selected = Normalize(culture ?? CultureInfo.CurrentUICulture);
        return resourceManager.GetString(key, selected) ??
            resourceManager.GetString(key, FallbackCulture) ??
            throw new MissingManifestResourceException($"Unknown localization key '{key}'.");
    }

    private static CultureInfo Normalize(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo("ru")
            : FallbackCulture;
}
