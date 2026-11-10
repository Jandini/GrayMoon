# Measures the git commands the commit timeline design depends on. Windows PowerShell 5.1 or later.
# Creates one synthetic repository in a temp folder, prints a Markdown table, deletes nothing outside that folder.
param([string]$RealRepositoryPath = "", [string]$RealBaseRef = "")

$ErrorActionPreference = "Stop"
$root = Join-Path ([IO.Path]::GetTempPath()) ("gm-timeline-bench-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$repo = Join-Path $root "r"
New-Item -ItemType Directory -Force $repo | Out-Null

function Invoke-Git([string]$workDir, [string]$arguments, [string]$stdin = $null) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "git"
    $psi.Arguments = $arguments
    $psi.WorkingDirectory = $workDir
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardInput = ($null -ne $stdin)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = [Diagnostics.Process]::Start($psi)
    if ($null -ne $stdin) { $p.StandardInput.Write($stdin); $p.StandardInput.Close() }
    $out = $p.StandardOutput.ReadToEnd()
    $p.WaitForExit()
    $sw.Stop()
    return New-Object PSObject -Property @{ Ms = $sw.Elapsed.TotalMilliseconds; Out = $out; Exit = $p.ExitCode }
}

$rows = New-Object System.Collections.Generic.List[string]
function Measure-Git([string]$label, [string]$workDir, [string]$arguments, [string]$stdin = $null) {
    $first = Invoke-Git $workDir $arguments $stdin
    if ($first.Exit -ne 0 -and $arguments -notlike "merge-base*") { throw "git $arguments failed with exit code $($first.Exit)" }
    $times = @(1..9 | ForEach-Object { (Invoke-Git $workDir $arguments $stdin).Ms } | Sort-Object)
    $rows.Add(("| {0} | {1:N0} | {2:N0} | {3:N0} | {4:N0} |" -f $label, $first.Ms, $times[0], $times[4], $times[8]))
}

# --- Build the fixture: 400 files on main, then 181 commits on feature (5 files each, 60 in every tenth) ---
$buildTimer = [Diagnostics.Stopwatch]::StartNew()
$rng = New-Object System.Random 1
$sb = New-Object System.Text.StringBuilder
function Add-Commit([string]$ref, [int]$n, [int]$fileCount, [int]$from) {
    [void]$sb.Append("commit refs/heads/$ref`nmark :$n`ncommitter T <t@example.com> $(1700000000 + $n * 600) +0000`n")
    $message = "commit $n`n"
    [void]$sb.Append("data $($message.Length)`n$message")
    if ($from -gt 0) { [void]$sb.Append("from :$from`n") }
    for ($j = 0; $j -lt $fileCount; $j++) {
        $f = $j
        if ($n -ne 1) { $f = $rng.Next(400) }
        $body = New-Object System.Text.StringBuilder
        for ($i = 0; $i -lt 300; $i++) { [void]$body.Append("line $n $i $($rng.Next())`n") }
        [void]$sb.Append("M 100644 inline src/d$($f % 20)/f$f.cs`ndata $($body.Length)`n$($body.ToString())`n")
    }
    [void]$sb.Append("`n")
}
Add-Commit "main" 1 400 0
for ($n = 2; $n -le 182; $n++) {
    $count = 5
    if ($n % 10 -eq 0) { $count = 60 }
    Add-Commit "feature" $n $count ($n - 1)
}
$streamPath = Join-Path $root "stream.txt"
[IO.File]::WriteAllText($streamPath, $sb.ToString(), (New-Object System.Text.ASCIIEncoding))
[void](Invoke-Git $repo "init -q --initial-branch=main .")
cmd /c "git -C `"$repo`" fast-import --quiet < `"$streamPath`""
if ($LASTEXITCODE -ne 0) { throw "git fast-import failed" }
[void](Invoke-Git $repo "checkout -q feature")
$buildTimer.Stop()

$ahead = (Invoke-Git $repo "rev-list --count main..feature").Out.Trim()
if ($ahead -ne "181") { throw "Fixture is wrong: expected 181 commits ahead, got '$ahead'" }
$tip = (Invoke-Git $repo "rev-parse feature").Out.Trim()
$prev = (Invoke-Git $repo "rev-parse feature~1").Out.Trim()
$big = (Invoke-Git $repo "rev-parse feature~2").Out.Trim()
$shas = (Invoke-Git $repo "rev-list main..feature").Out
$format = "--format=%H%x1f%P%x1f%an%x1f%aI%x1f%cI%x1f%s"

Measure-Git "Process start floor (``git --version``)" $repo "--version"
Measure-Git "``rev-list --count`` (the ahead badge)" $repo "rev-list --count main..feature"
Measure-Git "``log`` 181 commits, metadata only" $repo "log --reverse --no-color $format main..feature"
Measure-Git "``log`` 181 commits with ``--name-status``" $repo "log --reverse --no-color $format --name-status main..feature"
Measure-Git "``log`` 181 commits with ``--numstat``" $repo "log --reverse --no-color $format --numstat main..feature"
Measure-Git "``log`` one new commit (incremental)" $repo "log --no-color $format $prev..$tip"
Measure-Git "``merge-base --is-ancestor``" $repo "merge-base --is-ancestor $prev $tip"
Measure-Git "``diff-tree`` one commit, 5 files" $repo "diff-tree -r -M --no-commit-id --numstat $tip"
Measure-Git "``diff-tree`` one commit, 60 files" $repo "diff-tree -r -M --no-commit-id --numstat $big"
Measure-Git "``diff -M --numstat main...feature`` (all commits combined)" $repo "diff -M --numstat main...feature"
Measure-Git "``diff-tree --stdin --numstat``, 181 commits, one process" $repo "diff-tree --stdin -r --numstat" $shas
Measure-Git "``show <sha>:<path>`` (one side of a diff)" $repo "show ${tip}:src/d0/f0.cs"

if ($RealRepositoryPath -ne "" -and $RealBaseRef -ne "") {
    $realAhead = (Invoke-Git $RealRepositoryPath "rev-list --count $RealBaseRef..HEAD").Out.Trim()
    Measure-Git "REAL repository: ``log`` $realAhead commits, metadata only" $RealRepositoryPath "log --reverse --no-color $format $RealBaseRef..HEAD"
    Measure-Git "REAL repository: ``log`` $realAhead commits with ``--name-status``" $RealRepositoryPath "log --reverse --no-color $format --name-status $RealBaseRef..HEAD"
    Measure-Git "REAL repository: ``diff-tree`` HEAD" $RealRepositoryPath "diff-tree -r -M --no-commit-id --numstat HEAD"
    Measure-Git "REAL repository: ``diff -M --numstat`` base...HEAD" $RealRepositoryPath "diff -M --numstat $RealBaseRef...HEAD"
}

# --- Parallel sweep: 48 metadata log calls, P at a time (stands in for a backfill of 48 repositories) ---
$sweep = New-Object System.Collections.Generic.List[string]
foreach ($parallel in 1, 2, 4, 8, 16) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $done = 0
    while ($done -lt 48) {
        $batch = @()
        for ($k = 0; $k -lt $parallel -and $done -lt 48; $k++, $done++) {
            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName = "git"; $psi.Arguments = "log --reverse --no-color $format main..feature"
            $psi.WorkingDirectory = $repo; $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
            $psi.RedirectStandardOutput = $true
            $p = [Diagnostics.Process]::Start($psi)
            $batch += New-Object PSObject -Property @{ Process = $p; Read = $p.StandardOutput.ReadToEndAsync() }
        }
        foreach ($b in $batch) { [void]$b.Read.Result; $b.Process.WaitForExit() }
    }
    $sw.Stop()
    $sweep.Add(("| {0} | {1:N0} | {2:N0} |" -f $parallel, $sw.Elapsed.TotalMilliseconds, ($sw.Elapsed.TotalMilliseconds / 48)))
}

"Git: $((Invoke-Git $repo '--version').Out.Trim())"
"PowerShell: $($PSVersionTable.PSVersion)   Logical processors: $([Environment]::ProcessorCount)"
"Fixture build: $([int]$buildTimer.Elapsed.TotalSeconds) s"
""
"| Command | First run ms | Min ms | Median ms | Max ms |"
"|---|---|---|---|---|"
$rows
""
"| Parallel processes | 48 calls total ms | ms per call |"
"|---|---|---|"
$sweep
""
"Fixture folder (safe to delete): $root"
