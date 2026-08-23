namespace Forge.Desktop.Presentation;

/// <summary>
/// Serializes the workspace shell's mutations (a second click cannot re-enter one while the first is
/// still in flight -- the same discipline the previous monolithic page applied) while guaranteeing
/// that a sidebar or content render requested *during* an in-flight mutation still happens, once,
/// right after that mutation releases the guard, instead of being silently dropped.
/// </summary>
/// <remarks>
/// PR #98 review round 1 finding 1: <c>WorkspaceViewModel.NavigateAsync</c> raises
/// <c>RouteChanged</c> synchronously from inside every navigation click handler's own mutation
/// guard, and <c>ForgeSettingsViewModel.SaveAsync</c> raises <c>SurfaceTextProvider.Changed</c>
/// synchronously from inside the Forge-settings save handler's own guard. A render triggered by
/// either event therefore always finds the guard already held by the very click that triggered it,
/// so a naive "guard blocks everything" implementation drops the render entirely -- clicking a
/// sidebar item updates the route but the content pane never rebuilds, and a UI-language save never
/// refreshes the sidebar. This type keeps the original re-entrancy guard for mutations (its actual
/// purpose) but tracks a *pending* render request separately, and flushes it -- fully, one render at
/// a time -- the moment the guard is released, so the request is honored instead of lost.
/// </remarks>
public sealed class ShellRenderGate(Func<Task> renderSidebarAsync, Func<Task> renderContentAsync)
{
    private readonly Func<Task> renderSidebarAsync =
        renderSidebarAsync ?? throw new ArgumentNullException(nameof(renderSidebarAsync));
    private readonly Func<Task> renderContentAsync =
        renderContentAsync ?? throw new ArgumentNullException(nameof(renderContentAsync));
    private bool busy;
    private bool sidebarRenderPending;
    private bool contentRenderPending;

    /// <summary>Runs <paramref name="action"/> unless a mutation or render is already in flight, in
    /// which case this call is a no-op -- a genuine double click, matching the previous guard's own
    /// behavior. Used for every shell-driven mutation and, internally, for every render.</summary>
    public async Task RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (busy)
        {
            return;
        }

        busy = true;
        try
        {
            await action().ConfigureAwait(true);
        }
        finally
        {
            busy = false;
        }

        // The action just released above may have requested a sidebar/content render while it held
        // the guard (a route or language change raised from inside it) -- flush those now, one at a
        // time so a render itself never overlaps another, exactly the guarantee `busy` always gave
        // mutations.
        if (sidebarRenderPending)
        {
            sidebarRenderPending = false;
            await RunAsync(renderSidebarAsync).ConfigureAwait(true);
        }

        if (contentRenderPending)
        {
            contentRenderPending = false;
            await RunAsync(renderContentAsync).ConfigureAwait(true);
        }
    }

    /// <summary>Requests a sidebar re-render. Runs immediately when the gate is idle; otherwise
    /// records the request so <see cref="RunAsync"/> honors it right after the in-flight work
    /// releases the guard, instead of dropping it.</summary>
    public void RequestSidebarRender()
    {
        if (busy)
        {
            sidebarRenderPending = true;
            return;
        }

        _ = RunAsync(renderSidebarAsync);
    }

    /// <summary>Same guarantee as <see cref="RequestSidebarRender"/>, for the content pane. This is
    /// what makes a route change raised mid-mutation (every sidebar navigation click) always produce
    /// a real re-render once that mutation completes.</summary>
    public void RequestContentRender()
    {
        if (busy)
        {
            contentRenderPending = true;
            return;
        }

        _ = RunAsync(renderContentAsync);
    }
}
