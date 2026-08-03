using System.Xml.Linq;
using Forge.Localization;

namespace Forge.UnitTests;

public sealed class LocalizationCatalogTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void CatalogResolvesEnglishAndRussian()
    {
        ResourceLocalizationCatalog catalog = new();

        Assert.Equal("Forge is ready.", catalog.Resolve(MessageKeys.StatusReady, new("en-US")));
        Assert.Equal("Forge готов.", catalog.Resolve(MessageKeys.StatusReady, new("ru-RU")));
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

    private static HashSet<string> ReadKeys(string path) =>
        XDocument.Load(path)
            .Root!
            .Elements("data")
            .Select(item => (string)item.Attribute("name")!)
            .ToHashSet(StringComparer.Ordinal);
}
