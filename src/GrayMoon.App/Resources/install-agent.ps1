# GrayMoon Agent Installation Script
# Downloads a zip of the framework-dependent agent (graymoon-agent.exe + DLLs),
# extracts to Program Files, grants "Log on as a service" to the current user,
# and registers the Windows service using native Windows APIs.
#
# Run from a fresh Administrator PowerShell window.
# The host must have the .NET 8 runtime installed for the same RID as the published agent.

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

# Service configuration
$serviceName = 'GrayMoonAgent'
$serviceDisplayName = 'GrayMoon Agent'
$serviceDescription = 'Host-side agent for GrayMoon: executes git and repository I/O operations'

$agentPath = Join-Path $env:ProgramFiles 'GrayMoon'
$agentExe = Join-Path $agentPath 'graymoon-agent.exe'

$downloadUrl = 'http://localhost:8384/api/agent/download?platform=windows'
$hubUrl = 'http://localhost:8384/hub/agent'

$zipPath = Join-Path $env:TEMP 'graymoon-agent-windows-install.zip'

# Native credential validation
if (-not ('GrayMoonNativeLogonValidator' -as [type])) {
Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class GrayMoonNativeLogonValidator
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
}

# Native LSA rights helper.
# IMPORTANT:
# Add-Type cannot replace an already loaded type in the same PowerShell process.
# If you edit this C# code and re-run it, open a fresh Administrator PowerShell window.
if (-not ('GrayMoonServiceLogonRights' -as [type])) {
Add-Type @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class GrayMoonServiceLogonRights
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LSA_UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LSA_OBJECT_ATTRIBUTES
    {
        public uint Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern uint LsaOpenPolicy(
        IntPtr SystemName,
        ref LSA_OBJECT_ATTRIBUTES ObjectAttributes,
        uint DesiredAccess,
        out IntPtr PolicyHandle);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern uint LsaAddAccountRights(
        IntPtr PolicyHandle,
        IntPtr AccountSid,
        LSA_UNICODE_STRING[] UserRights,
        uint CountOfRights);

    [DllImport("advapi32.dll")]
    private static extern uint LsaClose(IntPtr ObjectHandle);

    [DllImport("advapi32.dll")]
    private static extern int LsaNtStatusToWinError(uint Status);

    private const uint POLICY_ALL_ACCESS = 0x000F0FFF;

    public static void AddAccountRight(byte[] sidBytes, string rightName)
    {
        if (sidBytes == null || sidBytes.Length == 0)
            throw new ArgumentException("SID is empty.", "sidBytes");

        if (string.IsNullOrWhiteSpace(rightName))
            throw new ArgumentException("Right name is empty.", "rightName");

        LSA_OBJECT_ATTRIBUTES attributes = new LSA_OBJECT_ATTRIBUTES();
        attributes.Length = (uint)Marshal.SizeOf(typeof(LSA_OBJECT_ATTRIBUTES));

        GCHandle sidHandle = GCHandle.Alloc(sidBytes, GCHandleType.Pinned);
        IntPtr policyHandle = IntPtr.Zero;
        IntPtr rightPtr = IntPtr.Zero;

        try
        {
            uint status = LsaOpenPolicy(
                IntPtr.Zero,
                ref attributes,
                POLICY_ALL_ACCESS,
                out policyHandle);

            if (status != 0)
            {
                int error = LsaNtStatusToWinError(status);
                throw new Win32Exception(error, "LsaOpenPolicy failed.");
            }

            rightPtr = Marshal.StringToHGlobalUni(rightName);

            LSA_UNICODE_STRING[] rights = new LSA_UNICODE_STRING[1];
            rights[0].Buffer = rightPtr;
            rights[0].Length = (ushort)(rightName.Length * 2);
            rights[0].MaximumLength = (ushort)((rightName.Length + 1) * 2);

            status = LsaAddAccountRights(
                policyHandle,
                sidHandle.AddrOfPinnedObject(),
                rights,
                1);

            if (status != 0)
            {
                int error = LsaNtStatusToWinError(status);
                throw new Win32Exception(error, "LsaAddAccountRights failed.");
            }
        }
        finally
        {
            if (rightPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(rightPtr);

            if (policyHandle != IntPtr.Zero)
                LsaClose(policyHandle);

            if (sidHandle.IsAllocated)
                sidHandle.Free();
        }
    }
}
"@
}

# Native Windows service creation helper.
# This avoids Win32_Service.Create and therefore avoids the CIM ServiceType mismatch.
if (-not ('GrayMoonNativeServiceInstaller' -as [type])) {
Add-Type @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class GrayMoonNativeServiceInstaller
{
    private const uint SC_MANAGER_CREATE_SERVICE = 0x00000002;

    private const uint SERVICE_ALL_ACCESS = 0x000F01FF;
    private const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
    private const uint SERVICE_AUTO_START = 0x00000002;
    private const uint SERVICE_ERROR_NORMAL = 0x00000001;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(
        string lpMachineName,
        string lpDatabaseName,
        uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateServiceW(
        IntPtr hSCManager,
        string lpServiceName,
        string lpDisplayName,
        uint dwDesiredAccess,
        uint dwServiceType,
        uint dwStartType,
        uint dwErrorControl,
        string lpBinaryPathName,
        string lpLoadOrderGroup,
        IntPtr lpdwTagId,
        string lpDependencies,
        string lpServiceStartName,
        string lpPassword);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    public static void CreateOwnProcessAutoService(
        string name,
        string displayName,
        string pathName,
        string startName,
        string password)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Service name is empty.", "name");

        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Service display name is empty.", "displayName");

        if (string.IsNullOrWhiteSpace(pathName))
            throw new ArgumentException("Service path is empty.", "pathName");

        if (string.IsNullOrWhiteSpace(startName))
            throw new ArgumentException("Service account is empty.", "startName");

        IntPtr scm = IntPtr.Zero;
        IntPtr service = IntPtr.Zero;

        scm = OpenSCManagerW(null, null, SC_MANAGER_CREATE_SERVICE);

        if (scm == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, "OpenSCManagerW failed.");
        }

        try
        {
            service = CreateServiceW(
                scm,
                name,
                displayName,
                SERVICE_ALL_ACCESS,
                SERVICE_WIN32_OWN_PROCESS,
                SERVICE_AUTO_START,
                SERVICE_ERROR_NORMAL,
                pathName,
                null,
                IntPtr.Zero,
                null,
                startName,
                password);

            if (service == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, "CreateServiceW failed.");
            }
        }
        finally
        {
            if (service != IntPtr.Zero)
                CloseServiceHandle(service);

            if (scm != IntPtr.Zero)
                CloseServiceHandle(scm);
        }
    }
}
"@
}

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

    # LOGON32_LOGON_INTERACTIVE = 2
    # LOGON32_PROVIDER_DEFAULT = 0
    $ok = [GrayMoonNativeLogonValidator]::LogonUser(
        $identity.UserName,
        $identity.Domain,
        $Password,
        2,
        0,
        [ref]$tokenHandle
    )

    if ($ok -and $tokenHandle -ne [IntPtr]::Zero) {
        [void][GrayMoonNativeLogonValidator]::CloseHandle($tokenHandle)
    }

    return $ok
}

function Grant-ServiceLogonRight-Secedit {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AccountSidString
    )

    $secedit = Join-Path $env:WINDIR 'System32\secedit.exe'
    if (-not (Test-Path -LiteralPath $secedit)) {
        throw 'secedit.exe was not found.'
    }

    $guid = [guid]::NewGuid().ToString('N')
    $infFile = Join-Path $env:TEMP "graymoon-secpol-$guid.inf"
    $dbFile = Join-Path $env:TEMP "graymoon-secedit-$guid.sdb"
    $logFile = Join-Path $env:TEMP "graymoon-secedit-$guid.log"
    $sidToken = "*$AccountSidString"

    try {
        & $secedit /export /cfg $infFile /areas USER_RIGHTS | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "secedit export exited with code $LASTEXITCODE."
        }

        if (-not (Test-Path -LiteralPath $infFile)) {
            throw "secedit export did not create '$infFile'."
        }

        $lines = @(Get-Content -LiteralPath $infFile -Encoding Unicode)
        $updated = $false
        $privilegeLineIndex = -1

        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '(?i)^\s*SeServiceLogonRight\s*=') {
                $privilegeLineIndex = $i
                break
            }
        }

        if ($privilegeLineIndex -ge 0) {
            $line = $lines[$privilegeLineIndex]
            if ($line -match [regex]::Escape($AccountSidString)) {
                return
            }

            $value = ($line -split '=', 2)[1].Trim()
            if ([string]::IsNullOrWhiteSpace($value)) {
                $lines[$privilegeLineIndex] = "SeServiceLogonRight = $sidToken"
            }
            else {
                $lines[$privilegeLineIndex] = "SeServiceLogonRight = $value,$sidToken"
            }

            $updated = $true
        }
        else {
            $sectionIndex = -1
            for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($lines[$i] -match '^\[Privilege Rights\]\s*$') {
                    $sectionIndex = $i
                    break
                }
            }

            if ($sectionIndex -ge 0) {
                $before = $lines[0..$sectionIndex]
                $after = @()
                if ($sectionIndex -lt ($lines.Count - 1)) {
                    $after = $lines[($sectionIndex + 1)..($lines.Count - 1)]
                }

                $lines = $before + @("SeServiceLogonRight = $sidToken") + $after
            }
            else {
                $lines = $lines + @('[Privilege Rights]', "SeServiceLogonRight = $sidToken")
            }

            $updated = $true
        }

        if (-not $updated) {
            return
        }

        $lines | Set-Content -LiteralPath $infFile -Encoding Unicode

        & $secedit /configure /db $dbFile /cfg $infFile /areas USER_RIGHTS /log $logFile | Out-Null
        if ($LASTEXITCODE -ne 0) {
            $logText = ''
            if (Test-Path -LiteralPath $logFile) {
                $logText = Get-Content -LiteralPath $logFile -Raw -ErrorAction SilentlyContinue
            }

            throw "secedit configure exited with code $LASTEXITCODE. $logText"
        }
    }
    finally {
        Remove-Item -LiteralPath $infFile, $dbFile, $logFile -Force -ErrorAction SilentlyContinue
    }
}

function Grant-ServiceLogonRight {
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.Principal.SecurityIdentifier]$AccountSid
    )

    $sidBytes = New-Object byte[] $AccountSid.BinaryLength
    $AccountSid.GetBinaryForm($sidBytes, 0)
    $sidString = $AccountSid.Value
    $lsaError = $null

    try {
        [GrayMoonServiceLogonRights]::AddAccountRight($sidBytes, 'SeServiceLogonRight')
        return
    }
    catch {
        $lsaError = $_.Exception.Message
    }

    Write-Host "LSA grant failed ($lsaError). Trying secedit..." -ForegroundColor Yellow

    try {
        Grant-ServiceLogonRight-Secedit -AccountSidString $sidString
    }
    catch {
        throw "Failed to grant 'Log on as a service' (SID: $sidString). LSA: $lsaError. Secedit: $($_.Exception.Message)"
    }
}

function Build-AgentServiceCommandLine {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AgentExePath,

        [Parameter(Mandatory = $true)]
        [string]$HubUrl
    )

    return '"' + $AgentExePath + '" run -u "' + $HubUrl + '"'
}

function Set-Win32ServicePathName {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$PathName
    )

    $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'" -ErrorAction Stop

    $out = Invoke-CimMethod `
        -InputObject $svc `
        -MethodName Change `
        -Arguments @{ PathName = $PathName } `
        -ErrorAction Stop

    if ($out.ReturnValue -ne 0) {
        throw "Win32_Service.Change failed with return code $($out.ReturnValue). PathName: $PathName"
    }
}

function Set-ServiceDescriptionRegistry {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $keyPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"

    if (-not (Test-Path -LiteralPath $keyPath)) {
        return
    }

    Set-ItemProperty `
        -LiteralPath $keyPath `
        -Name Description `
        -Value $Description `
        -Type String `
        -Force `
        -ErrorAction SilentlyContinue | Out-Null
}

function New-WindowsServiceWithLogon {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$DisplayName,

        [Parameter(Mandatory = $true)]
        [string]$PathName,

        [Parameter(Mandatory = $true)]
        [string]$StartName,

        [Parameter(Mandatory = $true)]
        [string]$StartPassword
    )

    [GrayMoonNativeServiceInstaller]::CreateOwnProcessAutoService(
        $Name,
        $DisplayName,
        $PathName,
        $StartName,
        $StartPassword
    )
}

Write-Host 'Checking for existing service...' -ForegroundColor Yellow

$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$serviceExists = $null -ne $existingService
$wasRunning = $false

# Stop service if it is running. This is needed to unlock the executable file.
if ($serviceExists -and $existingService.Status -eq 'Running') {
    Write-Host 'Found running service. Stopping to allow file update...' -ForegroundColor Yellow

    try {
        Stop-Service -Name $serviceName -Force -ErrorAction Stop | Out-Null
        Start-Sleep -Seconds 2
        $wasRunning = $true
        Write-Host 'Service stopped.' -ForegroundColor Green
    }
    catch {
        Write-Host "WARNING: Failed to stop service: $_" -ForegroundColor Yellow
        Write-Host 'Attempting to download anyway...' -ForegroundColor Yellow
    }
}

# Prepare installation directory.
# Clear existing files so the zip extract does not leave stale DLLs.
Write-Host 'Preparing installation directory...' -ForegroundColor Yellow

try {
    if (-not (Test-Path -LiteralPath $agentPath)) {
        New-Item -ItemType Directory -Path $agentPath -Force | Out-Null
    }
    else {
        Get-ChildItem -LiteralPath $agentPath -Force | Remove-Item -Recurse -Force -ErrorAction Stop
    }
}
catch {
    Write-Host "ERROR: Failed to prepare installation directory '$agentPath': $_" -ForegroundColor Red

    if ($wasRunning) {
        try {
            Start-Service -Name $serviceName -ErrorAction SilentlyContinue
        }
        catch {
        }
    }

    return 1
}

# Download agent archive.
Write-Host 'Downloading agent from http://localhost:8384...' -ForegroundColor Yellow

$webClient = $null

try {
    $webClient = New-Object System.Net.WebClient
    $webClient.DownloadFile($downloadUrl, $zipPath)

    Write-Host 'Download completed.' -ForegroundColor Green
}
catch {
    Write-Host "ERROR: Failed to download agent: $_" -ForegroundColor Red

    if ($wasRunning) {
        Write-Host 'Attempting to restart the service...' -ForegroundColor Yellow

        try {
            Start-Service -Name $serviceName -ErrorAction SilentlyContinue
        }
        catch {
        }
    }

    return 1
}
finally {
    if ($null -ne $webClient) {
        $webClient.Dispose()
    }
}

# Extract agent.
Write-Host 'Extracting agent...' -ForegroundColor Yellow

try {
    Expand-Archive -LiteralPath $zipPath -DestinationPath $agentPath -Force
}
catch {
    Write-Host "ERROR: Failed to extract agent: $_" -ForegroundColor Red

    if ($wasRunning) {
        try {
            Start-Service -Name $serviceName -ErrorAction SilentlyContinue
        }
        catch {
        }
    }

    return 1
}
finally {
    Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path -LiteralPath $agentExe)) {
    Write-Host "ERROR: graymoon-agent.exe not found under $agentPath after extract. Install .NET 8 Runtime if the app fails to start." -ForegroundColor Red

    if ($wasRunning) {
        try {
            Start-Service -Name $serviceName -ErrorAction SilentlyContinue
        }
        catch {
        }
    }

    return 1
}

$binPath = Build-AgentServiceCommandLine -AgentExePath $agentExe -HubUrl $hubUrl

if ($serviceExists) {
    Write-Host 'Agent files updated.' -ForegroundColor Green
    Write-Host 'Updating service to use the new binary path and hub URL...' -ForegroundColor Yellow

    try {
        Set-Win32ServicePathName -Name $serviceName -PathName $binPath
        Set-ServiceDescriptionRegistry -Name $serviceName -Description $serviceDescription

        Write-Host 'Service configuration updated successfully.' -ForegroundColor Green
    }
    catch {
        Write-Host "ERROR: Failed to update service binary path: $_" -ForegroundColor Red
        Write-Host "Agent files are under $agentPath but the service was not updated. Fix the service command line or re-run this script after resolving the error." -ForegroundColor Yellow

        return 1
    }
}
else {
    Write-Host 'Installing as Windows service...' -ForegroundColor Yellow

    try {
        # Run service as current user.
        # This is useful for git credentials, dotnet tools, and user environment.
        $currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name

        Write-Host "Service will run as: $currentUser" -ForegroundColor Cyan
        Write-Host "Enter password for $currentUser. This is required for the service to access git credentials and dotnet tools." -ForegroundColor Yellow

        $password = Read-Host -Prompt 'Password' -AsSecureString
        $passwordBstr = [IntPtr]::Zero
        $passwordPlain = $null

        try {
            $passwordBstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($password)
            $passwordPlain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordBstr)

            if (-not (Test-UserCredentials -AccountName $currentUser -Password $passwordPlain)) {
                throw "Invalid credentials for '$currentUser'. Aborting installation."
            }

            Write-Host 'Granting "Log on as a service" right...' -ForegroundColor Yellow
            Grant-ServiceLogonRight -AccountSid ([System.Security.Principal.WindowsIdentity]::GetCurrent().User)

            Write-Host 'Creating Windows service...' -ForegroundColor Yellow

            New-WindowsServiceWithLogon `
                -Name $serviceName `
                -DisplayName $serviceDisplayName `
                -PathName $binPath `
                -StartName $currentUser `
                -StartPassword $passwordPlain

            Set-ServiceDescriptionRegistry -Name $serviceName -Description $serviceDescription
        }
        finally {
            if ($passwordBstr -ne [IntPtr]::Zero) {
                [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordBstr)
            }

            $passwordPlain = $null

            if ($null -ne $password) {
                $password.Dispose()
            }

            [System.GC]::Collect()
        }

        Write-Host 'Service installed successfully.' -ForegroundColor Green
    }
    catch {
        Write-Host "ERROR: Failed to install service: $_" -ForegroundColor Red

        return 1
    }
}

# Start service.
Write-Host 'Starting service...' -ForegroundColor Yellow

try {
    Start-Service -Name $serviceName -ErrorAction Stop | Out-Null

    Write-Host 'Service started successfully.' -ForegroundColor Green
}
catch {
    Write-Host "WARNING: Failed to start service: $_" -ForegroundColor Yellow
    Write-Host 'You may need to start it manually using: Start-Service -Name GrayMoonAgent' -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Installation completed!' -ForegroundColor Green
Write-Host 'Service Name: GrayMoonAgent' -ForegroundColor Cyan
Write-Host 'Service Path:' $agentExe -ForegroundColor Cyan
Write-Host ''
