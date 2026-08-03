using Forge.Localization;
using Forge.Updater;

namespace Forge.Desktop;

public partial class App : Microsoft.Maui.Controls.Application
{
    private readonly ILocalizationCatalog catalog;
    private readonly IRestartTokenService restartTokens;
    private readonly IUpdateTargetDetector targetDetector;
    private readonly string? restartToken;

    public App(
        ILocalizationCatalog catalog,
        IRestartTokenService restartTokens,
        IUpdateTargetDetector targetDetector)
    {
        InitializeComponent();
        this.catalog = catalog;
        this.restartTokens = restartTokens;
        this.targetDetector = targetDetector;
        string[] arguments = Environment.GetCommandLineArgs();
        if (arguments.Skip(1).Contains("--self-test", StringComparer.Ordinal))
        {
            Environment.Exit(0);
        }

        if (arguments.Length >= 3 && string.Equals(arguments[1], "--restart-token", StringComparison.Ordinal))
        {
            restartToken = arguments[2];
        }
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        Window window = new(new MainPage(catalog))
        {
            Title = catalog.Resolve(MessageKeys.AppTitle),
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
