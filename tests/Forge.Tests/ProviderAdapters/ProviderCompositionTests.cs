using System.Reflection;
using System.Text.RegularExpressions;
using Forge.Application;
using Forge.Bootstrap;
using Forge.Providers;
using Forge.Providers.Claude;
using Forge.Providers.Codex;
using Forge.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.ProviderAdapterTests;

/// <summary>
/// Every user-facing composition root (CLI, Desktop, Host) must call both
/// <c>AddCodexProvider()</c> and <c>AddClaudeProvider()</c> — a composition root that forgets one
/// silently ships with an empty <see cref="ProviderCatalog"/>, which makes every
/// <c>providers.enabled</c> write fail with "unknown provider id" for a real, shipped provider.
/// </summary>
public sealed class ProviderCompositionTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ACompositionRootThatCallsBothProviderRegistrationsPopulatesTheCatalog()
    {
        using TestEnvironment environment = new();
        ServiceCollection services = new();
        services.AddForgeCore();
        services.AddSingleton<IEnvironmentPaths>(environment);
        services.AddCodexProvider();
        services.AddClaudeProvider();
        using ServiceProvider provider = services.BuildServiceProvider();

        ProviderCatalog catalog = provider.GetRequiredService<ProviderCatalog>();

        Assert.True(catalog.Contains(new ProviderId("codex")));
        Assert.True(catalog.Contains(new ProviderId("claude_code")));
    }

    /// <summary>Slice S5: the real <c>Forge.Providers.Codex.Windows</c>/
    /// <c>Forge.Providers.Claude.Windows</c> adapters, composed exactly as a shipping composition
    /// root builds them, project as <see cref="ProviderQuotaAvailability.Unknown"/> with no amount,
    /// unit, or reset time -- the adapters' own <c>DefaultModel</c> (the single adapter value
    /// <see cref="ProviderQuotaProjector"/> reads at all) changes nothing about the reading.
    /// <para>
    /// This pins the composed behavior only. It deliberately does NOT claim to prove ADR 0068's
    /// stronger terminality claim, because no behavioral test could: the projector asks no adapter
    /// about quota, so these assertions would stay green even if an adapter grew a real quota API
    /// (PR #125 review finding 1). The two structural checks below carry that claim instead.
    /// </para></summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void QuotaProjectsAsUnknownForBothRealProviderAdapters()
    {
        using TestEnvironment environment = new();
        ServiceCollection services = new();
        services.AddForgeCore();
        services.AddSingleton<IEnvironmentPaths>(environment);
        services.AddCodexProvider();
        services.AddClaudeProvider();
        using ServiceProvider provider = services.BuildServiceProvider();
        ProviderCatalog catalog = provider.GetRequiredService<ProviderCatalog>();
        ProviderToolchainStatus status = new(
        [
            ProviderStatus.Ready(new ProviderId("codex"), "0.149.1"),
            ProviderStatus.Ready(new ProviderId("claude_code"), "2.1.233"),
        ]);

        IReadOnlyList<ProviderQuotaSnapshot> entries =
            ProviderQuotaProjector.Project(status, catalog, DateTimeOffset.UnixEpoch);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry =>
        {
            Assert.Equal(ProviderQuotaAvailability.Unknown, entry.Availability);
            Assert.Null(entry.RemainingAmount);
            Assert.Null(entry.Unit);
            Assert.Null(entry.ResetAt);
            Assert.Equal(ProviderDiagnosticCodes.QuotaUnknown, entry.DiagnosticCode);
        });
    }

    /// <summary>ADR 0068's terminality claim, half one: there is no quota signal for
    /// <see cref="ProviderQuotaProjector"/> to have skipped reading. Every provider a shipping
    /// composition root actually registers is inspected -- inherited and non-public members
    /// included -- for anything quota-shaped by name or by quota contract type, as is
    /// <see cref="ILlmProvider"/> itself, the port through which neutral code could ever reach one.
    /// An adapter that grows a real quota API fails here, which is exactly the change that makes
    /// <see cref="ProviderQuotaAvailability.Unknown"/>'s "terminal" wording (and ADR 0068) stop being
    /// true and require revisiting.</summary>
    [Fact]
    [Trait("Category", "Architecture")]
    public void NeitherRealProviderAdapterExposesAQuotaSignalTheProjectorCouldRead()
    {
        using TestEnvironment environment = new();
        ServiceCollection services = new();
        services.AddForgeCore();
        services.AddSingleton<IEnvironmentPaths>(environment);
        services.AddCodexProvider();
        services.AddClaudeProvider();
        using ServiceProvider provider = services.BuildServiceProvider();
        ProviderCatalog catalog = provider.GetRequiredService<ProviderCatalog>();
        Assert.Equal(2, catalog.Providers.Count);
        Type[] inspected =
        [
            typeof(ILlmProvider),
            .. catalog.Providers.Select(registered => registered.GetType()).Distinct(),
        ];

        string[] quotaShaped = inspected
            .SelectMany(type => type
                .GetMembers(
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static)
                .Where(IsQuotaShaped)
                .Select(member => $"{type.Name}.{member.Name}"))
            .ToArray();

        Assert.True(
            quotaShaped.Length == 0,
            "A provider adapter now exposes a quota signal the projector does not read, so " +
            $"ProviderQuotaAvailability.Unknown is no longer terminal: {string.Join(", ", quotaShaped)}.");
    }

    /// <summary>ADR 0068's terminality claim, half two: there is nothing else that could produce a
    /// reading. <see cref="ProviderQuotaProjector"/>'s private <c>Unverified</c> factory is the only
    /// place production code builds a <see cref="ProviderQuotaSnapshot"/>, and it hardcodes
    /// <see cref="ProviderQuotaAvailability.Unknown"/> -- so with half one holding, no production code
    /// path can reach any other availability, amount, unit, or reset time.
    /// <para>
    /// Construction sites are found by scanning production source (comments stripped), the same way
    /// <c>ArchitectureTests.EveryLoggerEventIdNamesExactlyOneEvent</c> reads declarations it cannot
    /// reflect over. The scan recognizes an explicit <c>new ProviderQuotaSnapshot(...)</c> and a
    /// target-typed <c>new(...)</c> assigned or expression-bodied against a declaration naming the
    /// type -- the two forms this codebase writes. It is a text scan, not a compiler: it is the
    /// tripwire for a second producer appearing, not a proof that no exotic one could be written.
    /// </para></summary>
    [Fact]
    [Trait("Category", "Architecture")]
    public void TheProjectorsUnverifiedFactoryIsTheOnlyProductionProducerOfAQuotaSnapshot()
    {
        string sourceRoot = Path.Combine(Forge.UnitTests.RepositoryRoot.Find(), "src");
        (string File, int Count)[] constructionSites = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"))
            .Select(path => (File: path, Count: QuotaSnapshotConstruction.Count(ExecutableCode(path))))
            .Where(candidate => candidate.Count > 0)
            .ToArray();

        string[] expected = [Path.Combine(sourceRoot, "Forge.Runtime", "Providers", "ProviderQuota.cs")];
        Assert.Equal(expected, constructionSites.Select(site => site.File).ToArray());
        Assert.Equal(1, constructionSites[0].Count);

        Match factory = UnverifiedFactory.Match(ExecutableCode(constructionSites[0].File));
        Assert.True(factory.Success, "ProviderQuotaProjector no longer declares the Unverified factory.");
        string arguments = factory.Groups["arguments"].Value;
        Assert.Contains(
            $"{nameof(ProviderQuotaAvailability)}.{nameof(ProviderQuotaAvailability.Unknown)}",
            arguments,
            StringComparison.Ordinal);
        Assert.Contains(nameof(ProviderDiagnosticCodes.QuotaUnknown), arguments, StringComparison.Ordinal);
        string[] verifiedStates = Enum.GetNames<ProviderQuotaAvailability>()
            .Where(state => state != nameof(ProviderQuotaAvailability.Unknown))
            .Where(state => arguments.Contains(
                $"{nameof(ProviderQuotaAvailability)}.{state}", StringComparison.Ordinal))
            .ToArray();
        Assert.True(
            verifiedStates.Length == 0,
            $"The Unverified factory can now produce a non-terminal reading: {string.Join(", ", verifiedStates)}.");
    }

    private static bool IsQuotaShaped(MemberInfo member) =>
        member.Name.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
        SignatureTypes(member).SelectMany(Unwrap).Any(QuotaContracts.Contains);

    private static IEnumerable<Type> SignatureTypes(MemberInfo member) => member switch
    {
        MethodInfo method =>
            [method.ReturnType, .. method.GetParameters().Select(parameter => parameter.ParameterType)],
        ConstructorInfo constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType),
        PropertyInfo property => [property.PropertyType],
        FieldInfo field => [field.FieldType],
        _ => [],
    };

    // Task<T>/IReadOnlyList<T>/T[] all hide the contract type one level down, and a quota-reporting
    // member would almost certainly be asynchronous.
    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type;
        List<Type> nested = [.. type.GetGenericArguments()];
        if (type.GetElementType() is Type element)
        {
            nested.Add(element);
        }

        foreach (Type inner in nested.SelectMany(Unwrap))
        {
            yield return inner;
        }
    }

    private static string ExecutableCode(string path) => string.Join(
        '\n',
        File.ReadLines(path).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static readonly Type[] QuotaContracts =
        [typeof(ProviderQuotaSnapshot), typeof(ProviderQuotaAvailability), typeof(ProviderQuotaStatus)];

    // `new ProviderQuotaSnapshot(...)`, or a target-typed `new(...)` bound to a declaration that
    // names the type (`... Unverified(...) => new(...)`, `ProviderQuotaSnapshot x = new(...)`).
    // Statement-bounded by `;{}` so one declaration can never reach a `new(` belonging to another.
    private static readonly Regex QuotaSnapshotConstruction = new(
        @"new\s+ProviderQuotaSnapshot\s*\(|\bProviderQuotaSnapshot\b[^;{}]*?=>?\s*new\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex UnverifiedFactory = new(
        @"ProviderQuotaSnapshot\s+Unverified\s*\([^)]*\)\s*=>\s*new\s*\((?<arguments>[^;]*)\)\s*;",
        RegexOptions.Compiled);
}
