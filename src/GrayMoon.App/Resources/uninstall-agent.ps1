# GrayMoon Agent Uninstallation Script
# This script uninstalls the GrayMoon Agent Windows service

$ErrorActionPreference = 'Stop'

Write-Host 'GrayMoon Agent Uninstallation' -ForegroundColor Cyan
Write-Host '================================' -ForegroundColor Cyan
Write-Host ''

# Check if running as Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'ERROR: This script must be run as Administrator' -ForegroundColor Red
    return 1
}

# Service name
$serviceName = 'GrayMoonAgent'
$agentPath = Join-Path $env:ProgramData 'GrayMoon'

Write-Host 'Checking for service...' -ForegroundColor Yellow
$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

if ($existingService) {
    Write-Host 'Found service. Stopping...' -ForegroundColor Yellow
    if ($existingService.Status -eq 'Running') {
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    
    # Remove service
    Write-Host 'Removing service...' -ForegroundColor Yellow
    try {
        $result = sc.exe delete $serviceName
        if (${LASTEXITCODE} -ne 0) {
            throw "sc.exe delete failed with exit code ${LASTEXITCODE}: $result"
        }
        Write-Host 'Service removed successfully.' -ForegroundColor Green
    } catch {
        Write-Host "ERROR: Failed to remove service: $_" -ForegroundColor Red
        return 1
    }
} else {
    Write-Host 'Service not found.' -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Uninstallation completed!' -ForegroundColor Green
Write-Host ''
