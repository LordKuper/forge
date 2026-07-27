[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$publisher = (Resolve-Path "$PSScriptRoot/../scripts/publish-release.ps1").Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "forge-release-tests-$PID"
$passed = 0

function Invoke-TestGit([string]$Repository, [string[]]$Arguments) {
    $savedErrorPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = @(& git -C $Repository @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $savedErrorPreference
    }
    if ($exitCode -ne 0) {
        throw "git $($Arguments -join ' ') failed:`n$($output -join "`n")"
    }
    $output
}

function New-TestRepository {
    $repository = Join-Path $testRoot ([guid]::NewGuid())
    $remote = "$repository.git"
    New-Item -ItemType Directory -Path "$repository/.github/scripts" -Force | Out-Null
    & git init --bare --quiet $remote
    if ($LASTEXITCODE -ne 0) { throw "Cannot initialize test remote." }
    & git init --quiet --initial-branch=main $repository
    if ($LASTEXITCODE -ne 0) { throw "Cannot initialize test repository." }
    Copy-Item $publisher "$repository/.github/scripts/publish-release.ps1"
    Invoke-TestGit $repository @("config", "user.name", "Forge Tests") | Out-Null
    Invoke-TestGit $repository @("config", "user.email", "forge-tests@example.com") | Out-Null
    Invoke-TestGit $repository @("remote", "add", "origin", $remote) | Out-Null
    $repository
}

function Set-Release([string]$Repository, [string]$Version, [string]$Notes = "Test change.") {
    Set-Content "$Repository/VERSION" $Version
    Set-Content "$Repository/CHANGELOG.md" "# Changelog`n`n## v$Version`n`n### Added`n`n- $Notes"
}

function Save-Commit([string]$Repository, [string]$Message) {
    Invoke-TestGit $Repository @("add", ".") | Out-Null
    Invoke-TestGit $Repository @("commit", "--quiet", "-m", $Message) | Out-Null
    (@(Invoke-TestGit $Repository @("rev-parse", "HEAD")))[0]
}

function Invoke-Validation(
    [string]$Repository,
    [string]$Subject,
    [string]$Body = "Test release.",
    [string]$BaseSha = ""
) {
    $previousBase = $env:RELEASE_BASE_SHA
    $previousBody = $env:RELEASE_COMMIT_BODY
    $previousSubject = $env:RELEASE_COMMIT_SUBJECT
    try {
        $env:RELEASE_BASE_SHA = $BaseSha
        $env:RELEASE_COMMIT_BODY = $Body
        $env:RELEASE_COMMIT_SUBJECT = $Subject
        Push-Location $Repository
        try { & $publisher -ValidateOnly | Out-Null } finally { Pop-Location }
    } finally {
        $env:RELEASE_BASE_SHA = $previousBase
        $env:RELEASE_COMMIT_BODY = $previousBody
        $env:RELEASE_COMMIT_SUBJECT = $previousSubject
    }
}

function Assert-Throws([scriptblock]$Action, [string]$Pattern) {
    try {
        & $Action
        throw "Expected failure matching '$Pattern'."
    } catch {
        if ($_.Exception.Message -notlike "*$Pattern*") { throw }
    }
}

function Test-Case([string]$Name, [scriptblock]$Action) {
    & $Action
    $script:passed++
    Write-Host "PASS: $Name"
}

New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    Test-Case "initial release" {
        $repository = New-TestRepository
        $base = Save-Commit $repository "chore: initialize"
        Set-Release $repository "0.1.0"
        Save-Commit $repository "feat: initialize Forge" | Out-Null
        Invoke-Validation $repository "feat: initialize Forge" -BaseSha $base
    }

    Test-Case "malformed changelog" {
        $repository = New-TestRepository
        $base = Save-Commit $repository "chore: initialize"
        Set-Content "$repository/VERSION" "0.1.0"
        Set-Content "$repository/CHANGELOG.md" "# Changelog`n`n## v0.1.0`n`n- Missing category."
        Save-Commit $repository "feat: initialize Forge" | Out-Null
        Assert-Throws { Invoke-Validation $repository "feat: initialize Forge" -BaseSha $base } "categorized user-facing changes"
    }

    $bumps = @(
        @("feat: add behavior", "1.3.0", "Test release."),
        @("fix: repair behavior", "1.2.4", "Test release."),
        @("feat!: replace behavior", "2.0.0", "BREAKING CHANGE: behavior changed.")
    )
    foreach ($bump in $bumps) {
        Test-Case "valid $($bump[0]) bump" {
            $repository = New-TestRepository
            Set-Release $repository "1.2.3"
            $base = Save-Commit $repository "chore: establish version"
            Set-Release $repository $bump[1]
            Save-Commit $repository $bump[0] | Out-Null
            Invoke-Validation $repository $bump[0] $bump[2] $base
        }
    }

    Test-Case "invalid feature bump" {
        $repository = New-TestRepository
        Set-Release $repository "1.2.3"
        $base = Save-Commit $repository "chore: establish version"
        Set-Release $repository "1.2.4"
        Save-Commit $repository "feat: add behavior" | Out-Null
        Assert-Throws { Invoke-Validation $repository "feat: add behavior" -BaseSha $base } "requires a MINOR bump"
    }

    foreach ($kind in @("annotated", "lightweight")) {
        Test-Case "$kind existing tag" {
            $repository = New-TestRepository
            $base = Save-Commit $repository "chore: initialize"
            Set-Release $repository "0.1.0"
            Save-Commit $repository "feat: initialize Forge" | Out-Null
            $tagArgs = if ($kind -eq "annotated") { @("tag", "-a", "v0.1.0", "-m", "Forge v0.1.0") } else { @("tag", "v0.1.0") }
            Invoke-TestGit $repository $tagArgs | Out-Null
            if ($kind -eq "annotated") {
                Invoke-Validation $repository "feat: initialize Forge" -BaseSha $base
            } else {
                Assert-Throws { Invoke-Validation $repository "feat: initialize Forge" -BaseSha $base } "must be an annotated tag"
            }
        }
    }

    Test-Case "out-of-order release" {
        $repository = New-TestRepository
        Save-Commit $repository "chore: initialize" | Out-Null
        Set-Release $repository "1.0.0"
        $earlier = Save-Commit $repository "feat!: establish API"
        Set-Release $repository "1.1.0"
        Save-Commit $repository "feat: extend API" | Out-Null
        Invoke-TestGit $repository @("tag", "-a", "v1.1.0", "-m", "Forge v1.1.0") | Out-Null
        Invoke-TestGit $repository @("checkout", "--quiet", $earlier) | Out-Null
        Invoke-Validation $repository "feat!: establish API" "BREAKING CHANGE: API established."
    }

    Write-Host "$passed release publisher tests passed."
} finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
