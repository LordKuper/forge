using Forge.Desktop.Presentation;

namespace Forge.UnitTests;

/// <summary>
/// PR #98 review round 1 finding 1: the workspace shell's mutation guard silently swallowed every
/// re-render a route or language change requested while it was held, because
/// <c>WorkspaceViewModel.NavigateAsync</c>/<c>SurfaceTextProvider.Changed</c> both raise their event
/// synchronously from inside the very click handler that owns the guard. This is the regression test
/// for that bug: it reproduces the exact sequence (a render requested mid-mutation) against
/// <see cref="ShellRenderGate"/> -- the neutral type <c>WorkspaceShellPage</c> now delegates its own
/// guard to -- without needing a live MAUI page (no MAUI control can be instantiated headlessly in
/// this suite; see ADR 0050).
/// </summary>
public sealed class ShellRenderGateTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AContentRenderRequestedDuringAnInFlightMutationStillRendersOnceAfterwards()
    {
        int contentRenders = 0;
        TaskCompletionSource mutationReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ShellRenderGate gate = new(
            renderSidebarAsync: () => Task.CompletedTask,
            renderContentAsync: () =>
            {
                contentRenders++;
                return Task.CompletedTask;
            });

        Task mutation = gate.RunAsync(async () =>
        {
            // The exact sequence a sidebar navigation click produces: NavigateAsync's own
            // RouteChanged fires synchronously while this mutation still holds the guard.
            gate.RequestContentRender();
            Assert.Equal(0, contentRenders);
            await mutationReleased.Task;
        });

        mutationReleased.SetResult();
        await mutation;

        Assert.Equal(1, contentRenders);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ASidebarRenderRequestedDuringAnInFlightMutationStillRendersOnceAfterwards()
    {
        int sidebarRenders = 0;
        TaskCompletionSource mutationReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ShellRenderGate gate = new(
            renderSidebarAsync: () =>
            {
                sidebarRenders++;
                return Task.CompletedTask;
            },
            renderContentAsync: () => Task.CompletedTask);

        // The exact sequence a UI-language save produces: SurfaceTextProvider.Changed fires
        // synchronously while the save button's own mutation still holds the guard.
        Task mutation = gate.RunAsync(async () =>
        {
            gate.RequestSidebarRender();
            Assert.Equal(0, sidebarRenders);
            await mutationReleased.Task;
        });

        mutationReleased.SetResult();
        await mutation;

        Assert.Equal(1, sidebarRenders);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task BothRendersRequestedDuringAnInFlightMutationBothRenderAfterwards()
    {
        int sidebarRenders = 0;
        int contentRenders = 0;
        TaskCompletionSource mutationReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ShellRenderGate gate = new(
            renderSidebarAsync: () =>
            {
                sidebarRenders++;
                return Task.CompletedTask;
            },
            renderContentAsync: () =>
            {
                contentRenders++;
                return Task.CompletedTask;
            });

        Task mutation = gate.RunAsync(async () =>
        {
            gate.RequestSidebarRender();
            gate.RequestContentRender();
            await mutationReleased.Task;
        });

        mutationReleased.SetResult();
        await mutation;

        Assert.Equal(1, sidebarRenders);
        Assert.Equal(1, contentRenders);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ARenderRequestedWhileIdleRunsImmediately()
    {
        int contentRenders = 0;
        ShellRenderGate gate = new(
            renderSidebarAsync: () => Task.CompletedTask,
            renderContentAsync: () =>
            {
                contentRenders++;
                return Task.CompletedTask;
            });

        gate.RequestContentRender();

        Assert.Equal(1, contentRenders);
    }

    /// <summary>
    /// PR #99 review finding 1: the sprint workspace's periodic timeline poll used to route its whole
    /// fetch-then-render step through <see cref="ShellRenderGate.RunAsync"/> -- the same guard user
    /// clicks use -- taking <c>busy</c> for the duration of an unattended Host round-trip. A user
    /// click landing in that window was silently dropped before its own mutation ever started (not
    /// merely deferred), reproducing PR #98's "navigation silently does nothing" bug via a different
    /// path. <see cref="ShellRenderGate.RequestRender"/> is the fix: it never sets <c>busy</c> itself,
    /// so a poll using it for its render step can never block, drop, or delay a concurrent mutation.
    /// This test reproduces the exact interleaving: a render requested (simulating the poll's
    /// already-completed fetch) while a real mutation is in flight must not prevent a second,
    /// genuinely user-driven mutation attempt from being correctly evaluated against the guard (still
    /// rejected as a double click, exactly as without the pending render), and the deferred render
    /// must still apply exactly once after the mutation releases the guard.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARenderRequestDeferredDuringAnInFlightMutationNeverBlocksOrDropsAConcurrentMutation()
    {
        int renders = 0;
        TaskCompletionSource mutationReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ShellRenderGate gate = new(
            renderSidebarAsync: () => Task.CompletedTask,
            renderContentAsync: () => Task.CompletedTask);

        Task mutation = gate.RunAsync(async () =>
        {
            // Simulates the timeline poll's fetch having already completed while this user-driven
            // mutation still holds the guard -- the render must be deferred, not run immediately.
            gate.RequestRender(() => renders++);
            Assert.Equal(0, renders);
            await mutationReleased.Task;
        });

        // A second, genuinely user-driven mutation attempted while the first is still in flight must
        // still be rejected as a double click -- proving RequestRender never itself takes or releases
        // the guard (the exact bug: a poll's own fetch previously held `busy`, so a concurrent user
        // click's RunAsync saw `busy` and was silently dropped before it could even start).
        int secondMutationCalls = 0;
        await gate.RunAsync(() =>
        {
            secondMutationCalls++;
            return Task.CompletedTask;
        });
        Assert.Equal(0, secondMutationCalls);

        mutationReleased.SetResult();
        await mutation;

        Assert.Equal(1, renders);
    }

    /// <summary>
    /// PR #105 round-2 review finding 4: <c>WorkspaceShellPage.SprintWorkspace.cs</c>'s
    /// <c>PollTimelineAsync</c> already used <see cref="ShellRenderGate.RequestRender"/> for its own
    /// timeline refresh; a second, unrelated caller (the scroll-position-save failure notice) briefly
    /// used that same method too, and since <see cref="ShellRenderGate.RequestRender"/> deliberately
    /// coalesces same-caller repeats into one last-request-wins slot, two DIFFERENT callers sharing it
    /// evicted each other -- a routine timeline poll tick could silently drop a pending scroll-notice
    /// render, or vice versa. The actual fix moved the scroll-notice off
    /// <see cref="ShellRenderGate.RequestRender"/> onto <see cref="ShellRenderGate.RequestSidebarRender"/>
    /// -- its own independent pending slot (see
    /// <see cref="ASidebarRenderRequestedDuringAnInFlightMutationStillRendersOnceAfterwards"/>) -- so
    /// this reproduces exactly that combination: a content render queued through
    /// <see cref="ShellRenderGate.RequestRender"/> (the timeline poll) and a sidebar render queued
    /// through <see cref="ShellRenderGate.RequestSidebarRender"/> (the notice) during the same
    /// in-flight mutation must both survive, neither one silently discarding the other.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ATimelineRenderRequestAndASidebarNoticeRenderRequestBothSurviveTogether()
    {
        int timelineRenders = 0;
        int sidebarRenders = 0;
        TaskCompletionSource mutationReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ShellRenderGate gate = new(
            renderSidebarAsync: () =>
            {
                sidebarRenders++;
                return Task.CompletedTask;
            },
            renderContentAsync: () => Task.CompletedTask);

        Task mutation = gate.RunAsync(async () =>
        {
            // PollTimelineAsync's own render step, arriving while this unrelated mutation still holds
            // the guard.
            gate.RequestRender(() => timelineRenders++);
            // The scroll-position-save failure notice, arriving in the same window.
            gate.RequestSidebarRender();
            Assert.Equal(0, timelineRenders);
            Assert.Equal(0, sidebarRenders);
            await mutationReleased.Task;
        });

        mutationReleased.SetResult();
        await mutation;

        Assert.Equal(1, timelineRenders);
        Assert.Equal(1, sidebarRenders);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ARequestRenderWhileIdleRunsImmediately()
    {
        int renders = 0;
        ShellRenderGate gate = new(
            renderSidebarAsync: () => Task.CompletedTask,
            renderContentAsync: () => Task.CompletedTask);

        gate.RequestRender(() => renders++);

        Assert.Equal(1, renders);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ASecondMutationCannotReenterWhileTheFirstIsStillInFlight()
    {
        int calls = 0;
        TaskCompletionSource firstMutationReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ShellRenderGate gate = new(
            renderSidebarAsync: () => Task.CompletedTask,
            renderContentAsync: () => Task.CompletedTask);

        Task first = gate.RunAsync(async () =>
        {
            calls++;
            await firstMutationReleased.Task;
        });
        // A second click while the first mutation is still in flight must be a no-op, matching the
        // previous guard's own double-click protection.
        await gate.RunAsync(() =>
        {
            calls++;
            return Task.CompletedTask;
        });
        Assert.Equal(1, calls);

        firstMutationReleased.SetResult();
        await first;
        Assert.Equal(1, calls);
    }
}
