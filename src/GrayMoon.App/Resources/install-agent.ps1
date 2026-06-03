# GrayMoon Agent Installation Script
# Downloads the agent and delegates all service management to graymoon-agent.exe.
#
# Run from a fresh Administrator PowerShell window.
# The host must have the .NET 10 runtime installed.

$ErrorActionPreference = 'Stop'

Write-Host 'GrayMoon Agent Installation' -ForegroundColor Cyan

# Check if running as Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator
)

if (-not $isAdmin) {
    Write-Host 'ERROR: This script must be run as Administrator' -ForegroundColor Red
    return 1
}

$serviceName = 'GrayMoonAgent'
$agentPath   = Join-Path $env:ProgramFiles 'GrayMoon'
$agentExe    = Join-Path $agentPath 'graymoon-agent.exe'
$downloadUrl = '{DOWNLOAD_URL}'
$hubUrl      = '{HUB_URL}'
$zipPath     = Join-Path $env:TEMP 'graymoon-agent-windows-install.zip'

# Stop the service before replacing files so the executable is not locked.
$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -eq 'Running') {
    Write-Host 'Stopping running service...' -ForegroundColor Yellow
    Stop-Service -Name $serviceName -Force -ErrorAction Stop | Out-Null
    Start-Sleep -Seconds 2
    Write-Host 'Service stopped.' -ForegroundColor Green
}

# Prepare installation directory.
Write-Host 'Preparing installation directory...' -ForegroundColor Yellow
if (Test-Path -LiteralPath $agentPath) {
    Get-ChildItem -LiteralPath $agentPath -Force | Remove-Item -Recurse -Force -ErrorAction Stop
} else {
    New-Item -ItemType Directory -Path $agentPath -Force | Out-Null
}

# Download agent archive.
Write-Host 'Downloading agent from {BASE_URL}...' -ForegroundColor Yellow
(New-Object System.Net.WebClient).DownloadFile($downloadUrl, $zipPath)
Write-Host 'Download completed.' -ForegroundColor Green

# Extract agent.
Write-Host 'Extracting agent...' -ForegroundColor Yellow
Expand-Archive -LiteralPath $zipPath -DestinationPath $agentPath -Force
Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue

if (-not (Test-Path -LiteralPath $agentExe)) {
    Write-Host "ERROR: graymoon-agent.exe not found under $agentPath after extract. Install .NET 10 Runtime if the app fails to start." -ForegroundColor Red
    return 1
}

# Delegate all service management (create/update, rights grant, start) to the agent.
Write-Host 'Installing service...' -ForegroundColor Yellow
& $agentExe install --hub-url $hubUrl
if ($LASTEXITCODE -ne 0) {
    Write-Host "Installation failed. Correct any errors above and run the script again." -ForegroundColor Red
    return
}
Write-Host ''
Write-Host 'Installation completed!' -ForegroundColor Green
Write-Host ''
