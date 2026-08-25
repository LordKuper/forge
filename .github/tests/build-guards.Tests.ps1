[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$guard = Join-Path $repositoryRoot '.github/scripts/assert-lock-files-current.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "forge-build-guard-tests-$PID"
$passed = 0

function Test-Case([string]$Name, [scriptblock]$Action) {
    & $Action
    $script:passed++
    Write-Host "PASS: $Name"
}

function Assert-Throws([scriptblock]$Action, [string]$Pattern) {
    try {
        & $Action
    } catch {
        if ($_.Exception.Message -notlike "*$Pattern*") { throw }
        return
    }
    throw "Expected failure matching '$Pattern'."
}

New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    Test-Case 'Repository lock files use the current Forge version' {
        & $guard
    }

    Test-Case 'Windows Runtime keeps the lock graph portable' {
        $project = Get-Content -LiteralPath (
            Join-Path $repositoryRoot 'src/Forge.Runtime.Windows/Forge.Runtime.Windows.csproj'
        ) -Raw
        if ($project -notmatch '<RestoreEnablePackagePruning>false</RestoreEnablePackagePruning>') {
            throw 'Forge.Runtime.Windows must disable package pruning until dotnet/sdk#52557 is fixed.'
        }

        $lock = ConvertFrom-Json -InputObject (Get-Content -LiteralPath (
            Join-Path $repositoryRoot 'src/Forge.Runtime.Windows/packages.lock.json'
        ) -Raw) -AsHashtable
        $target = @($lock['dependencies'].Values)[0]
        $eventLogLogger = $target['Microsoft.Extensions.Logging.EventLog']
        $eventLogVersion = $eventLogLogger['dependencies']['System.Diagnostics.EventLog']
        if (-not $eventLogVersion -or
            $target['System.Diagnostics.EventLog']['resolved'] -ne $eventLogVersion) {
            throw 'Forge.Runtime.Windows lock graph must retain System.Diagnostics.EventLog.'
        }
    }

    Test-Case 'Stale internal pins are rejected' {
        $repository = Join-Path $testRoot 'stale-pin'
        New-Item -ItemType Directory -Path "$repository/.github/scripts" -Force | Out-Null
        Copy-Item -LiteralPath $guard -Destination "$repository/.github/scripts/assert-lock-files-current.ps1"
        Set-Content -LiteralPath "$repository/VERSION" -Value '0.78.0'
        Set-Content -LiteralPath "$repository/packages.lock.json" -Value @'
{
  "version": 2,
  "dependencies": {
    "net10.0": {
      "forge.runtime": {
        "type": "Project",
        "dependencies": {
          "Forge.Host.Client": "[0.77.0, )"
        }
      }
    }
  }
}
'@
        & git init --quiet --initial-branch=main $repository
        if ($LASTEXITCODE -ne 0) { throw 'Cannot initialize stale-pin fixture.' }
        & git -C $repository add VERSION packages.lock.json .github/scripts/assert-lock-files-current.ps1
        if ($LASTEXITCODE -ne 0) { throw 'Cannot stage stale-pin fixture.' }
        Assert-Throws { & "$repository/.github/scripts/assert-lock-files-current.ps1" } 'stale internal Forge version pins'
    }

    Write-Host "$passed build guard tests passed."
} finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
