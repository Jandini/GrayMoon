# Dependency level update when syncing workspace vs single repository

This document explains **how `DependencyLevel` (and related `Dependencies` / `UnmatchedDeps`) are updated** when syncing the **entire workspace** versus **one repository**, and proposes how **single-repo sync** could still refresh levels using **existing repos** without syncing them.

---

## 1. What dependency level is

- **Stored on:** `WorkspaceRepositoryLink` (`WorkspaceRepositories` table) as `DependencyLevel` (nullable int).
- **Meaning:** Repos with the **same level** can be built in parallel; **lower level** repos are dependents of higher-level repos (package/project references flow upward in the graph).
- **Used for:** Grouping rows on the Workspace Repositories page (`FilteredLevelGroups`), dependency-synchronized push ordering, sync-commits/update plans, and dependency graph links.

Source: `WorkspaceRepositoryLink.DependencyLevel` and `WorkspaceProjectRepository.PersistRepositoryDependencyLevelAndDependenciesAsync`.

---

## 2. Where levels are computed

All level persistence flows through:

**`WorkspaceProjectRepository.PersistRepositoryDependencyLevelAndDependenciesAsync`** (private)

It:

1. Loads **all** workspace repo links for the workspace.
2. Builds **repo-to-repo edges** from:
   - **Project-derived edges:** `ProjectDependencies` rows (dependent project → referenced project), mapped to repo pairs when both projects belong to workspace repos.
   - **File-config edges:** version pattern tokens in workspace file version configs that reference another repo by name.
3. Runs a **Kahn-style topological leveling** over repo IDs:
   - Repos with **no incoming edges** from other workspace repos get level 1; when “processed,” dependents get decremented in-degree and eventually assigned the next level.
4. If the graph has a **cycle** (or otherwise not all repos are assigned), **`remaining != 0`** and **`DependencyLevel` is set to `null`** for repos that could not be placed (consistent “no level” state).
5. Also computes **`Dependencies`** (out-edge count per repo) and **`UnmatchedDeps`** (edges where package version ≠ referenced repo’s `GitVersion`).

So levels are **global to the workspace**: they depend on the **full edge set**, not on a single repo in isolation.

---

## 3. How full-workspace sync updates levels

**Entry:** `WorkspaceGitService.SyncAsync(workspaceId, repositoryIds: null, skipDependencyLevelPersistence: false)`  
(except retry-after-error path, which may pass `skipDependencyLevelPersistence: true`).

**Flow:**

1. Agent sync runs for **every** workspace repo; each result includes `ProjectsDetail` (package references from `.csproj`).
2. **`PersistVersionsAsync`** updates `GitVersion`, branch, commit counts, etc., then calls:
3. **`MergeWorkspaceProjectDependenciesAsync(workspaceId, syncResults, persistDependencyLevel: true)`**  
   - `syncResults` contains **all repos** just synced.
   - For each repo, it **replaces** `ProjectDependencies` rows for that repo’s dependent projects with edges parsed from `ProjectsDetail`.
   - After save, **`uniqueEdges`** in memory reflects the **full** dependency graph just written for the whole workspace.
4. **`PersistRepositoryDependencyLevelAndDependenciesAsync`** runs with that **complete** `uniqueEdges` list and recomputes levels for **every** link.

**UI:** `WorkspaceRepositories.razor` main sync calls `SyncAsync` without `repositoryIds`, with `skipDependencyLevelPersistence: isRetryAfterError` only.

So **sync-all** both **refreshes** project dependencies from disk for every repo and **recomputes** levels from the **entire** graph in one pass.

---

## 4. Why single-repo (and level) sync skips level persistence

**Entry points:**

- **Single repo:** `SyncSingleRepoAsync` → `SyncAsync(..., repositoryIds: [id], skipDependencyLevelPersistence: true)`.
- **One level:** `SyncLevelAsync` → same with a list of repo IDs, `skipDependencyLevelPersistence: true`.

**Reason (code comment / behavior):**  
`MergeWorkspaceProjectDependenciesAsync` is documented to skip recomputing levels when `persistDependencyLevel` is false—*e.g. when syncing only selected repos*.

**Technical reason:**  
`MergeWorkspaceProjectDependenciesAsync` only **builds `uniqueEdges` from the `syncResults` passed in**. For a **partial** sync:

- It still **updates DB** correctly for the synced repo(s): it removes old `ProjectDependencies` for those repos’ dependent projects and inserts new edges from the fresh `ProjectsDetail`.
- If **`persistDependencyLevel: true`** were passed with **only** that repo in `syncResults`, **`uniqueEdges` would only contain edges derived from that repo’s projects**. Calling `PersistRepositoryDependencyLevelAndDependenciesAsync` with that **partial** edge list would **omit edges from other repos**, so the **topological sort and level assignment would be wrong** for the whole workspace.

Therefore the UI **deliberately** passes `skipDependencyLevelPersistence: true` so levels are **not** overwritten with a partial graph. Side effect: after single-repo sync, **`DependencyLevel` is not recalculated** even though that repo’s `ProjectDependencies` and `GitVersion` may have changed—grid grouping and push/update plans can stay stale until a full sync or another operation that recomputes stats.

---

## 5. Existing data that already represents “other repos”

Without syncing other repos, the app **already** has:

| Data | Role |
|------|------|
| **`WorkspaceProjects`** | All projects for all workspace repos (last merged state). |
| **`ProjectDependencies`** | Edges between workspace projects; after single-repo sync, **other repos’ edges are unchanged** in DB (only synced repo’s dependent projects were replaced). |
| **`WorkspaceRepositories.GitVersion`** | Per-repo version used for unmatched-dep detection; single sync updates the synced repo only. |
| **File version configs** | Optional repo edges from version patterns. |

So **the full edge set for leveling** can be **reloaded from the database** after a partial merge: not only from the in-memory `uniqueEdges` built inside `MergeWorkspaceProjectDependenciesAsync`.

---

## 6. Proposals: single-repo sync accounting for existing repos

Goal: **Keep current behavior** (sync one repo only via agent) but **refresh `DependencyLevel` / `Dependencies` / `UnmatchedDeps`** using **all** workspace repos’ **already-persisted** graph + updated versions for the synced repo.

### Option A — Recompute after partial sync (implemented)

After `PersistVersionsAsync` when `persistDependencyLevel` was false (partial sync):

1. **`MergeWorkspaceProjectDependenciesAsync(..., persistDependencyLevel: false)`** — already happens today; updates `ProjectDependencies` for synced repo only and saves.
2. Immediately call **`WorkspaceProjectRepository.RecomputeAndPersistRepositoryDependencyStatsAsync(workspaceId)`**.

`RecomputeAndPersistRepositoryDependencyStatsAsync`:

- Reloads **all** `WorkspaceProjects` and **all** `ProjectDependencies` for the workspace from the DB.
- Rebuilds `uniqueEdges` from that full set.
- Calls `PersistRepositoryDependencyLevelAndDependenciesAsync` with the **complete** graph.

**Pros:** No second agent sync; uses existing repos as stored. **Cons:** Other repos’ `ProjectDependencies` are only as fresh as their last sync/refresh—if their `.csproj` changed on disk but wasn’t synced, edges for those repos remain stale (same as today until they sync).

**Implementation:** In `WorkspaceGitService.PersistVersionsAsync`, whenever `persistDependencyLevel` is false, after `MergeWorkspaceProjectDependenciesAsync` the service calls `RecomputeAndBroadcastWorkspaceSyncedAsync` (recompute from full DB + `WorkspaceSynced` hub). Applies to **single-repo sync** and **level sync** (multiple repos in one call)—both use `skipDependencyLevelPersistence: true`, so one code path covers both.

### Option B — Merge always persists from DB after save

Change `MergeWorkspaceProjectDependenciesAsync` so that when `persistDependencyLevel` is true, **after** saving `ProjectDependencies`, **reload** all dependency rows into `uniqueEdges` (same query as in `RecomputeAndPersistRepositoryDependencyStatsAsync`) and then call `PersistRepositoryDependencyLevelAndDependenciesAsync`. That way partial sync could pass `persistDependencyLevel: true` safely.

**Pros:** Single code path. **Cons:** Extra DB read on every merge; still need to decide whether partial sync should trigger persist (may want explicit flag).

### Option C — Optional “refresh levels only” action

Expose a lightweight action that only runs `RecomputeAndPersistRepositoryDependencyStatsAsync` (and broadcast `WorkspaceSynced`). Users run it after partial syncs if levels look wrong.

**Pros:** No change to sync semantics. **Cons:** Manual step.

### Option D — Refresh projects for all repos without full git sync

If staleness of other repos’ edges is the main issue, **`RefreshWorkspaceProjectsAsync`** (agent `RefreshRepositoryProjects`, no fetch) could be run periodically or once after single sync to refresh `ProjectsDetail` for everyone, then merge with `persistDependencyLevel: true`. That **does** touch every repo on disk (not a git sync, but still agent work per repo).

### Implementation complexity (all options)

| Option | Effort | Touch points | Risk | Testing |
|--------|--------|--------------|------|--------|
| **A — Recompute after partial sync** | **Low** | One call site: `WorkspaceGitService.PersistVersionsAsync` after `MergeWorkspaceProjectDependenciesAsync(..., false)` when partial; optional UI unchanged if done only in service. | Low: recompute is already used elsewhere (`RecomputeAndPersistRepositoryDependencyStatsAsync`); must ensure partial merge completed and saved before recompute. | Sync single repo → assert `DependencyLevel` updates; workspace with cycles still gets null levels; full sync unchanged. |
| **B — Merge reloads edges then persist** | **Medium** | `WorkspaceProjectRepository.MergeWorkspaceProjectDependenciesAsync`: after `SaveChanges`, load all `ProjectDependencies` for workspace (duplicate query shape from `RecomputeAndPersistRepositoryDependencyStatsAsync`), then call persist. Possibly flip `SyncSingleRepoAsync` / `SyncLevelAsync` to `skipDependencyLevelPersistence: false` or remove flag for partial if merge always safe. | Medium: every merge pays extra read; must not regress full-sync path (behavior should match today). Regression if reload logic diverges from recompute’s edge load. | Full sync + single sync both; large workspace perf smoke test. |
| **C — Manual “refresh levels” action** | **Low–Medium** | New UI control (header or menu) + handler calling `RecomputeAndPersistRepositoryDependencyStatsAsync` + `WorkspaceSynced` broadcast (same as `RecomputeAndBroadcastWorkspaceSyncedAsync` without dependency sync). Authorization if needed. | Low for backend; UX risk if users don’t know when to click. | Manual click after partial sync; no agent required if DB already consistent. |
| **D — Refresh projects for all repos** | **High** | Orchestration in `WorkspaceGitService` (or UI) to call `RefreshWorkspaceProjectsAsync` for all repo IDs, then merge with persist — similar to full refresh flow. Progress/cancel semantics; agent must stay connected for duration. | High: N agent round-trips, failure mid-way leaves mixed freshness; timeouts on large workspaces. | Staging with many repos; cancel mid-refresh; compare edge counts before/after. |

**Effort scale (rough):** Low = small localized change, existing APIs; Medium = refactor or new branch in merge + caller updates; High = new flows, perf/cancel, and operational concerns.

---

## 7. Summary

| Scenario | Agent work | ProjectDependencies update | DependencyLevel update |
|----------|------------|------------------------------|------------------------|
| Sync all | All repos | Full replace per repo from full sync results | Yes — full `uniqueEdges` |
| Sync one / level | Subset only | Only synced repos’ dependents replaced in DB | No — skipped to avoid partial `uniqueEdges` |
| Single sync / level sync (Option A) | Subset only | Same as today | Yes — after merge, recompute loads **full** edges from DB |

**Recommended direction:** **Option A** — after partial `PersistVersionsAsync`, call **`RecomputeAndPersistRepositoryDependencyStatsAsync`** so the workspace grid and push/update logic see consistent levels without syncing other repositories.

---

## 8. Key file references

| Area | Location |
|------|----------|
| Sync orchestration, `skipDependencyLevelPersistence` | `WorkspaceGitService.SyncAsync`, `PersistVersionsAsync` |
| Merge + persist flag | `WorkspaceProjectRepository.MergeWorkspaceProjectDependenciesAsync` |
| Level algorithm | `WorkspaceProjectRepository.PersistRepositoryDependencyLevelAndDependenciesAsync` |
| Recompute from DB | `WorkspaceProjectRepository.RecomputeAndPersistRepositoryDependencyStatsAsync` |
| UI full sync | `WorkspaceRepositories.razor` — `SyncAsync` (no `repositoryIds`) |
| UI single / level sync | `WorkspaceRepositories.razor` — `SyncSingleRepoAsync`, `SyncLevelAsync` |
