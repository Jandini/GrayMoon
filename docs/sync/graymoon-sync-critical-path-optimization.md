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
| 2 Hook location cache | Implemented, tests green, awaiting VDI benchmark |
| 3 GitVersion fingerprint cache | Implemented, tests green, awaiting VDI benchmark |
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

## Unit 2 - Hook location cache

### Problem
Every Sync ran `git rev-parse --git-common-dir --git-path hooks` (plus `git config --get core.hooksPath` when the
hooks path pointed outside the common git directory) from `GitService.ResolveGitHooksLocationAsync`, only to learn
an answer that almost never changes. About 3-4 s per repository on the VDI.

### Design
- `GitHooksLocationCache` (Worker/Services) sits between `GitService.WriteSyncHooksCoreAsync` and the native
  resolution. `SyncRepositoryCommand` is untouched.
- Native git stays authoritative: the cache only replays an answer git already gave, for the same state. The first
  Sync of a repository after a worker start still asks git once.
- Fingerprint (SHA-256), computed from the file system only: repository path, git dir and common dir (`.git` folder,
  or the `gitdir:` target plus `commondir` for linked worktrees), existence/size/write time/content hash of the local
  `config`, `config.worktree`, global (`~/.gitconfig`, XDG) and system gitconfig files, and the environment
  (`HOME`, `USERPROFILE`, `XDG_CONFIG_HOME`, `GIT_WORK_TREE`, `PATH`, every `GIT_CONFIG*`).
- Always native (never cached): `GIT_DIR` or `GIT_COMMON_DIR` set, a layout `GitDirectoryLocator` does not recognise,
  an unreadable config file, or any stamped config containing `[include`/`[includeIf`.
- Failed resolutions are not cached. The fingerprint is re-checked after the native call, so an answer is only kept
  for the state it was asked in.
- Hooks are still compared with what is on disk and rewritten only when different (unchanged behaviour, no process).
- Debug line per install: `Hooks location for {RepoPath}: Cache | NativeCached | NativeUncacheable`.

### Tests
`GitHooksLocationCacheTests` (10): zero processes on unchanged repos and hook files not rewritten; `core.hooksPath`
changed to an inside folder; absolute outside path remembered (2 processes once, then 0) and never written to,
and removal of the setting; relative path; linked worktree (common hooks folder, own cache entry); worktree removed
and re-added; include directive always native; repository recreated at the same path; failed resolution not cached;
concurrent installs. The existing process-count test (1 / 1 / 2 on first call) still passes.
Full Worker suite: 541 passed, 1 skipped (pre-existing), 0 failed.

### Known limitations
- System gitconfig is found by locating `git` on `PATH` (`<root>/etc/gitconfig`, `<root>/mingw64/etc/gitconfig`,
  `/etc/gitconfig`); an unusual install is not stamped. Editing the system config to change `core.hooksPath` without
  restarting the worker could therefore go unnoticed. Env-var changes are not covered by a test (process-wide state).
- In memory only: one git call per repository after each worker restart.

### Enterprise benchmark
Pending.

| Metric | Before | After |
|---|---|---|
| hook-path git.exe per unchanged Sync | 1 (2 with outside hooksPath) | 0 (expected) |
| Repository total median / P90 | | |
| Sync wall time (workspace) | | |

## Unit 3 - GitVersion result cache

### Problem
GitVersion costs about 4-6 s per repository on the VDI, mostly process startup. Its own `.git/gitversion_cache`
only helps after the process is already running.

### Design
- `GitVersionResultCache` (Worker/Services, singleton) wraps `GitVersionRepositoryVersionProvider`. The provider takes
  it as an optional constructor argument, so call sites that build the provider without a cache behave as before.
- A hit returns the previous successful result without starting GitVersion. GitVersion's private cache is never read.
- `GitVersionInputFingerprint` computes the inputs in process (LibGit2Sharp and file reads), as separate digests:
  - `Head`: tip SHA. `HeadIdentity`: symbolic target of HEAD, or `detached`.
  - `Refs`: sorted name and target of every ref under `refs/` except `refs/stash` (heads, remotes, tags, others), plus
    the `shallow` file of the common git dir (so linked worktrees see refs created elsewhere).
  - `Config`: content of every GitVersion config file name in the working tree root.
  - `Tool`: hash of the repository's `dotnet-tools.json` (root or `.config`) when present, otherwise the global
    `dotnet-gitversion` found on `PATH` (size, write time) plus the version folders in `.store/gitversion.tool`.
  - `Invocation`: NonNormalize, CommitSha, any `GITVERSION*` environment variable.
- Entries are keyed by repository, NonNormalize and CommitSha, so alternating requests do not evict each other.
- Only successes are stored (`Probed`, no error, a result). Failures and exceptions are never cached. The fingerprint
  is re-taken after the run and the result is stored only if it did not move meanwhile.
- Identical concurrent requests (same key and inputs) share one run. If the request doing the run is cancelled, the
  others retry rather than inherit the cancellation. Each caller gets its own copy of the (mutable) result.
- Bounded: 256 entries, oldest use evicted. In memory only, no database.
- Bypassed (GitVersion runs, nothing cached): unborn HEAD, bare or unreadable repository, global tool not found on
  `PATH`, or a GitVersion config containing `UncommittedChanges` (the format would then depend on the working tree).
- Working-tree edits are not an input. Parity test (real GitVersion, tag plus a commit, then a staged edit and an
  untracked file): `InformationalVersion`, `BranchName`, `EscapedBranchName` are identical clean and dirty, and those
  are the only fields GrayMoon reads (`GitVersionResult` has just those three).

### Telemetry (Debug, no repository content)
`GitVersion cache hit for {RepoPath}`; `GitVersion cache miss for {RepoPath}: {Reason}` with Reason one of
`NO_PREVIOUS_SUCCESS`, `HEAD_CHANGED`, `HEAD_IDENTITY_CHANGED`, `REFS_CHANGED`, `CONFIG_CHANGED`, `TOOL_CHANGED`,
`INVOCATION_CHANGED` (first differing input, in that precedence order of tool/config before head/refs);
`GitVersion executed in {ElapsedMs}ms for {RepoPath}`; `GitVersion cache bypass ... inputs cannot be fingerprinted`.

### Tests
`GitVersionResultCacheTests` (20): unchanged repo hit; new commit (no stale result); branch switch at same commit; new
and moved tag; local and remote ref changes; config change; `UncommittedChanges` config never cached; tool identity
change; NonNormalize and CommitSha (no thrash between them); detached HEAD; linked worktree (own entry, shared ref
changes seen, own commit); failure then success; exception; 8 concurrent identical requests share one run; cancelled
first request does not fail the waiter; bounded; unborn HEAD bypass; working-tree edits keep the hit; real GitVersion
parity clean vs dirty; real provider starts `dotnet-gitversion` once for two requests and once more after a commit.
Full Worker suite: 561 passed, 1 skipped (pre-existing), 0 failed.

### Known limitations
- Anything GitVersion reads that is not in the inputs above is not tracked: CI environment variables other than
  `GITVERSION*`, grafts, and an uncommon tool location resolved by `dotnet` rather than `PATH`.
- The first Sync after a worker restart still runs GitVersion once per repository (in memory only).
- Cached version is not re-verified against GitVersion; if a bug makes inputs look unchanged, restarting the worker
  clears the cache.

### Enterprise benchmark
Pending.

| Metric | Before | After |
|---|---|---|
| GitVersion launches per unchanged repository per Sync | 1 | 0 (expected) |
| Repository total median / P90 | | |
| Sync wall time (workspace) | | |

## Accepted / rejected ideas
- Rejected: `Task.Delay` or time-window ignore of watcher events, global `IsSyncing` flag (brittle, not per repository).
- Rejected: parallelising the LibGit2Sharp snapshot internally (off the critical path).

## Remaining bottlenecks
Per the prompt: GitVersion (about 6 s), fetch, and the per-repository hook-location git.exe.
