[CmdletBinding()]
param()

# ADR 0005 / Stage 8 audit (P8.34-P8.41): proves the real production ILocalControlTransport
# (NamedPipeControlTransport, PipeOptions.CurrentUserOnly — the same transport ForgeHostClient and
# ControlPlaneHostedService use for the Host's control plane) actually blocks a connection attempt
# from a *different* local OS user, not just a different instance id. This needs a genuine second
# OS user account, so it runs only here (a Windows CI job with local-admin rights), never inside
# `dotnet test`, which cannot create OS users.

$ErrorActionPreference = 'Stop'

$probeProject = 'tests/Forge.PipeIsolationProbe/Forge.PipeIsolationProbe.csproj'
dotnet restore $probeProject --locked-mode
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build $probeProject --no-restore --configuration Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$probeDll = (Resolve-Path (Join-Path (Split-Path $probeProject -Parent) 'bin/Release/net10.0/Forge.PipeIsolationProbe.dll')).Path
$dotnetPath = (Get-Command dotnet).Source

function Start-ProbeListener {
    param([string]$PipeName)

    Start-Job -ScriptBlock {
        param($DotnetPath, $ProbeDll, $Pipe)
        & $DotnetPath $ProbeDll 'listen' $Pipe 15
        $LASTEXITCODE
    } -ArgumentList $dotnetPath, $probeDll, $PipeName
}

function New-RandomPassword {
    # Guarantees at least one character from each required class, then pads to length with the
    # combined pool — Windows local-account password complexity requires 3 of 4 classes.
    $upper = 'ABCDEFGHJKLMNPQRSTUVWXYZ'
    $lower = 'abcdefghijkmnpqrstuvwxyz'
    $digit = '23456789'
    $special = '!@#$%^&*-_='
    $pool = $upper + $lower + $digit + $special
    $required = @(
        $upper[(Get-Random -Maximum $upper.Length)],
        $lower[(Get-Random -Maximum $lower.Length)],
        $digit[(Get-Random -Maximum $digit.Length)],
        $special[(Get-Random -Maximum $special.Length)]
    )
    $rest = 1..20 | ForEach-Object { $pool[(Get-Random -Maximum $pool.Length)] }
    -join (($required + $rest) | Sort-Object { Get-Random })
}

# --- Sanity check: the SAME user connecting to its own listener must succeed. Without this, a
# broken probe (not the isolation it's meant to prove) could make the isolation check below pass
# for the wrong reason. ---
$samePipe = "forge-isolation-same-$([guid]::NewGuid().ToString('N'))"
$sameUserListenJob = Start-ProbeListener -PipeName $samePipe
Start-Sleep -Seconds 2
& $dotnetPath $probeDll 'connect' $samePipe 5
$sameUserConnectExit = $LASTEXITCODE
$sameUserListenResult = Receive-Job -Job $sameUserListenJob -Wait
Remove-Job -Job $sameUserListenJob
Write-Host "Same-user listener result: $sameUserListenResult"
if ($sameUserConnectExit -ne 0) {
    Write-Error "Sanity check failed: the same user could not connect to its own listener (exit $sameUserConnectExit). Not testing cross-user isolation against a broken baseline."
    exit 1
}

Write-Host 'Same-user round trip succeeded.'

# --- Isolation check: a DIFFERENT local OS user must fail to connect. ---
$userName = "forgeiso$([guid]::NewGuid().ToString('N').Substring(0, 10))"
$securePassword = ConvertTo-SecureString (New-RandomPassword) -AsPlainText -Force
New-LocalUser -Name $userName -Password $securePassword -AccountNeverExpires -PasswordNeverExpires -UserMayNotChangePassword | Out-Null
$credential = [System.Management.Automation.PSCredential]::new($userName, $securePassword)

try {
    $otherPipe = "forge-isolation-other-$([guid]::NewGuid().ToString('N'))"
    $otherUserListenJob = Start-ProbeListener -PipeName $otherPipe
    Start-Sleep -Seconds 2

    $process = Start-Process -FilePath $dotnetPath `
        -ArgumentList @($probeDll, 'connect', $otherPipe, '5') `
        -Credential $credential `
        -WorkingDirectory (Get-Location) `
        -WindowStyle Hidden `
        -PassThru `
        -Wait
    $otherUserConnectExit = $process.ExitCode
    $otherUserListenResult = Receive-Job -Job $otherUserListenJob -Wait
    Remove-Job -Job $otherUserListenJob

    Write-Host "Different-user connect exit code: $otherUserConnectExit"
    Write-Host "Listener result while waiting for the different user: $otherUserListenResult"

    if ($otherUserConnectExit -eq 0) {
        Write-Error 'SECURITY REGRESSION: a different local OS user connected to a PipeOptions.CurrentUserOnly control-plane pipe.'
        exit 1
    }

    Write-Host 'Same-user isolation confirmed: a different local OS user could not connect.'
}
finally {
    Remove-LocalUser -Name $userName -ErrorAction SilentlyContinue
}
