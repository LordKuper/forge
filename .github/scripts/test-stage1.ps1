[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

dotnet restore Forge.slnx --locked-mode
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$formatArguments = @('Forge.slnx', '--no-restore')
if ($env:CI -eq 'true') {
    $formatArguments += '--verify-no-changes'
}

dotnet format @formatArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build Forge.slnx --no-restore --configuration Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# The BCL cannot open a Windows directory handle for a durability flush (see ADR 0007); only the composed
# Windows TFM installs that adapter, so it is the one whose test run reflects the real shipped product here.
# tests/portable-tests.ps1 exercises the net10.0 TFM on Linux/macOS, where the portable flush works natively.
dotnet test Forge.slnx --no-build --configuration Release --framework net10.0-windows10.0.19041.0
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$auditJson = dotnet list Forge.slnx package --vulnerable --include-transitive --format json
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$audit = $auditJson | ConvertFrom-Json
$findings = @(
    $audit.projects |
        ForEach-Object { $_.frameworks } |
        ForEach-Object { @($_.topLevelPackages) + @($_.transitivePackages) } |
        Where-Object { $_.vulnerabilities }
)
$blocking = @(
    $findings |
        Where-Object {
            $_.vulnerabilities.severity -contains 'High' -or
            $_.vulnerabilities.severity -contains 'Critical'
        }
)
if ($blocking.Count -gt 0) {
    $blocking | ConvertTo-Json -Depth 8 | Write-Error
    exit 1
}
