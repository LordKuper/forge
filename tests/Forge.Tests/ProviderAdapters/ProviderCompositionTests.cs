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
    /// <see cref="ProviderQuotaProjector"/> to have skipped reading. Every provider adapter this test
    /// registers is inspected -- inherited and non-public members included -- for anything
    /// quota-shaped by name or by quota contract type, as is <see cref="ILlmProvider"/> itself, the
    /// port through which neutral code could ever reach one.
    /// <para>
    /// The registration list below is not trusted to stay in step with the shipping composition roots
    /// by memory: <see cref="ProviderRegistrationsInProductionSource"/> reads every
    /// <c>.Add*Provider(</c> invocation in <c>src/</c> and the first assertion fails if that set ever
    /// differs from what this test registers. A third adapter therefore cannot be composed into a
    /// shipping root without failing here until it is inspected too (PR #125 review round 2
    /// finding 1).
    /// </para>
    /// An adapter that grows a real quota API fails here, which is exactly the change that makes
    /// <see cref="ProviderQuotaAvailability.Unknown"/>'s "terminal" wording (and ADR 0068) stop being
    /// true and require revisiting.</summary>
    [Fact]
    [Trait("Category", "Architecture")]
    public void NeitherRealProviderAdapterExposesAQuotaSignalTheProjectorCouldRead()
    {
        string[] registeredHere = ["AddClaudeProvider", "AddCodexProvider"];
        string[] registeredByProductionSource = ProviderRegistrationsInProductionSource();
        Assert.True(
            registeredByProductionSource.SequenceEqual(registeredHere, StringComparer.Ordinal),
            "Production source now registers a different set of provider adapters than this test " +
            "does, so the adapters inspected below are no longer the ones Forge ships. Register the " +
            $"new adapter here too. Production source calls [{string.Join(", ", registeredByProductionSource)}]; " +
            $"this test calls [{string.Join(", ", registeredHere)}].");

        using TestEnvironment environment = new();
        ServiceCollection services = new();
        services.AddForgeCore();
        services.AddSingleton<IEnvironmentPaths>(environment);
        services.AddCodexProvider();
        services.AddClaudeProvider();
        using ServiceProvider provider = services.BuildServiceProvider();
        ProviderCatalog catalog = provider.GetRequiredService<ProviderCatalog>();
        Assert.Equal(registeredHere.Length, catalog.Providers.Count);
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
    /// reflect over. <see cref="TheQuotaSnapshotConstructionScanRecognizesEveryFormItClaimsTo"/> pins
    /// the exact form list this scan covers, and the exact blind spots it does not: a <c>var</c>
    /// local, a declaration assigned in a later statement, and a <c>return</c> behind a nested block.
    /// It is a text scan, not a compiler -- the tripwire for a second producer appearing, not a proof
    /// that no exotic one could be written,
    /// and ADR 0068 states the guarantee with that scope (PR #125 review round 2 finding 2).
    /// </para></summary>
    [Fact]
    [Trait("Category", "Architecture")]
    public void TheProjectorsUnverifiedFactoryIsTheOnlyProductionProducerOfAQuotaSnapshot()
    {
        string sourceRoot = Path.Combine(Forge.UnitTests.RepositoryRoot.Find(), "src");
        (string File, int Count)[] constructionSites = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(IsProductionSource)
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

    /// <summary>Keeps the previous test's stated scope provable rather than aspirational: each snippet
    /// below is a construction form its remarks (and ADR 0068) claim the scan recognizes, and each
    /// must be matched. The first revision missed <c>with</c> expressions -- the idiomatic way to
    /// derive a new value from a <c>sealed record</c>, and so the most likely shape of a future
    /// non-terminal reading -- and brace-bodied <c>return new(...)</c>, while the ADR claimed the
    /// guarantee unconditionally (PR #125 review round 2 finding 2). The non-construction snippets pin
    /// the other direction: merely consuming the type is not a construction site, or the scan would
    /// fail on every reader in <c>src/</c>.</summary>
    [Fact]
    [Trait("Category", "Architecture")]
    public void TheQuotaSnapshotConstructionScanRecognizesEveryFormItClaimsTo()
    {
        string[] construction =
        [
            "var x = new ProviderQuotaSnapshot(id, model, availability);",
            "ProviderQuotaSnapshot x = new(id, model, availability);",
            "private static ProviderQuotaSnapshot Make(ProviderId id) => new(id.Value, null);",
            "ProviderQuotaSnapshot degraded = entry with { Availability = ProviderQuotaAvailability.Limited };",
            "private static ProviderQuotaSnapshot Degrade(ProviderQuotaSnapshot e) => e with { Unit = \"tokens\" };",
            "private static ProviderQuotaSnapshot Make(ProviderId id)\n{\n    return new(id.Value, null);\n}",
            "private static ProviderQuotaSnapshot Degrade(ProviderQuotaSnapshot e)\n{\n    return e with { Unit = \"tokens\" };\n}",
        ];
        string[] consumptionOnly =
        [
            "public static string Row(ProviderQuotaSnapshot entry)\n{\n    return string.Create(entry.ProviderId);\n}",
            "IReadOnlyList<ProviderQuotaSnapshot> entries = Project(status, catalog, observedAt);",
            // The real shape in SidebarViewModel.BuildStatusRow: quota comes in, something else is
            // constructed on the way out. Anchoring the brace-bodied form on the return-type position
            // is what keeps this out.
            "private static SidebarStatusRow Build(IReadOnlyList<ProviderQuotaSnapshot> quota)\n{\n    return new(quota.Count);\n}",
        ];

        // The blind spots the remarks above disclose. Asserted so the disclosure stays accurate: if a
        // later revision starts catching one of these, this test fails and the wording is updated with it.
        string[] outOfScope =
        [
            "var derived = entry with { Unit = \"tokens\" };",
            "ProviderQuotaSnapshot x;\nx = new(id.Value, null);",
            "private static ProviderQuotaSnapshot Make(ProviderId id)\n{\n    if (id.Value.Length > 0)\n    {\n        Log();\n    }\n\n    return new(id.Value, null);\n}",
        ];

        string[] missed = [.. construction.Where(snippet => !QuotaSnapshotConstruction.IsMatch(snippet))];
        Assert.True(missed.Length == 0, $"The scan no longer recognizes: {string.Join(" | ", missed)}.");
        string[] falsePositives =
            [.. consumptionOnly.Where(snippet => QuotaSnapshotConstruction.IsMatch(snippet))];
        Assert.True(
            falsePositives.Length == 0,
            $"The scan now reads consumption as construction: {string.Join(" | ", falsePositives)}.");
        string[] newlyCovered =
            [.. outOfScope.Where(snippet => QuotaSnapshotConstruction.IsMatch(snippet))];
        Assert.True(
            newlyCovered.Length == 0,
            "The scan now covers a form documented as out of reach; widen the remarks above and ADR " +
            $"0068 to match: {string.Join(" | ", newlyCovered)}.");
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

    private static bool IsProductionSource(string path) => !path
        .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(segment => segment is "bin" or "obj");

    /// <summary>Every provider-adapter registration production source invokes, sorted and distinct --
    /// today the shipping composition roots' calls in <c>src/Forge.Cli.Windows/Program.cs</c>,
    /// <c>src/Forge.Desktop/MauiProgram.cs</c> and <c>src/Forge.Host.Windows/Program.cs</c>. Scanning
    /// all of <c>src/</c> rather than a fixed root list means a brand-new composition root is covered
    /// too. The leading `.` excludes the extension methods' own declarations, which are not
    /// invocations.</summary>
    private static string[] ProviderRegistrationsInProductionSource() =>
    [
        .. Directory
            .EnumerateFiles(
                Path.Combine(Forge.UnitTests.RepositoryRoot.Find(), "src"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(IsProductionSource)
            .SelectMany(path => ProviderRegistration.Matches(ExecutableCode(path)))
            .Select(match => match.Groups["method"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];

    private static readonly Type[] QuotaContracts =
        [typeof(ProviderQuotaSnapshot), typeof(ProviderQuotaAvailability), typeof(ProviderQuotaStatus)];

    private static readonly Regex ProviderRegistration = new(
        @"\.(?<method>Add[A-Za-z0-9_]*Provider)\s*\(",
        RegexOptions.Compiled);

    // A value of the type produced without naming it at the site: a target-typed `new(...)`, or a
    // `with` expression over an existing one (the idiomatic way to derive from a `sealed record`).
    private const string TargetTypedProducer = @"(?:new\s*\(|[\w.]+\s+with\s*\{)";

    // `new ProviderQuotaSnapshot(...)`, or a `TargetTypedProducer` bound to a declaration that names
    // the type -- expression-bodied or assigned (`... Unverified(...) => new(...)`,
    // `ProviderQuotaSnapshot x = e with { ... }`), or `return`ed from the brace body of a member
    // whose return type is the type. The expression-bodied form is statement-bounded by `;{}` so one
    // declaration can never reach a producer belonging to another; the brace-bodied form anchors on
    // the return-type position (so a mere `ProviderQuotaSnapshot` parameter on a member returning
    // something else is not a construction site) and stops at the first `}`, so it does not leak
    // into the next member either.
    private static readonly Regex QuotaSnapshotConstruction = new(
        @"new\s+ProviderQuotaSnapshot\s*\(" +
        @"|\bProviderQuotaSnapshot\b[^;{}]*?=>?\s*" + TargetTypedProducer +
        @"|\bProviderQuotaSnapshot\b\??\s+\w+\s*\([^()]*\)\s*\{[^{}]*?\breturn\s+" + TargetTypedProducer,
        RegexOptions.Compiled);

    private static readonly Regex UnverifiedFactory = new(
        @"ProviderQuotaSnapshot\s+Unverified\s*\([^)]*\)\s*=>\s*new\s*\((?<arguments>[^;]*)\)\s*;",
        RegexOptions.Compiled);
}
