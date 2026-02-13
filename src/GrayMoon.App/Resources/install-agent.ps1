# GrayMoon Agent Installation Script
# This script downloads and installs the GrayMoon Agent as a Windows service

$ErrorActionPreference = 'Stop'

Write-Host 'GrayMoon Agent Installation' -ForegroundColor Cyan
Write-Host '============================' -ForegroundColor Cyan
Write-Host ''

# Check if running as Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'ERROR: This script must be run as Administrator' -ForegroundColor Red
    Read-Host -Prompt 'Press Enter to close this window'
    return 1
}

# Service name
$serviceName = 'GrayMoonAgent'
$serviceDisplayName = 'GrayMoon Agent'
$serviceDescription = 'Host-side agent for GrayMoon: executes git and repository I/O operations'
$agentPath = Join-Path $env:ProgramData 'GrayMoon'
$agentExe = Join-Path $agentPath 'graymoon-agent.exe'
$downloadUrl = '{DOWNLOAD_URL}'

Write-Host 'Checking for existing service...' -ForegroundColor Yellow
$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

if ($existingService) {
    Write-Host 'Found existing service. Stopping...' -ForegroundColor Yellow
    if ($existingService.Status -eq 'Running') {
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    
    # Remove existing service
    $service = Get-WmiObject -Class Win32_Service -Filter "name='$serviceName'"
    if ($service) {
        $service.Delete()
        Write-Host 'Removed existing service.' -ForegroundColor Green
    }
}

# Create directory
Write-Host 'Creating installation directory...' -ForegroundColor Yellow
if (-not (Test-Path $agentPath)) {
    New-Item -ItemType Directory -Path $agentPath -Force | Out-Null
}

# Download agent
Write-Host "Downloading agent from {BASE_URL}..." -ForegroundColor Yellow
try {
    $webClient = New-Object System.Net.WebClient
    $webClient.DownloadFile($downloadUrl, $agentExe)
    Write-Host 'Download completed.' -ForegroundColor Green
} catch {
    Write-Host "ERROR: Failed to download agent: $_" -ForegroundColor Red
    Read-Host -Prompt 'Press Enter to close this window'
    exit 1
}

# Install as Windows service
Write-Host 'Installing as Windows service...' -ForegroundColor Yellow
try {
    $serviceArgs = @{
        Name = $serviceName
        BinaryPathName = "`"$agentExe`" run"
        DisplayName = $serviceDisplayName
        Description = $serviceDescription
        StartType = 'Automatic'
    }
    
    New-Service @serviceArgs -ErrorAction Stop | Out-Null
    Write-Host 'Service installed successfully.' -ForegroundColor Green
} catch {
    Write-Host "ERROR: Failed to install service: $_" -ForegroundColor Red
    Read-Host -Prompt 'Press Enter to close this window'
    exit 1
}

# Start service
Write-Host 'Starting service...' -ForegroundColor Yellow
try {
    Start-Service -Name $serviceName -ErrorAction Stop
    Write-Host 'Service started successfully.' -ForegroundColor Green
} catch {
    Write-Host "WARNING: Failed to start service: $_" -ForegroundColor Yellow
    Write-Host 'You may need to start it manually using: Start-Service -Name GrayMoonAgent' -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Installation completed!' -ForegroundColor Green
Write-Host 'Service Name: GrayMoonAgent' -ForegroundColor Cyan
Write-Host 'Service Path: ' $agentExe -ForegroundColor Cyan
Write-Host ''
Read-Host -Prompt 'Press Enter to close this window'
