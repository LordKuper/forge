using Forge.Application;
using Forge.Configuration;
using Forge.Desktop.Presentation;
using Forge.Localization;
using Forge.Providers;
using Forge.Updater;

namespace Forge.Desktop;

public partial class App : Microsoft.Maui.Controls.Application
{
    private readonly ILocalizationCatalog catalog;
    private readonly ForgeApplication application;
    private readonly ProjectCatalogStore projectCatalog;
    private readonly ProviderCatalog providerCatalog;
    private readonly Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations;
    /// <summary>Process-lifetime shared instance (plan 12.6's Host-connectivity status-row
    /// indicator): the same monitor is threaded into <see cref="HostMutationsFactory.CreateResolver"/>
    /// (so every real mutation attempt reports into it) and into <see cref="WorkspaceShellPage"/>'s
    /// sidebar (so the status row reads its last-observed reading) -- see
    /// <see cref="Forge.Application.IHostConnectivityMonitor"/>'s own remarks for why this is never a
    /// fresh probe.</summary>
    private readonly IHostConnectivityMonitor connectivityMonitor = new HostConnectivityMonitor();
    private readonly IRestartTokenService restartTokens;
    private readonly IUpdateTargetDetector targetDetector;
    private readonly string? restartToken;

    public App(
        ILocalizationCatalog catalog,
        ForgeApplication application,
        ProjectRootResolver rootResolver,
        IConfigurationRegistry registry,
        IEnvironmentPaths paths,
        ProjectCatalogStore projectCatalog,
        ProviderCatalog providerCatalog,
        IRestartTokenService restartTokens,
        IUpdateTargetDetector targetDetector)
    {
        InitializeComponent();
        this.catalog = catalog;
        this.application = application;
        this.projectCatalog = projectCatalog;
        this.providerCatalog = providerCatalog;
        resolveMutations = HostMutationsFactory.CreateResolver(
            rootResolver,
            registry,
            paths,
            application,
            typeof(App).Assembly.GetName().Version!.ToString(3),
            connectivityMonitor);
        this.restartTokens = restartTokens;
        this.targetDetector = targetDetector;
        StartupArguments arguments = StartupArguments.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
        if (arguments.IsSelfTest)
        {
            Environment.Exit(0);
        }

        restartToken = arguments.RestartToken;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // The startup sequence resolves the UI language before any text is rendered.
        StartupStatus startup = application
            .GetStartupStatusAsync(null, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        SurfaceTextProvider text = new(catalog, startup.Language.Ui);
        // WindowsFolderPicker needs the native HWND of the window it pops over, which does not
        // exist until the Window below is constructed -- the closure resolves it lazily, by which
        // time PickFolderAsync's own (always later, always async) call sees the real window.
        Window? window = null;
        WindowsFolderPicker folderPicker = new(() => window);
        WorkspaceShellPage page =
            new(text, application, projectCatalog, providerCatalog, resolveMutations, folderPicker, connectivityMonitor);
        window = new(page) { Title = text.Resolve(MessageKeys.AppTitle) };

        if (restartToken is not null)
        {
            UpdateDiagnostic handshake = new StartupHandshake(restartTokens).Confirm(
                restartToken,
                new(
                    SemanticVersion.Parse(typeof(App).Assembly.GetName().Version!.ToString(3)),
                    targetDetector.Detect(),
                    UpdateSurface.Desktop));
            if (handshake.Code != UpdateDiagnosticCode.None)
            {
                throw new InvalidOperationException(handshake.Detail);
            }
        }

        return window;
    }
}
