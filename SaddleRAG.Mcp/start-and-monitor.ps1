[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$ServiceName,
    [Parameter(Mandatory)] [string]$HealthUrl,
    [Parameter(Mandatory)] [string]$BinaryPath,
    [string]$DisplayName = 'SaddleRAG MCP Server',
    [string]$Description = 'Documentation RAG system - MCP server for AI-assisted code documentation lookup.',
    [int]$TotalTimeoutSec = 300,
    [int]$PollIntervalSec = 2,
    [int]$HealthRequestTimeoutSec = 5,
    [int]$MaxStartAttempts = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

function Write-Stamp
{
    param(
        [string]$Message,
        [System.Diagnostics.Stopwatch]$Sw
    )

    $elapsed = $Sw.Elapsed.ToString('mm\:ss')
    Write-Host "[$elapsed] $Message"
}

function Test-HealthEndpoint
{
    param(
        [string]$Url,
        [int]$TimeoutSec
    )

    $res = $false
    try
    {
        $resp = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec $TimeoutSec
        $res = ($resp.StatusCode -eq 200)
    }
    catch
    {
    }

    return $res
}

function Register-SaddleRagService
{
    param(
        [string]$Name,
        [string]$BinPath,
        [string]$Display,
        [string]$Desc,
        [System.Diagnostics.Stopwatch]$Sw
    )

    $existing = Get-Service -Name $Name -ErrorAction SilentlyContinue

    if ($null -ne $existing)
    {
        Write-Stamp "Service '$Name' already registered; reusing existing registration." $Sw
    }
    else
    {
        if (-not (Test-Path -LiteralPath $BinPath))
        {
            Write-Stamp "Binary path does not exist: $BinPath" $Sw
            throw "Binary not found at $BinPath"
        }

        Write-Stamp "Registering '$Name' via New-Service (Auto, LocalSystem) -> $BinPath" $Sw
        New-Service -Name $Name -BinaryPathName ('"' + $BinPath + '"') -DisplayName $Display -Description $Desc -StartupType Automatic | Out-Null
        Write-Stamp "Service '$Name' registered." $Sw
    }
}

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$startAttempts = 0
$lastStatus = ''
$healthy = $false
$exitCode = 1

Write-Stamp "Starting '$ServiceName' and polling '$HealthUrl' (overall timeout ${TotalTimeoutSec}s, max ${MaxStartAttempts} start attempts)" $sw

try
{
    Register-SaddleRagService -Name $ServiceName -BinPath $BinaryPath -Display $DisplayName -Desc $Description -Sw $sw
}
catch
{
    Write-Stamp "Service registration failed: $($_.Exception.Message)" $sw
    exit 1
}

while (-not $healthy -and $sw.Elapsed.TotalSeconds -lt $TotalTimeoutSec)
{
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

    if ($null -eq $svc)
    {
        Write-Stamp "Service '$ServiceName' is not registered in SCM. Aborting." $sw
        break
    }

    $status = $svc.Status.ToString()

    if ($status -ne $lastStatus)
    {
        Write-Stamp "Service status: $status (attempts so far: $startAttempts)" $sw
        $lastStatus = $status
    }

    if ($status -eq 'Stopped' -and $startAttempts -lt $MaxStartAttempts)
    {
        $startAttempts++
        Write-Stamp "Issuing Start-Service (attempt ${startAttempts} of ${MaxStartAttempts})" $sw
        try
        {
            Start-Service -Name $ServiceName -ErrorAction Stop
        }
        catch
        {
            Write-Stamp "Start-Service raised: $($_.Exception.Message)" $sw
        }
    }

    if ($status -eq 'Running')
    {
        $healthy = Test-HealthEndpoint -Url $HealthUrl -TimeoutSec $HealthRequestTimeoutSec
    }

    if ($status -eq 'Stopped' -and $startAttempts -ge $MaxStartAttempts)
    {
        Write-Stamp "Exceeded max start attempts (${MaxStartAttempts}); service kept transitioning to Stopped." $sw
        break
    }

    if (-not $healthy)
    {
        Start-Sleep -Seconds $PollIntervalSec
    }
}

if ($healthy)
{
    Write-Stamp "Service is healthy after $($sw.Elapsed.TotalSeconds.ToString('F1'))s ($startAttempts start attempt(s))" $sw
    $exitCode = 0
}
else
{
    Write-Stamp "Service did not become healthy within ${TotalTimeoutSec}s. Last status: '$lastStatus', start attempts: $startAttempts. Install will roll back." $sw
}

exit $exitCode
