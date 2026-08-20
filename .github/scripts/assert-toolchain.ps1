[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$actual = @{
    ".NET SDK" = ((& dotnet --version)).Trim()
}
$expected = @{
    ".NET SDK" = "10.0.303"
}
$minimumPowerShellVersion = [version]"7.6.3"
$actualPowerShellVersion = $PSVersionTable.PSVersion

if ($actualPowerShellVersion -lt $minimumPowerShellVersion) {
    throw "PowerShell must be $minimumPowerShellVersion or newer, found $actualPowerShellVersion."
}

foreach ($tool in $expected.Keys) {
    if ($actual[$tool] -ne $expected[$tool]) {
        throw "$tool must be $($expected[$tool]), found $($actual[$tool])."
    }
}
Write-Host "Verified pinned release toolchain."
