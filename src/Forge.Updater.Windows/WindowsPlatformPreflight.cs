using Forge.Application;

namespace Forge.Updater.Windows;

/// <summary>
/// Reports the detected target and the single resolved update strategy to the shared startup
/// pipeline. Resolution happens before any network or filesystem mutation.
/// </summary>
public sealed class WindowsPlatformPreflight(
    IUpdateTargetDetector detector,
    PlatformUpdateStrategyResolver resolver) : IPlatformPreflight
{
    public PlatformPreflightResult Check()
    {
        UpdateTarget target = detector.Detect();
        StrategyResolution resolution = resolver.Resolve(target);
        return new(
            target.OperatingSystem,
            target.Architecture,
            resolution.IsSuccess,
            resolution.IsSuccess
                ? DiagnosticCodes.None
                : resolution.Diagnostic.Code == UpdateDiagnosticCode.InvalidComposition
                    ? DiagnosticCodes.InternalError
                    : DiagnosticCodes.PlatformNotSupported);
    }
}
