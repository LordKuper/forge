[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

./.github/scripts/assert-lock-files-current.ps1

dotnet restore Forge.slnx --locked-mode
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$formatArguments = @('Forge.slnx', '--no-restore')
if ($env:CI -eq 'true') {
    $formatArguments += '--verify-no-changes'
}

dotnet format @formatArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

./.github/scripts/assert-no-vulnerable-packages.ps1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
