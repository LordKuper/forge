[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$version = (Get-Content -LiteralPath (Join-Path $repositoryRoot 'VERSION') -Raw).Trim()
$expected = "[$version, )"
$lockFiles = @(& git -C $repositoryRoot ls-files -- ':(glob)**/packages.lock.json')
if ($LASTEXITCODE -ne 0) {
    throw 'Cannot enumerate tracked NuGet lock files.'
}

$violations = foreach ($relativePath in $lockFiles) {
    $lock = ConvertFrom-Json -InputObject (
        Get-Content -LiteralPath (Join-Path $repositoryRoot $relativePath) -Raw
    ) -AsHashtable
    foreach ($target in $lock['dependencies'].Values) {
        foreach ($project in $target.GetEnumerator()) {
            if ($project.Value['type'] -ne 'Project' -or -not $project.Value.Contains('dependencies')) {
                continue
            }
            foreach ($dependency in $project.Value['dependencies'].GetEnumerator()) {
                if ($dependency.Key -like 'Forge.*' -and $dependency.Value -ne $expected) {
                    "$relativePath`: $($project.Key) -> $($dependency.Key) is $($dependency.Value), expected $expected"
                }
            }
        }
    }
}

if ($violations) {
    throw "NuGet lock files contain stale internal Forge version pins:`n$($violations -join "`n")`nRun dotnet restore Forge.slnx --force-evaluate."
}
