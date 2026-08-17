using Forge.Domain;

namespace Forge.UnitTests;

public sealed class BuiltInRubricTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void EveryItemHasAUniqueNonEmptyIdAndDescription()
    {
        Assert.NotEmpty(BuiltInRubric.Items);
        Assert.Equal(BuiltInRubric.Items.Count, BuiltInRubric.Items.Select(item => item.Id).Distinct().Count());
        Assert.All(BuiltInRubric.Items, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Id));
            Assert.False(string.IsNullOrWhiteSpace(item.Description));
        });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CatalogCoversBothThreatAndRuleCategories()
    {
        Assert.Contains(BuiltInRubric.Items, item => item.Category == RubricCategory.Threat);
        Assert.Contains(BuiltInRubric.Items, item => item.Category == RubricCategory.Rule);
    }
}
