namespace Forge.Updater.Windows;

internal static class WindowsCommandShim
{
    private const string Command = "@echo off\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%~dp0forge.ps1\" %*\r\nexit /b %ERRORLEVEL%\r\n";
    private const string Script = "& {\r\n    $ErrorActionPreference = 'Stop'\r\n    $root = Split-Path -Parent $PSScriptRoot\r\n    $version = (Get-Content -Raw (Join-Path $root 'current.json') | ConvertFrom-Json).Version\r\n    $hostPath = Join-Path $root ('versions\\' + $version + '\\forge.exe')\r\n    if (-not (Test-Path -LiteralPath $hostPath -PathType Leaf)) { throw 'The active Forge host is missing.' }\r\n    & $hostPath @args\r\n    exit $LASTEXITCODE\r\n}\r\n";

    public static void Ensure(string root)
    {
        string current = Path.Combine(root, "current");
        Directory.CreateDirectory(current);
        WriteKnownFile(Path.Combine(current, "forge.cmd"), Command);
        WriteKnownFile(Path.Combine(current, "forge.ps1"), Script);
    }

    public static void Remove(string root)
    {
        string current = Path.Combine(root, "current");
        DeleteKnownFile(Path.Combine(current, "forge.cmd"), Command);
        DeleteKnownFile(Path.Combine(current, "forge.ps1"), Script);
    }

    private static void WriteKnownFile(string path, string contents)
    {
        if (File.Exists(path) && !string.Equals(File.ReadAllText(path), contents, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The stable Forge command shim is not recognized: {path}");
        }

        File.WriteAllText(path, contents);
    }

    private static void DeleteKnownFile(string path, string contents)
    {
        if (File.Exists(path) && string.Equals(File.ReadAllText(path), contents, StringComparison.Ordinal))
        {
            File.Delete(path);
        }
    }
}
