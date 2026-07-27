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
$categoryPattern = "(?m)^### (Added|Changed|Deprecated|Removed|Fixed|Security)$"
if ($releaseNotes -notmatch $categoryPattern -or $releaseNotes -notmatch "(?m)^- .+") {
    throw "$releaseHeading must contain categorized user-facing changes."
}

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
        $baseVersion = (& git show "${baseSha}:VERSION").Trim()
        if ($LASTEXITCODE -ne 0 -or $baseVersion -notmatch $semVerPattern) {
            throw "The base commit contains an invalid VERSION."
        }
        if ($currentVersion -le [version]$baseVersion) {
            throw "$version must increase the base version $baseVersion."
        }
    }
}

$subject = $env:RELEASE_COMMIT_SUBJECT
if ([string]::IsNullOrWhiteSpace($subject)) {
    $subject = (& git log -1 --format=%s).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Cannot read the current commit."
    }
}

$commitPattern = "^(feat|fix|docs|refactor|perf|test|build|ci|chore|revert)(\([a-z0-9._/-]+\))?!?: .+"
if ($subject -notmatch $commitPattern) {
    throw "The release commit does not follow Conventional Commits: $subject"
}

$tag = "v$version"
& git fetch --tags origin
if ($LASTEXITCODE -ne 0) {
    throw "Cannot fetch release tags."
}

$head = (& git rev-parse HEAD).Trim()
$tagExists = @(& git tag --list $tag) -contains $tag

if ($tagExists) {
    $tagCommit = (& git rev-list -n 1 $tag).Trim()
    if (($tagCommit | Out-String).Trim() -ne $head) {
        throw "$tag already points to another commit."
    }
} else {
    $releasedVersions = @(
        & git tag --list "v*" |
            Where-Object { $_ -match "^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$" } |
            ForEach-Object { [version]$_.Substring(1) }
    )
    if ($releasedVersions -and $currentVersion -le ($releasedVersions | Sort-Object -Descending)[0]) {
        throw "$version does not increase the latest released version."
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
