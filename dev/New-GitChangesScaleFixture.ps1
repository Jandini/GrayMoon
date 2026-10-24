# Creates synthetic dirty git trees for Git Changes scale benchmarking (Phase 0 / M0).
# Sizes: 100, 2_000, 5_000, 25_000, 50_000 untracked paths.
#
# Usage (from GrayMoon repo root):
#   .\dev\New-GitChangesScaleFixture.ps1
#   .\dev\New-GitChangesScaleFixture.ps1 -Root D:\tmp\gm-scale -Sizes 100,2000
#   .\dev\New-GitChangesScaleFixture.ps1 -MeasureOnly
#
# Each size folder is a standalone git repo with that many untracked files under files/.
# Point GrayMoon at -Root (or add one size folder as a workspace repository) and open Changes;
# filter logs for GitChangesBench.

[CmdletBinding()]
param(
    [string]$Root = (Join-Path $env:TEMP 'graymoon-git-changes-scale'),
    [int[]]$Sizes = @(100, 2000, 5000, 25000, 50000),
    [switch]$MeasureOnly,
    [switch]$SkipMeasure
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Ensure-GitRepo {
    param([string]$RepoPath, [int]$PathCount)

    if (-not (Test-Path -LiteralPath $RepoPath)) {
        New-Item -ItemType Directory -Path $RepoPath | Out-Null
    }

    Push-Location $RepoPath
    try {
        if (-not (Test-Path -LiteralPath (Join-Path $RepoPath '.git'))) {
            git init -q -b main | Out-Null
            Set-Content -LiteralPath (Join-Path $RepoPath 'README.md') -Value "scale fixture $PathCount" -Encoding utf8
            git add README.md | Out-Null
            git -c user.email=scale@graymoon.local -c user.name=ScaleFixture commit -q -m "initial" | Out-Null
        }

        $filesDir = Join-Path $RepoPath 'files'
        if (-not (Test-Path -LiteralPath $filesDir)) {
            New-Item -ItemType Directory -Path $filesDir | Out-Null
        }

        $existing = @(Get-ChildItem -LiteralPath $filesDir -File -ErrorAction SilentlyContinue).Count
        if ($existing -ge $PathCount) {
            Write-Host "  already has $existing files (>= $PathCount)"
            return
        }

        Write-Host "  creating $($PathCount - $existing) files (target $PathCount)..."
        $pad = [Math]::Max(4, "$PathCount".Length)
        for ($i = $existing; $i -lt $PathCount; $i++) {
            $bucket = '{0:D2}' -f ($i % 100)
            $dir = Join-Path $filesDir $bucket
            if (-not (Test-Path -LiteralPath $dir)) {
                New-Item -ItemType Directory -Path $dir | Out-Null
            }
            $name = ('f{0:D' + $pad + '}.txt') -f $i
            $path = Join-Path $dir $name
            # Small distinct content; untracked so git status --untracked-files=all lists every path.
            [System.IO.File]::WriteAllText($path, "scale-$i")
            if (($i + 1) % 5000 -eq 0) {
                Write-Host "    $($i + 1) / $PathCount"
            }
        }
    }
    finally {
        Pop-Location
    }
}

function Measure-Repo {
    param([string]$RepoPath, [int]$PathCount)

    Push-Location $RepoPath
    try {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $porcelain = & git --no-optional-locks status --porcelain=v2 -z --branch --untracked-files=all 2>&1
        $sw.Stop()
        $statusMs = [int]$sw.ElapsedMilliseconds

        if ($porcelain -is [System.Array]) {
            $text = [string]::Join([char]0, $porcelain)
        }
        else {
            $text = [string]$porcelain
        }
        $porcelainBytes = [System.Text.Encoding]::UTF8.GetByteCount($text)

        # Approximate SignalR notification size: camelCase JSON for PathCount untracked entries
        # (same shape as GitChangeEntry / GitChangesSnapshotNotification fields the Worker sends).
        $entries = New-Object System.Collections.Generic.List[object]
        $sampleCount = [Math]::Min($PathCount, 50)
        for ($i = 0; $i -lt $sampleCount; $i++) {
            $bucket = '{0:D2}' -f ($i % 100)
            $pad = [Math]::Max(4, "$PathCount".Length)
            $name = ('f{0:D' + $pad + '}.txt') -f $i
            $entries.Add([pscustomobject]@{
                    path           = "files/$bucket/$name"
                    originalPath   = $null
                    indexChange    = 0
                    worktreeChange = 1
                    isTracked      = $false
                    isConflicted   = $false
                    isSubmodule    = $false
                }) | Out-Null
        }
        $sampleJson = $entries | ConvertTo-Json -Compress -Depth 5
        $sampleBytes = [System.Text.Encoding]::UTF8.GetByteCount($sampleJson)
        $bytesPerEntry = if ($sampleCount -gt 0) { [int]($sampleBytes / $sampleCount) } else { 0 }
        # Envelope overhead roughly constant; path list dominates.
        $payloadUtf8Bytes = ($bytesPerEntry * $PathCount) + 256

        [pscustomobject]@{
            Paths            = $PathCount
            StatusMs         = $statusMs
            PorcelainBytes   = $porcelainBytes
            SnapshotBytesEst = $payloadUtf8Bytes
            BytesPerEntryEst = $bytesPerEntry
            RepoPath         = $RepoPath
        }
    }
    finally {
        Pop-Location
    }
}

Write-Host "Fixture root: $Root"
if (-not $MeasureOnly) {
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
}

$results = @()
foreach ($size in $Sizes) {
    $repo = Join-Path $Root ("paths-{0}" -f $size)
    Write-Host "`n=== $size paths ==="
    if (-not $MeasureOnly) {
        Ensure-GitRepo -RepoPath $repo -PathCount $size
    }
    elseif (-not (Test-Path -LiteralPath (Join-Path $repo '.git'))) {
        Write-Warning "Missing repo at $repo (run without -MeasureOnly first)"
        continue
    }

    if (-not $SkipMeasure) {
        $row = Measure-Repo -RepoPath $repo -PathCount $size
        $results += $row
        Write-Host ("  StatusMs={0} PorcelainBytes={1} SnapshotBytesEst={2} BytesPerEntryEst={3}" -f `
                $row.StatusMs, $row.PorcelainBytes, $row.SnapshotBytesEst, $row.BytesPerEntryEst)
    }
}

if ($results.Count -gt 0) {
    Write-Host "`nMarkdown table (Worker-measurable columns; fill PersistMs/ReadMs/BuildTreeMs/Rows from App GitChangesBench logs):"
    Write-Host '| Paths | Snapshot bytes (est.) | StatusMs | PorcelainBytes | BytesPerEntry (est.) |'
    Write-Host '|---|---|---|---|---|'
    foreach ($r in $results) {
        Write-Host ("| {0} | {1} | {2} | {3} | {4} |" -f `
                $r.Paths, $r.SnapshotBytesEst, $r.StatusMs, $r.PorcelainBytes, $r.BytesPerEntryEst)
    }
}

Write-Host @"

Next:
  1. Add one size folder (e.g. $Root\paths-5000) as a repository in a GrayMoon Workspace.
  2. Open Changes and search logs for GitChangesBench (App + Worker under %ProgramData%\GrayMoon\Logs).
  3. Copy PersistMs / ReadMs / BuildTreeMs / RowCountRendered into the design tracker table.
"@
