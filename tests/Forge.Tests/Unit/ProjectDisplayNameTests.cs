using Forge.Desktop.Presentation;

namespace Forge.UnitTests;

public sealed class ProjectDisplayNameTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ResolvePrefersTheAliasWhenSet() =>
        Assert.Equal("My Project", ProjectDisplayName.Resolve(@"C:\repos\forge", "My Project"));

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveFallsBackToTheRootDirectoryNameWhenNoAlias(string? alias) =>
        Assert.Equal("forge", ProjectDisplayName.Resolve(@"C:\repos\forge", alias));

    [Fact]
    [Trait("Category", "Unit")]
    public void ResolveTrimsATrailingSeparatorBeforeTakingTheDirectoryName() =>
        Assert.Equal("forge", ProjectDisplayName.Resolve(@"C:\repos\forge\", null));

    [Fact]
    [Trait("Category", "Unit")]
    public void ResolveFallsBackToTheFullRootWhenNoDirectoryNameCanBeDerived() =>
        Assert.Equal("/", ProjectDisplayName.Resolve("/", null));
}
