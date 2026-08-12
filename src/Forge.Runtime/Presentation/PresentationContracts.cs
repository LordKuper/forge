using Forge.Domain;

namespace Forge.Presentation;

public static class CapabilityIds
{
    public const string StartupStatus = "startup.status";
    public const string ProjectSnapshot = "project.snapshot";
    public const string ProjectInitialize = "project.initialize";
    public const string ProjectStatus = "project.status";
    public const string ProjectNext = "project.next";
    public const string ConfigurationManage = "configuration.manage";

    /// <summary>Capabilities implemented on both surfaces by the current stage.</summary>
    public static IReadOnlyList<string> Implemented { get; } =
    [
        StartupStatus,
        ProjectSnapshot,
        ProjectInitialize,
        ProjectStatus,
        ProjectNext,
        ConfigurationManage,
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
