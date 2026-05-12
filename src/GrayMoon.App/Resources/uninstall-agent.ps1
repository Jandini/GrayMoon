# GrayMoon Agent Uninstallation Script
# This script uninstalls the GrayMoon Agent Windows service

$ErrorActionPreference = 'Stop'

Write-Host 'GrayMoon Agent Uninstallation' -ForegroundColor Cyan

# Check if running as Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'ERROR: This script must be run as Administrator' -ForegroundColor Red
    return 1
}

# Service name
$serviceName = 'GrayMoonAgent'

Write-Host 'Checking for service...' -ForegroundColor Yellow
$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

if ($existingService) {
    Write-Host 'Found service. Stopping...' -ForegroundColor Yellow
    if ($existingService.Status -eq 'Running') {
        Stop-Service -Name $serviceName -Force -ErrorAction Stop | Out-Null
        Start-Sleep -Seconds 2
    }
    
    # Remove service using PowerShell native cmdlet
    Write-Host 'Removing service...' -ForegroundColor Yellow
    try {
        $service = Get-CimInstance -ClassName Win32_Service -Filter "name='$serviceName'" -ErrorAction Stop
        Remove-CimInstance -InputObject $service -ErrorAction Stop | Out-Null
        Write-Host 'Service removed successfully.' -ForegroundColor Green
    } catch {
        Write-Host "ERROR: Failed to remove service: $_" -ForegroundColor Red
        return 1
    }
} else {
    Write-Host 'Service not found.' -ForegroundColor Yellow
}

$agentInstallPath = Join-Path $env:ProgramFiles 'GrayMoon'
if (Test-Path -Path $agentInstallPath) {
    Write-Host 'Removing installation directory...' -ForegroundColor Yellow
    Remove-Item -Path $agentInstallPath -Recurse -Force -ErrorAction Stop
    Write-Host 'Installation directory removed.' -ForegroundColor Green
}

$legacyAgentExe = Join-Path $env:ProgramData 'GrayMoon\graymoon-agent.exe'
if (Test-Path -Path $legacyAgentExe) {
    Write-Host 'Removing legacy agent executable...' -ForegroundColor Yellow
    Remove-Item -Path $legacyAgentExe -Force -ErrorAction Stop
    Write-Host 'Legacy executable removed.' -ForegroundColor Green
}

Write-Host ''
Write-Host 'Uninstallation completed!' -ForegroundColor Green
Write-Host ''
