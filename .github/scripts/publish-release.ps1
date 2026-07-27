[CmdletBinding()]
param([switch]$ValidateOnly)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$version = (Get-Content -LiteralPath "VERSION" -Raw).Trim()
$semVerPattern = "^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$"
if ($version -notmatch $semVerPattern) {
    throw "VERSION must contain a stable SemVer value."
}
$currentVersion = [version]$version

$changelogPath = "CHANGELOG.md"
if (-not (Test-Path -LiteralPath $changelogPath -PathType Leaf)) {
    throw "CHANGELOG.md is required."
}

$changelogLines = @(Get-Content -LiteralPath $changelogPath)
$releaseHeading = "## v$version"
$releaseHeadingPattern = "^## v(?<version>(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*))$"
$releaseVersions = @()
foreach ($line in $changelogLines) {
    if ($line -match "^## ") {
        if ($line -notmatch $releaseHeadingPattern) {
            throw "Every level-two changelog heading must contain a stable release version."
        }
        $releaseVersions += [version]$Matches["version"]
    }
}
if (-not $releaseVersions -or $releaseVersions[0] -ne $currentVersion) {
    throw "$releaseHeading must be the first release in CHANGELOG.md."
}
for ($index = 1; $index -lt $releaseVersions.Count; $index++) {
    if ($releaseVersions[$index] -ge $releaseVersions[$index - 1]) {
        throw "CHANGELOG.md releases must be ordered newest first."
    }
}

$releaseStart = [array]::IndexOf($changelogLines, $releaseHeading)
if ($releaseStart -lt 0) {
    throw "CHANGELOG.md must contain the exact heading '$releaseHeading'."
}

$releaseEnd = $changelogLines.Count
for ($index = $releaseStart + 1; $index -lt $changelogLines.Count; $index++) {
    if ($changelogLines[$index] -match "^## ") {
        $releaseEnd = $index
        break
    }
}

if ($releaseEnd -le $releaseStart + 1) {
    throw "$releaseHeading must not be empty."
}

$releaseNotes = ($changelogLines[($releaseStart + 1)..($releaseEnd - 1)] -join "`n").Trim()
$allowedCategories = @("Added", "Changed", "Deprecated", "Removed", "Fixed", "Security")
$hasCategory = $false
$hasEntry = $false
$categoryHasEntry = $false
foreach ($line in $releaseNotes -split "`n") {
    if ($line -match "^### (.+)$") {
        if ($hasCategory -and -not $categoryHasEntry) {
            throw "Every changelog category must contain an entry."
        }
        if ($Matches[1] -notin $allowedCategories) {
            throw "Unsupported changelog category: $($Matches[1])."
        }
        $hasCategory = $true
        $categoryHasEntry = $false
    } elseif ($line -match "^- .+") {
        if (-not $hasCategory) {
            throw "Every changelog entry must follow an allowed category."
        }
        $hasEntry = $true
        $categoryHasEntry = $true
    }
}
if (-not $hasCategory -or -not $hasEntry -or -not $categoryHasEntry) {
    throw "$releaseHeading must contain categorized user-facing changes."
}

$baseVersion = $null
$baseSha = $env:RELEASE_BASE_SHA
if (-not [string]::IsNullOrWhiteSpace($baseSha)) {
    $changedFiles = @(& git diff --name-only "$baseSha...HEAD" -- $changelogPath)
    if ($LASTEXITCODE -ne 0 -or $changedFiles -notcontains $changelogPath) {
        throw "Every feature branch must update CHANGELOG.md."
    }

    $baseVersionPath = @(& git ls-tree --name-only $baseSha -- "VERSION")
    if ($LASTEXITCODE -ne 0) {
        throw "Cannot inspect VERSION in the base commit."
    }
    if ($baseVersionPath -contains "VERSION") {
        $baseVersionText = (& git show "${baseSha}:VERSION").Trim()
        if ($LASTEXITCODE -ne 0 -or $baseVersionText -notmatch $semVerPattern) {
            throw "The base commit contains an invalid VERSION."
        }
        $baseVersion = [version]$baseVersionText
    }
}

$subject = $env:RELEASE_COMMIT_SUBJECT
if ([string]::IsNullOrWhiteSpace($subject)) {
    $subject = (& git log -1 --format=%s).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Cannot read the current commit."
    }
}

$commitBody = $env:RELEASE_COMMIT_BODY
if ([string]::IsNullOrWhiteSpace($commitBody)) {
    $commitBody = (& git log -1 --format=%B | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Cannot read the current commit body."
    }
}

$commitPattern = "^(?<type>feat|fix|docs|refactor|perf|test|build|ci|chore|revert)(\([^)]+\))?(?<breaking>!)?: .+"
$commitMatch = [regex]::Match($subject, $commitPattern)
if (-not $commitMatch.Success) {
    throw "The release commit does not follow Conventional Commits: $subject"
}

$isBreaking = $commitMatch.Groups["breaking"].Success
if ($isBreaking -and $commitBody -notmatch "(?m)^BREAKING CHANGE: \S.*$") {
    throw "A breaking release commit requires a BREAKING CHANGE footer."
}

if ($null -ne $baseVersion) {
    if ($isBreaking) {
        $expectedVersion = [version]::new($baseVersion.Major + 1, 0, 0)
        $expectedBump = "MAJOR"
    } elseif ($commitMatch.Groups["type"].Value -eq "feat") {
        $expectedVersion = [version]::new($baseVersion.Major, $baseVersion.Minor + 1, 0)
        $expectedBump = "MINOR"
    } else {
        $expectedVersion = [version]::new($baseVersion.Major, $baseVersion.Minor, $baseVersion.Build + 1)
        $expectedBump = "PATCH"
    }

    if ($currentVersion -ne $expectedVersion) {
        throw "$subject requires a $expectedBump bump from $baseVersion to $expectedVersion, not $version."
    }
}

$tag = "v$version"
& git fetch --tags origin
if ($LASTEXITCODE -ne 0) {
    throw "Cannot fetch release tags."
}

$head = (& git rev-parse HEAD).Trim()
$tagExists = @(& git tag --list $tag) -contains $tag

if ($tagExists) {
    $tagType = (& git cat-file -t $tag).Trim()
    if ($LASTEXITCODE -ne 0 -or $tagType -ne "tag") {
        throw "$tag must be an annotated tag."
    }
    $tagCommit = (& git rev-list -n 1 $tag).Trim()
    if (($tagCommit | Out-String).Trim() -ne $head) {
        throw "$tag already points to another commit."
    }
} else {
    $latestTag = & git tag --list "v*" |
        Where-Object { $_ -match "^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$" } |
        Sort-Object { [version]$_.Substring(1) } -Descending |
        Select-Object -First 1
    if ($latestTag) {
        $latestVersion = [version]$latestTag.Substring(1)
        if ($currentVersion -le $latestVersion) {
            $latestCommit = (& git rev-list -n 1 $latestTag).Trim()
            & git merge-base --is-ancestor $head $latestCommit
            $latestTagIsOnDescendant = $LASTEXITCODE -eq 0
            if ($currentVersion -eq $latestVersion -or -not $latestTagIsOnDescendant) {
                throw "$version does not increase the latest released version."
            }
        }
    }
}

if ($ValidateOnly) {
    Write-Host "Validated $tag and commit: $subject"
    exit 0
}

if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN) -or
    [string]::IsNullOrWhiteSpace($env:GITHUB_REPOSITORY)) {
    throw "GH_TOKEN and GITHUB_REPOSITORY are required."
}

if (-not $tagExists) {
    & git config user.name "github-actions[bot]"
    & git config user.email "41898282+github-actions[bot]@users.noreply.github.com"
    & git tag --annotate $tag --message "Forge $tag"
    & git push origin "refs/tags/$tag"
    if ($LASTEXITCODE -ne 0) {
        throw "Cannot publish $tag."
    }
}

$savedErrorPreference = $ErrorActionPreference
$ErrorActionPreference = "Continue"
& gh release view $tag --repo $env:GITHUB_REPOSITORY *> $null
$releaseExists = $LASTEXITCODE -eq 0
$ErrorActionPreference = $savedErrorPreference
if ($releaseExists) {
    Write-Host "Release $tag already exists."
    exit 0
}

& gh release create $tag `
    --repo $env:GITHUB_REPOSITORY `
    --verify-tag `
    --title "Forge $tag" `
    --notes $releaseNotes
if ($LASTEXITCODE -ne 0) {
    throw "Cannot publish release $tag."
}
