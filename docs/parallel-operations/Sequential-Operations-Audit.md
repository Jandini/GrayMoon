# Sequential operations audit

Date: 2026-10-15. Branch: `parallel-pr-lookup`.

Scope: `GrayMoon.App` and `GrayMoon.Worker` production code. This lists per-repository (or per-item) work that still runs one at a time and could run in parallel safely, and the work that must stay sequential.

## Method and limits

- Scanned every `foreach` / `for` / `await foreach` loop in App and Worker that contains an `await` (62 loops), then read the surrounding code of each candidate.
- Judged each against three constraints from the codebase:
  1. **Shared scoped `AppDbContext` is not thread-safe** (`AGENTS.md`). Only worker calls, GitHub/HTTP calls and factory-created contexts may fan out; writes through the shared context stay sequential.
  2. **Ordering that is part of the feature** (dependency levels, root-worktree-last) must not be parallelized.
  3. **Bounds:** fan-out uses `WorkspaceOptions.MaxParallelOperations` (App/worker round trips) or `GitChangesOptions.MaxParallelRepositoryOperations` (Git Changes scans) - never a hard-coded number.
- Not covered: sequential `await a; await b;` chains outside loops, Razor markup, git process internals inside the Worker. This is a loop-level audit, not a proof that nothing else is sequential.

## Already done on this branch

| Where | Change |
|---|---|
| `WorkspaceRepositoryStateWriter.ReconcilePullRequestsAsync` | PR lookups for all staged repositories go out in one batched refresh (commit `7fbe3ebb`). |
| `WorkspaceBranchHandler.CheckoutBranchForWorkspaceAsync` | Branch-occupancy checks run in parallel; the limit now comes from `MaxParallelOperations` instead of a constant 16. |
| `WorkspaceGitService.GetRepoSyncStatusAsync` | `GetRepositoryVersion` probes run in parallel. |
| `WorkspaceGitService.ApplyDefaultTipVersionsForMergedFeatureReposAsync` | `GetGitVersionAtDefaultTip` probes run in parallel; DB updates stay sequential. |

Tests: `WorkspaceGitServiceParallelismTests`, and a parallelism case in `WorkspaceBranchHandlerOccupancyTests` (uses `ConcurrencyProbe` to assert the limit is reached and never exceeded).

## Candidates that can run in parallel

Priority is value divided by risk. "Safe because" is the reason the DbContext and ordering constraints do not block it.

### High

**1. `GitHubActionsService.GetLatestActionsAsync` - `Services/GitHub/GitHubActionsService.cs:26-32`**
- Now: one `GetLatestWorkflowRunAsync` GitHub call per repository, across every connector, in a nested loop. Cost grows with the total repository count; this is the slowest sequential loop in the App.
- Safe because: `GitHubService` holds no `DbContext` (HTTP only); results are collected into a list. Only `results.Add` needs to become thread-safe (collect per task, merge after).
- Watch: GitHub secondary rate limits. `IGitHubRateLimitTracker` already pauses a connector on 429, so bound by `MaxParallelOperations` and treat a paused connector like the PR refresh does.

**2. `GitHubActionsService` per-workflow metadata - `GitHubActionsService.cs:211`**
- Now: for each latest run, `GetWorkflowByIdAsync` then `GetCachedDispatchSupportAsync` one run at a time.
- Safe because: same as above; the dispatch-support cache is `IMemoryCache`. Order of `result` must be kept (it is built from an ordered list), so use an indexed result array.

**3. `GitHubActionsPushRunWatch` - `Services/Ci/GitHubActionsPushRunWatch.cs:57` and `:131`**
- Now: during a push wait, workflow discovery calls `GetWorkflowStatusesForBranchAsync` once per pushed repo, and `PumpLiveFeedIntoOverlayAsync` calls `PollOnceAsync` once per live feed, each cycle (every ~4 s while active). With many pushed repos the cycle is longer than the poll interval, so updates lag.
- Safe because: feeds are independent objects and `JobTerminalBuffer` is lock-protected. Poll feeds in parallel, then append the returned lines on one thread to keep per-repo line order stable.
- Note: `_noWorkflowRepoIds` / `_feedByRunKey` are plain collections mutated in the discovery loop; collect results first, mutate after `Task.WhenAll`.

**4. `WorkspaceSyncHandler` per-repository scans - `Services/Orchestration/WorkspaceSyncHandler.cs:85`**
- Now: when a repository subset is given, `ScanWorkspaceAsync` is awaited once per repository in turn. Each call scans a single repo, so the scanner's own internal parallelism never kicks in.
- Safe because: the scanner creates its own scope (`IServiceScopeFactory`) and persists through the single-reader write queue. Either pass the whole id set in one call (preferred - add a `repositoryIds` overload) or fan out bounded by `GitChangesOptions.MaxParallelRepositoryOperations`.

**5. `WorkspaceFileVersionService.UpdateAllVersionsAsync` - `Services/Workspaces/WorkspaceFileVersionService.cs:215`**
- Now: one `UpdateFileVersions` worker command per configured file, sequentially.
- Safe because: it is a worker round trip; counters (`totalUpdated`, `totalFailed`, `updatedFiles`) become `Interlocked` / concurrent collection.
- Check before changing: two configs must never target the same file (the Worker edits it in place). Group by file path and keep each group sequential if that can happen.

### Medium

**6. Feature rollback / repair - `Services/Features/WorkspaceFeatureOperations.cs:2014`**
- Now: per row, `ListGitWorktrees` then `RemoveGitWorktree`, sequentially, with the Workspace-role root row removed last and only if every Source row succeeded.
- Safe because: the Source rows are independent worktrees; only the root-last rule is an ordering constraint. Fan out the Source rows (bounded by `MaxParallelOperations`), `await` them all, then do the root row. Progress reporting and `results` need a lock or per-row slots.
- Risk: removal races on the Worker with its own handle-release broker; keep the existing per-feature operation lock and test with the Worker fake.

**7. `WorkspacePushService.FetchAfterRejectionAsync` - `Services/Workspaces/WorkspacePushService.cs:1077`**
- Now: for each repo whose push was rejected, `RefreshBranches` (worker) then `UpdateCommitCountsAndUpstreamAfterPushAsync` (shared `_dbContext`), one repo at a time.
- Safe because: only the worker call can fan out. Split into a parallel `RefreshBranches` phase and a sequential DB phase. Low frequency (rejections are rare), so value is modest.

**8. `GitHubRepositoryService` connector fetch - `Services/GitHub/GitHubRepositoryService.cs:51`**
- Now: connectors are fetched one after another; each fetch is a paginated GitHub listing.
- Safe because: different connectors are different APIs. Must serialize the shared state: `allFetched`, `uniqueCloneUrls`, the `Progress<T>` callback (already marshalled), and `connectorRepository.UpdateStatusAsync` (shared DbContext - do after `WhenAll`). Value depends on connector count (usually 1-3).

**9. `WorkspaceSyncHandler` snapshot reads - `WorkspaceSyncHandler.cs:217`, `:266`, `WorkspaceSyncHandler.ReturnBatch.cs:168`**
- Now: `GetSnapshotAsync` per repository on one scope/DbContext (the comment says this is deliberate).
- Better fix than parallelism: add a batch `GetSnapshotsAsync(ids)` that reads all rows in one query. Parallel reads would need one scope per task. Mostly an N+1 query, not a latency problem.

### Low

**10. N+1 path and branch lookups (batch, do not parallelize)**
- `WorkspaceNativeLaunchService.cs:27` and `GitChangesWorkspaceScanner.cs:76`: `pathResolver.GetRepositoryPathAsync` opens a new DbContext per repository. Add a batch resolver (`GetRepositoryPathsAsync(contextId, ids)`) instead.
- `WorkspaceBranchOperations.cs:534`: one `RepositoryBranches` query per link inside `GetCommonBranchesAsync`; replace with one query grouped by `WorkspaceRepositoryId`.

**11. `FeatureFolderCleanupService` - `Services/Features/FeatureFolderCleanupService.cs:98`**
- Sweeps one workspace at a time. Background work, throttled per workspace, so latency does not matter; leave unless sweeps become slow.

**12. `MarkdownImageEmbedder` - `Services/GitChanges/MarkdownImageEmbedder.cs:54`**
- Downloads/reads embedded images one at a time (capped at `MaxImages`). Could fan out with a small bound; `replacements` and `embedded` need a concurrent dictionary / `Interlocked`. User-visible only on large markdown diffs.

**13. Worker `CsProjFileService.UpdatePackageVersionsAsync` - `GrayMoon.Worker/Services/CsProjFileService.cs:168`**
- Updates each `.csproj` in turn. Files are distinct, so parallel is safe, but each update is small local I/O; low gain.

**14. Worker `SweepPendingFeatureFoldersCommand` - `GrayMoon.Worker/Commands/SweepPendingFeatureFoldersCommand.cs:20`**
- Cleans marked folders one at a time. Distinct folders, so parallel is safe, but it runs rarely and is disk-bound.

## Must stay sequential (do not change)

| Where | Why |
|---|---|
| `DependencyUpdateOrchestrator` level loop (`:290`), `WorkspacePushService` level loops (`:217`, `:752`, `:590`) | Dependency-level ordering is the feature: level N+1 needs level N's versions and published packages. Parallelism already exists inside each level. |
| `WorkspaceGitService.Branches.cs:233`, `Fetch.cs:98`, `:119`, `WorkspaceRepositoryStateWriter.cs:286`, `:400` | Writes through the shared scoped `AppDbContext`. EF Core is not thread-safe here (`AGENTS.md`). The worker calls before them are already parallel; prefer batching writes over parallelizing them. |
| `WorkspaceFeatureReconciler.cs:226`, `:298`, `:305` | Worker `ListGitWorktrees` calls are already parallel (`Parallel.ForEachAsync`); the row-level apply loop mutates one `DbContext`. |
| `GitChangesMonitoringBackgroundService.cs:120`, `:127` | Documented on purpose: each scan is already bounded by `MaxParallelRepositoryOperations`, and looping keeps the whole sweep within that same bound. |
| `GitChangesLineStatsRefresh.cs:109`, `:154` | Same reasoning as the monitoring sweep. |
| `FeatureRemovalQueue`, `WorkspaceGitChangesWriteQueue`, `WorkerSyncNotificationQueue`, `SyncBackgroundService` (`await foreach` over channels) | Single-reader queues exist to serialize SQLite writes. |
| Worker `JobBackgroundService`, `*JobQueue` loops | They are the worker pools themselves. |
| Worker `DotnetRestoreCommand.cs:30` | Concurrent `dotnet restore` of projects in one repo shares `obj/` assets files and the NuGet cache and can race on project-to-project references. Not safe to assume. |
| `WorkspacePushService.cs:325` | Tiny connector prefetch through the shared DbContext, explicitly sequential to avoid concurrent reads. |

## Already parallel (for reference)

`WorkspaceActions` auto refresh and bulk actions, `WorkspaceGitChanges` multi-repo/multi-select/discard, `WorkspaceRepositories.BulkMerge`, `WorkspaceGitService` branch creation / fetch / commit-counts, `WorkspaceBranchHandler` create + checkout, `WorkspacePushService` push fan-out and NuGet availability checks, `PackageRegistrySyncService`, `TokenHealthBackgroundService`, `WorkspaceFeatureOperations` create, `WorkspaceFeatureReconciler` worktree lists, `WorkspacePullRequestService` refresh, `GitChangesWorkspaceScanner` scans.

## How to implement a candidate safely

1. Fan out only worker calls, HTTP calls, or work that creates its own context via `IDbContextFactory<AppDbContext>`. Keep shared-`DbContext` reads and writes in a sequential pass after `Task.WhenAll`.
2. Bound with `SemaphoreSlim(Math.Max(1, options.Value.MaxParallelOperations))` (or `GitChangesOptions.MaxParallelRepositoryOperations` for Git Changes). No constants.
3. Record results per item (concurrent collection or indexed array), then merge in the original order so output and progress stay deterministic.
4. Keep per-item `try/catch` so one failing repository does not fail the batch; let `OperationCanceledException` through.
5. Test with `FakeWorkerBridge.RespondAsync` and `ConcurrencyProbe` (`WorkspaceGitServiceParallelismTests.cs`): assert every item is processed, peak concurrency equals the limit, and one failing item leaves the others intact. Run with limit 1 as well to prove the bound is honoured.

## Suggested order

1. Items 1-2 (GitHub Actions dashboard) - biggest wall-clock win, no DbContext involved.
2. Item 3 (push run watch) - keeps the push overlay current on large workspaces.
3. Items 4-5 - small, contained changes.
4. Items 6-9 - need ordering/DbContext care and their own tests.
5. Item 10 - batch queries; a different kind of fix but removes N+1 round trips.
