# Parallel repository operations

Diagnosis of bulk post-merge Return to Default and of the initial Sync after Restore Workspace, against `main` at `56ba6b1`. Implementation notes for the return batch are in `parallel-repository-operations-implementation.md`.

No enterprise VDI timings were captured in this session. Concurrency below is what the code and the in-memory tests show. The new sync logs are there so a VDI trace can fill in wall-clock time.

## Bulk merge, then Return to Default

`RunBulkMergeAndReturnToDefaultAsync` still merges with `PullRequestOperations.MergeManyAsync`, which already uses `MaxParallelOperations`. After that, `RunBulkReturnToDefaultAsync` used to call `IWorkspaceSyncOperations.ReturnToDefaultAsync` once per repository, in a loop.

That loop was deliberate. `ReturnToDefaultUnattendedAsync` does two things that do not belong inside a per-repository loop:

- One blocked repository (tag, commits ahead without a merged or closed pull request, failed fetch, failed pull-request refresh) aborts the whole call. The result is a single `AbortReason` with no repository id, so the dialog cannot retry only the failed row.
- Every call ends in `WorkspaceGitService.RecomputeAndBroadcastWorkspaceSyncedAsync`. That recomputes file-version and dependency stats for the whole workspace and broadcasts `WorkspaceSynced`. Running it once per repository races SQLite and repeats the same workspace write.

`ReturnToDefaultUnattendedAsync` already runs the Git calls for several ids under `_maxConcurrent` (`WorkspaceOptions.MaxParallelOperations`, minimum 1). That does not make it a safe batch API. The abort happens earlier, in `AnalyzeReturnToDefaultAsync` / `BuildPlan`, before any of those Git calls, whenever any repository is ineligible.

`ExecuteReturnToDefaultAsync` is a different policy (explicit delete and close options, continues after per-repository failure). Unattended bulk merge must keep its own policy: delete the remote branch only when that repository has an upstream, force-delete the local branch, and do not close pull requests.

`AnalyzeReturnToDefaultAsync` also shares work that must not run once per repository in parallel:

- Branch refresh is already per repository, each on its own DI scope.
- `RefreshPullRequestsAsync` is one call for the whole id list. It is safe for different repositories inside that one call (its own semaphore, its own `DbContext` per invocation, a `ConcurrentDictionary` cache). Two overlapping calls for the same batch would write the same pull-request rows at once.
- `ReturnToDefaultDirectAsync` refreshes that one repository's pull request again immediately before the worker call. Unattended mode passes `allowForceDeleteLocalBranch: true`, so that refresh does not change the force-delete decision. It is the same per-repository write `ExecuteReturnToDefaultAsync` already runs in parallel.

The batch method `ReturnToDefaultUnattendedBatchAsync` therefore:

1. Deduplicates ids and keeps first-seen order.
2. Refreshes branches in parallel, and records a fetch failure against that repository only.
3. Refreshes pull requests once for the repositories that fetched.
4. Classifies each repository from a fresh snapshot. A tag, an unsafe discard, or any other blocker fails that repository and does not call the worker.
5. Returns eligible repositories in parallel on new DI scopes, bounded by the same `_maxConcurrent`.
6. Calls `RecomputeAndBroadcastWorkspaceSyncedAsync` once, only if at least one return was admitted, after those calls have settled. A failure or cancellation of that refresh is returned separately and does not flip a finished return to "not done".

`ReturnToDefaultUnattendedAsync` is unchanged for the single-repository and all-or-nothing callers.

## Restore Workspace initial Sync

Restore calls `RestoreFromRepositoryAsync`, then `SyncNewWorkspaceAsync`, which calls the same `IWorkspaceSyncOperations.SyncAsync` as the rest of the app (`RestoreWorkspaceModal.razor.cs`). The app orchestration is not a sequential repository loop.

`WorkspaceGitService.SyncAsync` fans out `SyncRepository` with `SemaphoreSlim(_maxConcurrent)` and `Task.WhenAll`. `WorkerBridge.SendCommandAsync` has no single-flight lock. The worker enqueues `SyncRepository` on the main job queue. `MainJobBackgroundService` runs that queue with `WorkerOptions.MaxConcurrentCommands` consumers (default `Environment.ProcessorCount * 2`). `GitProcessRunner` locks per repository path, not process-wide.

Inside one `SyncRepository`, fetch finishes before version, ref reads, and project discovery. Those three then overlap for that repository. That ordering is required: GitVersion is invoked with `/nofetch`, and the ref reads must see the fetched refs.

After the fan-out, these stages are sequential on purpose:

- `PersistVersionsAsync` writes each repository through the one scoped `WorkspaceGitService` `DbContext`. Parallel saves on that instance are the bug `AGENTS.md` forbids.
- File-version check, manifest drift, and tag pins.
- `RefreshGitChangesAfterSyncAsync`. A full sync (restore passes `repositoryIds: null`) makes one `ScanWorkspaceAsync` call. That scan already fans out `GetGitChangeStatus` up to `GitChangesOptions.MaxParallelRepositoryOperations`. A subset sync loops ids one at a time so it does not stack another scan on top of that limit.

The workspace-role repository's work tree is the workspace directory. Child repositories live in subfolders. `SyncAsync` creates the workspace directory before the fan-out, so `SyncRepository` does not clone the workspace repository when that directory already exists (clone runs only when the repository path is missing). Child clones are `git clone` with the workspace directory as the working directory, so they create subfolders and can overlap. Their git locks are keyed by those subfolder paths. The workspace repository's fetch is keyed by the workspace path. This session did not change that overlap: no corruption showed up in code review, and serializing the root would slow every sync without a measured fault.

What can still look sequential on a VDI, without a bug in the fan-out:

- `MaxParallelOperations` or `MaxConcurrentCommands` set to 1.
- Clone and fetch dominated by disk and network, so repositories start together and still finish far apart.
- One terminal collecting every repository's output.
- The post-fan-out persist and file-version work, which is supposed to be after the clones.

Sync now logs, at Information, one line when the fan-out settles: `WorkspaceId`, `ContextId`, `RepositoryCount`, `MaxActive`, `ElapsedMs`, `Stage=fan-out`. Debug lines cover each repository's dispatch and response (`OperationId`, `ActiveCount`, `ElapsedMs`) plus persist and the Git Changes refresh. Those lines do not include tokens, clone URLs, or file contents. `SyncRepository` already logs its own stage timings at Debug.

## Stage map

| Stage | Method | Scope | Before | Action | Race if parallelized blindly | Gain |
| --- | --- | --- | --- | --- | --- | --- |
| Bulk merge | `MergePullRequestsAsync` | Per repository, shared GitHub | Already parallel, bounded | Keep | Shared `DbContext` (already fixed) | Already taken |
| Post-merge return | `RunBulkReturnToDefaultAsync` | Per repository Git, one workspace recompute | Sequential loop | Batch method, one recompute | Workspace recompute and pull-request refresh | The wait that was N times one return |
| Unattended multi-id API | `ReturnToDefaultUnattendedAsync` | Mixed | Parallel Git, abort-all analysis | Leave in place | One blocker drops every repository's identity | None for bulk merge |
| Explicit return | `ExecuteReturnToDefaultAsync` | Per repository, one recompute | Already parallel | Leave | Different delete and close policy | Already taken |
| Restore sync fan-out | `WorkspaceGitService.SyncAsync` | Per repository worker command | Already parallel, bounded | Log `MaxActive` and elapsed | Shared `_dbContext` if saves moved into the fan-out | Not a missing fan-out |
| Worker `SyncRepository` | Main job queue | Per command, per-repo git lock | Parallel up to `MaxConcurrentCommands` | Leave | Workspace root vs child work trees (see above) | Not a single global mutex |
| Sync persist | `PersistVersionsAsync` | One `DbContext` | Sequential | Leave | Second operation on one context | Small next to clone and fetch |
| Git Changes after full sync | `ScanWorkspaceAsync` | Per repository inside one scan | Already parallel, bounded | Leave | A second parallel loop would exceed the scan limit | Already taken for restore |
| Git Changes after a subset | `RefreshGitChangesAfterSyncAsync` foreach | One id per call | Sequential | Leave | Stacked scans, shared watcher coordination | Not worth a new scheduler |

## Tests that lock the diagnosis

- Four eligible repositories overlap (`max` active greater than 1 and at most 4) and emit the same `WorkspaceSynced` count as a one-repository batch. No `WorkspaceSynced` is sent while the returns are still inside the worker call.
- `MaxParallelOperations` of 1 keeps return and `SyncRepository` at one active call.
- Duplicate ids run once, in first-seen order.
- One unsafe repository does not stop the others and does not call `ReturnToDefaultBranch`.
- A fetch failure is attributed to that repository. The other still returns, and a repository with no upstream does not request remote delete.
- Cancelling while one return is in flight does not start the next. The in-flight call is not reported as a finished success if persistence observes the cancellation.
- A feature context fails every repository and does not call the worker.
- A thrown workspace broadcast leaves `MutationCompleted` true and `ReconciliationSucceeded` false.
