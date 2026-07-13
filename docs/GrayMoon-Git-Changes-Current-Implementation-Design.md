# Git Changes Feature - Current Implementation Design

This document describes **what is actually built and running today** for the Git Changes feature
(`/workspaces/{id}/changes`), as opposed to the original spec (`GrayMoon-Git-Changes-Design-Implementation-Prompt-v6.md`)
or the staged implementation plan (`docs-graymoon-git-changes-design-implem-compiled-tiger.md`). Those two
documents describe *intent*; this document describes *what the code does right now*, including a bug that
was found and fixed (background monitoring never started) and a few gaps that still exist.

Read this before making further changes to Git Changes - it should let you answer "which file owns this
behaviour" and "what happens when X occurs" without re-reading every source file.

---

## 1. Who does what: App vs Agent

GrayMoon is a two-process system. This split is the single most important fact for understanding Git Changes:

- **GrayMoon.App** (Docker container, Blazor Server, SQLite). Never touches the local filesystem. Never runs
  `git`. Only ever *asks* the Agent to do things, over one persistent SignalR connection (`/hub/agent`).
- **GrayMoon.Agent** (runs on the developer's machine as a console app / Windows Service / systemd unit).
  This is the **only** process that runs `git` and the **only** process that owns `FileSystemWatcher`
  instances. It has no database of its own and no independent notion of "which workspaces exist" beyond
  whatever the App tells it in each request's `workspaceRoot`/`workspaceName`/`repositoryName` fields.

**Answer to "who does the file watchers": the Agent does.** `GitRepositoryWatcher` and
`GitRepositoryWatcherManager` live in `src/GrayMoon.Agent/Services/GitChanges/` and are Agent-only. The App
never sees a `FileSystemWatcher` and cannot create one (it doesn't run on the developer's machine and, even
in same-machine dev setups, is architecturally not supposed to touch the filesystem directly).

The Agent, however, is purely reactive: it does not know on its own which repositories to watch. It only
starts watching a repository the moment some App-issued command mentions that repository's path. This is the
crux of the bug described in Section 6.

---

## 2. Data model

### 2.1 Wire/domain model (`GrayMoon.Common/Git/GitChangeModels.cs`)

Pure data, no IO, shared by both processes:

- `GitChangeKind` - `None/Added/Modified/Deleted/Renamed/Copied/TypeChanged/Unmerged/Untracked`.
- `GitChangeEntry` - one changed path. Carries an `IndexChange` (staged) and a `WorktreeChange` (unstaged)
  kind independently, since porcelain v2 reports both per path. `IsStaged` / `IsChanged` are derived.
- `GitChangeSnapshot` - one versioned point-in-time scan result for a repository: branch name, head commit,
  detached/unborn/merging/rebasing/cherry-picking flags, the full `Changes` list, and `ScannedAt`. `Version`
  is a per-repository monotonically increasing `long` (see 2.2) used to reject out-of-order writes.
- `GitChangeOperationScope` - `ExplicitPaths/Folder/Repository/MultipleRepositories/EntireSection`, the
  explicit scope a stage/unstage mutation applies to (never inferred from what's currently rendered).
- `GitDiffComparison` - `Staged` (HEAD -> Index) or `Unstaged` (Index -> Working tree).
- `GitDiffDocument` / `GitDiffContentState` - diff payload plus a state enum
  (`Normal/NewFile/DeletedFile/Binary/TooLarge/UnsupportedEncoding/Error`) so the UI never has to guess why a
  diff can't render as plain text.
- `GitMutationResult` / `GitCommitResult` - stage/unstage/commit results, each carrying the post-operation
  `Snapshot` so the caller never has to make a second round trip to see the effect of its own mutation.

### 2.2 Snapshot versioning (`GitChangesSnapshotCache`, Agent-only, singleton)

A `ConcurrentDictionary<string, long>` keyed by normalized repo path. `NextVersion(repoPath)` increments and
returns the next version for that repo. Every status scan, and every stage/unstage/commit mutation, calls this
before running so the resulting `GitChangeSnapshot.Version` is strictly increasing per repository. The App's
persistence layer (`GitChangesSnapshotPushHandler`) uses this to reject stale writes if two updates for the
same repo race each other.

### 2.3 Persisted read model (App-side SQLite, `Migrations.cs` -> `MigrateWorkspaceGitChangesAsync`)

Two tables, created via the same `sqlite_master`-check-then-`CREATE TABLE` idiom used elsewhere in
`Migrations.cs` (not an EF Core migrations assembly - see `CLAUDE.md`):

```text
WorkspaceGitRepositoryStatus   (1:1 on WorkspaceRepositoryId, FK cascade delete)
    WorkspaceRepositoryId (PK), SnapshotVersion, BranchName, HeadCommit,
    IsDetachedHead, IsUnbornBranch, IsMerging, IsRebasing, IsCherryPicking,
    StagedCount, ChangedCount, ConflictCount,
    AgentScannedAt, PersistedAt, LastErrorCode, LastErrorMessage

WorkspaceGitChangeEntries      (many per WorkspaceRepositoryId, delete+reinsert every write)
    WorkspaceGitChangeEntryId (PK), WorkspaceRepositoryId (FK, indexed),
    Path, OriginalPath, IndexChange, WorktreeChange, IsTracked, IsConflicted, IsSubmodule
```

This is a derived cache, not a source of truth - it can be dropped and rebuilt from git at any time. Keying on
`WorkspaceRepositoryId` (not a separate `WorkspaceId`+`RepositoryId` pair) means workspace/repository deletion
cleans this up for free via the existing FK cascade chain, same pattern as
`WorkspaceRepositoryPullRequest`/`WorkspaceRepositoryAction`.

---

## 3. Agent-side pipeline (the watching/scanning half)

### 3.1 `GitRepositoryWatcher` (one instance per watched repo path)

Two native `FileSystemWatcher`s per repository:

- **Work-tree watcher**: recursive, on the repo root, `NotifyFilters.LastWrite|FileName|DirectoryName|Size`.
  Explicitly ignores any path containing `\.git\` so routine object writes don't double-fire alongside the
  git-dir watcher.
- **Git-dir watcher**: recursive, on `<repo>\.git`, but only events touching `index`, `HEAD`, `packed-refs`,
  `MERGE_HEAD`, `CHERRY_PICK_HEAD`, `rebase-merge`, `rebase-apply`, or anything under `refs/` are treated as
  relevant - everything else (loose objects, logs) is ignored.

Both watchers only ever raise an **invalidation hint** (`Changed` event, no payload) - per the design's own
rule, a watcher event must never be used to reconstruct state; it only ever triggers a fresh authoritative
`git status` scan. On a watcher `Error` (overflow or failure) the watcher disposes and recreates itself and
raises `Overflowed`, which is treated the same as `Changed` (mark dirty, rescan).

### 3.2 `GitRepositoryWatcherManager` (Agent singleton)

Reference-counted lease manager keyed by normalized repo path:

- `Acquire(repoPath)` returns an `IDisposable` lease. First lease creates the `GitRepositoryWatcher` (wires
  `Changed`/`Overflowed` to `GitStatusRefreshCoordinator.MarkDirty(repoPath)`); subsequent leases just bump a
  refcount and cancel any pending idle-disposal timer.
- Releasing the last lease does **not** dispose the watcher immediately - it starts an idle timer
  (`GitChangesOptions.WatcherIdleGraceMinutes`, default 10 minutes). If nothing re-acquires a lease before the
  timer fires, the watcher is disposed.
- **This is the only place a `FileSystemWatcher` gets created anywhere in GrayMoon.** If nothing ever calls
  `Acquire`, nothing is ever watched - see Section 6.

### 3.3 `GitChangesRepositoryRegistry` (Agent singleton)

A `ConcurrentDictionary<string, (int WorkspaceId, int RepositoryId)>`. `Register(repoPath, workspaceId,
repositoryId)` is called every time `GetGitChangeStatusCommand` runs. This exists purely so a watcher-driven
refresh - which only ever knows a filesystem path - can be attributed back to a `(WorkspaceId, RepositoryId)`
pair when pushing an update to the App. A path with no registry entry is silently dropped by
`GitChangesSnapshotPublisher` (Section 3.5); this is intentional (the App never asked about that repo, so it
has no row to update).

### 3.4 `GitStatusRefreshCoordinator` (Agent singleton) - debounce + fan-in

Per-repository state machine: `Clean -> Dirty -> Refreshing -> RefreshingAndDirty -> Disposed` (implemented in
`RepositoryRefreshTracker`, one per repo, inside a `ConcurrentDictionary`).

- `MarkDirty(repoPath)` (called from a watcher event): if `Clean`, starts a debounce timer
  (`GitChangesOptions.WatcherDebounceMilliseconds`, default 400 ms) and moves to `Dirty`. If already
  `Dirty`/`RefreshingAndDirty`, does nothing (already scheduled). If `Refreshing`, moves to
  `RefreshingAndDirty` so exactly one follow-up scan runs after the current one finishes.
- `RefreshNowAsync(repoPath, ct)` (called from `GetGitChangeStatusCommand`, i.e. an explicit request):
  bypasses the debounce timer but still coalesces with an in-flight scan for the same repo - a second caller
  while a scan is running gets the *same* result via a shared `TaskCompletionSource`, it never causes two
  concurrent `git status` invocations for one repository.
- All actual scans go through one shared `SemaphoreSlim` sized to
  `GitChangesOptions.MaxParallelRepositoryOperations` (default 16) - this is the "up to 16 repositories may
  scan concurrently, but never two scans for the same repo" rule from the design doc, implemented as
  "one global bounded semaphore + one per-repo state machine" rather than a generic priority-queue scheduler
  (a deliberate downscope from the original spec, see the compiled plan).
- On a successful scan, raises `SnapshotReady(repoPath, snapshot)` and calls
  `GitChangesSnapshotCache.SetLatest`.

### 3.5 `GitChangesSnapshotPublisher` (Agent `IHostedService`, constructed eagerly, no start/stop work)

Subscribes to `GitStatusRefreshCoordinator.SnapshotReady` in its constructor. On every snapshot, looks the
`repoPath` up in `GitChangesRepositoryRegistry`; if found, fire-and-forgets an unsolicited
`GitChangesSnapshotUpdated` SignalR invocation to the App (`AgentHubMethods.GitChangesSnapshotUpdated`),
mirroring the existing hook-driven `SyncCommand` push pattern. If the connection isn't `Connected` it silently
skips (there will be another scan eventually, or the App-side background monitor in Section 5 will pick it up
on its next sweep).

**Important:** this publisher only fires for scans that go through the coordinator - i.e. watcher-driven
scans and explicit `GetGitChangeStatus` calls. Stage/Unstage/Commit mutations (Section 3.7) call
`IRepositoryGitChangesService` directly and do **not** go through the coordinator, so they never trigger this
publisher. Their resulting snapshot is only returned in the command's own response; the App is responsible
for persisting it itself (see `PersistMutationResultAsync` in Section 4.4).

### 3.6 `GetGitChangeStatusCommand` (Agent command handler)

```csharp
registry.Register(repoPath, request.WorkspaceId, request.RepositoryId);
using var lease = watcherManager.Acquire(repoPath);
var result = await coordinator.RefreshNowAsync(repoPath, cancellationToken);
```

Every call: (1) registers/refreshes the `(workspaceId, repositoryId)` attribution, (2) acquires-then-
immediately-releases a watcher lease (the `using` disposes it before the method returns) - this is what
starts the idle-grace-period countdown described in 3.2, effectively meaning **"renew my lease for another
`WatcherIdleGraceMinutes`"** - and (3) runs (or coalesces into) an authoritative scan. `request.ForceRefresh`
is accepted on the wire (`GetGitChangeStatusRequest.ForceRefresh`) but is **not currently read anywhere in
the handler** - `RefreshNowAsync` always does the same thing regardless of the flag's value today. This is a
gap, not by design (see Section 7).

This command is the single most important integration point in the whole feature: it is the *only* thing
that ever creates a watcher. Nothing else in the Agent calls `watcherManager.Acquire`.

### 3.7 Mutations: `StageGitChangesCommand` / `UnstageGitChangesCommand` / `CommitGitChangesCommand`

All three follow the same shape: resolve `repoPath`, call `snapshotCache.NextVersion(repoPath)`, call the
matching `IRepositoryGitChangesService` method, `SetLatest` the resulting snapshot in the cache, and return it
in the response. They do **not** call `watcherManager.Acquire` or `registry.Register` - a mutation on a
repository the App has never asked `GetGitChangeStatus` for still works (git still runs), but it does not by
itself start monitoring that repository going forward.

### 3.8 `GitCliRepositoryGitChangesService` (Agent, implements `IRepositoryGitChangesService`)

The only thing that actually shells out to `git`, via `GitProcessRunner`'s `ArgumentList`-based `RunAsync`
overload (never an interpolated argument string - the existing `GitService`'s 76 other methods still use the
older string-`Arguments` convention; this is a deliberately scoped new convention, not a repo-wide change).
`GitProcessRunner.RepoLocks` (a `ConcurrentDictionary<string, SemaphoreSlim>` keyed by normalized repo path,
already used by every other `git`/`gitversion` invocation in the Agent) transparently serializes every
invocation per repository - this is what satisfies "a commit must never overlap a stage/unstage for the same
repo" without a second lock being invented for Git Changes specifically.

- `GetStatusAsync`: `git status --porcelain=v2 -z --branch --untracked-files=all`, parsed by the
  process-free `GitPorcelainV2Parser` (Common, unit-tested against fixtures: spaces, unicode, renames,
  untracked, deleted, conflicts, detached HEAD, unborn branch). Merge/rebase/cherry-pick state is derived by
  checking for `MERGE_HEAD` / `CHERRY_PICK_HEAD` / `rebase-merge` / `rebase-apply` under the resolved git dir
  (`git rev-parse --git-dir`, so it also works from worktrees).
- `GetDiffAsync`: reads original/modified content via `git show :0:<path>` (index) / working-tree file read
  (unstaged) or `git show HEAD:<path>` / `git show :0:<path>` (staged). Detects binary via
  `git diff --numstat` (`-\t-\t<path>` marker) before attempting to read content as text, and falls back to a
  `TooLarge` state above a 5 MB soft limit per side. Every path is passed through
  `GitRepositoryPathValidator.Validate` first (rejects absolute paths and `.`/`..` traversal, confirms the
  resolved path stays inside the repo root) - this runs on every diff and every stage/unstage path, since all
  of them arrive over the wire from the App and must be treated as untrusted input.
- `StageAsync`/`UnstageAsync`: whole-repo scope uses `git add --all` / `git restore --staged :/` (falling back
  to plain `git reset` on an unborn branch, where `restore` has nothing to restore from). Explicit paths use
  `--pathspec-from-file=-` with NUL-delimited UTF-8 stdin (`GitPathspecStdinWriter`), with a capability probe
  that falls back to bounded, char-count-limited batched positional arguments on git < 2.25 (avoids Windows
  command-line length limits either way).
- `CommitAsync`: optionally `git add --all` first (`StageAllFirst`), checks `git diff --cached --quiet` to
  short-circuit a "nothing staged" error before attempting a commit, then `git commit -F -` with the message
  piped over UTF-8 stdin (existing convention from `GitService.StageAndCommitAsync`, not a temp file).

---

## 4. App-side pipeline (the persistence/UI half)

### 4.1 `IGitChangesAgentClient` / `GitChangesAgentClient` (App, scoped)

Thin wrapper over `IAgentBridge.SendCommandAsync` for the five commands (`GetGitChangeStatus`,
`GetGitFileDiff`, `StageGitChanges`, `UnstageGitChanges`, `CommitGitChanges`). Callers resolve
`workspaceRoot`/`workspaceName`/`repositoryName` themselves (same convention as every other Agent-bridged
service).

### 4.2 `WorkspaceGitChangesWriteQueue` (App singleton `BackgroundService`)

An unbounded `Channel<GitChangesSnapshotNotification>` with one background reader. Every snapshot that needs
persisting - whether pushed unsolicited from the Agent (`AgentHub.GitChangesSnapshotUpdated`), or produced
locally by a mutation response, or (as of the fix in Section 6) produced by the new background monitor - goes
through `Enqueue`, then is processed one at a time by `GitChangesSnapshotPushHandler` in a fresh
`IDbContextFactory`-created `DbContext` per item. This decouples SignalR hub invocation threads from SQLite
writes, and guarantees only one write path into the projection tables (mirrors `AgentSyncNotificationQueue`'s
existing shape).

### 4.3 `GitChangesSnapshotPushHandler` (App, scoped)

For one notification: looks up the `WorkspaceRepositoryLink` by `(WorkspaceId, RepositoryId)`; if missing,
logs and drops (repository was removed from the workspace since the snapshot was requested). Loads the
existing `WorkspaceGitRepositoryStatus` row if any; **rejects the write if
`snapshot.Version <= existing.SnapshotVersion`** (stale-write protection using the version from Section 2.2).
Otherwise, upserts the status row (recomputing `StagedCount`/`ChangedCount`/`ConflictCount` from the entries),
deletes and reinserts all `WorkspaceGitChangeEntries` for that repo inside one transaction, then broadcasts
`GitChangesUpdated(workspaceId, repositoryId)` to every browser client via `WorkspaceSyncHub`.

### 4.4 The page: `WorkspaceGitChanges.razor` + partials

- **`WorkspaceGitChanges.razor.cs`** - `LoadAsync()` is explicitly documented as "reads the persisted SQLite
  projection only - never sends an Agent command. Opening or reloading this page must never trigger a status
  scan." It runs on `OnInitializedAsync`/`OnParametersSetAsync` and is also what the **Refresh button**
  currently calls (`GitChangesHeader`'s `OnRefresh` is wired straight to `LoadAsync`, see
  `WorkspaceGitChanges.razor` line ~11). **This means the Refresh button today only re-reads whatever is
  already in SQLite - it does not ask the Agent to run a fresh `git status`.** See Section 7.2.
- Stage/Unstage/Commit handlers (`StageAsync`/`UnstageAsync`/`CommitAsync`/`CommitWorkspaceAsync`/
  `BulkSectionActionAsync`) all funnel through `PersistMutationResultAsync`, which enqueues the mutation's
  returned snapshot onto `WorkspaceGitChangesWriteQueue` (same write path as watcher pushes) and then, for
  single-repo operations, waits 150 ms and calls `LoadAsync()` again so the UI reflects the just-persisted
  write rather than racing the background queue. Multi-repo fan-out (`CommitWorkspaceAsync`,
  `BulkSectionActionAsync`) reloads once after every repository's result has landed, not once per repository.
- **`WorkspaceGitChanges.Realtime.cs`** - opens one `HubConnection` to `/hubs/workspace-sync` per page
  instance and listens for `GitChangesUpdated`; on a match for the current `WorkspaceId`, calls `LoadAsync()`.
  This is how a watcher-driven push (or the background monitor's sweep) ends up visible on an already-open
  page without the user doing anything.
- **`WorkspaceGitChanges.MultiRepo.cs`** - workspace-wide "Commit Staged"/"Commit All" and bulk
  stage-all-changed/unstage-all-staged, using the same `SemaphoreSlim`+`Select`+`WhenAll` idiom as
  `PushOrchestrator`/`DependencyUpdateOrchestrator`, bounded by `WorkspaceOptions.MaxParallelOperations`.
- **`WorkspaceGitChanges.Diff.cs`** - lazy diff load on file selection, via `AgentClient.GetDiffAsync`.

### 4.5 UI composition

```text
WorkspaceGitChanges.razor
    GitChangesHeader.razor          - title/subtitle/Refresh (standard workspace-repos-header layout)
    GitChangesTree.razor            - flattened Staged/Changed section-first tree (built by
                                       GitChangesTreeBuilder, a pure function - filtering happens at the
                                       entry level so matching ancestors are preserved automatically)
    GitDiffViewer.razor/.razor.cs/.razor.js
                                     - Monaco diff editor, vendored under wwwroot/lib/monaco (no CDN, no
                                       build pipeline). The only component in the app using a per-instance
                                       IJSObjectReference JS module rather than a global window.* script -
                                       justified by Monaco's per-instance lifecycle/disposal needs.
```

`WorkspaceGitChangeSearchMatcher` implements the same `repo:`/`status:`/`staged:`/`ext:` field-prefixed filter
convention as every other grid page's `ISearchMatcher`, feeding `FilterSearchInput`.

---

## 5. The background monitoring service (added to fix Section 6's bug)

**File:** `src/GrayMoon.App/Services/GitChanges/GitChangesMonitoringBackgroundService.cs` (App, registered as
a hosted singleton in `Program.cs`).

This is **App-side**, not Agent-side. It does not watch files itself - it periodically calls the existing
`GetGitChangeStatus` command (Section 3.6) for every `(workspaceRoot, workspaceName, repositoryName)` triple
across every `WorkspaceRepositoryLink` in the database, which is what actually causes the **Agent** to
acquire/renew a watcher lease and run a scan.

```text
every WatcherRenewalIntervalMinutes (default 3, clamped below WatcherIdleGraceMinutes):
    if Agent not connected -> skip this sweep
    for every WorkspaceRepositoryLink with a resolvable workspace root:
        (bounded to MaxParallelRepositoryOperations concurrent in-flight calls)
        GetGitChangeStatus(root, wsName, repoName, workspaceId, repositoryId, forceRefresh:false)
        on success -> enqueue snapshot onto WorkspaceGitChangesWriteQueue (same path as watcher pushes)
also wakes immediately (skips the wait) when AgentConnectionTracker transitions to Online
```

This is why the doc calls it a "sweep": it is a scheduled, App-driven, per-repository fan-out over SignalR,
not something the Agent decided to do on its own. See Section 6 for why this exists and Section 8 for the
alternative (Agent-initiated) design the current sweep could be replaced with.

---

## 6. The bug this was built to fix

Before this service existed, **nothing in the entire codebase ever called `GetGitChangeStatus`** except the
command's own definition. Concretely:

- `WorkspaceGitChanges.razor.cs`'s `LoadAsync` deliberately never calls the Agent (by design - the page must
  read SQLite only).
- No other App service, page, or background job called `IGitChangesAgentClient.GetStatusAsync` anywhere.

Since `GetGitChangeStatusCommand` is the *only* code path that calls `GitRepositoryWatcherManager.Acquire`,
**no `FileSystemWatcher` was ever created for any repository, in any workspace, ever** - regardless of how
many times the Agent was restarted or how many files were edited. The persisted `WorkspaceGitRepositoryStatus`
table was permanently empty, so the page always rendered "No changed repositories" even when repositories had
real uncommitted changes.

The fix (Section 5) makes something call `GetGitChangeStatus` unconditionally, on a timer, for every known
repository, independent of whether the Git Changes page is open - which matches the original design doc's
explicit requirement: *"Opening the Git Changes page must not be required to start monitoring... If GrayMoon
chooses lease-based monitoring to control resource use, the lease should belong to the workspace background
service rather than the browser page."*

---

## 7. Known gaps / rough edges in the current implementation

These are not blockers, but you should know about them before changing this area further:

1. **Refresh button doesn't force a rescan.** `GitChangesHeader`'s Refresh button calls
   `WorkspaceGitChanges.razor.cs`'s `LoadAsync`, which only re-reads SQLite (Section 4.4). If the persisted
   projection happens to be stale (e.g. between monitoring sweeps, or if a watcher event was missed), clicking
   Refresh will not pick up a change that hasn't been pushed yet - it will just re-render the same stale
   data. A "real" refresh would need to call `AgentClient.GetStatusAsync(..., forceRefresh: true)` for every
   repository currently shown, wait for the result, and then reload - much closer to what the sweep does for
   one workspace, on demand, with `forceRefresh: true` actually honored (see gap 2).
2. **`ForceRefresh` is accepted on the wire but not implemented.** `GetGitChangeStatusRequest.ForceRefresh`
   exists and is passed through `GitChangesAgentClient.GetStatusAsync`'s `forceRefresh` parameter, but
   `GetGitChangeStatusCommand.ExecuteAsync` never reads `request.ForceRefresh` - it always calls
   `coordinator.RefreshNowAsync`, which already bypasses the debounce timer for *every* call, but does
   coalesce with an in-flight scan rather than forcing a brand-new one. There is currently no way to say "even
   if a scan just started, throw away its result and run another one anyway" - in practice this rarely
   matters (git status is cheap and idempotent), but it means the `forceRefresh` flag is presently a no-op.
3. **Mutations don't renew the watcher lease or flow through the snapshot publisher.**
   `StageGitChangesCommand`/`UnstageGitChangesCommand`/`CommitGitChangesCommand` call
   `IRepositoryGitChangesService` directly, bypassing `GitStatusRefreshCoordinator` and
   `GitRepositoryWatcherManager` entirely (Section 3.7). A repository that is only ever staged/committed
   through the UI, and never has `GetGitChangeStatus` called for it, will never get a watcher - only the
   background monitor (Section 5) or an explicit status request creates/renews one. In practice the
   monitor's sweep covers every repository anyway, so this is latent rather than user-visible today.
4. **The App-side sweep is a poll, not a push.** Every repository in every workspace gets a `GetGitChangeStatus`
   round trip every `WatcherRenewalIntervalMinutes`, whether or not anything changed and whether or not anyone
   is looking at that workspace. For a handful of repos this is unnoticeable; for a very large number of
   workspaces/repos it is `O(repos)` SignalR round trips on a timer, independent of what's actually being
   viewed. See Section 8 for the alternative this could evolve into.
5. **Stale-workspace-root repositories are silently skipped, not reported.** If
   `WorkspaceService.GetRootPathForWorkspaceAsync` can't resolve a root for a workspace, that workspace's
   repositories are just excluded from the sweep with no user-visible diagnostic - by design, since the sweep
   is a best-effort background process, not a user-initiated action with a result to show.

---

## 8. Open design question: should the Agent bootstrap its own watcher list on connect?

You asked whether the Agent should learn about workspaces on startup/connect and start watching immediately,
instead of the App polling it. Both are legitimate; here's the actual trade-off given how the rest of
GrayMoon is built:

**Current model (App polls, Section 5):**
- No new SignalR hub method needed - reuses the existing `GetGitChangeStatus` command and the existing
  App -> Agent request/response shape (`RequestCommand`/`ResponseCommand`).
- The Agent stays "dumb": it never needs to know what a workspace is beyond a single request's parameters.
  This matches every other Agent command today - the Agent has zero persistent knowledge of workspaces,
  repositories, or the database; the App is always the one who tells it what to act on, per request.
- Downside: watcher creation for a never-before-seen repository is only as fast as the sweep interval (up to
  `WatcherRenewalIntervalMinutes`, default 3 minutes) after Agent connect/reconnect, rather than immediate.
  Also, it's `O(repos)` requests on every sweep regardless of relevance.

**Alternative (Agent asks the App for its watch list on connect):**
- Would require a new **Agent -> App** request/response RPC, which does not exist today. Today's SignalR
  contract is one-directional per method: the App sends `RequestCommand` and waits for `ResponseCommand`; the
  Agent sends fire-and-forget notifications (`SyncCommand`, `ReportSemVer`, `ReportQueueStatus`,
  `GitChangesSnapshotUpdated`) but never *asks the App a question and waits for an answer*. Building this
  would mean either (a) a new hub method the App calls immediately after `AgentHub.OnConnectedAsync` to push
  the full repository list unsolicited (fire-and-forget, App-initiated, so actually still "App decides when",
  just once on connect instead of every N minutes), or (b) a genuine bidirectional RPC pattern that doesn't
  exist anywhere else in the codebase yet.
- Upside: monitoring starts immediately on Agent connect/reconnect instead of waiting for the next sweep tick,
  and doesn't need to re-ask about repositories that haven't changed - the Agent could keep watching them for
  as long as it's connected, with the App only needing to notify it of *additions/removals* rather than
  re-declaring the full list on a timer.
- This would still need *some* periodic reconciliation (per the original design doc's "periodic
  reconciliation" section) to catch missed watcher events - so it wouldn't fully eliminate a timer, just make
  it lower-frequency/best-effort instead of being the sole mechanism that starts monitoring in the first
  place.

**Recommendation if you want to change this:** keep `GetGitChangeStatus` as the mechanism (don't invent a new
command), but change *when* the App calls it for the "first time per repository": push the full list once,
immediately, in `AgentHub.OnConnectedAsync` (currently that method only refreshes the cached workspace root -
see `src/GrayMoon.App/Hubs/AgentHub.cs` lines 18-38), rather than waiting up to 3 minutes for the next sweep.
The periodic sweep in Section 5 would then become a lower-frequency safety net (closer to
`WatcherIdleGraceMinutes`, e.g. every 8 of its 10 minutes) purely to renew leases and catch anything the
watcher missed, instead of being the only path to initial monitoring. I have not made this change - flagging
it here since you asked, but wanted you to have the full picture before deciding.

---

## 9. Configuration reference (`GitChangesOptions`, bound from `"GitChanges"` config section - not currently present in `appsettings.json`, so all defaults apply)

| Setting | Default | Meaning |
|---|---|---|
| `MaxParallelRepositoryOperations` | 16 | Max concurrent `git status` scans across all repositories (Agent coordinator gate; also reused as the App sweep's fan-out bound). |
| `MaxParallelRepositoryMutations` | 4 | Max concurrent stage/unstage/commit mutations across repositories (declared; not yet wired to a gate - mutations are currently only serialized per-repo via `GitProcessRunner.RepoLocks`, not globally bounded to 4). |
| `MaxParallelDiffLoads` | 4 | Max concurrent diff loads (declared; diff loads are currently one-at-a-time per page since only one file can be selected, so this cap isn't exercised yet). |
| `WatcherDebounceMilliseconds` | 400 | Delay after a watcher event before running an authoritative scan. |
| `WatcherIdleGraceMinutes` | 10 | How long a repository's watcher survives with no renewing operation before being disposed. |
| `WatcherRenewalIntervalMinutes` | 3 | (Added with the Section 5 fix.) How often the background monitor sweeps every repository to renew leases. Clamped to stay below `WatcherIdleGraceMinutes`. |

---

## 10. File map

| Area | File | Responsibility |
|---|---|---|
| Common | `Git/GitChangeModels.cs` | Wire/domain records and enums (snapshot, entry, diff, mutation results). |
| Common | `Git/GitPorcelainV2Parser.cs` | Pure parser for `git status --porcelain=v2 -z`. |
| Common | `Git/GitRepositoryPathValidator.cs` | Rejects absolute paths/traversal for any path coming from the App. |
| Common | `Git/MonacoLanguageMapper.cs` | File extension -> Monaco language id. |
| Common | `Git/GitChangesOptions.cs` | Tunable concurrency/debounce/lease settings (Section 9). |
| Common | `Git/GitChangesSnapshotNotification.cs` | Agent -> App unsolicited push payload. |
| Agent | `Services/GitChanges/GitRepositoryWatcher.cs` | One repo's two `FileSystemWatcher`s + overflow recovery. |
| Agent | `Services/GitChanges/GitRepositoryWatcherManager.cs` | Lease-counted watcher lifecycle. |
| Agent | `Services/GitChanges/GitChangesRepositoryRegistry.cs` | Path -> `(WorkspaceId, RepositoryId)` attribution. |
| Agent | `Services/GitChanges/GitStatusRefreshCoordinator.cs` | Debounce + fan-in state machine + bounded scan gate. |
| Agent | `Services/GitChanges/GitChangesSnapshotCache.cs` | Per-repo version counter + latest snapshot cache. |
| Agent | `Services/GitChanges/GitChangesSnapshotPublisher.cs` | Pushes `GitChangesSnapshotUpdated` to the App. |
| Agent | `Services/GitChanges/GitCliRepositoryGitChangesService.cs` | All actual `git` invocations for this feature. |
| Agent | `Services/GitChanges/GitPathspecStdinWriter.cs` | NUL-delimited pathspec stdin + bounded-batch fallback. |
| Agent | `Commands/GetGitChangeStatusCommand.cs` | Registers + leases + triggers the scan (Section 3.6). |
| Agent | `Commands/GetGitFileDiffCommand.cs` | Diff load command. |
| Agent | `Commands/StageGitChangesCommand.cs` / `UnstageGitChangesCommand.cs` / `CommitGitChangesCommand.cs` | Mutations. |
| App | `Services/GitChanges/GitChangesAgentClient.cs` | Wraps `IAgentBridge` for the five commands. |
| App | `Services/GitChanges/WorkspaceGitChangesWriteQueue.cs` | Single writer queue into SQLite. |
| App | `Services/GitChanges/GitChangesSnapshotPushHandler.cs` | Persist (version-checked) + broadcast. |
| App | `Services/GitChanges/WorkspaceGitChangesReadService.cs` | SQLite-only read for the page. |
| App | `Services/GitChanges/GitChangesTreeBuilder.cs` | Pure section-first tree builder for the UI. |
| App | `Services/GitChanges/GitChangesMonitoringBackgroundService.cs` | **The fix** - App-side sweep that bootstraps/renews Agent watcher leases (Section 5). |
| App | `Services/WorkspaceGitChangeSearchMatcher.cs` | Filter query matcher for the tree. |
| App | `Hubs/AgentHub.cs` | `GitChangesSnapshotUpdated` inbound handler (`OnConnectedAsync` at lines 18-38 is the extension point discussed in Section 8). |
| App | `Components/Pages/WorkspaceGitChanges.razor` + `.razor.cs`/`.MultiRepo.cs`/`.Diff.cs`/`.Realtime.cs` | Page + partials. |
| App | `Components/GitChanges/GitChangesHeader.razor` | Title/subtitle/Refresh. |
| App | `Components/GitChanges/GitChangesTree.razor` | Renders the flattened tree rows. |
| App | `Components/GitChanges/GitDiffViewer.razor` + `.razor.cs`/`.razor.js` | Monaco diff editor. |
| App | `Data/AppDbContext.cs`, `Migrations.cs` | Schema (Section 2.3). |
