# GrayMoon Worktree Features — Dependency-Update & Cross-Cutting Wiring Gaps (2026-09-21)

**Question this document answers:** "Were dependency updates in worktrees (Features) missed in implementation?"
**Answer: Yes — substantially.** The physical worktree layer (Git, Agent commands, path resolution) and the
DB schema for Feature contexts are in place, and several read/write seams (Repositories grid overlay, Actions,
parts of PR persistence) have been migrated to be context-aware. But the **dependency graph / dependency-level /
dependency-update computation pipeline — the single most central piece of GrayMoon's "multi-repository
intelligence" that §49 of the detailed design calls the actual success criterion — is still entirely
Workspace-only.** For a Feature, dependency-update, push planning, and version-mismatch detection either
silently use the special Workspace's data or actively corrupt cross-context data.

**Reviewed against:**
- `GrayMoon-Worktree-Features-Detailed-Implementation-Design-v3.md` (source of truth for what *should* exist)
- `GrayMoon-Workspace-Current-Features-Baseline-Appendix.md` (behavior that must not regress)
- `GrayMoon/docs/architecture/*` (current system architecture)
- Live code on branch `worktree` (HEAD `be4347f`, 2 commits ahead of `origin/worktree`)

This is a fresh code audit, not a copy of `GrayMoon-Worktree-Features-Code-Review-Findings-2026-09-21.md`. Some
items that document flagged as "not implemented" (Actions context-awareness, some PR persistence) have since
been implemented on this branch; this document reflects what the code actually does *right now*, with exact
file/line evidence, and adds several gaps that document did not cover (the dependency-graph corruption below is
the most severe of these and was not previously documented).

---

## 0. Status update (2026-09-21, later same day)

Fix-sequence items **1–4 are done**, plus item **5** (grid sort/keyset/level-grouping). Commit `c24f892`
(items 1–4) plus an uncommitted follow-up (item 5) on top of the `be4347f` HEAD this document was audited
against. Full solution build is clean; the full test suite passes (320 tests, up from 319 — one new test added
for item 5).

- **Item 1 (stop the corruption):** `RecomputeAndPersistRepositoryDependencyStatsAsync` now takes a
  `WorkspaceFeatureContextId`, computes `WorkspaceProjects`/levels/unmatched-deps scoped to that context, and
  persists onto `WorkspaceRepositoryContextState` (get-or-create per repo). It mirrors onto the shared
  `WorkspaceRepositoryLink` only when the context is the special Workspace. §2.1/§2.2's cross-contamination is
  closed: a Feature's dependency sync can no longer overwrite the Workspace's own Dependencies grid, and a
  Feature now gets its own `DependencyLevel`/`Dependencies`/`UnmatchedDeps`.
- **Item 2 (context-scope the payload builders) — done for the data layer, not yet for the UI/interface
  threading above it:** `GetSyncDependenciesPayloadAsync`/`GetPushPlanPayloadAsync`/
  `GetPushDependencyInfoForRepo*` have context-aware overloads that read `GitVersion`/`DependencyLevel` from
  `WorkspaceRepositoryContextState` (falling back to the link only for the special Workspace) and filter
  `WorkspaceProjects` by context. All internal call sites in `WorkspaceGitService`/`WorkspacePushService` that
  already had a `contextId` in scope were switched to the new overloads. **Not yet done:** threading a
  `contextId` up through `IWorkspacePushOperations` and the Razor push pages/dialogs that call it — see the
  "not yet done" list below.
- **Item 3 (thread `contextId` through `GetUpdatePlanAsync`):** done. `IWorkspaceUpdateOperations.GetUpdatePlanAsync`
  now takes a `contextId`, threaded through `WorkspaceUpdateOperations`, `WorkspaceGitService.GetUpdatePlanAsync`,
  `DependencyUpdateOrchestrator`, and the three call sites in `WorkspaceRepositories.Update.cs`/
  `WorkspaceRepositories.Dependencies.cs`.
- **Item 4 (`UpdateProjectDependencyVersionsAsync` key collision):** done. Now keyed on
  `(ContextId, RepositoryId, ProjectFilePath)` instead of `(RepositoryId, ProjectFilePath)` alone.
- **Also fixed along the way (the §2.5 asymmetry):** `MergeWorkspaceProjectDependenciesAsync`'s calls from
  `WorkspaceGitService.RefreshWorkspaceProjectsAsync`/`RefreshSingleRepositoryProjectsAsync` were dropping the
  `contextId` they already had in scope; both now pass it through.
- **Item 5 (migrate grid sort/keyset/level-grouping) — done.** `ApplySort`/`ApplyKeyset` in
  `WorkspaceRepositoryLinkListQueryService` now take `contextId`/`isSpecialWorkspace` and, for a Feature
  context, order/paginate by a correlated lookup into that repo's `WorkspaceRepositoryContextState` row instead
  of the shared link's `DependencyLevel`/`RepositoryType`/`Dependencies` (falling back to the link only for the
  special Workspace, same rule as `Project(...)`). `GetIndexAsync` (virtual-scroll ordering + level-header
  grouping), `GetRepositoryIdsAtLevelAsync` ("jump to level" / bulk level actions), and
  `GetGitVersionNameMapAsync` (version-token tooltip lookups) all gained the same `contextId`/`isSpecialWorkspace`
  parameters and now branch the same way; all call sites in `WorkspaceRepositories.Loading.cs`/`.State.cs`
  that already had `_selectedContextId`/`_isFeatureContext` in scope were updated to pass them through. This
  closes the §3.1 gap: the grid can no longer display one dependency level (via `Project`) while
  sorting/grouping by a different one (via the unmigrated `ApplySort`/`ApplyKeyset`).
  - Fixed a related, pre-existing latent bug surfaced by the new test:
    `GetGitVersionNameMapAsync`'s `RepositoryName → GitVersion` map used a plain `ToDictionary`, which throws
    if two repositories in the same workspace share a display name (a realistic case, not exercised by any
    prior test). Extracted a shared `ToNameVersionMap` helper that keeps the first match per name instead of
    throwing, used by both the special-Workspace and context-scoped branches.
- **Verification:** full solution builds cleanly; full test suite (320 tests) passes, including a new test
  (`Feature_context_sort_keyset_and_level_grouping_use_context_state_not_shared_link`) that seeds a Feature
  context whose `WorkspaceRepositoryContextState` rows are the deliberate inverse of the shared link's fields,
  to catch any code path that silently falls back to reading the link.

- **Item 6 (wire PR polling/Create-PR refresh to context) — done.** `WorkspaceRepositories.PrPolling.cs`'s
  background poll and `WorkspaceRepositories.PullRequests.cs`'s post-Create-PR refresh both previously called
  the legacy `WorkspacePullRequestService.RefreshPullRequestsAsync(workspaceId, repositoryIds, ...)`
  unconditionally, which reads the shared link's branch and writes the shared/legacy PR row regardless of which
  context the page was viewing. Added `RefreshPullRequestsForContextAsync` (in `WorkspaceRepositories.PullRequests.cs`)
  that branches on `_isFeatureContext`/`_selectedContextId` the same way `WorkspaceActions.Loading.cs` already
  does: for the special Workspace it still calls the legacy overload; for a Feature it looks up that context's
  own checked-out branch per repo via `LinkListQueryService.GetAllSnapshotsAsync(..., isSpecialWorkspace: false)`
  and calls `WorkspacePullRequestService.RefreshContextPullRequestsAsync`, which persists into
  `WorkspaceRepositoryContextPullRequest` only. Both call sites now go through this helper. The redundant
  `prByRepositoryId = freshPrs` snapshot after Create-PR was also removed — the immediately-following
  `RefreshFromSync()` already re-reads the grid through the context-aware `Project(...)` overlay, so a
  Feature's freshly created PR badge now actually appears instead of silently never populating (§3.2).
  **Not covered by this fix:** `WorkspaceRepositoryStateWriter`'s own reconciliation path was already
  context-aware before this change (see `ReconcilePullRequestAsync`) — this item only closes the two page-level
  read paths that bypassed it. Verified: full solution builds cleanly; full test suite (177 + 320 + 134 = 631
  tests) passes unchanged (no new persistence behavior was added — `RefreshContextPullRequestsAsync` already
  existed and is exercised by `WorkspacePullRequestServiceTests.cs`).

- **Item 2 remainder (thread `contextId` through `IWorkspacePushOperations`/Razor push pages) — done.**
  `IWorkspacePushOperations.GetPlanAsync`/`GetRepositoryIdsNeedingPushAsync` now take a `WorkspaceFeatureContextId
  contextId` parameter (matching the sibling methods on the same interface), threaded through
  `WorkspacePushOperations`, `WorkspacePushHandler.GetPushPlanAsync` (which now calls a new context-aware
  overload of `WorkspacePushService.GetPushPlanAsync`), and every call site that already had a context in
  scope: `WorkspaceRepositories.Push.cs` (`OnPushClickAsync`, `PushBadgeClickCoreAsync`, `BuildPushPlanAsync` -
  the latter also used by `WorkspaceRepositories.Update.cs`/`.PrepareWorkspace.cs`),
  `WorkspaceActionNotificationPanel.razor` (both push-badge/plan-building call sites, using the same
  `ResolveOperationContextAsync` the panel's own `PushAsync`/`PushSingleAsync` calls already resolved), and
  `NewPullRequestModal.razor` (gained a new `ContextId` parameter, wired from
  `WorkspaceRepositories.razor`'s `RequireSelectedContextId()`).
  Added `WorkspaceRepository.GetRepositoryIdsNeedingPushAsync(workspaceId, contextId, repositoryIds, ...)`: for a
  Feature it reads OutgoingCommits/BranchHasUpstream/CheckedOutTag off that repo's own
  `WorkspaceRepositoryContextState` row (falling back to the shared link only for the special Workspace, same
  rule as `WorkspaceProjectRepository.GetContextVersionAndLevelByRepoAsync`), replacing the old
  `workspace.Repositories`-only (shared-link) computation in both `WorkspacePushOperations` and
  `WorkspacePushHandler.GetPushPlanAsync`. A Feature's Push button / not-upstreamed badge / bulk Create-PR
  push-first flow now decides "does this repo need a push" and "what level is it at" from that Feature's own
  state, not the Workspace's.
  **Known remaining asymmetry, out of scope for this item:** `WorkspaceProjectRepository.GetPushPlanPayloadAsync`'s
  tag-pinned-repo exclusion still reads the shared `WorkspaceRepositoryLink.CheckedOutTag` rather than the
  context's own `CheckedOutTag`, and `WorkspaceActionNotificationPanel.razor`'s `repoIdsThatNeedPush` (used only
  to decide whether to *show the sync-push modal*, not which repos actually get pushed) is still built from
  `WorkspaceRepository.GetByIdAsync`'s shared links rather than a context-aware read - the notification panel is
  workspace-level UI that has not otherwise been made context-aware end-to-end, and doing so is a larger,
  separate piece of work.
  Verified: full solution builds cleanly; full test suite (177 + 320 + 134 = 631 tests) passes unchanged (no
  behavior change for the special Workspace - the new parameters resolve to the same legacy code path when
  `contextId` is the special Workspace context).
- **Item 9 (§31 repository-membership guard) — verified already implemented, not a gap.** Contrary to §3.6's
  "not verified as enforced" flag, `WorkspaceRepository.AddRepositoriesAsync` and `.ReplaceRepositoriesAsync`
  (called by `UpdateAsync`) both already throw `InvalidOperationException("Cannot add/change Workspace
  repository membership while Features exist. Remove Features first.")` when
  `dbContext.WorkspaceFeatures.AnyAsync(f => f.WorkspaceId == workspaceId)` is true. No code change was needed;
  §3.6 and the table in §1 are corrected below.

- **Item 8 (§28A/§28B branch dialog worktree awareness + external worktree cleanup) — §28A found already
  substantially implemented; §28B wired in this pass.** Contrary to §3.4/§3.5, `SwitchBranchModal.razor` (the
  per-repository branch switcher) already injects `IWorkspaceBranchOccupancyService` and renders "Feature"/
  "Worktree" occupancy badges, blocks checkout onto an occupied branch (`IsCheckoutDisabled`), and routes a
  Feature-owned branch's delete icon to `RequestFeatureCleanup` → `OnRequestFeatureCleanup` (opens
  `RemoveFeatureModal`) instead of attempting an ordinary `git branch -d` that worktree rules would reject -
  this is §28A. What was still missing (§28B): the "Worktree" (external, non-Feature) badge had no
  corresponding cleanup action - its delete icon fell through to the same ordinary-delete button as an
  unoccupied branch, which would fail with git's "already checked out" error. Added a third branch alongside
  the existing Feature-cleanup icon: for `RequiresExternalCleanup` badges, a new delete-icon button now calls
  `RequestExternalWorktreeCleanupAsync`, which analyzes the worktree via the already-existing
  `IWorkspaceExternalWorktreeOperations.AnalyzeExternalWorktreeCleanupAsync` (registered in DI, previously never
  called from any UI) and shows an inline confirm (dirty-worktree double-confirm, same shape as the existing
  force-delete-branch confirm) before calling `RemoveExternalWorktreeAsync`.
  **Known remaining gap, flagged rather than fixed:** the separate bulk `BranchModal.razor` ("New Branch"/
  "Switch Branch" tabs, which switch *every* repository in the workspace to a common branch name at once) has
  no occupancy awareness at all - it has no per-repo branch list to attach a badge to, so a bulk switch can
  still hit "already checked out" mid-run for whichever repo has an occupied worktree. Giving the bulk dialog
  the same awareness is a larger redesign (surfacing per-repo occupancy in an aggregate view) and was left as a
  follow-up rather than attempted here.
  Verified: full solution builds cleanly; full test suite (177 + 320 + 134 = 631 tests) passes unchanged (no
  automated coverage exists for this Blazor-component interaction; manual testing against a live workspace with
  an external `git worktree add` is recommended before relying on this).

- **Item 7 (`WorkspaceFileContextState` for `IsMissingOnDisk`) — done.** The schema (`WorkspaceFileContextState`
  model, `WorkspaceFileContextStates` table/migration, `AppDbContext` `DbSet`, and a backfill routine) already
  existed but nothing read or wrote it - `WorkspaceFileVersionService.CheckAndPersistFileVersionStatusCoreAsync`
  mutated the shared `WorkspaceFile.IsMissingOnDisk` directly and unconditionally, and every decision point that
  gated on "is this file missing" (`UpdateAllVersionsAsync`, `SyncGeneratedPackageDependenciesAsync`, the
  file-config dependency-edge builder, the OK-badge tooltip lines) read that same shared flag with no context
  filter - the exact "context-scoped write, workspace-scoped read" shape AGENTS.md's Feature-context-scoping
  section calls out.
  - **Write path:** `CheckAndPersistFileVersionStatusCoreAsync` now get-or-creates a
    `WorkspaceFileContextState` row keyed on `(contextId, FileId)` and writes the freshly-checked
    `IsMissingOnDisk` there; it only mirrors onto the shared `WorkspaceFile.IsMissingOnDisk` when the context is
    the special Workspace.
  - **Read path:** added `WorkspaceFileVersionService.GetMissingFlagsByFileIdAsync(workspaceId, contextId, ...)`
    (public) and `ApplyMissingFlagOverlay(configs, flags)`, following the same "context row, else shared row only
    for special Workspace, else null" rule as `WorkspaceProjectRepository.GetContextVersionAndLevelByRepoAsync`.
    Applied at the top of every method in `WorkspaceFileVersionService` that reads `versionConfigRepository
    .GetByWorkspaceIdAsync` and already had (or gained) a `contextId` parameter: `UpdateAllVersionsAsync`,
    `CheckAndPersistFileVersionStatusCoreAsync` itself, `SyncGeneratedPackageDependenciesAsync`, and the new
    `contextId` parameter added to `GetAllFileVersionLinesByRepoAsync`/`GetAllFileVersionLinesForRepoAsync`
    (the latter's only call site, `WorkspaceRepositories.Loading.cs`'s tooltip loader, already had
    `_selectedContextId` in scope). Also added the same `contextId`-scoped overlay to
    `WorkspaceProjectRepository.BuildRepoDependencyEdgeSetsAsync`'s file-config-edge missing check (used by
    `RecomputeAndPersistRepositoryDependencyStatsAsync`, which already had a context in scope) via a new
    optional `workspaceFeatureContextId` parameter - existing dependency-graph-*visualization* callers
    (`GetRepositoryDependencyGraphAsync`, `LoadWorkspaceRepoDependencyGraphAsync`) were left on the legacy,
    unparameterized overload rather than migrated, since neither had a context available and changing that is a
    separate, larger piece of work (see below).
  - **Same-class fix folded in:** `GetMismatchedFileVersionLinesByRepoAsync`/`ForRepoAsync` and
    `GetFileLineStatusByWorkspaceAsync`/`ForRepoAsync` read `WorkspaceFileLineStatuses` filtered only by
    `WorkspaceId` despite that table already carrying its own `WorkspaceFeatureContextId` column and the write
    side already stamping it correctly - so a Feature's out-of-date-line badge/tooltip was silently mixing in
    the Workspace's (and every other Feature's) stale-line rows. All four now take a `contextId` and filter by
    it; the one live caller (`WorkspaceRepositories.Loading.cs`'s tooltip loader) was updated to pass
    `RequireSelectedContextId()`.
  - **Page-level fix:** `WorkspaceFiles.razor` (the Files page, already Feature-context-aware for everything
    else on the page) loaded its file list's `IsMissingOnDisk` straight off `WorkspaceFileRepository
    .GetByWorkspaceIdAsync` (the shared row) with no context overlay at all; it now calls the new
    `GetMissingFlagsByFileIdAsync` and overlays per-file.
  **Known remaining gap, flagged rather than fixed:** `IWorkspaceFileOperations.ListAsync` (used only by the
  `GET /api/workspaces/{id}/files` REST endpoint, not by any Blazor page) and the dependency-graph
  *visualization* methods noted above remain on the legacy special-Workspace-only path - neither had a context
  parameter to begin with, and adding one is a public-API/visualization-feature change orthogonal to this fix.
  Verified: full solution builds cleanly; full test suite (177 + 320 + 134 = 631 tests) passes (one
  `GitChangesLineStatsRefreshTests` timing test flaked once on a full run and passed cleanly on immediate
  re-run in isolation and as part of a second full run - confirmed pre-existing flakiness unrelated to this
  change, not a regression). No new automated test was added for this item (the service requires a
  fuller agent-bridge/DB test harness than existed for it); manual verification against a live workspace with
  a Feature whose worktree is missing a configured version file is recommended before relying on this.

**Not yet done:** generated-package context-scoping (`SyncGeneratedPackageDependenciesAsync` still isn't
context-scoped; the `WorkspaceProjectRepositoryGeneratedPackageTests` seed data and the new-context filter both
currently carve out generated/virtual package rows rather than scoping them), the `IWorkspaceFileOperations
.ListAsync`/dependency-graph-visualization gap noted under item 7, and the `BranchModal.razor` bulk-switch gap
noted under item 8. See the table in §1 and the fix sequence in §5 below for what's left; the summary and
evidence in §§1–4 otherwise still describe the code as it stood *before* this update and should be read with the
corrections above in mind.

---

## 1. Executive summary — what is done vs. not done

| Layer | Status | Evidence |
|---|---|---|
| Schema: `WorkspaceFeature`, `WorkspaceFeatureContext`, `WorkspaceFeatureRepository`, `WorkspaceRepositoryContextState`, context PR/Action/GitChanges/File tables | **Done** | `src/GrayMoon.App/Models/Features/*`, `AppDbContext.Features.cs` |
| Feature lifecycle (create/remove/reconcile worktrees) | **Done** (per design §25–§28) | `WorkspaceFeatureOperations.cs` |
| Context/path resolvers, hook attribution (worktree-safe) | **Done** | `WorkspaceFeatureContextResolver.cs`, `WorkspaceContextPathResolver.cs`, `WorkspaceHookContextAttributor.cs`, `GitService.WriteSyncHooksAsync` |
| Hierarchical operation locking (Workspace-structural vs. per-context) | **Done** (§21) | `WorkspaceOperationRunner.cs` (`TryStartContext`, `_contextByContextId`) |
| Repositories grid **display** overlay for Feature context | **Done** (recently) | `WorkspaceRepositoryLinkListQueryService.Project(...)` overlays `WorkspaceRepositoryContextState` / `WorkspaceRepositoryContextPullRequest` when `isSpecialWorkspace == false` |
| Repositories grid **sort / keyset paging / "group by level"** | **Done** (§0, item 5) | `ApplySort`/`ApplyKeyset`/`GetIndexAsync`/`GetRepositoryIdsAtLevelAsync`/`GetGitVersionNameMapAsync` in `WorkspaceRepositoryLinkListQueryService` now join `WorkspaceRepositoryContextState` for a Feature context (see §3.1, superseded) |
| Git Changes persistence | **Done** (context-scoped tables + mirror-on-special-only pattern) | `GitChangesSnapshotPushHandler.cs` |
| GitHub Actions read/write | **Done at the service layer**, wired into the Actions page | `WorkspaceActionService.FetchAndPersistContextAsync/GetPersistedActionsForWorkspaceContextAsync`, called from `WorkspaceActions.Loading.cs` / `WorkspaceActions.AutoRefresh.cs` |
| PR **persistence infrastructure** (`WorkspaceRepositoryContextPullRequest`, `UpsertContextAsync`, `RefreshContextPullRequestsAsync`) | **Done, and now called from the page** (§0, item 6) | See §4 |
| PR **polling / Create-PR flow** | **Done** (§0, item 6) — both routed through context | `WorkspaceRepositories.PrPolling.cs`, `WorkspaceRepositories.PullRequests.cs` |
| **Dependency graph / dependency level / dependency stats** | **Done** (§0, items 1 & 4) — no longer cross-contaminates contexts | See §2 — the core finding of this document (superseded by §0) |
| **Update / SyncDependencies / Push planning** | **Done** (§0, items 2 & 3; item-2 remainder) — data layer and `IWorkspacePushOperations`/Razor push pages are all context-aware now | See §2 (superseded by §0) |
| File-version missing-state / line-status mismatch | **Done** (§0, item 7) | See §3.3 (superseded by §0) |
| Branch dialog worktree awareness (§28A) | **Done, found already implemented** (§0, item 8) | `SwitchBranchModal.razor` + `WorkspaceBranchOccupancyService` (per-repo dialog only; bulk `BranchModal.razor` not covered) |
| External worktree cleanup (§28B) | **Done** (§0, item 8) | `SwitchBranchModal.razor` now calls `IWorkspaceExternalWorktreeOperations` |
| Repository membership guard while Features exist (§31) | **Done, found already implemented** (§0, item 9) | `WorkspaceRepository.AddRepositoriesAsync`/`.ReplaceRepositoriesAsync` |

---

## 2. The core gap: dependency graph / update / push planning ignore Feature context entirely

This is the direct answer to "were dependency updates in worktrees missed." Everything below reads/writes
`WorkspaceProjects`, `ProjectDependencies`, and the dependency-derived fields, filtered **only by `WorkspaceId`**,
even though `WorkspaceProject` has carried a `WorkspaceFeatureContextId` column since the schema migration and
some call sites (project *merge*) already pass it correctly.

### 2.1 Reading projects mixes every context's projects into one graph

```194:213:src/GrayMoon.App/Repositories/WorkspaceProjectRepository.DependencyStats.cs
    public async Task RecomputeAndPersistRepositoryDependencyStatsAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var workspaceProjects = await dbContext.WorkspaceProjects
            .AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);
```

No `WorkspaceFeatureContextId` filter. As soon as **one Feature exists and has run a project scan**, this query
returns the special Workspace's projects **and** every Feature's projects for the same repos, all merged into a
single project/edge set. `ProjectName`/`PackageId` collisions across contexts (which are the *normal* case — a
Feature's `MyLib.csproj` has the same `ProjectName` as the Workspace's) get folded together via
`packageNameToProjectId`/`byProject` dictionaries keyed only by name, silently picking whichever context's row
happened to win the dictionary insert. This directly contradicts the "context-scoped project graph" requirement
in design §15/§15.1/§15.2 and the required search-gate in §37.2.

### 2.2 The computed level/dependency-count/unmatched-deps is written onto the *shared* link, never onto context state

```69:83:src/GrayMoon.App/Repositories/WorkspaceProjectRepository.cs
    public async Task<List<WorkspaceProject>> GetByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        return await dbContext.WorkspaceProjects
            .AsNoTracking()
            .Include(p => p.Repository)
            .Where(p => p.WorkspaceId == workspaceId)
```

```125:132:src/GrayMoon.App/Repositories/WorkspaceProjectRepository.DependencyStats.cs
        foreach (var link in links)
        {
            link.DependencyLevel = dependencyLevelForRepo(link.RepositoryId);
            link.Dependencies = depCountByRepo.GetValueOrDefault(link.RepositoryId, 0);
            link.UnmatchedDeps = unmatchedCountByRepo.GetValueOrDefault(link.RepositoryId, 0);
        }
```

`WorkspaceRepositoryContextState.DependencyLevel/Dependencies/UnmatchedDeps` exist in the schema specifically so
each context has its own answer (design §6.4). This method never touches that table — it recomputes from the
**cross-context-polluted** project/edge set from §2.1 and writes the single result onto
`WorkspaceRepositoryLink`, i.e. the special Workspace's row. Consequences:

- **Any Feature's dependency sync corrupts the Workspace's own Dependencies page.** Running Update inside a
  Feature calls `MergeWorkspaceProjectDependenciesAsync(..., persistDependencyLevel: true, ...)` →
  `RecomputeAndPersistRepositoryDependencyStatsAsync(workspaceId, ...)`, which recomputes levels from the merged
  graph and overwrites `WorkspaceRepositoryLink.DependencyLevel`/`.Dependencies`/`.UnmatchedDeps` — the exact
  fields the (unmigrated) grid sort/keyset/level-grouping and the special-Workspace header badges read.
- **A Feature never gets its own dependency level.** `WorkspaceRepositoryContextState.DependencyLevel` is
  defined but nothing ever writes it.
- This is *worse* than a missing feature — it is a regression risk for the Workspace baseline the moment any
  Feature exists and is synced, which the design's Wave-9 "critical Workspace regression gate" (§36) and the
  hard invariant in §2 of the design ("no Feature exists → GrayMoon is behaviorally indistinguishable from
  current GM") were specifically written to prevent.

### 2.3 `GetSyncDependenciesPayloadAsync` / `GetPushPlanPayloadAsync` — same story, plus they read the link's `GitVersion`, not context state

```8:22:src/GrayMoon.App/Repositories/WorkspaceProjectRepository.Push.cs
    public async Task<List<SyncDependenciesRepoPayload>> GetSyncDependenciesPayloadAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var versionByRepoId = await dbContext.WorkspaceRepositories
            .AsNoTracking()
            .Where(wr => wr.WorkspaceId == workspaceId)
            .Select(wr => new { wr.RepositoryId, wr.GitVersion })
            .ToListAsync(cancellationToken);
        ...
        var projects = await dbContext.WorkspaceProjects
            .AsNoTracking()
            .Include(p => p.Repository)
            .Where(p => p.WorkspaceId == workspaceId)
```

This computes "which package references are mismatched and what they should become" using:
- `WorkspaceRepositoryLink.GitVersion` — the special Workspace's checked-out version, **not**
  `WorkspaceRepositoryContextState.GitVersion` (the Feature's own checked-out version, which per design §6.4 is
  the only correct source once a repo has a context-specific checkout). If a Feature's repo is on a different
  commit/tag than the Workspace, the "current version of the referenced package" used to compute the diff is
  simply wrong.
- the same cross-context-polluted `WorkspaceProjects` set as §2.1.

`GetPushPlanPayloadAsync` / `GetPushDependencyInfoForRepoAsync` / `GetPushDependencyInfoForRepoSetAsync`
(`WorkspaceProjectRepository.Push.cs`) repeat the identical pattern for push planning: `WorkspaceId`-only project
reads, `WorkspaceRepositoryLink.DependencyLevel` for level ordering. Design §30 explicitly requires
"dependency-aware push planning uses selected context's graph" — this is not the case.

### 2.4 The API surface never had a context parameter to begin with

```1:14:src/GrayMoon.Application/IWorkspaceUpdateOperations.cs
    Task<(IReadOnlyList<SyncDependenciesRepoPayload> Payload, bool IsMultiLevel)> GetUpdatePlanAsync(
        int workspaceId,
        IReadOnlySet<int>? repositoryIds = null,
        CancellationToken cancellationToken = default);
```

`GetUpdatePlanAsync` — used to decide *whether the Update button has anything to do and at how many levels* —
takes no context id, unlike every other method on the same interface (`UpdateAsync`,
`RestorePackagesAsync`, `UpdateSingleRepositoryAsync`, `RecomputeAndBroadcastWorkspaceSyncedAsync`, which do take
`WorkspaceFeatureContextId contextId`). The implementation forwards straight through:

```10:14:src/GrayMoon.App/Services/Application/WorkspaceUpdateOperations.cs
    public Task<(IReadOnlyList<SyncDependenciesRepoPayload> Payload, bool IsMultiLevel)> GetUpdatePlanAsync(...)
        => workspaceGitService.GetUpdatePlanAsync(workspaceId, repositoryIds, cancellationToken);
```

This means **the contextId parameter that `DependencyUpdateOrchestrator.RunAsync` and
`WorkspaceGitService.SyncDependenciesAsync`/`RefreshWorkspaceProjectsAsync` do carry** (correctly, for physical
Agent path resolution — see §2.5) is dropped on the floor the moment execution reaches the layer that decides
*what* is out of date and *at which dependency level*. The plumbing was half-done: a `contextId` parameter was
added to the orchestration/physical-I/O layer but the underlying data layer it calls into
(`WorkspaceProjectRepository.*`) was never migrated to accept or filter by it.

### 2.5 What actually *is* correct: physical file operations target the right worktree

To be precise about the boundary of the bug — this is **not** a bug in path resolution. `RefreshWorkspaceProjectsAsync`
and `SyncDependenciesAsync` both call `ResolveAgentPathArgsAsync(workspaceId, contextId, ...)` →
`IWorkspaceContextPathResolver.GetAgentWorkspaceArgsAsync(contextId, ...)` before talking to the Agent, so the
`.csproj` files are genuinely read from and written to the Feature's own worktree, not the Workspace's. The bug is
entirely in the **data layer above the physical layer**: what gets merged, how levels are computed, and where the
computed level/version-mismatch answer is persisted.

```59:94:src/GrayMoon.App/Services/Git/WorkspaceGitService.Projects.cs
        var (workspaceRoot, workspaceFolderName) = await ResolveAgentPathArgsAsync(workspace.WorkspaceId, contextId, cancellationToken);
        var syncResults = await Task.WhenAll(repos.Select(async repo => { ... }));
        ...
        if (repoProjectsToMerge.Count > 0)
            await _workspaceProjectRepository.MergeWorkspaceProjectsBatchAsync(workspaceId, repoProjectsToMerge, contextId.Value, cancellationToken);
        ...
        await _workspaceProjectRepository.MergeWorkspaceProjectDependenciesAsync(workspaceId, resultsForDeps, persistDependencyLevel: true, cancellationToken);
```

Note the asymmetry on the last two lines: the **project merge** call correctly passes `contextId.Value`
(and `MergeWorkspaceProjectsAsync`/`MergeWorkspaceProjectsBatchAsync` in `WorkspaceProjectRepository.Merge.cs`
correctly filter/write `WorkspaceFeatureContextId`) — but the very next call,
**`MergeWorkspaceProjectDependenciesAsync`, drops the contextId** and falls back to its `ResolveSpecialWorkspaceContextIdAsync`
overload internally recomputing stats workspace-wide (§2.2). So a Feature's project *rows* land in the right
context, but the *dependency graph built from them* does not.

### 2.6 `UpdateProjectDependencyVersionsAsync` can write into another context's rows

```262:279:src/GrayMoon.App/Repositories/WorkspaceProjectRepository.Merge.cs
    public async Task UpdateProjectDependencyVersionsAsync(
        int workspaceId,
        IReadOnlyList<(int RepoId, string ProjectPath, string PackageId, string NewVersion)> updates,
        CancellationToken cancellationToken = default)
    {
        ...
        var projects = await dbContext.WorkspaceProjects
            .Where(p => p.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);
        ...
        var dependentKeyToProjectId = projects
            .Where(p => !string.IsNullOrWhiteSpace(p.ProjectFilePath))
            .GroupBy(p => (p.RepositoryId, ProjectPath: p.ProjectFilePath.Trim().ToLowerInvariant()))
            .ToDictionary(g => g.Key, g => g.First().ProjectId);
```

Keyed by `(RepositoryId, ProjectFilePath)` only. If the Workspace and a Feature both have a project at the same
relative path in the same repository (the normal case — that is the whole point of a worktree), `GroupBy(...).First()`
picks one of them arbitrarily and `UpdateProjectDependencyVersionsAsync` (called after every
`SyncDependenciesAsync`) may persist the new dependency version onto the **wrong context's** `ProjectDependency`
row.

### 2.7 Net effect for the user

- Running **Update** inside a Feature: physically edits the Feature's own `.csproj` files (correct), but the
  "what needs updating and to which version" decision is computed from a graph that mixes Workspace + every
  Feature's projects, using the Workspace's `GitVersion`, and the resulting level/count/unmatched numbers are
  written onto the Workspace's shared row — visibly changing the Workspace's own Dependencies grid.
- The **Feature's own** `WorkspaceRepositoryContextState.DependencyLevel/Dependencies/UnmatchedDeps` are never
  populated, so any future context-aware reader (including the grid's own `Project(...)` overlay, §3.1) will
  show `null`/no badge for a Feature's dependency state even after the display layer is fully migrated.
- **Push planning** for a Feature uses the Workspace's dependency levels, so a synchronized push inside a
  Feature could sequence repositories incorrectly relative to the Feature's own actual code state.

---

## 3. Other confirmed wiring gaps for Features/worktrees

### 3.1 Repositories grid sort, keyset paging, and "group by level" still read the shared link — **fixed, see §0 item 5**

`WorkspaceRepositoryLinkListQueryService.Project(...)` (the row **projection**) is context-aware (see §1), but:

```314:324:src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs
    private static IQueryable<WorkspaceRepositoryLink> ApplySort(IQueryable<WorkspaceRepositoryLink> query) =>
        query
            .OrderByDescending(wr => wr.DependencyLevel ?? int.MinValue)
            ...
            .ThenByDescending(wr => wr.Dependencies ?? int.MinValue)
```

`ApplySort`, `ApplyKeyset` (same file), `GetRepositoryIdsAtLevelAsync` (used for "jump to level" / grouping), and
`GetGitVersionNameMapAsync` all still read `wr.DependencyLevel`/`wr.Dependencies`/`wr.GitVersion` straight off the
link. For a Feature context this means: the grid can *display* one number (from context state, once §2 is fixed)
while *sorting/grouping* by a different, wrong number (the Workspace's). This is a direct instance of the
required search-gate in design §37.2 that was not fully closed.

### 3.2 PR polling and "Create PRs" are not routed through the context-aware PR service at all

The context-aware PR write path exists and is correct:

```181:255:src/GrayMoon.App/Services/Workspaces/WorkspacePullRequestService.cs
    public Task ClearContextPullRequestAsync(int contextId, int workspaceRepositoryId, ...) => ...
    public async Task RefreshContextPullRequestsAsync(int workspaceId, int contextId, IReadOnlyDictionary<int, string?> branchByRepositoryId, ...)
```

But nothing calls it. Both call sites that refresh PR state from the Repositories page call the **legacy,
non-context** overload unconditionally:

```80:87:src/GrayMoon.App/Components/Pages/WorkspaceRepositories.PrPolling.cs
                await WorkspacePageService.WorkspacePullRequestService.RefreshPullRequestsAsync(
                    WorkspaceId, repositoryIds, cancellationToken: cancellationToken);
```

```258:266:src/GrayMoon.App/Components/Pages/WorkspaceRepositories.PullRequests.cs
                    var freshPrs = await ScopedExecutor.ExecuteAsync<WorkspacePullRequestService, IReadOnlyDictionary<int, PullRequestInfo?>>(async svc =>
                    {
                        await svc.RefreshPullRequestsAsync(WorkspaceId, refreshedIds, cancellationToken: ct);
```

Effect: opening a Feature and clicking "Create PRs" (§24, §17) will still create real PRs on GitHub for the
Feature's branch, but the poller and the post-create refresh both call `RefreshPullRequestsAsync`, which (per the
`WorkspaceRepositoryStateWriter` fix already applied per the code-review doc) is now guarded away from mutating
the Workspace's shared row for a Feature context — meaning the PR badge for a Feature **never gets populated at
all**, silently. This matches §2.4 of the prior code-review doc but confirms the fix is still one-sided: writing
is blocked, but the context-aware alternative was never wired into the caller.

### 3.3 File-version "missing on disk" state is shared across every context — fixed (§0, item 7)

```375:412:src/GrayMoon.App/Services/Workspaces/WorkspaceFileVersionService.cs
                    var wasMissing = trackedFile.IsMissingOnDisk == true;
                    var isMissing = fileResult.FileMissing;
                    if (wasMissing != isMissing)
                    {
                        trackedFile.IsMissingOnDisk = isMissing ? true : null;
```

`trackedFile` is the shared `WorkspaceFile` row (not a `WorkspaceFileContextState`, which design §6.11/§16.2 call
for explicitly: *"Never persist a single shared `WorkspaceFile.IsMissingOnDisk` as authoritative after
cutover."*). `WorkspaceFileLineStatus` rows *are* correctly filtered by `WorkspaceFeatureContextId` now (a
partial, real improvement over the design's original starting point), but the missing/present flag that feeds
the "configured file missing" indicator is still one boolean per file, shared by the Workspace and every Feature.
A file that's missing only in a Feature's worktree (e.g. a version file added later on `main` after the Feature
branched) will incorrectly mark it missing for the Workspace too, and vice versa.

### 3.4 Branch dialog worktree awareness (§28A) — corrected: already implemented for the per-repo dialog

This section originally reported no occupancy classification wired into either branch dialog. That was
inaccurate for `SwitchBranchModal.razor` (the per-repository branch switcher used from the Repositories grid):
it already injects `IWorkspaceBranchOccupancyService`, renders "Feature"/"Worktree" badges per branch row, and
blocks checkout onto an occupied branch. `BranchModal.razor` (the separate bulk "switch every repo in the
workspace to a common branch" dialog) genuinely has no occupancy awareness - see §0, item 8, for the corrected
status and the remaining bulk-dialog gap.

### 3.5 External worktree cleanup (§28B) — fixed (§0, item 8)

`IWorkspaceExternalWorktreeOperations` existed as a fully-implemented service
(`src/GrayMoon.App/Services/Features/WorkspaceExternalWorktreeOperations.cs`, registered in DI) but was not
called from any UI. `SwitchBranchModal.razor` now calls it from a new delete-icon action on "Worktree"-badged
branches (see §0, item 8 for detail). External worktrees (created by another IDE, Claude, or manual `git
worktree add`) are now cleanable from the per-repository Switch Branch dialog.

### 3.6 Repository membership change guard while Features exist (§31) — corrected: already implemented

Design §31 requires blocking Workspace repository add/remove while any Feature exists, "unless the implementation
includes fully transactional fanout across every Feature." This section originally flagged the guard as
unverified. It has since been confirmed present: `WorkspaceRepository.AddRepositoriesAsync` and
`.ReplaceRepositoriesAsync` (the latter called from both `AddAsync` and `UpdateAsync`) each check
`dbContext.WorkspaceFeatures.AnyAsync(f => f.WorkspaceId == workspaceId)` and throw
`InvalidOperationException("Cannot add/change Workspace repository membership while Features exist. Remove
Features first.")` before touching `WorkspaceRepositoryLink` rows. No further action needed for §31 (see §0,
item 9).

---

## 4. Why this matters more than it looks

The dependency-update pipeline is the piece the design doc's own closing section calls out as the actual bar for
success:

> §49 — "the central success criterion is not merely that `git worktree add` works. It is that GrayMoon's
> existing multi-repository intelligence — dependency graph, version files, Git Changes, PRs, Actions, hooks,
> package synchronization, restore, push orchestration, and safety workflows — becomes **context-correct**
> before worktree-backed Features are exposed."

Right now, dependency graph/level/version-mismatch computation is not just "not yet migrated" (a neutral gap) —
because it mixes every context's `WorkspaceProjects` into one graph and writes the single result back onto the
shared `WorkspaceRepositoryLink`, **using a Feature is actively unsafe for the Workspace's own Dependencies
data** the moment that Feature's Update/Sync runs. This is the same class of bug as the already-fixed
`WorkspaceRepositoryStateWriter` PR-corruption bug from the earlier code-review pass, just in a different
subsystem, and it was not caught by that pass.

---

## 5. Recommended fix sequence (maps onto design §36 waves 5/9)

1. ✅ **Done (§0).** ~~Stop the corruption first (small, high-value fix):~~ thread `WorkspaceFeatureContextId`
   through `RecomputeAndPersistRepositoryDependencyStatsAsync`, `GetByWorkspaceIdAsync` (or an overload), and
   `MergeWorkspaceProjectDependenciesAsync`'s call into it, so a Feature's recompute never reads/writes the
   Workspace's rows. Persist the result onto `WorkspaceRepositoryContextState.DependencyLevel/Dependencies/UnmatchedDeps`
   for that context, and keep the existing `WorkspaceRepositoryLink` write path only for `isSpecialWorkspace`.
2. ✅ **Done (§0).** ~~Context-scope~~ `GetSyncDependenciesPayloadAsync` / `GetPushPlanPayloadAsync` /
   `GetPushDependencyInfoForRepo*`: filter `WorkspaceProjects` by context, and read `GitVersion` from
   `WorkspaceRepositoryContextState` (falling back to the link only for the special Workspace). ~~Remaining:~~
   `contextId` is now threaded up through `IWorkspacePushOperations` and the Razor push pages/dialogs
   (`WorkspaceRepositories.Push.cs`, `WorkspaceActionNotificationPanel.razor`, `NewPullRequestModal.razor`).
3. ✅ **Done (§0).** ~~Add the missing~~ `contextId` parameter to `IWorkspaceUpdateOperations.GetUpdatePlanAsync`,
   threaded all the way to `WorkspaceGitService.GetUpdatePlanAsync`, mirroring the pattern already used by the
   sibling methods on the same interface.
4. ✅ **Done (§0).** ~~Fix~~ `UpdateProjectDependencyVersionsAsync`'s dependent-project lookup, now keyed on
   `(WorkspaceFeatureContextId, RepositoryId, ProjectFilePath)`, not `(RepositoryId, ProjectFilePath)` alone.
5. ✅ **Done (§0).** ~~Migrate~~ grid sort/keyset/level-grouping (`ApplySort`, `ApplyKeyset`,
   `GetRepositoryIdsAtLevelAsync`, `GetGitVersionNameMapAsync`) now use the same context-state join pattern
   already used by `Project(...)`.
6. ✅ **Done (§0).** ~~Wire~~ `RefreshContextPullRequestsAsync` into the Repositories page's PR polling loop and
   Create-PR-success refresh, branching on selected context the same way `WorkspaceActions.Loading.cs`/
   `.AutoRefresh.cs` already branch on `ctxForActions`.
7. ✅ **Done (§0).** ~~Add~~ `WorkspaceFileContextState` (the schema already existed) is now actually read/written;
   `IsMissingOnDisk` is off the shared `WorkspaceFile` row for every decision point that had (or could gain) a
   `contextId`, following the same "context table + special-Workspace-only legacy mirror" pattern already used
   correctly for Git Changes (`GitChangesSnapshotPushHandler`) and now for Actions.
8. ✅ **Done (§0).** §28A (branch dialog worktree awareness) was found already implemented in
   `SwitchBranchModal.razor` via `WorkspaceBranchOccupancyService`. ~~Implement~~ §28B (external worktree
   cleanup) is now wired into the same dialog using the already-declared `IWorkspaceExternalWorktreeOperations`
   interface. The bulk `BranchModal.razor` dialog remains without occupancy awareness (flagged as a follow-up,
   not fixed - see §0, item 8).
9. ✅ **Verified already implemented (§0).** The §31 repository-membership guard while any Feature exists is
   already present in `WorkspaceRepository.AddRepositoriesAsync`/`.ReplaceRepositoriesAsync` - no code change
   was needed, only verification (see the correction to §3.6).

Each of these should be its own reviewable change per design §36, gated by re-running the full baseline sweep in
`GrayMoon-Workspace-Current-Features-Baseline-Appendix.md` for the special Workspace before and after, since item
1 in particular changes what the Workspace's own Dependencies page currently (incorrectly) displays once a
Feature exists.
