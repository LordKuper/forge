using System.Globalization;
using System.Reflection;

namespace Forge.Updater.Windows;

public interface IWindowsDesktopShortcut
{
    DesktopShortcutSnapshot Capture();

    UpdateDiagnostic Ensure(string executablePath);

    void Restore(DesktopShortcutSnapshot snapshot);
}

public sealed record DesktopShortcutSnapshot(byte[]? Contents);

public sealed class WindowsDesktopShortcut : IWindowsDesktopShortcut
{
    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        "Programs",
        "Forge Desktop.lnk");

    public DesktopShortcutSnapshot Capture() =>
        new(File.Exists(ShortcutPath) ? File.ReadAllBytes(ShortcutPath) : null);

    public UpdateDiagnostic Ensure(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!File.Exists(executablePath))
        {
            return new(UpdateDiagnosticCode.ActivationFailed, "The active Forge Desktop host is missing.");
        }

        try
        {
            string programs = Path.GetDirectoryName(ShortcutPath)!;
            Directory.CreateDirectory(programs);
            Type shellType = Type.GetTypeFromProgID("WScript.Shell") ??
                throw new InvalidOperationException("Windows Script Host is unavailable.");
            object shell = Activator.CreateInstance(shellType) ??
                throw new InvalidOperationException("Windows Script Host could not be started.");
            object shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                null,
                shell,
                [ShortcutPath],
                CultureInfo.InvariantCulture) ?? throw new InvalidOperationException("The Forge Desktop shortcut could not be created.");
            Type shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [executablePath], CultureInfo.InvariantCulture);
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [Path.GetDirectoryName(executablePath)!], CultureInfo.InvariantCulture);
            shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, ["Forge Desktop"], CultureInfo.InvariantCulture);
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null, CultureInfo.InvariantCulture);
            return UpdateDiagnostic.None;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or UnauthorizedAccessException or TargetInvocationException)
        {
            return new(UpdateDiagnosticCode.ActivationFailed, "The Forge Desktop Start Menu shortcut could not be updated.");
        }
    }

    public void Restore(DesktopShortcutSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Contents is null)
        {
            if (File.Exists(ShortcutPath))
            {
                File.Delete(ShortcutPath);
            }

            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);
        File.WriteAllBytes(ShortcutPath, snapshot.Contents);
    }
}
