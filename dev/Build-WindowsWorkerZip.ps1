<#
.SYNOPSIS
    Builds worker-dist\graymoon-worker-windows.zip, the Windows Worker that the Docker image serves.

.DESCRIPTION
    The Worker is a Native AOT exe, and Native AOT cannot be compiled for Windows on Linux. So the Windows
    Worker is published here, on Windows, and the Dockerfile copies the zip into the image. The Docker Build
    workflow runs this script in its worker-windows job; run it yourself before a local "docker build".

    Needs the .NET SDK and the MSVC linker (Visual Studio C++ tools).

.PARAMETER Version
    Version to build the Worker with. It must be the version the image is built with: the App accepts only a
    Worker of its own version. When omitted, GitVersion decides (the same version a local App build gets).
#>
[CmdletBinding()]
param(
    [string] $Version = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
$worker = Join-Path $repo 'src\GrayMoon.Worker\GrayMoon.Worker.csproj'
$dist = Join-Path $repo 'worker-dist'
$publish = Join-Path $dist 'publish-win'
$zip = Join-Path $dist 'graymoon-worker-windows.zip'

# The AOT link step calls vswhere.exe by name.
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if ((Test-Path $installer) -and ($env:PATH -notlike "*$installer*")) { $env:PATH = "$installer;$env:PATH" }

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory $publish | Out-Null

$arguments = @('publish', $worker, '-c', 'Release', '-r', 'win-x64', '-o', $publish, '-p:DebugType=None', '-p:DebugSymbols=false', '-nologo')
if ($Version) { $arguments += @("-p:Version=$Version", '-p:DisableGitVersionTask=true') }

& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "The Worker publish failed (exit $LASTEXITCODE)." }

Get-ChildItem $publish -Recurse -File |
    Where-Object { $_.Extension -eq '.pdb' -or $_.Name -match '\.Development(\.|$)' } |
    Remove-Item -Force

if (-not (Test-Path (Join-Path $publish 'graymoon-worker.exe'))) { throw "graymoon-worker.exe is missing from $publish." }

Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -Force
Remove-Item $publish -Recurse -Force

Write-Host ("Created {0} ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
