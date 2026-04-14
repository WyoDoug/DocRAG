[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallDirectory,

    [string]$ProjectPath = (Join-Path $PSScriptRoot 'DocRAG.Mcp.csproj'),
    [string]$ServiceName = 'DocRAGMcp',
    [string]$DisplayName = 'DocRAG MCP',
    [string]$Description = 'DocRAG MCP server',
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
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

function Get-ResolvedFilePath
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

    if ($item.PSIsContainer)
    {
        throw "$Label is not a file: $Path"
    }

    $res = $item.FullName
    return $res
}

function Get-ResolvedDirectoryPath
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    New-Item -ItemType Directory -Force -Path $Path | Out-Null
    $item = Get-Item -LiteralPath $Path
    $res = $item.FullName
    return $res
}

function Stop-ServiceIfRunning
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $existingService = Get-Service -Name $Name -ErrorAction SilentlyContinue

    if ($null -ne $existingService)
    {
        if ($existingService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped)
        {
            if ($PSCmdlet.ShouldProcess($Name, 'Stop Windows service'))
            {
                Stop-Service -Name $Name -Force
            }
        }

        if ($PSCmdlet.ShouldProcess($Name, 'Remove Windows service'))
        {
            Remove-Service -Name $Name
        }
    }
}

if (-not (Test-Administrator))
{
    throw 'Run this script from an elevated PowerShell session.'
}

$resolvedProjectPath = Get-ResolvedFilePath -Path $ProjectPath -Label 'ProjectPath'
$resolvedInstallDirectory = Get-ResolvedDirectoryPath -Path $InstallDirectory

Stop-ServiceIfRunning -Name $ServiceName

if ($PSCmdlet.ShouldProcess($resolvedProjectPath, "Publish to $resolvedInstallDirectory"))
{
    & dotnet publish $resolvedProjectPath -c $Configuration -p:Platform=$Platform -o $resolvedInstallDirectory
}

$serviceExePath = Join-Path $resolvedInstallDirectory 'DocRAG.Mcp.exe'

if (-not (Test-Path -LiteralPath $serviceExePath))
{
    throw "Published executable not found: $serviceExePath"
}

$serviceBinaryPath = '"' + $serviceExePath + '"'

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

Write-Output "ProjectPath: $resolvedProjectPath"
Write-Output "ServiceName: $ServiceName"
Write-Output "InstallDirectory: $resolvedInstallDirectory"
Write-Output "Status: $($service.Status)"
Write-Output 'HealthUrl: http://localhost:6100/health'
Write-Output 'McpUrl: http://localhost:6100/mcp'