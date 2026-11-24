# Sync performance fix plan

Status: phases 1 to 4 built on 2026-11-24, not yet measured on a real Workspace. What is still open is listed under "Open". Evidence and the measurements that started this are in `ideas/feature-create-background-sync-cost.md`. How the built parts work is in `docs/architecture/` (03 "WorkspaceFileLineStatus", 04 "Hook sync batches", 05 "File versioning").

## Goal

After an operation finishes, the grid is final within a few seconds, and Update no longer spends most of its time re-checking version files. What is computed stays the same; how often it is computed changes.

| Case (measured before) | Before | Expected after (calculated from the logs, not measured) |
|---|---|---|
| Grid settles after Create Feature, 27 repositories | 51.8 s | about 7 s, the time the Worker needs to send its results |
| Hook syncs after a push of 5 repositories | 15 recomputes, about 27 s | 2 to 4 recomputes, under 3 s |
| Worker `GetHeadCommits` calls per day | 512 calls, 496 s | one per Update start and one per file write |

## Built

### Phase 1: the check reads the stored HEAD commit

- `WorkspaceFileVersionService.ResolveTokenValuesAsync` takes the source of `{@Repo:commit}` values. The check uses **Stored** (`HeadCommit` of the context state row, trimmed, no Worker call). `UpdateAllVersionsAsync` uses **Live** (one `GetHeadCommits`), because its value is written into a file and committed.
- A context without a state row, or with a blank `HeadCommit`, gives the token no value. Nothing else is read in its place: not the special Workspace's row, not the link, not the Worker.
- **Decision 2, taken as recommended:** such a token is stored as a "not checked" row (`WorkspaceFileLineStatus.NotCheckedReason`, strict migration step 22). It counts in `OutOfDateFileRepos`, so the badge is not green, and the tooltip shows "not checked: No commit is recorded for Repo. Sync the repository." Before, the check deleted the earlier out-of-date row and the file looked up to date.
- **Decision 1, taken as recommended:** `DependencyUpdateOrchestrator.RunAsync` (Update, Update and Push, Prepare Workspace) reads the commits that version files name from the checkouts once at its start (`ReadLiveCommitTokenHeadsAsync`), stores them (`WorkspaceRepositoryStateWriter.WriteHeadCommitsAsync`) and, when one changed, runs the check once. When a commit cannot be read the run stops before any change and names the repository. A Workspace without a commit token makes no Worker call.

### Phase 2: fewer checks inside Update

- The dependency update refreshes the committed repositories side by side without a check per repository (`SyncSingleRepositoryAsync(checkFileVersions: false)`), then runs one check for the batch, after the effective versions are resolved. Before, each refresh ran its own check, and ran it before the effective versions were resolved.
- The check that directly followed `RecomputeAndBroadcastWorkspaceSyncedAsync` at the end of a run is removed (two places); that call runs it.
- The check no longer broadcasts when it runs inside a recompute (`deferDependencyRecompute`). Pages no longer reload with new file badges next to old dependency badges.
- **Kept, different from the first plan:** the check at the end of `SyncDependenciesAsync`. It runs `SyncGeneratedPackageDependenciesAsync`, which reads the `.csproj` files that sync has just rewritten, and the dependency recompute on the next line reads those rows. With phase 1 the check costs one small Worker call.
- The check at the start of each level after the first is kept as it was.

### Phase 3: one recompute per burst in the sync queue

- `SyncCommandHandler` is split (`ISyncNotificationHandler`): `ApplyAsync` is the per-repository part, `CompleteAsync` the per-context recompute and broadcasts. `HandleAsync` is "apply, then complete".
- `WorkerSyncNotificationQueue` keeps one reader and one open batch: consecutive notifications of one (Workspace, context) whose state is stored. It completes the batch when the channel is empty, when a notification of another context has been attributed (before that one can wait for its Feature), and at 64 notifications (**decision 4, taken as recommended**). No timer.
- A notification stays pending for the barrier and the UI count until the completion that covers it returned or threw. One that wrote nothing is released at once.
- Per-repository events are sent at completion, after the recompute, then the context event once.
- **Different from the first plan:** a batch in which no notification stored anything (all skipped or superseded) sends no event, as before. Nothing changed, so there is nothing to reload.
- **The PLAUSIBLE item was reproduced and fixed.** With the old queue, an `OperationCanceledException` that was not shutdown ended the reader for good and every later barrier wait hung (the test timed out on the second notification). The reader now treats only shutdown as the end; any other cancellation is a failed item or completion.

### Phase 4: pages do less per event

- The notification panel no longer subscribes to `ContextRepositorySynced`. Its refreshes run one per Workspace at a time; an event during a refresh shares the next one. That also ends the last-writer-wins race between two refreshes. No timer was added.
- **Decision 3, taken as recommended:** the Repositories page reloads a Feature grid on `ContextSynced` of its own context, with its existing debounce.

### Tests

- `WorkerSyncNotificationQueueBatchTests` (13): one completion per burst; the barrier and the UI count during a gated completion; a failing item; a failure after the state write; a failing completion; the non-shutdown cancellation; two notifications of one repository; the sequence A, A, B, A with B waiting; refs-only; skipped and superseded; a notification during a completion; the maximum batch size; stop with an open batch.
- `FileVersionStoredCommitTests` (12): the stored commit is sent and `GetHeadCommits` is not called; a Feature uses its own commit; no state row; a blank commit with a seeded out-of-date row; the update still reads live; a hook sync end to end; the operation start (stored, unreadable, no token); Update stops before any change; Update decides from the live commit; a burst through the real handler gives one check and one context event.
- `FileVersionNotCheckedMigrationTests`: step 22 on a version 21 database.
- `PullDependencyRefreshTests`: one context event per pull recompute instead of two.

All 5,463 tests of `GrayMoon.App.Tests` (without `Category=Long`) pass.

## Open

### Measure

Not done: it needs the App running against a real Workspace. On Edx1, before and after:

- Create Feature: time from "Feature Create finished" to the last `SyncCommand persisted` of that context.
- Update and Push: run time, and the count and total of `CheckAndPersist` runs and `GetHeadCommits` calls.
- The `Slow sync notification` warning: none expected.

The build that wrote the analysed logs (`0.2.1-main.43`) predates effective versions, so the first measurement on a current build is also the first one that includes them.

### Paths that can leave the stored commit old or blank

The badge now depends on the stored commit, so these matter more than before. Update itself is protected by the read at its start. A survey of the code found identity written without a commit in these places; none is changed yet, and each needs a regression test first.

| # | Problem | Change |
|---|---|---|
| 1 | The state writer keeps the old SHA when identity was probed on a branch but the SHA is blank (`WorkspaceRepositoryStateWriter.ApplyIdentity`) | Blank clears the stored SHA, as the tag branch of the same method does. Only after 2 to 6: today several callers send identity without a SHA, and clearing would turn their repositories to "not checked" after every such operation |
| 2 | Create branch onto a branch that already exists, with hooks off (Prepare Workspace): HEAD moves, no SHA is returned (`WorkspaceBranchOperations`, `WorkspaceGitService.Branches`) | `CreateBranchCommand` returns the HEAD SHA after checkout in every case; both App callers store it |
| 3 | Update commits with hooks off; the SHA is stored by the refresh that follows. When that refresh fails or the run is aborted in between, the old SHA stays | The failure path reports "state not refreshed, run Sync" for those repositories |
| 4 | Return to Default fails at the pull, after the checkout already moved HEAD; no state is written | Write the state the Worker reports for the checkout before returning the pull failure |
| 5 | Shapes built without a SHA that mark identity as probed: `SyncCommandHandler.BuildSnapshotFromFlatNotification`, `WorkspaceGitService.SnapshotFromFlatInfo`, the flat branches of `WorkspaceCommitSyncHandler.BuildSnapshot` and of Return to Default | App and Worker are version-locked, so the Worker always sends `State`: delete the flat shapes. About 20 tests of `SyncCommandHandlerTests` build flat notifications and move to `State` in the same change |
| 6 | `RefreshRepositoryVersionCommand` on a missing folder returns branch "-" with identity probed | Return identity not probed |

Not detectable at all: a HEAD moved with no hook GrayMoon hears (`git reset`, a rebase, a foreign `core.hooksPath`, a notification lost while the Worker was disconnected). The badge then compares with the old commit until the next hook, Sync or Update.

### Tests not written

- A two-level Update with `{L1}` and `{L1:commit}` tokens in a level 2 file that counts the checks per level. The existing Update tests pass, and the start of the run is covered, but no test pins "level 2 is updated with level 1's new version and commit".
- A burst for a new Feature asks the Worker for the branch-point version once per repository, and an `Unresolved` registry is probed once per recompute. It follows from one recompute per batch; no test asserts the count.

### Later, only if the numbers still call for it

- **Push from GrayMoon**: run with hooks off and keep one full post-push sync from the command, instead of the pre-push sync, the post-push sync, the deferred sync and the App's own count read. Changes the Worker. Pushes from a terminal keep the hook path.
- **Switch branch and Create branch from the dialog**: return state inline and recompute once, the way Prepare Workspace does.
- **Effective versions**: keep the known parent version across an `Unresolved` decision, so a retry does not ask the Worker again; resolve at the end of Create Feature inside the overlay so the grid opens with final versions.
- **Create Feature without a hook burst** (option C of the analysis).

### Found on the way, not part of this plan

Each needs its own note in `defects/` before it is worked on.

- The Actions page starts up to 10 GitHub polls per repository for any hook kind, not only after a push, with no guard against overlapping loops.
- In a Feature the Actions page never receives the per-repository event, so a push from a Feature does not start its refresh.
- A Worker error during a branch-point version probe is stored as a decision and reused until HEAD moves.
- A `{@Repo}` token without a version and a `{@Repo:branch}` token on a detached HEAD are still skipped silently, as the commit token was. They could use the same "not checked" row.
- A file whose tokens all have no value is not sent to the Worker, so its "missing on disk" flag is not refreshed by that check.

## Decisions

All four were taken as the plan recommended, without a separate confirmation. Each is one small change to reverse.

1. **HEAD moved without a hook**: the update reads the live commits once at its start; the badge stays on the stored value.
2. **A `:commit` token without a value**: shown as not checked, with the reason, and counted as out of date.
3. **Feature grid reload on a hook sync**: on `ContextSynced` of its own context, with the existing debounce.
4. **Maximum batch size**: 64.
