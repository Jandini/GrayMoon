# WorkspaceRepositories - Behavioral Baseline

**Captured:** 2026-06-30  
**Purpose:** Pre-refactoring snapshot for Stage 0 of the WorkspaceRepositories code-behind split.  
**Constraint:** This document reflects the state before any runtime code changes.

---

## 1. Total line count

| File | Lines | Role |
|------|------:|------|
| `WorkspaceRepositories.razor` | 351 | Razor markup + @inject directives |
| `WorkspaceRepositories.razor.cs` | 4,115 | Single code-behind (no existing partials) |
| `WorkspaceRepositories.razor.css` | 865 | Scoped CSS |
| `WorkspaceRepositoriesRow.razor` | 562 | Per-row child component |
| `WorkspaceRepositoriesLevelHeader.razor` | 128 | Per-level header child component |
| `WorkspaceRepositoriesHeader.razor` | 502 | Page header/action bar child component |
| `WorkspaceRepositoriesModal.razor` | 292 | Repository assignment modal |
| `WorkspaceRepositoriesModal.razor.css` | 210 | Modal CSS |
| `WorkspaceRepositoriesHeader.razor.css` | 65 | Header CSS |

**Code-behind total: 4,115 lines (all in one file).**

---

## 2. Existing partial files

None. The entire code-behind is `WorkspaceRepositories.razor.cs`. No partial files exist.

---

## 3. All component fields grouped by state group

### 3.1 Workspace / display state

```
workspace                       Workspace?
workspaceRepositories           List<WorkspaceRepositoryLink>
prByRepositoryId                IReadOnlyDictionary<int, PullRequestInfo?>
_lastPrRefreshByRepoId          Dictionary<int, DateTime>
PrRefreshThrottle               static readonly TimeSpan (10 s)
errorMessage                    string?
isLoading                       bool
isOutOfSync                     bool?
repoSyncStatus                  Dictionary<int, RepoSyncStatus>
repositoryErrors                Dictionary<int, string>   (repositoryId -> message)
clickedVersions                 HashSet<string>
clickedDependencyBadges         HashSet<int>
TableColSpan                    const int = 4
TagBlockedActionMessage         const string
```

Computed display properties (no backing field):
```
hasUnmatchedDependencies        bool  (expression-bodied)
isPushRecommended               bool
lowestLevelNeedingWork          int?
hasTaggedRepos                  bool
hasIncomingCommits              bool
LevelGroups                     IEnumerable<IGrouping<int?,WorkspaceRepositoryLink>>
ApiBaseUrl                      string
RepositoriesModalTitle          string
ShowRepositoriesFetchOverlay    bool
RepositoriesFetchOverlayMessage string
```

### 3.2 Dependency / file-version tooltip data

```
_mismatchedDependencyLinesByRepo    IReadOnlyDictionary<int, IReadOnlyList<(string PackageId, string CurrentVersion, string NewVersion)>>
_fileLineStatusByRepo               IReadOnlyDictionary<int, IReadOnlyList<WorkspaceFileLineStatus>>
_mismatchedFileVersionLinesByRepo   IReadOnlyDictionary<int, IReadOnlyList<(string FileName, string TokenName, string CurrentValue, string ExpectedValue)>>
_allDependencyLinesByRepo           IReadOnlyDictionary<int, IReadOnlyList<(string PackageId, string Version)>>
_allFileVersionLinesByRepo          IReadOnlyDictionary<int, IReadOnlyList<(string FileName, string TokenName, string Version)>>
_customDependencyLinesByRepo        IReadOnlyDictionary<int, IReadOnlyList<string>>
```

### 3.3 Modal state

| Field | Type | Kind |
|-------|------|------|
| `_repositoriesModal` | `RepositoriesModalState` | mutable class (needs in-place mutation during fetch) |
| `_switchBranchModal` | `SwitchBranchModalState` | sealed record |
| `_branchModal` | `BranchModalState` | sealed record |
| `_updateModal` | `UpdateModalState` | sealed record |
| `_updateAndPushModal` | `UpdateModalState` | sealed record |
| `_levelOnlyUpdateAndPushModal` | `LevelOnlyUpdateAndPushModalState` | sealed record |
| `_updateSingleRepoModal` | `UpdateSingleRepoDependenciesModalState` | sealed record |
| `_customDependenciesModal` | `CustomDependenciesModalState` | mutable class |
| `_pushWithDependenciesModal` | `PushWithDependenciesModalState` | sealed record |
| `_confirmModal` | `ConfirmModalState` | sealed record |
| `_defaultBranchWarningModal` | `DefaultBranchWarningModalState` | sealed record |
| `_versionFilesCommitModal` | `VersionFilesCommitModalState` | sealed record |
| `_syncToDefaultOptionsModal` | `SyncToDefaultOptionsModalState` | sealed record |
| `_undoPushModal` | `UndoPushModalState` | sealed record |
| `_newPrModal` | `NewPullRequestModalState` | sealed record |
| `_newFeatureModal` | `NewFeatureModalState` | sealed record |
| `_operationErrorModal` | `OperationErrorModalState` | sealed record |
| `_syncToDefaultCheckResults` | `IReadOnlyList<(int RepoId, int? DefaultAhead, bool? HasUpstream)>?` | nullable check result |

### 3.4 Operation state (search)

```
searchTerm                      string (empty by default)
_filteredWorkspaceRepositories  List<WorkspaceRepositoryLink>
FilteredWorkspaceRepositories   List<WorkspaceRepositoryLink> (computed getter)
FilteredLevelGroups             IEnumerable<IGrouping<int?,WorkspaceRepositoryLink>> (computed)
HasSearchFilter                 bool (computed)
```

### 3.5 Lifecycle / disposal state

```
_disposed       bool
_wasJobRunning  bool
PageJobKey      string (computed: Uri.AbsolutePath.ToLowerInvariant())
IsJobRunning    bool   (computed: JobService.IsRunning(PageJobKey))
AgentTasksPendingCount  int (computed)
_hubConnection  HubConnection?
_fetchRepositoriesCts   CancellationTokenSource?
```

### 3.6 Synchronization primitives

```
RefreshDebounceMs       const int = 200
_refreshDebounceCts     CancellationTokenSource?
_refreshDebounceLock    readonly object
_loadMismatchedDepsLock SemaphoreSlim(1,1)  -- disposed in Dispose()
```

---

## 4. All event-handler methods grouped by feature

### 4.1 Loading and refresh

| Method | Lines (approx.) | Notes |
|--------|----------------:|-------|
| `OnInitializedAsync` | 114-121 | Lifecycle entry; subscribes events, calls LoadWorkspaceAsync |
| `ApplySyncStateFromWorkspace` | 180-191 | Builds repoSyncStatus, sets isOutOfSync |
| `LoadWorkspaceAsync` | 239-258 | try/finally; sets isLoading; fires background PR refresh |
| `ReloadWorkspaceDataAsync` | 260-278 | Uses circuit-scoped WorkspacePageService |
| `ReloadWorkspaceDataFromFreshScopeAsync` | 395-416 | Creates fresh scope for WorkspaceRepository |
| `ReloadWorkspaceDataAfterCancelAsync` | 371-392 | Safe wrapper; swallows ObjectDisposed/InvalidOperation |
| `RefreshFromSync` | 232-237 | Calls ReloadWorkspaceDataFromFreshScopeAsync then ApplySyncState |
| `LoadMismatchedDependencyLinesAsync` | 418-516 | Semaphore-guarded; loads 5 data dictionaries from fresh scope |
| `GetRepositoryTypeSortOrder` | 281-289 | Static helper; sort order for ProjectType enum |
| `BuildPrByRepositoryIdFromLinks` | 291-297 | Static helper; builds prByRepositoryId dict |
| `RefreshPullRequestsInBackgroundAndReloadAsync` | 300-331 | Fire-and-forget; refreshes PRs from API |
| `RefreshPrOnBadgeEnterAsync` | 334-354 | Per-repo PR refresh on mouse-enter; throttled |
| `RefreshBranchesForRepositoryAsync` | 356-368 | Fresh scope; WorkspaceGitService.RefreshBranchesAndBroadcastAsync |

### 4.2 SignalR / real-time

| Method | Lines (approx.) | Notes |
|--------|----------------:|-------|
| `OnAfterRenderAsync` | 125-178 | First render only: JS focus + HubConnection setup |
| `OnJobServiceChanged` | 211-223 | Fires RefreshFromSync when job transitions running->completed |
| `OnQueueStateChanged` | 123 | Invokes StateHasChanged |
| `SafeInvoke` | 225-229 | Dispatches action to UI thread; guards _disposed |

### 4.3 Search / filtering

| Method | Notes |
|--------|-------|
| `OnSearchChanged` | Updates searchTerm, calls UpdateFilteredRepositories |
| `OnSearchKeyDown` | Escape clears search |
| `ClearSearchFilter` | Clears searchTerm |
| `GetFilteredWorkspaceRepositories` | Applies WorkspaceRepositoryLinkSearchMatcher |
| `UpdateFilteredRepositories` | Private setter wrapper |

### 4.4 Pull requests

| Method | Notes |
|--------|-------|
| `OpenPullRequestDialogForAllRepositoriesAsync` | Delegates to core |
| `OpenPullRequestDialogForRepositoryAsync` | Single repo delegate |
| `OpenPullRequestDialogForRepositoriesAsync` | Level-group delegate |
| `OpenPullRequestDialogCoreAsync` | Filters eligible repos; shows modal or toast |
| `CloseNewPullRequestModal` | Closes + aborts running job |
| `HandleNewPrOpenInGitHubAsync` | Opens GitHub compare URLs; confirm if > 5 tabs |
| `HandleCreatePullRequestsAsync` | Validates form; delegates to ShowConfirm -> Execute |
| `ExecuteCreatePullRequestsAsync` | Background job: creates PRs, refreshes PR state |

### 4.5 Dependency updates

| Method | Notes |
|--------|-------|
| `ShowConfirmUpdateDependenciesAsync` | Gets update plan from fresh scope; optional default-branch warning |
| `OpenUpdateSingleRepoModalAsync` | Shows UpdateSingleRepoDependenciesModal |
| `CloseUpdateSingleRepositoryDependenciesModal` | Closes modal |
| `OnUpdateSingleRepositoryDependenciesProceedAsync` | Background job: WorkspaceUpdateHandler.RunUpdateAsync (single repo) |
| `UpdateSingleRepositoryAsync` | Background job: WorkspaceGitService.RunUpdateSingleRepositoryAsync |
| `HandleDependencyBadgeKeydown` | Enter/Space on dependency badge |
| `OnDependencyBadgeClick` | Tracks clicked badge; delegates to ShowConfirmUpdateDependencies |
| `GetMismatchedDependencyLines` | Lookup from _mismatchedDependencyLinesByRepo |
| `GetAllDependencyLines` | Lookup from _allDependencyLinesByRepo |
| `GetCustomDependencyLines` | Lookup from _customDependencyLinesByRepo |
| `ShowCustomDependenciesModalAsync` | Fresh scope: loads 3 services; builds modal state |
| `CloseCustomDependenciesModal` | Resets to new instance |
| `SaveCustomDependenciesAsync` | Fresh scope: saves + recomputes workspace sync |

### 4.6 File-version updates

| Method | Notes |
|--------|-------|
| `OnUpdateFilesClickAsync` | Background job: UpdateAllVersionsAsync + CheckAndPersist |
| `UpdateSingleRepositoryFileVersionsAsync` | Background job: single repo file version update |
| `OnFileDependencyBadgeClick` | Tracks badge; delegates to ShowFileVersionsCommitFlowAsync |
| `ShowFileVersionsCommitFlowAsync` | Optional default-branch warning; opens commit modal |
| `ShowVersionFilesCommitModalAsync` | Populates VersionFilesCommitModal |
| `OnVersionFilesCommitProceedAsync` | Sets IsBusy; invokes pending action |
| `CloseVersionFilesCommitModal` | Resets modal |
| `CommitFileVersionUpdateAsync` | Background job: update + optional commit + check + refresh |
| `GetMismatchedFileVersionLines` | Lookup |
| `GetFileLineStatus` | Lookup |
| `GetAllFileVersionLines` | Lookup |

### 4.7 Push

| Method | Notes |
|--------|-------|
| `OnPushClickAsync` | Gets push plan; shows modal or pushes immediately |
| `OnPushBadgeClickAsync` | Per-repo push; optional default-branch warning |
| `PushBadgeClickCoreAsync` | Gets dep info; shows PushWithDependencies modal or pushes directly |
| `ClosePushWithDependenciesModal` | Closes modal |
| `OnPushWithDependenciesProceedAsync` | Background job: ExecutePushCoreAsync |
| `BuildPushPlanAsync` | Fresh scope: 2 services; returns (repoIds, packageIds) |
| `ExecutePushCoreAsync` | Core push helper; finally-block refresh |
| `PushSingleRepositoryWithUpstreamAsync` | Single-repo push with upstream setup |
| `RestorePackagesAsync` | Background job: RestorePackagesCoreAsync |
| `RestorePackagesCoreAsync` | Fresh scope: WorkspaceGitService.RestoreAllWorkspacePackagesAsync |
| `RestoreSyncedPackagesCoreAsync` | Fresh scope: WorkspaceGitService.RestoreSyncedWorkspacePackagesAsync |

### 4.8 Pull / commit sync

| Method | Notes |
|--------|-------|
| `OnPullClickAsync` | Finds repos with incoming commits; delegates to CommitSync |
| `OnPullBadgeClickAsync` | Per-repo pull badge; fire-and-forget CommitSyncAsync |
| `CommitSyncAsync` | Background job: WorkspaceCommitSyncHandler.CommitSyncAsync |
| `CommitSyncLevelAsync` | Background job: WorkspaceCommitSyncHandler.CommitSyncLevelAsync |
| `ShowConfirmSyncCommits` | Shows ConfirmModal -> CommitSyncAsync |
| `ShowConfirmSyncCommitsLevel` | Filters tags; shows confirm or runs directly |

### 4.9 Sync (git fetch/status)

| Method | Notes |
|--------|-------|
| `SyncAsync` | Delegates to RunSyncJobAsync; clears error |
| `SyncLevelAsync` | Per-level sync |
| `SyncSingleRepoAsync` | Single repo sync |
| `RunSyncJobAsync` | Background job: WorkspaceSyncHandler.RunSyncAsync; handles AgentNotConnectedException + ConnectorHealthException |
| `ShowConfirmSyncLevel` | Confirm threshold of 10 repos |

### 4.10 Branches

| Method | Notes |
|--------|-------|
| `ShowSwitchBranchModal` | Opens single-repo branch modal |
| `CloseSwitchBranchModal` | Resets modal |
| `ShowSwitchBranchModalOnTagsTab` | Opens on tags tab |
| `ShowBranchModalAsync` | Loads common branches; opens workspace-wide branch modal |
| `CloseBranchModal` | Closes modal |
| `GetUnifiedWorkspaceCurrentBranch` | Static; returns branch name if all repos share one |
| `LoadCommonBranchesForBranchModalAsync` | Calls WorkspaceBranchHandler.GetCommonBranchesAsync |
| `FetchCommonBranchesAcrossWorkspaceAsync` | Background job: parallel per-repo RefreshBranchesForRepository |
| `CheckoutCommonBranchAcrossWorkspaceAsync` | Background job: WorkspaceBranchHandler.CheckoutBranchForWorkspaceAsync |
| `CreateBranchesAsync` | Background job: WorkspaceBranchHandler.CreateBranchesAsync |
| `OnBranchChangedAsync` | Calls RefreshFromSync |
| `CreateSingleBranchAsync` | Background job: WorkspaceBranchHandler.CreateSingleBranchAsync |
| `CheckoutBranchAsync` | Background job: WorkspaceBranchHandler.CheckoutBranchAsync |

### 4.11 Sync to default branch

| Method | Notes |
|--------|-------|
| `ShowConfirmSyncToDefaultLevel` | Filters non-default repos; delegates to CheckBranchesAndConfirm |
| `CheckBranchesAndConfirmSyncToDefaultLevel` | PR refresh + background job fetch + ShowSyncToDefaultOptions modal |
| `IsPrMergedForRepo` | Returns true if PR is merged or closed |
| `SyncToDefaultFromModalAsync` | Per-repo sync from SwitchBranchModal; optional ShowSyncToDefaultOptions |
| `SyncToDefaultSingleRepoAfterCheckAsync` | Background job: WorkspaceGitService.SyncToDefaultDirectAsync |
| `SyncToDefaultLevelAsync` | Background job: parallel SyncToDefaultDirectAsync per repo |
| `SyncAllToDefaultAsync` | Background job: PR refresh + parallel fetch + ShowSyncToDefaultOptions |
| `ExecuteSyncAllToDefaultAsync` | Background job: parallel PR close + SyncToDefaultDirectAsync |

### 4.12 Sync-to-default shared modal helpers

| Method | Notes |
|--------|-------|
| `ShowSyncToDefaultOptions` | Opens SyncToDefaultOptionsModal |
| `CloseSyncToDefaultOptionsModal` | Closes modal |
| `OnSyncToDefaultOptionsProceedAsync` | Invokes pending action |

### 4.13 New feature

| Method | Notes |
|--------|-------|
| `ShowNewFeatureModalAsync` | Loads common branches; opens modal |
| `CloseNewFeatureModal` | Closes modal |
| `HandleNewFeatureCreateAsync` | Background job: NewFeatureOrchestrator.RunAsync + optional push |

### 4.14 Undo push

| Method | Notes |
|--------|-------|
| `OnUndoPushClickAsync` | Opens UndoPushModal with repo/commit list |
| `OnUndoPushProceedAsync` | Background job: UndoPushHandler.RunUndoPushAsync |
| `CloseUndoPushModal` | Closes modal |

### 4.15 Update dependencies (multi-level)

| Method | Notes |
|--------|-------|
| `OnUpdateClickAsync` | Gets update plan; optional default-branch warning; opens modal |
| `OpenUpdateModalAsync` | Shows UpdateDependenciesModal (_updateModal) |
| `CloseUpdateModal` | Closes modal |
| `OnUpdateProceedAsync` | Closes modal; calls RunUpdateCoreAsync |
| `RunUpdateCoreAsync` | Background job: WorkspaceUpdateHandler.RunUpdateAsync |
| `OnUpdateAndPushClickAsync` | Gets update plan; optional warning; opens modal |
| `OpenUpdateAndPushModalAsync` | Shows UpdateDependenciesModal (_updateAndPushModal) |
| `CloseUpdateAndPushModal` | Closes modal |
| `OnUpdateAndPushProceedAsync` | Closes modal; calls RunUpdateAndPushCoreAsync |
| `RunUpdateAndPushCoreAsync` | Background job: Phase 1 update + Phase 2 push plan + Phase 3 push |
| `OnLevelOnlyUpdateAndPushClickAsync` | Gets update plan for lowest level needing work; optional warning |
| `OpenLevelOnlyUpdateAndPushModalAsync` | Shows LevelOnly modal |
| `CloseLevelOnlyUpdateAndPushModal` | Closes modal |
| `OnLevelOnlyUpdateAndPushProceedAsync` | Closes modal; calls RunLevelOnlyUpdateAndPushCoreAsync |
| `RunLevelOnlyUpdateAndPushCoreAsync` | Background job: Phase 1 update + Phase 2 plan + Phase 3 push (level-bounded) |

### 4.16 Workspace repositories modal

| Method | Notes |
|--------|-------|
| `ShowRepositoriesModalAsync` | Loads saved selections; calls EnsureRepositoriesForModalAsync |
| `EnsureRepositoriesForModalAsync` | Loads connectors + repository list on first open |
| `CloseRepositoriesModal` | Closes + clears error |
| `SaveRepositoriesAsync` | Validates; calls WorkspaceRepository.UpdateAsync; reloads |
| `AbortFetchRepositories` | Cancels _fetchRepositoriesCts |
| `FetchRepositoriesAsync` | Fetches repos via RepositoryService.RefreshRepositoriesAsync |

### 4.17 Shared modal helpers (confirm / default-branch warning / operation error)

| Method | Notes |
|--------|-------|
| `ShowConfirm` | Opens ConfirmModal with message + callback |
| `CloseConfirmModal` | Resets ConfirmModal |
| `OnConfirmModalYesAsync` | Invokes PendingAction |
| `ShowDefaultBranchWarning` | Opens DefaultBranchWarningModal |
| `CloseDefaultBranchWarningModal` | Closes modal |
| `OnDefaultBranchWarningProceedAsync` | Invokes PendingAction |
| `ShowOperationError` | Opens OperationErrorModal (fire-and-forget InvokeAsync) |
| `CloseOperationErrorModal` | Closes modal |

### 4.18 Formatting / display helpers

| Method | Notes |
|--------|-------|
| `CopyVersionToClipboard` | JS clipboard; adds to clickedVersions |
| `CopyDependenciesToClipboard` | JS clipboard |
| `OnVersionMouseLeave` | Removes from clickedVersions |
| `OnDependencyBadgeMouseLeave` | Removes from clickedDependencyBadges |
| `DismissRepositoryError` | Removes from repositoryErrors |
| `GetPrInfoForRepository` | Lookup in prByRepositoryId |
| `GetOpenPrUrlsForGroup` | Collects open PR html_urls for a level group |
| `GetOpenPrPullMapForGroup` | Collects baseUrl -> prUrl map for level group |
| `GetRepositoryError` | Lookup in repositoryErrors |
| `GetRepoSyncStatus` | Lookup in repoSyncStatus |
| `IsRepoOnTag` | Returns link.IsOnTag for given repositoryId |

---

## 5. All repeated CreateAsyncScope / GetRequiredService patterns

### 5.1 Single-service scopes (suitable for IScopedServiceExecutor in Stage 4)

| Site / method | Service resolved |
|---------------|-----------------|
| `RefreshBranchesForRepositoryAsync` | `WorkspaceGitService` |
| `ReloadWorkspaceDataFromFreshScopeAsync` | `WorkspaceRepository` |
| `ShowConfirmUpdateDependenciesAsync` | `WorkspaceGitService` |
| `OnUpdateSingleRepositoryDependenciesProceedAsync` (job body) | `WorkspaceUpdateHandler` |
| `UpdateSingleRepositoryAsync` (job body) | `WorkspaceGitService` |
| `OnUpdateClickAsync` (pre-job) | `WorkspaceGitService` |
| `OnUpdateAndPushClickAsync` (pre-job) | `WorkspaceGitService` |
| `OnLevelOnlyUpdateAndPushClickAsync` (pre-job) | `WorkspaceGitService` |
| `RunUpdateCoreAsync` (job body) | `WorkspaceUpdateHandler` |
| `RunUpdateAndPushCoreAsync` phase 1 (job body) | `WorkspaceUpdateHandler` |
| `RunLevelOnlyUpdateAndPushCoreAsync` phase 1 (job body) | `WorkspaceUpdateHandler` |
| `OnUpdateFilesClickAsync` (job body) | `WorkspaceFileVersionService` |
| `UpdateSingleRepositoryFileVersionsAsync` (job body) | `WorkspaceFileVersionService` |
| `ExecutePushCoreAsync` (job body) | `WorkspacePushHandler` |
| `PushSingleRepositoryWithUpstreamAsync` (job body) | `WorkspacePushHandler` |
| `RunSyncJobAsync` (job body) | `WorkspaceSyncHandler` |
| `CommitSyncAsync` (job body) | `WorkspaceCommitSyncHandler` |
| `CommitSyncLevelAsync` (job body) | `WorkspaceCommitSyncHandler` |
| `SyncToDefaultSingleRepoAfterCheckAsync` (job body) | `WorkspaceGitService` |
| `CheckoutCommonBranchAcrossWorkspaceAsync` (job body) | `WorkspaceBranchHandler` |
| `CreateBranchesAsync` (job body) | `WorkspaceBranchHandler` |
| `CreateSingleBranchAsync` (job body) | `WorkspaceBranchHandler` |
| `CheckoutBranchAsync` (job body) | `WorkspaceBranchHandler` |
| `RestorePackagesCoreAsync` (job body) | `WorkspaceGitService` |
| `RestoreSyncedPackagesCoreAsync` (job body) | `WorkspaceGitService` |

### 5.2 Multi-service shared scopes (must remain as CreateAsyncScope)

| Site / method | Services (must share one scope) |
|---------------|--------------------------------|
| `LoadMismatchedDependencyLinesAsync` | `WorkspaceProjectRepository` + `WorkspaceFileVersionService` |
| `CommitFileVersionUpdateAsync` (job body) | `WorkspaceFileVersionService` + `WorkspaceGitService` |
| `BuildPushPlanAsync` | `WorkspacePushHandler` + `WorkspaceDependencyService` |
| `ShowCustomDependenciesModalAsync` | `WorkspaceProjectRepository` + `WorkspaceRepositoryCustomDependencyRepository` + `WorkspaceRepository` |
| `SaveCustomDependenciesAsync` | `WorkspaceRepositoryCustomDependencyRepository` + `WorkspaceGitService` |
| `ExecuteCreatePullRequestsAsync` (PR refresh after create) | `WorkspacePullRequestService` (2 calls on same instance) |
| `SyncAllToDefaultAsync` inner PR scope | `WorkspacePullRequestService` (separate from git scope) |

### 5.3 Per-task parallel scopes (each Task lambda owns its scope)

| Site | Parallelism |
|------|------------|
| `CheckBranchesAndConfirmSyncToDefaultLevel` | Up to 8 concurrent; each task gets a `WorkspaceGitService` scope |
| `FetchCommonBranchesAcrossWorkspaceAsync` | Up to 8 concurrent; `WorkspaceGitService` |
| `SyncToDefaultLevelAsync` | Up to `MaxParallelOperations`; `WorkspaceGitService` |
| `ExecuteSyncAllToDefaultAsync` | Up to `MaxParallelOperations`; inner PR close scope + git scope |
| `SyncAllToDefaultAsync` | Up to 8 concurrent fetch; `WorkspaceGitService` |

---

## 6. All background-job entry points

Calls to `JobService.StartJob(PageJobKey, label, async (job, ct) => ...)`:

| # | Method | Label |
|---|--------|-------|
| 1 | `CheckBranchesAndConfirmSyncToDefaultLevel` | "Fetching latest branch state..." |
| 2 | `ExecuteCreatePullRequestsAsync` | "Creating N pull requests..." |
| 3 | `OnUpdateSingleRepositoryDependenciesProceedAsync` | "Updating repository..." |
| 4 | `RunUpdateCoreAsync` | "Updating dependencies..." |
| 5 | `RunUpdateAndPushCoreAsync` | "Updating dependencies..." |
| 6 | `RunLevelOnlyUpdateAndPushCoreAsync` | "Updating Level N..." |
| 7 | `OnUpdateFilesClickAsync` | "Updating file versions..." |
| 8 | `UpdateSingleRepositoryFileVersionsAsync` | "Updating file versions..." |
| 9 | `CommitFileVersionUpdateAsync` | "Updating and committing..." / "Updating..." |
| 10 | `OnPushWithDependenciesProceedAsync` | "Preparing push..." |
| 11 | `OnPushWithDependenciesProceedAsync` (SynchronizedPushNotPossible fallback - nested) | "Preparing push..." |
| 12 | `PushSingleRepositoryWithUpstreamAsync` | "Setting upstream..." |
| 13 | `RunSyncJobAsync` | variable label |
| 14 | `CommitSyncAsync` | "Synchronizing commits..." |
| 15 | `CommitSyncLevelAsync` | "Synchronizing commits..." |
| 16 | `UpdateSingleRepositoryAsync` | "Updating repository..." |
| 17 | `SyncToDefaultSingleRepoAfterCheckAsync` | "Synchronizing to default branch..." / "Synchronizing to {branch}..." |
| 18 | `SyncToDefaultLevelAsync` | "Synchronizing to default branch..." |
| 19 | `SyncAllToDefaultAsync` | "Fetching latest branch state..." |
| 20 | `ExecuteSyncAllToDefaultAsync` | "Synchronizing to default branch..." |
| 21 | `FetchCommonBranchesAcrossWorkspaceAsync` | "Fetching branches..." |
| 22 | `CheckoutCommonBranchAcrossWorkspaceAsync` | "Checking out..." |
| 23 | `CreateBranchesAsync` | "Creating branches..." |
| 24 | `CreateSingleBranchAsync` | "Creating branch..." |
| 25 | `CheckoutBranchAsync` | "Checking out tag..." / "Checking out branch..." |
| 26 | `HandleNewFeatureCreateAsync` | "Creating branches..." |
| 27 | `RestorePackagesAsync` | "Restoring packages..." |
| 28 | `OnUndoPushProceedAsync` | "Resetting outgoing commits..." |
| 29 | `RunLevelOnlyUpdateAndPushCoreAsync` (SynchronizedPushNotPossible fallback - nested) | "Preparing push..." |
| 30 | `RunUpdateAndPushCoreAsync` (SynchronizedPushNotPossible fallback - nested) | "Preparing push..." |

---

## 7. All methods longer than 50 lines

| Method | Approx. line range | Approx. length |
|--------|--------------------|----------------|
| `LoadMismatchedDependencyLinesAsync` | 418-516 | 98 lines |
| `CheckBranchesAndConfirmSyncToDefaultLevel` | 928-1044 | 116 lines |
| `ExecuteCreatePullRequestsAsync` | 771-863 | 93 lines |
| `RunUpdateCoreAsync` (job body closure) | 1560-1604 | ~45 + lambda |
| `RunLevelOnlyUpdateAndPushCoreAsync` (job body) | 1844-1939 | 95 lines |
| `RunUpdateAndPushCoreAsync` (job body) | 1941-2034 | 94 lines |
| `RunSyncJobAsync` (job body) | 2200-2269 | 70 lines |
| `HandleNewFeatureCreateAsync` (job body) | 2526-2619 | 94 lines |
| `SyncToDefaultLevelAsync` (job body) | 3002-3099 | 97 lines |
| `SyncAllToDefaultAsync` (job body) | 3101-3201 | 100 lines |
| `ExecuteSyncAllToDefaultAsync` | 3203-3333 | 131 lines |
| `ShowCustomDependenciesModalAsync` | 3690-3749 | 59 lines |

---

## 8. All methods that mutate more than one state group

| Method | State groups mutated |
|--------|---------------------|
| `OnInitializedAsync` | lifecycle (_wasJobRunning) + event subscriptions |
| `ReloadWorkspaceDataAsync` | workspace data + PR state + dependency data + search filter |
| `ReloadWorkspaceDataFromFreshScopeAsync` | same as above, via fresh scope |
| `ApplySyncStateFromWorkspace` | repoSyncStatus + isOutOfSync |
| `RefreshFromSync` | workspace data + sync state + UI |
| `LoadMismatchedDependencyLinesAsync` | all 5 dependency data dicts (_mismatched + _all + _file*) |
| `RunSyncJobAsync` | workspace data + repositoryErrors + repoSyncStatus + isOutOfSync |
| `CheckBranchesAndConfirmSyncToDefaultLevel` | _syncToDefaultCheckResults + workspace data + modal state |
| `FetchRepositoriesAsync` | _repositoriesModal fetching flags + Repositories list |
| `SaveRepositoriesAsync` | _repositoriesModal state + workspace state |
| `SaveCustomDependenciesAsync` | _customDependenciesModal state + workspace state |
| `ExecuteSyncAllToDefaultAsync` | repositoryErrors + workspace data + errorMessage |
| `SyncToDefaultLevelAsync` | repositoryErrors + workspace data + errorMessage |
| `ExecuteCreatePullRequestsAsync` | prByRepositoryId + UI (toast) |

---

## 9. All fire-and-forget asynchronous calls

All use the `_ = expression` pattern to discard the Task:

| Location | Expression discarded | Reason |
|----------|---------------------|--------|
| `LoadWorkspaceAsync` | `RefreshPullRequestsInBackgroundAndReloadAsync()` | Background PR sync; must not block initial render |
| `OnAfterRenderAsync` hub handler | `InvokeAsync(RefreshFromSync)` (after debounce delay) | Debounced callback on WorkspaceSynced event |
| `OnAfterRenderAsync` hub handler | `InvokeAsync(StateHasChanged)` (RepositoryError) | Hub callback must not block |
| `OnJobServiceChanged` | `InvokeAsync(...)` | Event handler; cannot be async |
| `OnQueueStateChanged` | `InvokeAsync(StateHasChanged)` | Event handler; cannot be async |
| `ShowOperationError` | `InvokeAsync(StateHasChanged)` | May be called from background job thread |
| `ShowConfirmSyncCommitsLevel` | `CommitSyncLevelAsync(filtered)` | Void entry point; starts job |
| `ShowConfirmSyncLevel` | `SyncLevelAsync(filtered)` | Void entry point; starts job |
| `OnPullBadgeClickAsync` | `CommitSyncAsync(repositoryId)` | Void entry point; starts job |
| `OnDependencyBadgeClick` | `ShowConfirmUpdateDependenciesAsync(...)` | Void entry point |
| `OnFileDependencyBadgeClick` | `ShowFileVersionsCommitFlowAsync(...)` | Void entry point |
| `FetchRepositoriesAsync` progress callback | `InvokeAsync(StateHasChanged)` | Progress callback is synchronous |
| `RunLevelOnlyUpdateAndPushCoreAsync` phase 1 tail | `InvokeAsync(() => { ApplySyncState; StateHasChanged(); })` | Intermediate UI update mid-job |
| `RunUpdateAndPushCoreAsync` phase 1 tail | `InvokeAsync(() => { ApplySyncState; StateHasChanged(); })` | Intermediate UI update mid-job |
| `HandleNewFeatureCreateAsync` phase 1 tail | `InvokeAsync(() => { ApplySyncState; StateHasChanged(); })` | Intermediate UI update mid-job |
| `Dispose` | `_hubConnection?.StopAsync()` | Cannot await in Dispose |
| `_hubConnection.DisposeAsync()` | `ValueTask` (implicit discard) | Cannot await in Dispose |

---

## 10. Cancellation token sources, locks, semaphores, subscriptions, disposal

### CancellationTokenSources

| Field / Variable | Scope | Disposed in |
|-----------------|-------|-------------|
| `_refreshDebounceCts` | Instance field | `Dispose()` + debounce `finally` block |
| `_fetchRepositoriesCts` | Instance field | `Dispose()` + FetchRepositoriesAsync |
| Per-task `CancellationToken` `ct` | Job parameter from `JobService.StartJob` | Owned by job service |

### Locks

| Field | Type | Guards |
|-------|------|--------|
| `_refreshDebounceLock` | `readonly object` | _refreshDebounceCts create/cancel/dispose |

### Semaphores (instance)

| Field | Type | Initial count | Guards | Disposed in |
|-------|------|---------------|--------|-------------|
| `_loadMismatchedDepsLock` | `SemaphoreSlim(1,1)` | 1 | Concurrent LoadMismatchedDependencyLinesAsync calls | `Dispose()` |

### Semaphores (local / method-scoped)

| Created in | Count | Guards |
|-----------|-------|--------|
| `CheckBranchesAndConfirmSyncToDefaultLevel` | 8 | Parallel repo fetch tasks |
| `FetchCommonBranchesAcrossWorkspaceAsync` | 8 | Parallel branch fetch tasks |
| `SyncToDefaultLevelAsync` | `MaxParallelOperations` | Parallel SyncToDefault tasks |
| `ExecuteSyncAllToDefaultAsync` | `MaxParallelOperations` | Parallel SyncToDefault tasks |
| `SyncAllToDefaultAsync` | 8 | Parallel PR + branch fetch tasks |

### Event subscriptions

| Subscription | Where added | Where removed |
|--------------|------------|---------------|
| `AgentQueueStateService.OnQueueStateChanged(OnQueueStateChanged)` | `OnInitializedAsync` | `Dispose()` |
| `JobService.Changed += OnJobServiceChanged` | `OnInitializedAsync` | `Dispose()` |
| `_hubConnection.On<int>("WorkspaceSynced", ...)` | `OnAfterRenderAsync` (firstRender) | Implicit (hub disposed) |
| `_hubConnection.On<int,int,string>("RepositoryError", ...)` | `OnAfterRenderAsync` (firstRender) | Implicit (hub disposed) |

### Disposal operations in Dispose()

```csharp
public void Dispose()
{
    _disposed = true;
    AgentQueueStateService.RemoveQueueStateChanged(OnQueueStateChanged);
    JobService.Changed -= OnJobServiceChanged;
    lock (_refreshDebounceLock) { _refreshDebounceCts?.Cancel(); _refreshDebounceCts?.Dispose(); _refreshDebounceCts = null; }
    _ = _hubConnection?.StopAsync();
    _hubConnection?.DisposeAsync();
    _fetchRepositoriesCts?.Cancel();
    _fetchRepositoriesCts?.Dispose();
    _loadMismatchedDepsLock.Dispose();
}
```

---

## 11. Existing tests that provide coverage

`GrayMoon.Common.Tests` (10 tests) covers:
- `FilterSearchExpression` (boolean query parser - 9 tests)
- `CommandLineService` (process execution wrapper - 1 test)

**There are no xUnit tests covering `WorkspaceRepositories` or any Blazor page component.** The page is tested only by running the App manually.

---

## 12. Build and test baseline

**Captured at:** 2026-06-30

### Build

```
dotnet build GrayMoon.slnx
Build succeeded. 0 Error(s), 0 Warning(s)
```

### Tests

```
dotnet test src/GrayMoon.Common.Tests/GrayMoon.Common.Tests.csproj
Passed! - Failed: 0, Passed: 10, Skipped: 0, Total: 10, Duration: ~400 ms
```

**This is the exact baseline that all subsequent refactoring stages must preserve.**

---

## 13. Stage 4 - IScopedServiceExecutor migration notes

### Migrated sites (single-service CreateAsyncScope replaced with ScopedExecutor.ExecuteAsync)

| File | Method | Service |
|------|--------|---------|
| Loading.cs | `RefreshBranchesForRepositoryAsync` | `WorkspaceGitService` |
| Loading.cs | `ReloadWorkspaceDataFromFreshScopeAsync` | `WorkspaceRepository` |
| CommitSync.cs | `CommitSyncAsync` | `WorkspaceCommitSyncHandler` |
| CommitSync.cs | `CommitSyncLevelAsync` | `WorkspaceCommitSyncHandler` |
| Push.cs | `PushSingleRepositoryWithUpstreamAsync` | `WorkspacePushHandler` |
| Push.cs | `ExecutePushCoreAsync` | `WorkspacePushHandler` |
| Push.cs | `RestorePackagesCoreAsync` | `WorkspaceGitService` |
| Push.cs | `RestoreSyncedPackagesCoreAsync` | `WorkspaceGitService` |
| Branches.cs | `FetchCommonBranchesAcrossWorkspaceAsync` (inner loop) | `WorkspaceGitService` |
| Branches.cs | `CheckoutCommonBranchAcrossWorkspaceAsync` | `WorkspaceBranchHandler` |
| Branches.cs | `CreateBranchesAsync` | `WorkspaceBranchHandler` |
| Branches.cs | `CreateSingleBranchAsync` | `WorkspaceBranchHandler` |
| Branches.cs | `CheckoutBranchAsync` | `WorkspaceBranchHandler` |
| Update.cs | `OnUpdateClickAsync` | `WorkspaceGitService` |
| Update.cs | `RunUpdateCoreAsync` | `WorkspaceUpdateHandler` |
| Update.cs | `OnUpdateAndPushClickAsync` | `WorkspaceGitService` |
| Update.cs | `OnLevelOnlyUpdateAndPushClickAsync` | `WorkspaceGitService` |
| Update.cs | `RunLevelOnlyUpdateAndPushCoreAsync` (Phase 1) | `WorkspaceUpdateHandler` |
| Update.cs | `RunUpdateAndPushCoreAsync` (Phase 1) | `WorkspaceUpdateHandler` |
| FileVersions.cs | `OnUpdateFilesClickAsync` | `WorkspaceFileVersionService` |
| FileVersions.cs | `UpdateSingleRepositoryFileVersionsAsync` | `WorkspaceFileVersionService` |
| FileVersions.cs | `CommitFileVersionUpdateAsync` | `WorkspaceFileVersionService` (x2), `WorkspaceGitService` (separate call) |
| Dependencies.cs | `OnUpdateSingleRepositoryDependenciesProceedAsync` | `WorkspaceUpdateHandler` |
| Dependencies.cs | `UpdateSingleRepositoryAsync` | `WorkspaceGitService` |
| SyncToDefault.cs | `FetchLatestBranchStateSyncToDefault` (fetch loop) | `WorkspaceGitService` |
| SyncToDefault.cs | `SyncToDefaultSingleRepoAfterCheckAsync` | `WorkspaceGitService` |
| SyncToDefault.cs | `SyncToDefaultLevelAsync` (parallel tasks) | `WorkspaceGitService` |
| SyncToDefault.cs | `ExecuteSyncAllToDefaultAsync` (PR refresh) | `WorkspacePullRequestService` |
| SyncToDefault.cs | `ExecuteSyncAllToDefaultAsync` (fetch loop) | `WorkspaceGitService` |
| SyncToDefault.cs | `ExecuteSyncAllToDefaultAsync` (close PR) | `WorkspacePullRequestService` |
| SyncToDefault.cs | `ExecuteSyncAllToDefaultAsync` (sync task) | `WorkspaceGitService` |
| NewFeature.cs | `HandleNewFeatureCreateAsync` (Phase 1) | `NewFeatureOrchestrator` |
| Sync.cs | `RunSyncJobAsync` | `WorkspaceSyncHandler` |
| PullRequests.cs | `CreatePullRequestsAsync` (post-create refresh) | `WorkspacePullRequestService` |

### Remaining direct CreateAsyncScope calls (multi-service - intentionally excluded)

| File | Method | Reason |
|------|--------|--------|
| Dependencies.cs | `ShowConfirmUpdateDependenciesAsync` | 2 services: `WorkspaceGitService` + `WorkspaceRepository` |
| Dependencies.cs | `ShowCustomDependenciesModalAsync` | 3 services: `WorkspaceProjectRepository`, `WorkspaceRepositoryCustomDependencyRepository`, `WorkspaceRepository` |
| Dependencies.cs | `SaveCustomDependenciesAsync` | 2 services: `WorkspaceRepositoryCustomDependencyRepository` + `WorkspaceGitService` |
| Loading.cs | `LoadMismatchedDependencyLinesAsync` | 2 services: `WorkspaceProjectRepository` + `WorkspaceFileVersionService` |
| Push.cs | `BuildPushPlanAsync` | 2 services: `WorkspacePushHandler` + `WorkspaceDependencyService` |
