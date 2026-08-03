[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier,
    [string]$OutputDirectory
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
    dotnet restore (Join-Path $repositoryRoot 'Forge.slnx') --locked-mode
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    dotnet publish (Join-Path $repositoryRoot 'src\Forge.Cli\Forge.Cli.csproj') --configuration Release --runtime $RuntimeIdentifier --self-contained true --no-restore --output $stagingDirectory
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    dotnet publish (Join-Path $repositoryRoot 'src\Forge.Desktop\Forge.Desktop.csproj') --configuration Release --runtime $RuntimeIdentifier --self-contained true --no-restore --output $stagingDirectory
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    Compress-Archive -Path (Join-Path $stagingDirectory '*') -DestinationPath $bundlePath -Force
}
finally {
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}
