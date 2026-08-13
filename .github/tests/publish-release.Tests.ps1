[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$publisher = (Resolve-Path "$PSScriptRoot/../scripts/publish-release.ps1").Path
$bundlePublisher = (Resolve-Path "$PSScriptRoot/../../build/Publish-WindowsBundle.ps1").Path
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

function Invoke-Publication(
    [string]$Repository,
    [string]$Subject,
    [bool]$ReleaseExists = $false,
    [string[]]$ReleaseAssets = @()
) {
    $log = Join-Path $Repository "gh-stub.log"
    function global:gh {
        Add-Content $env:GH_STUB_LOG ($args -join " ")
        if ($args[0] -eq "release" -and $args[1] -eq "view") {
            $global:LASTEXITCODE = if ($ReleaseExists) { 0 } else { 1 }
        } else {
            $global:LASTEXITCODE = 0
        }
    }

    $env:GH_STUB_LOG = $log
    $env:GH_TOKEN = "test-token"
    $env:GITHUB_REPOSITORY = "owner/repo"
    $env:RELEASE_BASE_SHA = ""
    $env:RELEASE_COMMIT_BODY = "Test release."
    $env:RELEASE_COMMIT_SUBJECT = $Subject
    $env:RELEASE_ASSETS = $ReleaseAssets -join ';'
    $env:RELEASE_REQUIRE_ASSETS = if ($ReleaseAssets.Count -gt 0) { "true" } else { "false" }
    try {
        Push-Location $Repository
        try { & $publisher | Out-Null } finally { Pop-Location }
    } finally {
        Remove-Item Function:\gh -Force -ErrorAction SilentlyContinue
        $env:GH_STUB_LOG = $null
        $env:GH_TOKEN = $null
        $env:GITHUB_REPOSITORY = $null
        $env:RELEASE_BASE_SHA = $null
        $env:RELEASE_COMMIT_BODY = $null
        $env:RELEASE_COMMIT_SUBJECT = $null
        $env:RELEASE_ASSETS = $null
        $env:RELEASE_REQUIRE_ASSETS = $null
    }
    $log
}

function Assert-Throws([scriptblock]$Action, [string]$Pattern) {
    $threw = $false
    try {
        & $Action
    } catch {
        $threw = $true
        if ($_.Exception.Message -notlike "*$Pattern*") { throw }
    }
    if (-not $threw) { throw "Expected failure matching '$Pattern'." }
}

function Test-Case([string]$Name, [scriptblock]$Action) {
    & $Action
    $script:passed++
    Write-Host "PASS: $Name"
}

New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    Test-Case "Windows bundle disables unavailable ReadyToRun" {
        $publishLines = @(Get-Content $bundlePublisher | Where-Object { $_ -match '^\s*dotnet publish ' })
        if ($publishLines.Count -ne 2) {
            throw "Expected two Windows bundle publish commands."
        }
        if (@($publishLines | Where-Object { $_ -notmatch '--property:PublishReadyToRun=false' }).Count -ne 0) {
            throw "Every Windows bundle publish command must disable ReadyToRun."
        }
    }

    Test-Case "Windows bundle restore is conditional on -SkipRestore" {
        $content = Get-Content $bundlePublisher -Raw
        if ($content -notmatch '(?ms)if\s*\(\s*-not\s*\$SkipRestore\s*\)\s*\{[^}]*dotnet restore') {
            throw "Publish-WindowsBundle.ps1 must restore by default and only skip it when -SkipRestore is passed."
        }
    }

    Test-Case "Release workflow restores once before both -SkipRestore publishes" {
        $releaseWorkflow = (Resolve-Path "$PSScriptRoot/../workflows/release.yml").Path
        $lines = Get-Content $releaseWorkflow
        $restoreLines = @($lines | Where-Object { $_ -match 'dotnet restore Forge\.slnx --locked-mode' })
        if ($restoreLines.Count -ne 1) {
            throw "Expected exactly one solution restore in release.yml, found $($restoreLines.Count)."
        }
        $skipRestoreLines = @($lines | Where-Object { $_ -match 'Publish-WindowsBundle\.ps1.*-SkipRestore' })
        if ($skipRestoreLines.Count -ne 2) {
            throw "Expected two -SkipRestore Windows bundle publish invocations in release.yml, found $($skipRestoreLines.Count)."
        }
        $restoreIndex = [array]::IndexOf($lines, $restoreLines[0])
        $skipRestoreIndexes = @($skipRestoreLines | ForEach-Object { [array]::IndexOf($lines, $_) })
        if (@($skipRestoreIndexes | Where-Object { $_ -lt $restoreIndex }).Count -gt 0) {
            throw "The solution restore must precede both -SkipRestore Windows bundle publish invocations."
        }
    }

    Test-Case "Release workflow checks the exit code of every bundle publish" {
        $releaseWorkflow = (Resolve-Path "$PSScriptRoot/../workflows/release.yml").Path
        $lines = Get-Content $releaseWorkflow
        $skipRestoreIndexes = @(0..($lines.Count - 1) | Where-Object { $lines[$_] -match 'Publish-WindowsBundle\.ps1.*-SkipRestore' })
        if ($skipRestoreIndexes.Count -ne 2) {
            throw "Expected two -SkipRestore Windows bundle publish invocations in release.yml, found $($skipRestoreIndexes.Count)."
        }
        foreach ($index in $skipRestoreIndexes) {
            if ($lines[$index + 1] -notmatch '\$LASTEXITCODE -ne 0.*exit \$LASTEXITCODE') {
                throw "Line $($index + 1) (`"$($lines[$index].Trim())`") must be immediately followed by an exit-code check, otherwise a failing publish is silently ignored."
            }
        }
    }

    Test-Case "initial release" {
        $repository = New-TestRepository
        $base = Save-Commit $repository "chore: initialize"
        Set-Release $repository "0.1.0"
        Save-Commit $repository "feat(UI): initialize Forge" | Out-Null
        Invoke-Validation $repository "feat(UI): initialize Forge" -BaseSha $base
    }

    Test-Case "malformed changelog" {
        $repository = New-TestRepository
        $base = Save-Commit $repository "chore: initialize"
        Set-Content "$repository/VERSION" "0.1.0"
        Set-Content "$repository/CHANGELOG.md" "# Changelog`n`n## v0.1.0`n`n- Missing category."
        Save-Commit $repository "feat: initialize Forge" | Out-Null
        Assert-Throws { Invoke-Validation $repository "feat: initialize Forge" -BaseSha $base } "allowed category"
    }

    Test-Case "unsupported changelog category" {
        $repository = New-TestRepository
        $base = Save-Commit $repository "chore: initialize"
        Set-Release $repository "0.1.0"
        Add-Content "$repository/CHANGELOG.md" "`n`n### Internal`n`n- Hidden change."
        Save-Commit $repository "feat: initialize Forge" | Out-Null
        Assert-Throws { Invoke-Validation $repository "feat: initialize Forge" -BaseSha $base } "Unsupported changelog category"
    }

    Test-Case "newest-first changelog" {
        $repository = New-TestRepository
        Set-Release $repository "0.1.0"
        $base = Save-Commit $repository "chore: establish version"
        Add-Content "$repository/CHANGELOG.md" "`n`n## v0.2.0`n`n### Added`n`n- Later change."
        Set-Content "$repository/VERSION" "0.2.0"
        Save-Commit $repository "feat: add behavior" | Out-Null
        Assert-Throws { Invoke-Validation $repository "feat: add behavior" -BaseSha $base } "must be the first release"
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

    Test-Case "unreleased base version correction" {
        $repository = New-TestRepository
        Set-Release $repository "0.5.1"
        Save-Commit $repository "fix: establish released version" | Out-Null
        Invoke-TestGit $repository @("tag", "-a", "v0.5.1", "-m", "Forge v0.5.1") | Out-Null
        Set-Release $repository "1.0.0"
        $base = Save-Commit $repository "feat!: premature major version"
        Set-Release $repository "0.6.0"
        Save-Commit $repository "feat: restore prerelease version" | Out-Null
        Invoke-Validation $repository "feat: restore prerelease version" -BaseSha $base
    }

    Test-Case "invalid unreleased base correction" {
        $repository = New-TestRepository
        Set-Release $repository "0.5.1"
        Save-Commit $repository "fix: establish released version" | Out-Null
        Invoke-TestGit $repository @("tag", "-a", "v0.5.1", "-m", "Forge v0.5.1") | Out-Null
        Set-Release $repository "1.0.0"
        $base = Save-Commit $repository "feat!: premature major version"
        Set-Release $repository "0.5.2"
        Save-Commit $repository "feat: downgrade version" | Out-Null
        Assert-Throws { Invoke-Validation $repository "feat: downgrade version" -BaseSha $base } "requires a MINOR bump"
    }

    Test-Case "breaking footer without marker" {
        $repository = New-TestRepository
        Set-Release $repository "1.2.3"
        $base = Save-Commit $repository "chore: establish version"
        Set-Release $repository "1.3.0"
        Save-Commit $repository "feat: replace behavior" | Out-Null
        Assert-Throws {
            Invoke-Validation $repository "feat: replace behavior" "BREAKING CHANGE: behavior changed." $base
        } "requires !"
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

    Test-Case "publication" {
        $repository = New-TestRepository
        Save-Commit $repository "chore: initialize" | Out-Null
        Set-Release $repository "0.1.0"
        Save-Commit $repository "feat: initialize Forge" | Out-Null
        $log = Invoke-Publication $repository "feat: initialize Forge"
        if ((Invoke-TestGit $repository @("cat-file", "-t", "v0.1.0")) -ne "tag") {
            throw "Publication did not create an annotated tag."
        }
        if ((Invoke-TestGit "$repository.git" @("cat-file", "-t", "v0.1.0")) -ne "tag") {
            throw "Publication did not push the annotated tag."
        }
        $ghCalls = Get-Content $log -Raw
        if ($ghCalls -notmatch "release create v0.1.0") {
            throw "Publication did not create a GitHub Release. Stub calls: $ghCalls"
        }
    }

    Test-Case "existing release uploads replacement assets" {
        $repository = New-TestRepository
        Save-Commit $repository "chore: initialize" | Out-Null
        Set-Release $repository "0.1.0"
        Save-Commit $repository "feat: initialize Forge" | Out-Null
        $asset = Join-Path $repository "forge-windows-x64-portable_bundle.zip"
        Set-Content $asset "bundle"
        $log = Invoke-Publication $repository "feat: initialize Forge" $true @($asset)
        $ghCalls = Get-Content $log -Raw
        if ($ghCalls -notmatch "release upload v0.1.0 .*--clobber") {
            throw "Publication did not upload replacement release assets. Stub calls: $ghCalls"
        }
    }

    Write-Host "$passed release publisher tests passed."
} finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
