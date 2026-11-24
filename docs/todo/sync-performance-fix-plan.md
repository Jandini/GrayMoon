# Sync performance fix plan

Status: built on 2026-11-24 (phases 1 to 6). Create Feature is measured on Edx1; Update, Push and the pages are not. What is still open is listed under "Open". Evidence and the measurements that started this are in `ideas/feature-create-background-sync-cost.md`. How the built parts work is in `docs/architecture/` (03 "WorkspaceFileLineStatus", 04 "Hook sync batches", 05 "File versioning").

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

### Phase 5: every path stores the commit it left behind

The badge now depends on the stored commit, so the paths that could leave it old or blank are closed.

| # | Was | Now |
|---|---|---|
| 1 | The state writer kept the old SHA when identity was probed on a branch but the SHA was blank | Blank clears it (`WorkspaceRepositoryStateWriter.ApplyIdentity`), as on a tag |
| 2 | Create branch onto a branch that already existed, with hooks off (Prepare Workspace): HEAD moved, no SHA returned | `CreateBranchCommand` returns `headCommitSha` in every case; New Branch and Create Branches store it |
| 3 | Update commits with hooks off; when the refresh after it failed or the run was aborted in between, the old SHA stayed and nothing said so | Every such repository gets the error "... The commit was made, but the repository's commit and version were not refreshed. Sync the repository." All failed refreshes are reported, not only the first |
| 4 | Return to Default failing at the pull, after the checkout had moved HEAD, wrote no state | The Worker's failure carries the state the checkout left; the App stores it and still reports the failure |
| 5 | Shapes built without a SHA that marked identity as probed (hook notification, Sync, Pull and Return to Default fallbacks for old Workers) | Deleted. A hook notification without `State` is logged and ignored; a pull that reports none writes only the sync status; a successful Return to Default without one is a failure |
| 6 | `RefreshRepositoryVersionCommand` on a missing folder answered branch "-" and the App stored "no branch" | The command fails with "The folder of repository X was not found." |
| 7 | Found in the logs of 2026-11-24 21:07 (Edx1.File.Backup after Merge with "return to default"): the return wrote main, then a push-hook snapshot taken on the feature branch before it was applied from the queue and put the feature branch back in the grid. Return to Default, Switch branch and Create branch did not tell the stale-snapshot guard when they began | They pass `OperationStartedAt`, so a snapshot received before the operation began no longer overwrites what it wrote (`StaleHookSnapshotTests`) |

Not detectable at all, and unchanged: a HEAD moved with no hook GrayMoon hears (`git reset`, a rebase, a foreign `core.hooksPath`, a notification lost while the Worker was disconnected). The badge then compares with the old commit until the next hook, Sync or Update; Update itself reads the commits at its start.

### Tests

- `WorkerSyncNotificationQueueBatchTests` (13): one completion per burst; the barrier and the UI count during a gated completion; a failing item; a failure after the state write; a failing completion; the non-shutdown cancellation; two notifications of one repository; the sequence A, A, B, A with B waiting; refs-only; skipped and superseded; a notification during a completion; the maximum batch size; stop with an open batch.
- `FileVersionStoredCommitTests` (13): the stored commit is sent and `GetHeadCommits` is not called; a Feature uses its own commit; no state row; a blank commit with a seeded out-of-date row; the update still reads live; a hook sync end to end; the operation start (stored, unreadable, no token); Update stops before any change; Update decides from the live commit; a burst through the real handler gives one check and one context event; a burst asks an unreachable registry and the branch-point version once.
- `UpdateLevelFileCheckTests`: a two-level Update. Level 2's file gets level 1's new version and commit; four checks and two `GetHeadCommits` calls for the whole run.
- `StoredHeadCommitPathsTests` (8): one per row of the phase 5 table, each with a seeded branch and commit the old code would have left or overwritten.
- `FileVersionNotCheckedMigrationTests`: step 22 on a version 21 database.
- Worker: `HeadCommitReportTests` (create onto an existing branch, refresh of a missing folder), `ReturnToDefaultBranchCommandTests` (failure at the pull carries the state).
- `PullDependencyRefreshTests`: one context event per pull recompute instead of two.
- Tests that built the deleted flat shapes now send the state snapshot the Worker sends (`WorkerStateTestExtensions`, `RepositorySyncNotificationBuilder`).

All tests of `GrayMoon.App.Tests`, `GrayMoon.Worker.Tests` and `GrayMoon.Common.Tests` (without `Category=Long`) pass.

## Open

### Measure

**Create Feature, measured on Edx1 on 2026-11-24 with build `0.2.1-app-backgroun-jobs.46` (27 repositories, no CodeGraph index):**

| | Before (`0.2.1-main.43`) | After |
|---|---|---|
| Create Feature itself | 26.6 s | 13.5 s |
| From "Feature Create finished" to the last notification applied and recomputed | 51.8 s | 10.5 s |
| Recomputes for the 27 notifications | 27 | 2 (one for the first 2 notifications, one for the other 25) |
| `GetHeadCommits` calls | 27 | 0 |

The 10.5 s are 2.8 s until the Worker's first notification arrived, 7.2 s for the first effective version resolution of the new Feature (17 repositories checked at their branch point), and 0.25 s for everything else: the second recompute took 104 ms. The effective version resolution is now the whole remaining wait; "Effective versions" under "Later" is the change that removes it from this window.

Still to measure (they need commits and pushes in real repositories, which is the owner's call):

- Create Feature: time from "Feature Create finished" to the last `SyncCommand persisted` of that context.
- Update and Push: run time, and the count and total of `CheckAndPersist` runs and `GetHeadCommits` calls.
- The `Slow sync notification` warning: none expected. The logs of 2026-11-24 (AVR, 40 repositories) had 61 during Create Feature, because the reader waited for the Feature inside one notification; fixed, see `defects/create-remove-feature-log-findings.md`. The same logs show the `EffectiveVersions` stage at 82.4 s, queued behind the hook burst on the Worker; open in that note.

The build that wrote the analysed logs (`0.2.1-main.43`) predates effective versions, so the first measurement on a current build is also the first one that includes them.

To look at while measuring:

- the dependency badge tooltip line "not checked: No commit is recorded for Repo. Sync the repository." (rendered with the existing tooltip styles, not looked at in a browser);
- a Feature grid reloading by itself after a commit made in a terminal.

## Phase 6: follow-ups built after the measurement

- **Create Feature decides the effective versions before it finishes** (stage `EffectiveVersions`, after the seed). This was the 7.2 s of the measured 10.5 s: the grid now opens with final versions, and the first hook recompute reuses the stored decisions. A failure is logged and Create still succeeds; the next recompute resolves again. Not measured again after this change.
- **A parent version is calculated once per commit** (`BranchPointVersionCache`, in memory). A repository whose packages cannot be verified no longer runs GitVersion on the Worker in every recompute while a registry is failing.
- **A failed Worker call for the parent version is not kept as a decision** (`ParentVersionProbeFailed`, never reused). It used to be stored as "own version" and reused until HEAD moved.
- **The check sends every configured file to the Worker**, also one whose tokens all have no value, so a deleted file is noticed. A file reported missing has no "not checked" rows.
- **Actions page:** it now listens to the Context events of its Feature, so a push from a Feature starts its refresh; and it runs one GitHub polling loop per repository at a time (`KeyedRerunGuard`), where every hook sync used to start its own loop next to the others.
- **A `{@Repo}` token of a repository without a version is "not checked"** (owner decision, 2026-11-24): it counts on the badge with "No version is known for Repo. Sync the repository." A version still being calculated for a new Feature is not reported. A `{@Repo:branch}` token of a repository without a branch stays silent, because a tag pin has none on purpose and its badge could never be cleared.

Tests: `FileVersionStoredCommitTests` (Create Feature decides before it finishes; the missing file), `EffectiveVersionResolverTests` (a failed probe is asked again; unresolved does not rerun GitVersion), `KeyedRerunGuardTests`.

## Not built, with the reason

The plan said to measure before starting these. The measurement says they are no longer where the time goes: 25 queued notifications are applied and recomputed in 0.25 s.

- **Push from GrayMoon with one sync instead of several.** The App side of the 4 to 5 notifications per repository is now one or two cheap recomputes. What is left is Worker time (GitVersion runs for the pre-push, post-push and deferred syncs). Changing that means rewriting the Worker's push flow, the most sensitive one in the product, for a gain nobody has measured. To decide: time a push of 5 repositories on Edx1 and look at the Worker log between `Git push completed` and the last `SyncCommand persisted`.
- **Switch branch and Create branch returning their state inline.** Same reasoning: 27 checkout hooks are now one batch.
- **Create Feature without a hook burst.** The burst now costs 0.25 s in the App. The Worker's GitVersion runs, the only new information, take the same time wherever they are reported.

## Decisions

All four were taken as the plan recommended and confirmed by the owner on 2026-11-24.

1. **HEAD moved without a hook**: the update reads the live commits once at its start; the badge stays on the stored value.
2. **A `:commit` token without a value**: shown as not checked, with the reason, and counted as out of date.
3. **Feature grid reload on a hook sync**: on `ContextSynced` of its own context, with the existing debounce.
4. **Maximum batch size**: 64.
