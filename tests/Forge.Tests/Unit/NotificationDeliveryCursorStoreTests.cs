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
    /// throw when there was never anything to clean up. Moving onto an existing directory raises
    /// <see cref="UnauthorizedAccessException"/> on Windows but <see cref="IOException"/> (`EISDIR`)
    /// on Linux/macOS, so this asserts against the hosted service's own catch filter -- the set of
    /// types that must propagate correctly on every platform -- rather than one exact type.</summary>
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
            Exception exception = await Assert.ThrowsAnyAsync<Exception>(() => NotificationDeliveryCursorStore
                .SaveAsync(root, "cursor-token", TestContext.Current.CancellationToken));
            Assert.True(
                exception is IOException or UnauthorizedAccessException,
                $"Expected IOException or UnauthorizedAccessException (the hosted service's own catch " +
                    $"filter), got {exception.GetType()}.");

            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
