# WorkspaceRepositories.razor — Deep Analysis

**File:** `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.razor`  
**Approximate size:** ~2,845 lines (markup + @code)  
**Purpose:** Page that displays a workspace’s repositories in a table (grouped by dependency level), with sync, update, push, pull, branch, and dependency actions.

---

## 1. Methods by Responsibility

### 1.1 Lifecycle & Initialization

| Method | Responsibility |
|--------|----------------|
| `OnInitializedAsync()` | Loads workspace via `LoadWorkspaceAsync()` and applies sync state from workspace. |
| `OnAfterRenderAsync(bool firstRender)` | On first render: focuses search box; builds SignalR hub for workspace sync and repository errors; starts hub. |
| `Dispose()` | Cancels and disposes all CancellationTokenSources and hub connection. |

---

### 1.2 Data Loading & Refresh

| Method | Responsibility |
|--------|----------------|
| `LoadWorkspaceAsync()` | Sets loading state, clears error, calls `ReloadWorkspaceDataAsync()`, kicks off background PR refresh; on exception sets error and clears repos. |
| `ReloadWorkspaceDataAsync()` | Gets workspace by ID, builds ordered `workspaceRepositories`, builds `prByRepositoryId`, loads mismatched dependency lines. |
| `ReloadWorkspaceDataFromFreshScopeAsync()` | Same as above but using a new `IServiceScope` (fresh DbContext) to avoid EF cache; used after sync/update. |
| `ReloadWorkspaceDataAfterCancelAsync()` | Reloads from fresh scope and applies sync state; swallows `ObjectDisposedException` / `InvalidOperationException`. |
| `RefreshFromSync()` | When not syncing/updating: reloads from fresh scope, applies sync state, triggers `StateHasChanged`. Used after SignalR WorkspaceSynced and after update. |
| `ApplySyncStateFromWorkspace()` | Builds `repoSyncStatus` and `isOutOfSync` from current `workspaceRepositories` and calls `StateHasChanged`. |
| `BuildPrByRepositoryIdFromLinks(List<WorkspaceRepositoryLink> links)` | Static. Builds dictionary of repository ID → `PullRequestInfo?` from workspace links that have a pull request. |
| `RefreshPullRequestsInBackgroundAndReloadAsync()` | Refreshes PRs for workspace, reloads data; for newly merged PRs refreshes branches for those repos. |
| `RefreshBranchesForRepositoryAsync(int repositoryId)` | Calls `/api/branches/refresh` for one repo (e.g. after PR merge). |
| `LoadMismatchedDependencyLinesAsync()` | If any repo has unmatched deps, gets update plan and builds `_mismatchedDependencyLinesByRepo` for tooltip lines. |

---

### 1.3 Progress & State Setters

| Method | Responsibility |
|--------|----------------|
| `SetSyncProgress(string message)` | Sets `syncProgressMessage` and invokes `StateHasChanged`. |
| `SetUpdateProgress(string message)` | Sets `updateProgressMessage` and invokes `StateHasChanged`. |
| `SetPushProgress(string message)` | Sets `pushProgressMessage` and invokes `StateHasChanged`. |
| `SetCommitSyncProgress(string message)` | Sets `commitSyncProgressMessage` and invokes `StateHasChanged`. |
| `SetCheckoutProgress(string message)` | Sets `checkoutProgressMessage` and invokes `StateHasChanged`. |
| `SetCreateBranchesProgress(string message)` | Sets `createBranchesProgressMessage` and invokes `StateHasChanged`. |
| `OnCommitDependencyProgress(int current, int total, int unused)` | Sets update progress to "Committed {current} of {total}". |

---

### 1.4 Abort / Cancel

| Method | Responsibility |
|--------|----------------|
| `AbortSyncAsync()` | Cancels `_syncCts`. |
| `AbortUpdateAsync()` | Cancels `_updateCts`. |
| `AbortPushAsync()` | Cancels `_pushCts`. |
| `AbortCommitSyncAsync()` | Cancels `_commitSyncCts`. |
| `AbortCheckoutAsync()` | Cancels `_checkoutCts`. |
| `AbortFetchRepositories()` | Cancels `_fetchRepositoriesCts`. |

---

### 1.5 Confirm Modal (Generic)

| Method | Responsibility |
|--------|----------------|
| `ShowConfirm(string message, Func<Task> onConfirm, string confirmButtonText)` | Sets confirm modal message, button text, and pending action; shows modal. |
| `CloseConfirmModal()` | Hides confirm modal and clears pending action. |
| `OnConfirmModalYesAsync()` | Runs pending confirm action (if any) after closing modal. |

---

### 1.6 Sync (Full, Level, Single Repo)

| Method | Responsibility |
|--------|----------------|
| `SyncAsync()` | Full workspace sync: creates fresh scope, runs `WorkspaceGitService.SyncAsync`, updates in-memory repo state and `repoSyncStatus`, reloads data, applies sync state, sets per-repo errors from results. |
| `SyncLevelAsync(List<int> repositoryIds)` | Same as sync but only for given repo IDs; uses `skipDependencyLevelPersistence: true`. |
| `SyncSingleRepoAsync(int repositoryId)` | Same as sync but for a single repository. |

---

### 1.7 Commit Sync (Pull)

| Method | Responsibility |
|--------|----------------|
| `CommitSyncAsync(int repositoryId)` | Calls `/api/commitsync` for one repo; handles merge conflict and errors in `repositoryErrors`; then `RefreshFromSync()`. |
| `CommitSyncLevelAsync(List<int> repositoryIds)` | Calls `/api/commitsync` for each repo in parallel; aggregates errors and progress; then `RefreshFromSync()`. |
| `ShowConfirmSyncCommits(int repositoryId)` | Shows confirm "Do you want to sync commits for this repository?" then runs `CommitSyncAsync(repositoryId)`. |
| `ShowConfirmSyncCommitsLevel(List<int> repositoryIds)` | If count ≤ 1 runs commit sync immediately; else shows confirm then `CommitSyncLevelAsync`. |

---

### 1.8 Update Dependencies

| Method | Responsibility |
|--------|----------------|
| `OnUpdateClickAsync()` | Gets update plan; if empty may run file-version update and show version-files commit modal; else shows update modal (single vs multi-level). |
| `OnUpdateProceedAsync()` | Closes update modal and runs `RunUpdateCoreAsync(withCommits: true)`. |
| `OnUpdateOnlyAsync()` | Closes update modal and runs `RunUpdateCoreAsync(withCommits: false)`. |
| `RunUpdateCoreAsync(bool withCommits)` | Runs full update (refresh, sync deps, optional commits); handles file-version updates and version-files commit modal or confirm for "Update only" path. |
| `CommitPayloadAfterUpdateOnlyAsync(IReadOnlyList<SyncDependenciesRepoPayload> payload)` | Commits dependency updates for "Update only" path via `CommitDependencyUpdatesAsync`, then tries file-version update and refresh. |
| `ShowConfirmUpdateDependenciesAsync(int repositoryId, int unmatchedCount)` | Gets update plan for single repo; shows UpdateSingleRepositoryDependenciesModal with payload. |
| `OnUpdateSingleRepositoryDependenciesProceedAsync(bool commitVersionUpdate)` | Runs update for single repo, optionally commits dependency updates; may open version-files commit modal. |
| `CloseUpdateSingleRepositoryDependenciesModal()` | Hides single-repo update modal and clears payload. |
| `UpdateSingleRepositoryAsync(int repositoryId)` | Runs update for one repo only (refresh, sync deps, no commit); same overlay and error handling as full update. |
| `TryUpdateFileVersionsAsync()` | Calls `FileVersionService.UpdateAllVersionsAsync`; shows toasts for updated files; logs warnings on failure. |
| `RunFileVersionUpdateAndGetUpdatedFilesAsync()` | Runs file-version update without toasts; returns list of updated files (repo, path) for commit/dialog. |
| `CloseUpdateModal()` | Hides update modal. |

---

### 1.9 Version Files Commit Modal

| Method | Responsibility |
|--------|----------------|
| `OpenVersionFilesCommitModal(string message, IReadOnlyList<...> byRepo, IReadOnlyList<string> filesForDisplay)` | Sets message, by-repo data, file list and shows version-files commit modal. |
| `CloseVersionFilesCommitModal()` | Hides modal and clears related state. |
| `OnVersionFilesCommitProceedAsync()` | Closes modal and runs `CommitFileVersionUpdatesAsync(byRepo)`. |
| `CommitFileVersionUpdatesAsync(IReadOnlyList<(int RepoId, string RepoName, IReadOnlyList<string> FilePaths)> byRepo)` | Commits file paths per repo via `CommitFilePathsAsync`; then file-version update and refresh. |

---

### 1.10 Push

| Method | Responsibility |
|--------|----------------|
| `OnPushClickAsync()` | Gets push plan and dependency info; if no package deps pushes immediately; else shows PushWithDependenciesModal. |
| `OnPushBadgeClickAsync(int repositoryId, string? branchName)` | Gets push dependency info for repo; if no deps pushes directly; else shows PushWithDependenciesModal. |
| `OnPushWithDependenciesProceedAsync(bool synchronizedPush)` | From modal: either synchronized push (sync registries then level-order push) or parallel push; then refresh. |
| `PushSingleRepositoryWithUpstreamAsync(int repositoryId, string? branchName)` | Pushes single repo with upstream via service; uses page overlay. |
| `ClosePushWithDependenciesModal()` | Hides push-with-deps modal and clears related state. |

---

### 1.11 Pull (Header)

| Method | Responsibility |
|--------|----------------|
| `OnPullClickAsync()` | If any repo has incoming commits: shows confirm then runs `CommitSyncAsync` or `CommitSyncLevelAsync` for those repos. |
| `OnPullBadgeClickAsync(int repositoryId)` | Shows confirm "Do you want to pull for this repository?" then `CommitSyncAsync(repositoryId)`. |

---

### 1.12 Level / Row Confirm Actions (Open PR, Open GitHub, Sync Level, Sync to Default)

| Method | Responsibility |
|--------|----------------|
| `ShowConfirmOpenPr(IEnumerable<WorkspaceRepositoryLink> group)` | Builds list of PR or compare URLs for group; if count ≥ threshold shows confirm; else opens URLs via JS. |
| `ShowConfirmOpenGitHub(int count, IReadOnlyList<string?> urls)` | If count ≤ 1 opens URLs; else shows confirm then opens via JS. |
| `ShowConfirmSyncLevel(List<int> repositoryIds)` | If count &lt; threshold runs `SyncLevelAsync`; else shows confirm then sync. |
| `ShowConfirmSyncToDefaultLevel(List<int> repositoryIds)` | Filters to repos not on default branch; if none shows toast; else calls `CheckBranchesAndConfirmSyncToDefaultLevel`. |
| `CheckBranchesAndConfirmSyncToDefaultLevel(List<int> repositoryIds)` | Checks ahead/upstream/PR merged; skips blocked repos with toast; shows confirm for safe repos then `SyncToDefaultLevelAsync`. |
| `SyncToDefaultLevelAsync(List<int> repositoryIds)` | For each repo (with semaphore): optionally delete remote branch, then call sync-to-default API; aggregates errors and refresh. |
| `SyncToDefaultFromModalAsync(...)` | From SwitchBranchModal: checks ahead/PR merged; calls `SyncToDefaultSingleRepoAfterCheckAsync`. |
| `SyncToDefaultSingleRepoAfterCheckAsync(...)` | Single-repo sync to default: optional delete remote, then sync-to-default API; updates errors and refresh. |

---

### 1.13 Repositories Modal (Assign Repos to Workspace)

| Method | Responsibility |
|--------|----------------|
| `ShowRepositoriesModalAsync()` | Sets selected IDs from workspace, ensures modal repo list, shows modal. |
| `EnsureRepositoriesForModalAsync()` | Loads connectors and persisted repositories for modal if needed. |
| `CloseRepositoriesModal()` | Hides modal and clears error. |
| `SaveRepositoriesAsync()` | Validates selection, calls `WorkspaceRepository.UpdateAsync`, closes modal, reloads data and sync state. |
| `FetchRepositoriesAsync()` | Refreshes repos from GitHub via `RepositoryService.RefreshRepositoriesAsync` with progress; updates modal list. |

---

### 1.14 Branch Modal (Create Branch for All)

| Method | Responsibility |
|--------|----------------|
| `ShowBranchModalAsync()` | Fetches common branches from `/api/branches/common`, sets branch modal data, shows modal. |
| `CloseBranchModal()` | Hides branch modal. |
| `CreateBranchesAsync((string NewBranchName, string BaseBranch) args)` | Creates branch in all repos via `WorkspaceGitService.CreateBranchesAsync`; then refresh. |

---

### 1.15 Switch Branch Modal (Single Repo)

| Method | Responsibility |
|--------|----------------|
| `ShowSwitchBranchModal(int repositoryId, string? currentBranch, string? cloneUrl)` | Sets modal state from workspace repo and shows SwitchBranchModal. |
| `CloseSwitchBranchModal()` | Hides modal and clears state. |
| `OnBranchChangedAsync()` | Refreshes workspace data (e.g. after branch change from modal). |
| `CreateSingleBranchAsync(...)` | Creates one branch via `/api/branches/create`; optionally sets upstream via `/api/branches/set-upstream`; then refresh. |
| `CheckoutBranchAsync((int RepositoryId, string BranchName) request)` | Calls `/api/branches/checkout`; updates `repositoryErrors`; then refresh. |

---

### 1.16 Search & Filter

| Method | Responsibility |
|--------|----------------|
| `OnSearchChanged(ChangeEventArgs e)` | Updates `searchTerm` from input and calls `StateHasChanged`. |
| `OnSearchKeyDown(KeyboardEventArgs e)` | Clears search when Escape is pressed. |
| `ClearSearchFilter()` | Sets `searchTerm` to empty and calls `StateHasChanged`. |
| `GetFilteredWorkspaceRepositories()` | Filters `workspaceRepositories` by `searchTerm` (words matched against repo name, branch, version, level title, sync badge text). |

---

### 1.17 UI Helpers (Version, Badge, Graph, Errors)

| Method | Responsibility |
|--------|----------------|
| `CopyVersionToClipboard(string version)` | Copies version to clipboard via JS, shows toast, adds to `clickedVersions` for highlight. |
| `OnVersionMouseLeave(string? version)` | Removes version from `clickedVersions` so hover can show again. |
| `OnDependencyBadgeClick(int repositoryId, int unmatchedDeps)` | Adds repo to `clickedDependencyBadges` and shows update-dependencies confirm/flow. |
| `OnDependencyBadgeMouseLeave(int repositoryId)` | Removes repo from `clickedDependencyBadges` and refreshes. |
| `HandleDependencyBadgeKeydown(KeyboardEventArgs e, int repositoryId, int unmatchedDeps)` | On Enter/Space opens update-dependencies flow. |
| `GetDependencyGraphUrl(int repositoryId)` | Returns `/workspaces/{WorkspaceId}/dependencies?repo={repositoryId}`. |
| `GetDependencyGraphUrlForLevel(int level)` | Returns `/workspaces/{WorkspaceId}/dependencies?level={level}`. |
| `DismissRepositoryError(int repositoryId)` | Removes error for repo from `repositoryErrors` and refreshes. |

---

### 1.18 Helpers (PR / Repo)

| Method | Responsibility |
|--------|----------------|
| `IsPrMergedForRepo(int repositoryId)` | Returns whether `prByRepositoryId` has a merged PR for that repo. |

---

## 2. Properties / Computed (used like methods)

- **FilteredWorkspaceRepositories** — calls `GetFilteredWorkspaceRepositories()`.
- **LevelGroups** — groups `workspaceRepositories` by dependency level, ordered.
- **FilteredLevelGroups** — same grouping on filtered list.
- **ApiBaseUrl** — `NavigationManager.BaseUri` trimmed.

---

## 3. Nested Type

- **CreateBranchApiResult** — DTO for `/api/branches/create` (and set-upstream) response: `Success`, `Error`.

---

## 4. Summary Counts

- **Lifecycle / init:** 3  
- **Data load / refresh:** 10  
- **Progress setters:** 7  
- **Abort:** 6  
- **Confirm modal:** 3  
- **Sync (full/level/single):** 3  
- **Commit sync (pull):** 4  
- **Update dependencies:** 11  
- **Version files commit modal:** 4  
- **Push:** 5  
- **Pull (header/badge):** 2  
- **Level/row confirm (PR, GitHub, sync, sync to default):** 8  
- **Repositories modal:** 5  
- **Branch modal:** 3  
- **Switch branch modal:** 5  
- **Search/filter:** 4  
- **UI helpers (version, badge, graph, errors):** 8  
- **Helpers (PR):** 1  

**Total methods (excluding nested class):** ~95+.

---

## 5. Best Practices to Keep Razor Files Lean and Slim

1. **Move logic to a code-behind or view model**
   - Use a partial class (e.g. `WorkspaceRepositories.razor.cs`) or a dedicated component/view model class.
   - Keep the `.razor` file for: directives, markup, and binding to a small set of methods/properties that delegate to the other class.

2. **Extract feature-based services or handlers**
   - Group by feature: e.g. `WorkspaceSyncHandler`, `WorkspaceUpdateHandler`, `WorkspacePushHandler`, `BranchModalHandler`, `RepositoriesModalHandler`.
   - Page only injects these and calls high-level methods; handlers own progress, cancellation, and error handling for their flow.

3. **Extract modal state and commands**
   - One object per modal (or one “modal coordinator”) holding visibility, message, payload, and actions (Open, Close, Confirm).
   - Razor binds to that object; open/close/confirm methods live in the handler or a dedicated modal service.

4. **Reuse sync/update/push orchestration**
   - `SyncAsync`, `SyncLevelAsync`, and `SyncSingleRepoAsync` share most logic (progress callback, error aggregation, reload). Extract a single “RunSyncAsync(repoIds?)” and call it from three thin methods.
   - Same idea for commit-sync and sync-to-default: one core implementation, multiple entry points.

5. **Keep markup focused on structure and binding**
   - Avoid large `@code` blocks in the same file. Prefer small, readable markup that references parameters and callbacks.
   - Use child components for repeated UI (e.g. level header, repo row) and pass only the data and callbacks they need.

6. **Use partials or subcomponents for table sections**
   - E.g. a component for “dependency level header” and one for “repository row” (or “level group”). That shrinks the main file and isolates row/level logic.

7. **Centralize URLs and navigation**
   - `GetDependencyGraphUrl` and `GetDependencyGraphUrlForLevel` can live in a small `WorkspaceUrlHelper` or route helper used by the page and other components.

8. **Limit injected services in the page**
   - Prefer injecting one or two “facade” or “page” services that wrap WorkspaceGitService, FileVersionService, PR, etc. The page then talks to the facade instead of many services.

9. **Extract API and DTOs**
   - Move `CreateBranchApiResult` and any other response DTOs to a shared API/Models folder so the page stays free of data contracts.

10. **Single responsibility per component**
    - This page currently does: grid, search, sync, update, push, pull, branch creation, checkout, sync-to-default, repos assignment, and multiple modals. Splitting by feature (e.g. “workspace grid” vs “workspace actions” vs “branch modals”) will make each file easier to maintain and test.

Applying these will reduce the size of `WorkspaceRepositories.razor` and make it easier to extend and refactor.
