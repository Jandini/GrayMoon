# GrayMoon Worker Installation Script
# Downloads the worker and delegates all service management to graymoon-worker.exe.
#
# Run from a fresh Administrator PowerShell window.
# The host must have the .NET 10 runtime installed.

$ErrorActionPreference = 'Stop'

Write-Host 'GrayMoon Worker Installation' -ForegroundColor Cyan

# Check if running as Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator
)

if (-not $isAdmin) {
    Write-Host 'ERROR: This script must be run as Administrator' -ForegroundColor Red
    return 1
}

$serviceName = 'GrayMoonWorker'
$legacyServiceName = 'GrayMoonAgent'
$agentPath   = Join-Path $env:ProgramFiles 'GrayMoon'
$agentExe    = Join-Path $agentPath 'graymoon-worker.exe'
$downloadUrl = '{DOWNLOAD_URL}'
$hubUrl      = '{HUB_URL}'
$zipPath     = Join-Path $env:TEMP 'graymoon-worker-windows-install.zip'

# Stop any running service before replacing files so the executable is not locked.
foreach ($name in @($serviceName, $legacyServiceName)) {
    $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -eq 'Running') {
        Write-Host "Stopping running service '$name'..." -ForegroundColor Yellow
        Stop-Service -Name $name -Force -ErrorAction Stop | Out-Null
        Start-Sleep -Seconds 2
        Write-Host "Service '$name' stopped." -ForegroundColor Green
    }
}

# Prepare installation directory.
Write-Host 'Preparing installation directory...' -ForegroundColor Yellow
if (Test-Path -LiteralPath $agentPath) {
    Get-ChildItem -LiteralPath $agentPath -Force | Remove-Item -Recurse -Force -ErrorAction Stop
} else {
    New-Item -ItemType Directory -Path $agentPath -Force | Out-Null
}

# Download worker archive.
Write-Host 'Downloading worker from {BASE_URL}...' -ForegroundColor Yellow
(New-Object System.Net.WebClient).DownloadFile($downloadUrl, $zipPath)
Write-Host 'Download completed.' -ForegroundColor Green

# Extract worker.
Write-Host 'Extracting worker...' -ForegroundColor Yellow
Expand-Archive -LiteralPath $zipPath -DestinationPath $agentPath -Force
Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue

if (-not (Test-Path -LiteralPath $agentExe)) {
    Write-Host "ERROR: graymoon-worker.exe not found under $agentPath after extract. Install .NET 10 Runtime if the app fails to start." -ForegroundColor Red
    return 1
}

# Delegate all service management (create/update, rights grant, start) to the worker.
Write-Host 'Installing service...' -ForegroundColor Yellow
& $agentExe install --hub-url $hubUrl
if ($LASTEXITCODE -ne 0) {
    Write-Host "Installation failed. Correct any errors above and run the script again." -ForegroundColor Red
    return
}
Write-Host ''
Write-Host 'Installation completed!' -ForegroundColor Green
Write-Host ''
