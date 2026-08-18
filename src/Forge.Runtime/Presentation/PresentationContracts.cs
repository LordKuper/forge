using Forge.Domain;

namespace Forge.Presentation;

public static class CapabilityIds
{
    public const string ProjectSnapshot = "project.snapshot";
    public const string ProjectInitialize = "project.initialize";
    public const string ConfigurationManage = "configuration.manage";
    public const string ProviderHealth = "provider.health";
    public const string WorkflowReview = "workflow.review";
    public const string AttemptSupersede = "attempt.supersede";
    public const string ControlEvents = "control.events";
    public const string IntegrationSkill = "integration.skill";

    /// <summary>Capabilities implemented on both surfaces by the current stage.</summary>
    public static IReadOnlyList<string> Implemented { get; } =
    [
        ProjectSnapshot,
        ProjectInitialize,
        ConfigurationManage,
        ProviderHealth,
        WorkflowReview,
        AttemptSupersede,
        ControlEvents,
        IntegrationSkill,
    ];
}

public interface ICommand;

public interface IQuery<out TResult>;

public interface IPresentationEvent;

public sealed record ShowStatusQuery : IQuery<ProjectStatusView>;

public sealed record ProjectStatusView(
    string ProjectRoot,
    IReadOnlyList<SprintSnapshot> Sprints);

public sealed record RefreshRequested : IPresentationEvent;
