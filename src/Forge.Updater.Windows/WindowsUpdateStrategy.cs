namespace Forge.Updater.Windows;

public sealed class WindowsUpdateStrategy : IPlatformUpdateStrategy
{
    public bool Supports(UpdateTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return string.Equals(target.OperatingSystem, "windows", StringComparison.Ordinal) &&
            (string.Equals(target.Architecture, "x64", StringComparison.Ordinal) ||
             string.Equals(target.Architecture, "arm64", StringComparison.Ordinal));
    }
}
