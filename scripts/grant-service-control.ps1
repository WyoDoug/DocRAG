# scripts/grant-service-control.ps1
#
# One-time setup: grant the current Windows user the right to start, stop,
# and query the DocRAGMcp service WITHOUT needing UAC elevation. After
# running this once, the dev-loop tasks (Stop/Start/Restart Service) work
# from a non-elevated VS Code session.
#
# Mechanism: extends the service's discretionary access control list (DACL)
# with an ACE granting the user SERVICE_START + SERVICE_STOP +
# SERVICE_INTERROGATE + READ_CONTROL. Other access (delete, change config,
# read security descriptor) still requires admin.
#
# This script self-elevates: if you launch it without admin, it triggers a
# single UAC prompt, runs elevated, then exits.
#
# Caveats:
#   - The SDDL change persists across reboots and service restarts.
#   - An MSI upgrade that re-registers the service may reset the ACL.
#     Re-run this script after upgrading if the dev tasks start failing
#     with "Cannot open service".

[CmdletBinding()]
param(
    [string]$UserName = $env:USERNAME,
    [string]$ServiceName = 'DocRAGMcp'
)

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
$isAdmin = $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "grant: Not elevated. Re-launching with UAC..."
    $argList = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $PSCommandPath,
        '-UserName', $UserName,
        '-ServiceName', $ServiceName
    )
    try {
        $proc = Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -Verb RunAs -PassThru -Wait -ErrorAction Stop
        Write-Host "grant: Elevated process exited with code $($proc.ExitCode)."
        exit $proc.ExitCode
    }
    catch {
        Write-Host "grant: Elevation declined or failed: $($_.Exception.Message)"
        exit 1
    }
}

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

# RP = start, WP = stop, LO = interrogate, RC = read security descriptor.
# Granting these gives the user enough rights to run Get-Service / Start-Service
# / Stop-Service. Anything more dangerous (delete, change config) still needs admin.
$ourAce = "(A;;RPWPLORC;;;$sid)"

if ($existingSddl -match $([regex]::Escape($ourAce))) {
    Write-Host "grant: ACE already present for SID=$sid. No change needed."
    exit 0
}

# Insert our ACE inside the DACL portion, just before any audit (S:) section.
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
    Write-Host "grant: Verify by running, in a NEW non-admin shell: scripts\dev.ps1 stop"
}
else {
    Write-Host "grant: sc.exe sdset failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}
