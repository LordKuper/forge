using System.Globalization;
using System.Text.Json;
using Forge.Application;
using Forge.Providers;

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

    /// <summary>Same as <see cref="Machine{TEnum}(TEnum)"/>, but renders <see langword="null"/>
    /// (e.g. a disabled provider's never-probed <c>state</c>) as <c>"-"</c> instead of throwing.</summary>
    public static string Machine<TEnum>(TEnum? value)
        where TEnum : struct, Enum =>
        value is { } resolved ? Machine(resolved) : "-";

    /// <summary>One provider's row, shared by every surface that lists provider health (`forge
    /// models`, Desktop) so the `provider-health-parity` capability can never drift between them —
    /// distinguishes every state ADR 0008 requires: id, enabled/disabled, install state, version,
    /// update availability, authentication, and diagnostic code.</summary>
    public static string ProviderRow(ProviderHealthEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string updateAvailable = entry.UpdateAvailable switch
        {
            true => "update_available",
            false => "current",
            null => "-",
        };
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{entry.Id} {(entry.Enabled ? "enabled" : "disabled")} {Machine(entry.State)} " +
                $"{entry.Version ?? "-"} {updateAvailable} {Machine(entry.Authentication)} {entry.DiagnosticCode}");
    }
}
