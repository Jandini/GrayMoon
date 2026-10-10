# GrayMoon - Prepare Workspace Two-Lane Pipeline Integration

## Role and objective

You are a senior .NET 10 engineer implementing a narrowly scoped, safety-first change in GrayMoon. **Make Prepare Workspace reuse the existing two-lane `UpdateAndPushOrchestrator` whenever the user enables both Update Dependencies and Push Changes.** Do not write another pipeline, queue, scheduler, or package synchronization implementation.

**Critical rule: branch creation is an all-or-stop gate. If any repository intended for branch creation fails to create or check out the requested branch, abort the remainder of Prepare Workspace. Do not update dependencies, commit changes, build a push plan, start either pipeline lane, or push any repository.** Previously created branches may remain on disk; report partial branch-creation success and the failed repositories accurately. Do not attempt automatic rollback or claim atomic branch creation. Cancellation or unknown branch-creation outcome is also a stop condition.

This requirement applies to **all four Prepare Workspace option combinations**, including Push Only. It overrides any prior instruction to continue update/push for repositories with successful branch creation. A repository intentionally excluded *before* the operation, for example through Skip Repos on Tags, is not a branch failure; verify the intended selection semantics before implementing.

## Mandatory code review before edits

Inspect the current `main` branch and find the real interfaces, call sites, service registrations, and tests. Start with:

- `src/GrayMoon.App/Services/Orchestration/PrepareWorkspaceOrchestrator.cs`
- `src/GrayMoon.App/Services/Orchestration/UpdateAndPushOrchestrator.cs`
- `src/GrayMoon.App/Services/Orchestration/DependencyUpdateOrchestrator.cs`
- `src/GrayMoon.App/Services/Orchestration/PushOrchestrator.cs`
- `src/GrayMoon.App/Services/Application/WorkspacePreparationOperations.cs`
- `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.PrepareWorkspace.cs`
- `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Update.cs`
- `src/GrayMoon.App/Components/Modals/PrepareWorkspaceModels.cs`

Locate and inspect `IWorkspacePreparationOperations`, `WorkspaceBranchHandler.CreateBranchesAsync`, `WorkspacePushService`, `WorkspacePushOperations`, `UpdateAndPushResult`, `DependencyUpdateRunResult`, `DependencyLevelCompletion`, `TwoLaneProgress`, `CanRunPushLaneAsync`, `RunPushLaneAsync`, `BuildPushPlanAsync`, `ExecutePushCoreAsync`, `TryRunPipelinedUpdateAndPushAsync`, operation-locking code, and relevant tests.

Before changing code, briefly document the existing Prepare Workspace and Push Updated call paths, the result types, repository selection and tag behavior, branch success/failure signaling, dependency propagation, preflight/fallback behavior, and the smallest safe integration point. **Do not infer success from the absence of thrown exceptions:** `CreateBranchesAsync` currently returns per-repository errors; inspect exactly how to interpret them and any additional result/state checks needed.

## Confirmed architectural direction

The existing `UpdateAndPushOrchestrator` already runs an update lane (`DependencyUpdateOrchestrator`) and a push lane (`WorkspacePushService.RunPushLaneAsync`) linked by a channel of completed dependency levels. The push lane uses its own DI scope/DbContext, and may push Level 1 while higher levels are updating. `CanRunPushLaneAsync` performs a no-mutation preflight and `UpdateAndPushResult.NotPipelined` signals that the caller should use the established sequential fallback.

Currently, `PrepareWorkspaceOrchestrator` creates branches, then calls `DependencyUpdateOrchestrator.RunAsync`, and the Razor page later reloads data, builds a push plan, and invokes `ExecutePushCoreAsync`. This makes Prepare Workspace sequential. Integrate the **existing** two-lane orchestrator behind `IWorkspacePreparationOperations`, not directly from the Razor page.

Desired flow:

```text
Prepare Workspace
  -> determine intended eligible repositories/context
  -> create/check out all requested branches
  -> persist and verify branch state
  -> if ANY attempted branch fails or outcome is uncertain:
       report errors; STOP (no updates, commits, plans, or pushes)
  -> select mode:
       branches only           -> finish
       branches + update       -> existing dependency update
       branches + push         -> existing push workflow
       branches + update+push  -> existing UpdateAndPushOrchestrator
                                     | update/commit level N
                                     +----> push level N (overlap next updates)
```

Do not overlap branch creation with either lane. Do not add Prepare-specific worker queues or duplicate package checks.

## Branch creation: strict gate (highest priority)

Implement and test this exact policy:

1. Determine the set of repositories **actually targeted for branch creation** after the existing selection and Skip Repos on Tags rules. Preserve the current meaning of deliberate exclusions.
2. Execute branch creation using the existing `WorkspaceBranchHandler`, including its inline state synchronization and hook-suppression behavior.
3. Capture **all** per-repository branch errors. Also treat cancellation, exceptions, and any verified inconsistent or indeterminate branch state as failure; do not proceed optimistically.
4. If at least one targeted branch fails, immediately end the preparation flow. **No dependency update, auto-commit, push-plan construction, synchronized push, normal push, or two-lane startup may occur.** Do not continue for only the successful repositories.
5. Report the failed repositories and meaningful error messages in the current repository/level error UI. Show a clear summary such as: `Prepare Workspace stopped: branches could not be created in all selected repositories. No dependency updates or pushes were started.`
6. Preserve actual disk state: some branches may already have been created. Do not delete those branches, reset checkouts, silently retry, or imply that they were rolled back. Refresh UI/database state so the user can see what succeeded and what failed.
7. Do not return or display overall Success if the branch stage was partially successful. Use the smallest appropriate result-contract change to distinguish branch failure from update/push failure.
8. Once the gate passes, verify all intended repositories have the correct branch/context before mutations begin. Avoid adding redundant expensive Git commands if the existing reliable synchronized state suffices.
9. This gate applies regardless of whether Update Dependencies or Push Changes is checked. Branch-only preparation with partial failure must also report failure accurately.
10. If there are legitimately no targeted repositories, follow the established empty-selection behavior; never interpret an unexpected empty result as evidence that every requested branch succeeded.

**Concrete negative test:** Given three selected repositories, branch creation succeeds in A and B and fails in C. Assert: A/B branch creations remain; C error is visible; preparation returns unsuccessful; update orchestrator calls = 0; combined pipeline calls = 0; push-plan calls = 0; push service calls = 0. Repeat for all option combinations that could otherwise mutate later phases.

## Four option combinations

| Update Dependencies | Push Changes | Required behavior after successful branch gate |
|---|---|---|
| No | No | Create branches only |
| Yes | No | Existing dependency update/commit only |
| No | Yes | Existing push-only path |
| Yes | Yes | Reuse existing two-lane `UpdateAndPushOrchestrator` |

Respect workspace profile capability checks (including `_presentation.ShowDependencyUpdateActions`). Do not run dependency updates in Push Only or push in Update Only. Preserve current base branch, new branch name, commit message semantics, tag handling, Feature context, and supported repository selection.

## Repository scope and dependency correctness

Both existing update orchestration and two-lane orchestration may pass `repoIdsToUpdate: null`. **Do not assume this is safe for Prepare Workspace.** Trace exactly what `null` means, how selected/skipped/tagged repositories are treated after checkout, and how higher-level dependency propagation works. Ensure that after the successful branch gate:

- No repository outside the intended Prepare Workspace operation is unexpectedly updated or pushed.
- No repository on an unintended branch is mutated.
- Newly created branches are reflected in the state used for update and push decisions.
- Dependency updates use live per-level state; do not freeze a stale initial out-of-date list.
- A level with no new dependency edits may still need push handoff for pre-existing outgoing commits, matching current Push Updated semantics.

If a scope parameter is genuinely needed, add the smallest reusable backward-compatible extension to the existing orchestrator/services, with tests. Do not filter ad hoc inside the channel consumer.

## Preflight and safe fallback

Reuse `CanRunPushLaneAsync` and `UpdateAndPushResult.NotPipelined`. An unavailable pipeline is **not** successful completion. Fall back to the existing sequential update-then-push path only when the preflight guarantees no update/push mutations have started. Preserve all synchronized-push package registry mapping and availability checks, and the established confirmation UX for continuing with a non-synchronized push when relevant. Never silently bypass synchronization, duplicate updates/commits/pushes, or rerun a partially executed pipeline as a fallback.

Branch creation must pass the strict gate **before any fallback update or push**. Whether to check pipeline availability before or after branch creation depends on the real preflight dependencies; do not use stale branch state or change failure semantics to optimize this.

## Results, progress, error reporting, and cancellation

The existing `IWorkspacePreparationOperations.PrepareAsync` / `PrepareWorkspaceOrchestrator` returns `DependencyUpdateRunResult`, while the combined orchestration returns `UpdateAndPushResult`. Make the smallest coherent contract adjustment so callers can accurately distinguish branch-stage failure, update failure, push failure, not-pipelined fallback, cancellation, and partial completion. Avoid a generic workflow framework. Preserve the existing repository and level error callbacks, synced repository ID handling, and refresh behavior.

Use the existing `TwoLaneProgress` and combined overlay reporting for the pipelined mode. Keep branch creation progress separate and first. Preserve the current modal layout/checkboxes and existing `StartPageJob` and operation-lock lifecycle. Keep business orchestration inside the application facade/orchestrator, not Razor page code.

Maintain concurrent-lane thread safety; the push lane must retain its separate scope and DbContext. Respect cancellation, settle all started tasks, complete the channel, observe exceptions, and never leave an unobserved background push. Reflect completed work accurately after cancellation; no claimed rollback of Git mutations.

After partial branch failure, refresh the UI from a fresh scope and display the failure without accidentally clearing it as a successful update. If the existing page job API refreshes only on success, add the smallest focused refresh-on-failure/cancellation handling required.

## Implementation sequence for a weaker coding model

Complete each step, inspect the diff, compile, and only then proceed:

1. **Read and map:** inspect primary paths and tests. Write short findings, including how branch errors are returned and where the existing page chains pushes.
2. **Strict branch gate:** fix `PrepareWorkspaceOrchestrator` to stop on *any* branch failure before any update, commit, pipeline, or push; ensure correct result and user-visible errors. Add focused branch-failure tests first.
3. **Contract wiring:** minimally expose push intent and a sufficient combined result through `IWorkspacePreparationOperations` and `WorkspacePreparationOperations`. Recheck all callers and DI registrations.
4. **Reuse pipeline:** after successful branch gate, call the existing `UpdateAndPushOrchestrator` only for Update + Push. Preserve the other modes. Make no new lane implementation.
5. **Preflight/fallback:** reuse no-mutation `NotPipelined` and established sequential fallback/confirmation behavior, with no double mutations.
6. **UI integration:** adapt `WorkspaceRepositories.PrepareWorkspace.cs` to invoke one preparation workflow for the combined mode; remove only obsolete duplicate chaining. Preserve progress/error callbacks and state refresh.
7. **Tests and validation:** add targeted concurrency, branch-failure, repository-scope, fallback, and cancellation tests; build and run existing suites; inspect for regressions.

Do not refactor unrelated code or change Git/LibGit2Sharp implementation, NuGet registry logic, CI provider logic, background workers, database schema, modal styling, or feature architecture.

## Mandatory tests

Use deterministic synchronization (e.g. task completion sources) rather than sleeps to verify lane overlap.

### Branch gate

- All intended branch creations succeed: selected mode may proceed.
- One of several branch creations fails: **no subsequent update, commit, pipeline, plan, or push starts**; result is failure and partial branch state is visible.
- Multiple branch failures: all captured errors are reported, still no later mutation.
- Branch creation throws: no later operation starts.
- Branch creation is cancelled: no later operation starts and cancellation propagates.
- Branch state cannot be reliably verified: fail closed, no later operation starts.
- Repositories deliberately excluded by Skip Repos on Tags are not counted as failed branch creation; no unintended updates/pushes.
- Branch-only option with one failure is not reported as a successful Prepare Workspace.
- Repeat critical partial-failure checks for Update Only, Push Only, and Update + Push.

### Pipeline and fallback

- All four option combinations use the correct path.
- Level 1 push starts while Level 2 update remains in progress when pipeline is viable.
- Dependency and package availability ordering remains correct.
- Levels with no updated files but existing outgoing commits are handled consistently with Push Updated.
- `NotPipelined` performs no pipeline mutations and switches to safe sequential behavior.
- Missing registry mappings never cause an automatic unsafe push.
- No duplicate update, commit, or push across fallback transitions.
- Update-lane failure and push-lane failure are independently and accurately reported.
- Cancellation settles both lanes and does not leave detached tasks.
- Feature context, repository selection, tagged repo handling, and operation locks remain correct.
- Existing Push Updated and Level-Only Push Updated behavior remains unchanged.

### UI and regression

- Branch partial failure shows failed repo messages and a clear stopped summary, preserves already-created branch state, and refreshes appropriately.
- Successful combined Prepare Workspace reports both lanes and final outcomes accurately.
- The Prepare Workspace modal and other three modes behave as before.

## Build and validate

Follow `CLAUDE.md` and `AGENTS.md`: .NET 10, C# project conventions, `sealed` classes by default, Windows CRLF line endings, ASCII hyphens instead of Unicode dashes in source, existing facade boundaries, no blocking async calls, and no unrelated formatting changes.

```powershell
dotnet build GrayMoon.slnx
dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj
dotnet test src/GrayMoon.Common.Tests/GrayMoon.Common.Tests.csproj
dotnet test src/GrayMoon.Worker.Tests/GrayMoon.Worker.Tests.csproj
```

Do not claim tests ran successfully unless they actually did. Explain any environment blockers.

## Definition of done

- [ ] Every targeted branch is successfully created/checked out and its state synchronized before later phases.
- [ ] **Any branch creation failure stops the entire remaining workflow before updates, commits, push-plan construction, or pushes.**
- [ ] Partial branch creations are accurately reported and left intact; no implicit rollback.
- [ ] Prepare Workspace Update + Push reuses `UpdateAndPushOrchestrator` with no duplicate lanes.
- [ ] The other three mode combinations retain correct behavior.
- [ ] Repo scope, Feature context, tag exclusions, and dependency propagation remain correct.
- [ ] Preflight/fallback retains synchronization safety and avoids double mutations.
- [ ] Progress, result reporting, cancellation, error persistence, and refresh are correct.
- [ ] Existing Push Updated and Level-Only behavior is preserved.
- [ ] Focused tests and full build pass, or blockers are explicitly documented.

## Final report

Provide: root cause of old sequential execution; changed file paths and minimal implementation summary; exact enforcement point of the all-or-stop branch gate; how the existing pipeline was reused; safety/fallback decisions; tests actually run and outcomes; and any genuine unresolved risks. Review the final diff for duplicate orchestration, unintended repository scope, stale branch state, unobserved tasks, shared DbContexts, accidental sequential waits, and duplicated pushes.

**Final instruction:** Reuse the existing proven pipeline. **Partial branch creation is an unconditional stop condition for the remainder of Prepare Workspace.** Keep the implementation small, explicit, testable, and safe.
