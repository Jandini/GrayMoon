# GrayMoon Sync critical path optimization

Living document. Principle: do less work and launch fewer processes before adding more parallelism.
Prompt: `docs/git-critical/graymoon-sync-critical-path-optimization-prompt.md`.

## Baseline (from the prompt; the benchmark report itself was not available in the repo)

- Local snapshot (LibGit2Sharp): about 50-60 ms median per repository, no longer the critical path.
- GitVersion: about 6 s per repository warm on the enterprise VDI.
- Fetch: several seconds per repository (network bound, native git).
- Each git.exe launch costs seconds on the VDI (AV/EDR), so process count and concurrency matter.

## Unit status

| Unit | Status |
|---|---|
| 1 Sync / Git Changes coordination | Implemented, tests green, awaiting VDI benchmark |
| 2 Hook location cache | Planned |
| 3 GitVersion fingerprint cache | Planned |
| 4 Fetch policy review | Planned |

## Unit 1 - Sync / Git Changes coordination

### Problem
`git fetch` rewrites refs under `.git`. `GitRepositoryWatcher` reports that as a repository change, `MarkDirty`
arms a 400 ms debounce and a `git status --porcelain=v2 -z --branch --untracked-files=all` runs, for every watched
repository, while Sync is still running fetch / GitVersion / reads. `SyncRepositoryCommand` had no link to the
coordinator.

### Design
- `IGitChangesRefreshSuppressor.BeginExternalRepositoryMutation(repoPath)` returns an `IDisposable` scope
  (Worker/Abstractions). `GitStatusRefreshCoordinator` implements it; the Git Changes services stay separate.
- State is per repository, in `RepositoryRefreshTracker`: an open-scope count, a "dirtied while suppressed" flag and
  per-episode counters. Counting makes overlapping scopes on one repository release together; separate repositories
  have separate trackers, so one finishing never releases another.
- While the count is above zero a watcher-originated `MarkDirty` only sets the flag. A debounce timer armed before the
  scope opened is neutralised when it fires (the repository stays Dirty, the flag is set).
- Closing the last scope schedules exactly one refresh, through the normal debounced path, only if the flag was set.
  Going through the debounce also absorbs FileSystemWatcher events that arrive just after the last git process exits.
- Not suppressed: `RefreshNowAsync` (manual and on-demand), stage / unstage / commit, watcher observation recording
  (`Observed` still fills the buffer, so external changes are not lost).
- `SyncRepositoryCommand` opens the scope after the clone step, around the whole fetch-to-response block, with
  `using`, so a failed fetch, an exception or a cancellation all release it. The scope parameter is optional
  (`refreshSuppressor`), so existing construction sites are unchanged.
- No scheduled refresh when no watcher event was seen: same behaviour as before for unwatched repositories (Sync
  does not start `git status` on repositories nobody asked about).

### Telemetry (Debug, two lines per repository per Sync, never per file event)
- `Git Changes refresh suppression entered for {RepoPath}`
- `Git Changes refresh suppression released for {RepoPath}: duration, watcherRefreshesCoalesced, manualRefreshBypasses, authoritativeRefreshScheduled`

### Known edge
If the watcher lease expires (10 min idle) while a Sync is mid-flight, `RemoveTracker` drops the tracker; the scope
keeps its own reference and releases cleanly, but a watcher recreated during that window starts unsuppressed. Not
expected in practice (leases are renewed while a workspace is active).

### Tests
`GitStatusRefreshSuppressionTests` (12): events during a scope give no scan and one scan on release; quiet scope
gives none; debounce armed before the scope; events during an in-flight scan queue no follow-up; manual refresh
bypasses; two repositories independent; overlapping scopes; double dispose; failed and cancelled operations release;
tracker removed mid-scope; Sync releases its scope on a failed fetch.
Full Worker suite: 531 passed, 1 skipped (pre-existing), 0 failed.

### Enterprise benchmark
Pending (39 repositories, 16 workers, restart, ignore first Sync, at least 3 quiet warm Syncs). Compare the
`git status` process count during Sync and the per-repository totals before and after.

| Metric | Before | After |
|---|---|---|
| git status processes during Sync | | |
| Sync wall time (workspace) | | |
| Repository total median / P90 | | |
| Max simultaneous git.exe | | |

## Accepted / rejected ideas
- Rejected: `Task.Delay` or time-window ignore of watcher events, global `IsSyncing` flag (brittle, not per repository).
- Rejected: parallelising the LibGit2Sharp snapshot internally (off the critical path).

## Remaining bottlenecks
Per the prompt: GitVersion (about 6 s), fetch, and the per-repository hook-location git.exe.
