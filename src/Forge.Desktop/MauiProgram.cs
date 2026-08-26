using Forge.Bootstrap;
using Forge.Providers.Claude;
using Forge.Providers.Codex;
using Forge.Runtime.Windows;
using Forge.Updater.Windows;
#if DEBUG
using Microsoft.Extensions.Logging;
#endif

namespace Forge.Desktop;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        ForgeRuntimeWindowsAdapter.Install();
        MauiAppBuilder builder = MauiApp.CreateBuilder()
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("Inter-Regular.ttf", "InterRegular");
                fonts.AddFont("Inter-Medium.ttf", "InterMedium");
                fonts.AddFont("Phosphor.ttf", "Phosphor");
            });
        builder.Services.AddForgeCore();
        builder.Services.AddForgeRuntimeWindowsProcessContainment();
        builder.Services.AddForgeWindowsUpdater();
        builder.Services.AddCodexProvider();
        builder.Services.AddClaudeProvider();
#if DEBUG
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}
