namespace Forge.Updater;

public sealed record UpdateTarget(string OperatingSystem, string Architecture);

public interface IPlatformUpdateStrategy
{
    bool Supports(UpdateTarget target);
}

public interface IForgeReleaseClient;

public interface IForgeSelfUpdater;
