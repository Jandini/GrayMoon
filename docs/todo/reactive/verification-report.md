# Reactive workspace engine - verification report

Written by package Z1 on 2026-11-26 for the run of `docs/todo/graymoon-reactive-architecture-implementation.md` (workspace root repository). It says what was built, what the tests showed, which acceptance criteria of section 9 are met and which are not, and what only the owner can check.

Nothing is committed. All work is uncommitted on `reactive-approach`: 82 changed and 39 new files in `GrayMoon/`, one changed file in `GrayMoon.Desktop/` (`README.md`). No file is staged in any of the three repositories. Neither the App, the Worker, the Desktop shell nor a browser was started during the run.

## 1. Test results

Final run, 2026-11-26, after the last change, `dotnet build GrayMoon.slnx` then the three test projects without a filter (long tests included):

```text
Build                 0 errors, 6 warnings (the 6 that were there before the run)
GrayMoon.Common.Tests Failed 0, Passed  480
GrayMoon.Worker.Tests Failed 0, Passed 1239
GrayMoon.App.Tests    Failed 0, Passed 5681
Long tests on their own (--filter "Category=Long"): Worker Passed 2, App Passed 2
```

Before the run: Common 480, Worker 1189, App 5517 (long tests included). The run added 50 Worker tests and 164 App tests. No test was deleted. Tests that were rewritten are named in the ledger with the reason (the removal tests that asserted "the whole removal is refused" now assert the per-repository contract; one assertion "23 is the last migration step" was removed).

Migration upgrade: `ReactiveSchemaUpgradeTests` takes a version 22 database with a Workspace, a Feature whose removal was interrupted (one row `Removed`, one `Removing`) and stored inherited versions, runs the whole runner twice and checks the version (24), both new columns, every row and that the new code can write to it. `EffectiveVersionEvidenceKeyTests` upgrades a version 15 database through the runner (the step that inserts context state rows through the model) and a version 23 database through step 24. `FeatureRemovalOutcomeMigrationTests` runs step 23 on a version 22 database. All 47 migration tests pass.

## 2. What each package built and what its tests showed

| Package | Built | Evidence |
|---|---|---|
| P0 | Counting helpers: `GitProcessCounter` (Worker tests), `WorkerCommandCounts` (App tests). Baseline of the suites | Their own tests; used by every later count test |
| R1 | The refresh before a Feature removal is a fetch per repository, not a Sync of the Workspace. Create Feature compares the live HEAD snapshot with the stored state and syncs only the repositories that differ | `RemoveFeatureWorkspaceRefreshTests` (zero `SyncRepository` around a removal; an explicit Sync afterwards changes nothing), `CreateFeatureStoredStateCheckTests`, `FetchCommitsCommandTests` |
| C1 | The typed outcome contract (`OperationOutcome`, targets, steps, reason codes, statuses including unknown and partial) on `OperationResult`, without changing its JSON for existing callers | `OperationResultContractTests` (golden JSON), `OperationOutcomeContractTests` |
| R2 | Safe-first Feature removal: each repository on its own, nothing forced without a reviewed plan, three new Worker guards (nested repository, detached commits not held by a ref, operation in progress), evidence-based refusal codes, branch delete with a tip lease, every step stored on its row, retry runs only what is open. The dialog opens from stored state; "Review details" inspects on demand. Migration step 23 | Worker: `WorktreeRemovalSafetyTests` (23, real git), `GitServiceDeleteBranchDetailedTests` (6, real git). App: `RemoveFeatureSafeFirstTests` (17), `RemoveFeatureSummaryTests`, `FeatureRemovalOutcomeMigrationTests` |
| S1 | Order of two reports decided by evidence: the Worker stamps every snapshot with its session and a capture interval; older is dropped, a duplicate ignored, overlapping captures applied and the repository read again; branch lists ordered across the worktrees of one repository; a deleted context is not written; the state writer reports what it changed | `RepositoryStateEvidenceTests` (18), `RepositoryStateCaptureStampTests` (2), `StaleHookSnapshotTests` unchanged and green |
| S2 | The context recompute after a batch of Git events runs only when a write changed the commit, the version or the projects (or did not say, or a recompute is owed). A project scan is compared with the stored rows | `ScopedRecomputeTests` (17): no-op, counts-only, refs-only, commit, project edit, owed, and "the full recompute changes nothing" after each of 11 notifications for both profiles. With the old rule forced, 4 of them fail |
| G1 | A lost Git hook is repaired without another event: every Git Changes status scan (the periodic audit included) is compared with the stored commit and branch; a difference that is still there after 30 s asks for one read of that repository | `RepositoryStateDriftMonitorTests` (18), time on a manual clock |
| V1 | A stored version decision carries a key of what it was made from (parent branch, branch point, required packages and their registries) and is made again when the key differs. Bulk Push waits for pending Git events before it builds its plan. Migration step 24 | `EffectiveVersionEvidenceKeyTests` (8; with the key ignored, 4 fail), `EffectiveVersionStartCheckTests` (13, one new) |
| F1 | Create Feature ends when the worktrees exist and are seeded; the version stage runs in the background and keeps the finalization barrier and the hold on Checkout syncs; Update, Push and Prepare wait for it in their start check. Repair pauses Git Changes monitoring while it retries worktrees | `CreateFeatureVersionStageTests` (6), `CreateFeatureMonitoringPauseTests` (4, two new) |
| O1 | Undo Push Commits refuses unless the named branch is checked out; after a discarding undo the projects are read again. Every row W02 to W10 and W16 traced | Worker `GitServiceUndoUnpushedCommitsTests` (three new cases; all three fail without the guard, on real git), `PushUndoReturnCapabilitiesTests` (three new cases) |
| M1 | A changed Workspace profile makes the next completion of each context recompute once. What the App remembers in memory about a removed Workspace is dropped. Rows W17 to W21 and W26 traced | `ScopedRecomputeTests` (two of its tests), `RepositoryStateEvidenceTests` (one) |
| U1 | Actions, Dependencies, Files and Packages follow `?context=` while they stay open, load one context at a time and drop a load that was overtaken | `PageContextBindingTests` (5), `ContextPageBindingDisciplineTests` (12 cases), `WorkspaceContextNavigationServiceTests` (7 new cases) |
| X1 | Trace of REST, connectors, AI, native tools, settings. A convention test that every mutating REST route goes through an operation facade under the Workspace lock. No product change | `ApiEndpointFacadeConventionTests` (2) |
| Z1 | The upgrade test, the self-review, this report | `ReactiveSchemaUpgradeTests` (1) |

The check inventory (`check-inventory.md`) has 77 rows. Every row has a decision, a reason and a test or a stated reason for none: 54 retained, 9 extended, 7 narrowed, 4 replaced, 1 reduced, 1 background, 1 deferred.

## 3. Acceptance criteria of section 9

### 9.1 Correctness gates, the mandatory scenarios

| Scenario | Result | Evidence or gap |
|---|---|---|
| A known mutation updates state from its outcome; a later duplicate hook causes no duplicate heavy work; an external change in the same window is detected | Met | `ScopedRecomputeTests` (a notification that confirms the stored state does not recompute), `RepositoryStateEvidenceTests` (duplicate dropped), `RepositoryStateDriftMonitorTests` |
| Stale hook after a newer write; captured before and received after; sibling worktrees sharing refs; late notification from a deleted context | Met | `StaleHookSnapshotTests`, `RepositoryStateEvidenceTests` (the four named tests). Ambiguous order asks for a read |
| Late UI result after a context switch | Met as logic, not rendered | `PageContextBindingTests`, `ContextPageBindingDisciplineTests`. No component test renders a page (the repository has no component test host). Owner check 5 |
| Watcher lost or overflowed, idle expiry, disconnected Worker, Worker restart, App restart, paused context, external checkout / commit / reset and file edit; recovery without a later event; no false clean while unavailable | Met, with one stated limit | Git Changes side: `GitChangesAuditSchedulerTests`, `GitChangesWatcherKeeperTests` (unchanged). Repository state side: `RepositoryStateDriftMonitorTests`. Limit: a branch or tag created or deleted without a HEAD move whose own hook was lost is not seen by the drift check (doc 04, "A lost hook") |
| Safe partial removal: dirty, locked, missing, unknown targets; nested root; no forced discard; scoped consent; branch-only retries; lease refusal; lost reply between git and the database; no Sync of the main checkouts; no foreign residue deleted | Met | R2 tests above, `RemoveFeatureWorkspaceRefreshTests`, Worker `FeatureFolderCleanerTests` |
| Create, Repair, Rollback, Prepare with failure or cancel before and after intent, detached / read only / pinned, branch collision, incomplete checkout, another operation running | Met for what was changed; the rest unchanged and green | Existing Create and Prepare tests; `CreateFeatureVersionStageTests`, `CreateFeatureMonitoringPauseTests`. Rollback was not changed |
| A no-op Feature version causes no push or publication; version, parent, package mapping changes invalidate the decision; registry unavailable, missing, exact version; failed CI; two-lane Update and Push; generated edges and commit tokens; incremental equals full | Met, with one stated limit | `EffectiveVersionStartCheckTests`, `EffectiveVersionEvidenceKeyTests`, `EffectiveVersionResolverTests`, `UpdateAndPushOrchestratorPipelineTests`, `ScopedRecomputeTests`. Limit: a failed CI run is shown and named in the timeout report but does not end the package wait early (owner decision 3) |
| Every UI, REST and MCP entry point uses the same policy and result; a protected repository cannot be changed by another route | Met for UI and REST | `ApiEndpointFacadeConventionTests`, policy characterization tests, `OperationResultContractTests`. There is no MCP entry point in the repository |
| Bulk operations keep their selection and per-repository results; the Repositories page and the pages changed handle back / forward and context switches without wrong data | Met as logic | As "late UI result" above. Owner check 5 |

Other requirements of 9.1: tests that fail on the old behavior exist for every behavior change and the proof is recorded per package in the ledger; destructive behavior is tested on real temporary git repositories; concurrency tests use gates and a manual clock, not sleeps; no GitHub write and no package publication happens in any test; expected refusals are logged at Information, real failures at Warning or Error.

### 9.2 Performance gates

No measurement on the VDI was possible. Nothing in this report is a measured time. The structural expectations, which are testable here:

| Expectation | Result | Evidence |
|---|---|---|
| Removal does not need a live analysis of all repositories first; safe repositories do not wait for blocked ones | Met | `RemoveFeatureSummaryTests` (no Worker command to open the dialog), `RemoveFeatureSafeFirstTests` |
| A normal removal sends zero `SyncRepository`; the fetch-only step runs no GitVersion, project, file or dependency work; Create right after syncs only what differs | Met | `RemoveFeatureWorkspaceRefreshTests`, `CreateFeatureStoredStateCheckTests` |
| The process counts of `FeatureWorktreeGitProcessCountTests` do not rise | Met | The test is unchanged and passes |
| A source edit, a no-op notification and a branch-only change trigger no GitVersion, registry, project or graph work; real input changes still do | Met | `ScopedRecomputeTests` |
| No duplicate evidence in result, hook and job end; no dropped unknown event | Met | `RepositoryStateEvidenceTests`, `RepositoryStateDriftMonitorTests` |
| Full reconciliation is deliberate; audits bounded | Met | The explicit Sync is the only all-repository read left; the drift check asks for single-repository reads through a bounded, keyed queue |
| Creation keeps the HEAD snapshot, the monitoring pause and the hold on Checkout syncs; version readiness stays correct when deferred | Met | `CreateFeatureHookSyncHoldTests`, `CreateFeatureMonitoringPauseTests`, `CreateFeatureVersionStageTests` |

Which rows of the table in section 2.3 the run should move, to be measured by the owner:

| Row of section 2.3 | Expected effect | Why |
|---|---|---|
| Remove Feature (49.8 to 59.3 s, refresh not included) | The refresh before it becomes one fetch per repository instead of one Sync per repository. The removal itself no longer inspects before removing | R1, R2. The fetch-only step has no measurement at all yet |
| `InspectWorktree` during "Checking" (5.4 to 7.3 s) | Gone from opening and confirming the dialog. It runs only for "Review details" | R2 |
| Create, `EffectiveVersions` (19.8 to 22.4 s) | No longer part of the wait: Create ends before it. The work itself is the same and runs in the background | F1 |
| Create in total (105 s alone) | Shorter by the `EffectiveVersions` stage | F1 |
| Create, `HeadSnapshot` and `SourceWorktrees` | Unchanged | Not touched: section 6.1 asks for measurements first |
| "CheckAndPersist completed" | Unchanged per run; it runs less often (only when an input changed) | S2 |

New background work to count in the full convergence window: one `RefreshRepositoryVersion` per repository whose stored state was found behind (G1) or whose captures overlapped (S1); two small reads of project rows before and after each hook's project reconcile (S2).

### 9.3 Self-review

The diff was read for the points section 9.3 lists.

- Per-context state: no new write to `WorkspaceRepositoryContextStates` or to the link outside `WorkspaceRepositoryStateWriter`. The drift monitor only reads.
- Queries: the new ones are scoped by context id (`ReadProjectFingerprintAsync`, the drift monitor) or by Workspace id on purpose (the contexts of a Workspace being removed).
- Swallowed errors: every new `catch` logs or rethrows. One skips silently by design: an unreadable folder in the nested-repository scan of R2.
- Background tasks: two new ones, both tracked, both catch everything and both stop with the App: `FeatureVersionStage` (10 minute limit) and `RepositoryStateDriftMonitor` (one waiting comparison per repository). `RepositoryStateReconciler` is a hosted service with a bounded queue.
- Force flags: none added. The only forced operations are the ones R2 scopes to a reviewed plan with a commit or tip lease. O1 added a refusal.
- Hidden remote refresh: none added. R1 removed one (the Sync around a removal).
- Migrations: steps 23 and 24, both append-only, idempotent, tested from older versions.
- False-ready badges: the version column of a new Feature is empty until the background version stage has ended; operations that depend on versions wait for it.
- In-memory records (state evidence, drift entries, recompute marks) are dropped when a Feature is finished and when a Workspace is removed.
- Line endings are CRLF and no en or em dash was added in any changed or new file.

## 4. Not met, not built, or decided narrowly

Each item is recorded with its reason in the ledger or the inventory.

1. **No VDI measurement.** Section 9.2 asks for before and after numbers; only the structural expectations could be shown.
2. **No rendered UI test.** The Remove Feature dialog (R2) and the four pages (U1) were verified as logic and by source checks, not in a browser.
3. **Drift check limit.** A branch or tag change without a HEAD move whose hook was lost, and remote state, are not found by it (G1).
4. **Recompute closure is all or nothing.** When an input changed, the whole context is recomputed; a per-repository closure was not built because no test proves a narrower rebuild equal (S2). The file-version check was not narrowed to changed files (V1, inventory W24).
5. **Broadcasts unchanged.** A notification that changed nothing is still announced; single-row patches on the other pages were not built (U1, inventory B12).
6. **Create Feature has two paths for its version stage.** Production runs it in the background. A host without the `FeatureVersionStage` service runs it inside Create; the existing Create tests use that. The owner prefers designs without a second path (F1).
7. **Evidence is in memory.** After an App restart the order of reports is decided by arrival again, as before the run; the drift check then repairs what was missed.
8. **HeadSnapshot and worktree checkout** were not optimized (no measurement possible).
9. **Rollback** was not converted to per-repository outcomes (it needs consent rules the plan does not delegate).
10. **MCP** has no entry point in the repository; the transport-neutral contract is held by the facades and the REST tests.

## 5. For the owner

### Decisions that are yours

1. **Return to Default forces the local branch delete for a merged or closed pull request also without the tick** (`WorkspaceGitService.ReturnToDefaultDirectAsync`, the comment there records it as intended). For a closed, unmerged pull request this deletes unmerged commits. The REST body defaults the tick to true. Not changed.
2. **Rollback** stays "refuse everything when one repository is dirty". Say so if it should become safe-first like removal.
3. **A failed CI run does not end the package wait of a push early.** A re-run can still publish; ending early would be a behavior change.
4. **A "pending" marker for versions** of a new Feature while the background stage runs: today the column is empty.
5. **REST works on the special Workspace only.**
6. **The second path of the Create version stage** (item 6 above): remove it by registering the stage in the test host and adapting about twenty Create tests, or keep it.

### Checks only you can do

1. **Remove Feature dialog, both states**: opens at once from stored state; "Review details" loads the per-repository inspection; Confirm is enabled as described in doc 05; a removal that keeps a repository shows its reason and the Feature Status shows the stored outcome afterwards.
2. **A removal that is incomplete, then Remove again**: only the open steps run.
3. **Roll back and external worktree removal**: the three new Worker guards of R2 apply there too (nested repository, detached commits, operation in progress).
4. **Create Feature**: the overlay closes when the worktrees are ready; versions appear in the grid shortly after; Update or Push started at once shows "Verifying published packages..." and then proceeds.
5. **Actions, Dependencies, Files, Packages**: switch the Feature with the selector, then use the browser's back and forward; switch twice quickly; open a URL with another Workspace's `?context=`.
6. **Undo Push Commits** on a repository whose branch was switched in a terminal a moment before: refused with the new message.
7. **A commit made in a terminal while the Worker is stopped**: after the Worker is back, the grid shows the commit without a Sync, at the latest about five minutes later for an open Workspace.
8. **Push right after a commit**: the repository is in the plan; "Applying n pending repository changes..." shows only when something is pending.
9. **Change the Workspace profile**, then commit once in any repository: the badges of that context are recalculated.
10. **VDI measurements** against the table in section 2.3, as listed under 9.2 above. The fetch-only step before a removal has no measurement at all.
11. **Upgrade of a real database** from the released version: a backup `graymoon.db.bak-*` is written, the App starts, schema version 24.
