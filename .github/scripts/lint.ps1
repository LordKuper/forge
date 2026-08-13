[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

dotnet restore Forge.slnx --locked-mode
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet format Forge.slnx --no-restore --verify-no-changes
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
