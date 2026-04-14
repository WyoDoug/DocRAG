[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ServiceName = 'DocRAGMcp',
    [string]$DisplayName = 'DocRAG MCP',
    [string]$Description = 'DocRAG MCP server',
    [string]$PublishDirectory = (Join-Path $PSScriptRoot 'bin\x64\Release\net10.0\win-x64\publish'),
    [string]$InstallDirectory = (Join-Path $env:ProgramFiles 'DocRAG\DocRAG.Mcp'),
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

if (-not (Test-Administrator))
{
    throw 'Run this script from an elevated PowerShell session.'
}

$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($null -ne $existingService)
{
    throw "Service '$ServiceName' already exists. Use update-service.ps1 or uninstall-service.ps1 first."
}

$resolvedPublishDirectory = Get-ResolvedDirectory -Path $PublishDirectory -Label 'PublishDirectory'
$sourceExePath = Join-Path $resolvedPublishDirectory 'DocRAG.Mcp.exe'

if (-not (Test-Path -LiteralPath $sourceExePath))
{
    throw "Published executable not found: $sourceExePath"
}

$targetDirectory = $resolvedPublishDirectory

if (-not $InPlace)
{
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

$serviceExePath = Join-Path $targetDirectory 'DocRAG.Mcp.exe'

if (-not (Test-Path -LiteralPath $serviceExePath))
{
    throw "Service executable not found: $serviceExePath"
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

Write-Output "ServiceName: $ServiceName"
Write-Output "InstallDirectory: $targetDirectory"
Write-Output "Status: $($service.Status)"
Write-Output 'HealthUrl: http://localhost:6100/health'
Write-Output 'McpUrl: http://localhost:6100/mcp'