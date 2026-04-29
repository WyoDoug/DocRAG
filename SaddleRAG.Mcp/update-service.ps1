[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ServiceName = 'SaddleRAGMcp',
    [string]$DisplayName = 'SaddleRAG MCP',
    [string]$Description = 'SaddleRAG MCP server',
    [string]$PublishDirectory = (Join-Path $PSScriptRoot 'bin\x64\Release\net10.0\win-x64\publish'),
    [string]$InstallDirectory,
    [switch]$InPlace,
    [switch]$SkipStart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-Administrator
{
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    $res = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    return $res
}

function Get-ResolvedDirectory
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path))
    {
        throw "$Label does not exist: $Path"
    }

    $item = Get-Item -LiteralPath $Path

    if (-not $item.PSIsContainer)
    {
        throw "$Label is not a directory: $Path"
    }

    $res = $item.FullName
    return $res
}

function Get-ServiceExecutablePath
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $service = Get-CimInstance Win32_Service -Filter "Name = '$Name'"

    if ($null -eq $service)
    {
        throw "Service '$Name' was not found."
    }

    $pathName = $service.PathName.Trim()
    $res = $pathName

    if ($pathName.StartsWith('"'))
    {
        $closingQuoteIndex = $pathName.IndexOf('"', 1)

        if ($closingQuoteIndex -lt 0)
        {
            throw "Unable to parse service executable path: $pathName"
        }

        $res = $pathName.Substring(1, $closingQuoteIndex - 1)
    }
    else
    {
        $firstSpaceIndex = $pathName.IndexOf(' ')

        if ($firstSpaceIndex -ge 0)
        {
            $res = $pathName.Substring(0, $firstSpaceIndex)
        }
    }

    return $res
}

if (-not (Test-Administrator))
{
    throw 'Run this script from an elevated PowerShell session.'
}

$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($null -eq $existingService)
{
    throw "Service '$ServiceName' was not found. Run install-service.ps1 first."
}

$resolvedPublishDirectory = Get-ResolvedDirectory -Path $PublishDirectory -Label 'PublishDirectory'
$sourceExePath = Join-Path $resolvedPublishDirectory 'SaddleRAG.Mcp.exe'

if (-not (Test-Path -LiteralPath $sourceExePath))
{
    throw "Published executable not found: $sourceExePath"
}

$targetDirectory = $resolvedPublishDirectory

if (-not $InPlace)
{
    if ([string]::IsNullOrWhiteSpace($InstallDirectory))
    {
        $existingExePath = Get-ServiceExecutablePath -Name $ServiceName
        $InstallDirectory = Split-Path -Path $existingExePath -Parent
    }

    if ($PSCmdlet.ShouldProcess($InstallDirectory, 'Create install directory'))
    {
        New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
    }

    if ($PSCmdlet.ShouldProcess($InstallDirectory, "Copy published files from $resolvedPublishDirectory"))
    {
        Copy-Item -Path (Join-Path $resolvedPublishDirectory '*') -Destination $InstallDirectory -Recurse -Force
    }

    $targetDirectory = Get-ResolvedDirectory -Path $InstallDirectory -Label 'InstallDirectory'
}

$serviceExePath = Join-Path $targetDirectory 'SaddleRAG.Mcp.exe'

if (-not (Test-Path -LiteralPath $serviceExePath))
{
    throw "Service executable not found: $serviceExePath"
}

$serviceBinaryPath = '"' + $serviceExePath + '"'

if ($existingService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped)
{
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Stop Windows service'))
    {
        Stop-Service -Name $ServiceName -Force
    }
}

if ($PSCmdlet.ShouldProcess($ServiceName, 'Remove Windows service'))
{
    Remove-Service -Name $ServiceName
}

if ($PSCmdlet.ShouldProcess($ServiceName, "Create Windows service using $serviceExePath"))
{
    New-Service -Name $ServiceName -BinaryPathName $serviceBinaryPath -DisplayName $DisplayName -Description $Description -StartupType Automatic
}

if (-not $SkipStart)
{
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Start Windows service'))
    {
        Start-Service -Name $ServiceName
    }
}

$service = Get-Service -Name $ServiceName

Write-Output "ServiceName: $ServiceName"
Write-Output "InstallDirectory: $targetDirectory"
Write-Output "Status: $($service.Status)"
Write-Output 'HealthUrl: http://localhost:6100/health'
Write-Output 'McpUrl: http://localhost:6100/mcp'