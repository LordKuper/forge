using System.Text.Json;
using Forge.Configuration;

namespace Forge.UnitTests;

public sealed class ConfigurationTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void BclDirectoryDurabilityFailsClosedInsteadOfSilentlyDegrading()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"forge-durability-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            // Pins the assumption every composed IDirectoryDurability adapter (and the test-only portable
            // fallback) depends on: the BCL cannot open a directory handle on any current .NET platform, so the
            // default fails closed rather than silently no-opping. If a future SDK ever makes this succeed, this
            // test starts failing instead of the gap going unnoticed.
            Assert.Throws<UnauthorizedAccessException>(() => new BclDirectoryDurability().Flush(directory));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

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
                        ["artifacts.language.agent_facing"] =
                            JsonSerializer.SerializeToElement("en"),
                    },
                    Guid.Parse("7d634db2-586e-49c0-9da6-69292575be19")),
                TestContext.Current.CancellationToken);

            ConfigurationDocument result =
                await store.ReadAsync(TestContext.Current.CancellationToken);
            string manifest = Path.Combine(directory, ".forge", "manifest.yaml");
            Assert.Equal("ru", result.Values["artifacts.language.user_facing"].GetString());
            string yaml = await File.ReadAllTextAsync(
                manifest,
                TestContext.Current.CancellationToken);
            Assert.Contains("schema_version: 1.2.0", yaml);
            Assert.Contains("project_id: 7d634db2-586e-49c0-9da6-69292575be19", yaml);
            Assert.Contains("workflow: implementation-critical", yaml);
            Assert.Contains("artifacts:", yaml);
            Assert.DoesNotContain("values:", yaml);
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
    public async Task UserStoreWritesCanonicalNestedJson()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"forge-tests-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "config.json");
        try
        {
            JsonConfigurationStore store =
                new(path, ConfigurationScope.User, new ConfigurationRegistry());
            await store.WriteAsync(
                new(
                    1,
                    new Dictionary<string, JsonElement>
                    {
                        ["language.ui"] = JsonSerializer.SerializeToElement("ru"),
                        ["interaction.confirm_destructive"] =
                            JsonSerializer.SerializeToElement(true),
                    }),
                TestContext.Current.CancellationToken);

            using JsonDocument json = JsonDocument.Parse(
                await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal("1.3.0", json.RootElement.GetProperty("schema_version").GetString());
            Assert.Equal(
                "ru",
                json.RootElement.GetProperty("language").GetProperty("ui").GetString());
            Assert.True(
                json.RootElement
                    .GetProperty("interaction")
                    .GetProperty("confirm_destructive")
                    .GetBoolean());
            Assert.False(json.RootElement.TryGetProperty("values", out _));
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
    public async Task AnOrderedProviderEnablementListRoundTripsThroughTheStore()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"forge-tests-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "config.json");
        try
        {
            JsonConfigurationStore store = new(path, ConfigurationScope.User, new ConfigurationRegistry());
            List<string> enabled = ["claude_code", "codex"];
            await store.WriteAsync(
                new(
                    1,
                    new Dictionary<string, JsonElement>
                    {
                        ["providers.enabled"] = JsonSerializer.SerializeToElement(enabled),
                    }),
                TestContext.Current.CancellationToken);

            ConfigurationDocument reloaded = await store.ReadAsync(TestContext.Current.CancellationToken);

            Assert.Equal(
                enabled,
                reloaded.Values["providers.enabled"].EnumerateArray().Select(item => item.GetString()));
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
    public async Task AnExplicitEmptyProviderListIsPreservedDistinctFromOmission()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"forge-tests-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "config.json");
        try
        {
            JsonConfigurationStore store = new(path, ConfigurationScope.User, new ConfigurationRegistry());
            await store.WriteAsync(
                new(
                    1,
                    new Dictionary<string, JsonElement>
                    {
                        ["providers.enabled"] = JsonSerializer.SerializeToElement(Array.Empty<string>()),
                    }),
                TestContext.Current.CancellationToken);

            ConfigurationDocument reloaded = await store.ReadAsync(TestContext.Current.CancellationToken);

            Assert.True(reloaded.Values.ContainsKey("providers.enabled"));
            Assert.Empty(reloaded.Values["providers.enabled"].EnumerateArray());
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
    public async Task APreExistingConfigurationFileWithoutProvidersStillLoadsAndUpgradesOnNextSave()
    {
        // Simulates a file written by a Forge version before providers.enabled existed: literal
        // schema_version "1.0.0", no "providers" key at all. user-config.schema.json's tolerant
        // schema_version enum must still accept it.
        string directory = Path.Combine(Path.GetTempPath(), $"forge-tests-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "config.json");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(
                path,
                """{"schema_version":"1.0.0","language":{"ui":"en"},"interaction":{}}""",
                TestContext.Current.CancellationToken);
            JsonConfigurationStore store = new(path, ConfigurationScope.User, new ConfigurationRegistry());

            ConfigurationDocument reloaded = await store.ReadAsync(TestContext.Current.CancellationToken);
            Assert.False(reloaded.Values.ContainsKey("providers.enabled"));

            await store.WriteAsync(reloaded, TestContext.Current.CancellationToken);
            using JsonDocument upgraded = JsonDocument.Parse(
                await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal("1.3.0", upgraded.RootElement.GetProperty("schema_version").GetString());
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
    public async Task InvalidUserValueDoesNotReplaceValidConfiguration()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"forge-tests-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "config.json");
        try
        {
            JsonConfigurationStore store =
                new(path, ConfigurationScope.User, new ConfigurationRegistry());
            ConfigurationDocument valid = new(
                1,
                new Dictionary<string, JsonElement>
                {
                    ["language.ui"] = JsonSerializer.SerializeToElement("en"),
                });
            await store.WriteAsync(valid, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => store.WriteAsync(
                    new(
                        1,
                        new Dictionary<string, JsonElement>
                        {
                            ["language.ui"] = JsonSerializer.SerializeToElement(42),
                        }),
                    TestContext.Current.CancellationToken));

            ConfigurationDocument current =
                await store.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal("en", current.Values["language.ui"].GetString());
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
    public async Task UserStoreRecoversMalformedCurrentFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"forge-tests-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "config.json");
        try
        {
            JsonConfigurationStore store =
                new(path, ConfigurationScope.User, new ConfigurationRegistry());
            await WriteUserLanguageAsync(store, "en");
            await WriteUserLanguageAsync(store, "ru");
            await File.WriteAllTextAsync(
                path,
                "{broken",
                TestContext.Current.CancellationToken);

            ConfigurationDocument recovered =
                await store.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal("en", recovered.Values["language.ui"].GetString());

            ConfigurationDocument restored =
                await store.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal("en", restored.Values["language.ui"].GetString());
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
    public async Task ProjectStoreRecoversInvalidCurrentFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"forge-tests-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, ".forge", "manifest.yaml");
        try
        {
            YamlConfigurationStore store =
                new(path, ConfigurationScope.Project, new ConfigurationRegistry());
            Guid projectId = Guid.Parse("7d634db2-586e-49c0-9da6-69292575be19");
            await WriteProjectLanguagesAsync(store, projectId, "en");
            await WriteProjectLanguagesAsync(store, projectId, "ru");
            await File.WriteAllTextAsync(
                path,
                "schema_version: invalid",
                TestContext.Current.CancellationToken);

            ConfigurationDocument recovered =
                await store.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(
                "en",
                recovered.Values["artifacts.language.user_facing"].GetString());

            ConfigurationDocument restored =
                await store.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(
                "en",
                restored.Values["artifacts.language.user_facing"].GetString());
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
    public async Task InvalidProjectValueDoesNotReplaceValidConfiguration()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"forge-tests-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, ".forge", "manifest.yaml");
        try
        {
            YamlConfigurationStore store =
                new(path, ConfigurationScope.Project, new ConfigurationRegistry());
            Guid projectId = Guid.Parse("7d634db2-586e-49c0-9da6-69292575be19");
            await WriteProjectLanguagesAsync(store, projectId, "en");

            await Assert.ThrowsAsync<InvalidDataException>(
                () => store.WriteAsync(
                    new(
                        1,
                        new Dictionary<string, JsonElement>
                        {
                            ["artifacts.language.user_facing"] =
                                JsonSerializer.SerializeToElement(42),
                            ["artifacts.language.agent_facing"] =
                                JsonSerializer.SerializeToElement("en"),
                        },
                        projectId),
                    TestContext.Current.CancellationToken));

            ConfigurationDocument current =
                await store.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(
                "en",
                current.Values["artifacts.language.user_facing"].GetString());
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
    public async Task ProjectStoreRejectsMissingRequiredRawField()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"forge-tests-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, ".forge", "manifest.yaml");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(
                path,
                """
                schema_version: 1.0.0
                project_id: 7d634db2-586e-49c0-9da6-69292575be19
                artifacts:
                  language:
                    user_facing: en
                    agent_facing: en
                """,
                TestContext.Current.CancellationToken);
            YamlConfigurationStore store =
                new(path, ConfigurationScope.Project, new ConfigurationRegistry());

            await Assert.ThrowsAsync<InvalidDataException>(
                () => store.ReadAsync(TestContext.Current.CancellationToken));
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
    public async Task ProjectStoreMigratesThePersistedSprintRegistryWithoutExposingIt()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"forge-tests-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, ".forge", "manifest.yaml");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(
                path,
                """
                schema_version: 1.0.0
                project_id: 7d634db2-586e-49c0-9da6-69292575be19
                workflow: implementation-critical
                artifacts:
                  language:
                    user_facing: en
                    agent_facing: en
                sprints:
                  - 44444444-4444-4444-8444-444444444444
                """,
                TestContext.Current.CancellationToken);
            YamlConfigurationStore store =
                new(path, ConfigurationScope.Project, new ConfigurationRegistry());

            ConfigurationDocument document = await store.ReadAsync(TestContext.Current.CancellationToken);
            await store.WriteAsync(document, TestContext.Current.CancellationToken);

            Assert.Equal(Guid.Parse("7d634db2-586e-49c0-9da6-69292575be19"), document.ProjectId);
            Assert.DoesNotContain(
                "sprints:",
                await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken),
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static Task WriteUserLanguageAsync(JsonConfigurationStore store, string language) =>
        store.WriteAsync(
            new(
                1,
                new Dictionary<string, JsonElement>
                {
                    ["language.ui"] = JsonSerializer.SerializeToElement(language),
                }),
            TestContext.Current.CancellationToken);

    private static Task WriteProjectLanguagesAsync(
        YamlConfigurationStore store,
        Guid projectId,
        string language) =>
        store.WriteAsync(
            new(
                1,
                new Dictionary<string, JsonElement>
                {
                    ["artifacts.language.user_facing"] =
                        JsonSerializer.SerializeToElement(language),
                    ["artifacts.language.agent_facing"] =
                        JsonSerializer.SerializeToElement("en"),
                },
                projectId),
            TestContext.Current.CancellationToken);
}
