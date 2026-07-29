using Forge.Domain;

namespace Forge.UnitTests;

public sealed class WorkflowContractTests
{
    [Fact]
    [Trait("Category", "Contract")]
    public void SprintStatesMatchFrozenV1Contract()
    {
        string[] states = Enum
            .GetValues<SprintState>()
            .Select(ToSnakeCase)
            .ToArray();

        Assert.Equal(
            [
                "draft",
                "ready",
                "running",
                "awaiting_human",
                "blocked",
                "failed",
                "ready_to_finalize",
                "completed",
                "cancelled",
            ],
            states);
    }

    private static string ToSnakeCase(SprintState state) =>
        string.Concat(
            state.ToString().Select(
                (character, index) =>
                    char.IsUpper(character) && index > 0
                        ? $"_{char.ToLowerInvariant(character)}"
                        : char.ToLowerInvariant(character).ToString()));
}
