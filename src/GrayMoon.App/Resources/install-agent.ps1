# GrayMoon Agent Installation Script
# Downloads a zip of the framework-dependent agent (graymoon-agent.exe + DLLs), extracts to Program Files, and registers the Windows service.
# The host must have the .NET 8 runtime (or ASP.NET Core 8 runtime) installed for the same RID as the published agent.

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
$agentPath = Join-Path $env:ProgramFiles 'GrayMoon'
$agentExe = Join-Path $agentPath 'graymoon-agent.exe'
$downloadUrl = '{DOWNLOAD_URL}'
$zipPath = Join-Path $env:TEMP 'graymoon-agent-windows-install.zip'

Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class NativeLogonValidator
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool LogonUser(
        string lpszUsername,
        string lpszDomain,
        string lpszPassword,
        int dwLogonType,
        int dwLogonProvider,
        out IntPtr phToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);
}
"@

function Split-AccountName {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AccountName
    )

    if ($AccountName.Contains('\')) {
        $parts = $AccountName.Split('\', 2)
        return @{
            Domain = $parts[0]
            UserName = $parts[1]
        }
    }

    if ($AccountName.Contains('@')) {
        $parts = $AccountName.Split('@', 2)
        return @{
            Domain = $parts[1]
            UserName = $parts[0]
        }
    }

    return @{
        Domain = $env:COMPUTERNAME
        UserName = $AccountName
    }
}

function Test-UserCredentials {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AccountName,
        [Parameter(Mandatory = $true)]
        [string]$Password
    )

    $identity = Split-AccountName -AccountName $AccountName
    $tokenHandle = [IntPtr]::Zero
    $logonTypeInteractive = 2
    $providerDefault = 0

    $ok = [NativeLogonValidator]::LogonUser(
        $identity.UserName,
        $identity.Domain,
        $Password,
        $logonTypeInteractive,
        $providerDefault,
        [ref]$tokenHandle
    )

    if ($ok -and $tokenHandle -ne [IntPtr]::Zero) {
        [void][NativeLogonValidator]::CloseHandle($tokenHandle)
    }

    return $ok
}

function Grant-ServiceLogonRight {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AccountName
    )

    $sid = ([System.Security.Principal.NTAccount]$AccountName).Translate([System.Security.Principal.SecurityIdentifier]).Value
    $sidEntry = "*$sid"
    $tempDir = Join-Path $env:TEMP "GrayMoon-ServiceRights"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

    $cfgPath = Join-Path $tempDir "secpol.cfg"
    $infPath = Join-Path $tempDir "secpol.inf"
    $dbPath = Join-Path $tempDir "secpol.sdb"

    try {
        $null = & secedit /export /cfg "$cfgPath" /areas USER_RIGHTS
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to export local security policy."
        }

        $content = if (Test-Path $cfgPath) { Get-Content -Path $cfgPath -ErrorAction Stop } else { @() }
        $lineIndex = -1
        for ($i = 0; $i -lt $content.Count; $i++) {
            if ($content[$i] -match '^SeServiceLogonRight\s*=') {
                $lineIndex = $i
                break
            }
        }

        $newLine = "SeServiceLogonRight = $sidEntry"
        if ($lineIndex -ge 0) {
            $existingPart = ($content[$lineIndex] -split '=', 2)[1]
            $entries = @()
            if (-not [string]::IsNullOrWhiteSpace($existingPart)) {
                $entries = $existingPart.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
            }

            if ($entries -contains $sidEntry) {
                return
            }

            $entries += $sidEntry
            $newLine = "SeServiceLogonRight = $($entries -join ',')"
            $content[$lineIndex] = $newLine
        } else {
            $privilegeSectionIndex = -1
            for ($i = 0; $i -lt $content.Count; $i++) {
                if ($content[$i] -match '^\[Privilege Rights\]') {
                    $privilegeSectionIndex = $i
                    break
                }
            }

            if ($privilegeSectionIndex -lt 0) {
                $content += "[Privilege Rights]"
                $content += $newLine
            } else {
                $insertIndex = $privilegeSectionIndex + 1
                while ($insertIndex -lt $content.Count -and -not $content[$insertIndex].StartsWith('[')) {
                    $insertIndex++
                }

                $before = @()
                $after = @()
                if ($insertIndex -gt 0) {
                    $before = $content[0..($insertIndex - 1)]
                }
                if ($insertIndex -lt $content.Count) {
                    $after = $content[$insertIndex..($content.Count - 1)]
                }
                $content = @($before + $newLine + $after)
            }
        }

        @"
[Unicode]
Unicode=yes
[Version]
signature="`$CHICAGO`$"
Revision=1
[Privilege Rights]
$newLine
"@ | Set-Content -Path $infPath -Encoding Unicode -Force

        $null = & secedit /configure /db "$dbPath" /cfg "$infPath" /areas USER_RIGHTS
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to apply local security policy update."
        }
    }
    finally {
        Remove-Item -Path $cfgPath -Force -ErrorAction SilentlyContinue
        Remove-Item -Path $infPath -Force -ErrorAction SilentlyContinue
        Remove-Item -Path $dbPath -Force -ErrorAction SilentlyContinue
    }
}

function Build-AgentServiceCommandLine {
    param(
        [Parameter(Mandatory)]
        [string]$AgentExePath,
        [Parameter(Mandatory)]
        [string]$HubUrl
    )
    '"' + $AgentExePath + '" run -u "' + $HubUrl + '"'
}

function Set-Win32ServicePathName {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [string]$PathName
    )
    $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'" -ErrorAction Stop
    $out = Invoke-CimMethod -InputObject $svc -MethodName Change -Arguments @{ PathName = $PathName }
    if ($out.ReturnValue -ne 0) {
        throw "Win32_Service.Change failed with return code $($out.ReturnValue). PathName: $PathName"
    }
}

function Set-ServiceDescriptionRegistry {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [string]$Description
    )
    $keyPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
    if (-not (Test-Path -LiteralPath $keyPath)) {
        return
    }
    Set-ItemProperty -LiteralPath $keyPath -Name Description -Value $Description -Type String -Force -ErrorAction SilentlyContinue | Out-Null
}

function New-Win32ServiceWithLogon {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [string]$DisplayName,
        [Parameter(Mandatory)]
        [string]$PathName,
        [Parameter(Mandatory)]
        [string]$StartName,
        [Parameter(Mandatory)]
        [string]$StartPassword
    )
    $out = Invoke-CimMethod -ClassName Win32_Service -MethodName Create -Arguments @{
        Name = $Name
        DisplayName = $DisplayName
        PathName = $PathName
        ServiceType = [uint32]16
        ErrorControl = [uint32]1
        StartMode = 'Automatic'
        DesktopInteract = $false
        StartName = $StartName
        StartPassword = $StartPassword
        LoadOrderGroup = ''
        LoadOrderGroupDependencies = $null
        ServiceDependencies = $null
    }
    if ($out.ReturnValue -ne 0) {
        throw "Win32_Service.Create failed with return code $($out.ReturnValue)."
    }
}

Write-Host 'Checking for existing service...' -ForegroundColor Yellow
$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$serviceExists = $null -ne $existingService
$wasRunning = $false

# Stop service if it's running (needed to unlock the executable file)
if ($serviceExists -and $existingService.Status -eq 'Running') {
    Write-Host 'Found running service. Stopping to allow file update...' -ForegroundColor Yellow
    try {
        Stop-Service -Name $serviceName -Force -ErrorAction Stop | Out-Null
        Start-Sleep -Seconds 2
        $wasRunning = $true
        Write-Host 'Service stopped.' -ForegroundColor Green
    } catch {
        Write-Host "WARNING: Failed to stop service: $_" -ForegroundColor Yellow
        Write-Host "Attempting to download anyway..." -ForegroundColor Yellow
    }
}

# Prepare installation directory (clear existing files so the zip extract does not leave stale DLLs)
Write-Host 'Preparing installation directory...' -ForegroundColor Yellow
if (-not (Test-Path $agentPath)) {
    New-Item -ItemType Directory -Path $agentPath -Force | Out-Null
} else {
    Get-ChildItem -Path $agentPath -Force | Remove-Item -Recurse -Force -ErrorAction Stop
}

# Download agent archive, then extract (framework-dependent publish: exe + DLLs)
Write-Host "Downloading agent from {BASE_URL}..." -ForegroundColor Yellow
try {
    $webClient = New-Object System.Net.WebClient
    $webClient.DownloadFile($downloadUrl, $zipPath)
    Write-Host 'Download completed.' -ForegroundColor Green
} catch {
    Write-Host "ERROR: Failed to download agent: $_" -ForegroundColor Red
    if ($wasRunning) {
        Write-Host "Attempting to restart the service..." -ForegroundColor Yellow
        try {
            Start-Service -Name $serviceName -ErrorAction SilentlyContinue
        } catch {
            Write-Host "Could not restart service. Please restart manually." -ForegroundColor Yellow
        }
    }
    return 1
}

Write-Host 'Extracting agent...' -ForegroundColor Yellow
try {
    Expand-Archive -LiteralPath $zipPath -DestinationPath $agentPath -Force
} catch {
    Write-Host "ERROR: Failed to extract agent: $_" -ForegroundColor Red
    if ($wasRunning) {
        try { Start-Service -Name $serviceName -ErrorAction SilentlyContinue } catch { }
    }
    return 1
} finally {
    Remove-Item -Path $zipPath -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path -Path $agentExe)) {
    Write-Host "ERROR: graymoon-agent.exe not found under $agentPath after extract. Install .NET 8 Runtime if the app fails to start." -ForegroundColor Red
    if ($wasRunning) {
        try { Start-Service -Name $serviceName -ErrorAction SilentlyContinue } catch { }
    }
    return 1
}

if ($serviceExists) {
    Write-Host 'Agent files updated.' -ForegroundColor Green
    Write-Host 'Updating service to use the new binary path and hub URL...' -ForegroundColor Yellow
    try {
        $hubUrl = '{HUB_URL}'
        $binPath = Build-AgentServiceCommandLine -AgentExePath $agentExe -HubUrl $hubUrl
        Set-Win32ServicePathName -Name $serviceName -PathName $binPath
        Set-ServiceDescriptionRegistry -Name $serviceName -Description $serviceDescription
        Write-Host 'Service configuration updated successfully.' -ForegroundColor Green
    } catch {
        Write-Host "ERROR: Failed to update service binary path: $_" -ForegroundColor Red
        Write-Host "Agent files are under $agentPath but the service was not updated. Fix the service command line or re-run this script after resolving the error." -ForegroundColor Yellow
        return 1
    }
} else {
    # Service doesn't exist: create it
    Write-Host 'Installing as Windows service...' -ForegroundColor Yellow
    try {
        $hubUrl = '{HUB_URL}'
        $binPath = Build-AgentServiceCommandLine -AgentExePath $agentExe -HubUrl $hubUrl

        # Install service to run as current user (required for git credentials, dotnet tools, and user environment)
        $currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        Write-Host "Service will run as: $currentUser" -ForegroundColor Cyan
        Write-Host "Enter password for $currentUser (required for service to access git credentials and dotnet tools):" -ForegroundColor Yellow
        $password = Read-Host -Prompt "Password" -AsSecureString
        $passwordBstr = [IntPtr]::Zero
        try {
            $passwordBstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($password)
            $passwordPlain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordBstr)

            if (-not (Test-UserCredentials -AccountName $currentUser -Password $passwordPlain)) {
                throw "Invalid credentials for '$currentUser'. Aborting installation."
            }

            Write-Host 'Granting "Log on as a service" right...' -ForegroundColor Yellow
            Grant-ServiceLogonRight -AccountName $currentUser

            New-Win32ServiceWithLogon -Name $serviceName -DisplayName $serviceDisplayName -PathName $binPath -StartName $currentUser -StartPassword $passwordPlain
            Set-ServiceDescriptionRegistry -Name $serviceName -Description $serviceDescription
        }
        finally {
            if ($passwordBstr -ne [IntPtr]::Zero) {
                [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordBstr)
            }
            $passwordPlain = $null
            $password.Dispose()
            [System.GC]::Collect()
        }

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
