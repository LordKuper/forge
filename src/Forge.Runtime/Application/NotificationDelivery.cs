namespace Forge.Application;

/// <summary>ADR 0007: "Notification policy and durable attention events" are cross-platform;
/// "OS notification delivery" is the one adapter-owned piece behind this port. ADR 0005:
/// notifications are best-effort — an implementation must never throw for an ordinary delivery
/// failure (the platform has no notifications enabled, the OS call itself failed); a caller never
/// treats a failed delivery as changing durable workflow state.</summary>
public interface INotificationService
{
    Task NotifyAsync(string title, string body, CancellationToken cancellationToken);
}

/// <summary>The default registration (ADR 0024) until a platform composition root overrides it —
/// mirrors <c>UnsupportedPlatformPreflight</c>'s role for <c>IPlatformPreflight</c>. Never throws;
/// silently discards, since "no OS adapter installed" is not itself a delivery failure worth
/// logging on every tick.</summary>
public sealed class NullNotificationService : INotificationService
{
    public Task NotifyAsync(string title, string body, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>Durable per-project resume point for a notification-delivery sweep: an opaque
/// <see cref="ControlEventsCursor"/> token, the exact mechanism `forge events --cursor` already
/// uses, so a delivered event is never re-delivered after a Host restart. Deliberately not wrapped
/// in <c>AtomicConfigurationFile</c>'s full crash-durability machinery (fsync'd directory flush,
/// `.previous` fallback) — ADR 0005 frames notification delivery as best-effort by design, so
/// losing a write to a genuine crash costs, at most, one already-seen sweep's worth of
/// re-delivered notifications on restart, an accepted cost matching the feature's own stated
/// bound.</summary>
public static class NotificationDeliveryCursorStore
{
    private const string DirectoryName = "notifications";
    private const string FileName = "cursor.json";

    public static string CursorFilePath(string projectRoot) =>
        Path.Combine(ProjectRootResolver.ForgeDirectory(projectRoot), DirectoryName, FileName);

    public static async Task<string?> LoadAsync(string projectRoot, CancellationToken cancellationToken)
    {
        string path = CursorFilePath(projectRoot);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static async Task SaveAsync(string projectRoot, string cursor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor);
        string path = CursorFilePath(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = string.Create(
            System.Globalization.CultureInfo.InvariantCulture, $"{path}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temp, cursor, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // A failed write or rename must not leave a `.tmp` file behind -- this runs on a
            // recurring timer, so a persistent failure would otherwise leak one file per tick
            // forever. Cleanup itself is best-effort: a failure deleting the temp file (e.g. it
            // was never created, or is now locked) must never replace the ORIGINAL exception --
            // `throw;` below rethrows exactly what this catch caught, unaffected by the nested
            // try/catch, so the caller's own handling (including cancellation) always sees the
            // real failure.
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
                // Best-effort; the original exception below is what actually matters.
            }

            throw;
        }
    }
}
