using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Application;
using Forge.Configuration;
using Forge.Domain;
using Forge.Tests.Support;
using Json.Schema;

namespace Forge.Tests.Contracts;

public sealed class ContractTests
{
    /// <summary>Round 1 review of PR #94 found `project-snapshot.schema.json`'s `$defs.sprint.state`
    /// stayed a closed enum without `paused` even after `state-machines.json` 1.2.0 added
    /// `running -&gt; paused` and <see cref="SprintState"/> gained <see cref="SprintState.Paused"/> —
    /// so the very first paused sprint a real snapshot ever reports would serialize to a document
    /// that fails its own published schema. Proven with the actual serializer
    /// (<see cref="StatusJson.Serialize(ProjectSnapshot)"/>), not a hand-built JSON string, so a
    /// future rename of the enum member or a converter change is caught here too.</summary>
    [Fact]
    [Trait("Category", "Contracts")]
    public void ASnapshotWithAPausedSprintSatisfiesTheProjectSnapshotContract()
    {
        ProjectSnapshot snapshot = new(
            SchemaVersion: "1.2.0",
            StateVersion: 5,
            GeneratedAt: DateTimeOffset.Parse("2026-08-23T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            Project: new ProjectDescriptor("C:/src/forge", true),
            Startup: StartupState.Ready,
            ActiveSprintId: Guid.Parse("44444444-4444-4444-8444-444444444444"),
            Sprints:
            [
                new SprintStatus(
                    Guid.Parse("44444444-4444-4444-8444-444444444444"),
                    1,
                    SprintState.Paused,
                    "implementation-critical",
                    "0123456789abcdef0123456789abcdef01234567")
            ],
            Attention: [],
            SuggestedActions: [],
            StartupChecks: [],
            Providers: []);

        string json = StatusJson.Serialize(snapshot);
        using JsonDocument instance = JsonDocument.Parse(json);

        EvaluationResults result = ContractSchemas.Load("project-snapshot").Evaluate(
            instance.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        Assert.True(result.IsValid, json);
    }

    /// <summary>Plan section 12.3: `project-snapshot.schema.json` 1.4.0 adds `provider`/`model` to
    /// `$defs.entity` so an attempt row can carry the routed decision the sticky header now renders
    /// (<see cref="AttemptSnapshot.Provider"/>/<c>.Model</c>). Proven with the actual serializer, not
    /// a hand-built JSON string, so a converter or naming-policy change is caught here too -- the
    /// same discipline <see cref="ASnapshotWithAPausedSprintSatisfiesTheProjectSnapshotContract"/>
    /// already applies to the sprint-state enum.</summary>
    [Fact]
    [Trait("Category", "Contracts")]
    public void ASnapshotWithAnAttemptsRoutedProviderAndModelSatisfiesTheProjectSnapshotContract()
    {
        ProjectSnapshot snapshot = new(
            SchemaVersion: "1.4.0",
            StateVersion: 6,
            GeneratedAt: DateTimeOffset.Parse("2026-08-24T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            Project: new ProjectDescriptor("C:/src/forge", true),
            Startup: StartupState.Ready,
            ActiveSprintId: Guid.Parse("44444444-4444-4444-8444-444444444444"),
            Sprints:
            [
                new SprintStatus(
                    Guid.Parse("44444444-4444-4444-8444-444444444444"),
                    1,
                    SprintState.Running,
                    "implementation-critical",
                    "0123456789abcdef0123456789abcdef01234567")
            ],
            Attention: [],
            SuggestedActions: [],
            StartupChecks: [],
            Providers: [],
            Detail: SnapshotDetail.Full,
            Details: new SprintDetails(
                Guid.Parse("44444444-4444-4444-8444-444444444444"),
                Nodes: [new EntityStatus("implementation", "running", Kind: "work")],
                Attempts:
                [
                    new EntityStatus(
                        "55555555-5555-4555-8555-555555555555", "running", OwnerId: "implementation",
                        Provider: "claude_code", Model: "claude-sonnet-4-5")
                ],
                Findings: [],
                Gates: [],
                Artifacts: [],
                Routing: new RoutingStatus(9, null)));

        string json = StatusJson.Serialize(snapshot);
        using JsonDocument instance = JsonDocument.Parse(json);

        EvaluationResults result = ContractSchemas.Load("project-snapshot").Evaluate(
            instance.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        Assert.True(result.IsValid, json);
    }

    /// <summary>Round 1 review of PR #64 found `docs/contracts/v1/configuration.json`'s own `keys`
    /// list can drift from `ConfigurationRegistry.CreateDefaultKeys()` (it had, for the new
    /// `notifications.enabled` key) with nothing catching it. Proves both directions: every
    /// registered key is documented, and every documented key is registered, with matching scope,
    /// session-override, sensitivity, inheritance, and default value.</summary>
    [Fact]
    [Trait("Category", "Contracts")]
    public void ConfigurationRegistryMatchesTheContractsKeyList()
    {
        string root = Forge.UnitTests.RepositoryRoot.Find();
        string path = Path.Combine(root, "docs", "contracts", "v1", "configuration.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement keysElement = document.RootElement.GetProperty("keys");

        ConfigurationRegistry registry = new();
        Dictionary<string, ConfigurationKey> registryKeys =
            registry.Keys.ToDictionary(key => key.Name, StringComparer.Ordinal);
        List<string> contractKeyNames = [];

        foreach (JsonElement contractKey in keysElement.EnumerateArray())
        {
            string name = contractKey.GetProperty("key").GetString()!;
            contractKeyNames.Add(name);
            Assert.True(
                registryKeys.TryGetValue(name, out ConfigurationKey? registryKey),
                $"'{name}' is documented in configuration.json but not registered in ConfigurationRegistry.");

            Assert.Equal(
                registryKey!.Scope == ConfigurationScope.User ? "user" : "project",
                contractKey.GetProperty("scope").GetString());
            Assert.Equal(
                registryKey.AllowsSessionOverride, contractKey.GetProperty("session_override").GetBoolean());
            Assert.Equal(registryKey.Sensitive, contractKey.GetProperty("sensitive").GetBoolean());
            JsonElement inheritsProperty = contractKey.GetProperty("inherits");
            Assert.Equal(
                registryKey.Inherits,
                inheritsProperty.ValueKind == JsonValueKind.Null ? null : inheritsProperty.GetString());

            bool contractHasDynamicDefault =
                contractKey.TryGetProperty("default_is_dynamic", out JsonElement dynamicFlag) &&
                dynamicFlag.GetBoolean();
            if (!contractHasDynamicDefault)
            {
                string expectedDefault = registryKey.DefaultValue.HasValue
                    ? registryKey.DefaultValue.Value.GetRawText()
                    : "null";
                Assert.Equal(expectedDefault, contractKey.GetProperty("default").GetRawText());
            }
        }

        Assert.Equal(
            registryKeys.Keys.OrderBy(name => name, StringComparer.Ordinal),
            contractKeyNames.OrderBy(name => name, StringComparer.Ordinal));
    }

    /// <summary>Stage 12's migration/versioned-contract audit found every embedded contract
    /// schema's own `schema_version` is a closed set (`const`/`enum`, never an open string), so
    /// `Draft202012SchemasMatchCompatibilityFixtures` already fails closed on an out-of-range
    /// version wherever a fixture case exercises one — but only `user-config` actually had such a
    /// case; the other 21 schemas were unverified. This proves every current schema file has at
    /// least one fixture case whose `schema_version` sits outside that schema's own allowed set
    /// AND whose every other field is otherwise schema-valid — round 1 review of the PR that added
    /// this test found two of the first-drafted cases (`project-snapshot`, `startup-check`) were
    /// invalid for an unrelated reason too (a missing required field; an out-of-enum check id), so
    /// checking only "this case is invalid" could not actually prove the version check is what
    /// rejected it. Proven by re-validating a copy of the same instance with only `schema_version`
    /// corrected to an allowed value and requiring THAT to pass. Since this walks the schema
    /// directory rather than a hardcoded name list, a schema added later with no matching case
    /// fails this test instead of silently going unverified.</summary>
    [Fact]
    [Trait("Category", "Contracts")]
    public void EveryContractSchemaRejectsAnUnsupportedSchemaVersion()
    {
        string root = Forge.UnitTests.RepositoryRoot.Find();
        string schemaRoot = Path.Combine(root, "docs", "contracts", "v1", "schemas");
        string fixturePath = Path.Combine(root, "tests", "Forge.Tests", "Contracts", "fixtures", "contract-cases.json");
        using JsonDocument fixtureDocument = JsonDocument.Parse(File.ReadAllText(fixturePath));
        JsonElement cases = fixtureDocument.RootElement.GetProperty("cases");
        IReadOnlyDictionary<string, JsonSchema> schemas = ContractSchemas.LoadAll();
        var options = new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true,
            AddAnnotationForUnknownKeywords = true
        };

        List<string> schemaNames = [.. Directory
            .GetFiles(schemaRoot, "*.schema.json")
            .Select(path => Path.GetFileName(path).Replace(".schema.json", string.Empty, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)];

        List<string> schemasMissingAVersionRejectionCase = [];
        foreach (string schemaName in schemaNames)
        {
            using JsonDocument schemaDocument = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(schemaRoot, $"{schemaName}.schema.json")));
            JsonElement versionProperty =
                schemaDocument.RootElement.GetProperty("properties").GetProperty("schema_version");
            List<string> allowedVersions = versionProperty.TryGetProperty("const", out JsonElement constValue)
                ? [constValue.GetString()!]
                : [.. versionProperty.GetProperty("enum").EnumerateArray().Select(value => value.GetString()!)];
            HashSet<string> allowedVersionSet = allowedVersions.ToHashSet(StringComparer.Ordinal);

            bool provesVersionIsTheSoleCause = false;
            foreach (JsonElement testCase in cases.EnumerateArray())
            {
                if (testCase.GetProperty("schema").GetString() != schemaName ||
                    testCase.GetProperty("valid").GetBoolean() ||
                    !testCase.GetProperty("instance").TryGetProperty("schema_version", out JsonElement instanceVersion) ||
                    instanceVersion.ValueKind != JsonValueKind.String ||
                    allowedVersionSet.Contains(instanceVersion.GetString()!))
                {
                    continue;
                }

                JsonNode correctedInstance = JsonNode.Parse(testCase.GetProperty("instance").GetRawText())!;
                correctedInstance["schema_version"] = allowedVersions[0];
                EvaluationResults correctedResult = schemas[schemaName].Evaluate(
                    JsonSerializer.SerializeToElement(correctedInstance), options);
                if (correctedResult.IsValid)
                {
                    provesVersionIsTheSoleCause = true;
                    break;
                }
            }

            if (!provesVersionIsTheSoleCause)
            {
                schemasMissingAVersionRejectionCase.Add(schemaName);
            }
        }

        Assert.Empty(schemasMissingAVersionRejectionCase);
    }

    [Fact]
    [Trait("Category", "Contracts")]
    public void Draft202012SchemasMatchCompatibilityFixtures()
    {
        string root = Forge.UnitTests.RepositoryRoot.Find();
        string fixturePath = Path.Combine(root, "tests", "Forge.Tests", "Contracts", "fixtures", "contract-cases.json");
        var buildOptions = new BuildOptions
        {
            Dialect = Dialect.Draft202012,
            SchemaRegistry = new SchemaRegistry()
        };
        var schemas = ContractSchemas.LoadAll();
        using var fixtureDocument = JsonDocument.Parse(File.ReadAllText(fixturePath));
        var failures = new List<string>();
        var options = new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true,
            AddAnnotationForUnknownKeywords = true
        };

        Assert.ThrowsAny<Exception>(() =>
        {
            using var invalidSchema = JsonDocument.Parse(
                """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":7}""");
            _ = JsonSchema.Build(invalidSchema.RootElement, buildOptions);
        });

        foreach (var testCase in fixtureDocument.RootElement.GetProperty("cases").EnumerateArray())
        {
            string name = testCase.GetProperty("name").GetString()!;
            string schemaName = testCase.GetProperty("schema").GetString()!;
            bool expected = testCase.GetProperty("valid").GetBoolean();
            if (!schemas.TryGetValue(schemaName, out JsonSchema? schema) || schema is null)
            {
                failures.Add($"{name}: unknown schema '{schemaName}'.");
                continue;
            }

            var result = schema.Evaluate(testCase.GetProperty("instance"), options);
            if (result.IsValid != expected)
            {
                failures.Add($"{name}: expected valid={expected}, actual valid={result.IsValid}. Result: {JsonSerializer.Serialize(result)}");
            }
        }

        Assert.Empty(failures);
    }
}
