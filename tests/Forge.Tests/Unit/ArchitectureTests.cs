using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Forge.Configuration;

namespace Forge.UnitTests;

/// <summary>Mechanically enforces the ADR 0007 cross-platform/OS-adapter boundary.</summary>
public sealed class ArchitectureTests
{
    [Fact]
    [Trait("Category", "Architecture")]
    public void NeutralProjectsDoNotReferenceAnOsAdapter()
    {
        foreach (SourceProject project in SourceProjects())
        {
            if (project.IsOsAdapter)
            {
                continue;
            }

            IEnumerable<string> adapterReferences = project.References
                .Where(reference => IsOsAdapter(ResolveReference(project.Path, reference)));
            Assert.True(
                !adapterReferences.Any(),
                $"{project.Name} is neutral but references the OS adapter(s): {string.Join(", ", adapterReferences)}.");
        }
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void NeutralProjectsDoNotTargetAnOsSpecificFramework()
    {
        foreach (SourceProject project in SourceProjects())
        {
            if (project.IsOsAdapter)
            {
                continue;
            }

            Assert.True(
                !project.TargetFrameworks.Any(ContainsOsMoniker),
                $"{project.Name} is neutral but targets an OS-specific framework: {string.Join(';', project.TargetFrameworks)}.");
        }
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void LeafOsAdaptersOnlyReferenceNeutralProjects()
    {
        foreach (SourceProject project in SourceProjects())
        {
            if (!project.IsOsAdapter || project.IsCompositionRoot)
            {
                // A composition root (a native executable/UI bootstrap) selects and wires leaf adapters together;
                // every other adapter implements one port and may depend inward on neutral contracts only.
                continue;
            }

            IEnumerable<string> adapterReferences = project.References
                .Where(reference => IsOsAdapter(ResolveReference(project.Path, reference)));
            Assert.True(
                !adapterReferences.Any(),
                $"{project.Name} is a leaf OS adapter but references another adapter: {string.Join(", ", adapterReferences)}.");
        }
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void LeafOsAdaptersAreNamedForTheirOperatingSystem()
    {
        foreach (SourceProject project in SourceProjects())
        {
            if (!project.IsOsAdapter || project.IsCompositionRoot)
            {
                // A composition root's name is the product's native executable/UI host, not a technical adapter
                // label (e.g. "Forge.Desktop"), so ADR 0007's naming rule applies only to the leaf adapters it wires.
                continue;
            }

            Assert.True(
                RecognizedOsAdapterMonikers.Any(
                    moniker => project.Name.Contains(moniker, StringComparison.Ordinal)),
                $"{project.Name} is marked ForgeOsAdapter but its name does not identify an operating system.");
        }
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void HostsDoNotContainHardCodedLabelText()
    {
        string root = RepositoryRoot.Find();
        string[] xamlFiles =
            Directory.GetFiles(Path.Combine(root, "src", "Forge.Desktop"), "*.xaml", SearchOption.AllDirectories);

        Assert.DoesNotContain(
            xamlFiles.SelectMany(File.ReadLines),
            line => line.Contains("Text=\"", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void ProjectLeaseAndProviderInstallLockConstructTheirMutexIdentically()
    {
        // MutexProjectLease and ProviderInstallLock are two independent copies of the same uniform
        // session-scoped construction (see ProjectLease.cs's remarks for why not Global\). Nothing
        // else keeps them from silently diverging.
        string sourceRoot = Path.Combine(RepositoryRoot.Find(), "src");
        string projectLeaseCode = ExtractSharedMutexConstructionCode(
            Path.Combine(sourceRoot, "Forge.Host.Client", "ProjectLease.cs"));
        string providerInstallLockCode = ExtractSharedMutexConstructionCode(
            Path.Combine(sourceRoot, "Forge.Runtime", "Providers", "ProviderInstallLock.cs"));

        Assert.Equal(projectLeaseCode, providerInstallLockCode);
    }

    // Captures CreateMutex's body (from its own declaration through its `out _);`), and normalizes
    // away the one intentional difference (the parameter name — leaseName vs lockName) and all
    // whitespace, so the comparison catches any other divergence in the construction logic itself
    // (options, ordering) without being brittle about formatting.
    private static string ExtractSharedMutexConstructionCode(string path)
    {
        string source = File.ReadAllText(path);
        int start = source.IndexOf("private static Mutex CreateMutex(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{path} does not declare a CreateMutex method.");
        // The first "out _);" at or after CreateMutex's own start — not the last one in the whole
        // file — so a future Mutex construction added elsewhere in the file can never extend this
        // boundary past CreateMutex's actual end.
        int end = source.IndexOf("out _);", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"{path}'s CreateMutex does not end with the expected construction.");
        end += "out _);".Length;

        // Doc comments intentionally differ (one cross-references the other type by name), so only
        // the actual code is compared — everything a `///` line contributes is documentation, never
        // construction logic.
        string code = string.Join(
            '\n',
            source[start..end]
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith("///", StringComparison.Ordinal)));
        return string.Join(
            ' ',
            code.Replace("leaseName", "name", StringComparison.Ordinal)
                .Replace("lockName", "name", StringComparison.Ordinal)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void HostsDoNotBypassConfigurationStores()
    {
        string sourceRoot = Path.Combine(RepositoryRoot.Find(), "src");
        string[] boundaryProjects =
        [
            "Forge.Cli",
            "Forge.Cli.Windows",
            "Forge.Desktop",
            "Forge.Desktop.Presentation",
        ];
        string[] bypasses = boundaryProjects
            .SelectMany(project => Directory.GetFiles(
                Path.Combine(sourceRoot, project),
                "*.cs",
                SearchOption.AllDirectories))
            .Where(path => File.ReadAllText(path).Contains(
                "File.WriteAllText",
                StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(bypasses);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void EveryUserFacingWindowsCompositionRootReferencesBothBuiltInProviders()
    {
        // ADR 0008: registration ("AddCodexProvider()"/"AddClaudeProvider()") is how a
        // composition root's ProviderCatalog learns which providers exist. A composition root
        // that forgets one reference ships with a catalog that silently rejects every
        // `providers.enabled` write for a provider Forge actually ships.
        string[] userFacingCompositionRoots = ["Forge.Cli.Windows", "Forge.Desktop", "Forge.Host.Windows"];
        foreach (SourceProject project in SourceProjects().Where(
            project => userFacingCompositionRoots.Contains(project.Name)))
        {
            Assert.True(
                project.References.Any(
                    reference => reference.Contains("Forge.Providers.Codex.Windows", StringComparison.Ordinal)),
                $"{project.Name} must reference Forge.Providers.Codex.Windows.");
            Assert.True(
                project.References.Any(
                    reference => reference.Contains("Forge.Providers.Claude.Windows", StringComparison.Ordinal)),
                $"{project.Name} must reference Forge.Providers.Claude.Windows.");
        }
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void EveryFontRegisteredByMauiProgramIsPackagedAsAMauiFontItem()
    {
        // PR #112 review round 2 finding 2: ConfigureFonts only names a file, it never proves the
        // file is packaged. The original defect (no MauiFont item at all, so every glyph silently
        // fell back to the platform font) passed the full build, every test, and lint -- it took a
        // hand-run `dotnet msbuild -getItem:MauiFont` to see it. This pins both directions: a font
        // MauiProgram registers but the csproj does not package, and a font the csproj packages but
        // MauiProgram never registers.
        string desktopRoot = Path.Combine(RepositoryRoot.Find(), "src", "Forge.Desktop");
        string[] registeredFonts = AddFontPattern
            .Matches(File.ReadAllText(Path.Combine(desktopRoot, "MauiProgram.cs")))
            .Select(match => match.Groups["file"].Value)
            .ToArray();
        Assert.NotEmpty(registeredFonts);

        foreach (string font in registeredFonts)
        {
            Assert.True(
                File.Exists(Path.Combine(desktopRoot, "Resources", "Fonts", font)),
                $"MauiProgram registers the font '{font}', but src/Forge.Desktop/Resources/Fonts/{font} does not exist.");
        }

        string[] packagedFonts = XDocument.Load(Path.Combine(desktopRoot, "Forge.Desktop.csproj"))
            .Descendants("MauiFont")
            .Select(item => (string)(item.Attribute("Include") ?? item.Attribute("Update"))!)
            .SelectMany(include => ExpandProjectItemGlob(desktopRoot, include))
            .ToArray();

        Assert.Equal(
            registeredFonts.OrderBy(font => font, StringComparer.OrdinalIgnoreCase).ToArray(),
            packagedFonts.OrderBy(font => font, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    // Evaluates one MSBuild item Include against the real directory listing -- enough for the flat
    // `Resources\Fonts\*.ttf` shape this project uses (and a `**` prefix, should a future glob grow
    // one) without dragging full MSBuild evaluation into a unit test.
    private static IEnumerable<string> ExpandProjectItemGlob(string projectDirectory, string include)
    {
        string normalized = include.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        string pattern = Path.GetFileName(normalized);
        string relativeDirectory = Path.GetDirectoryName(normalized) ?? string.Empty;
        bool recursive = relativeDirectory.Contains("**", StringComparison.Ordinal);
        if (recursive)
        {
            relativeDirectory = relativeDirectory[..relativeDirectory.IndexOf("**", StringComparison.Ordinal)];
        }

        string directory = Path.Combine(projectDirectory, relativeDirectory);
        return Directory.Exists(directory)
            ? Directory
                .EnumerateFiles(
                    directory,
                    pattern,
                    recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Select(path => Path.GetFileName(path))
            : [];
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void TheSettingsKeysThisSliceShipsInertHaveNoConsumer()
    {
        // PR #124 review finding 3. ADR 0067 ships four keys deliberately inert, and for
        // `interaction.auto_approve_gate` that inertness is a safety claim: the human-approval gate
        // is unconditional today, and StageTransitionAssessor.NodeSucceededWithLiveEvidence returns
        // true for NodeState.Skipped, so a consumer that made the gate skippable would *silently*
        // satisfy the HumanApproved prerequisite. Prose in an ADR cannot fail a build; this can.
        //
        // The guard is not a prohibition -- it is a notification. It fails closed the moment any
        // consumer appears, which forces that change into its own slice with its own review, where
        // the reviewer confirms it is the right slice and deletes the key's entry from this list.
        (string Member, string Literal)[] inertKeys =
        [
            (nameof(ConfigurationKeys.AutoApproveGate), ConfigurationKeys.AutoApproveGate),
            (nameof(ConfigurationKeys.ShellTheme), ConfigurationKeys.ShellTheme),
            (nameof(ConfigurationKeys.ProvidersPriority), ConfigurationKeys.ProvidersPriority),
            (nameof(ConfigurationKeys.ModelsEffort), ConfigurationKeys.ModelsEffort),
        ];

        // The configuration layer itself declares, registers, and (de)serializes every key, so its
        // own three files reference them freely -- that is not consumption.
        string[] configurationLayer =
            ["ConfigurationContracts.cs", "ConfigurationRegistry.cs", "ConfigurationSchemaCodec.cs"];

        // The one allowed reference outside that layer, pinned by exact count rather than by file:
        // ForgeApplication.RequireRegisteredProviders validates a `providers.priority` *write*
        // against the provider catalog (ADR 0067). It never reads the stored value to route
        // anything, and a second reference in the same file would no longer be that check.
        Dictionary<(string Member, string File), int> allowedOutsideTheConfigurationLayer = new()
        {
            [(nameof(ConfigurationKeys.ProvidersPriority), "ForgeApplication.cs")] = 1,
        };

        string[] sourceFiles = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot.Find(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .ToArray();
        Assert.NotEmpty(sourceFiles);

        List<string> violations = [];
        foreach ((string member, string literal) in inertKeys)
        {
            foreach (string path in sourceFiles)
            {
                string file = Path.GetFileName(path);
                if (configurationLayer.Contains(file, StringComparer.Ordinal))
                {
                    continue;
                }

                int references = CountKeyReferences(path, member, literal);
                if (references == 0)
                {
                    continue;
                }

                allowedOutsideTheConfigurationLayer.TryGetValue((member, file), out int allowed);
                if (references != allowed)
                {
                    violations.Add(
                        $"{file} references ConfigurationKeys.{member} ('{literal}') {references} time(s), " +
                        $"but only {allowed} are allowed");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "ADR 0067 ships these configuration keys with no consumer, and its 'Consumption status' " +
            "section is the record of that. A consumer now exists: " + string.Join("; ", violations) +
            ". If that consumer is intended, review it as its own slice (for interaction.auto_approve_gate " +
            "that means adding a real bypass to a mandatory safety gate) and then remove the key from " +
            "this guard and from the ADR's table.");

        // PR #124 review round 2 finding 2b. The scan above structurally cannot see this: it exempts
        // ConfigurationRegistry.cs as "the configuration layer", but that file is also where each
        // key's ConfigurationScope is declared, and SprintOrchestrator.ConfigurationSnapshotAsync
        // sweeps *every* effective project-scope value into SprintDefinition.ConfigurationSnapshot
        // without naming a key. Flipping one of these four from User to Project -- a one-token edit
        // inside the exempt file -- would feed it to that sweep and into every frozen sprint with no
        // new textual reference anywhere. Pinning the scope costs one assertion and covers exactly
        // that class of change; the general "an exempt file can hide anything" problem is not solved
        // here and is not claimed to be.
        ConfigurationRegistry registry = new();
        Assert.All(
            inertKeys,
            key => Assert.Equal(ConfigurationScope.User, registry.FindRequired(key.Literal).Scope));
    }

    // Counts references to one key in one file, by its ConfigurationKeys member name and by its
    // literal string, ignoring whole-line comments -- ADR references such as "ADR 0067's
    // models.effort" live in doc comments across the runtime and are documentation, never
    // consumption.
    //
    // The member name is matched as a bare word, not as `ConfigurationKeys.<member>` (PR #124 review
    // round 2 finding 2a): `using static Forge.Configuration.ConfigurationKeys;` followed by a bare
    // `AutoApproveGate` compiles, reads the same const, and slipped through the qualified-only scan.
    // The tradeoff is that an unrelated identifier sharing one of these names now trips the guard
    // too. That is the safe direction for a check that exists to fail closed: the cost is one
    // reviewer glance, resolved by renaming the identifier or recording it in the caller's allowance
    // table, whereas the cost of the miss is an unnoticed consumer of a safety-relevant key.
    private static int CountKeyReferences(string path, string member, string literal)
    {
        string code = string.Join(
            '\n',
            File.ReadLines(path).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        return Regex.Count(code, $@"\b{Regex.Escape(member)}\b") + CountOccurrences(code, $"\"{literal}\"");
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal);
            index >= 0;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void ArtifactVersionMatchesCanonicalVersion()
    {
        string expected = $"{File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "VERSION")).Trim()}.0";
        string actual = typeof(ConfigurationRegistry).Assembly.GetName().Version!.ToString();

        Assert.Equal(expected, actual);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void EveryLoggerEventIdNamesExactlyOneEvent()
    {
        // PR #118 review round 2 finding 1: every hosted service and adapter that logs owns a block of
        // ids in one flat numeric space, because they all log into the same Host process -- a log
        // filter, alert rule, or telemetry query keyed on an id must select exactly one event:
        //
        //   2000-2009  ControlPlaneHostedService          2050-2069  ImplementationExecutionHostedService
        //   2010-2019  ResumeSchedulerHostedService        2070-2079  WindowsJobObjectProcessContainment
        //   2020-2029  NotificationDeliveryHostedService   2080-2089  ReviewExecutionHostedService
        //   2030-2039  IntakeExecutionHostedService
        //   2040-2049  PlanningExecutionHostedService
        //
        // The blocks themselves are a convention this test deliberately does not encode (a service
        // outgrowing one is legitimate -- implementation already did, which is how the collision
        // arose). What it pins is the invariant the blocks exist to protect: id and name are one
        // another's key. LoggerMessage.Define captures the EventId in a closure, so the declarations
        // are read from source rather than reflected off the compiled delegates.
        (int Id, string Name, string File)[] declarations = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot.Find(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .SelectMany(path => EventIdPattern.Matches(File.ReadAllText(path))
                .Select(match => (
                    Id: int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture),
                    Name: match.Groups["name"].Value,
                    File: Path.GetFileName(path))))
            .ToArray();
        Assert.NotEmpty(declarations);

        string[] reusedIds = declarations
            .GroupBy(declaration => declaration.Id)
            .Where(group => group.Select(declaration => declaration.Name).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => $"{group.Key} names " +
                string.Join(", ", group.Select(declaration => $"{declaration.Name} ({declaration.File})")))
            .ToArray();
        Assert.True(
            reusedIds.Length == 0,
            $"EventId(s) reused for unrelated events: {string.Join("; ", reusedIds)}.");

        string[] splitNames = declarations
            .GroupBy(declaration => declaration.Name, StringComparer.Ordinal)
            .Where(group => group.Select(declaration => declaration.Id).Distinct().Count() > 1)
            .Select(group => $"{group.Key} declared as " +
                string.Join(", ", group.Select(declaration => declaration.Id).Distinct()))
            .ToArray();
        Assert.True(
            splitNames.Length == 0,
            $"Event name(s) declared with more than one id: {string.Join("; ", splitNames)}.");
    }

    private static bool IsBuildOutput(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is "bin" or "obj");

    // new EventId(<id>, "<name>") -- the only shape this codebase declares them in.
    private static readonly Regex EventIdPattern =
        new(@"new EventId\(\s*(?<id>\d+)\s*,\s*""(?<name>[^""]+)""", RegexOptions.Compiled);

    // Every OS adapter in this codebase today is Windows-only (ADR 0008; see also ADR 0056 for
    // IProcessContainment, whose POSIX investigation ended in removal rather than a real adapter).
    // A prior revision speculatively added "Linux"/"macOS"/"Posix" monikers for that never-built
    // POSIX adapter; they had zero callers and "macOS" would not even match this codebase's own
    // PascalCase project-naming convention (it would need "MacOs" or "Macos" to satisfy
    // StringComparison.Ordinal against a real future project name). Removed rather than fixed: a
    // genuinely new OS family should pick its own moniker and extend this list then, when a real
    // adapter's actual naming is known, instead of this test guessing it in advance.
    private static readonly string[] RecognizedOsAdapterMonikers = ["Windows"];

    // fonts.AddFont("<file>", "<alias>") -- only the file name matters here; the alias is what XAML
    // binds to and is already covered by the theme's own FontFamily resources.
    private static readonly Regex AddFontPattern =
        new(@"AddFont\(\s*""(?<file>[^""]+)""", RegexOptions.Compiled);

    // The source tree does not change within a test run, and every [Fact] above needs the full project list, so
    // it is parsed once and shared instead of re-walking/re-parsing src/*.csproj per assertion.
    private static readonly Lazy<IReadOnlyList<SourceProject>> Projects = new(LoadSourceProjects);

    private static readonly Lazy<IReadOnlyDictionary<string, bool>> AdapterByFullPath = new(
        () => SourceProjects().ToDictionary(
            project => Path.GetFullPath(project.Path),
            project => project.IsOsAdapter,
            StringComparer.OrdinalIgnoreCase));

    private static IReadOnlyList<SourceProject> SourceProjects() => Projects.Value;

    private static List<SourceProject> LoadSourceProjects()
    {
        string sourceRoot = Path.Combine(RepositoryRoot.Find(), "src");
        List<SourceProject> projects = [];
        foreach (string project in Directory.GetFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories))
        {
            XDocument document = XDocument.Load(project);
            projects.Add(new(
                Path.GetFileNameWithoutExtension(project),
                project,
                document.Descendants("ProjectReference").Select(item => (string)item.Attribute("Include")!).ToArray(),
                document.Descendants("TargetFramework").Concat(document.Descendants("TargetFrameworks"))
                    .SelectMany(element => element.Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    .ToArray(),
                document.Descendants("ForgeOsAdapter")
                    .Any(element => bool.TryParse(element.Value, out bool value) && value),
                document.Descendants("OutputType")
                    .Any(element => string.Equals(element.Value, "Exe", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(element.Value, "WinExe", StringComparison.OrdinalIgnoreCase))));
        }

        return projects;
    }

    private static bool IsOsAdapter(string projectPath) =>
        AdapterByFullPath.Value.TryGetValue(Path.GetFullPath(projectPath), out bool isAdapter) && isAdapter;

    private static string ResolveReference(string fromProject, string relativeReference) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fromProject)!, relativeReference));

    private static bool ContainsOsMoniker(string targetFramework) =>
        targetFramework.Contains("-windows", StringComparison.OrdinalIgnoreCase) ||
        targetFramework.Contains("-linux", StringComparison.OrdinalIgnoreCase) ||
        targetFramework.Contains("-macos", StringComparison.OrdinalIgnoreCase) ||
        targetFramework.Contains("-ios", StringComparison.OrdinalIgnoreCase) ||
        targetFramework.Contains("-android", StringComparison.OrdinalIgnoreCase);

    private sealed record SourceProject(
        string Name,
        string Path,
        IReadOnlyList<string> References,
        IReadOnlyList<string> TargetFrameworks,
        bool IsOsAdapter,
        bool IsCompositionRoot);
}

internal static class RepositoryRoot
{
    public static string Find()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Forge.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
            throw new DirectoryNotFoundException("Could not locate the Forge repository root.");
    }
}
