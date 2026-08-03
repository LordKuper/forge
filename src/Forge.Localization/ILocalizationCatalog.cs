using System.Globalization;

namespace Forge.Localization;

public interface ILocalizationCatalog
{
    string Resolve(string key, CultureInfo? culture = null);

    IReadOnlyCollection<string> SupportedCultures { get; }
}

public static class MessageKeys
{
    public const string AppDescription = "AppDescription";
    public const string AppTitle = "AppTitle";
    public const string StatusDescription = "StatusDescription";
    public const string StatusReady = "StatusReady";
    public const string InstallDescription = "InstallDescription";
    public const string InstallCompleted = "InstallCompleted";
}
