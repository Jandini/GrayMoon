# GrayMoon Worker Installation Script
# Downloads the worker and delegates all service management to graymoon-worker.exe.
#
# Run from a fresh Administrator PowerShell window.
# The host must have the .NET 10 runtime installed.

$ErrorActionPreference = 'Stop'

# Desktop launches this file in its own window and sets GRAYMOON_DESKTOP_INSTALL=1
# so the window can pause on failure and return a process exit code.
# A copied "irm | iex" session leaves the variable unset and stays open.
function Complete-WorkerInstall {
    param([int]$Code = 0)

    if ($env:GRAYMOON_DESKTOP_INSTALL -ne '1') {
        return
    }

    if ($script:GrayMoonInstallCompleting) {
        exit 1
    }

    $script:GrayMoonInstallCompleting = $true
    if ($Code -ne 0) {
        Write-Host ''
        Write-Host 'Press Enter to close this window.' -ForegroundColor Yellow
        if (-not [Console]::IsInputRedirected) {
            [void](Read-Host)
        }
    }
    else {
        Start-Sleep -Seconds 2
    }

    exit $Code
}

if ($env:GRAYMOON_DESKTOP_INSTALL -eq '1') {
    trap {
        Write-Host ''
        Write-Host $_.Exception.Message -ForegroundColor Red
        Complete-WorkerInstall -Code 1
    }
}

Write-Host 'GrayMoon Worker Installation' -ForegroundColor Cyan

# Check if running as Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator
)

if (-not $isAdmin) {
    Write-Host 'ERROR: This script must be run as Administrator' -ForegroundColor Red
    Complete-WorkerInstall -Code 1
    return 1
}

$serviceName = 'GrayMoonWorker'
$legacyServiceName = 'GrayMoonAgent'
$workerPath   = Join-Path $env:ProgramFiles 'GrayMoon'
$workerExe    = Join-Path $workerPath 'graymoon-worker.exe'
$downloadUrl = '{DOWNLOAD_URL}'
$hubUrl      = '{HUB_URL}'
$zipPath     = Join-Path $env:TEMP 'graymoon-worker-windows-install.zip'
$baseUrl     = '{BASE_URL}'

# The worker secret lives outside the install folder so updating the worker keeps it.
# This script never contains the secret: GrayMoon Desktop writes the file itself, and a manual
# install exchanges the one-time pairing code shown on GrayMoon > Worker for it.
$secretRequired = '{SECRET_REQUIRED}' -eq '1'
$secretDir      = Join-Path $env:ProgramData 'GrayMoon'
$secretPath     = Join-Path $secretDir 'worker.secret'
$hasSecret      = (Test-Path -LiteralPath $secretPath) -and -not [string]::IsNullOrWhiteSpace((Get-Content -LiteralPath $secretPath -Raw -ErrorAction SilentlyContinue))

if (-not ($hasSecret -and $env:GRAYMOON_DESKTOP_INSTALL -eq '1')) {
    $pairingCode = $env:GRAYMOON_WORKER_PAIRING_CODE
    if ([string]::IsNullOrWhiteSpace($pairingCode)) {
        $prompt = if ($hasSecret) { 'Pairing code from GrayMoon > Worker (press Enter to keep the existing worker secret)' } else { 'Pairing code from GrayMoon > Worker' }
        try { $pairingCode = Read-Host $prompt } catch { $pairingCode = $null }
    }

    if (-not [string]::IsNullOrWhiteSpace($pairingCode)) {
        Write-Host 'Pairing with GrayMoon...' -ForegroundColor Yellow
        try {
            $pairBody = @{ code = $pairingCode.Trim() } | ConvertTo-Json -Compress
            $pairResult = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/worker/pair" -ContentType 'application/json' -Body $pairBody
            if ([string]::IsNullOrWhiteSpace($pairResult.secret)) { throw 'empty secret' }
            New-Item -ItemType Directory -Path $secretDir -Force | Out-Null
            Set-Content -LiteralPath $secretPath -Value $pairResult.secret -NoNewline -Encoding ASCII
            $hasSecret = $true
            Write-Host 'Paired.' -ForegroundColor Green
        }
        catch {
            Write-Host 'ERROR: Pairing failed. Open GrayMoon > Worker and copy a new pairing code.' -ForegroundColor Red
            Complete-WorkerInstall -Code 1
            return 1
        }
    }
    elseif (-not $hasSecret) {
        if ($secretRequired) {
            Write-Host 'ERROR: A pairing code is required. Open GrayMoon > Worker and copy a new pairing code.' -ForegroundColor Red
            Complete-WorkerInstall -Code 1
            return 1
        }

        Write-Host 'WARNING: No pairing code given. The worker will connect without a secret; reinstall it later from GrayMoon > Worker.' -ForegroundColor Yellow
    }
}

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
if (Test-Path -LiteralPath $workerPath) {
    Get-ChildItem -LiteralPath $workerPath -Force | Remove-Item -Recurse -Force -ErrorAction Stop
} else {
    New-Item -ItemType Directory -Path $workerPath -Force | Out-Null
}

# Download worker archive.
Write-Host 'Downloading worker from {BASE_URL}...' -ForegroundColor Yellow
(New-Object System.Net.WebClient).DownloadFile($downloadUrl, $zipPath)
Write-Host 'Download completed.' -ForegroundColor Green

# Extract worker.
Write-Host 'Extracting worker...' -ForegroundColor Yellow
Expand-Archive -LiteralPath $zipPath -DestinationPath $workerPath -Force
Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue

if (-not (Test-Path -LiteralPath $workerExe)) {
    Write-Host "ERROR: graymoon-worker.exe not found under $workerPath after extract. Install .NET 10 Runtime if the app fails to start." -ForegroundColor Red
    Complete-WorkerInstall -Code 1
    return 1
}

# Delegate all service management (create/update, rights grant, start) to the worker.
Write-Host 'Installing service...' -ForegroundColor Yellow
& $workerExe install --hub-url $hubUrl
if ($LASTEXITCODE -ne 0) {
    Write-Host "Installation failed. Correct any errors above and run the script again." -ForegroundColor Red
    Complete-WorkerInstall -Code 1
    return
}
Write-Host ''
Write-Host 'Installation completed!' -ForegroundColor Green
Write-Host ''
Complete-WorkerInstall -Code 0
