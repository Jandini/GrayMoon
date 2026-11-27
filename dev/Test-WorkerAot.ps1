<#
.SYNOPSIS
    Publishes the Worker as a Native AOT exe and runs the smoke test against it.

.DESCRIPTION
    The unit tests run on the normal runtime, so they cannot show that the published exe works. This script
    publishes src\GrayMoon.Worker for this machine (win-x64 or linux-x64; Native AOT is set in the project), then runs
    AotWorkerSmokeTests with GRAYMOON_AOT_WORKER_EXE pointing at the exe: the test hosts a hub, starts the exe
    against it and sends commands the way the App does.

    Exit code 0 only when the publish has no error and the smoke test passes. Needs git, the .NET SDK and the
    native linker: the MSVC linker (Visual Studio C++ tools) on Windows, clang and zlib1g-dev on Linux. On Linux the
    Worker writes its log under /usr/share/GrayMoon, so that folder must be writable for the user who runs this.

.PARAMETER WorkDir
    Folder for the publish output. Emptied at the start. Default: %TEMP%\graymoon-worker-aot.
#>
[CmdletBinding()]
param(
    [string] $WorkDir = (Join-Path ([System.IO.Path]::GetTempPath()) 'graymoon-worker-aot')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
$worker = Join-Path $repo 'src/GrayMoon.Worker/GrayMoon.Worker.csproj'
$tests = Join-Path $repo 'src/GrayMoon.Worker.Tests/GrayMoon.Worker.Tests.csproj'
$onWindows = [System.OperatingSystem]::IsWindows()
$rid = if ($onWindows) { 'win-x64' } else { 'linux-x64' }
$exeName = if ($onWindows) { 'graymoon-worker.exe' } else { 'graymoon-worker' }

if ($onWindows) {
    # The AOT link step calls vswhere.exe by name.
    $installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    if ((Test-Path $installer) -and ($env:PATH -notlike "*$installer*")) { $env:PATH = "$installer;$env:PATH" }
}

if (Test-Path $WorkDir) { Remove-Item $WorkDir -Recurse -Force }
New-Item -ItemType Directory $WorkDir | Out-Null
$out = Join-Path $WorkDir 'out'
$log = Join-Path $WorkDir 'publish.log'

Write-Host '==> Publishing the Worker as Native AOT' -ForegroundColor Cyan
& dotnet publish $worker -c Release -r $rid --artifacts-path (Join-Path $WorkDir 'obj') -o $out -nologo *> $log
if ($LASTEXITCODE -ne 0) {
    Select-String -Path $log -Pattern ' error ' | Select-Object -First 20 | ForEach-Object { Write-Host $_.Line -ForegroundColor Red }
    throw "AOT publish failed, see $log"
}

$exe = Join-Path $out $exeName
$files = Get-ChildItem $out -File | Where-Object { $_.Extension -notin '.pdb', '.dbg' }
Write-Host ("  {0} file(s), {1:N1} MB: {2}" -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB), (($files.Name | Sort-Object) -join ', '))

Write-Host '==> Running the smoke test against the exe' -ForegroundColor Cyan
$env:GRAYMOON_AOT_WORKER_EXE = $exe
try {
    & dotnet test $tests --filter 'FullyQualifiedName~AotWorkerSmokeTests' --nologo
    $code = $LASTEXITCODE
}
finally {
    Remove-Item Env:GRAYMOON_AOT_WORKER_EXE
}

if ($code -ne 0) {
    Write-Host 'FAILED: the Native AOT Worker did not pass the smoke test.' -ForegroundColor Red
    exit 1
}

Write-Host 'PASSED: the Native AOT Worker connects and answers commands.' -ForegroundColor Green
exit 0
