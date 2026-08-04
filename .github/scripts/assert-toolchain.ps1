[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$actual = @{
    "runner image" = $env:ImageVersion
    "Git" = ((& git --version) -replace "^git version ", "").Trim()
    "GitHub CLI" = ((& gh --version)[0] -replace "^gh version ([^ ]+).*$", '$1').Trim()
    ".NET SDK" = ((& dotnet --version)).Trim()
}
$expectedRunnerImage = if ($env:RUNNER_OS -eq "Windows") {
    "20260728.188.1"
}
else {
    "20260720.247.2"
}
$expected = @{
    "runner image" = $expectedRunnerImage
    "Git" = "2.55.0.windows.3"
    "GitHub CLI" = "2.96.0"
    ".NET SDK" = "10.0.302"
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
