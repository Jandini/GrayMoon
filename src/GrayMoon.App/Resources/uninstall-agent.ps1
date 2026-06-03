# GrayMoon Agent Uninstallation Script
# Delegates service management to graymoon-agent.exe, then removes installation files.
#
# Run from a fresh Administrator PowerShell window.

$ErrorActionPreference = 'Stop'

Write-Host 'GrayMoon Agent Uninstallation' -ForegroundColor Cyan

# Check if running as Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator
)

if (-not $isAdmin) {
    Write-Host 'ERROR: This script must be run as Administrator' -ForegroundColor Red
    return 1
}

$agentPath = Join-Path $env:ProgramFiles 'GrayMoon'
$agentExe  = Join-Path $agentPath 'graymoon-agent.exe'

if (Test-Path -LiteralPath $agentExe) {
    Write-Host 'Removing service...' -ForegroundColor Yellow
    & $agentExe uninstall
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'ERROR: Agent uninstall failed.' -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

if (Test-Path -LiteralPath $agentPath) {
    Write-Host 'Removing installation directory...' -ForegroundColor Yellow
    Remove-Item -LiteralPath $agentPath -Recurse -Force -ErrorAction Stop
    Write-Host 'Installation directory removed.' -ForegroundColor Green
}

Write-Host ''
Write-Host 'Uninstallation completed!' -ForegroundColor Green
Write-Host ''
