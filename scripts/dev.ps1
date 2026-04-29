# scripts/dev.ps1
# Dev-loop helper for SaddleRAG. Wraps the common operations the IDE / terminal
# wants: stop the installed Windows service, start it, check health, tail
# logs. Tolerant of the service not being installed - useful for fresh
# checkouts where you have not run the MSI yet.
#
# Service stop/start requires admin. Run VS Code (or the terminal) elevated,
# or set up a one-time `sc.exe sdset` delegation if you prefer non-admin.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('stop', 'start', 'restart', 'status', 'health', 'logs')]
    [string]$Command
)

$ServiceName = 'SaddleRAGMcp'
$HealthUrl = 'http://localhost:6100/health'
$LogDir = Join-Path $env:LOCALAPPDATA 'SaddleRAG\logs'

function Write-ElevationHint {
    param([string]$Operation)
    Write-Host ""
    Write-Host "dev: $Operation failed: access denied."
    Write-Host "dev: This needs admin OR a one-time grant. To avoid UAC every time, run the"
    Write-Host "dev: 'SaddleRAG: Grant Service Control (one-time, requires UAC)' task once. After that,"
    Write-Host "dev: stop/start work non-elevated."
}

function Stop-SaddleRagService {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $svc) {
        Write-Host "dev: Service '$ServiceName' is not installed; nothing to stop."
        return
    }
    if ($svc.Status -eq 'Running') {
        Write-Host "dev: Stopping $ServiceName..."
        try {
            Stop-Service -Name $ServiceName -ErrorAction Stop
            Write-Host "dev: Stopped."
        }
        catch [System.ServiceProcess.TimeoutException] {
            Write-Host "dev: Stop timed out; service may still be shutting down."
            exit 1
        }
        catch {
            Write-ElevationHint -Operation 'Stop'
            exit 1
        }
    }
    else {
        Write-Host "dev: Service '$ServiceName' is already $($svc.Status)."
    }
}

function Start-SaddleRagService {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $svc) {
        Write-Host "dev: Service '$ServiceName' is not installed; cannot start."
        return
    }
    if ($svc.Status -ne 'Running') {
        Write-Host "dev: Starting $ServiceName..."
        try {
            Start-Service -Name $ServiceName -ErrorAction Stop
            Write-Host "dev: Started."
        }
        catch {
            Write-ElevationHint -Operation 'Start'
            exit 1
        }
    }
    else {
        Write-Host "dev: Service '$ServiceName' is already running."
    }
}

function Get-SaddleRagStatus {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $svc) {
        Write-Host "dev: Service '$ServiceName' is not installed."
    }
    else {
        Write-Host "dev: Service: $($svc.Name) - Status: $($svc.Status), StartType: $($svc.StartType)"
    }
    Test-SaddleRagHealth
}

function Test-SaddleRagHealth {
    try {
        $resp = Invoke-WebRequest -Uri $HealthUrl -TimeoutSec 5 -UseBasicParsing
        Write-Host "dev: Health: $($resp.StatusCode) $($resp.StatusDescription)"
        Write-Host $resp.Content
    }
    catch {
        Write-Host "dev: Health endpoint unreachable: $($_.Exception.Message)"
    }
}

function Tail-SaddleRagLogs {
    if (-not (Test-Path $LogDir)) {
        Write-Host "dev: Log directory does not exist: $LogDir"
        return
    }
    $latest = Get-ChildItem $LogDir -Filter 'saddlerag-*.log' |
              Sort-Object LastWriteTime -Descending |
              Select-Object -First 1
    if ($null -eq $latest) {
        Write-Host "dev: No logs found in $LogDir"
        return
    }
    Write-Host "dev: Tailing $($latest.FullName) (Ctrl+C to stop)..."
    Get-Content -Path $latest.FullName -Wait -Tail 50
}

switch ($Command) {
    'stop'    { Stop-SaddleRagService }
    'start'   { Start-SaddleRagService }
    'restart' { Stop-SaddleRagService; Start-SaddleRagService }
    'status'  { Get-SaddleRagStatus }
    'health'  { Test-SaddleRagHealth }
    'logs'    { Tail-SaddleRagLogs }
}
