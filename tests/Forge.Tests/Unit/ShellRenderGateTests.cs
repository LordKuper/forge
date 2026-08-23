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
