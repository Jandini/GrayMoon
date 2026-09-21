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

**Not yet done:** items 6–9, plus the remainder of item 2 (`IWorkspacePushOperations`/Razor push pages), and
generated-package context-scoping (`SyncGeneratedPackageDependenciesAsync` still isn't context-scoped; the
`WorkspaceProjectRepositoryGeneratedPackageTests` seed data and the new-context filter both currently carve out
generated/virtual package rows rather than scoping them). See the table in §1 and the fix sequence in §5 below
for what's left; the summary and evidence in §§1–4 otherwise still describe the code as it stood *before* this
update and should be read with the corrections above in mind.

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
| PR **persistence infrastructure** (`WorkspaceRepositoryContextPullRequest`, `UpsertContextAsync`, `RefreshContextPullRequestsAsync`) | **Built, but not called from any page** | See §4 |
| PR **polling / Create-PR flow** | **Not wired to context at all** | `WorkspaceRepositories.PrPolling.cs`, `WorkspaceRepositories.PullRequests.cs` |
| **Dependency graph / dependency level / dependency stats** | **Done** (§0, items 1 & 4) — no longer cross-contaminates contexts | See §2 — the core finding of this document (superseded by §0) |
| **Update / SyncDependencies / Push planning** | **Context-aware for the data layer** (§0, items 2 & 3); **UI/interface threading above `WorkspaceGitService`/`WorkspacePushService` still not done** for push planning specifically | See §2 (superseded by §0) |
| File-version missing-state / line-status mismatch | **Partially done.** `WorkspaceFileLineStatus` rows now carry `WorkspaceFeatureContextId` and are filtered by it. But `WorkspaceFile.IsMissingOnDisk` (the shared file row) is still mutated directly — no `WorkspaceFileContextState` table is used despite existing in the design (§6.11/§16.2) | See §5 |
| Branch dialog worktree awareness (§28A) | **Not implemented** | No `GitWorktreeInfo`/`LocalBranchView.WorktreeKind` found in `BranchModal.razor`/`SwitchBranchModal.razor` |
| External worktree cleanup (§28B) | **Not implemented** | `IWorkspaceExternalWorktreeOperations` interface exists but no corresponding UI/analysis service found wired |
| Repository membership guard while Features exist (§31) | **Not verified as enforced** — needs explicit check | See §7 |

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

### 3.3 File-version "missing on disk" state is shared across every context

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

### 3.4 Branch dialog worktree awareness (§28A) not implemented

No `GitWorktreeInfo`, `LocalBranchView.WorktreeKind`, or equivalent classification logic was found in
`BranchModal.razor` / `SwitchBranchModal.razor`. `WorkspaceBranchOccupancyService.cs` exists (confirmed by file
search) but nothing in the branch dialogs references it for row-level badges/checkout blocking. This means a
user can still hit `branch is already checked out at <path>` by attempting to switch the special Workspace onto
a branch that a Feature (or an external tool) already has checked out in a worktree — the exact failure mode
§28A was written to prevent.

### 3.5 External worktree cleanup (§28B) not implemented

`IWorkspaceExternalWorktreeOperations` exists as an interface
(`src/GrayMoon.Application/Features/IWorkspaceExternalWorktreeOperations.cs`) but no implementing service or
modal was found wired into `SwitchBranchModal`/`BranchModal`. External worktrees (created by another IDE, Claude,
or manual `git worktree add`) are therefore not cleanable from GrayMoon at all yet.

### 3.6 Repository membership change guard while Features exist (§31)

Design §31 requires blocking Workspace repository add/remove while any Feature exists, "unless the implementation
includes fully transactional fanout across every Feature." No such guard was found during this pass in the
repository-membership edit path. **This needs a follow-up code search before relying on this document as proof
either way** — it was not exhaustively traced in this session and is flagged here as an open verification item,
not a confirmed gap.

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
2. ✅ **Data layer done (§0); UI/interface threading for push planning still open.**
   ~~Context-scope~~ `GetSyncDependenciesPayloadAsync` / `GetPushPlanPayloadAsync` / `GetPushDependencyInfoForRepo*`:
   filter `WorkspaceProjects` by context, and read `GitVersion` from `WorkspaceRepositoryContextState` (falling
   back to the link only for the special Workspace). **Remaining:** thread `contextId` up through
   `IWorkspacePushOperations` and the Razor push pages/dialogs that currently call the legacy overloads.
3. ✅ **Done (§0).** ~~Add the missing~~ `contextId` parameter to `IWorkspaceUpdateOperations.GetUpdatePlanAsync`,
   threaded all the way to `WorkspaceGitService.GetUpdatePlanAsync`, mirroring the pattern already used by the
   sibling methods on the same interface.
4. ✅ **Done (§0).** ~~Fix~~ `UpdateProjectDependencyVersionsAsync`'s dependent-project lookup, now keyed on
   `(WorkspaceFeatureContextId, RepositoryId, ProjectFilePath)`, not `(RepositoryId, ProjectFilePath)` alone.
5. ✅ **Done (§0).** ~~Migrate~~ grid sort/keyset/level-grouping (`ApplySort`, `ApplyKeyset`,
   `GetRepositoryIdsAtLevelAsync`, `GetGitVersionNameMapAsync`) now use the same context-state join pattern
   already used by `Project(...)`.
6. **Not yet done.** Wire `RefreshContextPullRequestsAsync`/`ClearContextPullRequestAsync` into the Repositories
   page's PR polling loop and Create-PR-success refresh, branching on selected context the same way
   `WorkspaceActions.Loading.cs`/`.AutoRefresh.cs` already branch on `ctxForActions`.
7. **Not yet done.** Add `WorkspaceFileContextState` (or equivalent) and move `IsMissingOnDisk` off the shared
   `WorkspaceFile` row, following the same "context table + special-Workspace-only legacy mirror" pattern already
   used correctly for Git Changes (`GitChangesSnapshotPushHandler`) and now for Actions.
8. **Not yet done.** Implement §28A (Branch dialog worktree awareness) using the already-existing
   `WorkspaceBranchOccupancyService`, then §28B (external worktree cleanup) using the already-declared
   `IWorkspaceExternalWorktreeOperations` interface.
9. **Not yet done / not verified.** Verify/implement the §31 repository-membership guard while any Feature
   exists.

Each of these should be its own reviewable change per design §36, gated by re-running the full baseline sweep in
`GrayMoon-Workspace-Current-Features-Baseline-Appendix.md` for the special Workspace before and after, since item
1 in particular changes what the Workspace's own Dependencies page currently (incorrectly) displays once a
Feature exists.
