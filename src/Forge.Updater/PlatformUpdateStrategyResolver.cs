using System.Runtime.InteropServices;

namespace Forge.Updater;

public sealed record StrategyResolution(IPlatformUpdateStrategy? Strategy, UpdateDiagnostic Diagnostic)
{
    public bool IsSuccess => Strategy is not null;
}

public sealed class RuntimeUpdateTargetDetector(string packaging = "portable_bundle") : IUpdateTargetDetector
{
    public UpdateTarget Detect() => new(
        GetOperatingSystem(),
        GetArchitecture(RuntimeInformation.ProcessArchitecture),
        packaging);

    private static string GetOperatingSystem()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macos";
        }

        return RuntimeInformation.OSDescription;
    }

    private static string GetArchitecture(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        Architecture.Arm => "arm",
        _ => architecture.ToString(),
    };
}

public sealed class PlatformUpdateStrategyResolver(IEnumerable<IPlatformUpdateStrategy> strategies)
{
    private readonly IReadOnlyList<IPlatformUpdateStrategy> strategies =
        strategies?.ToArray() ?? throw new ArgumentNullException(nameof(strategies));

    public StrategyResolution Resolve(UpdateTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        IPlatformUpdateStrategy[] matches = strategies.Where(strategy => strategy.Supports(target)).ToArray();
        return matches.Length switch
        {
            1 => new(matches[0], UpdateDiagnostic.None),
            0 => new(
                null,
                new(UpdateDiagnosticCode.PlatformNotSupported, $"No update strategy supports '{target.OperatingSystem}/{target.Architecture}/{target.Packaging}'.")),
            _ => new(
                null,
                new(UpdateDiagnosticCode.InvalidComposition, "More than one update strategy supports the detected target.")),
        };
    }
}
