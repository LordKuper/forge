[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repository = "LordKuper/forge"
$release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repository/releases/latest" -Headers @{ "User-Agent" = "Forge-Installer" }
if ($release.draft -or $release.prerelease -or [string]::IsNullOrWhiteSpace($release.published_at) -or
    $release.tag_name -notmatch "^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$") {
    throw "The latest release is not a published stable SemVer release."
}

$architecture = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    "X64" { "x64" }
    "Arm64" { "arm64" }
    default { throw "Forge supports only x64 and arm64 Windows installations." }
}
$version = $release.tag_name.Substring(1)
$assetName = "forge-windows-$architecture-portable_bundle.zip"
$asset = @($release.assets | Where-Object name -eq $assetName)
$checksums = @($release.assets | Where-Object name -eq "checksums.txt")
$provenance = @($release.assets | Where-Object name -eq "provenance.intoto.jsonl")
if ($asset.Count -ne 1 -or $checksums.Count -ne 1 -or $provenance.Count -ne 1) {
    throw "The release is missing the required package, checksum manifest, or provenance bundle."
}

$root = Join-Path $env:LOCALAPPDATA "Forge"
$versions = Join-Path $root "versions"
$destination = Join-Path $versions $version
$staging = Join-Path $versions ".staging-$([guid]::NewGuid().ToString('N'))"
$archive = Join-Path $root ".download-$([guid]::NewGuid().ToString('N')).zip"

function Get-ChecksumEntry([string]$Manifest, [string]$ExpectedName) {
    foreach ($line in Get-Content -LiteralPath $Manifest) {
        if ($line -match "^(?<hash>[A-Fa-f0-9]{64})\s+(?<size>\d+)\s+\*?(?<name>.+)$" -and $Matches.name -eq $ExpectedName) {
            return [pscustomobject]@{ Hash = $Matches.hash.ToUpperInvariant(); Size = [long]$Matches.size }
        }
    }

    throw "The checksum manifest does not contain $ExpectedName."
}

function Set-CurrentVersion([string]$CurrentPath, [string]$CurrentVersion) {
    $temp = "$CurrentPath.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [System.IO.File]::WriteAllText($temp, (@{ version = $CurrentVersion } | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
        if ([System.IO.File]::Exists($CurrentPath)) {
            [System.IO.File]::Replace($temp, $CurrentPath, "$CurrentPath.previous", $true)
        } else {
            [System.IO.File]::Move($temp, $CurrentPath)
        }
    } finally {
        if ([System.IO.File]::Exists($temp)) { [System.IO.File]::Delete($temp) }
    }
}

function Add-ForgePath([string]$BinPath) {
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $entries = @($userPath -split ";" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($entries -notcontains $BinPath) {
        [Environment]::SetEnvironmentVariable("Path", ($entries + $BinPath -join ";"), "User")
    }
    if (($env:Path -split ";") -notcontains $BinPath) { $env:Path = "$env:Path;$BinPath" }
}

try {
    New-Item -ItemType Directory -Force -Path $root, $versions | Out-Null
    Invoke-WebRequest -Uri $checksums[0].browser_download_url -OutFile "$archive.checksums"
    Invoke-WebRequest -Uri $asset[0].browser_download_url -OutFile $archive
    $expected = Get-ChecksumEntry "$archive.checksums" $assetName
    $actual = Get-FileHash -LiteralPath $archive -Algorithm SHA256
    if ((Get-Item -LiteralPath $archive).Length -ne $asset[0].size -or
        (Get-Item -LiteralPath $archive).Length -ne $expected.Size -or
        $actual.Hash -ne $expected.Hash) {
        throw "The downloaded package does not match its expected size and SHA-256 hash."
    }

    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "GitHub CLI is required to verify release provenance."
    }
    & gh attestation verify $archive --repo $repository
    if ($LASTEXITCODE -ne 0) { throw "Release provenance verification failed." }

    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($entry in $zip.Entries) {
            $path = [System.IO.Path]::GetFullPath((Join-Path $staging $entry.FullName))
            if (-not $path.StartsWith("$staging$([System.IO.Path]::DirectorySeparatorChar)", [StringComparison]::OrdinalIgnoreCase)) {
                throw "The release package contains an unsafe path."
            }
            if ([string]::IsNullOrEmpty($entry.Name)) { New-Item -ItemType Directory -Force -Path $path | Out-Null; continue }
            New-Item -ItemType Directory -Force -Path ([System.IO.Path]::GetDirectoryName($path)) | Out-Null
            $entry.ExtractToFile($path, $false)
        }
    } finally {
        $zip.Dispose()
    }

    $selfTest = Join-Path $staging "forge.exe"
    if (-not (Test-Path -LiteralPath $selfTest -PathType Leaf)) { throw "The release package has no Forge CLI host." }
    if ((Start-Process -FilePath $selfTest -ArgumentList "--self-test" -Wait -PassThru).ExitCode -ne 0) {
        throw "The staged Forge CLI host self-test failed."
    }

    if (Test-Path -LiteralPath $destination) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    } else {
        [System.IO.Directory]::Move($staging, $destination)
    }
    Set-CurrentVersion (Join-Path $root "current.json") $version

    $bin = Join-Path $root "bin"
    New-Item -ItemType Directory -Force -Path $bin | Out-Null
    $shim = @'
@echo off
for /f %%i in ('powershell -NoProfile -Command "(Get-Content (Join-Path $env:LOCALAPPDATA 'Forge\current.json') -Raw | ConvertFrom-Json).version"') do set FORGE_VERSION=%%i
"%LOCALAPPDATA%\Forge\versions\%FORGE_VERSION%\forge.exe" %*
exit /b %ERRORLEVEL%
'@
    Set-Content -LiteralPath (Join-Path $bin "forge.cmd") -Encoding Ascii -Value $shim
    Add-ForgePath $bin
    Write-Host "Installed Forge $version for win-$architecture."
} catch {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    throw
} finally {
    Remove-Item -LiteralPath $archive, "$archive.checksums" -Force -ErrorAction SilentlyContinue
}
