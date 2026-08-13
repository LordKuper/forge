[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

dotnet restore Forge.slnx --locked-mode
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build Forge.slnx --no-restore --configuration Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# The BCL cannot open a directory handle for a durability flush on any OS (see ADR 0007); only the composed
# Windows adapter provides it, so the Windows TFM's test run is the one that reflects the real shipped product
# here. .github/scripts/test-portable.ps1 exercises the net10.0 TFM on Linux/macOS with a test-only fallback.
dotnet test Forge.slnx --no-build --configuration Release --framework net10.0-windows10.0.19041.0
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
