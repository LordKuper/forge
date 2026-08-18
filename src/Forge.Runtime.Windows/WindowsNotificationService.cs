using System.ComponentModel;
using Forge.Application;

namespace Forge.Runtime.Windows;

/// <summary>
/// ADR 0024: delivers a best-effort local OS notification via
/// <see cref="NotifyIcon.ShowBalloonTip(int, string, string, ToolTipIcon)"/>. Windows 10/11 render
/// a balloon-tip call from the shell as a standard Action Center toast, so this needs neither the
/// modern Windows App SDK notification API's heavier bootstrap (a message pump, package identity,
/// or an unpackaged-app COM/AUMID registration step) nor any new NuGet dependency — a deliberate
/// trade-off: no click-activation or argument routing back into Forge, acceptable since ADR 0024
/// names neither as in scope ("best-effort" local notifications only).
/// </summary>
/// <remarks>
/// The tray icon stays visible for this service's own lifetime (bound to the owning Host process)
/// rather than toggling around each call — <see cref="NotifyIcon.ShowBalloonTip(int, string,
/// string, ToolTipIcon)"/> is a no-op while <see cref="NotifyIcon.Visible"/> is <see
/// langword="false"/>, and hiding it again immediately after each call risks racing the shell's own
/// asynchronous balloon rendering. The accepted cost is a small, persistent tray icon while a
/// project's Host process runs — named honestly here rather than silently assumed away.
/// </remarks>
public sealed class WindowsNotificationService : INotificationService, IDisposable
{
    private readonly NotifyIcon icon = new()
    {
        Icon = SystemIcons.Information,
        Text = "Forge",
        Visible = true,
    };

    public Task NotifyAsync(string title, string body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);
        try
        {
            icon.ShowBalloonTip(10_000, title, body, ToolTipIcon.Info);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            // ADR 0005: "A notification is never the authoritative record and a delivery failure
            // never changes workflow state" -- the caller (NotificationDeliveryHostedService) never
            // sees this as an exception.
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }
}
