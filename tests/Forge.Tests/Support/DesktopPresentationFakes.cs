using Forge.Desktop.Presentation;

namespace Forge.Tests.Support;

/// <summary>Stands in for the real Windows folder picker (ADR 0007: neutral tests never touch an OS
/// adapter). <see cref="NextResult"/> is the path returned by the next <see cref="PickFolderAsync"/>
/// call, or <see langword="null"/> to simulate the user dismissing the picker.</summary>
internal sealed class FakeFolderPicker(string? nextResult = null) : IFolderPickerPort
{
    public string? NextResult { get; set; } = nextResult;

    public int Calls { get; private set; }

    public Task<string?> PickFolderAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(NextResult);
    }
}
