using System.Text.Json;
using Forge.Configuration;

namespace Forge.UnitTests;

public sealed class ConfigurationTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void WrongScopeIsRejectedWithStableDiagnostic()
    {
        ConfigurationRegistry registry = new();

        ConfigurationScopeException error = Assert.Throws<ConfigurationScopeException>(
            () => registry.RequireScope("language.ui", ConfigurationScope.Project));

        Assert.Contains(ConfigurationScopeException.DiagnosticCode, error.Message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UserLanguageInheritancePreservesProvenance()
    {
        ConfigurationRegistry registry = new();
        ConfigurationResolver resolver = new(registry);
        Dictionary<string, JsonElement> values = new(StringComparer.Ordinal)
        {
            ["language.ui"] = JsonSerializer.Deserialize<JsonElement>("\"ru\""),
        };

        EffectiveConfigurationValue effective = resolver.ResolveUser(
            "language.llm",
            new Dictionary<string, JsonElement>(),
            new ConfigurationDocument(1, values));

        Assert.Equal("ru", effective.Value.GetString());
        Assert.Equal(ConfigurationProvenance.Inherited, effective.Provenance);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StoreWritesAtomicallyAndRetainsPrevious()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"forge-tests-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "config.json");
        try
        {
            ConfigurationRegistry registry = new();
            JsonConfigurationStore store = new(path, ConfigurationScope.User, registry);
            JsonElement en = JsonSerializer.Deserialize<JsonElement>("\"en\"");
            JsonElement ru = JsonSerializer.Deserialize<JsonElement>("\"ru\"");

            await store.WriteAsync(
                new(1, new Dictionary<string, JsonElement> { ["language.ui"] = en }),
                TestContext.Current.CancellationToken);
            await store.WriteAsync(
                new(1, new Dictionary<string, JsonElement> { ["language.ui"] = ru }),
                TestContext.Current.CancellationToken);

            ConfigurationDocument current =
                await store.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal("ru", current.Values["language.ui"].GetString());
            Assert.True(File.Exists($"{path}.previous"));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProjectStoreRoundTripsCanonicalYaml()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"forge-tests-{Guid.NewGuid():N}");
        try
        {
            ConfigurationRegistry registry = new();
            ConfigurationStoreFactory factory = new(registry);
            IConfigurationStore store = factory.CreateProjectStore(directory);
            JsonElement language = JsonSerializer.Deserialize<JsonElement>("\"ru\"");

            await store.WriteAsync(
                new(
                    1,
                    new Dictionary<string, JsonElement>
                    {
                        ["artifacts.language.user_facing"] = language,
                    }),
                TestContext.Current.CancellationToken);

            ConfigurationDocument result =
                await store.ReadAsync(TestContext.Current.CancellationToken);
            string manifest = Path.Combine(directory, ".forge", "manifest.yaml");
            Assert.Equal("ru", result.Values["artifacts.language.user_facing"].GetString());
            Assert.Contains("schema_version: 1", await File.ReadAllTextAsync(
                manifest,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
