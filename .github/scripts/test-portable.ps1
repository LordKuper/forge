[CmdletBinding()]
param()

# Proves the ADR 0007 neutral projects build and test on this OS via the portable net10.0 TFM. Windows-only
# projects (Forge.Cli.Windows, Forge.Desktop, Forge.Host.Windows, Forge.Providers.Codex.Windows,
# Forge.Providers.Claude.Windows, Forge.Runtime.Windows, Forge.Updater.Windows, Forge.ProcessContainmentProbe)
# are intentionally excluded; they build only on Windows.

$ErrorActionPreference = 'Stop'

# Forge.Cli, Forge.Desktop.Presentation, and Forge.Host.TestHost together transitively reference every other
# neutral project (Forge.Runtime, Forge.Updater, Forge.Host.Client, Forge.Host.Runtime), so building these
# standalone is enough to prove the whole neutral graph restores and builds independently of the test project;
# Forge.Tests below then builds (and tests) that same graph again regardless.
$leafProjects = @(
    'src/Forge.Cli/Forge.Cli.csproj',
    'src/Forge.Desktop.Presentation/Forge.Desktop.Presentation.csproj',
    'src/Forge.Host.TestHost/Forge.Host.TestHost.csproj',
    # Neutral (references only Forge.Host.Client); its own same-user isolation test is Windows-only
    # (test-same-user-isolation.ps1, requiring OS user creation), but the project itself must still
    # build on every OS per this script's own rule.
    'tests/Forge.PipeIsolationProbe/Forge.PipeIsolationProbe.csproj',
    'tests/Forge.MutexIsolationProbe/Forge.MutexIsolationProbe.csproj'
    # Forge.ProcessContainmentProbe is deliberately NOT here: unlike its two siblings above, it calls
    # the real WindowsJobObjectProcessContainment directly and targets net10.0-windows10.0.19041.0
    # only (no portable TFM, no POSIX branch), so it belongs with the Windows-only exclusion list
    # above, not this neutral leaf-project list. Forge.Tests.csproj's own build-order-only reference
    # to it (ReferenceOutputAssembly="false") still makes its restore/build get exercised on every OS
    # via the "Forge.Tests multi-targets..." step below.
)

foreach ($project in $leafProjects) {
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
