[CmdletBinding()]
param()

# ADR 0005 / Stage 8 audit (P8.34-P8.41): proves the real production ILocalControlTransport
# (NamedPipeControlTransport, PipeOptions.CurrentUserOnly — the same transport ForgeHostClient and
# ControlPlaneHostedService use for the Host's control plane) actually blocks a connection attempt
# from a *different* local OS user, not just a different instance id. This needs a genuine second
# OS user account, so it runs only here (a Windows CI job with local-admin rights), never inside
# `dotnet test`, which cannot create OS users.
#
# A "different user could not connect" result is meaningless on its own: it could mean isolation
# worked, or it could mean the listener never bound in time, or the new account cannot even launch
# dotnet.exe for an unrelated reason. Every check below distinguishes those causes explicitly
# instead of trusting a bare exit code, and fails loudly (never silently reports success) when it
# cannot.

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
        $output = & $DotnetPath $ProbeDll 'listen' $Pipe 20
        [PSCustomObject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
    } -ArgumentList $dotnetPath, $probeDll, $PipeName
}

function Invoke-ProbeAsCurrentUser {
    param([string]$PipeName, [int]$TimeoutSeconds)

    $output = & $dotnetPath $probeDll 'connect' $PipeName $TimeoutSeconds
    [PSCustomObject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
}

function Invoke-ProcessAsCredential {
    param([string[]]$ArgumentList, [System.Management.Automation.PSCredential]$Credential)

    $stdout = New-TemporaryFile
    $stderr = New-TemporaryFile
    try {
        $process = Start-Process -FilePath $dotnetPath -ArgumentList $ArgumentList -Credential $Credential `
            -WorkingDirectory (Get-Location) -WindowStyle Hidden -PassThru -Wait `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        [PSCustomObject]@{
            ExitCode = $process.ExitCode
            StdOut = (Get-Content $stdout -Raw)
            StdErr = (Get-Content $stderr -Raw)
        }
    }
    finally {
        Remove-Item $stdout, $stderr -ErrorAction SilentlyContinue
    }
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

# --- Sanity check: the SAME user connecting to its own listener must succeed, and must report the
# "connected" outcome specifically (not just any zero exit code). Without this, a broken probe
# could make the isolation check below pass for the wrong reason. ---
$samePipe = "forge-isolation-same-$([guid]::NewGuid().ToString('N'))"
$sameUserListenJob = Start-ProbeListener -PipeName $samePipe
# A fixed wait, not a Test-Path poll for the pipe's existence: the pipe object exists (and
# Test-Path reports it) well before the listener has actually reached WaitForConnectionAsync in
# the spawned job's own dotnet process, so connecting as soon as Test-Path succeeds races and
# reliably fails — confirmed empirically. 3 seconds covers job scheduling + a cold `dotnet` start.
Start-Sleep -Seconds 3
$sameUserConnect = Invoke-ProbeAsCurrentUser -PipeName $samePipe -TimeoutSeconds 5
$sameUserListenResult = Receive-Job -Job $sameUserListenJob -Wait
Remove-Job -Job $sameUserListenJob
Write-Host "Same-user connect output: $($sameUserConnect.Output)"
Write-Host "Same-user listener result: exit=$($sameUserListenResult.ExitCode) output=$($sameUserListenResult.Output)"
if ($sameUserConnect.ExitCode -ne 0 -or $sameUserConnect.Output -ne 'connected') {
    Write-Error "Sanity check failed: the same user could not connect to its own listener (exit $($sameUserConnect.ExitCode), output '$($sameUserConnect.Output)'). Not testing cross-user isolation against a broken baseline."
    exit 1
}

Write-Host 'Same-user round trip succeeded.'

# --- Isolation check: a DIFFERENT local OS user must fail to connect, and the failure must be
# distinguishable from an unrelated setup problem. ---
$userName = "forgeiso$([guid]::NewGuid().ToString('N').Substring(0, 10))"
$securePassword = ConvertTo-SecureString (New-RandomPassword) -AsPlainText -Force
New-LocalUser -Name $userName -Password $securePassword -AccountNeverExpires -PasswordNeverExpires -UserMayNotChangePassword | Out-Null

try {
    # Everything that can fail after the account exists lives inside this try, including building
    # the credential object, so `finally` always removes the account regardless of what fails next.
    $credential = [System.Management.Automation.PSCredential]::new($userName, $securePassword)

    # Positive control: prove the new account can launch dotnet.exe at all. Without this, "the
    # different user could not connect" is indistinguishable from "this brand-new account cannot
    # run programs" — a false confirmation of pipe isolation that never touched the pipe's ACL.
    $control = Invoke-ProcessAsCredential -ArgumentList @('--version') -Credential $credential
    Write-Host "Positive control (dotnet --version as the new user) exit code: $($control.ExitCode)"
    if ($control.ExitCode -ne 0) {
        Write-Error "Positive control failed: the new local user could not even run 'dotnet --version' (exit $($control.ExitCode)). $($control.StdErr) The isolation check below cannot distinguish a real access-control failure from a broken account, so it will not run."
        exit 1
    }

    $otherPipe = "forge-isolation-other-$([guid]::NewGuid().ToString('N'))"
    $otherUserListenJob = Start-ProbeListener -PipeName $otherPipe
    try {
        Start-Sleep -Seconds 3
        $otherUserConnect = Invoke-ProcessAsCredential `
            -ArgumentList @($probeDll, 'connect', $otherPipe, '5') `
            -Credential $credential
    }
    finally {
        # Reclaim the listener job (and its spawned dotnet.exe child) even if the credentialed
        # connect attempt above throws a terminating error instead of returning — e.g. a transient
        # Start-Process -Credential logon failure — which would otherwise skip the Receive-Job/
        # Remove-Job pair entirely and leak the background job.
        $otherUserListenResult = Receive-Job -Job $otherUserListenJob -Wait -ErrorAction SilentlyContinue
        Remove-Job -Job $otherUserListenJob -Force
    }

    Write-Host "Different-user connect exit code: $($otherUserConnect.ExitCode)"
    Write-Host "Different-user connect stdout: $($otherUserConnect.StdOut)"
    Write-Host "Different-user connect stderr: $($otherUserConnect.StdErr)"
    Write-Host "Listener result while waiting for the different user: exit=$($otherUserListenResult.ExitCode) output=$($otherUserListenResult.Output)"

    if ($otherUserConnect.ExitCode -eq 0) {
        Write-Error 'SECURITY REGRESSION: a different local OS user connected to a PipeOptions.CurrentUserOnly control-plane pipe.'
        exit 1
    }

    # The positive control above only proves the account can launch dotnet.exe from the repo root;
    # it does NOT prove the account can read tests/Forge.PipeIsolationProbe's own build output, a
    # different ACL surface. So an unrelated read-access failure on the probe DLL itself could also
    # produce non-empty diagnostic text here — require the SPECIFIC exception NamedPipeClientStream
    # raises for a CurrentUserOnly rejection, not merely "some diagnostic text exists".
    $accessDeniedIndicators = @('UnauthorizedAccessException', 'access is denied', 'access denied')
    $diagnostic = $otherUserConnect.StdOut.Trim()
    $isAccessDenied = $accessDeniedIndicators | Where-Object { $diagnostic -like "*$_*" }
    if (-not $isAccessDenied) {
        Write-Error "Isolation check inconclusive: the different-user connect attempt failed (exit $($otherUserConnect.ExitCode)) but the reason ('$diagnostic') is not a recognized access-control failure — it may be an unrelated setup problem (e.g. the account cannot read the probe's own build output) rather than PipeOptions.CurrentUserOnly rejecting the connection. $($otherUserConnect.StdErr)"
        exit 1
    }

    Write-Host "Same-user isolation confirmed: a different local OS user could not connect (reason: $diagnostic)."
}
finally {
    Remove-LocalUser -Name $userName -ErrorAction SilentlyContinue
}
