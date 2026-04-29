[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ServiceName = 'SaddleRAGMcp',
    [switch]$RemoveFiles
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

function Get-ServiceExecutablePath
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $service = Get-CimInstance Win32_Service -Filter "Name = '$Name'"
    $res = $null

    if ($null -ne $service)
    {
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
    throw "Service '$ServiceName' was not found."
}

$existingExePath = Get-ServiceExecutablePath -Name $ServiceName
$existingInstallDirectory = $null

if (-not [string]::IsNullOrWhiteSpace($existingExePath))
{
    $existingInstallDirectory = Split-Path -Path $existingExePath -Parent
}

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

if ($RemoveFiles -and -not [string]::IsNullOrWhiteSpace($existingInstallDirectory) -and (Test-Path -LiteralPath $existingInstallDirectory))
{
    if ($PSCmdlet.ShouldProcess($existingInstallDirectory, 'Remove installed files'))
    {
        Remove-Item -LiteralPath $existingInstallDirectory -Recurse -Force
    }
}

Write-Output "ServiceName: $ServiceName"
Write-Output 'Status: Removed'

if (-not [string]::IsNullOrWhiteSpace($existingInstallDirectory))
{
    Write-Output "InstallDirectory: $existingInstallDirectory"
}