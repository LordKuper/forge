using Forge.Localization;
using Forge.Updater;

namespace Forge.Desktop;

public partial class App : Microsoft.Maui.Controls.Application
{
    private readonly ILocalizationCatalog catalog;

    public App(
        ILocalizationCatalog catalog,
        IRestartTokenService restartTokens,
        IUpdateTargetDetector targetDetector)
    {
        InitializeComponent();
        this.catalog = catalog;
        string[] arguments = Environment.GetCommandLineArgs();
        if (arguments.Skip(1).Contains("--self-test", StringComparer.Ordinal))
        {
            Environment.Exit(0);
        }

        if (arguments.Length >= 3 && string.Equals(arguments[1], "--restart-token", StringComparison.Ordinal))
        {
            UpdateDiagnostic handshake = new StartupHandshake(restartTokens).Confirm(
                arguments[2],
                new(
                    SemanticVersion.Parse(typeof(App).Assembly.GetName().Version!.ToString(3)),
                    targetDetector.Detect(),
                    UpdateSurface.Desktop));
            if (handshake.Code != UpdateDiagnosticCode.None)
            {
                throw new InvalidOperationException(handshake.Detail);
            }
        }
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new MainPage(catalog))
        {
            Title = catalog.Resolve(MessageKeys.AppTitle),
        };
}
