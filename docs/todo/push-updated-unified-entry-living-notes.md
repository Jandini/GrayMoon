# Push Updated unification - living design notes and checklist

Spec: workspace `docs/todo/GrayMoon_Push_Updated_Implementation_Plan_2026-10-27_v2.md` and the bootstrap prompt next to it.
Branch: `push-updated-notification` (GrayMoon worktree). Baseline SHA: `eb320738a999486bfec9aeaab4d31a87c6f4f93e` (equals the reviewed SHA, so no drift; working tree clean at start).

Status: code review and P0 done. No production code changed. P1-P4 done (see Result section).

## Verified call graph (before)

| Surface | Path | Engine |
|---|---|---|
| Repositories full | `WorkspaceRepositories.Update.cs` `RunUpdateAndPushCoreAsync` (l.364) | `IWorkspaceUpdateOperations.UpdateAndPushAsync` -> `UpdateAndPushOrchestrator` (two lanes); if `Pipelined=false`, page-local `UpdateAsync(repoIdsToUpdate:null)` -> `if (!Success) return` -> `BuildPushPlanAsync` -> `ExecutePushCoreAsync(synchronized:true)`; `SynchronizedPushNotPossibleException` -> `ShowConfirm` -> normal push |
| Repositories Level Only | same file `RunLevelOnlyUpdateAndPushCoreAsync` (l.256) | same, `maxLevel: level` |
| Popup full | `WorkspaceActionNotificationPanel.razor` `StartUpdateAndPushJobAsync` (l.669) | `UpdateAsync(repoIdsToUpdate: prefiltered)` result DISCARDED -> `ExecutePushAsync` (own sync/parallel decision) |
| Popup Level Only | same file `StartLevelOnlyUpdateAndPushJobAsync` (l.755) | same with `maxLevel` and level-filtered prefilter |
| Prepare Workspace | `PrepareWorkspaceOrchestrator` (l.64-98) -> `PrepareWorkspacePhases.UpdateAndPushAsync` (restore=true, includeDeps=true) | two lanes; on `Pipelined=false` runs `UpdateDependenciesAsync`, returns `PushPending`; page (`WorkspaceRepositories.PrepareWorkspace.cs` l.114-144) then does `BuildPushPlanAsync` + `ExecutePushCoreAsync(synchronized:true)`; on `SynchronizedPushNotPossibleException` it only shows an error (no normal-push approval) |
| REST | `WorkspaceOperationsEndpoints.cs` `/update` (l.95), `/push` (l.123-132), `/prepare-workspace` (l.152, then `PushPendingAsync` l.190 when `PushPending`) | `/update` and `/push` are standalone; `/prepare-workspace` composes via `PrepareAsync` |
| Raw engine callers | `WorkspaceUpdateOperations.UpdateAndPushAsync`, `PrepareWorkspacePhases` | both resolve `UpdateAndPushOrchestrator` |

Note: the "UI-owned Update then Push chain" exists in THREE page-side places, not two: popup (x2), Repositories partial (x2), Prepare Workspace partial (post-pipeline push). The architecture guard (T27) must scan `Components/**` for all of them, including `.cs` partials.

## Findings re-check against current code

| ID | Still applies | Evidence |
|---|---|---|
| F01 two implementations | yes | popup never calls `UpdateAndPushAsync` (grep of call sites) |
| F02 ignores `Success=false` | yes (popup only) | popup l.693 and l.777 discard `DependencyUpdateRunResult`; page checks it at Update.cs l.294 and l.400 |
| F03 frozen prefilter | yes | popup l.680-688, l.764-772; page passes `repoIdsToUpdate: null` with explanatory comment |
| F04 link vs context data | yes | prefilter reads `WorkspaceRepository.GetByIdAsync(...).Repositories` counters, not context snapshots |
| F05 push-mode divergence | yes | popup `ExecutePushAsync` l.919-936 `ShouldShowSynchronizedPushModal`; page always `synchronized:true` first |
| F06 error propagation | yes | popup passes `(_, _) => { }` callbacks, `ShowRepoErrors` only |
| F07 outcome overwritten | yes for popup; also page sequential fallback in principle | `WorkspaceUpdateOperations.UpdateAsync` does `Begin("Update")`, `WorkspacePushOperations.PushAsync` does `Begin("Push")`, `Begin` replaces the entry. `UpdateAndPushAsync` facade uses `Begin("Update and Push")` once, which is fine for the pipelined case |
| F08 context per phase | yes | `ResolveOperationContextAsync` called separately in update and push phases (l.692/776, ExecutePushAsync l.910) |
| F09 job key | yes | popup `WorkspaceJobKey` = `/workspaces/{id}` (l.311); page uses `PageJobKey` (context-aware). Lock semantics not re-verified yet - verify in P3 before touching keys |
| F10 restore/commit options | yes | popup `commitMessage:null`, `includeDeps:true`, restore omitted -> `PushAsync` default true; Repositories modal default off; Prepare true |
| F11 default-branch warning | yes | popup builds from `n.Repos` filtered `UnmatchedDeps > 0`; list capped by `WorkspacePendingActionsService.MaxListedActionRepos = 100` (l.30) |
| F12 dismissal | yes | `PendingActionsService.Dismiss` called before `StartJob` |
| F13 Update Only prefilter | yes | `StartUpdateJobAsync` l.614-636 same prefilter. Out of scope unless tests show it; record as follow-up |

## Design decisions (proposed)

1. `IWorkspaceUpdateAndPushOperations.ExecuteAsync(UpdateAndPushCommand, ...)` in `GrayMoon.Application`, implementation in `GrayMoon.App/Services/Application/` as a sealed scoped class. Command variants: `StartUpdateAndPush` and `ApproveNormalPushContinuation` (same entry).
2. The coordinator composes existing pieces only: `UpdateAndPushOrchestrator.RunAsync` first; on `Pipelined=false` the existing `IWorkspaceUpdateOperations.UpdateAsync(null set)` -> fresh `IWorkspacePushOperations.GetPlanAsync` -> `PushAsync(synchronized:true)`. No change to orchestrator, lanes, Worker, package wait.
3. Outcome: coordinator calls `outcomes.Begin(..., "Update and Push")` once and passes a recorder-aware path so nested Update/Push facades do not call `Begin` again. Narrowest option: add optional `WorkspaceOperationOutcomeStore.Recorder? recorder` parameter (default null = current behavior) to the two facade methods, or call the underlying handlers directly from the coordinator. To decide in P2 after reading `WorkspaceUpdateHandler` / `WorkspacePushHandler` signatures.
4. Pending normal-push approval: in-memory, singleton store keyed by `(WorkspaceId, ContextId)` holding RunId, PendingApprovalId, repo ids, required package ids, options, synced repo ids, created time. Consumed once, expires, revalidated against a fresh plan at continuation. Needs a singleton because `ExecuteAsync` is scoped per call.
5. Options are explicit in the command; UI keeps its own defaults (Repositories: restore off; popup: restore on, includeDeps on, no message; Prepare: restore on).
6. Job runner: keep `BackgroundJobService.StartJob`; the job body only calls `ExecuteAsync` with a command built before the job starts (context captured once).

## Open questions to settle before P1

- Where the popup gets the selected context from today (`ResolveOperationContextAsync` is defined outside the files read; locate it and confirm NeedsRepair/deleted behavior).
- Whether the default-branch preflight should be a new read-only `PreviewAsync` on the new interface or reuse `GetUpdatePlanAsync` (page already uses `GetUpdatePlanAsync` for Level Only, l.205).
- Prepare Workspace: scope of P5 migration (PushPending path has no normal-push approval today; moving it onto the coordinator would ADD that approval - needs a product decision).

## Checklist

- [x] Baseline SHA recorded; working tree clean (only untracked `docs/` at workspace root).
- [x] Call sites inventoried; F01-F13 rechecked.
- [x] Baseline `GrayMoon.App.Tests`: 1696 passed, 0 failed, 0 skipped. Common and Worker projects not run (untouched so far).
- [x] P0 done (partial by design, see P0 result).
- [ ] P1 contract and DI.
- [ ] P2 coordinator.
- [ ] P3 popup migration.
- [ ] P4 Repositories migration.
- [ ] P5 Prepare and REST audit, architecture guard.
- [ ] P6 diagnostics, docs (`docs/architecture`), Desktop README bullet, full test run.

## P0 result

- Added `UpdateAndPushEntryPointArchitectureTests` (T27 ratchet): UI files that chain update and push must equal `KnownViolations` (popup, Update.cs, PrepareWorkspace.cs). Green now; each phase removes its file; end state is an empty list.
- Added `Begin_for_a_following_push_replaces_an_earlier_update_failure` (F07/T25 current behavior).
- Already covered: `NotPipelined` has no side effects (`WorkspacePushStrategyTests.Update_and_push_falls_back_without_touching_anything...`), failed update does not chain push (`DependencyUpdateRunResultTests`).
- Deferred to P2 (need the coordinator seams; the popup is a Razor component and cannot be unit tested): T03 evolving 3-level dependency, T04 returned `Success=false` gives zero push, T09/T10 approval, T13 Feature context pinning. No red tests were added.
- Focused run: 6 passed, 0 failed.

## P1-P4 result

- Contract `IWorkspaceUpdateAndPushOperations` and coordinator `WorkspaceUpdateAndPushOperations` added; popup and Repositories page migrated; page-local pipelined/sequential chain and `RestoreSyncedPackagesCoreAsync` removed.
- Architecture test `KnownViolations` now holds only `WorkspaceRepositories.PrepareWorkspace.cs` (P5, Q2 open).
- Open: execution-time re-check of default-branch targets, engine-level T03 test, popup/page manual smoke tests.

## Follow-up result (risks closed)

- Default branch: `StartUpdateAndPush.ReviewedDefaultBranchRepoIds` plus `GetDefaultBranchReviewAsync`. The re-check is one read of the stored link snapshots; the update plan is only read when a repository sits on its default branch. No Worker call, nothing per level. A change in the middle of a run is not covered (would need a per-level check inside the update lane).
- Popup/page logic: `UpdateAndPushResultPresenter` (unit tested) decides toast or approval prompt for both surfaces. Razor event wiring itself is still checked only by manual smoke tests.
- Job locks: popup uses the whole-workspace key, a Feature page the context key; the runner refuses each while the other is held. `StartJob` silently starts nothing while the lock is held, so both approval callbacks now wait (in memory, up to 10 s) before starting.
- Three-level chain: NOT unit tested. `UpdateAndPushOrchestrator` takes the concrete `DependencyUpdateOrchestrator` and `WorkspacePushService`, so a fake-lane test needs interfaces extracted from the engine (behavior neutral, but it touches the engine). Decision pending. Manual scenario: three repositories A (level 1) -> B (level 2, consumes A's package) -> C (level 3, consumes B's package), each with an out-of-date reference. Run Push Updated from the popup and from the page. Expect: B is updated to the version A just published, C to the version B just published, pushes in order 1, 2, 3, one outcome per run, no second update after the push. Then repeat with the synchronized push refused (remove a NuGet mapping): expect the prompt, nothing pushed until Continue.
- Open: Prepare Workspace (P5, question Q2). REST: no combined endpoint exists; /update, /push, /prepare-workspace each take the whole-workspace lock themselves.
