using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Forge.Configuration;

public sealed class YamlConfigurationStore(
    string path,
    ConfigurationScope scope,
    IConfigurationRegistry registry) : IConfigurationStore
{
    // YamlDotNet's untyped Deserialize<object> stringifies every scalar -- the same verified
    // behavior ForgeDocumentCompiler's own typedDeserializer comment documents -- which would
    // silently turn `token_budget: 40000` into the JSON string "40000" and fail
    // project-manifest.schema.json's `"type": "integer"` (ADR 0029). This store cannot deserialize
    // directly into a typed DTO the way ForgeDocumentCompiler does, since NormalizeYaml/
    // StripLegacySprintRegistry must inspect the raw shape before any typed schema applies.
    //
    // Round 2 review of PR #69: the obvious fix (YamlDotNet's own
    // WithAttemptingUnquotedStringTypeDeserialization() builder option, making the untyped pass
    // itself scalar-type-aware) was tried first and reverted -- it is too broad, reproducibly
    // introducing two new hazards for every OTHER string-typed scalar this store already handles
    // correctly: `true`/`false` string values silently stop round-tripping (YamlDotNet coerces
    // them to bool, then schema validation's "type": "string" check fails, and the SAME
    // .previous-recovery path this PR's own bug report is about silently discards the write again
    // -- reintroducing that exact defect for a different field); and YAML float specials
    // (`.inf`/`.nan`) get parsed as `double.PositiveInfinity`/`NaN`, which
    // JsonSerializer.SerializeToElement then throws ArgumentException on -- unguarded by any catch
    // filter in this codebase, a strictly worse crash than the one this whole PR exists to fix.
    // CoerceTokenBudgetToNumber below is the narrow alternative: it touches only the one known
    // integer field this PR introduces, leaving every string field's round-trip exactly as it was
    // before this PR.
    //
    // Round 3 review of PR #69: "no broad type-inference risk at all" above overclaimed what
    // reverting the builder option actually achieves. YamlDotNet's plain, option-less
    // Deserialize<object> still honors an EXPLICIT YAML type tag (e.g. `workflow: !!float .inf`) --
    // only untagged/plain scalars are stringified by default. Reproduced directly: an explicitly
    // float-tagged `.inf`/`.nan`/`-.inf` anywhere in a hand-edited manifest still parses as
    // `double.PositiveInfinity`/`NaN`/`NegativeInfinity`, which the default
    // JsonSerializer.SerializeToElement(object) below still throws ArgumentException on -- the
    // same crash round 2 believed it had eliminated, pre-existing on `main` (this PR did not
    // introduce it, but did ship a test overclaiming it was closed). Fixed at the actual point of
    // failure instead of by another type-inference change: SerializeToElement is called with
    // JsonNumberHandling.AllowNamedFloatingPointLiterals, which writes those three values as the
    // JSON strings "Infinity"/"NaN"/"-Infinity" instead of throwing. Every field's own schema type/
    // pattern/const constraint then rejects that string exactly like any other garbled scalar
    // value, through the same InvalidDataException path a plain corrupt value already used.
    private static readonly JsonSerializerOptions RawSerializerOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly IDeserializer rawDeserializer = new DeserializerBuilder().Build();
    private readonly ISerializer serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public ConfigurationScope Scope { get; } = scope;

    public async Task<ConfigurationDocument> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return ConfigurationDocument.Empty;
        }

        try
        {
            return await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (IsRecoverable(error) && File.Exists($"{path}.previous"))
        {
            ConfigurationDocument recovered =
                await ReadFileAsync($"{path}.previous", cancellationToken).ConfigureAwait(false);
            byte[] contents = await File.ReadAllBytesAsync(
                $"{path}.previous",
                cancellationToken).ConfigureAwait(false);
            await AtomicConfigurationFile.WriteAsync(
                path,
                contents,
                cancellationToken,
                false).ConfigureAwait(false);
            return recovered;
        }
    }

    public async Task WriteAsync(
        ConfigurationDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateScope(document);
        ConfigurationSchemaCodec.ProjectConfiguration persisted =
            ConfigurationSchemaCodec.ToProject(document);
        string yaml = serializer.Serialize(persisted);
        await AtomicConfigurationFile.WriteAsync(
            path,
            Encoding.UTF8.GetBytes(yaml),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ConfigurationDocument> ReadFileAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        string yaml = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        object? raw = rawDeserializer.Deserialize<object>(yaml);
        object? normalized = NormalizeYaml(raw);
        StripLegacySprintRegistry(normalized);
        CoerceTokenBudgetToNumber(normalized);
        JsonElement rawElement = JsonSerializer.SerializeToElement(normalized, RawSerializerOptions);
        ConfigurationSchemaCodec.ValidateProject(rawElement);
        ConfigurationSchemaCodec.ProjectConfiguration persisted =
            rawElement.Deserialize<ConfigurationSchemaCodec.ProjectConfiguration>(
                ConfigurationSchemaCodec.SerializerOptions) ??
            throw new InvalidDataException("Project configuration is empty.");
        ConfigurationDocument result = ConfigurationSchemaCodec.FromProject(persisted);
        ValidateScope(result);
        return result;
    }

    private void ValidateScope(ConfigurationDocument document)
    {
        foreach (string key in document.Values.Keys)
        {
            registry.RequireScope(key, Scope);
        }
    }

    // JsonException (round 1 review of PR #69): the same out-of-int32-range-integer hazard
    // ProjectRootResolver.ReadManifestAsync's own catch filter documents -- widened here too since
    // this is the second, independent place a caller can observe ReadFileAsync's typed
    // deserialization throw.
    private static bool IsRecoverable(Exception error) =>
        error is YamlException or InvalidDataException or ConfigurationScopeException or
            FormatException or JsonException;

    /// <summary>Migrates persisted pre-v0.11 data before validating the current pre-1.0 contract.
    /// The removed registry is not exposed by the schema or application API.</summary>
    private static void StripLegacySprintRegistry(object? normalized)
    {
        if (normalized is not Dictionary<string, object?> root ||
            !root.Remove("sprints", out object? legacy))
        {
            return;
        }

        if (legacy is not object?[] values || values.Any(item => item is not string text || !Guid.TryParse(text, out _)) ||
            values.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
        {
            throw new InvalidDataException("The legacy project sprint registry is invalid.");
        }
    }

    // The narrow alternative to YamlDotNet's broad type-inference option (see rawDeserializer's own
    // comment for why that option was reverted): touches only `context.token_budget`, the one
    // integer field this store's schema declares, leaving every other scalar's round-trip
    // (including `true`/`false`-valued strings) exactly as NormalizeYaml already produces it. A
    // string that does not parse as a `long` is left untouched -- ConfigurationSchemaCodec.
    // ValidateProject's own `"type": "integer"` check rejects it with the same InvalidDataException
    // a garbled value already produced before this coercion existed. `long`, not `int`, deliberately:
    // an out-of-Int32-range value (e.g. "3000000000") must still reach the JSON Schema `"maximum"`
    // check as a genuine number so it fails there with a clean, expected InvalidDataException,
    // rather than staying a string and failing the unrelated `"type"` check instead.
    // Round 3 review of PR #69: a bare long.TryParse(string, out long) uses NumberStyles.Integer
    // (permits leading/trailing whitespace and a leading sign) under CultureInfo.CurrentCulture
    // (whose NegativeSign/positive-sign glyphs vary by locale), so the same hand-edited manifest
    // could parse a token_budget differently -- or not at all -- depending on the machine's culture
    // settings. Pinned to InvariantCulture with only AllowLeadingSign, so parsing is deterministic
    // across machines and a value padded with whitespace is left as a string for the ordinary
    // schema "type": "integer" rejection instead of being silently trimmed and accepted.
    private static void CoerceTokenBudgetToNumber(object? normalized)
    {
        if (normalized is not Dictionary<string, object?> root ||
            !root.TryGetValue("context", out object? contextValue) ||
            contextValue is not Dictionary<string, object?> context ||
            !context.TryGetValue("token_budget", out object? tokenBudgetValue) ||
            tokenBudgetValue is not string text ||
            !long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long parsed))
        {
            return;
        }

        context["token_budget"] = parsed;
    }

    private static object? NormalizeYaml(object? value) =>
        value switch
        {
            IDictionary dictionary => NormalizeDictionary(dictionary),
            IEnumerable collection when value is not string =>
                collection.Cast<object?>().Select(NormalizeYaml).ToArray(),
            _ => value,
        };

    private static Dictionary<string, object?> NormalizeDictionary(IDictionary dictionary)
    {
        Dictionary<string, object?> result = new(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in dictionary)
        {
            string key = entry.Key as string ??
                throw new InvalidDataException("YAML configuration keys must be strings.");
            result.Add(key, NormalizeYaml(entry.Value));
        }

        return result;
    }
}
