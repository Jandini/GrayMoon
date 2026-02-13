# GrayMoon Agent Installation Script
# This script downloads and installs the GrayMoon Agent as a Windows service

$ErrorActionPreference = 'Stop'

Write-Host 'GrayMoon Agent Installation' -ForegroundColor Cyan

# Check if running as Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'ERROR: This script must be run as Administrator' -ForegroundColor Red
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
$serviceExists = $null -ne $existingService

# Create directory
Write-Host 'Creating installation directory...' -ForegroundColor Yellow
if (-not (Test-Path $agentPath)) {
    New-Item -ItemType Directory -Path $agentPath -Force | Out-Null
}

# Download agent (replace existing file if present)
Write-Host "Downloading agent from {BASE_URL}..." -ForegroundColor Yellow
try {
    $webClient = New-Object System.Net.WebClient
    $webClient.DownloadFile($downloadUrl, $agentExe)
    Write-Host 'Download completed.' -ForegroundColor Green
} catch {
    Write-Host "ERROR: Failed to download agent: $_" -ForegroundColor Red
    return 1
}

if ($serviceExists) {
    # Service exists: stop it, file already replaced, then start it
    Write-Host 'Found existing service. Stopping...' -ForegroundColor Yellow
    if ($existingService.Status -eq 'Running') {
        Stop-Service -Name $serviceName -Force -ErrorAction Stop | Out-Null
        Start-Sleep -Seconds 2
    }
    Write-Host 'Service stopped. File updated.' -ForegroundColor Green
} else {
    # Service doesn't exist: create it
    Write-Host 'Installing as Windows service...' -ForegroundColor Yellow
    try {
        $hubUrl = '{HUB_URL}'
        $binPath = "`"$agentExe`" run -u `"$hubUrl`""
        
        # Install service to run as current user (required for git credentials, dotnet tools, and user environment)
        $currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        Write-Host "Service will run as: $currentUser" -ForegroundColor Cyan
        Write-Host "Enter password for $currentUser (required for service to access git credentials and dotnet tools):" -ForegroundColor Yellow
        $password = Read-Host -Prompt "Password" -AsSecureString
        $passwordPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($password))
        
        # Clear the secure string from memory
        $password.Dispose()
        
        # Use sc.exe to create service with custom user account (New-Service doesn't support this)
        # Note: sc.exe is still needed for setting custom user credentials
        $result = & sc.exe create $serviceName binPath= $binPath start= auto DisplayName= "$serviceDisplayName" obj= "$currentUser" password= "$passwordPlain" 2>&1
        if ($LASTEXITCODE -ne 0) {
            $errorMsg = if ($result -is [System.Array]) { ($result | Out-String).Trim() } else { $result.ToString() }
            throw "sc.exe create failed with exit code ${LASTEXITCODE}: $errorMsg"
        }
        
        # Clear password from memory immediately
        $passwordPlain = $null
        [System.GC]::Collect()
        
        # Set description using sc.exe
        & sc.exe description $serviceName "$serviceDescription" | Out-Null
        
        Write-Host 'Service installed successfully.' -ForegroundColor Green
    } catch {
        Write-Host "ERROR: Failed to install service: $_" -ForegroundColor Red
        return 1
    }
}

# Start service
Write-Host 'Starting service...' -ForegroundColor Yellow
try {
    Start-Service -Name $serviceName -ErrorAction Stop | Out-Null
    Write-Host 'Service started successfully.' -ForegroundColor Green
} catch {
    Write-Host "WARNING: Failed to start service: $_" -ForegroundColor Yellow
    Write-Host 'You may need to start it manually using: Start-Service -Name GrayMoonAgent' -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Installation completed!' -ForegroundColor Green
Write-Host 'Service Name: GrayMoonAgent' -ForegroundColor Cyan
Write-Host 'Service Path:' $agentExe -ForegroundColor Cyan
Write-Host ''
