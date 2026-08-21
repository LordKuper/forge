[CmdletBinding()]
param()

function Assert-ForgeWindowsSupport(
    [bool]$PlatformIsWindows,
    [Version]$WindowsVersion
) {
    if (-not $PlatformIsWindows) {
        throw 'Forge installation is currently supported only on Windows.'
    }

    $minimumVersion = [Version]'10.0.19041'
    if ($WindowsVersion -lt $minimumVersion) {
        throw "Forge requires Windows 10 version 2004 (build 19041) or later. Detected: $WindowsVersion."
    }
}

function Get-ForgeBootstrapArchitecture {
    switch ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()) {
        'X64' { 'x64' }
        'Arm64' { 'arm64' }
        default { throw "Unsupported Windows architecture: $([Runtime.InteropServices.RuntimeInformation]::OSArchitecture)." }
    }
}

function Invoke-ForgeBootstrap(
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture,
    [object]$Release,
    [scriptblock]$DownloadFile,
    [scriptblock]$RunInstaller
) {
    if ($Release.draft -or $Release.prerelease -or $Release.tag_name -notmatch '^v\d+\.\d+\.\d+$') {
        throw "GitHub returned an invalid stable release: $($Release.tag_name)."
    }

    $bundleName = "forge-windows-$Architecture-portable_bundle.zip"
    $bundleAssets = @($Release.assets | Where-Object { $_.name -eq $bundleName })
    $checksumAssets = @($Release.assets | Where-Object { $_.name -eq 'checksums.txt' })
    if ($bundleAssets.Count -ne 1 -or $checksumAssets.Count -ne 1) {
        throw "Release $($Release.tag_name) does not contain the expected Windows bundle and checksums."
    }

    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $temporaryDirectory = Join-Path $temporaryRoot ("forge-install-" + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
        $bundlePath = Join-Path $temporaryDirectory $bundleName
        $checksumsPath = Join-Path $temporaryDirectory 'checksums.txt'

        Write-Output "Downloading Forge $($Release.tag_name) for Windows $Architecture..."
        & $DownloadFile $checksumAssets[0].browser_download_url $checksumsPath
        & $DownloadFile $bundleAssets[0].browser_download_url $bundlePath

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
            throw 'Downloaded bundle size does not match checksums.txt.'
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
        & $RunInstaller $forgePath
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
}

function Install-Forge {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'

    $platformIsWindows = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [Runtime.InteropServices.OSPlatform]::Windows)
    Assert-ForgeWindowsSupport $platformIsWindows ([Environment]::OSVersion.Version)

    if ($PSVersionTable.PSVersion.Major -lt 6) {
        [Net.ServicePointManager]::SecurityProtocol =
            [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    }

    $headers = @{ 'User-Agent' = 'Forge-Installer' }
    Write-Output 'Resolving the latest stable Forge release...'
    $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/LordKuper/forge/releases/latest' -Headers $headers
    Invoke-ForgeBootstrap `
        -Architecture (Get-ForgeBootstrapArchitecture) `
        -Release $release `
        -DownloadFile {
            param($Uri, $Destination)
            Invoke-WebRequest -Uri $Uri -Headers $headers -OutFile $Destination -UseBasicParsing
        } `
        -RunInstaller {
            param($ForgePath)
            & $ForgePath install
            if ($LASTEXITCODE -ne 0) {
                throw "Forge installation failed with exit code $LASTEXITCODE."
            }
        }
}

if ($MyInvocation.InvocationName -ne '.') {
    Install-Forge
}
