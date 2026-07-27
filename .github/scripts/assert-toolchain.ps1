[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$actual = @{
    "runner image" = $env:ImageVersion
    "Git" = ((& git --version) -replace "^git version ", "").Trim()
    "GitHub CLI" = ((& gh --version)[0] -replace "^gh version ([^ ]+).*$", '$1').Trim()
}
$expected = @{
    "runner image" = "20260720.247.2"
    "Git" = "2.54.0"
    "GitHub CLI" = "2.96.0"
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
