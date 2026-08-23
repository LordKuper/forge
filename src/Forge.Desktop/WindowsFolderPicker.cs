using Forge.Desktop.Presentation;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Forge.Desktop;

/// <summary>
/// The Windows adapter behind <see cref="IFolderPickerPort"/> (AGENTS.md Portability: this is the
/// only place in the solution allowed to reference <c>Windows.Storage.Pickers</c> directly).
/// <see cref="Forge.Desktop.Presentation"/> depends only on the port; this type is wired up by the
/// composition root (<see cref="App"/>), never referenced from neutral code.
/// </summary>
public sealed class WindowsFolderPicker(Func<Microsoft.Maui.Controls.Window?> activeWindow) : IFolderPickerPort
{
    public async Task<string?> PickFolderAsync(CancellationToken cancellationToken)
    {
        FolderPicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add("*");

        // WinUI 3's picker is a COM object scoped to a top-level HWND; unlike a WinForms/WPF
        // dialog it throws instead of defaulting to the foreground window when uninitialized.
        if (activeWindow() is { Handler.PlatformView: Microsoft.UI.Xaml.Window window })
        {
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        }

        StorageFolder? folder = await picker.PickSingleFolderAsync().AsTask(cancellationToken).ConfigureAwait(false);
        return folder?.Path;
    }
}
