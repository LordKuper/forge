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
