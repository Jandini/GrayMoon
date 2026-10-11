# Parallel repository operations - implementation checklist

Branch `parallel-operations`, starting from `56ba6b1`. Design: `parallel-repository-operations-design.md`.

## Phase 0 - Research

- [x] Read bulk merge, unattended return, execute return, sync fan-out, restore, worker queues, and git locks.
- [x] Wrote the stage map in the design note before the batch method landed.

## Phase 1 - Post-merge Return to Default

- [x] `ReturnToDefaultUnattendedBatchAsync` plans each distinct repository, runs eligible returns under `MaxParallelOperations`, and recomputes the workspace once.
- [x] `ReturnToDefaultUnattendedAsync` still aborts the whole call on the first blocker. Single-repository merge still uses it.
- [x] Bulk merge sends retry-only rows and newly merged rows through one deduplicated batch. A merge-phase exception still does not drop the retry-only rows.
- [x] Row updates run on the Blazor synchronization context. Abort still marks leftover `Merging` as `Skipped` and leftover `ReturningToDefault` as `ReturnFailed` with "Return to default branch was cancelled."
- [x] A failed workspace refresh shows "refreshing the workspace failed" and leaves returned rows as `ReturnedToDefault`.
- [x] Tests: overlap, limit 1, duplicates, one blocker, fetch failure, result order, cancellation, feature context, recompute failure, progress text, row apply, abort finalizer. Existing `ReturnToDefaultAnalyzeTests` still pass.

## Phase 2 - Restore Sync

- [x] Traced restore to `SyncAsync`. The fan-out, worker main queue, and per-repository git lock were already concurrent.
- [x] Added fan-out, persist, and Git Changes timing logs (`WorkspaceId`, `ContextId`, `RepositoryId`, `OperationId`, `Stage`, `ElapsedMs`, `ActiveCount` / `MaxActive`). No tokens or clone URLs.
- [x] Tests: two `SyncRepository` commands overlap; `MaxParallelOperations` of 1 does not. Existing sync state tests still pass.
- [x] No worker or clone-order change. No VDI before/after wall-clock numbers. Use the `Stage=fan-out` Information line on a real restore to record `MaxActive` and `ElapsedMs`.

## Phase 3 - Adjacent sequential work

- [x] Reviewed. Nothing else implemented.

| Candidate | Decision | Why |
| --- | --- | --- |
| `RefreshGitChangesAfterSyncAsync` per-id loop | Leave | Full sync (including restore) already uses one internally parallel scan. A parallel loop of per-id scans would stack on `MaxParallelRepositoryOperations`. |
| `PersistVersionsAsync` | Leave | One scoped `DbContext`. The writes are after the fan-out. |
| `ExecuteReturnToDefaultAsync` and dependency-level return | Leave | Already parallel, with a different options policy. |
| Prepare Workspace, push, GitVersion, dependency levels | Leave | Out of scope. Levels stay ordered where the dataflow requires it. |

## Preserved behavior

- Merge results still decide which repositories may return. Failed merges are not returned.
- Unattended options: remote delete only with an upstream, force-delete local, no pull-request close.
- Eligibility is classified from a snapshot taken after the batch's own fetch and pull-request refresh, not from the dialog's row.
- `MaxParallelOperations` of 1 stays one-at-a-time.
- A cancelled in-flight return that does not finish persistence stays unsuccessful so retry can run it again. A return that did finish stays successful if only the workspace refresh fails.
