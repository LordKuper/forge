[CmdletBinding()]
param()

# Proves the ADR 0007 neutral projects build and test on this OS via the portable net10.0 TFM. Windows-only
# projects (Forge.Cli.Windows, Forge.Desktop, Forge.Runtime.Windows, Forge.Updater.Windows) are intentionally
# excluded; they build only on Windows.

$ErrorActionPreference = 'Stop'

$neutralProjects = @(
    'src/Forge.Runtime/Forge.Runtime.csproj',
    'src/Forge.Updater/Forge.Updater.csproj',
    'src/Forge.Cli/Forge.Cli.csproj',
    'src/Forge.Desktop.Presentation/Forge.Desktop.Presentation.csproj'
)

foreach ($project in $neutralProjects) {
    dotnet restore $project --locked-mode
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    dotnet build $project --no-restore --configuration Release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Forge.Tests multi-targets net10.0 and net10.0-windows; NuGet's --locked-mode target-framework check does not
# honor a command-line TargetFrameworks override, so this restore covers the whole matrix (harmless on any OS,
# since both TFMs' dependencies are ordinary NuGet packages) and only the net10.0 run below is OS-portable.
dotnet restore tests/Forge.Tests/Forge.Tests.csproj --locked-mode
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet test tests/Forge.Tests/Forge.Tests.csproj --no-restore --configuration Release --framework net10.0
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
