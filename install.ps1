[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$windows = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [Runtime.InteropServices.OSPlatform]::Windows)
if (-not $windows) {
    throw 'Forge installation is currently supported only on Windows.'
}

$architecture = switch ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()) {
    'X64' { 'x64' }
    'Arm64' { 'arm64' }
    default { throw "Unsupported Windows architecture: $([Runtime.InteropServices.RuntimeInformation]::OSArchitecture)." }
}

if ($PSVersionTable.PSVersion.Major -lt 6) {
    [Net.ServicePointManager]::SecurityProtocol =
        [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
}

$repository = 'LordKuper/forge'
$releaseUri = "https://api.github.com/repos/$repository/releases/latest"
$headers = @{ 'User-Agent' = 'Forge-Installer' }
$bundleName = "forge-windows-$architecture-portable_bundle.zip"
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$temporaryDirectory = Join-Path $temporaryRoot ("forge-install-" + [Guid]::NewGuid().ToString('N'))

try {
    Write-Output 'Resolving the latest stable Forge release...'
    $release = Invoke-RestMethod -Uri $releaseUri -Headers $headers
    if ($release.draft -or $release.prerelease -or $release.tag_name -notmatch '^v\d+\.\d+\.\d+$') {
        throw "GitHub returned an invalid stable release: $($release.tag_name)."
    }

    $bundleAssets = @($release.assets | Where-Object { $_.name -eq $bundleName })
    $checksumAssets = @($release.assets | Where-Object { $_.name -eq 'checksums.txt' })
    if ($bundleAssets.Count -ne 1 -or $checksumAssets.Count -ne 1) {
        throw "Release $($release.tag_name) does not contain the expected Windows bundle and checksums."
    }

    New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
    $bundlePath = Join-Path $temporaryDirectory $bundleName
    $checksumsPath = Join-Path $temporaryDirectory 'checksums.txt'

    Write-Output "Downloading Forge $($release.tag_name) for Windows $architecture..."
    Invoke-WebRequest -Uri $checksumAssets[0].browser_download_url -Headers $headers -OutFile $checksumsPath -UseBasicParsing
    Invoke-WebRequest -Uri $bundleAssets[0].browser_download_url -Headers $headers -OutFile $bundlePath -UseBasicParsing

    $matchingChecksums = @(Get-Content -LiteralPath $checksumsPath | ForEach-Object {
        if ($_ -match '^(?<hash>[0-9A-Fa-f]{64})\s+(?<size>\d+)\s+(?<name>.+)$' -and
            $Matches.name -eq $bundleName) {
            [pscustomobject]@{
                Hash = $Matches.hash
                Size = [long]$Matches.size
            }
        }
    })
    if ($matchingChecksums.Count -ne 1) {
        throw "checksums.txt does not contain exactly one entry for $bundleName."
    }

    $downloadedBundle = Get-Item -LiteralPath $bundlePath
    if ($downloadedBundle.Length -ne $matchingChecksums[0].Size) {
        throw "Downloaded bundle size does not match checksums.txt."
    }

    $actualHash = (Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256).Hash
    if ($actualHash -ne $matchingChecksums[0].Hash) {
        throw 'Downloaded bundle SHA-256 does not match checksums.txt.'
    }

    $extractionPath = Join-Path $temporaryDirectory 'bundle'
    Expand-Archive -LiteralPath $bundlePath -DestinationPath $extractionPath
    $forgePath = Join-Path $extractionPath 'forge.exe'
    if (-not (Test-Path -LiteralPath $forgePath -PathType Leaf)) {
        throw 'The downloaded bundle does not contain forge.exe at its root.'
    }

    Write-Output 'Starting the Forge per-user installer...'
    & $forgePath install
    if ($LASTEXITCODE -ne 0) {
        throw "Forge installation failed with exit code $LASTEXITCODE."
    }

    Write-Output 'Forge installation completed. Open a new terminal and run: forge --version'
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        $resolvedTemporaryDirectory = [IO.Path]::GetFullPath($temporaryDirectory)
        $safePrefix = Join-Path $temporaryRoot 'forge-install-'
        if (-not $resolvedTemporaryDirectory.StartsWith($safePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove an unexpected temporary directory: $resolvedTemporaryDirectory"
        }

        Remove-Item -LiteralPath $resolvedTemporaryDirectory -Recurse -Force
    }
}
