using Forge.Localization;

namespace Forge.Desktop;

public partial class App : Microsoft.Maui.Controls.Application
{
    private readonly ILocalizationCatalog catalog;

    public App(ILocalizationCatalog catalog)
    {
        InitializeComponent();
        this.catalog = catalog;
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new MainPage(catalog))
        {
            Title = catalog.Resolve(MessageKeys.AppTitle),
        };
}
