using Forge.Domain;

namespace Forge.Presentation;

public static class CapabilityIds
{
    public const string ProjectStatusView = "project.status.view";
    public const string ConfigurationManage = "configuration.manage";
}

public interface ICommand;

public interface IQuery<out TResult>;

public interface IPresentationEvent;

public sealed record ShowStatusQuery : IQuery<ProjectStatusView>;

public sealed record ProjectStatusView(
    string ProjectRoot,
    IReadOnlyList<SprintSnapshot> Sprints);

public sealed record RefreshRequested : IPresentationEvent;
