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
                project.Name.Contains("Windows", StringComparison.Ordinal),
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
        // MutexProjectLease and ProviderInstallLock are two independent copies of the same
        // capability-probed Global\-namespace-when-possible construction (see ProjectLease.cs's
        // remarks for why the real lease/lock name must never itself decide the fallback). Nothing
        // else keeps them from silently diverging.
        string sourceRoot = Path.Combine(RepositoryRoot.Find(), "src");
        string projectLeaseCode = ExtractSharedMutexConstructionCode(
            Path.Combine(sourceRoot, "Forge.Host.Client", "ProjectLease.cs"));
        string providerInstallLockCode = ExtractSharedMutexConstructionCode(
            Path.Combine(sourceRoot, "Forge.Runtime", "Providers", "ProviderInstallLock.cs"));

        Assert.Equal(projectLeaseCode, providerInstallLockCode);
    }

    // Captures from the CanCreateGlobalMutexes field through the end of CreateMutex's `out _);`,
    // and normalizes away the one intentional difference (the parameter name — leaseName vs
    // lockName) and all whitespace, so the comparison catches any other divergence in the
    // construction logic itself (options, exception type, ordering) without being brittle about
    // formatting.
    private static string ExtractSharedMutexConstructionCode(string path)
    {
        string source = File.ReadAllText(path);
        int start = source.IndexOf(
            "private static readonly Lazy<bool> CanCreateGlobalMutexes", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{path} does not declare CanCreateGlobalMutexes.");
        int createMutexStart = source.IndexOf(
            "private static Mutex CreateMutex(", start, StringComparison.Ordinal);
        Assert.True(createMutexStart > start, $"{path} does not declare CreateMutex after CanCreateGlobalMutexes.");
        int end = source.LastIndexOf("out _);", StringComparison.Ordinal);
        Assert.True(end > createMutexStart, $"{path}'s CreateMutex does not end with the expected construction.");
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
    public void ArtifactVersionMatchesCanonicalVersion()
    {
        string expected = $"{File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "VERSION")).Trim()}.0";
        string actual = typeof(ConfigurationRegistry).Assembly.GetName().Version!.ToString();

        Assert.Equal(expected, actual);
    }

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
