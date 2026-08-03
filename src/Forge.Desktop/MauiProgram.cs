using Forge.Bootstrap;
using Forge.Updater.Windows;
#if DEBUG
using Microsoft.Extensions.Logging;
#endif

namespace Forge.Desktop;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        MauiAppBuilder builder = MauiApp.CreateBuilder()
            .UseMauiApp<App>();
        builder.Services.AddForgeCore();
        builder.Services.AddForgeWindowsUpdater();
#if DEBUG
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}
