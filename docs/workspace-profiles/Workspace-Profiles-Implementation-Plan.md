# Workspace Profiles - Implementation Plan

Living execution document. `Workspace-Profiles-Design.md` defines the architecture; this file tracks
state. Both are read before starting a unit.

It must be possible to stop work here and resume later without reconstructing state from chat history.

**Statuses:** `TODO` `READY` `IN PROGRESS` `BLOCKED` `REVIEW` `DONE`

---

## Overall status

| | |
|---|---|
| Project status | IN PROGRESS |
| Current phase | Wave 1 (Units C, E) integrated; Wave 2 (Units D, F, G, J) in progress in parallel |
| Phases planned in detail | 1, 2, 3; Unit E of 5 |
| Phases not yet designed | 4, 5 (Units F, G, H), 6 |
| Last verified | 2026-10-06, after Wave 1 integration. Build clean / 0 warnings. App 904/904, Worker 312 + 1 pre-existing skip, Common 234/234. |

**What works today.** A Workspace carries three independent persisted axes, every pre-profile Workspace
was migrated to the .NET triple, and the Worker honours the profile on every entry point it owns: a
Basic + None workspace does a full, correct Git sync with zero GitVersion launches, zero
`dotnet tool restore` and zero `.csproj` scans, receives managed Git hooks, and no longer reads as a
version failure. On the App side a Basic workspace now produces no projects, dependency edges, levels,
unmatched counts or generated packages on any path, while version files still work and `{@Repo}` is
refused when versioning is off. A workspace with CI=None makes no GitHub Actions request, including
during synchronized push, while GitHub repositories and pull requests are unaffected. Nothing else is
user-visible yet: there is still no way to create anything but a .NET Dependency Workspace, because the
create/edit controls are Unit H.

**Integration method.** Parallel units run in separate git worktrees on their own branches
(`wp/unit-<id>`), so two agents never build the same tree. The owner merges them into
`workspace-profiles` one at a time and reruns all three suites after each wave.

### Phase map

| Phase | Content | Status |
|---|---|---|
| 1 | Profile model, migration, capability resolver | DONE (Unit A) |
| 2 | Worker sync decoupling, version provider, hook capability resolution | DONE (Unit B) |
| 3 | App persistence / dependency recompute gating | DONE (Unit C) |
| 4 | Push / update / restore strategies | READY (Unit D) |
| 5 | UX: create/edit, grid, navigation, CI provider boundary, host readiness | Unit E DONE; F, G, J IN PROGRESS; H TODO |
| 6 | Profile transitions, stale derived state, final regression | TODO (Units H, I) |

### Phase 3-6 sequencing, re-planned from the integrated state

Phases 1 and 2 are complete, so the remaining units were re-sequenced against what actually shipped.

| Wave | Units | Why together |
|---|---|---|
| 1 | **C** (.NET enrichment and recompute) and **E** (CI provider boundary), in parallel | Both depend only on finished work and are file-disjoint: C lives in the project/dependency/recompute layer, E in the GitHub/Actions layer. E is the larger unknown, so starting it early de-risks Phase 5. |
| 2 | **D** (push/update/restore), **F** (grid/header UX), **G** (navigation and page access) and **J** (host readiness), in parallel | D needed C's gating; F and G needed C's and E's capabilities; all three suppliers are integrated. The four are file-disjoint by assignment: D owns services and Worker requests, F the grid and header Razor files, G the nav, page guards and `Program.cs`, J the host-prerequisite code. Merge order D, F, G, J. Originally D was Wave 2 and F+G Wave 3; they were merged into one wave once Wave 1 showed the capability contracts were already complete. |
| 3 | **H** (create/edit and transitions) | The first user-visible change, and deliberately last: until it ships, no user can create anything but a .NET Dependency Workspace, so every earlier wave is safe to land incrementally. |
| 4 | **I** (regression and assurance) | Verification over the whole feature, including the owner's manual Feature pass. |

Two things changed in the plan as a result of Phase 1-2:

- **Unit C gained a hard pre-work item.** `CreateGitWorktreeRequest` does not derive from
  `WorkspaceCommandRequest`, so worktree creation cannot be capability-gated at all until it is rebased
  onto the base class. This blocks part of both C and D and must be done first.
- **Unit C gained a second recompute entry point**, `SyncCommandHandler.cs:90`, which the original notes
  missed. Gating only the one entry point the notes named would have left Basic workspaces computing
  dependency state on every hook sync.

Unit H's scope **shrank**: because the version state is fully derived rather than persisted, the
"versioning toggles handle stale version state" requirement no longer needs a data migration or a
cleanup pass. Turning versioning off simply stops the enrichment and the UI stops reading the column.

### Rules for agents

1. Read both documents before starting a unit.
2. The owner defines shared contracts before dependent units start. Do not invent a competing
   capability abstraction.
3. Do not edit a file another unit owns. If two units need one file, the owner assigns it and exposes a
   seam.
4. Read the latest integrated state before beginning a dependent unit.
5. Every unit must compile and pass its focused tests before handoff.
6. No opportunistic cleanup outside the unit's scope.
7. Document discovered coupling that violates the intended architecture instead of patching around it
   locally. The owner decides whether it belongs in this change or becomes a follow-up.
8. Update this file before handoff: what changed, tests run, result, architectural discoveries,
   deviations, follow-ups. Any architectural change also updates the design document.
9. A unit is not `DONE` until its acceptance criteria and focused tests pass.

### Avoid parallel edits to these hot files

```text
WorkspaceRepositories.razor and partials
WorkspaceRepositoriesHeader.razor
WorkspaceGitService*
WorkspaceRepository
WorkspaceProjectRepository*
NavMenu.razor
Program.cs / DI bootstrap
AppDbContext.cs / Migrations.cs
```

---

## Unit O - Owner: contracts and documents

| | |
|---|---|
| Owner | owner |
| Status | DONE |
| Dependencies | none |

**Scope.** Both living documents, and the shared contracts every other unit depends on.

**Files owned.**

```text
docs/workspace-profiles/Workspace-Profiles-Design.md
docs/workspace-profiles/Workspace-Profiles-Implementation-Plan.md
src/GrayMoon.Abstractions/Workspaces/WorkspaceType.cs
src/GrayMoon.Abstractions/Workspaces/WorkspaceVersioningMode.cs
src/GrayMoon.Abstractions/Workspaces/WorkspaceCiProvider.cs
src/GrayMoon.Abstractions/Workspaces/RepositoryOperationCapabilities.cs
src/GrayMoon.Application/Workspaces/WorkspaceCapabilities.cs
src/GrayMoon.Application/Workspaces/IWorkspaceCapabilitiesResolver.cs
```

**Acceptance.** Contracts compile; the three enums and the capability record exist with no behaviour
attached; resolver interface takes a `workspaceId` and nothing context-shaped.

**Notes.** Enums live in `GrayMoon.Abstractions` because both App and Worker need them.
`RepositoryOperationCapabilities` is the Worker-facing wire subset and carries only what the Worker
acts on. `WorkspaceCapabilities` and its resolver live in `GrayMoon.Application` because they are an
App-side policy contract consumed by pages, orchestrators and endpoints.

---

## Unit A - Workspace profile model and capability policy

| | |
|---|---|
| Owner | subagent |
| Status | DONE |
| Dependencies | Unit O |

**Scope.** Persist the three axes, migrate existing rows, implement the resolver. No behaviour change
anywhere else.

**Files owned.**

```text
src/GrayMoon.App/Models/Workspace.cs
src/GrayMoon.App/Data/AppDbContext.cs          (Workspace entity block only)
src/GrayMoon.App/Migrations.cs
src/GrayMoon.App/Services/Workspaces/WorkspaceCapabilitiesResolver.cs
src/GrayMoon.App/Repositories/WorkspaceRepository.cs
src/GrayMoon.App/Program.cs                    (DI registration only)
src/GrayMoon.App.Tests/WorkspaceCapabilitiesResolverTests.cs
src/GrayMoon.App.Tests/WorkspaceProfileMigrationTests.cs
```

**Implementation goal.**

- Three enum properties on `Workspace`, persisted with `HasConversion<int>()`, model defaults
  `Basic` / `None` / `None`.
- `MigrateWorkspaceProfileColumnsAsync` in `Migrations.cs`, called from `RunAllAsync`: guard each column
  with `pragma_table_info('Workspaces')`, add when absent, and backfill
  `Type=1, VersioningMode=1, CiProvider=1` **only on the branch that just created the columns**.
- `WorkspaceCapabilitiesResolver` implementing `IWorkspaceCapabilitiesResolver`, sealed, resolving by
  `workspaceId` only.
- An overload or helper so `WorkspaceRepository.AddAsync` can create a workspace with an explicit
  profile, defaulting to today's behaviour for existing callers.

**Tests.**

- existing row migrates to `DotNetDependency` + `GitVersion` + `GitHubActions`
- migration is idempotent across repeated runs
- migration does not overwrite a profile the user changed after the columns existed
- fresh database produces model defaults
- resolver matrix: Basic+None, Basic+GitVersion, DotNetDependency+GitVersion
- resolver returns the same capabilities for a Feature context's workspace as for the workspace itself

**Acceptance.** All of the above pass; no other behaviour changes; all three test projects still pass.

**Explicit non-goals.** No sync changes, no UI, no transition lifecycle, no dependency gating.

**Risks / findings.**

- The migration landed as **strict step 4** in `Migrations.StrictSteps`, not as a legacy tolerant step.
  Strict steps run in their own transaction and must let exceptions propagate, so
  `MigrateWorkspaceProfileColumnsAsync` deliberately has **no** `try`/`catch` - do not "fix" that by
  copying the tolerant pattern used by the pre-baseline methods.
- Raw ADO commands (`pragma_table_info`, `ALTER TABLE`, the backfill `UPDATE`) enlist in the strict
  step's ambient transaction unchanged. `Runs_inside_the_strict_step_transaction` pins this.
- The backfill is guarded by "at least one column was just created". A user who deliberately sets a
  Workspace to Basic is therefore never stomped by a later migration run.
- `Connector.ConnectorType` needs `.HasSentinel((ConnectorType)0)` because its default value is
  non-zero. The three profile enums all default to `0` on the model, so they need no sentinel - if a
  future axis gains a non-zero default, it will.
- Migration tests cannot build the pre-migration table shape with `EnsureCreated()`, which always
  creates the new columns. They use `ALTER TABLE Workspaces DROP COLUMN` to simulate the old shape.
- `WorkspaceRepository.AddAsync` defaults the three new parameters to the **.NET triple**, not the model
  defaults, so the existing create modal keeps producing today's behaviour until Unit H adds the
  controls. Two different sets of defaults coexist on purpose; see the design doc's defaults table.

**Owner verification.** `dotnet build GrayMoon.slnx` clean (0 warnings). `GrayMoon.App.Tests` 848/848,
`GrayMoon.Worker.Tests` 279 passed + 1 skipped (pre-existing conditional GitVersion test),
`GrayMoon.Common.Tests` 234/234. All eight touched files are CRLF with no non-ASCII dashes.

**Follow-ups.**

- Unit H passes all three profile values explicitly from the create modal and can then decide whether
  `AddAsync`'s defaults should fall back to the model defaults.
- Unit F/G consume `GetManyAsync` for list surfaces; it silently omits ids that no longer exist, so
  those callers must handle a missing key rather than indexing blindly.

---

## Unit B - Worker Git sync and repository versioning

| | |
|---|---|
| Owner | subagent |
| Status | DONE |
| Dependencies | Unit O, Unit A |

**Step progress.** All six steps are integrated and verified, in three passes: steps 1-3 (explicit probed
flags, sync restructure, `IRepositoryVersionProvider` plus capabilities on `WorkspaceCommandRequest`),
steps 4-5 (`IWorkspaceCapabilityProvider` with its three resolution tiers, all four hooks routed through
`RepositoryStateProbe`, hook-install version gate removed), step 6 (Feature default-tip path gated
App-side, three-valued version state).

The capability provider resolves in three tiers: a cache warmed from every inbound App command
(`CommandDispatcher.ExecuteAsync`), then `GET /workspaces/{id}/capabilities` behind the worker secret,
then `LegacyFullEnrichment` when the App is unreachable. A fallback result is never cached, and a hook
sync never fails because capabilities could not be resolved. Hook scripts are unchanged and remain
context-agnostic.

**Scope.** Pure Git sync as the baseline, with optional version and project enrichment, across every
Worker entry point including the four hooks.

**Files owned.**

```text
src/GrayMoon.Abstractions/Notifications/RepositorySyncNotification.cs
src/GrayMoon.Worker/Commands/SyncRepositoryCommand.cs
src/GrayMoon.Worker/Commands/RefreshRepositoryVersionCommand.cs
src/GrayMoon.Worker/Commands/GetRepositoryVersionCommand.cs
src/GrayMoon.Worker/Commands/GetGitVersionAtDefaultTipCommand.cs
src/GrayMoon.Worker/Commands/CommitHookSyncCommand.cs
src/GrayMoon.Worker/Commands/CheckoutHookSyncCommand.cs
src/GrayMoon.Worker/Commands/MergeHookSyncCommand.cs
src/GrayMoon.Worker/Commands/PushHookSyncCommand.cs
src/GrayMoon.Worker/Commands/CommitSyncRepositoryCommand.cs
src/GrayMoon.Worker/Commands/PushRepositoryCommand.cs
src/GrayMoon.Worker/Services/RepositoryStateProbe.cs
src/GrayMoon.Worker/Services/RepositoryVersionProvider.cs        (new)
src/GrayMoon.Worker/Services/WorkspaceCapabilityProvider.cs      (new)
src/GrayMoon.Worker/Services/GitService.cs                       (hook-writing region only)
src/GrayMoon.Worker/Jobs/Requests/*
src/GrayMoon.Worker/Cli/Handlers/RunCommandHandler.cs            (DI registration only)
src/GrayMoon.App/Api/Endpoints/WorkspaceEndpoints.cs             (one new endpoint)
src/GrayMoon.App/Services/Git/WorkspaceGitService.Sync.cs
src/GrayMoon.App/Services/Git/WorkspaceGitService.Projects.cs
src/GrayMoon.App/Services/Worker/SyncCommandHandler.cs
src/GrayMoon.Worker.Tests/*                                      (new test files)
```

**Implementation goal, in order.**

1. **Explicit probed flags.** Add `GitVersionProbed` / `ProjectsProbed` to `RepositorySyncNotification`
   and read them in `SyncCommandHandler` instead of inferring. Convert `CommitHookSyncCommand` and
   `PushHookSyncCommand` to send an explicit `State` snapshot (the checkout and merge hooks already do).
   Keep inference as a fallback for pre-`State` Workers. **Nothing may skip a group before this lands.**
2. **Sync restructure.** Common Git snapshot, then optional version enrichment, then optional project
   enrichment, each guarded by the request's capabilities and reporting its probed flag as `false` when
   skipped.
3. **Version provider.** `IRepositoryVersionProvider` with a GitVersion implementation and a no-op
   implementation, selected from capabilities. Route every GitVersion call site through it. Thread
   capabilities into `RepositoryStateProbeOptions`.
4. **Capability provider.** `IWorkspaceCapabilityProvider` in the Worker: command-warmed per-workspace
   cache, cold-miss fetch from `GET /workspaces/{id}/capabilities` behind the worker secret, legacy
   full-enrichment fallback when neither is available, plus an invalidation path. Hook commands resolve
   through it. Hook scripts stay unchanged and context-agnostic.
5. **Hook install gate.** Remove the `version != "-"` condition so Basic+None still gets hooks. Preserve
   `CreateGitWorktreeCommand`'s rewrite-before-`worktree add` ordering.
6. **Feature path and version semantics.** Gate
   `ApplyDefaultTipVersionsForMergedFeatureReposAsync` / `GetGitVersionAtDefaultTipCommand` on
   `UsesRepositoryVersioning`. Add the explicit not-applicable version state without redefining
   `IsVersionUnresolved`.

**Tests.** Following the existing Worker convention - real temp git repositories
(`TempGitRepositoryFixture`), `[FactIfGitVersion]` where GitVersion is required, hand-written fakes like
`NoProjects`. This repository has no process mocking and no bUnit.

- Basic+None: full Git sync, zero GitVersion process launches, zero `dotnet tool restore`, zero
  `.csproj` scan
- Basic+GitVersion: version resolved, still no `.csproj` scan
- DotNetDependency+GitVersion: behaviourally identical to today
- each of the four hook paths honours capabilities, including the cold-cache REST path and the
  App-unreachable fallback
- a Basic Feature context runs no GitVersion through the merged-PR default-tip path
- a skipped group never clears persisted state (asserted through `WorkspaceRepositoryStateWriter`)
- a genuinely project-free .NET repository stays distinguishable from a skipped scan
- hooks are installed for a repository with no resolvable version

**Acceptance.** All of the above pass; all three test projects pass; the Feature suite passes; the three
awaiting-retest GitVersion/branch issues behave no worse than before.

**Explicit non-goals.** No App-side dependency orchestration gating (Unit C). No UI. No push strategy
work (Unit D).

**Risks / findings.**

- Step 1 needed no new fields on `RepositorySyncNotification`: `RepositoryStateSnapshot` already carried
  both markers, so it reduced to making every current Worker send an explicit `State`.
  `BuildSnapshotFromFlatNotification` is untouched and now only reachable from a pre-`State` Worker.
- Step 5 turned out to be a **regression fix**, not a precaution. Once step 2 stopped resolving a version
  for Basic+None, the `version != "-"` gate silently stopped installing any managed hooks. If the steps
  are ever re-sequenced, 2 and 5 must stay adjacent.
- The pre-push hook could not simply be pointed at `RepositoryStateProbe`: it fires before the push data
  is transferred, so a straight probe would mark stale commit counts as probed and let the App persist
  them over the real ones. `RepositoryStateProbeOptions` gained `IncludeCommitCounts` (default `true`,
  so every existing caller is unchanged).
- Converting the post-commit hook onto the probe changed where `HasUpstream` comes from: the git-configured
  upstream rather than name-matching against the remote branch list. That is the direction the repo already
  took deliberately (`RefreshRepositoryVersionCommand.cs:58`), and it means post-commit now makes no network
  call and no longer needs a connector token - but it is a behaviour change, so it wants a line in Unit I.
- The not-applicable version state is **fully derived**: no new column, no new enum on the link, no
  denormalized state. `IsVersionUnresolved` and both of its pinning test files are untouched; the state is
  layered on at the three points of consumption.
- Three files outside Unit B's owned list had to change: `CommandDispatcher.cs` (the only seam every inbound
  command passes through, needed for cache warming), `WorkerSecretMiddleware.cs` (unavoidable - the new
  endpoint has to be classified there), and `SyncStateTestContext.cs` (shared test harness, forced by a
  constructor change).

**Owner verification.** Build clean / 0 warnings after each of the three passes. Final counts:
`GrayMoon.App.Tests` 878/878, `GrayMoon.Worker.Tests` 307 passed + 1 skipped (pre-existing conditional
GitVersion test), `GrayMoon.Common.Tests` 234/234. Phase 1-2 added 43 tests to App and 28 to Worker. The
Feature-context isolation suite is inside `GrayMoon.App.Tests` and passes.

**Follow-ups.**

- `CreateGitWorktreeRequest` does not derive from `WorkspaceCommandRequest`, so it cannot be
  capability-gated until it is rebased onto the base class. Blocks part of Units C/D.
- `UndoPushRequest`, `FetchCommitsRequest` and `GetGitChangeStatusRequest` have a workspace id but the App
  does not populate capabilities on them yet.
- The four hooks now disagree about `GitVersionFailed`: the rewritten commit and push hooks set it
  honestly, checkout and merge never set it. Harmless today, cleanup for Unit I.
- `RefreshRepositoryProjectsCommand` and `CommitSyncRepositoryCommand` receive capabilities but do not act
  on all of them yet; those are deliberate seams for Unit C.

---

## Unit C - .NET project/dependency enrichment and recompute

| | |
|---|---|
| Owner | subagent, owner integration |
| Status | DONE |
| Dependencies | Unit A (DONE), Unit B (DONE) |

**Step progress.** All seven items are implemented in one pass: worktree request rebased onto
`WorkspaceCommandRequest`, recompute gated at the scope boundary, every direct recompute caller routed
through the scope, generic file versioning separated from generated-package inference, the `{@Repo}`
token rule enforced in the Files page, the two Worker seams closed, and the stale `Derive` comment fixed.
No new derived capability was needed (design section 4 unchanged): the gates use the existing
`UsesDependencyGraph`, `DiscoversDotNetProjects`, `UsesRepositoryVersioning` and
`UsesGeneratedPackagesFromVersionFiles`.

**What changed.**

- `CreateGitWorktreeRequest` now derives from `WorkspaceCommandRequest`. `WorkspaceId` was **not**
  lifted onto the base class, so no member-hiding cleanup was needed. `WorkspaceFeatureOperations`
  resolves capabilities and sends them on every worktree request (Feature create and repair), and
  `CommandDispatcher` warms the capability cache from it, so the `post-checkout` hook fired by
  `git worktree add` honours the profile. `CreateGitWorktreeCommand` itself runs no enrichment and is
  unchanged.
- `WorkspaceStateRecomputeScope` gained `RecomputeDependencyStatsAsync`, a no-op unless
  `UsesDependencyGraph`. `RecomputeAsync` always runs the file-version check, then calls it. Because
  `SyncCommandHandler.cs:90` already goes through the scope, the hook entry point is gated with no change
  to that file.
- Direct recompute callers rerouted through the scope: version refresh (`WorkspaceGitService.Sync.cs`),
  dependency sync (`WorkspaceGitService.Projects.cs`), the Files page. `PersistVersionsAsync` merges
  project dependencies only when `UsesDependencyGraph`. `RefreshWorkspaceProjectsAsync` and
  `RefreshSingleRepositoryProjectsAsync` return before contacting the Worker when the workspace does not
  discover .NET projects.
- `WorkspaceFileVersionService`: generated-package sync returns "no changes" unless
  `UsesGeneratedPackagesFromVersionFiles`; its own two recompute calls are gated on
  `UsesDependencyGraph` (it cannot use the scope, which depends on it); `{@Repo}` tokens are skipped in
  checks and updates when versioning is off (logged at debug, never a GitVersion failure); the grid's
  version-line readers return nothing when versioning is off. New public helpers
  `GetTokensRequiringRepositoryVersioning` and `RepositoryVersioningRequiredMessage`.
- Files page: `VersionConfigModal` shows "requires repository versioning (GitVersion) to be enabled" for a
  `{@Repo}` token and disables Save; `WorkspaceFiles.razor` also refuses the save. No new UI primitive.
- Worker: `RefreshRepositoryProjectsCommand` returns a null project list (not scanned, as opposed to
  empty) when project discovery is off. `CommitSyncRepositoryCommand` already honoured capabilities
  (probe and version provider both built from them; it never scans projects) - no change needed.
- `SyncStatusWrite.Derive` doc comment now matches the implementation (branch or tag identity; version
  deliberately ignored).

**Files touched.**

```text
src/GrayMoon.Worker/Jobs/Requests/CreateGitWorktreeRequest.cs
src/GrayMoon.Worker/Services/CommandDispatcher.cs                       (cache-warming case only)
src/GrayMoon.Worker/Commands/RefreshRepositoryProjectsCommand.cs
src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs       (capabilities on worktree requests only)
src/GrayMoon.App/Services/Workspaces/WorkspaceStateRecomputeScope.cs
src/GrayMoon.App/Services/Workspaces/WorkspaceFileVersionService.cs
src/GrayMoon.App/Services/Workspaces/WorkspaceRepositoryStateWriter.cs  (doc comment only)
src/GrayMoon.App/Services/Git/WorkspaceGitService.Sync.cs
src/GrayMoon.App/Services/Git/WorkspaceGitService.Projects.cs
src/GrayMoon.App/Components/Pages/WorkspaceFiles.razor
src/GrayMoon.App/Components/Modals/VersionConfigModal.razor
src/GrayMoon.App.Tests/WorkspaceDependencyGatingTests.cs                (new)
src/GrayMoon.App.Tests/WorkspaceB4ContextLeakTests.cs                   (forced: constructor change)
src/GrayMoon.Worker.Tests/WorktreeAndProjectRefreshCapabilitiesTests.cs (new)
docs/workspace-profiles/Workspace-Profiles-Design.md                    (section 8)
```

**Tests.** `WorkspaceDependencyGatingTests` (13, real DI + in-memory SQLite + fake worker bridge):
Basic hook sync builds no dependency state while DotNet still does; Basic recompute still runs the
file-version check but builds no levels from a version file, DotNet still orders the consumer after its
producer; Basic+None checks `{@Repo:branch}` and never resolves `{@Repo}`; Basic+GitVersion resolves
`{@Repo}` with no `.csproj`; a `.csproj` version file becomes a generated package for DotNet only; version
lines are not applicable without versioning; the token rule rejects `{@Repo}` only when versioning is off;
Basic project refresh sends nothing to the Worker; Basic sync merges no dependencies even when the Worker
reports a project; Feature create sends the profile on every worktree request.
`WorktreeAndProjectRefreshCapabilitiesTests` (5): the worktree request deserializes capabilities; the
dispatcher warms the cache the `post-checkout` hook reads (so Basic+None worktree creation runs no
GitVersion or project scan through the hook, which Unit B's hook tests already cover from the cache);
project refresh skips the scan for Basic, scans for DotNet and for a pre-profile App.

**Owner verification.** `dotnet build GrayMoon.slnx` 0 warnings, 0 errors. `GrayMoon.App.Tests`
891/891 (+13), `GrayMoon.Worker.Tests` 312 passed + 1 skipped (pre-existing conditional GitVersion test,
+5), `GrayMoon.Common.Tests` 234/234. The Feature-context isolation suite is inside `GrayMoon.App.Tests`
and passes.

**Owner integration (closes the two acceptance gaps below).** The subagent left two paths on which Basic
could still build dependency state, both outside its edit scope. Both are fixed on the integration branch:

- `WorkspaceFeatureOperations` (Feature seed) now recomputes through
  `WorkspaceStateRecomputeScope.RecomputeDependencyStatsAsync` instead of calling the repository directly.
  No dedicated test: there is no Feature-seed test harness, and the gate itself is covered by
  `WorkspaceDependencyGatingTests`. Unit I's manual Feature pass covers it end to end.
- `WorkspaceRepositoryStateWriter` persists probed project rows only when `DiscoversDotNetProjects`. This
  covers return-to-default and the hook fallback in one place. New test
  `Basic_workspace_persists_no_projects_even_when_the_snapshot_carries_them`.
- Consequence: five existing writer/hook/return-to-default tests seeded the harness workspace with the model
  default (Basic) while asserting .NET project behaviour. They now opt in through a new
  `SyncStateTestContext.UseDotNetDependencyProfileAsync()`. The harness default stays Basic because the
  profile tests rely on it.

Verified after the merge with Unit E: build 0 warnings, App 904/904, Worker 312 + 1 skip, Common 234/234.

**Risks / findings.**

- **Generated-package context-scoping (deferred TODO) is complete for reads, not for writes.** Generated
  rows are workspace-global in the special context and every context's project read includes them
  (`WorkspaceProjectRepository.cs:105`), and consumer edges are built for real projects in every context
  (`WorkspaceProjectRepository.GeneratedPackages.cs:133-135`). But the sync is driven from one context's
  view and applied workspace-wide: `WorkspaceFileVersionService.cs:793-794` applies the *calling*
  context's missing-file overlay, so a Feature whose version file is missing removes the generated row
  for every context; and `GeneratedPackages.cs:141-148` writes the version resolved in that context onto
  the matching consumer project in *every* context, which can change another context's unmatched count.
  Left as found; fixing it means per-context edge versions.
- **Fixed in owner integration.** `WorkspaceFeatureOperations.cs:2104` recomputed dependency stats
  directly on Feature create, so a Basic Feature context still got a `DependencyLevel`.
- **Fixed in owner integration.** `ReturnToDefaultBranchCommand.cs:99-101` asks for GitVersion and
  projects with no capabilities, and the state writer merged any probed project list, so return-to-default
  and the hook fallback (no `AppApiBaseUrl`, cold cache) still persisted projects for Basic. The writer is
  now the gate. `ReturnToDefaultBranchCommand` still runs GitVersion for Basic+None: that is a
  Worker-side wasted probe, not persisted state, and is left for Unit D, which owns that request family.
- `TotalFileConfigRepos` (`Services/Workspaces/WorkspaceFileVersionService.cs:914-957`) still counts `{@Repo}` references when
  versioning is off; harmless today but the grid counter is not yet profile-aware.

**Deviations.** `WorkspaceB4ContextLeakTests.cs` (shared test, not owned) gained one constructor argument,
forced by the new `WorkspaceFileVersionService` dependency. `SyncCommandHandler.cs` and
`WorkspaceProjectRepository*.cs` were not changed: the scope gate covers the hook path, and the
repository stays profile-agnostic.

**Follow-ups.**

- Unit D: `DependencyUpdateOrchestrator.cs:81` calls `RefreshWorkspaceProjectsAsync`, which is now a
  no-op for workspaces that do not discover .NET projects.
- Unit F: Basic grid rows have null `DependencyLevel`/`UnmatchedDeps` and empty version lines when
  versioning is off; render them as not applicable, not as zero.
- Unit G: a Basic workspace produces no new projects, edges or generated packages, so the
  Projects/Packages/dependency pages have nothing current to show and can be hidden on
  `DiscoversDotNetProjects` / `UsesDependencyGraph`.
- Unit H: switching a workspace to Basic leaves previously persisted dependency state in place; the
  transition must clear it, since nothing recomputes it any more.
- Unit I: verify the Feature-seed recompute gate manually with a Basic Feature, and the
  generated-package write scoping (Deferred TODOs).

**Scope.** Project discovery persistence/reconciliation, dependency graph activation, dependency-stat
recompute gating, generated packages from version files, and the separation between generic file
versioning and .NET-specific dependency inference.

Key seam: `WorkspaceStateRecomputeScope.RecomputeAsync` currently always does both the file-version
check and the dependency-stat recompute. The file-version check is useful in both workspace types and
must not be disabled for Basic; only dependency-stat recompute is gated, at the scope boundary rather
than inside `WorkspaceProjectRepository`.

**Added by Phase 1-2 (see the discoveries log for detail).**

- `RecomputeAsync` has a **second** entry point the original notes missed:
  `SyncCommandHandler.cs:90` invokes it unconditionally on every hook sync, including for a Basic
  workspace. Both entry points must be gated.
- Two readers count a null `GitVersion` as an unmatched dependency, which would give a Basic workspace a
  nonzero unmatched-dependency badge out of nothing: `WorkspaceProjectRepository.DependencyStats.cs:68-74`
  and `DependencyLines.cs:308-313`. The correct fix is the gate above (produce no dependency state at
  all for Basic), not a display tweak.
- `WorkspaceFileVersionService.cs:1046,1080` are the two lines that must implement design section 11's
  rule that only the default `{@Repo}` GitVersion token requires versioning to be enabled.
- `RefreshRepositoryProjectsCommand` and `CommitSyncRepositoryCommand` already receive capabilities from
  the App but do not act on all of them. Those are deliberate seams left for this unit.
- Fix the stale `SyncStatusWrite.Derive` doc comment at `WorkspaceRepositoryStateWriter.cs:16`: it claims
  "Error without a usable version" but the implementation deliberately ignores the version.

**Acceptance.** Basic never builds .NET dependency state; generic Files/version-file behaviour still
works for Basic; .NET Dependency behaviour is unchanged.

**Pre-work.** Verify whether generated-package context-scoping is complete; an older gaps document
recorded it as unfinished.

**Pre-work, new.** `CreateGitWorktreeRequest` does not derive from `WorkspaceCommandRequest`, so it has
no `Capabilities` property and worktree creation cannot be gated at all until it is rebased onto the base
class. Do that first, and note that `WorkspaceCommandRequest` has no `WorkspaceId` of its own - if this
unit lifts one onto the base class, the three derived types that declare their own must have those
declarations removed or member-hiding warnings will break the 0-warning bar.

---

## Unit D - Push / update / restore strategies

| | |
|---|---|
| Owner | unassigned |
| Status | READY |
| Dependencies | Unit A (DONE), Unit C (DONE), Unit E (DONE) |

**Inputs from Wave 1.**

- Capabilities to dispatch on already exist: `UsesDependencyAwarePush`, `UsesDependencyAwareUpdate`,
  `UsesPackageRestore`, `UsesNuGetPackages`. Do not add new ones for push.
- `DependencyUpdateOrchestrator.cs:81` calls `RefreshWorkspaceProjectsAsync`, which is now a no-op for
  workspaces that do not discover .NET projects (Unit C).
- Keep the three CI lines in `WorkspacePushService.cs` (`:182-184` resolve the provider once per run,
  `:224` one run watch per level, `:268` one `TickAsync` per wait tick). A Basic path that skips the package
  wait never ticks the watch and needs no CI check (Unit E).
- `ReturnToDefaultBranchCommand.cs:99-101` still requests GitVersion and projects with no capabilities.
  Persisted state is already safe (the state writer drops the projects), but Basic+None still launches
  GitVersion there. Populate capabilities on that request alongside the three below.

**Note from Phase 1-2.** `PushRepositoryRequest` already carries capabilities, but `UndoPushRequest`,
`FetchCommitsRequest` and `GetGitChangeStatusRequest` have a workspace id with no capabilities populated
by the App. Populate them here rather than in Unit C.

**Scope.** Basic Git push path, dependency-aware push path, package waits, dependency update
orchestration, package restore, capability-based strategy dispatch.

Key seam: `WorkspacePushOperations.GetPlanForLinksAsync` always requests dependency info after building
the push plan.

**Acceptance.** Basic push performs no package/dependency query or wait; .NET Dependency retains current
semantics; no deep workspace-type checks inside `PushOrchestrator`.

---

## Unit E - CI provider boundary

| | |
|---|---|
| Owner | subagent |
| Status | DONE |
| Dependencies | Unit A (DONE) |

**Scope.** The `WorkspaceCiProvider` boundary, GitHub Actions refresh/query activation, CI-specific
status sources, and the separation of source control from CI.

Known coupling to work through: one `GitHubService` partial class spans repositories, pull requests and
workflows; one `Connector` row, one token and one rate-limit tracker serve both; the Actions page gates
on `link.Repository.Connector != null` as a proxy for "CI enabled"; `WorkspacePushService` polls Actions
during synchronized push.

**Acceptance.** CI=None performs no GitHub Actions refresh or query; GitHub repository and PR
functionality still works with CI=None; CI=GitHubActions preserves current behaviour; another provider
would not require provider checks throughout the UI.

**Non-goal.** Do not implement a second provider.

**What changed.** A small seam in `Services/Ci/` (design section 11a): `IWorkspaceCiProviderResolver`
selects an `IWorkspaceCiProvider` from the workspace's `CiProvider` - `NoCiProvider` (does nothing) or
`GitHubActionsCiProvider` (thin adapter over the unchanged `WorkspaceActionService` / `GitHubActionsService` /
`GhaWorkflowLiveFeedService`). Push-time run watching became an `IPushCiRunWatch` strategy: the discovery and
live-feed loop moved verbatim out of `WorkspacePushService` into `GitHubActionsPushRunWatch`, and the push
loop now makes one provider-agnostic `TickAsync` call. The Actions page reads persisted status and refreshes
rows through the provider; for CI=None it builds no rows and shows "CI is not enabled for this workspace.",
so no background refresh, auto-poll or hub-triggered refresh can start. No GitHub-specific type was renamed,
`GitHubService` was not split, and no Actions service gained a capability check of its own.

**Files touched.**

```text
src/GrayMoon.App/Services/Ci/IWorkspaceCiProvider.cs             (new)
src/GrayMoon.App/Services/Ci/IPushCiRunWatch.cs                  (new)
src/GrayMoon.App/Services/Ci/NoCiProvider.cs                     (new, includes NoOpPushCiRunWatch)
src/GrayMoon.App/Services/Ci/GitHubActionsCiProvider.cs          (new)
src/GrayMoon.App/Services/Ci/GitHubActionsPushRunWatch.cs        (new, code moved from WorkspacePushService)
src/GrayMoon.App/Services/Ci/WorkspaceCiProviderResolver.cs      (new, interface + implementation)
src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs     (GHA-polling region only)
src/GrayMoon.App/Components/Pages/WorkspaceActions.razor.cs
src/GrayMoon.App/Components/Pages/WorkspaceActions.Loading.cs
src/GrayMoon.App/Components/Pages/WorkspaceActions.AutoRefresh.cs
src/GrayMoon.App/Program.cs                                      (DI registration only)
src/GrayMoon.App.Tests/WorkspaceCiProviderTests.cs               (new)
```

`GitHubActionsService.cs`, `GhaWorkflowLiveFeedService.cs`, `GitHubService.Workflows.cs` and
`WorkspaceActionService.cs` needed no change.

**Tests.** `WorkspaceCiProviderTests` (12, fake `HttpMessageHandler` recording every GitHub request, SQLite
in-memory): resolver selection (None, GitHubActions, unknown value -> None); `IsEnabled` equals
`UsesCiIntegration`; CI=None refresh makes zero HTTP requests and persists nothing (special Workspace and
Feature); CI=None reports no persisted status even over an old Actions row; CI=GitHubActions refresh fetches
and persists onto the link for the special Workspace and onto the context row only for a Feature; CI=None push
run watch makes zero requests and writes no overlay lines; CI=GitHubActions push run watch discovers the
running run and streams its jobs; no overlay -> no-op; a PR lookup still reaches GitHub for a CI=None workspace
while no `/actions/` request is made.

**Owner verification (subagent run).** `dotnet build GrayMoon.slnx` 0 warnings / 0 errors.
`GrayMoon.App.Tests` 890/890 (878 + 12), `GrayMoon.Worker.Tests` 307 + 1 pre-existing skip,
`GrayMoon.Common.Tests` 234/234. Touched files CRLF, no non-ASCII dashes.

**Owner verification.** Diff reviewed; merged after Unit C with no conflicts and no integration changes.
Verified after the merge: build 0 warnings, App 904/904, Worker 312 + 1 skip, Common 234/234.

**Risks / findings.**

- `WorkspacePushService.RunPushAsync` has no test harness in the repository (it needs a Worker bridge,
  dependency service, NuGet and DbContext). The CI=None push guarantee is therefore tested at the seam: the
  push loop's only CI path is `ciRunWatch.TickAsync` (`WorkspacePushService.cs:268`), created from the
  resolved provider (`:182-184`, `:224`), and the watch is tested directly.
- `WorkspacePushService` previously took `GitHubActionsService?` and `GhaWorkflowLiveFeedService?` as optional
  constructor parameters; it now takes `IWorkspaceCiProviderResolver?` instead. A null resolver selects
  `NoCiProvider` - the safe direction - where a null `GitHubActionsService` also used to disable watching.
- Push-wait GHA log lines now log under the `GitHubActionsCiProvider` category instead of
  `WorkspacePushService`. Messages are unchanged.
- The Actions page's rerun / run / cancel / logs and `GhaWorkflowLiveTerminal` / `GhaLogsModal` still call
  GitHub directly (`WorkspaceActions.WorkflowRun.cs:260,322,382`, `WorkspaceActions.BulkActions.cs:114,192,272`,
  `GhaLogsModal.razor:263`, `GhaWorkflowLiveTerminal.razor:161`). They only act on rows, and rows exist only
  when CI is enabled (`WorkspaceActions.Loading.cs:24-30`), so CI=None cannot reach them. Left GitHub-specific
  on purpose: a second provider would need different actions.
- `link.Repository.Connector != null` (`WorkspaceActions.Loading.cs:43,169`) is kept as a reachability filter.
  It is no longer the CI decision.
- `GitHubActionsService.GetLatestActionsAsync` / `GetLatestActionAsync` have no callers. Left untouched.
- The workspace repository grid query (`IWorkspaceRepositoryLinkListQueryService` and DTOs), workspace sync,
  hook sync and background services do **not** read or refresh Actions; the only consumers were the Actions
  page and synchronized push. Nothing else needed gating.
- The PR merge dialog's checks row is a pull-request check-run summary (`GitHubService.PullRequests.cs:291`),
  not GitHub Actions, and correctly stays available with CI=None. Only its link to the Actions page is CI.

**Deviations.** No new capability was added to `WorkspaceCapabilities`: `UsesCiIntegration` already answers
"does this workspace have a CI page", and `IWorkspaceCiProvider.IsEnabled` is pinned equal to it by test. The
Actions page shows an inline "CI is not enabled" message for CI=None as a data-loading fallback; the redirect /
access policy is still Unit G's.

**Follow-ups (seam consumers).**

- **Unit G (navigation and page access).** Gate the Actions nav item `NavMenu.razor:83-88` and the route
  `WorkspaceActions.razor:1` on `WorkspaceCapabilities.UsesCiIntegration` (the SSR nav menu should use the
  capability resolver, not the provider, so it pulls in no GitHub services). Once G guards the route, the
  inline fallback at `WorkspaceActions.Loading.cs:25-30` can stay as defence in depth or be removed.
- **Unit F (grid / header).** Pass `ActionsUrl` to `MergePullRequestModal` only when
  `_capabilities.UsesCiIntegration` (`WorkspaceRepositories.razor:425`); the modal already hides the checks
  link for a null/empty URL (`MergePullRequestModal.razor:191`). Optionally hide the "Actions" item of the
  "Open in GitHub" menu (`GitHubSectionsMenu.razor:50`) for CI=None - it opens github.com, not GrayMoon, so
  this is a product call rather than a correctness one. The grid itself has no Actions badges or query to gate.
- **Unit D (push).** Keep the three CI lines in `WorkspacePushService.cs` (`:182-184` resolve once per run,
  `:224` one watch per level, `:268` one `TickAsync` per wait tick) when restructuring push. A Basic push path
  that skips the package wait simply never ticks the watch; no CI check is needed there.

---

## Unit F - Repositories page / grid / header UX

| | |
|---|---|
| Owner | subagent |
| Status | REVIEW |
| Dependencies | Unit A, Unit C, Unit E |

**What changed.** Design section 10a. The page builds one `WorkspaceGridPresentation` from
`WorkspaceCapabilities` per load. The markup reads only that record. `TableColSpan` became
`ColumnCount` (3 plus Version). The Version `<th>`/`<td>` and the dependency metric block (header icon and
row badge, tooltip and custom-dependency entry) render only when their flag is set. The metrics grid gets a
4-block modifier. `ComputeSlots` is a pure flat-or-grouped slot layout. Header rules
`DetermineUpdateControl` (None / Update / PushUpdated) and `DetermineShowsFileVersionUpdate` sit next to
`DeterminePrimaryAction`. Restore is shown only with `ShowPackageRestore`. `ActionsUrl` is passed only with
`UsesCiIntegration`. `GetHeaderStateAsync` takes optional `capabilities`: without the dependency graph it
issues no dependency aggregate and returns the new `HasOutOfDateFiles` instead. Without the dependency graph,
the page also: shows a standalone **Update Files** button (same file-version operation) while a file is out of
date; puts bulk **Merge PRs...** (all repositories) in the Branch/Feature menu; shows level errors in the page
callout; never runs the dependency tooltip loader; and does not pass "Update dependencies" from Prepare
Workspace. .NET Dependency renders exactly as before.

**Files touched.**

```text
src/GrayMoon.App/Components/Pages/WorkspaceGridPresentation.cs                (new)
src/GrayMoon.App/Components/Pages/WorkspaceRepositories.razor, .razor.css
src/GrayMoon.App/Components/Pages/WorkspaceRepositories.State.cs, .Loading.cs, .Display.cs, .BulkMerge.cs, .PrepareWorkspace.cs
src/GrayMoon.App/Components/Pages/WorkspaceRepositoriesRow.razor
src/GrayMoon.App/Components/Shared/WorkspaceRepositoriesHeader.razor
src/GrayMoon.App/Services/Queries/IWorkspaceRepositoryLinkListQueryService.cs, WorkspaceRepositoryLinkListQueryService.cs, WorkspaceRepositoryLinkListModels.cs
src/GrayMoon.App.Tests/WorkspaceGridPresentationTests.cs                     (new)
src/GrayMoon.App.Tests/WorkspaceRepositoryLinkHeaderStateProfileTests.cs     (new)
src/GrayMoon.App.Tests/ListQueryTestContext.cs                               (interceptor-capable query factory)
docs/workspace-profiles/Workspace-Profiles-Design.md                         (new section 10a)
```

**Tests.** The presentation matrix covers Basic+None, Basic+GitVersion, .NET, null and the CI split.
Colspan equals the rendered column count for every type x versioning x CI combination. Slot tests cover a flat
list (rows only, stale levels ignored) and a grouped list (unchanged, including the "No dependencies" group).
Header tests cover: no update control for Basic in any state, the .NET Update/PushUpdated rule unchanged,
Basic file-version button only when out of date, and bulk merge in the menu only when flat. Header-state query
tests use real SQLite and record the SQL issued. With Basic, no command references `UnmatchedDeps` or
`DependencyLevel`, over stale values, for both the Workspace and a Feature context (which reads its own state).
.NET equals the profile-less call. The existing `DeterminePrimaryAction`, version-cell and link-query tests
still pass.

**Verification.** `dotnet build GrayMoon.slnx` 0 warnings / 0 errors. App 933/933 (+29). Common 234/234.
Worker: 312 passed + 1 skip. In the full run, `GitStatusRefreshCoordinatorTests.Coalesced_caller_receives_the_follow_up_scan_not_the_stale_in_flight_scan`
failed once (timing under load). It passes when rerun alone (11/11), and no Worker code changed.
Touched files are CRLF, and added lines have no non-ASCII dashes.

**Risks / findings.**

- `TotalFileConfigRepos` (`Services/Workspaces/WorkspaceFileVersionService.cs:914-957`) is not in any grid DTO, and the grid never
  displays it. So it was not changed: the counter only matters where it is shown.
- Row/index DTOs and `ApplySort` still read `DependencyLevel`/`RepositoryType`/`Dependencies`. For Basic these
  are null (Unit C), so the order is effectively `WorkspaceRepositoryId`. Stale values from an earlier profile
  can still affect sort order, but they cannot affect grouping or badges.
- Before capabilities load, the header renders with `Legacy` presentation. It is disabled at that point
  (no workspace name yet), so a Basic workspace could briefly show a disabled Update button.
- Capabilities are resolved once per workspace load. A profile change made while the page is open shows after
  navigation or reload.
- `Modals/PrepareWorkspaceModal.razor:108-110` (not owned) still shows the "Update dependencies" checkbox for Basic.
  The page ignores it (`WorkspaceRepositories.PrepareWorkspace.cs:64`).
- The bulk-merge load-failure toast still says "for this level" (`WorkspaceRepositories.BulkMerge.cs:75`), also
  when it is opened from the header menu.

**Deviations.** `ListQueryTestContext.cs` (shared test harness) gained a factory method. Two changes outside
the brief: header-menu bulk merge, and the Prepare Workspace page gate. Both prevent a Basic regression or
dependency behaviour that the flat grid would otherwise cause.

**Follow-ups.**

- Owner (product): the github.com "Actions" item in `GitHubSectionsMenu.razor:50` was left as is.
- Unit H or owner: hide "Update dependencies" in `PrepareWorkspaceModal` for workspaces without
  dependency-aware update.
- Unit D: page push paths (`WorkspaceRepositories.Push.cs:36,109`) still call
  `WorkspaceDependencyService.GetPushDependencyInfoFor*`; the page relies on the service being gated.

**Scope.** Capability-aware grid presentation, Version column visibility, dependency metric visibility,
dependency-level grouping and level headers, header action availability, colspan and virtualization
correctness.

Notes: `TableColSpan` is a const in `WorkspaceRepositories.State.cs`; level headers and rows take their
own `ColSpan` parameters. The header's `DeterminePrimaryAction` is a pure static method with existing
tests - extend that pattern rather than adding a render-time branch, since there is no component test
harness.

**Inputs from Wave 1.**

- Basic rows carry null `DependencyLevel` / `UnmatchedDeps` and empty version lines when versioning is
  off. Render them as not applicable, never as zero (Unit C).
- `TotalFileConfigRepos` (`Services/Workspaces/WorkspaceFileVersionService.cs:914-957`) still counts `{@Repo}` references when
  versioning is off. Make the counter profile-aware here if the grid shows it.
- Pass `ActionsUrl` to `MergePullRequestModal` only when `UsesCiIntegration`
  (`WorkspaceRepositories.razor:425`); the modal already hides the checks link for an empty URL. Whether to
  hide the github.com "Actions" item in `GitHubSectionsMenu.razor:50` is a product decision for the owner.
  The grid has no Actions badges or query to gate (Unit E).

**Acceptance.** Basic+None is a clean Git-focused grid with no Version column, no dependency metric, no
level headers and no dependency Update/Restore UI; Basic+GitVersion adds version presentation only;
.NET Dependency is unchanged; no fake "Level 0" or "No dependencies" grouping for Basic.

---

## Unit G - Navigation and page access

| | |
|---|---|
| Owner | unassigned |
| Status | TODO |
| Dependencies | Unit A, Unit E |

**Scope.** A reusable workspace-page capability/access policy, `NavMenu`, direct-route handling,
Projects/Packages/Dependencies availability, Actions availability by CI provider.

Constraint: `NavMenu` renders under the static/SSR layout and never joins a live circuit; it rebuilds
workspace context from `NavigationManager.Uri` on every location change. Do not introduce an interactive
state dependency to hide items.

**Inputs from Wave 1.** Gate the Actions nav item (`NavMenu.razor:83-88`) and route
(`WorkspaceActions.razor:1`) on `UsesCiIntegration`, using `IWorkspaceCapabilitiesResolver` rather than the CI
provider so the SSR nav pulls in no GitHub services. The Actions page's inline "CI is not enabled for this
workspace." fallback (`WorkspaceActions.Loading.cs:25-30`) may stay as defence in depth. Projects, Packages
and Dependencies gate on `DiscoversDotNetProjects` / `UsesDependencyGraph`: a Basic workspace now produces
nothing current for them to show.

**Acceptance.** Basic hides Projects/Packages/Dependencies; .NET Dependency shows them; Actions depends
only on the CI provider; direct navigation cannot bypass the rules; Files remains available for Basic.

---

## Unit J - Host readiness requirements

| | |
|---|---|
| Owner | unassigned |
| Status | IN PROGRESS |
| Dependencies | Unit A (DONE) |

Added by the owner after Wave 1: prompt section 15 (Worker/environment readiness UX) was not assigned to
any unit.

**Scope.** Make host-prerequisite *requirement evaluation* capability-aware without removing host-info
probing. Git is always required; the .NET SDK and GitVersion are required only when some workspace
needs them. A missing .NET SDK or GitVersion must not mark the host, the Worker page, the Home page or the
nav attention indicator as broken when no workspace profile uses them. Code: `HostPrerequisiteState`,
`HostPrerequisiteInstallService`, `HomeNavAttentionMonitor`, `Worker.razor`, `Home.razor`, and the readiness
parts of `WorkspaceService`.

**Acceptance.** Basic + None needs only Git; Basic + GitVersion needs Git plus what the GitVersion path
actually requires; .NET Dependency needs Git, the .NET SDK and GitVersion; probing is unchanged.

---

## Unit H - Workspace create/edit and profile transitions

| | |
|---|---|
| Owner | unassigned |
| Status | TODO |
| Dependencies | Unit A |

**Scope.** The three modal controls (design document section 10), and the transition lifecycle.

Simplicity is a hard requirement: three controls in the existing modal, no settings page, no capability
matrix, no new list badges, no new UI primitives. Type changes are blocked while Features exist, reusing
the existing disabled-field mechanism and wording.

**Input from Wave 1.** Switching a workspace to Basic leaves its previously persisted projects, edges,
levels and generated packages in place, and nothing recomputes or clears them any more, because every
producer is now gated. The .NET to Basic transition must clear them, or every reader must stop reading
them. Switching versioning off leaves `{@Repo}` patterns in place; they are skipped, not failed.

**Acceptance.** An existing workspace opens with .NET Dependency / GitVersion / GitHub Actions selected;
Basic to .NET activates enrichment safely; .NET to Basic cannot leave dependency UI or behaviour active;
versioning toggles handle stale version state; CI toggling does not disturb Git or PR state.

---

## Unit I - Regression and feature assurance

| | |
|---|---|
| Owner | unassigned |
| Status | TODO |
| Dependencies | all |

**Scope.** Verification, not redesign. The existing Feature-context suite is a mandatory gate, and the
owner additionally performs a focused manual end-to-end pass of the main Feature workflow.

Verify: Workspace context; Feature contexts; Feature create, repair, remove; branch switching; parent
branch behaviour; PR create/merge; Git Changes; file versioning; push/pull; Actions when enabled;
dependency update; synchronized push; Projects/Packages/Dependencies pages; context-state isolation.

A design that works for the special Workspace context but breaks Feature worktrees is not acceptable.

**Carried in from Phase 1-2.**

- The four git hooks now disagree about `RepositorySyncNotification.GitVersionFailed`: the rewritten
  commit and push hooks set it honestly, checkout and merge never set it. Harmless today because only
  the pre-`State` fallback path reads it, but make them consistent.
- The post-commit hook's `HasUpstream` changed source, from name-matching against the remote branch list
  to the git-configured upstream. It is a behaviour improvement and removes a network call, but it is a
  behaviour change on a hook path and deserves an explicit manual check.
- The merged-PR projection can be erased by its own sync before the default-tip step reads it (first row
  of the discoveries log). Pre-existing, but verify it with a real Feature and a real merged PR.

**Carried in from Wave 1.**

- Create a Feature in a Basic workspace and confirm its context has no `DependencyLevel`. The Feature-seed
  recompute gate has no automated test.
- Synchronized push for a GitHub Actions workspace still streams run jobs into the push overlay. The run
  watch moved out of `WorkspacePushService` and `RunPushAsync` has no test harness.
- Push-wait GitHub Actions log lines now log under the `GitHubActionsCiProvider` category.

---

## Discoveries log

Architectural findings that changed the design. Newest first.

| Date | Finding | Consequence |
|---|---|---|
| 2026-10-06 | The shared test harness `SyncStateTestContext` seeds its workspace with the model defaults (Basic / None / None), so once the state writer gated projects, five pre-profile tests asserting .NET project persistence failed. | Pre-profile tests that exercise .NET behaviour must opt in with `UseDotNetDependencyProfileAsync()`. The harness default stays Basic because the profile tests depend on it. Later units adding project assertions must do the same. |
| 2026-10-06 | Not every Worker path can be told not to scan: `ReturnToDefaultBranchCommand.cs:99-101` requests GitVersion and projects with no capabilities, and the hook fallback does full enrichment by design. | `WorkspaceRepositoryStateWriter` became the persisted-state gate for project rows (`DiscoversDotNetProjects`). Producer-side gating alone was not enough. Design section 8. |
| 2026-10-06 | Feature seeding (`WorkspaceFeatureOperations.cs:2104`) was a fourth direct caller of the repository's dependency recompute, outside every scope the notes named. | Routed through `WorkspaceStateRecomputeScope.RecomputeDependencyStatsAsync`. That method is now the only sanctioned entry point. |
| 2026-10-06 | Generated-package context scoping is complete for reads but not writes: `WorkspaceFileVersionService.cs:793-794` applies the calling context's missing-file overlay workspace-wide, and `WorkspaceProjectRepository.GeneratedPackages.cs:141-148` writes one context's resolved version into every context. | Pre-existing and profile-independent. Deferred TODO; fixing it means per-context edge versions. |
| 2026-10-06 | GitHub Actions was only ever read or refreshed by the Actions page and synchronized push. The grid query, workspace sync, hook sync and background services never touch it, and the merge dialog's checks row is the PR check-run API (`GitHubService.PullRequests.cs:291`), not Actions. | The CI boundary is two consumers wide. No grid gating is needed beyond the merge dialog's Actions link. Design section 11a. |
| 2026-10-06 | The Actions page used `link.Repository.Connector != null` (`WorkspaceActions.Loading.cs:43,169`) as its "CI enabled" proxy. | It is now only a reachability filter; the CI decision is the resolved provider. |
| 2026-10-06 | `WorkspaceCapabilities` already carried the full .NET capability set (`UsesNuGetPackages`, `UsesDependencyAwareUpdate`, `UsesDependencyAwarePush`, `UsesPackageRestore`, `UsesGeneratedPackagesFromVersionFiles`), but design section 4 listed only six properties. | Section 4 corrected. Unit D dispatches on the existing push/update/restore capabilities rather than adding new ones. |
| 2026-10-05 | The merged-PR default-tip path can erase its own precondition. `PersistVersionsAsync` runs the state writer with `ReconcilePullRequest = true` **before** `ApplyDefaultTipVersionsForMergedFeatureReposAsync` reads `MergedAt`. `WorkspaceRepositoryStateWriter.ReconcilePullRequestAsync` (`:341-359`) upserts on both `Refreshed` and `CacheHit`, so a lookup that legitimately finds no pull request overwrites the merged-PR row with null (`WorkspacePullRequestService.cs:272-273`). Only the `Failed` outcome leaves the projection alone, which is why the step-6 Feature tests must rate-limit the connector to reach the path at all. | Pre-existing and orthogonal to profiles, but it compounds the recorded R3 mismatch and belongs in the Unit I regression pass. |
| 2026-10-05 | `SyncStatusWrite.Derive`'s doc comment (`WorkspaceRepositoryStateWriter.cs:16`) still says "Error without a usable version", but the implementation (`:268-285`) deliberately ignores the version entirely and says so in its own comment. | Actively misleading to Units C-F, which will read that enum looking for version coupling. Fix the comment in Unit C or I. |
| 2026-10-05 | Two more places read an absent `GitVersion` as a problem, both outside Unit B: `WorkspaceProjectRepository.DependencyStats.cs:68-74` and `DependencyLines.cs:308-313` count a null version as an unmatched dependency, which would produce a nonzero unmatched-dependency badge for a Basic workspace out of nothing. | Unit C's gate is "dependency state is not produced at all for Basic", not a display fix. Unit C's notes did not mention these two. |
| 2026-10-05 | `WorkspaceFileVersionService.cs:1046,1080` are the two lines that must implement design section 11's rule that only the default `{@Repo}` GitVersion token requires versioning to be enabled. Otherwise a `{@Repo}` token in a Basic+None workspace compares against a version that will never exist. | Unit C's file-versioning boundary. |
| 2026-10-05 | `WorkspaceCommandRequest` has **no** `WorkspaceId`; three derived types declare their own. Cache warming is therefore an explicit per-type list in `CommandDispatcher.ExecuteAsync` (`CommandDispatcher.cs:102`), not a base-class check. | If a later unit lifts `WorkspaceId` onto the base class, the three derived declarations must be removed or member-hiding warnings break the 0-warning bar. |
| 2026-10-05 | Only `SyncRepositoryRequest`, `CommitSyncRepositoryRequest` and `PushRepositoryRequest` carry both a workspace id and capabilities. `UndoPushRequest`, `FetchCommitsRequest` and `GetGitChangeStatusRequest` have the id but the App does not populate capabilities. **`CreateGitWorktreeRequest` does not derive from `WorkspaceCommandRequest` at all**, so it has no `Capabilities` property. | Units C/D cannot capability-gate worktree creation without first rebasing that request onto the base class. |
| 2026-10-05 | `GetRepositoryVersionResponse` cannot express "not probed" versus "probed and failed", although the Worker has the distinction in `RepositoryVersionResult.Probed`. `ParseGetRepositoryVersionToStatus` (`ResponseParsing.cs:256-264`) therefore returns `VersionMismatch` for every row of a Basic+None workspace. | Step 6 must add that signal to the response DTO; it is the remaining half of the not-applicable version state. |
| 2026-10-05 | `GetGitVersionAtDefaultTipCommand` is registered against the **direct** GitVersion-backed `IRepositoryVersionProvider` singleton (`RunCommandHandler.cs:125`), deliberately the one un-gated call site. | Step 6 gates it App-side in `ApplyDefaultTipVersionsForMergedFeatureReposAsync`, which is cheaper than threading capabilities onto the request and keeps that registration honest. |
| 2026-10-05 | `WriteSyncHooksCoreAsync` (`GitService.cs:1373-1419`) has no version assumptions at all. | Step 5 was genuinely a one-condition fix; nothing else downstream of hook writing cared about the version. |
| 2026-10-05 | `WorkspaceStateRecomputeScope.RecomputeAsync` is invoked unconditionally from `SyncCommandHandler.cs:90` on **every** hook sync, including for a Basic workspace. | A second entry point into dependency recompute that Unit C's notes do not mention. |
| 2026-10-05 | The checkout and merge hooks never set `RepositorySyncNotification.GitVersionFailed`; the rewritten commit and push hooks now do. Harmless today, since `SyncCommandHandler` only reads it on the pre-`State` fallback path (`SyncCommandHandler.cs:184`). | The four hooks are inconsistent with each other. Cleanup candidate for Unit I. |
| 2026-10-05 | The post-commit hook used to derive `HasUpstream` by name-matching against the remote branch list, which needed a connector token and a network call. Converting it onto `RepositoryStateProbe` switched it to the git-configured upstream - the direction the repo already took deliberately elsewhere (`RefreshRepositoryVersionCommand.cs:58` comment). | Post-commit now makes no network call and no longer takes `IWorkerTokenProvider`. A behaviour improvement, but a behaviour change: worth a line in the Unit I regression pass. |
| 2026-10-05 | The hook-install gate is now a **live** defect, not a hypothetical one. `SyncRepositoryCommand.cs:102` still reads `version != "-" && (branch != "-" \|\| currentTag != null)`. Before capability gating the version always resolved, so the gate never fired; a Basic+None workspace now receives **no managed git hooks at all**. | Unit B step 5 is no longer independent of step 2 and must land immediately after it. |
| 2026-10-05 | Only the **notification** path inferred probe markers. The command-response path (`WorkspaceGitService.ResponseParsing.cs:75-99,125-147`) already built honest snapshots, including a `HasProjectsBlock(data)` helper at line 230 that exists precisely to tell an empty project list from an absent one. | Design section 6 overstates the defect's reach. Unit B steps 2-3 needed zero App-side probed-flag work. |
| 2026-10-05 | Four Worker call sites relied on the flat-notification inference, not two: `PushRepositoryCommand.SendPostOperationSyncAsync` and `UndoPushCommand.SendPostResetSyncAsync` as well as the commit and push hooks. | All four now send an explicit `State`. Design section 6's "two live callers" is wrong. |
| 2026-10-05 | `RepositoryStateSnapshot` already carries `GitVersionProbed` and `ProjectsProbed`, so no top-level probed fields were needed on `RepositorySyncNotification`. | Step 1 reduced to "make every current Worker send `State`". `RepositorySyncNotification` is unmodified. |
| 2026-10-05 | Three more consumers assume "a version means health", beyond `IsVersionUnresolved`: the workspace-level `isInSync` rollup in `WorkspaceGitService.SyncAsync` (now gated on `ShouldCalculateVersion`), and `ParseGetRepositoryVersionToStatus` (`ResponseParsing.cs:256-264`), which returns `VersionMismatch` for every repository in a Basic+None workspace. | The second is **not** yet fixed and belongs to Unit B step 6's not-applicable state. |
| 2026-10-05 | The Worker's only `dotnet tool restore` lives inside `GitService.GetVersionAsync` (`GitService.cs:128`) and is reached only after GitVersion already failed. | Gating the version provider removes it for free; there is no second mechanism to gate. Design section 6 lists it as an independent requirement, which is misleading. |
| 2026-10-05 | `CheckoutHookSyncCommand` and `MergeHookSyncCommand` funnel through `RepositoryStateProbe`, but `CommitHookSyncCommand` and `PushHookSyncCommand` call `GetVersionAsync` and `FindAsync` directly and hand-build their snapshots, so they have no single seam for capabilities. | Unit B step 4 should begin by converting those two onto the probe, which also deletes the hand-built snapshots step 1 added. |
| 2026-10-05 | Three GitVersion / project-scan paths remain un-gated and are missing from the design's entry-point table: `ReturnToDefaultBranchCommand.cs:97` (captures with both includes on, passes no capabilities), `CreateBranchCommand.cs:63` (direct `GetVersionAsync`, not routed through the provider), and `RefreshBranchesCommand.cs:48` (uses the probe but requests neither, so harmless). | Must be swept before Phase 2 can be called complete. |
| 2026-10-05 | `SyncRepositoryResponse` has no `state` field, so the App reconstructs the snapshot. `CommitCountsProbed = probed && !onTag` (`ResponseParsing.cs:95`) is therefore true even when the count commands failed and returned nulls, which clears persisted counts. | Pre-existing, not a regression. Candidate for Unit C. |
| 2026-10-05 | Hook scripts are deliberately context-agnostic (`GitService.cs` comment): they send the repo path so the App attributes the context, so no decision state belongs in the script. | Capabilities are pulled by the Worker from the App API, following `WorkerTokenProvider`, instead of being baked into the hook payload. Design section 5. |
| 2026-10-05 | `SyncCommandHandler` infers probe markers; `ProjectsProbed = n.Projects is { Count: > 0 }` cannot distinguish a project-free repository from an unscanned one. `CommitHookSyncCommand` and `PushHookSyncCommand` are the live callers relying on it. | Explicit probed flags became step 1 of Unit B, a precondition for any skipping. Design section 6. |
| 2026-10-05 | `ApplyDefaultTipVersionsForMergedFeatureReposAsync` runs `GetGitVersionAtDefaultTip` after every Feature-context sync and returns early for the special Workspace, so it is invisible when tracing Workspace sync. | Gated on `UsesRepositoryVersioning`, with its own acceptance test. Design section 7. |
| 2026-10-05 | Hook installation is gated on `version != "-"`, so a workspace that resolves no version would never receive hooks. | Gate removed in Unit B step 5. Design section 12. |
| 2026-10-05 | Repository membership changes and name/root edits are already refused while Features exist. | Workspace-type changes adopt the same rule, removing most of the Feature transition problem. Design section 10. |
| 2026-10-05 | `WorkspaceFeature.BaseKind` / `BaseWorkspaceFeatureId` exist but are unused - a reserved Feature hierarchy. | Profile inheritance must not collide with it. Design section 15. |

## Deferred TODOs

| Item | Owner | Notes |
|---|---|---|
| Fold shipped behaviour into `docs/architecture/` 01-06 | owner | After Phase 6. `docs/architecture/README.md` keeps current-state docs authoritative; this folder is the proposal record, mirroring `docs/worktree/`. |
| Generated-package write scoping across contexts | owner decision | Verified by Unit C: complete for reads, not writes (discoveries log, 2026-10-06). Pre-existing and profile-independent, so a follow-up rather than part of this change unless the owner decides otherwise. |
| `GrayMoon.App/Services/Git/GitVersionCommandService.cs` appears unused | - | Confirmed callerless. Left untouched; removal is out of scope. |
| `GitHubActionsService.GetLatestActionsAsync` / `GetLatestActionAsync` appear unused | - | Found by Unit E. Left untouched; removal is out of scope. |
| Desktop README "Recent GrayMoon changes" entry | Unit H | Phase 1-2 has no user-visible change; the entry belongs with the UX work. |
