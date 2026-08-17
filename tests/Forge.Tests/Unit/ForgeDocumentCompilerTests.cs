using System.Text;
using Forge.Application;
using Forge.Compiler;

namespace Forge.UnitTests;

public sealed class ForgeDocumentCompilerTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task MissingRulesAndKnowledgeDirectoriesYieldAnEmptySet()
    {
        using TempForgeProject project = new();

        ForgeDocumentSet set = await new ForgeDocumentCompiler().ParseAsync(
            project.Root, TestContext.Current.CancellationToken);

        Assert.Empty(set.Documents);
        Assert.Empty(set.Errors);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ValidDocumentsParseWithDerivedKindAndResolvedReferences()
    {
        using TempForgeProject project = new();
        project.WriteKnowledge(
            "adr-0006.md",
            Frontmatter("adr-0006", "ADR 0006 summary") + "Body about review convergence.");
        project.WriteRule(
            "testing.md",
            Frontmatter("testing-invariant", "Testing invariant", references: ["knowledge/adr-0006.md"]) +
            "Implement before testing.");

        ForgeDocumentSet set = await new ForgeDocumentCompiler().ParseAsync(
            project.Root, TestContext.Current.CancellationToken);

        Assert.Empty(set.Errors);
        Assert.Equal(2, set.Documents.Count);
        ForgeDocument rule = Assert.Single(set.Documents, d => d.Kind == ForgeDocumentKind.Rule);
        Assert.Equal("testing-invariant", rule.Id);
        Assert.Equal(ForgeDocumentScope.Project, rule.Scope);
        Assert.Equal("rules/testing.md", rule.RelativePath);
        Assert.Equal(4000, rule.ContextLimitTokens);
        ForgeDocumentReference reference = Assert.Single(rule.References);
        Assert.Equal("knowledge/adr-0006.md", reference.RelativePath);
        Assert.Equal(Path.Combine(project.ForgeRoot, "knowledge", "adr-0006.md"), reference.ResolvedPath);
        ForgeDocument knowledge = Assert.Single(set.Documents, d => d.Kind == ForgeDocumentKind.Knowledge);
        Assert.Equal("adr-0006", knowledge.Id);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DocumentMissingFrontmatterDelimiterIsRejectedAloneOthersStillParse()
    {
        using TempForgeProject project = new();
        project.WriteRule("broken.md", "No frontmatter here.");
        project.WriteRule("ok.md", Frontmatter("ok-rule", "OK rule") + "Fine.");

        ForgeDocumentSet set = await new ForgeDocumentCompiler().ParseAsync(
            project.Root, TestContext.Current.CancellationToken);

        ForgeDocumentError error = Assert.Single(set.Errors);
        Assert.Equal("rules/broken.md", error.RelativePath);
        Assert.Equal(ForgeDocumentDiagnosticCodes.FrontmatterInvalid, error.DiagnosticCode);
        ForgeDocument ok = Assert.Single(set.Documents);
        Assert.Equal("ok-rule", ok.Id);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FrontmatterViolatingTheSchemaIsRejected()
    {
        using TempForgeProject project = new();
        project.WriteRule(
            "bad.md",
            "---\nschema_version: \"1.0.0\"\nid: bad\nscope: project\n---\nMissing title.");

        ForgeDocumentSet set = await new ForgeDocumentCompiler().ParseAsync(
            project.Root, TestContext.Current.CancellationToken);

        ForgeDocumentError error = Assert.Single(set.Errors);
        Assert.Equal(ForgeDocumentDiagnosticCodes.FrontmatterInvalid, error.DiagnosticCode);
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("/etc/passwd")]
    [InlineData("sub\\file.md")]
    [InlineData("C:/absolute.md")]
    [Trait("Category", "Unit")]
    public async Task UnsafeReferenceShapesAreRejected(string reference)
    {
        using TempForgeProject project = new();
        project.WriteRule("referrer.md", Frontmatter("referrer", "Referrer", references: [reference]) + "Body.");

        ForgeDocumentSet set = await new ForgeDocumentCompiler().ParseAsync(
            project.Root, TestContext.Current.CancellationToken);

        ForgeDocumentError error = Assert.Single(set.Errors);
        Assert.Equal("rules/referrer.md", error.RelativePath);
        Assert.Equal(ForgeDocumentDiagnosticCodes.ReferenceUnsafe, error.DiagnosticCode);
        Assert.Empty(set.Documents);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReferenceToAFileTheCompilerDidNotDiscoverIsRejected()
    {
        using TempForgeProject project = new();
        // Exists on disk under a known top-level directory, but nested — discovery is
        // TopDirectoryOnly, so it never becomes a candidate document.
        Directory.CreateDirectory(Path.Combine(project.ForgeRoot, "knowledge", "nested"));
        await File.WriteAllTextAsync(
            Path.Combine(project.ForgeRoot, "knowledge", "nested", "hidden.md"),
            Frontmatter("hidden", "Hidden") + "Body.",
            TestContext.Current.CancellationToken);
        project.WriteRule(
            "referrer.md",
            Frontmatter("referrer", "Referrer", references: ["knowledge/nested/hidden.md"]) + "Body.");

        ForgeDocumentSet set = await new ForgeDocumentCompiler().ParseAsync(
            project.Root, TestContext.Current.CancellationToken);

        ForgeDocumentError error = Assert.Single(set.Errors);
        Assert.Equal(ForgeDocumentDiagnosticCodes.ReferenceUnsafe, error.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SymlinkReferenceEscapingForgeRootIsRejected()
    {
        using TempForgeProject project = new();
        string outside = Path.Combine(project.Root, "outside.md");
        await File.WriteAllTextAsync(outside, "secret", TestContext.Current.CancellationToken);
        string link = Path.Combine(project.ForgeRoot, "knowledge", "escape.md");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception symlinkError) when (symlinkError is UnauthorizedAccessException or IOException)
        {
            // Creating a symlink requires elevated privileges or Developer Mode on some Windows
            // CI runners; the safe-path check under test cannot be exercised there.
            return;
        }

        project.WriteRule("referrer.md", Frontmatter("referrer", "Referrer", references: ["knowledge/escape.md"]) + "Body.");

        ForgeDocumentSet set = await new ForgeDocumentCompiler().ParseAsync(
            project.Root, TestContext.Current.CancellationToken);

        // escape.md itself is also a discovered candidate (a symlinked .md file under a known
        // directory) and fails to parse on its own merits (its target's content has no
        // frontmatter) — this asserts only referrer.md's reference-safety error.
        ForgeDocumentError error = Assert.Single(set.Errors, e => e.RelativePath == "rules/referrer.md");
        Assert.Equal(ForgeDocumentDiagnosticCodes.ReferenceUnsafe, error.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DocumentExceedingItsContextLimitIsRejected()
    {
        using TempForgeProject project = new();
        project.WriteRule("big.md", Frontmatter("big", "Big", contextLimitTokens: 5) + new string('x', 100));

        ForgeDocumentSet set = await new ForgeDocumentCompiler().ParseAsync(
            project.Root, TestContext.Current.CancellationToken);

        ForgeDocumentError error = Assert.Single(set.Errors);
        Assert.Equal(ForgeDocumentDiagnosticCodes.ContextLimitExceeded, error.DiagnosticCode);
        Assert.Empty(set.Documents);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DuplicateIdsAcrossDocumentsAreAllRejected()
    {
        using TempForgeProject project = new();
        project.WriteRule("a.md", Frontmatter("dup", "A") + "Body A.");
        project.WriteKnowledge("b.md", Frontmatter("dup", "B") + "Body B.");

        ForgeDocumentSet set = await new ForgeDocumentCompiler().ParseAsync(
            project.Root, TestContext.Current.CancellationToken);

        Assert.Empty(set.Documents);
        Assert.Equal(2, set.Errors.Count);
        Assert.All(set.Errors, error => Assert.Equal(ForgeDocumentDiagnosticCodes.DuplicateId, error.DiagnosticCode));
    }

    private static string Frontmatter(
        string id,
        string title,
        IReadOnlyList<string>? references = null,
        int? contextLimitTokens = null)
    {
        StringBuilder builder = new();
        builder.Append("---\nschema_version: \"1.0.0\"\nid: ").Append(id)
            .Append("\ntitle: ").Append(title)
            .Append("\nscope: project\n");
        if (references is { Count: > 0 })
        {
            builder.Append("references:\n");
            foreach (string reference in references)
            {
                builder.Append("  - \"").Append(reference).Append("\"\n");
            }
        }

        if (contextLimitTokens is not null)
        {
            builder.Append("context_limit_tokens: ").Append(contextLimitTokens.Value).Append('\n');
        }

        builder.Append("---\n");
        return builder.ToString();
    }

    private sealed class TempForgeProject : IDisposable
    {
        public TempForgeProject()
        {
            Root = Directory.CreateTempSubdirectory("forge-doc-tests-").FullName;
            ForgeRoot = ProjectRootResolver.ForgeDirectory(Root);
            Directory.CreateDirectory(ForgeRoot);
        }

        public string Root { get; }

        public string ForgeRoot { get; }

        public void WriteRule(string fileName, string content) => Write("rules", fileName, content);

        public void WriteKnowledge(string fileName, string content) => Write("knowledge", fileName, content);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a locked handle on a CI runner is not a test failure.
            }
        }

        private void Write(string directoryName, string fileName, string content)
        {
            string directory = Path.Combine(ForgeRoot, directoryName);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, fileName), content, Encoding.UTF8);
        }
    }
}
