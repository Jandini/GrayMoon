# WorkspaceRepositories.razor — Refactoring Plan

**Goal:** Make the page safe, maintainable, and optimized without breaking UX or functionality.  
**Reference:** [WorkspaceRepositories-razor-Analysis.md](WorkspaceRepositories-razor-Analysis.md) (method inventory and best practices).

This plan applies **all 10 best practices** in a phased order so each step is small, testable, and reversible.

---

## Principles

- **Incremental:** One phase at a time; verify after each.
- **Behavior unchanged:** Same URLs, same UI, same callbacks and modals; only structure and location of code change.
- **Test manually:** After each phase: load workspace, sync, update, push, pull, branch actions, modals, search.
- **Reversible:** Use partials and new types so existing behavior stays until new code is wired; avoid big-bang rewrites.

---

## Phase 0 — Preparation (no UX change)

**Purpose:** Add missing shared types and a URL helper so later phases don’t depend on the page.

### 0.1 — Point 9: Extract API and DTOs

| Action | Detail |
|--------|--------|
| **Add DTO** | In `Models/Api/BranchesApiModels.cs` add `CreateBranchApiResult` with `Success` and `Error` (or align with existing `CreateBranchResponse` if the API returns `errorMessage`). |
| **Update page** | In `WorkspaceRepositories.razor` remove the nested `CreateBranchApiResult` class and use the type from `GrayMoon.App.Models.Api`. |
| **Verify** | Build; run; create branch from Switch Branch modal — response still deserializes. |

**Risk:** Low. Only moves a type and updates a using.

---

### 0.2 — Point 7: Centralize URLs and navigation

| Action | Detail |
|--------|--------|
| **Add helper** | Create `Services/WorkspaceUrlHelper.cs` (or extend an existing route helper) with: `GetDependencyGraphUrl(int workspaceId, int? repositoryId = null, int? level = null)` returning `/workspaces/{id}/dependencies?repo={repo}` or `?level={level}`. |
| **Update page** | Replace `GetDependencyGraphUrl(repoId)` and `GetDependencyGraphUrlForLevel(level)` with calls to the helper (pass `WorkspaceId`). |
| **Verify** | Build; click dependency graph links for repo and level — same URLs and navigation. |

**Risk:** Low. Pure URL generation; no state or UI change.

---

## Phase 1 — Code-behind and shared orchestration

**Purpose:** Move logic out of the `.razor` file and deduplicate sync/commit-sync/sync-to-default so the page stays stable while we add structure.

### 1.1 — Point 1: Move logic to a code-behind or view model

| Action | Detail |
|--------|--------|
| **Add partial** | Create `WorkspaceRepositories.razor.cs` with the same namespace and `partial class WorkspaceRepositories`. |
| **Move** | Move the entire `@code { ... }` block from `WorkspaceRepositories.razor` into the `.cs` file (all fields, properties, methods). Remove `@code` from the Razor file. |
| **Verify** | Build; full smoke test (load, sync, update, push, pull, branch, modals, search). |

**Risk:** Low. No behavior change; only file split. Keeps all logic in one place for the next steps.

---

### 1.2 — Point 4: Reuse sync/update/push orchestration

| Action | Detail |
|--------|--------|
| **Sync** | In the code-behind, add a single private method `RunSyncCoreAsync(IReadOnlyList<int>? repositoryIds, bool skipDependencyLevelPersistence)` that contains the common logic (create scope, call `WorkspaceGitService.SyncAsync`, progress callback, update `repoSyncStatus` and in-memory link state, reload, apply sync state, set per-repo errors). |
| **Refactor** | `SyncAsync()` calls `RunSyncCoreAsync(null, isRetryAfterError)`. `SyncLevelAsync(ids)` and `SyncSingleRepoAsync(id)` call `RunSyncCoreAsync(ids ?? new[] { id }, true)`. |
| **Commit sync** | Add `RunCommitSyncCoreAsync(int repositoryId)` (single HTTP call, error handling, refresh). `CommitSyncAsync(id)` calls it. `CommitSyncLevelAsync(ids)` runs it in parallel per repo (keep existing parallel and progress logic). |
| **Sync to default** | Add `RunSyncToDefaultSingleAsync(...)` for one repo (delete remote if needed, then sync-to-default API). Reuse it from `SyncToDefaultSingleRepoAfterCheckAsync` and from the per-repo loop inside `SyncToDefaultLevelAsync` so level sync is “for each repo, call RunSyncToDefaultSingleAsync” with semaphore. |
| **Verify** | Full sync (all, level, single repo), commit sync (single + level), sync to default (modal + level). |

**Risk:** Medium. Logic is consolidated; same behavior. Test sync and commit-sync and sync-to-default thoroughly.

---

## Phase 2 — Feature handlers and modal state

**Purpose:** Isolate feature logic and modal state so the page only coordinates.

### 2.1 — Point 2: Extract feature-based services or handlers

| Action | Detail |
|--------|--------|
| **Handlers** | Create stateless handler classes (or scoped services) that take dependencies in the constructor and expose async methods. Page creates them via factory or gets them from DI. Suggested split: |
| | **WorkspaceSyncHandler** — `SyncAsync`, `SyncLevelAsync`, `SyncSingleRepoAsync` (call `RunSyncCoreAsync`); progress/CTS/errors reported via callbacks or a context object provided by the page. |
| | **WorkspaceUpdateHandler** — Update plan, `RunUpdateCoreAsync`, single-repo update, commit payload, file-version update; page passes `WorkspaceId`, progress/error callbacks, and cancellation. |
| | **WorkspacePushHandler** — Push plan, push with deps, single-repo push with upstream; page passes workspace id, progress, errors. |
| | **WorkspaceCommitSyncHandler** — `CommitSyncAsync`, `CommitSyncLevelAsync` (calls API); page passes base URL, HTTP client, progress, errors. |
| | **WorkspaceBranchHandler** — Create branches (all/single), checkout, sync to default (single/level); page passes workspace id, progress, errors. |
| **Page** | Page keeps: loading state, `workspaceRepositories`, `errorMessage`, `repositoryErrors`, modal visibility flags, and high-level actions that call the handlers and then set state / refresh. Handlers do not hold component state; they receive everything they need. |
| **Verify** | Same flows as before: sync, update, push, pull, branch create/checkout/sync-to-default. |

**Risk:** Medium–high. Start with one handler (e.g. `WorkspaceCommitSyncHandler`) and wire it; then replicate the pattern for sync, update, push, branch.

---

### 2.2 — Point 3: Extract modal state and commands

| Action | Detail |
|--------|--------|
| **State objects** | Introduce small state classes (or records) per modal, e.g. `ConfirmModalState`, `RepositoriesModalState`, `BranchModalState`, `SwitchBranchModalState`, `UpdateModalState`, `UpdateSingleRepoDependenciesModalState`, `VersionFilesCommitModalState`, `PushWithDependenciesModalState`. Each holds: visibility, message/title, payload (if any), and optional callback or action id. |
| **Commands** | Open/Close/Confirm (where applicable) become methods on the state object or on a small “modal coordinator” that the page uses. Page binds modal visibility and content to these state objects. |
| **Page** | Page no longer holds dozens of separate `showXModal`, `xMessage`, `xPayload` fields; it holds one state object per modal and calls Open/Close/Confirm. Handlers (from 2.1) can receive the relevant state or callbacks to open/close modals. |
| **Verify** | Every modal still opens/closes/confirms as today; no change in when they appear or what they do. |

**Risk:** Medium. Many bindings change from individual fields to properties on state objects. Do one modal at a time (e.g. Confirm first, then Repositories, then Branch, etc.).

---

## Phase 3 — Markup and components

**Purpose:** Shrink the Razor file and isolate table structure.

### 3.1 — Point 5: Keep markup focused on structure and binding

| Action | Detail |
|--------|--------|
| **Code-behind** | Ensure no new business logic is added in the Razor file. All new behavior goes in the partial class or handlers. |
| **Markup** | Keep only: layout, conditionals (loading/empty/filtered), one `@foreach` over level groups that renders a level component and a row component (see 3.2), and binding of modals/overlays to state and callbacks. Remove any remaining inline C# that could live in the code-behind (e.g. complex expressions → properties or methods in partial). |
| **Verify** | UI unchanged; no regression in layout or visibility. |

**Risk:** Low.

---

### 3.2 — Point 6: Use partials or subcomponents for table sections

| Action | Detail |
|--------|--------|
| **Level header component** | Create `WorkspaceRepositoriesLevelHeader.razor`. Props: `LevelKey` (int?), `RepositoryIds` (list), `LevelUrls` (for “open in GitHub”), `ColSpan`. Events: `OnSyncToDefault`, `OnSyncCommits`, `OnOpenPr`, `OnSyncLevel`, `OnOpenGitHub`. Markup: single `<tr class="dependency-level-header">` with level title, graph link (use `WorkspaceUrlHelper`), action icons, and count link. |
| **Repository row component** | Create `WorkspaceRepositoriesRow.razor`. Props: `WorkspaceId`, `Link` (WorkspaceRepositoryLink), `PrInfo`, `RepoSyncStatus`, `MismatchedDependencyLines`, `IsVersionClicked`, `IsDependencyBadgeClicked`, `RepositoryError`, `IsSyncing`, `IsUpdating`. Events: `OnVersionClick`, `OnVersionMouseLeave`, `OnBranchClick`, `OnDependencyBadgeClick`, `OnDependencyBadgeKeydown`, `OnDependencyBadgeMouseLeave`, `OnPushBadgeClick`, `OnPullBadgeClick`, `OnSyncClick`, `OnDismissError`. Markup: one repo row (cells for repo, version, branch, divergence, PR, deps, commits, status) and optional error row. Use existing shared components (DivergenceBadge, PRBadge, CommitsBadge, RepoSyncStatusBadge). |
| **Page** | In the table body, `@foreach (var group in FilteredLevelGroups)`: render `<WorkspaceRepositoriesLevelHeader ... />` then `@foreach (var wr in group)` render `<WorkspaceRepositoriesRow ... />`. Pass only the data and callbacks needed. |
| **Verify** | Table looks and behaves the same: level headers, repo rows, badges, links, tooltips, errors. Resize, hover, click, keyboard. |

**Risk:** Medium. Many parameters and events; ensure no callback is dropped and two-way or parent state updates (e.g. `clickedVersions`, `clickedDependencyBadges`) are still updated from the row.

---

## Phase 4 — Service surface and responsibility

**Purpose:** Reduce what the page injects and does so it acts as a coordinator.

### 4.1 — Point 8: Limit injected services in the page

| Action | Detail |
|--------|--------|
| **Facade** | Introduce a single “workspace page” or “workspace operations” service (e.g. `IWorkspacePageService` or `IWorkspaceOperationsService`) that wraps: `WorkspaceRepository`, `WorkspaceGitService`, `WorkspaceFileVersionService`, `WorkspacePullRequestService`, `GitHubPullRequestService`, `ConnectorRepository`, `GitHubRepositoryService`, and optionally `IHttpClientFactory`/base URL for commit-sync and branch API calls. Handlers (from 2.1) can depend on this facade instead of many services. |
| **Page** | Page injects: `IWorkspacePageService` (or the facade), `NavigationManager`, `ILogger`, `IJSRuntime`, `IToastService`, `IServiceScopeFactory` (if still needed for fresh scope), `IOptions<WorkspaceOptions>`. Remove direct injection of WorkspaceRepository, WorkspaceGitService, FileVersionService, etc., if they are only used via the facade. |
| **Verify** | All operations still work; no missing dependency. Prefer doing this after handlers are in place so the facade is implemented in terms of the same services the handlers use. |

**Risk:** Medium. DI registration and constructor changes; ensure the facade is registered and all code paths use it correctly.

---

### 4.2 — Point 10: Single responsibility per component

| Action | Detail |
|--------|--------|
| **Optional split** | If the page is still large or has distinct “zones,” consider splitting into: (1) **WorkspaceRepositoriesPage** — route, layout, header, one main child; (2) **WorkspaceRepositoriesGrid** — table, level headers, rows, loading/empty states, search filter applied to data; (3) **WorkspaceRepositoriesHeader** — already exists; reuse. The page would own workspace load, errors, and modal/overlay state and pass data + callbacks into the grid. |
| **Responsibility** | Page: load workspace, manage errors and global state, show/hide modals and overlays, delegate actions to handlers. Grid: display and filter repos, emit row/level events. Handlers: run sync, update, push, commit-sync, branch operations. |
| **Verify** | No UX or URL change; only file boundaries and responsibility boundaries clarified. |

**Risk:** Low to medium. Can be done last and only if the team wants a clearer split; otherwise Phase 1–3 already achieve most of the maintainability gain.

---

## Summary: All 10 Points Mapped to Phases

| # | Best practice | Phase | Action |
|---|----------------|-------|--------|
| 1 | Move logic to code-behind or view model | 1.1 | Add `WorkspaceRepositories.razor.cs` and move `@code` into it. |
| 2 | Extract feature-based services or handlers | 2.1 | Add Sync, Update, Push, CommitSync, Branch handlers; page delegates to them. |
| 3 | Extract modal state and commands | 2.2 | One state object per modal; Open/Close/Confirm methods; bind markup to state. |
| 4 | Reuse sync/update/push orchestration | 1.2 | `RunSyncCoreAsync`, shared commit-sync and sync-to-default core; thin entry points. |
| 5 | Keep markup focused on structure and binding | 3.1 | No new logic in Razor; only structure and bindings. |
| 6 | Use partials or subcomponents for table sections | 3.2 | `WorkspaceRepositoriesLevelHeader`, `WorkspaceRepositoriesRow`; page loops and passes data/events. |
| 7 | Centralize URLs and navigation | 0.2 | `WorkspaceUrlHelper` for dependency graph URLs; page uses helper. |
| 8 | Limit injected services in the page | 4.1 | One facade service for workspace operations; page and handlers use it. |
| 9 | Extract API and DTOs | 0.1 | `CreateBranchApiResult` in `Models/Api`; remove nested class from page. |
| 10 | Single responsibility per component | 4.2 | Optional: split page into page + grid; clear ownership of load vs display vs actions. |

---

## Suggested order of implementation

1. **Phase 0** — DTO and URL helper (quick wins, no behavior change).  
2. **Phase 1.1** — Code-behind (enables all other refactors).  
3. **Phase 1.2** — Sync/commit-sync/sync-to-default consolidation (fewer bugs, easier to change).  
4. **Phase 2.2** — Modal state (one modal at a time; reduces field sprawl).  
5. **Phase 2.1** — Handlers (one handler at a time; e.g. CommitSync → Sync → Update → Push → Branch).  
6. **Phase 3.2** — Level header and row components (biggest markup reduction).  
7. **Phase 3.1** — Cleanup markup and bindings.  
8. **Phase 4.1** — Facade service (after handlers exist).  
9. **Phase 4.2** — Optional page/grid split.

---

## Verification checklist (after each phase)

- [ ] Build succeeds; no new warnings in refactored code.
- [ ] Page loads for a workspace with repos.
- [ ] Sync (all / level / single repo) works; progress and errors show correctly.
- [ ] Update (with/without commits) works; version-files commit modal appears when expected.
- [ ] Push (header and badge, with/without deps) works.
- [ ] Pull and commit sync (single and level) work.
- [ ] Branch modal: common branches load; create branches works.
- [ ] Switch branch modal: list branches, checkout, create branch, sync to default work.
- [ ] Repositories modal: open, fetch, save assignment work.
- [ ] Confirm modal appears for sync level, open PR, open GitHub, etc.
- [ ] Search/filter and clear search work; Escape clears.
- [ ] Dependency graph links (repo and level) navigate correctly.
- [ ] Version copy, dependency badge click/tooltip, dismiss repo error work.

---

## Document info

- **Created:** Refactoring plan for `WorkspaceRepositories.razor`.
- **Related:** `WorkspaceRepositories-razor-Analysis.md` (method list and best practices).
- **Scope:** Safe, incremental refactor covering all 10 best practices; UX and functionality unchanged.
