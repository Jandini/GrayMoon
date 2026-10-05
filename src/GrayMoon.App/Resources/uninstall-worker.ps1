# GrayMoon Worker Uninstallation Script
# Delegates service management to graymoon-worker.exe, then removes installation files.
#
# Run from a fresh Administrator PowerShell window.

$ErrorActionPreference = 'Stop'

Write-Host 'GrayMoon Worker Uninstallation' -ForegroundColor Cyan

# Check if running as Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator
)

if (-not $isAdmin) {
    Write-Host 'ERROR: This script must be run as Administrator' -ForegroundColor Red
    return 1
}

$workerPath = Join-Path $env:ProgramFiles 'GrayMoon'
$workerExe  = Join-Path $workerPath 'graymoon-worker.exe'

if (Test-Path -LiteralPath $workerExe) {
    Write-Host 'Removing service...' -ForegroundColor Yellow
    & $workerExe uninstall
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'ERROR: Worker uninstall failed.' -ForegroundColor Red
        return
    }
}

if (Test-Path -LiteralPath $workerPath) {
    Write-Host 'Removing installation directory...' -ForegroundColor Yellow
    Remove-Item -LiteralPath $workerPath -Recurse -Force -ErrorAction Stop
    Write-Host 'Installation directory removed.' -ForegroundColor Green
}

Write-Host ''
Write-Host 'Uninstallation completed!' -ForegroundColor Green
Write-Host ''
