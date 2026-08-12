using System.Text.Json;
using Forge.Application;

namespace Forge.Localization;

/// <summary>Formatting shared by every surface (CLI, Desktop) that renders durable state as localized/machine text.</summary>
public static class SurfaceFormatting
{
    public static string StartupMessageKey(StartupState state) => state switch
    {
        StartupState.Ready => MessageKeys.StartupReady,
        StartupState.Blocked => MessageKeys.StartupBlocked,
        _ => MessageKeys.StartupFailed,
    };

    /// <summary>Renders an enum as the culture-invariant snake_case token every machine contract uses.</summary>
    public static string Machine<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString()!);
}
