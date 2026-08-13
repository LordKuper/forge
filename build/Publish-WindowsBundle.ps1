[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier,
    [string]$OutputDirectory,
    [switch]$SkipRestore
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts'
}

$stagingDirectory = Join-Path ([IO.Path]::GetTempPath()) "forge-bundle-$([Guid]::NewGuid().ToString('N'))"
$bundlePath = Join-Path $OutputDirectory "forge-windows-$($RuntimeIdentifier.Substring(4))-portable_bundle.zip"

try {
    New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
    if (-not $SkipRestore) {
        dotnet restore (Join-Path $repositoryRoot 'Forge.slnx') --locked-mode
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    dotnet publish (Join-Path $repositoryRoot 'src\Forge.Cli.Windows\Forge.Cli.Windows.csproj') --configuration Release --runtime $RuntimeIdentifier --self-contained true --no-restore --property:PublishReadyToRun=false --output $stagingDirectory
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    dotnet publish (Join-Path $repositoryRoot 'src\Forge.Desktop\Forge.Desktop.csproj') --configuration Release --runtime $RuntimeIdentifier --self-contained true --no-restore --property:PublishReadyToRun=false --output $stagingDirectory
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $bundlePath) {
        Remove-Item -LiteralPath $bundlePath -Force
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $timestamp = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    $archive = [IO.Compression.ZipFile]::Open($bundlePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        Get-ChildItem -LiteralPath $stagingDirectory -File -Recurse |
            Sort-Object { $_.FullName.Substring($stagingDirectory.Length).TrimStart('\', '/') } |
            ForEach-Object {
                $name = $_.FullName.Substring($stagingDirectory.Length).TrimStart('\', '/') -replace '\\', '/'
                $entry = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $timestamp
                $input = [IO.File]::OpenRead($_.FullName)
                try {
                    $output = $entry.Open()
                    try { $input.CopyTo($output) } finally { $output.Dispose() }
                } finally { $input.Dispose() }
            }
    }
    finally {
        $archive.Dispose()
    }
}
finally {
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}
