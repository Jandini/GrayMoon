# File Configuration Dependencies — Design Plan

This document describes how to extend GrayMoon’s dependency computation so that **file configuration** (version patterns with `{repositoryname}` tokens) contributes to repository dependencies, using the **existing dependency persistence** (WorkspaceRepositoryLink: DependencyLevel, Dependencies, UnmatchedDeps).

---

## 1. Current State

### 1.1 How dependencies are computed today

- **Source:** `.csproj` files and NuGet `PackageReference`s only.
- **Flow:**
  1. Agent **RefreshRepositoryProjects** returns projects and package references per repo.
  2. **WorkspaceProjectRepository.MergeWorkspaceProjectDependenciesAsync** maps references to workspace projects (by PackageId/ProjectName), persists **ProjectDependency** rows (DependentProjectId → ReferencedProjectId, Version).
  3. **PersistRepositoryDependencyLevelAndDependenciesAsync** (called from Merge and from Recompute):
     - Builds **project-level** edges from `ProjectDependencies`.
     - Derives **repo-level** edges: (depProj.RepositoryId, refProj.RepositoryId).
     - Computes **dependency level** from the **project** graph (in-degree, topological sort); then **DependencyLevel** per repo = max level of that repo’s projects.
     - **Dependencies** = count of edges where this repo is the dependent.
     - **UnmatchedDeps** = count of those edges where the referenced repo’s GitVersion ≠ dependency Version.
  4. Results are persisted on **WorkspaceRepositoryLink**: DependencyLevel, Dependencies, UnmatchedDeps.
- **Recompute** is used when projects don’t change but we want to refresh stats (e.g. after version change): **RecomputeAndPersistRepositoryDependencyStatsAsync** reloads projects + ProjectDependencies and calls the same persist logic.

### 1.2 Existing dependency persistence

- **WorkspaceRepositoryLink** (WorkspaceRepositories): DependencyLevel, Dependencies, UnmatchedDeps.
- **ProjectDependency** (ProjectDependencies): DependentProjectId, ReferencedProjectId, Version.
- No separate table for “repository depends on repository”; repo edges are derived from project edges only.

### 1.3 File configuration (already in the app)

- **WorkspaceFile**: FileId, WorkspaceId, RepositoryId, FilePath — a file belongs to one repository in a workspace.
- **WorkspaceFileVersionConfig**: ConfigId, FileId, VersionPattern (multi-line, each line `KEY={repositoryname}`).
- **WorkspaceFileVersionService.ExtractTokens(pattern)** returns all `{token}` names from the pattern; these are **repository names** (matched to workspace repos by name).
- **WorkspaceFileVersionConfigRepository.GetByWorkspaceIdAsync(workspaceId)** returns configs with File and Repository loaded (so we know file → RepositoryId).
- Version config is used to **update** version strings in files (UpdateFileVersions agent command); it is **not** currently used for dependency computation.

---

## 2. Goal

- **If the repository has configured files:** for each such file, take all **matching repository tokens** from its version pattern (i.e. `{repositoryname}` tokens that resolve to a workspace repository) and treat those repositories as **dependencies of the repository that owns the file**.
- **Use existing persistence:** do **not** add a new persistence table for “file-config dependencies.” Reuse WorkspaceRepositoryLink (DependencyLevel, Dependencies, UnmatchedDeps) by merging file-config–derived repo edges with project-derived repo edges when computing and persisting stats.
- **Downstream behavior:** Push order, sync order, dependency graph UI, and any logic that uses DependencyLevel or Dependencies should automatically respect file-config dependencies because they are folded into the same persisted fields.

---

## 3. Design

### 3.1 File-config repository edges

- **Input:** WorkspaceFileVersionConfigs for the workspace (with File and File.Repository), and the set of workspace repository links with Repository (for name → RepositoryId).
- **Rule:** For each config:
  - **Dependent repo** = `config.File.RepositoryId` (repo that contains the configured file).
  - **Referenced repos** = each token from `WorkspaceFileVersionService.ExtractTokens(config.VersionPattern)` that equals (case-insensitive) some workspace repo’s `RepositoryName`; map name → RepositoryId via workspace links.
- **Edges:** Add (dependentRepoId, referencedRepoId) for each referenced repo. Skip when referencedRepoId == dependentRepoId (self). Deduplicate (multiple files or lines can yield the same (dep, ref)).
- **Tokens that do not match any workspace repository** are ignored (same as today in version update: “unknown tokens are silently ignored”).

### 3.2 Merge with project-based repo edges

- **Today:** Repo edges come only from ProjectDependencies (project A → project B ⇒ repo(A) → repo(B)).
- **After change:** Repo edges = **union** of:
  1. Edges from ProjectDependencies (unchanged).
  2. Edges from file configuration (above).
- Use a **single** repo-level dependency graph for:
  - Computing **DependencyLevel** (topological sort on repos).
  - **Dependencies** count (number of edges where this repo is the dependent).
  - **UnmatchedDeps** (see below).

### 3.3 Dependency level and counts

- **Level:** Compute from the **merged repo graph** (not from the project graph). Run topological sort on repos; assign level per repo. Repos with no projects but with file-config deps get a level; repos with no incoming edges (and no project deps) get level 1.
- **Dependencies:** For each repo, count edges (depRepoId, refRepoId) where depRepoId == this repo (both project and file-config edges).
- **UnmatchedDeps:** Only **project** edges have a stored Version to compare to the referenced repo’s GitVersion. File-config edges do not store a version. Options:
  - **Recommended:** Keep UnmatchedDeps as **project-only** (count only project-derived edges where version ≠ ref repo GitVersion). File-config deps do not contribute to UnmatchedDeps. Simple and consistent with “version mismatch” being a .csproj concept.
  - Alternative: Treat file-config deps as “matched” (0 contribution) so they don’t inflate the badge; same net effect if we only count project edges for UnmatchedDeps.

### 3.4 Where to compute file-config edges

- **PersistRepositoryDependencyLevelAndDependenciesAsync** is the single place that turns project edges + DB state into WorkspaceRepositoryLink updates. It should:
  1. Accept (workspaceId, workspaceProjects, uniqueEdges) as today (project-level data).
  2. **Load** file configs for the workspace (e.g. via **WorkspaceFileVersionConfigRepository.GetByWorkspaceIdAsync**) and workspace links with Repository (for name → RepositoryId).
  3. Build repo edges from project edges (current logic).
  4. Build repo edges from file config (dependent = file.RepositoryId, referenced = token’s RepositoryId for each token that resolves in the workspace; exclude self).
  5. **Merge** repo edges (union, distinct).
  6. Compute **DependencyLevel** from the merged **repo** graph (topological sort on repos).
  7. Compute **Dependencies** from merged edges (count where repo is dependent).
  8. Compute **UnmatchedDeps** from **project** edges only (current logic).
  9. Persist to WorkspaceRepositoryLink as today.

- **RecomputeAndPersistRepositoryDependencyStatsAsync** already reloads projects and ProjectDependencies and calls `PersistRepositoryDependencyLevelAndDependenciesAsync`. No signature change needed; the private method will load file configs internally, so recompute automatically includes file-config edges.

- **MergeWorkspaceProjectDependenciesAsync** also calls `PersistRepositoryDependencyLevelAndDependenciesAsync` with (workspaceId, workspaceProjects, uniqueEdges). Again, no change at the call site; file-config edges will be loaded inside the private method.

### 3.5 Repository dependency graph API

- **GetRepositoryDependencyGraphAsync** is used for the Cytoscape repository graph. It currently builds repo edges only from project edges. It should be updated to **also** include file-config repo edges (same rules: configs for workspace, tokens → repo by name, exclude self), merge with project-derived repo edges, and return the combined nodes/edges. So the UI graph shows both project-based and file-config dependencies.

### 3.6 Push / sync behavior

- **GetPushPlanPayloadAsync** and sync flows use **DependencyLevel** and project-level **RequiredPackages**. RequiredPackages are only from ProjectDependencies (packages that must be in the registry). File-config does not add packages; it only adds **repo-to-repo** ordering. So:
  - **DependencyLevel** will already reflect file-config (once we persist it from the merged repo graph). Push order and level-by-level sync will therefore respect file-config dependencies without further change.
  - No change needed to GetPushPlanPayloadAsync or GetSyncDependenciesPayloadAsync for package lists; file-config only affects level and dependency count.

---

## 4. Implementation Outline

### 4.1 WorkspaceProjectRepository

- **Inject** **WorkspaceFileVersionConfigRepository** (and ensure it can be used from the same DbContext / scope as WorkspaceProjectRepository, which it can — both are scoped and use AppDbContext).
- **PersistRepositoryDependencyLevelAndDependenciesAsync** (private):
  - After loading workspaceProjects and building project-based repo edges:
    1. Load `WorkspaceRepositories` for workspaceId with `Include(wr => wr.Repository)`.
    2. Load file configs: `versionConfigRepository.GetByWorkspaceIdAsync(workspaceId)` (already includes File and File.Repository).
    3. Build `repoEdgesFromFileConfig`: for each config, if `config.File?.RepositoryId` is set, get tokens with `WorkspaceFileVersionService.ExtractTokens(config.VersionPattern)`; for each token, resolve to RepositoryId via links (RepositoryName match, case-insensitive); add (file.RepositoryId, tokenRepoId) when tokenRepoId != file.RepositoryId.
    4. Merge repo edges: `allRepoEdges = projectDerivedRepoEdges.Union(fileConfigRepoEdges).Distinct()`.
    5. Compute level from **repo graph**: build in-degree and reverse edges per **repo**, run BFS/topological sort, assign level per repo (repos not in any edge get level 1).
    6. Dependencies count: for each repo, count edges where that repo is the dependent.
    7. UnmatchedDeps: keep current logic (only project edges, version vs GitVersion).
    8. Persist to links (DependencyLevel, Dependencies, UnmatchedDeps).

- **GetRepositoryDependencyGraphAsync**:
  - After building project-based repo edges, load file configs and workspace links, build file-config repo edges (same logic), merge, then build nodes and edge list from merged repo edges. Return RepositoryDependencyGraph(nodes, edges).

### 4.2 No new tables or entities

- No new persistence for “file-config dependency” rows. All information is derived from existing WorkspaceFileVersionConfig + WorkspaceFile + WorkspaceRepositories at compute time.

### 4.3 Recompute triggers

- Existing callers of **RecomputeAndPersistRepositoryDependencyStatsAsync** (e.g. after sync, after version refresh, notify job) need no change; file-config will be included automatically when the private method loads configs.

### 4.4 When file config changes

- If the user adds/edits/removes a version config or adds/removes a file, **DependencyLevel/Dependencies** are not recomputed until the next:
  - **MergeWorkspaceProjectDependenciesAsync** (e.g. after RefreshRepositoryProjects), or
  - **RecomputeAndPersistRepositoryDependencyStatsAsync** (e.g. after sync or refresh).
- Optional improvement: after saving or deleting a WorkspaceFileVersionConfig (e.g. in VersionConfigModal or WorkspaceFiles), call **RecomputeAndPersistRepositoryDependencyStatsAsync** for that workspace so the grid and graph update immediately. This is a small UX enhancement and can be a follow-up.

---

## 5. Edge Cases

| Case | Handling |
|------|----------|
| Token not in workspace | Ignore (no edge). Same as version-update behavior. |
| Self-reference (token = file’s repo name) | Do not add edge (dependentRepoId == referencedRepoId). |
| Repo has no projects, only file-config deps | Repo gets a level from the merged repo graph and a Dependencies count; UnmatchedDeps = 0 for those edges. |
| No file configs in workspace | No file-config edges; behavior identical to today. |
| Config with no tokens / empty pattern | No edges from that config. |
| File or Repository missing on config | Skip that config when building edges. |

---

## 6. Summary

- **Data:** Use existing **WorkspaceFileVersionConfig** and **WorkspaceFile**; tokens in VersionPattern are repository names; resolve to workspace RepositoryIds via **WorkspaceRepositories** + Repository.
- **Rule:** Repository that **owns a configured file** depends on every **workspace repository** whose name appears as a `{repositoryname}` token in that file’s version pattern (excluding self).
- **Persistence:** Reuse **WorkspaceRepositoryLink** (DependencyLevel, Dependencies, UnmatchedDeps). In **PersistRepositoryDependencyLevelAndDependenciesAsync**, merge file-config repo edges with project-derived repo edges; compute level from merged repo graph; persist as today. **UnmatchedDeps** remains project-only.
- **APIs:** **GetRepositoryDependencyGraphAsync** should include file-config repo edges so the dependency graph UI shows them.
- **Push/sync:** No change to payload types; dependency level and order already come from persisted DependencyLevel, which will now include file-config dependencies.
