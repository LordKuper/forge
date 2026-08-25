using Forge.Desktop.Presentation;

namespace Forge.UnitTests;

/// <summary>
/// Plan 12.6 ("focus-stable after refresh"): <see cref="FocusKeyTracker"/> is the neutral half of the
/// workspace shell's focus-preservation mechanism -- the mapping/lookup logic that needs no live MAUI
/// visual tree. The actual <c>.Focus()</c> calls live in <c>WorkspaceShellPage</c> (Forge.Desktop) and
/// are exercised only by running the app (ADR 0050: no MAUI control can be instantiated headlessly in
/// this suite).
/// </summary>
public sealed class FocusKeyTrackerTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ConsumeReturnsTheMostRecentlyCapturedKey()
    {
        FocusKeyTracker tracker = new();

        tracker.Capture("project:11111111-1111-1111-1111-111111111111");

        Assert.Equal("project:11111111-1111-1111-1111-111111111111", tracker.Consume());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ConsumeClearsTheCapturedKeySoARepeatedConsumeReturnsNull()
    {
        FocusKeyTracker tracker = new();
        tracker.Capture("sidebar-toggle");

        tracker.Consume();

        Assert.Null(tracker.Consume());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ConsumeWithNothingCapturedReturnsNull()
    {
        FocusKeyTracker tracker = new();

        Assert.Null(tracker.Consume());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ASecondCaptureReplacesTheFirstRatherThanQueuing()
    {
        // Only the most recently focused control is a meaningful restoration target -- an earlier
        // capture (e.g. a control the user tabbed through and then left) must not resurface later.
        FocusKeyTracker tracker = new();

        tracker.Capture("sprint:11111111-1111-1111-1111-111111111111");
        tracker.Capture("sprint:22222222-2222-2222-2222-222222222222");

        Assert.Equal("sprint:22222222-2222-2222-2222-222222222222", tracker.Consume());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ClearDiscardsTheCapturedKeyWithoutReturningIt()
    {
        FocusKeyTracker tracker = new();
        tracker.Capture("forge-settings");

        tracker.Clear();

        Assert.Null(tracker.Consume());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CaptureRejectsANullKey()
    {
        FocusKeyTracker tracker = new();

        Assert.Throws<ArgumentNullException>(() => tracker.Capture(null!));
    }
}
