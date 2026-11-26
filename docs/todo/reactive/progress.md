# Reactive workspace engine - progress ledger

The ledger of section 10.4 of `docs/todo/graymoon-reactive-architecture-implementation.md` (workspace root repository). With the uncommitted working tree and `check-inventory.md` beside this file it is the only memory between sessions: read it first, update it before stopping.

Nothing is committed. Every package's work is uncommitted on `reactive-approach` in `GrayMoon/` (code, tests, documents) and in `GrayMoon.Desktop/` (the README bullets only).

## Status

| Package | Status | Since / finished |
|---|---|---|
| P0 | done | 2026-11-26 |
| R1 | done | 2026-11-26 |
| C1 | done | 2026-11-26 |
| R2 | done | 2026-11-26 |
| S1 | done | 2026-11-26 |
| S2 | done | 2026-11-26 |
| G1 | done | 2026-11-26 |
| V1 | done | 2026-11-26 |
| F1 | done | 2026-11-26 |
| O1 | done | 2026-11-26 |
| M1 | done | 2026-11-26 |
| U1 | done | 2026-11-26 |
| X1 | done | 2026-11-26 |
| Z1 | done | 2026-11-26 |

Order: P0, R1, C1, R2, S1, S2, G1, V1, F1, O1, M1, U1, X1, Z1.

## How to run the suites

Inside `GrayMoon/`:

```powershell
dotnet build GrayMoon.slnx
dotnet test src/GrayMoon.Common.Tests/GrayMoon.Common.Tests.csproj --no-build --filter "Category!=Long"
dotnet test src/GrayMoon.Worker.Tests/GrayMoon.Worker.Tests.csproj --no-build --filter "Category!=Long"
dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj --no-build --filter "Category!=Long"
# before a package is set to done, also: --filter "Category=Long" for Worker and App (Common has no long test)
```

## Rule differences (plan against binding rules)

| Where | Difference | Followed |
|---|---|---|
| Plan header, "Repository" | The plan says the plan file is uncommitted; `git status` of the workspace root is clean and commit `f7a7de6` holds it. | The file on disk (same content). No effect on the work. |
| `GrayMoon/CLAUDE.md` "Task completion", `.cursor/rules/summary-of-changes.mdc` | One-sentence summary per task; the owner's message asks for a per-package report at the end of the run. | The owner's message (first in the binding order). |
| `.cursor/rules/no-commit-or-push.mdc` | "Always give a commit message at the end of a reply that changed files". | Followed at the end of the run: one message per repository in the final report. |
| Plan section 10.3, P0 "new test helper files only" | - | Followed: P0 changed no existing file. |

## P0 - baseline and coverage

- **Status:** done (2026-11-26).
- **Base:** `GrayMoon` `reactive-approach @ dd2c0ab9` (the reviewed baseline, clean tree); `GrayMoon.Desktop` `reactive-approach @ 1005791` (clean tree).
- **Changed files (all new):**
  - `docs/todo/reactive/progress.md`, `docs/todo/reactive/check-inventory.md`
  - `src/GrayMoon.Worker.Tests/GitProcessCounter.cs`, `GitProcessCounterTests.cs`
  - `src/GrayMoon.App.Tests/WorkerCommandCounts.cs`, `WorkerCommandCountsTests.cs`
- **Tests (baseline, before any edit, `dd2c0ab9`):**
  - `dotnet build GrayMoon.slnx`: 0 errors, 6 warnings (all in `GrayMoon.App.Tests`: CS8601, CS8714, CS8631 x3, xUnit2029; they were there before this work).
  - Common, `Category!=Long`: Failed 0, Passed 480, Skipped 0.
  - Worker, `Category!=Long`: Failed 0, Passed 1187, Skipped 0 (1 m 20 s).
  - App, `Category!=Long`: Failed 0, Passed 5515, Skipped 0 (26 s).
  - `Category=Long`: Common has none; Worker Passed 2; App Passed 2.
  - No pre-existing failure.
- **Tests (after P0):** `GitProcessCounterTests` Passed 1; `WorkerCommandCountsTests` Passed 2.
- **Decisions:**
  - The Worker-side helper is `GitProcessCounter` (an `ICommandLineService` wrapper that records file name, arguments and working directory). `FeatureWorktreeGitProcessCountTests` keeps its private recorder: P0 may only add files.
  - The App-side helper is `WorkerCommandCounts`, a read-only view over `FakeWorkerBridge.Calls` (`Of`, `Since`, `Count`, `ArgValues`, `AssertNone`, `AssertCount`). It needs no change to `FakeWorkerBridge`.
  - Inventory IDs: `R01`-`R18` (+`R09a`) for A.1.1, `F01`-`F13`, `W01`-`W26`, `B01`-`B12` for A.4, `X..` reserved for package X1.
- **Open points:** none.

## R1 - fetch-only refresh before a removal, stored-state check in Create

- **Status:** done (2026-11-26).
- **Base:** the tree after P0.
- **Changed files:**
  - App: `Services/Features/WorkspaceRemoteRefresh.cs` (new; replaces the deleted `WorkspaceCheckoutRefresh.cs`), `FeatureRemovalQueue.cs`, `WorkspaceFeatureOperations.cs` (class made `partial`, the check call after the collision check, three non-ASCII dashes in comments replaced), `WorkspaceFeatureOperations.StoredStateCheck.cs` (new), `Services/Git/WorkspaceGitService.Fetch.cs`, `Models/Api/WorkerSyncApiModels.cs` (`WorkerFetchCommitsResponse`), `GrayMoonServiceRegistration.cs` (section 10.5 file: the registration is now `IWorkspaceRemoteRefresh`, still a singleton).
  - Worker: `Commands/FetchCommitsCommand.cs`, `Jobs/Response/FetchCommitsResponse.cs`. No new command, so `WorkerHubMethods`, `CommandDispatcher`, `CommandJobFactory`, `WorkerCommandPoolRouter` and `RunCommandHandler` are unchanged.
  - Tests, new: `App.Tests/CreateFeatureStoredStateCheckTests.cs`, `Worker.Tests/FetchCommitsCommandTests.cs`. Changed: `RemoveFeatureWorkspaceRefreshTests.cs` (the "refresh runs a Sync" test replaced by five fetch-only tests), `SyncStateTestContext.cs` (`StoreHeadSnapshotAsCurrentStateAsync`, `FakeWorkerBridge.Peek`), `WorkspaceFeatureReconcilerTests.cs` (test double without `Start`), and one line (`StoreHeadSnapshotAsCurrentStateAsync`) in the Create fixtures of `CreateFeatureHookSyncHoldTests`, `CreateFeatureIntentAtomicityTests`, `CreateFeatureMonitoringPauseTests`, `CreateFeatureParentBranchTests`, `FeatureBranchPersistenceTests`, `FeatureContextIsolationTests`, `FeatureNameValidationTests`, `FileVersionStoredCommitTests`, `WorkspaceDependencyGatingTests`, `WorkspaceFeatureWorkspaceRepositoryTests`.
  - Documents: `CLAUDE.md`, `docs/architecture/02`, `03`, `04`, `05`, `06`; `GrayMoon.Desktop/README.md` (new top bullet, one sentence of the older Remove Feature bullet corrected).
- **Tests (2026-11-26, whole suites including `Category=Long`):** build 0 errors, 6 warnings (the baseline ones). Common Failed 0, Passed 480. Worker Failed 0, Passed 1197 (baseline 1189 + 1 helper + 7 `FetchCommitsCommandTests`). App Failed 0, Passed 5529 (baseline 5517 + 2 helper + 6 `CreateFeatureStoredStateCheckTests` + 4 net new in `RemoveFeatureWorkspaceRefreshTests`).
- **Done-when of section 10.3:**
  - Count test, no `SyncRepository` / GitVersion / project / file-version command around a removal: `The_removal_queue_refreshes_the_Workspace_with_a_fetch_and_sends_no_Sync`.
  - Behind after merged upstream commits: `Workspace_remote_refresh_fetches_and_stores_what_the_fetch_changes_without_a_Sync` (App), `Reports_the_commits_merged_on_the_remote_as_incoming_and_starts_no_git_process_but_the_fetch` (Worker, real git).
  - Create syncs exactly the differing repository before the seed, and fails naming it: `CreateFeatureStoredStateCheckTests`.
  - Oracle: `An_explicit_Sync_after_the_remote_refresh_changes_no_remote_observation` (App), `A_Sync_of_the_same_repository_reports_the_same_remote_observation` (Worker, real git).
  - `Start` / `WaitAndStartAsync` removed with the whole `WorkspaceCheckoutRefresh`.
  - The list of section 5.1: confirmed in `check-inventory.md` ("What the next Create Feature needs current").
- **Decisions:**
  - The service is renamed `WorkspaceRemoteRefresh` / `IWorkspaceRemoteRefresh`: it no longer refreshes a checkout, and the old name would describe behavior the code does not have.
  - No structural lock and no overlay for the step. It writes only groups a fetch observes (counts, upstream flag, tags, branch rows) through the state writer and never identity, version or projects, so it cannot conflict with what Create reads live. The `refresh-workspace` structural kind is gone; Create Feature is no longer refused while it runs. A fetch beside a `git worktree add` in the same repository was already possible (explicit Fetch or Sync from another context).
  - Built from what exists: the step is `WorkspaceGitService.QuickFetchAsync` (the Quick Fetch action), not a new command. To store "remote branch rows with pruning", `FetchCommits` now also returns the branch lists.
  - `FetchCommits` reads the local facts after its fetch from the in-process `ILocalGitSnapshotReader` (the read `SyncRepository` uses) and falls back to the git CLI reads when the snapshot cannot be used (failure, `GIT_DIR`, origin/HEAD missing or dangling). Reason: the fetch is then the only git process (was about 8), and the counts are by construction the ones a Sync reports, which is what the oracle test needs. This also changes the Quick Fetch button: same values, fewer processes, branch rows refreshed.
  - `FetchCommits` reports `commitCountsProbed` from the probe instead of the App guessing it from "any count has a value"; the tag list is sent also when empty so removed tags are pruned.
  - Quick Fetch writes pass `OperationStartedAt`, like Sync, so a hook snapshot received before the fetch cannot overwrite what the fetch stored.
  - Create check: "differs" is no stored commit, another commit, another branch, or another tag; a detached checkout that is not on a tag is compared by commit only (the stored branch of such a checkout is whatever its last probe called it). No stored commit counts as different: no evidence is not "current". After the Sync only the commit must match the snapshot, so a probe that names a branch differently makes the next Create sync again instead of refusing.
  - The Sync of the differing repositories uses `skipDependencyLevelPersistence: true` (the single-repository Sync convention): dependency levels are recomputed from the stored edges, never from a partial graph.
  - New Create failure condition `WorkspaceStateNotCurrent` (existing dialogs show `Error` for a condition they do not know).
  - Unproven and therefore not changed: `WorkspaceRepositoryStateWriter` creates a missing special-Workspace state row with `NeedsSync` and mirrors that onto the link on a write that leaves the status alone. No test or code path showed a link that is `InSync` without a state row (the migration and every Sync create the row), so it stays as it is; S1 looks at it again.
- **Rule differences:** the hyphen rule flags em and en dashes that were already in `docs/architecture/05-user-capability-reference.md` and `GrayMoon.Desktop/README.md` (not in text this package wrote). They are left: replacing them would change many unrelated lines. The three in `WorkspaceFeatureOperations.cs` and the one in `FeatureContextIsolationTests.cs` were replaced because those files were edited.
- **Open points:** none. For the owner: the fetch-only step has no VDI measurement yet (the "Remove Feature" row of section 2.3 does not include the refresh time).

## C1 - typed outcome contract

- **Status:** done (2026-11-26).
- **Base:** the tree after R1.
- **Changed files:**
  - `src/GrayMoon.Application/OperationOutcome.cs` (new): `OperationOutcome`, `OperationTargetOutcome`, `OperationStepOutcome`, `OperationTarget`, `OperationOutcomeStatus`, `DerivedStateReadiness`, `OperationTargetKind`, `OperationFactGroups`, `OperationReasonCodes`, `OperationAuthorizations`, `OperationOutcomeAggregate`.
  - `src/GrayMoon.Application/OperationModels.cs` (section 10.5 file): one additive property, `OperationResult.Outcome`, left out of the JSON when null.
  - Tests, new: `App.Tests/OperationResultContractTests.cs` (6), `App.Tests/OperationOutcomeContractTests.cs` (22 with the theory rows).
  - Documents: `docs/architecture/06-developer-extension-guide.md` (section 4, "The typed outcome"), one line each in `02` and `04`. No Desktop README bullet: no behavior changed.
- **Tests (2026-11-26, whole suites including `Category=Long`):** build 0 errors. Common Failed 0, Passed 480. Worker Failed 0, Passed 1197. App Failed 0, Passed 5557 (5529 + 28).
- **Done-when of section 10.3:**
  - REST shapes unchanged: the six golden-JSON tests of `OperationResultContractTests` were run and passed against the unchanged `OperationModels.cs` first, and pass unchanged with the new property.
  - Non-UI caller: `A_caller_that_is_not_the_UI_reads_a_partial_removal_from_JSON_without_parsing_text` reads "37 removed, 2 blocked, root waiting" from the serialized result with `JsonDocument` only.
- **Decisions:**
  - The outcome hangs off the existing `OperationResult` (`Outcome`), it does not replace it: REST serializes `OperationResult` as it is, so everything new must be additive. `[JsonIgnore(WhenWritingNull)]` keeps the JSON of operations without an outcome byte-identical.
  - Three levels: run, target, step. A step is the unit that has exactly one outcome (worktree, local branch, remote branch, residue), which is what section 5.3 needs to keep "removed, branch kept" apart from success and from failure.
  - Statuses: `Succeeded`, `NoOp`, `Skipped`, `Blocked`, `Failed`, `Cancelled`, `Unknown`, `Partial` (aggregate only). `NotApplicable` is a reason code of `Skipped`, not a status, so a reader has one "left out on purpose" case.
  - One aggregation rule (`OperationOutcomeAggregate.Of`): `Unknown` wins over everything, because nothing may be assumed about that part.
  - Reason codes and authorizations are string constants, not enums: a new code must not break a reader that deserializes strictly, and they are never renamed.
  - Enums of the new contract serialize as names (`JsonStringEnumConverter<T>` on the type); the existing `RemoveFeatureBranchOutcome` enums keep their numbers.
  - `OperationFactGroups` (flags) lives in `GrayMoon.Application`, beside the contract that carries it. S1 maps the state writer's probed groups onto it when it adds changed-fact reporting.
  - `ContractVersion` = 1 on the outcome instead of versioned routes: there is no second version to route to yet.
  - No operation fills `Outcome` yet; R2 is the first producer.
- **Open points:** none.

## R2 - safe-first Feature removal

- **Status:** done (2026-11-26).
- **Base:** the tree after C1.
- **Changed files:**
  - Worker: `Services/GitWorktreeService.cs` (`RemoveWorktreeAsync`: lock from the inventory, `CheckRemovalGuardsAsync`, `FindNestedRepository`, `IsCommitHeldByARefAsync`, `HasUncommittedChangesAsync`, `expectedHeadSha`, the registration returned with the residue result), `Services/WorktreeRemovalFailureClassifier.cs` (new kinds, `WorktreeRemovalErrorCodes`), `Abstractions/IGitWorktreeService.cs`, `Commands/RemoveGitWorktreeCommand.cs`, `Jobs/Requests/RemoveGitWorktreeRequest.cs` (`expectedHeadSha`), `Jobs/Response/RemoveGitWorktreeResponse.cs` (`branch`, `headSha`, `wasDetached`), `Models/WorktreeResidueResult.cs` (`Registration`), `Services/GitService.cs` (`DeleteBranchDetailedAsync`, `DeleteBranchCoreAsync`), `Models/BranchDeleteResult.cs` (new), `Abstractions/IGitService.cs` (default interface method), `Commands/DeleteBranchCommand.cs`, `Jobs/Response/DeleteBranchResponse.cs`. No new Worker command.
  - App: `Services/Features/WorkspaceFeatureOperations.Removal.cs` (new: the whole removal, the summary), `WorkspaceFeatureOperations.cs` (old `RemoveFeatureAsync`, `RunRemovalPhaseOneAsync`, `RemoveFeatureCoreAsync`, `RemovalPhaseOne`, `RemoveGitWorktreeResult` deleted; `FinalizeFeatureRemovalAsync` takes the new run record), `FeatureRepositoryRemovalRecord.cs` (new), `WorkspaceHookContextAttributor.cs` (Removed rows excluded), `Models/Features/WorkspaceFeatureRepository.cs` (`RemovalOutcomeJson`), `Migrations.cs` (section 10.5 file: step 23 appended), `Migrations.FeatureRemovalOutcome.cs` (new), `Components/Features/RemoveFeatureModal.razor`, `RemoveFeatureQuickDialog.cs` (new), `Components/Pages/WorkspaceRepositories.razor.cs` (`OpenRemoveFeatureDialog`, no analysis job).
  - Application: `Features/RemoveFeatureContract.cs` (new: `RemoveFeatureSteps`, `RemoveFeatureSummary`, `RemoveFeaturePendingRepository`), `Features/IWorkspaceFeatureOperations.cs` (`GetRemoveFeatureSummaryAsync`, `AnalyzedPlan` documentation), `OperationOutcome.cs` (reason code `NestedRepository`; additive, C1's serialization tests green).
  - Tests, new: `Worker.Tests/WorktreeRemovalSafetyTests.cs` (23), `GitServiceDeleteBranchDetailedTests.cs` (6), `App.Tests/RemoveFeatureSafeFirstTests.cs` (17), `RemoveFeatureSummaryTests.cs` (8 with theory rows), `FeatureRemovalOutcomeMigrationTests.cs` (2). Changed: `WorktreeRemovalFailureClassifierTests.cs`, `GitWorktreeCommandTests.cs` (one error code), `RemoveFeatureWorkspaceRefreshTests.cs` (three tests of the old whole-removal refusal rewritten to the per-repository contract, one expectation), `RemoveFeatureReportTests.cs` (fixtures send the Worker's `failureKind`; report lists earlier removed rows; unlock needs reviewed details; error text names the repository), `FeatureBranchPersistenceTests.cs` (fixture).
  - Documents: `CLAUDE.md`, `AGENTS.md` (destructive rule 3), `docs/architecture/01`, `03`, `04`, `05`, `06`; `GrayMoon.Desktop/README.md` (new top bullet).
- **Tests (2026-11-26, whole suites including `Category=Long`):** build 0 errors, 6 warnings (the baseline ones). Common Failed 0, Passed 480. Worker Failed 0, Passed 1231 (1197 + 23 + 6 + 5 classifier rows). App Failed 0, Passed 5584 (5557 + 27 net).
- **Evidence (git 2.55.0.windows.3, temporary repositories):** `git worktree remove` without `--force` exits 0 and deletes: ignored files; a repository nested in an ignored folder; a clean worktree with `CHERRY_PICK_HEAD`, `MERGE_HEAD` or an open `rebase-merge`; a worktree on a detached commit no ref holds; a worktree whose folder is already gone. It refuses (exit 128, still registered): untracked or modified files, an untracked nested repository, a lock, a submodule. `WorktreeRemovalSafetyTests` restates this, so a different git shows as a failing test.
- **Done-when of section 10.3:** every scenario of section 5.4 has a test (list in the inventory rows R01 to R17; the App scenarios are in `RemoveFeatureSafeFirstTests`, the real-git ones in `WorktreeRemovalSafetyTests`); the real-git tests pass on Windows; `Forty_repositories_with_two_blocked_sources_report_37_removed_2_blocked_and_the_root_waiting` passes.
- **Decisions:**
  - Ignored files: no guard. No document promises to keep them, a .NET worktree always has `bin` / `obj`, and a guard would block almost every removal. Written down in `05` and `06`, held by `Ignored_files_go_with_the_worktree`.
  - Three Worker guards git does not have: nested repository (with or without force), commits only a detached HEAD holds (with or without force), unfinished merge / rebase / cherry-pick (without force). These also apply to every other caller of `RemoveGitWorktree` (Roll back, external worktree removal): that narrows them, never widens.
  - Refusal kinds that say work is at stake come from evidence codes; the classifier no longer derives `UncommittedChanges` from message text.
  - Exit 0 stays trusted. A lost reply is settled by one `ListGitWorktrees`; the removal is never sent twice.
  - No analysis in the removal and no whole-operation refusal. Consents are scoped: force only for repositories the reviewed plan listed as changed and only at the reviewed commit; unlock only for the ones listed as locked; forced branch delete only for the ones listed as not merged and leased on the reviewed tip. A consent without reviewed details authorizes nothing (this is narrower than before, where `AllowUnlockWorktrees` and `AllowForceDeleteLocalBranches` reached every repository).
  - Branch deletion without reviewed details: local only non-force, with one pull request refresh for exactly the repositories whose branch git kept, so a squash-merged branch is still deleted; remote only together with a local branch deleted as merged, leased on the tip the local delete read. Reason: without this the default path would either keep every squash-merged branch or delete remote copies nobody reviewed.
  - Checkbox defaults of the unreviewed dialog: local on when a repository has a Feature branch, remote on when GrayMoon recorded a push. Same defaults as the reviewed dialog; safe because of the rule above.
  - Durable outcome: one nullable column `RemovalOutcomeJson` (the row's step outcomes, the branch tip and upstream) instead of a column per step; strict migration step 23.
  - A Feature is finished when every worktree is removed and no requested branch delete is `Failed` / `Unknown`. A branch kept for a reason is resolved. A failed branch delete keeps the Feature (`NeedsRepair`) so it can be retried alone; before, the Feature was deleted and the failure was only in the toast.
  - The result of an incomplete removal carries the report of the removed repositories and the typed outcome; `OperationResult.RemoveFeatureReport` is no longer only filled on success (additive).
  - Feature Status shows the per-repository reason through the existing `LastError` column; no new panel. The same text is in the dialog's "Left from the last removal" list.
  - Dialog: one dialog, two states (stored facts, then live details after "Review details"), not two removal modes. The Remove button is red in the first state: green is reserved for live details that are safe.
  - `WorkspaceFeatureOperations.cs` was not split by moves; the removal was rewritten into its own partial file instead, and the old code deleted.
  - `WorkspaceHookContextAttributor` no longer attributes a path to a `Removed` row (a late event from a removed worktree would have written Feature state while the removal is incomplete).
- **Rule differences:** none.
- **Open points:** none in code. For the owner (section 10.7): look at the Remove Feature dialog in both states and at Feature Status after an incomplete removal; the `InspectWorktree` row of section 2.3 now only applies to "Review details".

## S1 - evidence and generations

- **Status:** done (2026-11-26).
- **Base:** the tree after R2.
- **Changed files:**
  - Abstractions: `Notifications/RepositoryStateSnapshot.cs` (section 10.3 file: `CaptureSession`, `CaptureStart`, `CaptureEnd`), `Notifications/RepositoryStateCapture.cs` (new: `RepositoryStateCaptureClock`).
  - Worker: `Services/RepositoryStateProbe.cs`, `Commands/RefsHookSyncCommand.cs` (stamp the capture).
  - App: `Services/Workspaces/RepositoryStateFreshness.cs` (rewritten: `RepositoryFactGroups`, `RepositoryStateFreshnessVerdict`, `Evaluate`, `RecordApplied`, `ForgetContext`, evidence shared per Workspace for branch lists), `WorkspaceRepositoryStateWriter.cs` (`RepositoryStateWriteResult`, changed-fact detection, `RecordApplied`), `RepositoryBranchWriter.cs` (`PersistAsync` returns whether rows changed), `RepositoryStateReconciler.cs` (new), `Services/Worker/SyncCommandHandler.cs`, `ISyncNotificationHandler.cs` (`AppliedSync.Changed`), `Services/Features/WorkspaceFeatureOperations.cs` (optional `RepositoryStateFreshness`, `ForgetContext` at finalization), `GrayMoonServiceRegistration.cs` (section 10.5 file: `RepositoryStateReconciler` singleton, its interface and hosted service).
  - Tests, new: `App.Tests/RepositoryStateEvidenceTests.cs` (17), `Worker.Tests/RepositoryStateCaptureStampTests.cs` (2).
  - Documents: `AGENTS.md` (Feature-context scoping rule 9), `docs/architecture/04` ("Stale hook snapshots"), `06` (section 8); `GrayMoon.Desktop/README.md` (new top bullet).
- **Tests (2026-11-26, whole suites including `Category=Long`):** build 0 errors. Common Failed 0, Passed 480. Worker Failed 0, Passed 1233. App Failed 0, Passed 5601.
- **Done-when of section 10.3:** stale (`StaleHookSnapshotTests`, unchanged and green), captured-before/received-after (`A_hook_snapshot_captured_first_and_delivered_last_does_not_put_the_older_commit_back`), sibling worktrees sharing refs (`An_older_branch_list_from_the_main_checkout_does_not_remove_a_branch_a_Feature_worktree_reported_later`), deleted context (`A_late_notification_from_a_deleted_Feature_writes_nothing_and_brings_nothing_back`). The tests choose the order and the capture stamps themselves; none waits or sleeps.
- **Decisions:**
  - Capture evidence is an interval of a per-process counter (start before the first read, end after the last), plus a session id per Worker process. An interval, not a single number, so that two reads that overlapped are recognized as not orderable instead of being ordered wrongly. No wall clock is compared between processes.
  - Stamped: the state probe (every hook sync and every command that reports state through it) and the `reference-transaction` hook's branch-lists snapshot. Not stamped: the post-push and post-undo snapshots the Worker builds itself; they follow an App operation and are ordered by arrival, as before.
  - A snapshot of another session or without stamps is never dropped on that ground: it falls back to the existing rule.
  - Overlapping captures: apply (the snapshot may be the newer one) and queue one read of that repository. The read is the existing single-repository refresh, not a new Worker command; it runs in a hosted service with a bounded, keyed queue (`RepositoryStateReconciler`), one read at a time. G1 can route "evidence was lost" through the same interface.
  - Branch lists and tags are ordered per Workspace and repository across contexts (they are the same rows). Checkout groups stay per context.
  - The writer's result converts to bool, so the callers that only ask "was it written" did not change. A probed project scan counts as changed: the project reconcile does not report whether rows differed (S2 can refine this if it needs to).
  - `AppliedSync.Changed` is filled; nothing reads it yet. S2 is the consumer.
  - `_stamps` had no removal path (a bounded-collection finding). `ForgetContext` is called when a Feature is finished.
  - The R1 note about the writer creating a special-Workspace state row as `NeedsSync` was looked at again: still no path that reaches it with an `InSync` link, still unchanged.
- **Rule differences:** none.
- **Open points:** none.

## S2 - scoped invalidation and recompute

- **Status:** done (2026-11-26).
- **Base:** the tree after S1.
- **Changed files:**
  - App: `Services/Worker/SyncCommandHandler.cs` (`CompleteAsync` decides from `AppliedSync.Changed`), `Services/Workspaces/WorkspaceStateRecomputeScope.cs` (`RecomputeInputs`, `NeedsRecompute`, `RecomputeAsync` and `ResolveEffectiveVersionsAsync` return whether they completed), `SyncRecomputeCoalescer.cs` (`MarkOwed`, `TakeOwed`), `WorkspaceRepositoryStateWriter.cs` (a project scan is compared before and after the reconcile), `Repositories/WorkspaceProjectRepository.Reconcile.cs` (`ReadProjectFingerprintAsync`).
  - Tests, new: `App.Tests/ScopedRecomputeTests.cs` (15).
  - Documents: `CLAUDE.md`, `docs/architecture/04` ("Hook sync batches"), `06` (section 9); `GrayMoon.Desktop/README.md` (new top bullet). `WorkerSyncNotificationQueue.cs` needed no change: it already hands the batch to the handler.
- **Tests (2026-11-26, whole suites including `Category=Long`):** build 0 errors. Common Failed 0, Passed 480. Worker Failed 0, Passed 1233. App Failed 0, Passed 5616.
- **Proof against the old behavior:** with `needed` forced to true (the old rule) 4 of the 15 tests fail (confirming notification, counts-only, branch created outside, owed-once); the file was restored afterwards.
- **Done-when of section 10.3:** equivalence for both profiles (`After_any_sequence_of_notifications_the_full_recompute_changes_nothing`, .NET Dependency and Basic, 11 notifications each, compared after every one); counts for a source edit that is committed (`A_commit_that_changes_the_version_recomputes_its_consumers`, `An_edited_project_file_recomputes_although_commit_and_version_are_the_same`), a no-op notification and a refs-only change.
- **Decisions:**
  - The closure is decided per batch and is all or nothing: when an input changed, the full context recompute runs. A per-repository closure (only the consumers of the changed package) is not built: the three parts rebuild from context-wide snapshots and no test proves a narrower rebuild equal. Section 4.4 asks for the conservative fallback in that case. V1 narrows the file-version check.
  - "Did the recompute run" is measured in tests by overwriting a derived value with one no recompute writes, not by counting Worker commands: without configured version files the recompute sends none.
  - Projects: the writer now compares the repository's project rows and edges before and after the reconcile (two reads each). Only on the single-repository path the hook uses; the batch path (operations, which complete with their own full recompute) still reports a probed scan as changed.
  - Broadcasts are unchanged. A notification that changed nothing is still announced, because the pull request reconcile and the context sync flags in the same apply are not part of the reported change. U1 decides whether the grid can skip it.
  - A recompute that threw, or whose effective-version or file-version part failed, marks the context owed; the next completion of that context runs it whatever changed. Before, the next event of any kind repaired it by accident; that accident is now explicit.
  - Known consequence, documented in 04: registry answers are time-dependent and are no longer re-asked by unrelated Git events.
- **Rule differences:** none.
- **Open points:** none.

## G1 - monitoring and recovery

- **Status:** done (2026-11-26).
- **Base:** the tree after S2.
- **Changed files:**
  - App: `Services/Workspaces/RepositoryStateDriftMonitor.cs` (new: `IRepositoryStateDriftMonitor`, `RepositoryCheckoutObservation`), `Services/GitChanges/GitChangesSnapshotPushHandler.cs` (reports each snapshot that is not older than the stored one), `Services/Features/WorkspaceFeatureOperations.cs` (forgets a finished Feature's entries), `GrayMoonServiceRegistration.cs` (section 10.5 file: one singleton).
  - Tests: `App.Tests/RepositoryStateDriftMonitorTests.cs` (new, 18), `GitChangesPushHandlerTestFactory.cs` (optional monitor).
  - Documents: `AGENTS.md` (Feature-context scoping rule 10), `docs/architecture/04` ("A lost hook: the drift check", Known gaps), `06` (section 8); `GrayMoon.Desktop/README.md` (new top bullet).
- **Tests (2026-11-26, whole suites including `Category=Long`):** build 0 errors. Common Failed 0, Passed 480. Worker Failed 0, Passed 1233. App Failed 0, Passed 5634.
- **Trace result:**
  - Lost events are real, not theoretical: every hook sync command of the Worker logs "Hub not connected, cannot send ..." and drops the notification; the App keeps nothing across a restart.
  - Before G1 nothing compared the stored commit and branch with the checkout outside an explicit Sync and the Create check of R1. The Git Changes audit repaired the status projection only.
  - `WorkspaceFeatureReconciler` (F12) already treats a failed list as unknown, skips busy Workspaces before and after its Worker calls, and leaves queued removals and `Removing` / `Removed` rows alone. `FeatureFolderCleanupService` (F13) already works from marked folders under a managed root. Both retained without a code change.
  - Watcher renewal (`GitChangesWatcherKeeper`), the offline notice of the Git Changes page and the audit's skip conditions were read and retained.
- **Done-when of section 10.3 (recovery scenarios of 9.1, including "no later event"):** `A_missed_commit_is_found_from_one_status_scan_although_no_other_event_follows` (one observation, then only the clock moves); external branch switch; hook within the grace period (no duplicate work); busy context; deleted context; `After_the_requested_read_the_stored_state_is_the_checkout_and_the_next_scan_agrees` (projection matches the checkout after recovery, one `RefreshRepositoryVersion`); the handler reports also a duplicate snapshot version, which is what an audit of an unchanged repository produces. Worker restart is covered by the capture session of S1; watcher loss, overflow, idle expiration, pause and offline skip are covered by the existing `GitChangesAuditSchedulerTests` and `GitChangesWatcherKeeperTests`, which were not changed.
- **Decisions:**
  - No new scan and no new Worker command: the audit already reads commit and branch of every repository of an active context. The monitor only compares.
  - The comparison waits 30 s (`Grace`) because a status scan is normally faster than the hook for the same change; an immediate comparison would add a read to every commit. The latest observation is the one compared.
  - While the Workspace is under a structural operation or the context under a mutation, the comparison waits: the operation reports its own result (and Pull runs with hooks off).
  - Only two known, different values are drift. An unknown stored commit is not repaired here (the read might not store one either, which would loop).
  - Loop guard: the same observation asks again only after 10 minutes.
  - Maximum recovery window, stated in doc 04: audit interval (configurable, 5 min) + audit run time + 30 s for an open Workspace; at activation for one that is not open. Inactive Workspaces are not audited in the background: that is the existing, documented presence rule and was kept.
  - Not covered, recorded in doc 04: a ref-only change whose `reference-transaction` hook was lost, and remote state. A fingerprint of the refs would need a new Worker read per audit and repository; not built without a measurement that the loss happens.
- **Rule differences:** none.
- **Open points:** none.

## V1 - version and dependency engine

- **Status:** done (2026-11-26).
- **Base:** the tree after G1.
- **Changed files:**
  - App: `Services/Versions/EffectiveVersionResolver.cs` (`EvidenceKeyOf`, key compared by `StoredProbeDecision`, `StoredSnapshotDecision` and `StoredParentVersion`, key on `EffectiveVersionDecision`), `Services/Workspaces/WorkspaceRepositoryStateWriter.cs` (`EffectiveVersionWrite.EvidenceKey`), `Models/Features/WorkspaceRepositoryContextState.cs` and `Data/AppDbContext.Features.cs` (section 10.5 file: one nullable column), `Migrations.cs` (section 10.5 file: step 24 and the ahead-of-steps call), `Migrations.EffectiveVersionEvidenceKey.cs` (new), `Services/Application/WorkspacePushOperations.cs` (pending-sync barrier before the plan).
  - Tests: `App.Tests/EffectiveVersionEvidenceKeyTests.cs` (new, 8), `EffectiveVersionStartCheckTests.cs` (one new test and its barrier double), `FeatureRemovalOutcomeMigrationTests.cs` (the assertion "23 is the last step" removed: it was mine from R2 and wrong by construction).
  - Documents: `docs/architecture/03` (Effective versions), `05` (Effective versions), `06` (section 9); `GrayMoon.Desktop/README.md` (new top bullet).
- **Tests (2026-11-26, whole suites including `Category=Long`):** build 0 errors. Common Failed 0, Passed 480. Worker Failed 0, Passed 1233. App Failed 0, Passed 5643.
- **Proof against the old behavior:** with the key comparison switched off, 4 of the 8 evidence key tests fail (new required package, other registry, other parent, row without key); the file was restored. The barrier test fails without the wait by construction (the plan completes before the gate opens).
- **Trace result (W14 first, as section 10.3 requires):** the package wait is already scoped to the exact ids and versions the next level requires, asks only for what is not found yet, treats a transient registry error as "not yet", and reports producers and workflow runs at the timeout. No change. A failed CI run does not end the wait early; that is a product choice (a re-run can still publish) and is listed for the owner in the verification report.
- **Done-when of section 10.3:** W14 traced first (above); dependency scenarios of 9.1: no-op Feature version causes no push (`Bulk_push_plan_of_a_fresh_resolvable_context_is_empty_and_says_why`, existing and green), version / parent / package mapping changes invalidate the decision (new tests), registry unavailable / missing / exact version (`EffectiveVersionResolverTests`, existing), two-lane Update and Push (`UpdateAndPushOrchestratorPipelineTests`, existing), generated edges and commit tokens (`EffectiveVersionDependencyTests`, existing), incremental equals full (`ScopedRecomputeTests`, S2).
- **Decisions:**
  - Evidence key = parent branch + branch point (first half), required packages with their registry mapping (second half). The commit stays in its own column. Availability is not part of the key.
  - A row without a key (stored before step 24) is not reused as a decision, so it is checked once; its recorded parent version is still used, so an upgrade costs registry lookups (cached briefly), not one GitVersion run per repository in the hook queue.
  - The column is also added ahead of the strict steps, like the other effective version columns, because step 16 inserts state rows through the EF model. `The_runner_adds_the_column_before_an_older_step_writes_context_state_rows_through_the_model` upgrades a version 15 database.
  - Bulk Push waits for pending hook syncs before it builds its plan. Found while tracing "barriers before Update / Push / Prepare": Update had the barrier (Prepare and Update and Push run the same update), bulk Push did not. Nothing is announced when nothing is pending.
  - The file-version check is not narrowed to changed files: see inventory row W24 for the reason. S2 already stops it from running when no input changed.
  - Push of one named repository still runs no start check, as documented.
- **Rule differences:** none.
- **Open points:** none.

## F1 - creation, repair, rollback

- **Status:** done (2026-11-26).
- **Base:** the tree after V1.
- **Changed files:**
  - App: `Services/Features/FeatureVersionStage.cs` (new), `WorkspaceFeatureOperations.cs` (Create starts the stage instead of awaiting the version step; Repair pauses monitoring and requests one audit), `Services/Versions/EffectiveVersionStartCheck.cs` (waits for the finalization barrier), `GrayMoonServiceRegistration.cs` (section 10.5 file: one singleton).
  - Tests: `App.Tests/CreateFeatureVersionStageTests.cs` (new, 6), `CreateFeatureMonitoringPauseTests.cs` (two new tests for Repair).
  - Documents: `CLAUDE.md`, `docs/architecture/04` (section 5C); `GrayMoon.Desktop/README.md` (new top bullet).
- **Tests (2026-11-26, whole suites including `Category=Long`):** build 0 errors. Common Failed 0, Passed 480. Worker Failed 0, Passed 1233. App Failed 0, Passed 5651.
- **Done-when of section 10.3:** "a Feature is usable when its worktrees are ready and the gated actions wait for version readiness (test)": `Create_ends_with_a_Ready_Feature_while_the_version_stage_still_holds_the_barrier_and_the_Checkout_syncs` and `An_operation_that_depends_on_versions_waits_for_the_version_stage_in_its_start_check`. Create scenarios of 9.1 (failure before and after intent, detached / reference / pinned, branch collision, incomplete checkout) are covered by the existing Create tests, all green and unchanged.
- **Decisions:**
  - The version stage moved behind the end of Create. Safety argument, each part tested: (1) the stage holds the finalization barrier, armed before `Start` returns, so hook syncs of the context are set aside exactly as long as before; (2) the stage, not Create, releases the hold on the Checkout syncs, so their GitVersion runs still follow the version stage; (3) every operation that decides from versions runs `EffectiveVersionStartCheck`, which now waits for the barrier and then resolves with fresh registry answers. Push of one named repository runs no start check, by existing design, and does not depend on effective versions.
  - No new "pending" marker in the grid: the version column of a new Feature is empty until the stage has ended, which is what the grid already shows for a repository whose own GitVersion is pending. One `ContextSynced` at the end reloads open pages. A marker is a U1 topic if the owner wants one (listed in the verification report).
  - `FeatureVersionStage` is an optional dependency of `WorkspaceFeatureOperations`. Production registers it. A host without it resolves inside Create as before; that is what the roughly twenty existing Create tests run, which read Worker calls and state right after Create returns and would otherwise depend on a background task. The background path has its own tests with the stage registered. This is a second code path kept for determinism of existing tests, recorded here because the owner prefers designs without fallbacks.
  - Background work without the job's cancellation token: the stage has its own 10 minute limit (the Worker's hold ends then anyway) and stops with the App.
  - Repair: monitoring pause and one audit, only when a worktree is retried. The hold on Checkout syncs was not added to Repair: Repair has no version stage for those syncs to compete with.
  - Rollback kept as it is (inventory F11): making it partial needs consent rules the document does not delegate.
  - HeadSnapshot and the worktree checkout stage were not changed: section 6.1 asks for measurements first, and none can be taken here (VDI). Listed for the owner.
- **Rule differences:** none.
- **Open points:** none.

## O1 - sync, branch, commit, pull, push, undo, Return to Default

- **Status:** done (2026-11-26).
- **Base:** the tree after F1.
- **Changed files:**
  - Worker: `Services/GitService.cs` (`ResetToRemoteAsync` refuses unless the named branch is checked out), `Commands/UndoPushCommand.cs` (optional `ICsProjFileService`; project scan after a discarding undo).
  - Tests: `Worker.Tests/GitServiceUndoUnpushedCommitsTests.cs` (theory of two and one fact, new), `PushUndoReturnCapabilitiesTests.cs` (theory of three, new).
  - Documents: `docs/architecture/05` (Undo Push); `GrayMoon.Desktop/README.md` (new top bullet).
- **Tests (2026-11-26, whole suites including `Category=Long`):** build 0 errors. Common Failed 0, Passed 480. Worker Failed 0, Passed 1239. App Failed 0, Passed 5651.
- **Follow-up rows traced first (section 10.3):**
  - **W10 Undo Push.** Path: page -> `IWorkspaceSyncOperations.UndoPushAsync` -> `WorkspaceUndoPushHandler` (policy, outgoing > 0, stored branch) -> Worker `UndoPush` -> `GitService.ResetToRemoteAsync` (`git reset --mixed` or `--hard` to `origin/<branch>`, or to the merge base for a branch that was never pushed) -> post-reset `SyncCommand`. Invariant: only the exact target is rewritten. Finding: the target ref was built from the stored branch name and never compared with what is checked out. Confirmed with real git before the fix (three failing cases: the other branch was moved to `origin/main`, with "discard" its commit and files were gone; a detached HEAD was reset too). Fixed by refusing.
  - **W16 Git Changes.** Path, invariant and current work are in the inventory row. No finding; nothing changed.
- **Decisions:**
  - The Undo guard is in `ResetToRemoteAsync`, so any future caller has it. It refuses and names both branches; it does not "correct" the target.
  - Project scan after a discarding undo: a reset fires no hook, and `--hard` changes project files. Without it the stored projects were those of the undone commits until the next Sync. After "keep changes" the files do not change, so nothing is scanned.
  - Return to Default: no change. A forced local delete for a merged or closed pull request without the user's tick is an owner decision recorded in the code; it is listed in the verification report because a closed, unmerged pull request then deletes unmerged commits.
  - Pull, explicit Sync, the single-repository refresh, branch catalog, checkout and branch creation: retained, each with its reason in the inventory. No preflight was removed in this package.
- **Rule differences:** none.
- **Open points:** none.

## M1 - manifest, attach, import, restore, detach, roles, pins, profile

- **Status:** done (2026-11-26).
- **Base:** the tree after O1.
- **Changed files:**
  - App: `Services/Worker/SyncCommandHandler.cs` (a changed profile makes the completion recompute), `Services/Workspaces/SyncRecomputeCoalescer.cs` (`ObserveProfile`, `Forget`), `RepositoryStateFreshness.cs` (`ForgetWorkspace`), `Services/Application/WorkspaceRepositoryOperations.cs` and `.Removal.cs` (Remove Workspace drops what is remembered for its contexts), `Services/Features/WorkspaceFeatureOperations.cs` (a finished Feature drops its recompute marks).
  - Tests: `App.Tests/ScopedRecomputeTests.cs` (two new), `RepositoryStateEvidenceTests.cs` (one new).
  - Documents: `docs/architecture/04` ("When the recompute runs"); `GrayMoon.Desktop/README.md` (new top bullet).
- **Tests (2026-11-26, whole suites including `Category=Long`):** build 0 errors. Common Failed 0, Passed 480. Worker Failed 0, Passed 1239. App Failed 0, Passed 5654.
- **Follow-up rows traced first (section 10.3):** W19 import / attach, W20 remove / unlink / disable, W21 restore, W26 profile. Path, invariant and current work of each are in its inventory row. Findings:
  - W26: a profile change writes no repository state, so after S2 nothing recomputed the derived state under the new profile until a repository really changed. Added a trigger at the place that decides (the completion of a hook batch), so it covers both callers of `WorkspaceProfileTransition` and any later one.
  - W20: the in-memory records added in S1, S2 and G1 had no removal path for a removed Workspace. Added.
  - W19, W21: no finding. These flows already plan again before they write, report per repository and roll back their own rows; nothing was changed.
- **Decisions:**
  - The profile is compared where the recompute is decided, not announced by the code that changes it: there are two writers today, and a missed announcement would be silent.
  - The first profile seen for a context after an App start is not a change: there is nothing to compare with, and the stored derived state was computed by the previous run.
  - No check was removed in this package. Section 6.4 names these flows as identity- and filesystem-sensitive; every retained row says what protects it.
- **Rule differences:** none.
- **Open points:** none.

## U1 - PR, CI, Actions and the context pages

- **Status:** done (2026-11-26).
- **Base:** the tree after M1.
- **Changed files:**
  - App: `Components/Pages/PageJobGuards.cs` (`PageContextBinding`), `WorkspaceActions.razor.cs` and `WorkspaceActions.Loading.cs`, `WorkspaceDependencies.razor`, `WorkspaceFiles.razor`, `WorkspacePackages.razor`, `Services/Features/WorkspaceContextNavigationService.cs` (`ResolveQueryChangeAsync`, `ResolveSelectionAsync`).
  - Tests: `App.Tests/PageContextBindingTests.cs` (new, 5), `ContextPageBindingDisciplineTests.cs` (new, 12 cases), `WorkspaceContextNavigationServiceTests.cs` (two theories, 7 cases), `RepositoryStateEvidenceTests.cs` (a nullable warning of S1 removed; the build is back at its 6 baseline warnings).
  - Documents: `docs/architecture/01`, `04` (section 4); `GrayMoon.Desktop/README.md` (new top bullet).
- **Tests (2026-11-26, whole suites including `Category=Long`):** build 0 errors. Common Failed 0, Passed 480. Worker Failed 0, Passed 1239. App Failed 0, Passed 5678.
- **Follow-up rows traced first (section 10.3):** W22 (PR), W23 (Actions), W25 (other pages); each row in the inventory. Finding, W23 and W25: Actions, Dependencies, Files and Packages resolved `?context=` once, in `OnInitializedAsync`. Browser back and forward changed the URL and not the data; two quick switches ran two loads on the page's scoped services at once and the slower one won; Files read the selected context at two different awaits of one load.
- **Done-when of section 10.3 ("context switch and back/forward tests for each page changed"):** the switch and back / forward rules are tested as logic (`PageContextBindingTests`: a Feature and the Workspace both ways, the selector's own navigation is not a second switch, an overtaken load is not current, loads never overlap and the last one sees the latest selection) and as navigation rules (`ResolveQueryChangeAsync`: a Feature id, no value, zero, another Workspace's context, a missing one). `ContextPageBindingDisciplineTests` then checks, per page changed, that its `OnParametersSetAsync`, its one switch method and its load use them. No component test renders the pages: the repository has no component test host, and this run may not start a browser. The four pages are in the owner's manual list.
- **Decisions:**
  - One small helper instead of copying the Repositories page's fields four times; Repositories, Git Changes and Projects keep their own working mechanism.
  - A URL without `?context=` that arrives while the page is open means the Workspace, never the remembered selection (the Repositories page's rule). The URL is not rewritten on a change the user made.
  - A selector entry that no longer belongs to the Workspace resolves to the Workspace instead of throwing out of the event handler (Dependencies logged and went on with a half-set layout context; Actions and Packages threw).
  - Loads are serialized per page and an overtaken one assigns nothing. It still runs to its end: cancelling it would need a token through every repository call for no visible gain.
  - Not done, recorded in inventory row B12: skipping the broadcast for a notification that changed nothing, and patching single rows on the other pages. Both need a browser to verify sorting, filtering and grouping.
  - PR and CI display: already stored display plus targeted live calls, with the provider's answer read back after every action; nothing changed.
- **Rule differences:** none.
- **Open points:** none.

## X1 - peripheral mutation boundaries

- **Status:** done (2026-11-26).
- **Base:** the tree after U1.
- **Changed files:**
  - Tests: `App.Tests/Conventions/ApiEndpointFacadeConventionTests.cs` (new, 2).
  - Documents: `docs/todo/reactive/check-inventory.md` (rows X01 to X07). No product code changed, so no architecture text and no README bullet.
- **Tests:** the convention test passes (2 of 2); the whole suites are run by Z1, which follows directly.
- **Trace result:** every mutating REST route is one facade call under the Workspace lock, for the special Workspace context, and returns the facade's result. No route reaches a handler, an orchestrator, the git service, the state writer or the Worker. The connector health gate is a stored flag, not a network call. The AI path produces text only. Nothing found that the trace would justify changing.
- **Decisions:**
  - Section 9.1 asks for a non-UI caller test of the transport-neutral entry contract when there is no MCP entry point. There is none in this repository (only the notification panel and REST). The contract is held by: the facades being the only way in (`ApiEndpointFacadeConventionTests`), the result type (`OperationResultContractTests`, C1), and the existing REST caller test (`UpdateAndPushEndpointTests`).
  - REST works on the special Workspace context only. Whether REST should take a Feature context is a product question and was not opened.
- **Rule differences:** none.
- **Open points:** none.

## Z1 - validation and cleanup

- **Status:** done (2026-11-26).
- **Base:** the tree after X1.
- **Changed files:**
  - Tests: `App.Tests/ReactiveSchemaUpgradeTests.cs` (new, 1).
  - Documents: `docs/todo/reactive/verification-report.md` (new), `check-inventory.md` (the last three rows without a decision, F02, B01 and B11, and the legend of the statuses).
- **Tests (2026-11-26, final run after the last change, no filter):** build 0 errors, 6 warnings (the baseline). Common Failed 0, Passed 480. Worker Failed 0, Passed 1239. App Failed 0, Passed 5681. Long tests on their own: Worker Passed 2, App Passed 2. Migration tests (47, including the upgrade from version 22 through the whole runner): all passed.
- **Self-review of section 9.3:** done on the whole diff; the result is section 3 of the verification report. No finding that needed a change.
- **Inventory:** 77 rows, every one with a decision: 54 retained, 9 extended, 7 narrowed, 4 replaced, 1 reduced, 1 background, 1 deferred.
- **Not possible in this run:** VDI measurements and any check in a running App or browser. Both are listed for the owner in the verification report, section 5.
- **Open points:** none. Everything stays uncommitted; no file is staged in any repository.
