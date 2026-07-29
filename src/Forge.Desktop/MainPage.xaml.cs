using Forge.Localization;

namespace Forge.Desktop;

public partial class MainPage : ContentPage
{
    public MainPage(ILocalizationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        InitializeComponent();
        TitleLabel.Text = catalog.Resolve(MessageKeys.AppTitle);
        StatusLabel.Text = catalog.Resolve(MessageKeys.StatusReady);
    }
}
