[CmdletBinding()]
param()

# ADR 0005 / Stage 8 audit (P8.34-P8.41): proves two real production current-user-scoped
# primitives actually deny access to a *different* local OS user, not just a different instance id:
#   1. ILocalControlTransport (NamedPipeControlTransport, PipeOptions.CurrentUserOnly — the
#      transport ForgeHostClient/ControlPlaneHostedService use for the Host's control plane).
#   2. IProjectLease (MutexProjectLease, NamedWaitHandleOptions.CurrentUserOnly — the same
#      primitive ControlPlaneHostedService/ProviderInstallLock use).
# Both assertions have the same shape: CurrentUserOnly restricts a security descriptor on a SHARED
# named object (it does not create a separate object per user), so a different user's attempt to
# open a name the current user already owns fails with an access-denial exception — confirmed
# empirically (`UnauthorizedAccessException: Access to the path 'Local\...' is denied.`) rather
# than assumed. An earlier version of this script asserted the opposite for the mutex (that two
# different users must both succeed independently) on a mistaken belief that CurrentUserOnly
# namespaces objects per user; it does not, so that assertion could never pass and is wrong.
# Both checks need a genuine second OS user account, so they run only here (a Windows CI job with
# local-admin rights), never inside `dotnet test`, which cannot create OS users.
#
# A bare "different user could (not) do X" result is meaningless on its own: it could mean
# isolation worked, or it could mean the listener/holder never reached the ready state in time, or
# the new account cannot even launch dotnet.exe for an unrelated reason. Every check below
# distinguishes those causes explicitly instead of trusting a bare exit code, and fails loudly
# (never silently reports success) when it cannot.

$ErrorActionPreference = 'Stop'

$pipeProbeProject = 'tests/Forge.PipeIsolationProbe/Forge.PipeIsolationProbe.csproj'
$mutexProbeProject = 'tests/Forge.MutexIsolationProbe/Forge.MutexIsolationProbe.csproj'
foreach ($project in @($pipeProbeProject, $mutexProbeProject)) {
    dotnet restore $project --locked-mode
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    dotnet build $project --no-restore --configuration Release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$probeDll = (Resolve-Path (Join-Path (Split-Path $pipeProbeProject -Parent) 'bin/Release/net10.0/Forge.PipeIsolationProbe.dll')).Path
$mutexProbeDll = (Resolve-Path (Join-Path (Split-Path $mutexProbeProject -Parent) 'bin/Release/net10.0/Forge.MutexIsolationProbe.dll')).Path
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

function Start-MutexHolder {
    param([string]$LeaseName, [int]$HoldSeconds)

    Start-Job -ScriptBlock {
        param($DotnetPath, $ProbeDll, $Name, $Hold)
        $output = & $DotnetPath $ProbeDll 'acquire' $Name $Hold 10
        [PSCustomObject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
    } -ArgumentList $dotnetPath, $mutexProbeDll, $LeaseName, $HoldSeconds
}

function Invoke-MutexProbeAsCurrentUser {
    param([string]$LeaseName, [int]$TimeoutSeconds)

    $output = & $dotnetPath $mutexProbeDll 'acquire' $LeaseName 0 $TimeoutSeconds
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

# --- Sanity check: the SAME user acquiring a mutex of an identical name it already holds must
# block and time out (ordinary mutual exclusion via Mutex.WaitOne) while the first holder is still
# alive. This is a different code path from the cross-user check below, which denies access
# outright (an exception, not a wait) — proving this one first establishes that the probe's
# same-user contention behaves as expected before drawing any conclusion from the other. ---
$sameLease = "forge-mutex-isolation-same-$([guid]::NewGuid().ToString('N'))"
$sameUserHolderJob = Start-MutexHolder -LeaseName $sameLease -HoldSeconds 8
Start-Sleep -Seconds 3
$sameUserContend = Invoke-MutexProbeAsCurrentUser -LeaseName $sameLease -TimeoutSeconds 3
$sameUserHolderResult = Receive-Job -Job $sameUserHolderJob -Wait
Remove-Job -Job $sameUserHolderJob
Write-Host "Same-user contend output: $($sameUserContend.Output)"
Write-Host "Same-user holder result: exit=$($sameUserHolderResult.ExitCode) output=$($sameUserHolderResult.Output)"
if ($sameUserHolderResult.ExitCode -ne 0 -or $sameUserHolderResult.Output -ne 'acquired') {
    Write-Error "Sanity check failed: the same-user holder never acquired its own mutex (exit $($sameUserHolderResult.ExitCode), output '$($sameUserHolderResult.Output)')."
    exit 1
}

if ($sameUserContend.ExitCode -eq 0 -or $sameUserContend.Output -ne 'timeout') {
    Write-Error "Sanity check failed: the same user acquired a mutex of an identical name while already holding it (exit $($sameUserContend.ExitCode), output '$($sameUserContend.Output)') — the probe does not actually contend, so the cross-user isolation check below would be meaningless."
    exit 1
}

Write-Host 'Same-user mutex contention confirmed (identical name, same user, second attempt correctly timed out).'

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
    # Both exception types are recognized because .NET's documented behavior for a CurrentUserOnly
    # owner/DACL mismatch is WaitHandleCannotBeOpenedException, while the actual OS-level access
    # denial observed empirically in this CI job's own runs is UnauthorizedAccessException — the
    # exact type can depend on exactly which layer detects the mismatch first.
    $accessDeniedIndicators = @(
        'UnauthorizedAccessException', 'WaitHandleCannotBeOpenedException', 'access is denied', 'access denied')
    $diagnostic = $otherUserConnect.StdOut.Trim()
    $isAccessDenied = $accessDeniedIndicators | Where-Object { $diagnostic -like "*$_*" }
    if (-not $isAccessDenied) {
        Write-Error "Isolation check inconclusive: the different-user connect attempt failed (exit $($otherUserConnect.ExitCode)) but the reason ('$diagnostic') is not a recognized access-control failure — it may be an unrelated setup problem (e.g. the account cannot read the probe's own build output) rather than PipeOptions.CurrentUserOnly rejecting the connection. $($otherUserConnect.StdErr)"
        exit 1
    }

    Write-Host "Same-user isolation confirmed: a different local OS user could not connect (reason: $diagnostic)."

    # --- Positive control: prove the new account can construct/acquire a CurrentUserOnly mutex of
    # its OWN, uncontended, before drawing any conclusion from the contended check below. Without
    # this, any setup problem specific to this account (e.g. a Windows privilege it lacks) would
    # read as a "SECURITY REGRESSION" instead of an inconclusive environment problem. This also
    # exercises MutexProjectLease's Global\-first-then-session-scoped-fallback construction under a
    # standard non-admin account for real. ---
    $mutexControlLease = "forge-mutex-isolation-control-$([guid]::NewGuid().ToString('N'))"
    $mutexControl = Invoke-ProcessAsCredential `
        -ArgumentList @($mutexProbeDll, 'acquire', $mutexControlLease, '0', '5') `
        -Credential $credential
    $mutexControlOutput = if ($null -ne $mutexControl.StdOut) { $mutexControl.StdOut.Trim() } else { '' }
    Write-Host "Mutex positive control (uncontended acquire as the new user) exit code: $($mutexControl.ExitCode), output: $mutexControlOutput"
    if ($mutexControl.ExitCode -ne 0 -or $mutexControlOutput -ne 'acquired') {
        Write-Error "Mutex positive control failed: the new local user could not acquire an uncontended CurrentUserOnly mutex at all (exit $($mutexControl.ExitCode), output '$mutexControlOutput'). $($mutexControl.StdErr) The isolation check below cannot distinguish a real access-control problem from this account being unable to use the primitive at all, so it will not run."
        exit 1
    }

    # --- Isolation check: a DIFFERENT local OS user must be DENIED access to a mutex of the
    # identical name the current user already holds — same shape as the pipe check above.
    # CurrentUserOnly restricts a security descriptor on one shared named object; it does not
    # create a separate object per user, so the two are expected to contend, and the current
    # user's holder must win. The credentialed process launch above (positive control) and below
    # can itself take upwards of ten seconds on a loaded CI runner, so the hold has generous
    # margin — an elapsed-time guard still confirms genuine overlap rather than trusting a
    # coincidental race. ---
    $otherLease = "forge-mutex-isolation-other-$([guid]::NewGuid().ToString('N'))"
    $holdSeconds = 40
    $holderStarted = Get-Date
    $currentUserHolderJob = Start-MutexHolder -LeaseName $otherLease -HoldSeconds $holdSeconds
    try {
        Start-Sleep -Seconds 3
        $otherUserMutexAcquire = Invoke-ProcessAsCredential `
            -ArgumentList @($mutexProbeDll, 'acquire', $otherLease, '0', '5') `
            -Credential $credential
        # Measured HERE, not after reaping the holder job below: Receive-Job -Wait blocks until the
        # holder's own script block returns, which does not happen until it finishes sleeping for
        # the full $holdSeconds and releases — so measuring afterward would always show an elapsed
        # time at or beyond $holdSeconds regardless of how fast the credentialed attempt actually
        # ran, making the overlap guard below vacuous.
        $elapsedSinceHolderStarted = ((Get-Date) - $holderStarted).TotalSeconds
    }
    finally {
        # Reclaim the holder job even if the credentialed acquire attempt above throws a
        # terminating error instead of returning, same reasoning as the pipe listener job above.
        $currentUserHolderResult = Receive-Job -Job $currentUserHolderJob -Wait -ErrorAction SilentlyContinue
        Remove-Job -Job $currentUserHolderJob -Force
    }

    Write-Host "Different-user mutex acquire exit code: $($otherUserMutexAcquire.ExitCode)"
    Write-Host "Different-user mutex acquire stdout: $($otherUserMutexAcquire.StdOut)"
    Write-Host "Different-user mutex acquire stderr: $($otherUserMutexAcquire.StdErr)"
    Write-Host "Current-user holder result while the other user attempted: exit=$($currentUserHolderResult.ExitCode) output=$($currentUserHolderResult.Output)"
    Write-Host "Elapsed since the holder started (must stay well under its ${holdSeconds}s hold to prove genuine overlap): $elapsedSinceHolderStarted s"

    if ($currentUserHolderResult.ExitCode -ne 0 -or $currentUserHolderResult.Output -ne 'acquired') {
        Write-Error "Cross-user mutex check invalid: the current user's own holder never acquired the lease (exit $($currentUserHolderResult.ExitCode), output '$($currentUserHolderResult.Output)') — cannot prove isolation against a baseline that never actually held anything."
        exit 1
    }

    if ($elapsedSinceHolderStarted -ge $holdSeconds) {
        Write-Error "Cross-user mutex check inconclusive: the credentialed acquire attempt took $elapsedSinceHolderStarted s, longer than the holder's ${holdSeconds}s hold — the holder may have already released before the other user's attempt ran, so this result would not prove genuine overlap."
        exit 1
    }

    if ($otherUserMutexAcquire.ExitCode -eq 0) {
        Write-Error 'SECURITY REGRESSION: a different local OS user acquired a NamedWaitHandleOptions.CurrentUserOnly mutex of an identical name that the current user already holds.'
        exit 1
    }

    # Same reasoning as the pipe check: the positive control above only proves the account can
    # launch dotnet.exe and use the mutex primitive on its OWN name; require the specific
    # access-denial exception, not merely "some diagnostic text exists", so an unrelated setup
    # failure can't masquerade as isolation.
    $otherDiagnostic = if ($null -ne $otherUserMutexAcquire.StdOut) { $otherUserMutexAcquire.StdOut.Trim() } else { '' }
    $isMutexAccessDenied = $accessDeniedIndicators | Where-Object { $otherDiagnostic -like "*$_*" }
    if (-not $isMutexAccessDenied) {
        Write-Error "Isolation check inconclusive: the different-user mutex acquire attempt failed (exit $($otherUserMutexAcquire.ExitCode)) but the reason ('$otherDiagnostic') is not a recognized access-control failure — it may be an unrelated setup problem rather than CurrentUserOnly denying access. $($otherUserMutexAcquire.StdErr)"
        exit 1
    }

    Write-Host "Mutex isolation confirmed: a different local OS user could not acquire a mutex of the identical name the current user already holds (reason: $otherDiagnostic)."
}
finally {
    Remove-LocalUser -Name $userName -ErrorAction SilentlyContinue
}
