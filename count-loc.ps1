# Count lines of code in the GrayMoon repository.
# Excludes bin, obj, node_modules, packages, .vs, and other generated/output folders.
# Run from repo root: .\count-loc.ps1

param(
    [string]$Root = $PSScriptRoot,
    [switch]$ByExtension,
    [switch]$ByDirectory
)

$excludeDirs = @(
    'bin', 'obj', 'node_modules', 'packages', '.vs', '.git', 'artifacts',
    'Debug', 'Release', 'x64', 'x86', 'bld', 'out', 'log', 'logs',
    'PublishScripts', 'Generated', 'Generated_Code', '_UpgradeReport_Files',
    'coverage', '.axoCover', 'OpenCover', 'TestResults', 'BenchmarkDotNet.Artifacts',
    'scopedcss', 'projectbundle', 'bundle'
)

$includeExtensions = @(
    '*.cs', '*.razor', '*.cshtml', '*.css', '*.scss', '*.js', '*.ts', '*.jsx', '*.tsx',
    '*.json', '*.xml', '*.yml', '*.yaml', '*.md', '*.ps1', '*.sh', '*.sql', '*.html'
)

# Resolve root
if (-not [System.IO.Path]::IsPathRooted($Root)) {
    $Root = Join-Path (Get-Location) $Root
}
$Root = [System.IO.Path]::GetFullPath($Root)

if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
    Write-Error "Root not found: $Root"
    exit 1
}

function ShouldExcludePath {
    param([string]$Path)
    $rel = $Path.Replace($Root, '').TrimStart('\', '/')
    foreach ($d in $excludeDirs) {
        if ($rel -eq $d -or $rel.StartsWith($d + [IO.Path]::DirectorySeparatorChar) -or $rel.StartsWith($d + '/')) {
            return $true
        }
        $sep = [regex]::Escape([IO.Path]::DirectorySeparatorChar)
        $seg = $rel -split $sep
        if ($seg -contains $d) { return $true }
    }
    return $false
}

$totalLines = 0
$byExt = @{}
$byDir = @{}
$fileCount = 0

Get-ChildItem -Path $Root -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object {
        if (ShouldExcludePath $_.FullName) { return $false }
        $match = $false
        foreach ($pat in $includeExtensions) {
            if ($_.Name -like $pat) { $match = $true; break }
        }
        $match
    } |
    ForEach-Object {
        $fullName = $_.FullName
        $ext = $_.Extension
        if ($ext -eq '') { $ext = '(no ext)' }
        $dir = [IO.Path]::GetDirectoryName($fullName).Replace($Root, '').TrimStart('\', '/')
        if ($dir -eq '') { $dir = '(root)' }

        $lines = 0
        try {
            $lines = (Get-Content -LiteralPath $fullName -ErrorAction Stop | Measure-Object -Line).Lines
        } catch {
            # skip binary or locked files
            return
        }

        $script:totalLines += $lines
        $script:fileCount += 1

        if (-not $byExt.ContainsKey($ext)) { $byExt[$ext] = @{ Lines = 0; Count = 0 } }
        $byExt[$ext].Lines += $lines
        $byExt[$ext].Count += 1

        if ($ByDirectory) {
            if (-not $byDir.ContainsKey($dir)) { $byDir[$dir] = @{ Lines = 0; Count = 0 } }
            $byDir[$dir].Lines += $lines
            $byDir[$dir].Count += 1
        }
    }

Write-Host "GrayMoon - Lines of code" -ForegroundColor Cyan
Write-Host ("Root: {0}" -f $Root) -ForegroundColor Gray
Write-Host ""
Write-Host ("Total: {0} lines in {1} files" -f $totalLines.ToString("N0"), $fileCount.ToString("N0")) -ForegroundColor White
Write-Host ""

if ($ByExtension) {
    Write-Host "By extension:" -ForegroundColor Cyan
    $byExt.GetEnumerator() | Sort-Object { $_.Value.Lines } -Descending | ForEach-Object {
        $ext = $_.Key
        $lines = $_.Value.Lines
        $count = $_.Value.Count
        Write-Host ("  {0,-10} {1,10} lines   {2,5} files" -f $ext, $lines.ToString("N0"), $count)
    }
    Write-Host ""
}

if ($ByDirectory) {
    Write-Host "By directory (top 20 by lines):" -ForegroundColor Cyan
    $byDir.GetEnumerator() | Sort-Object { $_.Value.Lines } -Descending | Select-Object -First 20 | ForEach-Object {
        $dir = $_.Key
        $lines = $_.Value.Lines
        $count = $_.Value.Count
        Write-Host ("  {0,-50} {1,10} lines   {2,5} files" -f $dir, $lines.ToString("N0"), $count)
    }
    Write-Host ""
}

if (-not $ByExtension -and -not $ByDirectory) {
    Write-Host 'Use -ByExtension and/or -ByDirectory for a breakdown.' -ForegroundColor Gray
}
