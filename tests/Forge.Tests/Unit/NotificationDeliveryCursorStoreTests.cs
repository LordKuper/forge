using Forge.Application;

namespace Forge.UnitTests;

/// <summary>Round 3 review of PR #64 found <see cref="NotificationDeliveryCursorStore.SaveAsync"/>'s
/// own `.tmp`-cleanup path -- rewritten by two prior review rounds -- had zero tests, against
/// AGENTS.md's regression-test rule.</summary>
public sealed class NotificationDeliveryCursorStoreTests
{
    /// <summary>Forces the final `File.Move` to fail (a directory, not a file, already occupies
    /// the destination path) after the temp file has genuinely been written, so this proves the
    /// cleanup actually runs against a real leftover file -- not merely that the method doesn't
    /// throw when there was never anything to clean up. On Windows, moving onto an existing
    /// directory raises <see cref="UnauthorizedAccessException"/> (confirmed by running this test),
    /// not <see cref="IOException"/> -- both are in the hosted service's own catch filter.</summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task SaveAsyncCleansUpItsTempFileWhenTheFinalMoveFailsAndPropagatesTheOriginalException()
    {
        string root = Path.Combine(Path.GetTempPath(), $"forge-notification-cursor-{Guid.NewGuid():N}");
        string cursorPath = NotificationDeliveryCursorStore.CursorFilePath(root);
        string directory = Path.GetDirectoryName(cursorPath)!;
        Directory.CreateDirectory(cursorPath);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => NotificationDeliveryCursorStore.SaveAsync(
                root, "cursor-token", TestContext.Current.CancellationToken));

            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
