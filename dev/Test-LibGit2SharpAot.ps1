<#
.SYNOPSIS
    Proves or disproves that the Worker's LibGit2Sharp code works as Native AOT.

.DESCRIPTION
    Builds src\GrayMoon.Worker.AotProbe twice: for the normal runtime and as a Native AOT exe. Each run builds
    the same fixture repositories and checks every LibGit2Sharp-backed Worker class against the git CLI. Then:

      1. the normal-runtime run must pass every check (otherwise the probe itself is wrong),
      2. the AOT run must pass every check,
      3. the AOT run from an install layout (only the exe and git2-*.dll, started from another folder) must pass,
      4. the answers of all runs must be identical line by line,
      5. the AOT exe without git2-*.dll must report a damaged install instead of "no damage".

    Exit code 0 only when all five hold. Needs git, the .NET SDK and the MSVC linker (Visual Studio C++ tools).

.PARAMETER WorkDir
    Folder for build output, fixtures and logs. Emptied at the start. Default: %TEMP%\graymoon-libgit2-aot.
#>
[CmdletBinding()]
param(
    [string] $WorkDir = (Join-Path $env:TEMP 'graymoon-libgit2-aot'),
    # Test this LibGit2Sharp version instead of the one the Worker references (a patched build or a new release).
    [string] $LibGit2SharpVersion = '',
    # Folder or feed that holds that version, when it is not on the configured feeds.
    [string] $PackageSource = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot '..\src\GrayMoon.Worker.AotProbe\GrayMoon.Worker.AotProbe.csproj'
$exeName = 'graymoon-libgit2-aot-probe.exe'

# The AOT link step calls vswhere.exe by name.
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if ((Test-Path $installer) -and ($env:PATH -notlike "*$installer*")) { $env:PATH = "$installer;$env:PATH" }

if (Test-Path $WorkDir) { Remove-Item $WorkDir -Recurse -Force }
New-Item -ItemType Directory $WorkDir | Out-Null
$root = Join-Path $WorkDir 'fixtures'
$findings = [System.Collections.Generic.List[string]]::new()
$override = @()
if ($LibGit2SharpVersion) { $override += "-p:LibGit2SharpOverrideVersion=$LibGit2SharpVersion" }
if ($PackageSource) { $override += "-p:LibGit2SharpOverrideSource=$PackageSource" }

function Invoke-Probe {
    param([string] $Label, [string] $Exe, [string] $StartIn)
    $log = Join-Path $WorkDir "run-$Label.log"
    $canonical = Join-Path $WorkDir "canonical-$Label.txt"
    Push-Location $StartIn
    try { & $Exe --root $root --canonical $canonical *> $log } finally { Pop-Location }
    $code = $LASTEXITCODE
    $result = (Select-String -Path $log -Pattern '^RESULT:' | Select-Object -Last 1).Line
    Write-Host ("  {0,-22} exit {1}  {2}" -f $Label, $code, $result)
    if ($code -ne 0) {
        if (-not $result) { $script:findings.Add("${Label}: the probe ended without a result (exit $code), see $log") }
        Select-String -Path $log -Pattern '^\[FAIL\]' | ForEach-Object {
            Write-Host "      $($_.Line)" -ForegroundColor Red
            $script:findings.Add("${Label}: $($_.Line)")
        }
    }
    return $canonical
}

Write-Host '==> Building for the normal runtime' -ForegroundColor Cyan
& dotnet build $project -c Release --artifacts-path (Join-Path $WorkDir 'jit') -p:DisableGitVersionTask=true -p:Version=0.0.1 @override -v q -nologo *> (Join-Path $WorkDir 'build-jit.log')
if ($LASTEXITCODE -ne 0) { throw "Build failed, see $WorkDir\build-jit.log" }
$jitExe = Join-Path $WorkDir "jit\bin\GrayMoon.Worker.AotProbe\release\$exeName"

Write-Host '==> Publishing as Native AOT' -ForegroundColor Cyan
$aotDir = Join-Path $WorkDir 'aot'
& dotnet publish $project -c Release -r win-x64 --artifacts-path (Join-Path $WorkDir 'aot-obj') -o $aotDir -p:PublishAot=true -p:DisableGitVersionTask=true -p:Version=0.0.1 @override -nologo *> (Join-Path $WorkDir 'publish-aot.log')
if ($LASTEXITCODE -ne 0) { throw "AOT publish failed, see $WorkDir\publish-aot.log" }

$installDir = Join-Path $WorkDir 'install-layout'
$noNativeDir = Join-Path $WorkDir 'no-native'
New-Item -ItemType Directory $installDir, $noNativeDir | Out-Null
Copy-Item (Join-Path $aotDir $exeName) $installDir
Copy-Item (Join-Path $aotDir 'git2-*.dll') $installDir
Copy-Item (Join-Path $aotDir $exeName) $noNativeDir

Write-Host '==> Running the checks' -ForegroundColor Cyan
Write-Host "  LibGit2Sharp $((& $jitExe --version-only | Select-Object -Last 1))"
$jit = Invoke-Probe -Label 'normal-runtime' -Exe $jitExe -StartIn $WorkDir
$aot = Invoke-Probe -Label 'aot' -Exe (Join-Path $aotDir $exeName) -StartIn $WorkDir
$install = Invoke-Probe -Label 'aot-install-layout' -Exe (Join-Path $installDir $exeName) -StartIn $env:SystemRoot

Write-Host '==> Comparing the answers' -ForegroundColor Cyan
foreach ($pair in @(@('aot', $aot), @('aot-install-layout', $install))) {
    if (-not (Test-Path $pair[1])) { continue }
    $diff = @(Compare-Object (Get-Content $jit) (Get-Content $pair[1]) | Where-Object SideIndicator -eq '=>')
    Write-Host ("  {0,-22} {1} answer(s) differ from the normal runtime" -f $pair[0], $diff.Count)
    foreach ($line in $diff) {
        if ($line.InputObject -notmatch '= FAILED ') { $findings.Add("$($pair[0]): differs without a failed check: $($line.InputObject)") }
    }
}

Write-Host '==> Missing native library' -ForegroundColor Cyan
& (Join-Path $noNativeDir $exeName) --load-only *> (Join-Path $WorkDir 'run-no-native.log')
$noNativeCode = $LASTEXITCODE
Write-Host "  exit $noNativeCode  $((Get-Content (Join-Path $WorkDir 'run-no-native.log') | Select-Object -Last 1))"
if ($noNativeCode -ne 3) { $findings.Add('no-native: WorkerInstallProbe reported no damage although git2-*.dll is missing') }

Write-Host ''
if ($findings.Count -eq 0) {
    Write-Host 'PROVED: LibGit2Sharp gives the same answers as Native AOT.' -ForegroundColor Green
    exit 0
}

Write-Host "DISPROVED: $($findings.Count) finding(s). Logs: $WorkDir" -ForegroundColor Red
$findings | ForEach-Object { Write-Host "  $_" }
exit 1
