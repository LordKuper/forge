namespace Forge.Updater.Windows;

public interface IWindowsUserPathRegistrar
{
    UpdateDiagnostic Ensure(string directory);
}

public sealed class WindowsUserPathRegistrar(
    Func<string?>? read = null,
    Action<string>? write = null) : IWindowsUserPathRegistrar
{
    private readonly Func<string?> read = read ?? (() => Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User));
    private readonly Action<string> write = write ?? (value => Environment.SetEnvironmentVariable("Path", value, EnvironmentVariableTarget.User));

    public UpdateDiagnostic Ensure(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string expected = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? current = read();
        if ((current ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Any(path => string.Equals(path, expected, StringComparison.OrdinalIgnoreCase)))
        {
            return UpdateDiagnostic.None;
        }

        try
        {
            write(string.IsNullOrWhiteSpace(current) ? expected : $"{current.TrimEnd(';')};{expected}");
            return UpdateDiagnostic.None;
        }
        catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new(UpdateDiagnosticCode.ActivationFailed, "The Forge command directory could not be added to the user PATH.");
        }
    }
}
