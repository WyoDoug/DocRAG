# scripts/grant-service-control.ps1
#
# One-time setup: grant the current Windows user the right to start, stop,
# and query the DocRAGMcp service WITHOUT needing UAC elevation. After
# running this once, the dev-loop tasks (Stop / Start / Restart Service)
# work from a non-elevated VS Code session.
#
# Mechanism: extends the service's discretionary access control list (DACL)
# with an ACE granting the user SERVICE_START + SERVICE_STOP +
# SERVICE_INTERROGATE + READ_CONTROL. Other access (delete, change config,
# read security descriptor) still requires admin.
#
# This script self-elevates: if you launch it without admin, it triggers a
# single UAC prompt, runs elevated, exits, and the parent (non-elevated)
# prints the elevated step's output so you see what happened in the VS Code
# task panel.
#
# Use -Verify to print just the current SDDL state without modifying anything.
#
# Caveats:
#   - The SDDL change persists across reboots and service restarts.
#   - An MSI upgrade that re-registers the service may reset the ACL.
#     Re-run this script after upgrading if the dev tasks start failing
#     with "Cannot open service".

[CmdletBinding()]
param(
    [string]$UserName = $env:USERNAME,
    [string]$ServiceName = 'DocRAGMcp',
    [switch]$Verify
)

function Get-CurrentShellSid {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    return $identity.User.Value
}

function Show-VerifyState {
    param([string]$ServiceName, [string]$Sid)

    Write-Host "verify: Service:   $ServiceName"
    Write-Host "verify: Shell SID: $Sid"

    $sddl = (& sc.exe sdshow $ServiceName | Out-String).Trim()
    Write-Host "verify: SDDL:      $sddl"

    if ($sddl -like "*$Sid*") {
        $matches = [regex]::Matches($sddl, "\(A;[^;]*;([A-Z]+);[^;]*;[^;]*;$([regex]::Escape($Sid))\)")
        if ($matches.Count -gt 0) {
            $rights = $matches | ForEach-Object { $_.Groups[1].Value }
            Write-Host "verify: GRANT PRESENT for this SID. Rights: $($rights -join ', ')"
            return $true
        }
    }
    Write-Host "verify: NO GRANT for this SID. Run the task without -Verify to apply it."
    return $false
}

$shellSid = Get-CurrentShellSid

if ($Verify) {
    [void](Show-VerifyState -ServiceName $ServiceName -Sid $shellSid)
    exit 0
}

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
$isAdmin = $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "grant: Not elevated. Re-launching with UAC and capturing output..."

    $logFile = Join-Path $env:TEMP "docrag-grant-service-$(Get-Date -Format 'yyyyMMddHHmmss').log"
    if (Test-Path $logFile) { Remove-Item $logFile -Force }

    # The elevated child writes its full transcript to $logFile so the
    # parent can replay it. Without this, the elevated console window
    # flashes and closes before the user can read anything.
    $innerCmd = @"
Start-Transcript -Path '$logFile' -Force | Out-Null
& '$PSCommandPath' -UserName '$UserName' -ServiceName '$ServiceName'
`$inner = `$LASTEXITCODE
Stop-Transcript | Out-Null
exit `$inner
"@

    try {
        $proc = Start-Process -FilePath 'powershell.exe' `
            -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-Command',$innerCmd `
            -Verb RunAs -PassThru -Wait -ErrorAction Stop
        Write-Host ""
        Write-Host "--- elevated transcript ($logFile) ---"
        if (Test-Path $logFile) {
            Get-Content $logFile
        }
        else {
            Write-Host "(no transcript captured)"
        }
        Write-Host "--- end transcript (exit $($proc.ExitCode)) ---"
        Write-Host ""
        if ($proc.ExitCode -eq 0) {
            Show-VerifyState -ServiceName $ServiceName -Sid $shellSid | Out-Null
        }
        exit $proc.ExitCode
    }
    catch {
        Write-Host "grant: Elevation declined or failed: $($_.Exception.Message)"
        Write-Host "grant: If you cancelled the UAC prompt, re-run the task and click Yes."
        exit 1
    }
}

# --- elevated path below ---

Write-Host "grant: Running elevated as $($identity.Name)."

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -eq $svc) {
    Write-Host "grant: Service '$ServiceName' is not installed. Nothing to do."
    exit 1
}

try {
    $account = New-Object System.Security.Principal.NTAccount($UserName)
    $sid = $account.Translate([System.Security.Principal.SecurityIdentifier]).Value
}
catch {
    Write-Host "grant: Could not resolve user '$UserName' to a SID: $($_.Exception.Message)"
    exit 1
}

Write-Host "grant: Target user '$UserName' SID=$sid"

$existingSddl = (& sc.exe sdshow $ServiceName | Out-String).Trim()
if (-not $existingSddl -or $existingSddl -notmatch '^D:') {
    Write-Host "grant: Failed to read SDDL for '$ServiceName'. sc.exe output:"
    Write-Host $existingSddl
    exit 1
}
Write-Host "grant: Current SDDL: $existingSddl"

$ourAce = "(A;;RPWPLORC;;;$sid)"

if ($existingSddl -match $([regex]::Escape($ourAce))) {
    Write-Host "grant: ACE already present for SID=$sid. No change needed."
    exit 0
}

if ($existingSddl -match '^(D:[^S]*)(S:.*)?$') {
    $daclPart = $matches[1]
    $saclPart = $matches[2]
    $newSddl = "$daclPart$ourAce$saclPart"
}
else {
    Write-Host "grant: Unexpected SDDL format; refusing to modify."
    exit 1
}

Write-Host "grant: New SDDL:     $newSddl"

$output = & sc.exe sdset $ServiceName $newSddl 2>&1
Write-Host $output

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "grant: Success. '$UserName' can now start/stop '$ServiceName' without elevation."
    Write-Host "grant: Verify with: scripts\dev.ps1 stop  (in a NEW non-admin shell)"
}
else {
    Write-Host "grant: sc.exe sdset failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}
