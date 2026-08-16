using Forge.Application;
using Forge.Configuration;
using Forge.Localization;
using Forge.Updater;

namespace Forge.Desktop;

public partial class App : Microsoft.Maui.Controls.Application
{
    private readonly ILocalizationCatalog catalog;
    private readonly ForgeApplication application;
    private readonly Func<string?, CancellationToken, Task<IForgeMutations>> resolveMutations;
    private readonly IRestartTokenService restartTokens;
    private readonly IUpdateTargetDetector targetDetector;
    private readonly string? restartToken;

    public App(
        ILocalizationCatalog catalog,
        ForgeApplication application,
        ProjectRootResolver rootResolver,
        IConfigurationRegistry registry,
        IEnvironmentPaths paths,
        IRestartTokenService restartTokens,
        IUpdateTargetDetector targetDetector)
    {
        InitializeComponent();
        this.catalog = catalog;
        this.application = application;
        resolveMutations = HostMutationsFactory.CreateResolver(
            rootResolver,
            registry,
            paths,
            application,
            typeof(App).Assembly.GetName().Version!.ToString(3));
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
        SurfaceText text = SurfaceText.For(catalog, startup.Language.Ui);
        Window window = new(new MainPage(text, application, resolveMutations))
        {
            Title = text.Resolve(MessageKeys.AppTitle),
        };

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
