# Read-only (reference) repositories - Phase 0A inventory

Status: Phase 0A output of the [implementation plan](read-only-repositories-implementation-plan.md). Collected 2026-11-12 from the application repository `C:\Workspace\GrayMoon\GrayMoon` (branch `readonly-repos`, commit `1515d32b` plus the uncommitted 0A test files). Deleted when the feature ships.

No production code was changed in 0A. Paths are relative to the repository root.

## 1. How to read this

- **Part A** lists every line in production code that reads, writes or names tag state. Phase 0B replaces the `eligibility-*` rows with the central policy; the other classes stay as they are.
- **Part B** is the App side: every REST route, which entry points hold the Workspace lock (F18), and where a service checks the tag before it calls the Worker.
- **Part C** is the Worker side: every command, what it writes, and which commands look at the tag themselves.
- **Part D** answers the open facts of plan section 5 (F8, F13, F18, F22, F28, role visibility).
- **Part E** maps the characterization tests to the behavior they pin.

Parts B and C were collected by code reading. A row that was not re-read by hand says "not verified"; nothing in those rows is a guess presented as a fact. Part A was collected by a search and classified by rule (file and line shape), then reviewed; the class of a single row can be wrong, the list of rows is complete for the pattern.

## 2. Part A - tag hits in production code

Search pattern (case-sensitive, `.cs` and `.razor`, `src/GrayMoon.App`, `.Application`, `.Abstractions`, `.Common`, `.Worker`):

```
IsOnTag|[Cc]heckedOutTag|[Pp]innedTag|OnTag|[Tt]agPinned|[Ss]kipReposOnTags|pinned to a tag|[Oo]nTags|IsPinnedToTag|tagFilteredRepoIds
```

Result: **379 lines in 78 files** of production code. The plan said "329 hits in 92 files, tests included" for the narrower name list `IsOnTag`, `CheckedOutTag`, `IsPinnedToTag`, `PinnedTag`, `tagFilteredRepoIds`, `SkipReposOnTags`; that list gives 333 lines today with the existing tests included. This inventory leaves tests out and adds the local names and message text the narrow list misses (`tagPinnedIds`, `checkedOutTagByRepo`, `IsRepoOnTag`, `AreAllOnTag`, "pinned to a tag").

| Class | Rows | Meaning |
|---|---|---|
| eligibility-filter | 119 | A service or query leaves a tag-pinned repository out of a set, or a counter does not count it. |
| eligibility-ui | 94 | A page or component filters, disables or returns early. Only the user interface is protected. |
| eligibility-refuse | 17 | A request for one repository is refused or made a no-op (includes the Feature branch guard, which reads the Feature pin). |
| pin-bookkeeping | 37 | `WorkspaceFeatureRepository.PinnedTag` and the Workspace definition pin: how a Feature knows a repository is detached. |
| state-write | 27 | Writes `CheckedOutTag` to the link or the context state (state writer, response parsing, migrations). |
| presentation | 32 | Shows the tag; decides nothing. |
| worker-git-state | 29 | The Worker reads the live checkout. |
| declaration | 11 | Property, parameter or DTO field. (Class added in 0A; the plan did not name one.) |
| comment | 13 | Comment or documentation text. |

Columns: **Subject** is the row the tag is read from (the shared link, the per-context state, or the Feature row). **Operation** is the policy operation the row belongs to. **Service twin** applies to `eligibility-ui` rows: is the same rule also enforced in a service ("n" is finding F5: the page is the only guard).

| File:line | Method | Class | Subject | Operation | Service twin |
|---|---|---|---|---|---|
| `src/GrayMoon.Abstractions/Notifications/RepositoryStateSnapshot.cs:26` | - | declaration | - | - | - |
| `src/GrayMoon.App/Components/Features/WorkspaceContextBar.razor:88` | UnifiedWorkspaceBranch | eligibility-ui | page link list (context overlay) | Checkout / CreateBranch / UpdateBranchFromDefault | n |
| `src/GrayMoon.App/Components/Modals/BranchModal.razor:190` | - | eligibility-ui | page link list (context overlay) | Checkout / CreateBranch / UpdateBranchFromDefault | n |
| `src/GrayMoon.App/Components/Modals/BranchModal.razor:191` | - | eligibility-ui | page link list (context overlay) | Checkout / CreateBranch / UpdateBranchFromDefault | n |
| `src/GrayMoon.App/Components/Modals/BranchModal.razor:239` | - | eligibility-ui | page link list (context overlay) | Checkout / CreateBranch / UpdateBranchFromDefault | n |
| `src/GrayMoon.App/Components/Modals/BranchModal.razor:241` | - | eligibility-ui | page link list (context overlay) | Checkout / CreateBranch / UpdateBranchFromDefault | n |
| `src/GrayMoon.App/Components/Modals/BranchModal.razor:246` | - | eligibility-ui | page link list (context overlay) | Checkout / CreateBranch / UpdateBranchFromDefault | n |
| `src/GrayMoon.App/Components/Modals/BranchModal.razor:385` | CheckoutSelectedBranchAsync | eligibility-ui | page link list (context overlay) | Checkout / CreateBranch / UpdateBranchFromDefault | n |
| `src/GrayMoon.App/Components/Modals/BranchModal.razor:422` | OnParametersSet | eligibility-ui | page link list (context overlay) | Checkout / CreateBranch / UpdateBranchFromDefault | n |
| `src/GrayMoon.App/Components/Modals/BranchModal.razor:486` | CreateAsync | eligibility-ui | page link list (context overlay) | Checkout / CreateBranch / UpdateBranchFromDefault | n |
| `src/GrayMoon.App/Components/Modals/BranchModal.razor:514` | ProceedWithCreateAsync | eligibility-ui | page link list (context overlay) | Checkout / CreateBranch / UpdateBranchFromDefault | n |
| `src/GrayMoon.App/Components/Modals/PrepareWorkspaceModal.razor:98` | - | eligibility-ui | page link list (context overlay) | PrepareWorkspace | n |
| `src/GrayMoon.App/Components/Modals/PrepareWorkspaceModal.razor:149` | - | eligibility-ui | page link list (context overlay) | PrepareWorkspace | n |
| `src/GrayMoon.App/Components/Modals/PrepareWorkspaceModal.razor:208` | OnParametersSet | eligibility-ui | page link list (context overlay) | PrepareWorkspace | n |
| `src/GrayMoon.App/Components/Modals/PrepareWorkspaceModal.razor:260` | CreateAsync | eligibility-ui | page link list (context overlay) | PrepareWorkspace | n |
| `src/GrayMoon.App/Components/Modals/PrepareWorkspaceModal.razor:279` | ProceedWithCreateAsync | eligibility-ui | page link list (context overlay) | PrepareWorkspace | n |
| `src/GrayMoon.App/Components/Modals/PrepareWorkspaceModels.cs:7` | PrepareWorkspaceRequest | declaration | - | - | - |
| `src/GrayMoon.App/Components/Modals/SwitchBranchModal.razor:625` | - | eligibility-ui | page link list (context overlay) | Checkout / CreateBranch / UpdateBranchFromDefault | n |
| `src/GrayMoon.App/Components/Modals/SwitchBranchModal.razor:1070` | GetFeatureCheckoutRefusal | eligibility-ui | page link list (context overlay) | Checkout / CreateBranch / UpdateBranchFromDefault | n |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Branches.cs:45` | ShowSwitchBranchModalOnTagsTab | eligibility-ui | page link list (context overlay) | Checkout / CreateBranch / UpdateBranchFromDefault | n |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Branches.cs:68` | GetUnifiedWorkspaceCurrentBranch | eligibility-ui | page link list (context overlay) | Checkout / CreateBranch / UpdateBranchFromDefault | n |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Branches.cs:221` | CheckoutCommonBranchAcrossWorkspaceAsync | eligibility-ui | page link list (context overlay) | Checkout / CreateBranch / UpdateBranchFromDefault | n |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Branches.cs:223` | CheckoutCommonBranchAcrossWorkspaceAsync | eligibility-ui | page link list (context overlay) | Checkout / CreateBranch / UpdateBranchFromDefault | n |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Branches.cs:229` | CheckoutCommonBranchAcrossWorkspaceAsync | eligibility-ui | page link list (context overlay) | Checkout / CreateBranch / UpdateBranchFromDefault | n |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Branches.cs:280` | CreateBranchesAsync | eligibility-ui | page link list (context overlay) | Checkout / CreateBranch / UpdateBranchFromDefault | n |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Branches.cs:282` | CreateBranchesAsync | eligibility-ui | page link list (context overlay) | Checkout / CreateBranch / UpdateBranchFromDefault | n |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Branches.cs:289` | CreateBranchesAsync | eligibility-ui | page link list (context overlay) | Checkout / CreateBranch / UpdateBranchFromDefault | n |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Branches.cs:290` | CreateBranchesAsync | eligibility-ui | page link list (context overlay) | Checkout / CreateBranch / UpdateBranchFromDefault | n |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Branches.cs:292` | CreateBranchesAsync | eligibility-ui | page link list (context overlay) | Checkout / CreateBranch / UpdateBranchFromDefault | n |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Branches.cs:302` | CreateBranchesAsync | eligibility-ui | page link list (context overlay) | Checkout / CreateBranch / UpdateBranchFromDefault | n |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Branches.cs:446` | ShowUpdateBranchModal | eligibility-ui | page link list (context overlay) | Checkout / CreateBranch / UpdateBranchFromDefault | n |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.BulkMerge.cs:136` | OpenMergePullRequestsDialogAsync | eligibility-ui | page link list (context overlay) | PullRequest | not verified |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.CommitSync.cs:18` | OnPullClickAsync | eligibility-ui | page link list (context overlay) | Pull | n |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.CommitSync.cs:38` | OnPullBadgeClickAsync | eligibility-ui | page link list (context overlay) | Pull | n |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.CommitSync.cs:56` | GroupHasSyncCommits | eligibility-ui | page link list (context overlay) | Pull | n |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.CommitSync.cs:82` | ShowConfirmSyncCommitsLevel | eligibility-ui | page link list (context overlay) | Pull | n |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.CommitSync.cs:98` | CommitSyncAsync | eligibility-ui | page link list (context overlay) | Pull | n |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.CommitSync.cs:122` | CommitSyncLevelAsync | eligibility-ui | page link list (context overlay) | Pull | n |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Dependencies.cs:70` | ShowConfirmUpdateDependenciesAsync | eligibility-ui | page link list (context overlay) | UpdateDependencies (single) | y (silent no-op) |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Dependencies.cs:173` | UpdateSingleRepositoryAsync | eligibility-ui | page link list (context overlay) | UpdateDependencies (single) | y (silent no-op) |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Dependencies.cs:209` | ShowCustomDependenciesModalAsync | eligibility-ui | page link list (context overlay) | UpdateDependencies (single) | y (silent no-op) |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.FileVersions.cs:62` | UpdateSingleRepositoryFileVersionsAsync | eligibility-ui | page link list (context overlay) | UpdateFileVersions | y |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.FileVersions.cs:126` | ShowFileVersionsCommitFlowAsync | eligibility-ui | page link list (context overlay) | UpdateFileVersions | y |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.PrepareWorkspace.cs:46` | HandlePrepareWorkspaceAsync | eligibility-ui | page link list (context overlay) | PrepareWorkspace | n |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.PrepareWorkspace.cs:47` | HandlePrepareWorkspaceAsync | eligibility-ui | page link list (context overlay) | PrepareWorkspace | n |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.PrepareWorkspace.cs:65` | HandlePrepareWorkspaceAsync | eligibility-ui | page link list (context overlay) | PrepareWorkspace | n |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.PullRequests.cs:62` | OpenPullRequestDialogCoreAsync | eligibility-ui | page link list (context overlay) | PullRequest | not verified |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.PullRequests.cs:405` | GroupHasCreatablePr | eligibility-ui | page link list (context overlay) | PullRequest | not verified |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Push.cs:33` | OnPushClickAsync | eligibility-ui | page link list (context overlay) | Push | y |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Push.cs:89` | OnPushBadgeClickAsync | eligibility-ui | page link list (context overlay) | Push | y |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Push.cs:119` | PushBadgeClickCoreAsync | eligibility-ui | page link list (context overlay) | Push | y |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.ReturnToDefault.cs:64` | - | eligibility-ui | page link list (context overlay) | ReturnToDefault | y |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.ReturnToDefault.cs:127` | ReturnToDefaultFromModalAsync | eligibility-ui | page link list (context overlay) | ReturnToDefault | y |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.ReturnToDefault.cs:160` | ReturnToDefaultFromModalAsync | eligibility-ui | page link list (context overlay) | ReturnToDefault | y |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.ReturnToDefault.cs:295` | ReturnToDefaultLevelAsync | eligibility-ui | page link list (context overlay) | ReturnToDefault | y |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.ReturnToDefault.cs:354` | ReturnAllToDefaultAsync | eligibility-ui | page link list (context overlay) | ReturnToDefault | y |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.ReturnToDefault.cs:390` | - | eligibility-ui | page link list (context overlay) | ReturnToDefault | y |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.State.cs:135` | IsRepoOnTag | eligibility-ui | page link list (context overlay) | shared page helper | - |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.State.cs:136` | IsRepoOnTag | eligibility-ui | page link list (context overlay) | shared page helper | - |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.State.cs:192` | FreshLinkState | eligibility-ui | page link list (context overlay) | shared page helper | - |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.State.cs:386` | IsLevelActionsDisabled | eligibility-ui | page link list (context overlay) | shared page helper | - |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.State.cs:387` | AreAllOnTag | eligibility-ui | page link list (context overlay) | shared page helper | - |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.State.cs:394` | AreAllOnTag | eligibility-ui | page link list (context overlay) | shared page helper | - |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Sync.cs:31` | ShowConfirmSyncLevel | eligibility-ui | page link list (context overlay) | Sync | not verified |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.UndoPush.cs:18` | OnUndoPushClickAsync | eligibility-ui | page link list (context overlay) | UndoPush | y |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Update.cs:24` | OnUpdateClickAsync | eligibility-ui | page link list (context overlay) | UpdateDependencies / UpdateAndPush | y |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Update.cs:36` | OnUpdateClickAsync | eligibility-ui | page link list (context overlay) | UpdateDependencies / UpdateAndPush | y |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Update.cs:135` | OnUpdateAndPushClickAsync | eligibility-ui | page link list (context overlay) | UpdateDependencies / UpdateAndPush | y |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Update.cs:147` | OnUpdateAndPushClickAsync | eligibility-ui | page link list (context overlay) | UpdateDependencies / UpdateAndPush | y |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Update.cs:154` | OnUpdateAndPushClickAsync | eligibility-ui | page link list (context overlay) | UpdateDependencies / UpdateAndPush | y |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Update.cs:211` | OnLevelOnlyUpdateAndPushClickAsync | eligibility-ui | page link list (context overlay) | UpdateDependencies / UpdateAndPush | y |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Update.cs:223` | OnLevelOnlyUpdateAndPushClickAsync | eligibility-ui | page link list (context overlay) | UpdateDependencies / UpdateAndPush | y |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.Update.cs:231` | OnLevelOnlyUpdateAndPushClickAsync | eligibility-ui | page link list (context overlay) | UpdateDependencies / UpdateAndPush | y |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.razor:198` | - | eligibility-ui | page link list (context overlay) | Checkout (Feature pin) | y (FeatureBranchGuard) |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.razor:225` | - | eligibility-ui | page link list (context overlay) | Checkout (Feature pin) | y (FeatureBranchGuard) |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.razor:311` | - | eligibility-ui | page link list (context overlay) | Checkout (Feature pin) | y (FeatureBranchGuard) |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.razor:375` | - | eligibility-ui | page link list (context overlay) | Checkout (Feature pin) | y (FeatureBranchGuard) |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.razor.cs:109` | ApplySelectedContext | comment | - | - | - |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.razor.cs:115` | GetSwitchBranchModalFeatureBranchName | eligibility-ui | page link list (context overlay) | Checkout (Feature pin) | y (FeatureBranchGuard) |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.razor.cs:119` | GetSwitchBranchModalPinnedTag | eligibility-ui | page link list (context overlay) | Checkout (Feature pin) | y (FeatureBranchGuard) |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.razor.cs:123` | GetSwitchBranchModalPinnedTag | eligibility-ui | page link list (context overlay) | Checkout (Feature pin) | y (FeatureBranchGuard) |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.razor.cs:132` | GetOffFeatureBranchName | eligibility-ui | page link list (context overlay) | Checkout (Feature pin) | y (FeatureBranchGuard) |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositoriesRow.razor:32` | - | eligibility-ui | page row | dependency badge and dialogs, row actions | y for update (silent no-op) |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositoriesRow.razor:125` | - | presentation | page row | - | - |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositoriesRow.razor:130` | - | presentation | page row | - | - |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositoriesRow.razor:131` | - | presentation | page row | - | - |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositoriesRow.razor:163` | - | presentation | page row | - | - |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositoriesRow.razor:178` | - | presentation | page row | - | - |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositoriesRow.razor:321` | - | presentation | page row | - | - |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositoriesRow.razor:323` | - | eligibility-ui | page row | dependency badge and dialogs, row actions | y for update (silent no-op) |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositoriesRow.razor:324` | - | eligibility-ui | page row | dependency badge and dialogs, row actions | y for update (silent no-op) |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositoriesRow.razor:327` | - | presentation | page row | - | - |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositoriesRow.razor:333` | - | presentation | page row | - | - |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositoriesRow.razor:425` | - | presentation | page row | - | - |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositoriesRow.razor:443` | - | eligibility-ui | page row | dependency badge and dialogs, row actions | y for update (silent no-op) |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositoriesRow.razor:460` | - | eligibility-ui | page row | dependency badge and dialogs, row actions | y for update (silent no-op) |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositoriesRow.razor:461` | - | presentation | page row | - | - |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositoriesRow.razor:469` | - | eligibility-ui | page row | dependency badge and dialogs, row actions | y for update (silent no-op) |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositoriesRow.razor:645` | DetermineVersionCell | presentation | page row | - | - |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositoriesRow.razor:658` | HandleDependencyBadgeClick | eligibility-ui | page row | dependency badge and dialogs, row actions | y for update (silent no-op) |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositoriesRow.razor:668` | HandleCustomDependenciesClick | eligibility-ui | page row | dependency badge and dialogs, row actions | y for update (silent no-op) |
| `src/GrayMoon.App/Components/Pages/WorkspaceRepositoriesRow.razor:675` | HandleShowDependenciesClick | eligibility-ui | page row | dependency badge and dialogs, row actions | y for update (silent no-op) |
| `src/GrayMoon.App/Components/Shared/CommitsBadge.razor:3` | - | presentation | page row | - | - |
| `src/GrayMoon.App/Components/Shared/CommitsBadge.razor:44` | - | presentation | page row | - | - |
| `src/GrayMoon.App/Components/Shared/CommitsBadge.razor:48` | - | presentation | page row | - | - |
| `src/GrayMoon.App/Components/Shared/CommitsBadge.razor:55` | HasIncomingOrOutgoingCommits | presentation | page row | - | - |
| `src/GrayMoon.App/Components/Shared/CommitsBadge.razor:57` | HasIncomingOrOutgoingCommits | presentation | page row | - | - |
| `src/GrayMoon.App/Components/Shared/CommitsBadge.razor:64` | HasIncomingOrOutgoingCommits | presentation | page row | - | - |
| `src/GrayMoon.App/Components/Shared/CommitsBadge.razor:70` | HasIncomingOrOutgoingCommits | presentation | page row | - | - |
| `src/GrayMoon.App/Components/Shared/DivergenceBadge.razor:6` | - | presentation | page row | - | - |
| `src/GrayMoon.App/Components/Shared/DivergenceBadge.razor:93` | - | presentation | page row | - | - |
| `src/GrayMoon.App/Components/Shared/PRBadge.razor:13` | - | presentation | page row | - | - |
| `src/GrayMoon.App/Components/Shared/PRBadge.razor:24` | - | presentation | page row | - | - |
| `src/GrayMoon.App/Components/Shared/PRBadge.razor:42` | - | presentation | page row | - | - |
| `src/GrayMoon.App/Components/Shared/PRBadge.razor:77` | - | presentation | page row | - | - |
| `src/GrayMoon.App/Components/Shared/PRBadge.razor:97` | ShowsCreateBadge | presentation | page row | - | - |
| `src/GrayMoon.App/Components/Shared/PRBadge.razor:104` | ShowsCreateBadge | presentation | page row | - | - |
| `src/GrayMoon.App/Components/Shared/UiRaceGuards.cs:125` | IsOnTag | eligibility-ui | context list DTO | notification panel sets | n (in-memory helper) |
| `src/GrayMoon.App/Components/Shared/UiRaceGuards.cs:133` | NeedingUpdate | eligibility-ui | context list DTO | notification panel sets | n (in-memory helper) |
| `src/GrayMoon.App/Components/Shared/UiRaceGuards.cs:139` | WithIncoming | eligibility-ui | context list DTO | notification panel sets | n (in-memory helper) |
| `src/GrayMoon.App/Components/Shared/UiRaceGuards.cs:145` | UpdateOnDefaultBranch | eligibility-ui | context list DTO | notification panel sets | n (in-memory helper) |
| `src/GrayMoon.App/Components/Shared/UiRaceGuards.cs:151` | PushOnDefaultBranch | eligibility-ui | context list DTO | notification panel sets | n (in-memory helper) |
| `src/GrayMoon.App/Components/Shared/UiRaceGuards.cs:157` | List | eligibility-ui | context list DTO | notification panel sets | n (in-memory helper) |
| `src/GrayMoon.App/Data/AppDbContext.Features.cs:69` | ConfigureFeatureEntities | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Data/AppDbContext.Features.cs:89` | ConfigureFeatureEntities | declaration | - | - | - |
| `src/GrayMoon.App/Migrations.Features.cs:29` | MigrateWorkspaceFeatureContextSchemaAsync | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Migrations.Features.cs:98` | EnsureFeatureCoreTablesAsync | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Migrations.Features.cs:118` | EnsureFeatureCoreTablesAsync | state-write | link / context | - | - |
| `src/GrayMoon.App/Migrations.Features.cs:364` | BackfillSpecialWorkspaceContextsAsync | state-write | link / context | - | - |
| `src/GrayMoon.App/Models/Features/WorkspaceFeatureRepository.cs:43` | - | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Models/Features/WorkspaceRepositoryContextState.cs:27` | - | declaration | - | - | - |
| `src/GrayMoon.App/Models/WorkspaceRepositoryLink.cs:35` | - | declaration | - | - | - |
| `src/GrayMoon.App/Models/WorkspaceRepositoryLink.cs:39` | - | declaration | - | - | - |
| `src/GrayMoon.App/Models/WorkspaceRepositoryLink.cs:54` | - | declaration | - | - | - |
| `src/GrayMoon.App/Models/WorkspaceRepositoryLink.cs:138` | - | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Models/WorkspaceRepositoryLink.cs:159` | - | declaration | - | - | - |
| `src/GrayMoon.App/Models/WorkspaceRepositoryLink.cs:185` | - | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Repositories/WorkspaceProjectRepository.Push.cs:158` | GetSyncDependenciesPayloadAsync | comment | - | - | - |
| `src/GrayMoon.App/Repositories/WorkspaceProjectRepository.Push.cs:159` | GetContextCheckedOutTagByRepoAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Repositories/WorkspaceProjectRepository.Push.cs:173` | GetContextCheckedOutTagByRepoAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Repositories/WorkspaceProjectRepository.Push.cs:180` | GetContextCheckedOutTagByRepoAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Repositories/WorkspaceProjectRepository.Push.cs:188` | GetContextCheckedOutTagByRepoAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Repositories/WorkspaceProjectRepository.Push.cs:190` | GetContextCheckedOutTagByRepoAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Repositories/WorkspaceProjectRepository.Push.cs:215` | GetPushPlanPayloadAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Repositories/WorkspaceProjectRepository.Push.cs:267` | GetPushPlanPayloadAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Repositories/WorkspaceProjectRepository.Push.cs:288` | GetPushPayloadWithoutDependenciesAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Repositories/WorkspaceProjectRepository.Push.cs:292` | GetPushPayloadWithoutDependenciesAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Repositories/WorkspaceRepository.cs:283` | ToggleDefaultAsync | comment | - | - | - |
| `src/GrayMoon.App/Repositories/WorkspaceRepository.cs:310` | GetRepositoryIdsNeedingPushAsync | eligibility-filter | context (state row, else link) | needs-push counter | - |
| `src/GrayMoon.App/Repositories/WorkspaceRepository.cs:319` | GetRepositoryIdsNeedingPushAsync | eligibility-filter | context (state row, else link) | needs-push counter | - |
| `src/GrayMoon.App/Repositories/WorkspaceRepository.cs:326` | GetRepositoryIdsNeedingPushAsync | eligibility-filter | context (state row, else link) | needs-push counter | - |
| `src/GrayMoon.App/Repositories/WorkspaceRepository.cs:332` | GetRepositoryIdsNeedingPushAsync | eligibility-filter | context (state row, else link) | needs-push counter | - |
| `src/GrayMoon.App/Repositories/WorkspaceRepository.cs:338` | GetRepositoryIdsNeedingPushAsync | eligibility-filter | context (state row, else link) | needs-push counter | - |
| `src/GrayMoon.App/Repositories/WorkspaceRepository.cs:347` | GetRepositoryIdsNeedingPushAsync | eligibility-filter | context (state row, else link) | needs-push counter | - |
| `src/GrayMoon.App/Repositories/WorkspaceRepository.cs:409` | GetRepositoryIdsWithOutOfDateFilesAsync | comment | - | - | - |
| `src/GrayMoon.App/Repositories/WorkspaceRepository.cs:441` | GetRepositoryIdsThatNeedPushForNotificationAsync | eligibility-filter | context (state row, else link) | needs-push counter | - |
| `src/GrayMoon.App/Repositories/WorkspaceRepository.cs:450` | GetRepositoryIdsThatNeedPushForNotificationAsync | eligibility-filter | context (state row, else link) | needs-push counter | - |
| `src/GrayMoon.App/Repositories/WorkspaceRepository.cs:457` | GetRepositoryIdsThatNeedPushForNotificationAsync | eligibility-filter | context (state row, else link) | needs-push counter | - |
| `src/GrayMoon.App/Repositories/WorkspaceRepository.cs:464` | GetRepositoryIdsThatNeedPushForNotificationAsync | eligibility-filter | context (state row, else link) | needs-push counter | - |
| `src/GrayMoon.App/Repositories/WorkspaceRepository.cs:471` | GetRepositoryIdsThatNeedPushForNotificationAsync | eligibility-filter | context (state row, else link) | needs-push counter | - |
| `src/GrayMoon.App/Repositories/WorkspaceRepository.cs:481` | GetRepositoryIdsThatNeedPushForNotificationAsync | eligibility-filter | context (state row, else link) | needs-push counter | - |
| `src/GrayMoon.App/Services/Application/WorkspaceBranchOperations.cs:181` | BuildSnapshot | presentation | branch snapshot | - | - |
| `src/GrayMoon.App/Services/Application/WorkspaceBranchOperations.cs:188` | BuildSnapshot | presentation | branch snapshot | - | - |
| `src/GrayMoon.App/Services/Application/WorkspaceBranchOperations.cs:195` | BuildSnapshot | comment | - | - | - |
| `src/GrayMoon.App/Services/Application/WorkspaceBranchOperations.cs:200` | BuildSnapshot | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Application/WorkspaceBranchOperations.cs:358` | CheckoutAsync | presentation | branch snapshot | - | - |
| `src/GrayMoon.App/Services/Application/WorkspaceBranchOperations.cs:361` | CheckoutAsync | state-write | link + context state | - | - |
| `src/GrayMoon.App/Services/Application/WorkspaceBranchOperations.cs:365` | CheckoutAsync | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Application/WorkspaceBranchOperations.cs:368` | CheckoutAsync | presentation | branch snapshot | - | - |
| `src/GrayMoon.App/Services/Application/WorkspaceBranchOperations.cs:412` | CheckoutAsync | pin-bookkeeping | at `1515d32b`: link instance the state writer had already cleared, never true (F22, fixed since) | tag pin clear | - |
| `src/GrayMoon.App/Services/Application/WorkspaceBranchOperations.cs:524` | GetCommonBranchesAsync | eligibility-filter | link | common-branch intersection | - |
| `src/GrayMoon.App/Services/Application/WorkspaceBranchOperations.cs:691` | CreateBranchAsync | pin-bookkeeping | link, read before any state write: clears the pin | tag pin clear | - |
| `src/GrayMoon.App/Services/Application/WorkspaceBranchOperations.cs:877` | UpdateFeaturePinnedTagAsync | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Application/WorkspaceBranchOperations.cs:887` | UpdateFeaturePinnedTagAsync | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Application/WorkspaceBranchOperations.cs:888` | UpdateFeaturePinnedTagAsync | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Application/WorkspaceSyncOperations.cs:146` | UndoPushAsync | comment | - | - | - |
| `src/GrayMoon.App/Services/Application/WorkspaceSyncOperations.cs:162` | UndoPushAsync | state-write | link + context state | - | - |
| `src/GrayMoon.App/Services/Application/WorkspaceUpdateAndPushOperations.cs:84` | IsOnDefaultBranchInScope | eligibility-filter | link DTO | default-branch review | - |
| `src/GrayMoon.App/Services/Features/FeatureBranchGuard.cs:52` | CheckAsync | eligibility-refuse | Feature row | Checkout / CreateBranch (Feature branch guard) | - |
| `src/GrayMoon.App/Services/Features/FeatureBranchGuard.cs:56` | CheckAsync | eligibility-refuse | Feature row | Checkout / CreateBranch (Feature branch guard) | - |
| `src/GrayMoon.App/Services/Features/FeatureBranchGuard.cs:61` | CheckAsync | eligibility-refuse | Feature row | Checkout / CreateBranch (Feature branch guard) | - |
| `src/GrayMoon.App/Services/Features/FeatureBranchGuard.cs:62` | CheckAsync | eligibility-refuse | Feature row | Checkout / CreateBranch (Feature branch guard) | - |
| `src/GrayMoon.App/Services/Features/FeatureBranchPolicy.cs:18` | - | eligibility-refuse | Feature row | Checkout / CreateBranch (Feature branch guard) | - |
| `src/GrayMoon.App/Services/Features/FeatureBranchPolicy.cs:26` | - | eligibility-refuse | Feature row | Checkout / CreateBranch (Feature branch guard) | - |
| `src/GrayMoon.App/Services/Features/FeatureBranchPolicy.cs:30` | - | comment | - | - | - |
| `src/GrayMoon.App/Services/Features/FeatureBranchPolicy.cs:32` | ExpectedBranch | eligibility-refuse | Feature row | Checkout / CreateBranch (Feature branch guard) | - |
| `src/GrayMoon.App/Services/Features/FeatureBranchPolicy.cs:33` | ExpectedBranch | eligibility-refuse | Feature row | Checkout / CreateBranch (Feature branch guard) | - |
| `src/GrayMoon.App/Services/Features/FeatureBranchPolicy.cs:45` | IsOffFeatureBranch | comment | - | - | - |
| `src/GrayMoon.App/Services/Features/FeatureBranchPolicy.cs:50` | GetOffFeatureBranch | eligibility-refuse | Feature row | Checkout / CreateBranch (Feature branch guard) | - |
| `src/GrayMoon.App/Services/Features/FeatureBranchPolicy.cs:56` | GetOffFeatureBranch | eligibility-refuse | Feature row | Checkout / CreateBranch (Feature branch guard) | - |
| `src/GrayMoon.App/Services/Features/FeatureBranchPolicy.cs:68` | Evaluate | eligibility-refuse | Feature row | Checkout / CreateBranch (Feature branch guard) | - |
| `src/GrayMoon.App/Services/Features/FeatureBranchPolicy.cs:80` | Evaluate | eligibility-refuse | Feature row | Checkout / CreateBranch (Feature branch guard) | - |
| `src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs:270` | CreateFeatureCoreAsync | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs:271` | CreateFeatureCoreAsync | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs:282` | CreateFeatureCoreAsync | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs:283` | CreateFeatureCoreAsync | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs:342` | CreateFeatureCoreAsync | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs:343` | CreateFeatureCoreAsync | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs:386` | CreateFeatureCoreAsync | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs:516` | AnalyzeRemoveFeatureAsync | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs:551` | AnalyzeRemoveFeatureAsync | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs:584` | AnalyzeRemoveFeatureAsync | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs:934` | RemoveFeatureCoreAsync | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs:1977` | RepairFeatureCoreAsync | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs:1978` | RepairFeatureCoreAsync | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs:2019` | RepairFeatureCoreAsync | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs:2252` | RollbackRowAsync | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs:2425` | SeedInitialFeatureProjectionsAsync | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs:2442` | SeedInitialFeatureProjectionsAsync | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs:2443` | SeedInitialFeatureProjectionsAsync | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs:2450` | SeedInitialFeatureProjectionsAsync | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs:2482` | SeedInitialFeatureProjectionsAsync | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Branches.cs:258` | ProcessOne | state-write | link + context state | - | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Commit.cs:32` | Task | eligibility-filter | link (every context, F6) | CommitDependencyUpdates | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Commit.cs:34` | Task | eligibility-filter | link (every context, F6) | CommitDependencyUpdates | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Commit.cs:37` | Task | eligibility-filter | link (every context, F6) | CommitDependencyUpdates | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Commit.cs:132` | Task | eligibility-filter | link (every context, F6) | CommitDependencyUpdates | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Commit.cs:134` | Task | eligibility-filter | link (every context, F6) | CommitDependencyUpdates | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Commit.cs:137` | Task | eligibility-filter | link (every context, F6) | CommitDependencyUpdates | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Projects.cs:46` | RefreshWorkspaceProjectsAsync | eligibility-filter | link (every context, F6) | RefreshProjects | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Projects.cs:47` | RefreshWorkspaceProjectsAsync | eligibility-filter | link (every context, F6) | RefreshProjects | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Projects.cs:50` | RefreshWorkspaceProjectsAsync | eligibility-filter | link (every context, F6) | RefreshProjects | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Projects.cs:51` | RefreshWorkspaceProjectsAsync | eligibility-filter | link (every context, F6) | RefreshProjects | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Projects.cs:141` | RefreshSingleRepositoryProjectsAsync | eligibility-filter | link (every context, F6) | RefreshProjects (single) | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Projects.cs:191` | RunUpdateSingleRepositoryAsync | eligibility-filter | link (every context, F6) | UpdateDependencies | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Projects.cs:213` | Task | eligibility-filter | link (every context, F6) | UpdateDependencies | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Projects.cs:215` | Task | eligibility-filter | link (every context, F6) | UpdateDependencies | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Projects.cs:220` | Task | eligibility-filter | link (every context, F6) | UpdateDependencies | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Projects.cs:252` | SyncDependenciesAsync | eligibility-filter | link (every context, F6) | SyncDependencies | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Projects.cs:254` | SyncDependenciesAsync | eligibility-filter | link (every context, F6) | SyncDependencies | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Projects.cs:260` | SyncDependenciesAsync | eligibility-filter | link (every context, F6) | SyncDependencies | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.ResponseParsing.cs:116` | ParseSyncRepositoryResponse | state-write | link + context state | - | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.ResponseParsing.cs:169` | ParseRefreshRepositoryVersionResponse | state-write | link + context state | - | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.ResponseParsing.cs:175` | ParseRefreshRepositoryVersionResponse | comment | - | - | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Restore.cs:65` | RestoreDependenciesAsync | comment | - | - | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Restore.cs:84` | RestoreAllWorkspacePackagesAsync | eligibility-filter | context state (link in the special Workspace) | Restore | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Restore.cs:91` | RestoreAllWorkspacePackagesAsync | eligibility-filter | context state (link in the special Workspace) | Restore | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Restore.cs:123` | RestoreSyncedWorkspacePackagesAsync | eligibility-filter | context state (link in the special Workspace) | Restore | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Restore.cs:131` | RestoreSyncedWorkspacePackagesAsync | eligibility-filter | context state (link in the special Workspace) | Restore | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Restore.cs:148` | GetTagPinnedRepositoryIdsAsync | eligibility-filter | context state (link in the special Workspace) | Restore | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Restore.cs:154` | GetTagPinnedRepositoryIdsAsync | eligibility-filter | context state (link in the special Workspace) | Restore | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Restore.cs:159` | GetTagPinnedRepositoryIdsAsync | eligibility-filter | context state (link in the special Workspace) | Restore | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.ReturnToDefault.cs:183` | BuildReturnToDefaultSnapshot | state-write | link + context state | - | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Sync.cs:191` | SyncAsync | state-write | link + context state | - | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Sync.cs:242` | RecordCheckedOutTagPinsAsync | state-write | link + context state | - | - |
| `src/GrayMoon.App/Services/Git/WorkspaceGitService.Sync.cs:479` | SnapshotFromFlatInfo | state-write | link + context state | - | - |
| `src/GrayMoon.App/Services/Orchestration/DependencyUpdateOrchestrator.cs:370` | RunVersionFileUpdateAsync | eligibility-filter | link (every context, F6) | UpdateFileVersions | - |
| `src/GrayMoon.App/Services/Orchestration/DependencyUpdateOrchestrator.cs:419` | Task | comment | - | - | - |
| `src/GrayMoon.App/Services/Orchestration/DependencyUpdateOrchestrator.cs:420` | Task | eligibility-filter | link (every context, F6) | UpdateDependencies | - |
| `src/GrayMoon.App/Services/Orchestration/DependencyUpdateOrchestrator.cs:421` | Task | eligibility-filter | link (every context, F6) | UpdateDependencies | - |
| `src/GrayMoon.App/Services/Orchestration/DependencyUpdateOrchestrator.cs:431` | Task | eligibility-filter | link (every context, F6) | UpdateDependencies | - |
| `src/GrayMoon.App/Services/Orchestration/DependencyUpdateOrchestrator.cs:446` | Task | eligibility-filter | link (every context, F6) | UpdateDependencies | - |
| `src/GrayMoon.App/Services/Orchestration/DependencyUpdateOrchestrator.cs:556` | Task | comment | - | - | - |
| `src/GrayMoon.App/Services/Orchestration/DependencyUpdateOrchestrator.cs:558` | Task | eligibility-filter | link (every context, F6) | CommitDependencyUpdates (version files) | - |
| `src/GrayMoon.App/Services/Orchestration/DependencyUpdateOrchestrator.cs:559` | Task | eligibility-filter | link (every context, F6) | CommitDependencyUpdates (version files) | - |
| `src/GrayMoon.App/Services/Orchestration/DependencyUpdateOrchestrator.cs:562` | Task | eligibility-filter | link (every context, F6) | CommitDependencyUpdates (version files) | - |
| `src/GrayMoon.App/Services/Orchestration/DependencyUpdateOrchestrator.cs:563` | Task | eligibility-filter | link (every context, F6) | CommitDependencyUpdates (version files) | - |
| `src/GrayMoon.App/Services/Orchestration/WorkspaceSyncHandler.ReturnBatch.cs:184` | ReturnToDefaultUnattendedBatchAsync | eligibility-filter | context list DTO | ReturnToDefault | - |
| `src/GrayMoon.App/Services/Orchestration/WorkspaceSyncHandler.cs:274` | ExecuteReturnToDefaultAsync | eligibility-refuse | context list DTO | ReturnToDefault | - |
| `src/GrayMoon.App/Services/Orchestration/WorkspaceSyncHandler.cs:383` | ReturnToDefaultUnattendedAsync | eligibility-filter | context list DTO | ReturnToDefault | - |
| `src/GrayMoon.App/Services/Orchestration/WorkspaceSyncHandler.cs:460` | ClassifyRepository | eligibility-filter | context list DTO | ReturnToDefault | - |
| `src/GrayMoon.App/Services/Orchestration/WorkspaceSyncHandler.cs:461` | ClassifyRepository | eligibility-filter | context list DTO | ReturnToDefault | - |
| `src/GrayMoon.App/Services/Orchestration/WorkspaceSyncHandler.cs:481` | ClassifyRepository | eligibility-filter | context list DTO | ReturnToDefault | - |
| `src/GrayMoon.App/Services/Orchestration/WorkspaceSyncHandler.cs:492` | ClassifyRepository | eligibility-filter | context list DTO | ReturnToDefault | - |
| `src/GrayMoon.App/Services/Orchestration/WorkspaceSyncHandler.cs:501` | ClassifyRepository | eligibility-filter | context list DTO | ReturnToDefault | - |
| `src/GrayMoon.App/Services/Orchestration/WorkspaceSyncHandler.cs:521` | ClassifyRepository | eligibility-filter | context list DTO | ReturnToDefault | - |
| `src/GrayMoon.App/Services/Orchestration/WorkspaceSyncHandler.cs:546` | BuildPlan | eligibility-filter | context list DTO | ReturnToDefault | - |
| `src/GrayMoon.App/Services/Orchestration/WorkspaceUndoPushHandler.cs:27` | Task | eligibility-filter | context list DTO | UndoPush | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListMapper.cs:16` | ToLink | declaration | - | - | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListMapper.cs:35` | ToLink | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListModels.cs:27` | WorkspaceRepositoryLinkListItemDto | declaration | - | - | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListModels.cs:58` | WorkspaceRepositoryLinkListItemDto | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs:81` | GetHeaderStateAsync | eligibility-filter | link (special Workspace) | header counter | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs:83` | GetHeaderStateAsync | eligibility-filter | link (special Workspace) | header counter | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs:85` | GetHeaderStateAsync | eligibility-filter | link (special Workspace) | header counter | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs:87` | GetHeaderStateAsync | eligibility-filter | link (special Workspace) | header counter | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs:89` | GetHeaderStateAsync | eligibility-filter | link (special Workspace) | header counter | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs:120` | GetHeaderStateAsync | eligibility-filter | link (special Workspace) | header counter | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs:122` | GetHeaderStateAsync | eligibility-filter | link (special Workspace) | header counter | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs:124` | GetHeaderStateAsync | eligibility-filter | link (special Workspace) | header counter | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs:126` | GetHeaderStateAsync | eligibility-filter | link (special Workspace) | header counter | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs:128` | GetHeaderStateAsync | eligibility-filter | link (special Workspace) | header counter | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs:132` | GetHeaderStateAsync | eligibility-filter | link (special Workspace) | header counter | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs:176` | GetHeaderStateAsync | eligibility-filter | context state (Feature) | header counter | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs:179` | GetHeaderStateAsync | eligibility-filter | context state (Feature) | header counter | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs:182` | GetHeaderStateAsync | eligibility-filter | context state (Feature) | header counter | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs:184` | GetHeaderStateAsync | eligibility-filter | context state (Feature) | header counter | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs:187` | GetHeaderStateAsync | eligibility-filter | context state (Feature) | header counter | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs:224` | GetHeaderStateAsync | eligibility-filter | context state (Feature) | header counter | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs:228` | GetHeaderStateAsync | eligibility-filter | context state (Feature) | header counter | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs:231` | GetHeaderStateAsync | eligibility-filter | context state (Feature) | header counter | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs:233` | GetHeaderStateAsync | eligibility-filter | context state (Feature) | header counter | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs:236` | GetHeaderStateAsync | eligibility-filter | context state (Feature) | header counter | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs:241` | GetHeaderStateAsync | eligibility-filter | context state (Feature) | header counter | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs:654` | Project | presentation | list projection | - | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs:688` | Project | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs:724` | Project | presentation | list projection | - | - |
| `src/GrayMoon.App/Services/Queries/WorkspaceRepositoryLinkListQueryService.cs:751` | Project | pin-bookkeeping | Feature row | - | - |
| `src/GrayMoon.App/Services/Ui/WorkspacePendingActionsService.cs:89` | ComputeNotification | eligibility-filter | context list DTO | notification counter | - |
| `src/GrayMoon.App/Services/Ui/WorkspacePendingActionsService.cs:90` | ComputeNotification | eligibility-filter | context list DTO | notification counter | - |
| `src/GrayMoon.App/Services/Ui/WorkspacePendingActionsService.cs:92` | ComputeNotification | eligibility-filter | context list DTO | notification counter | - |
| `src/GrayMoon.App/Services/Ui/WorkspacePendingActionsService.cs:97` | ComputeNotification | eligibility-filter | context list DTO | notification counter | - |
| `src/GrayMoon.App/Services/Ui/WorkspacePendingActionsService.cs:126` | ComputeNotification | eligibility-filter | context list DTO | notification counter | - |
| `src/GrayMoon.App/Services/Ui/WorkspacePendingActionsService.cs:138` | IsOnTag | eligibility-filter | context list DTO | notification counter | - |
| `src/GrayMoon.App/Services/Worker/SyncCommandHandler.cs:216` | BuildSnapshotFromFlatNotification | state-write | link + context state | - | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePullRequestService.cs:104` | RefreshPullRequestsAsync | comment | - | - | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:167` | RunPushAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:168` | RunPushAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:169` | RunPushAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:863` | RunPushLaneAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:901` | RunPushLaneAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:930` | PushLaneLevelAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:942` | PushLaneLevelAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:1009` | Task | eligibility-refuse | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:1010` | Task | eligibility-refuse | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:1090` | RunPushReposInLevelOrderAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:1091` | RunPushReposInLevelOrderAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:1092` | RunPushReposInLevelOrderAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:1193` | RunPushReposParallelAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:1194` | RunPushReposParallelAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:1195` | RunPushReposParallelAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:1527` | UpdateCommitCountsAndUpstreamAfterPushAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:1571` | UpdateCommitCountsAndUpstreamAfterPushAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:1572` | UpdateCommitCountsAndUpstreamAfterPushAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:1576` | UpdateCommitCountsAndUpstreamAfterPushAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:1643` | ContextCheckout | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:1660` | LoadContextCheckoutsAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:1672` | LoadContextCheckoutsAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:1679` | LoadContextCheckoutsAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:1684` | LoadContextCheckoutsAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:1688` | LoadTagPinnedRepoIdsAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspacePushService.cs:1693` | LoadTagPinnedRepoIdsAsync | eligibility-filter | context (state row, else link) | Push | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspaceRepositoryStateWriter.cs:133` | ApplyAsync | state-write | link + context state | - | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspaceRepositoryStateWriter.cs:203` | ApplyTagRefreshAsync | state-write | link + context state | - | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspaceRepositoryStateWriter.cs:226` | StageTagRefresh | state-write | link + context state | - | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspaceRepositoryStateWriter.cs:246` | StageTagRefresh | state-write | link + context state | - | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspaceRepositoryStateWriter.cs:249` | StageTagRefresh | state-write | link + context state | - | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspaceRepositoryStateWriter.cs:260` | StageTagRefresh | state-write | link + context state | - | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspaceRepositoryStateWriter.cs:262` | StageTagRefresh | state-write | link + context state | - | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspaceRepositoryStateWriter.cs:278` | ApplyIdentity | state-write | link + context state | - | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspaceRepositoryStateWriter.cs:280` | ApplyIdentity | state-write | link + context state | - | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspaceRepositoryStateWriter.cs:291` | ApplyIdentity | state-write | link + context state | - | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspaceRepositoryStateWriter.cs:303` | MirrorIdentityToLink | state-write | link + context state | - | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspaceRepositoryStateWriter.cs:343` | ApplySyncStatus | state-write | link + context state | - | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspaceRepositoryStateWriter.cs:544` | ApplyBatchAsync | state-write | link + context state | - | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspaceRepositoryStateWriter.cs:605` | StageTracked | state-write | link + context state | - | - |
| `src/GrayMoon.App/Services/Workspaces/WorkspaceRepositoryStateWriter.cs:624` | StageTracked | state-write | link + context state | - | - |
| `src/GrayMoon.Application/IWorkspaceSyncOperations.cs:51` | ReturnToDefaultRepositoryPlan | declaration | - | - | - |
| `src/GrayMoon.Worker/Abstractions/IGitRepositoryReader.cs:78` | - | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Commands/CheckoutHookSyncCommand.cs:85` | ExecuteAsync | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Commands/CheckoutHookSyncCommand.cs:107` | ExecuteAsync | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Commands/CommitHookSyncCommand.cs:55` | ExecuteAsync | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Commands/CreateBranchCommand.cs:69` | ExecuteAsync | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Commands/FetchCommitsCommand.cs:31` | ExecuteAsync | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Commands/GetBranchesCommand.cs:51` | ExecuteAsync | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Commands/GetHeadCommitsCommand.cs:67` | ExecuteAsync | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Commands/MergeHookSyncCommand.cs:53` | ExecuteAsync | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Commands/PushHookSyncCommand.cs:50` | ExecuteAsync | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Commands/PushHookSyncCommand.cs:67` | ExecuteAsync | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Commands/PushHookSyncCommand.cs:146` | SendDeferredPostPushCountsAsync | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Commands/RefreshBranchesCommand.cs:63` | ExecuteAsync | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Commands/RefreshRepositoryVersionCommand.cs:39` | ExecuteAsync | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Commands/ReturnToDefaultBranchCommand.cs:115` | ExecuteAsync | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Commands/StageAndCommitCommand.cs:26` | ExecuteAsync | eligibility-refuse | live Git | CommitDependencyUpdates | - |
| `src/GrayMoon.Worker/Commands/StageAndCommitCommand.cs:27` | ExecuteAsync | eligibility-refuse | live Git | CommitDependencyUpdates | - |
| `src/GrayMoon.Worker/Commands/SyncRepositoryCommand.cs:517` | ReadRefsAsync | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Commands/SyncRepositoryCommand.cs:599` | ReadRefsWithGitCliAsync | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Commands/SyncRepositoryCommand.cs:600` | ReadRefsWithGitCliAsync | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Commands/SyncRepositoryCommand.cs:617` | ReadRefsWithGitCliAsync | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Commands/SyncRepositoryCommand.cs:618` | ReadRefsWithGitCliAsync | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Jobs/Requests/CreateGitWorktreeRequest.cs:31` | - | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Models/LocalGitSnapshot.cs:15` | - | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Models/LocalGitSnapshot.cs:34` | LocalGitSnapshot | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Services/GitCliRepositoryReader.cs:449` | GetCheckedOutTagAsync | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Services/LibGit2SharpLocalGitSnapshotReader.cs:136` | ReadCore | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Services/LibGit2SharpLocalGitSnapshotReader.cs:169` | ReadCore | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Services/LibGit2SharpLocalGitSnapshotReader.cs:305` | PickCheckedOutTag | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Services/RepositoryStateProbe.cs:49` | CaptureAsync | worker-git-state | live Git | - | - |
| `src/GrayMoon.Worker/Services/RepositoryStateProbe.cs:103` | CaptureAsync | worker-git-state | live Git | - | - |

### Page filters

The page filters (`eligibility-ui` rows in `Components/Pages/WorkspaceRepositories.*.cs`) are inline lambdas inside component methods; none is a static helper that a test can call. 0A may not change production code, so they are **not** characterized by a test of their own. They are covered in two ways: the service behind each one is characterized in Part E, and 0B extracts each filter into the policy, where it gets a direct test before the page is switched over. The static helpers that do exist are tested: `NotificationPanelRepoSets`, `PRBadge.ShowsCreateBadge`, `WorkspacePendingActionsService.ComputeNotification`.

### What Part A shows

- **Three tag tests.** `IsNullOrWhiteSpace` in memory, `== null || == ""` in special-Workspace SQL, `IsNullOrEmpty` in Feature SQL (F8, answered in Part D).
- **Two subjects for one question (F6).** Update, commit and project refresh read the shared link in every context; push, restore, undo push and the counters read the per-context state.
- **The special Workspace push plan reads the context state row when one exists**, and the link only when it does not. A test that pins only the link does not take the repository out of the push plan. The state writer keeps the two in step in production.
- **Five copies of the per-context rule (F7)**: `WorkspaceProjectRepository.Push.cs:159-190`, `WorkspacePushService.cs:1643-1693`, `WorkspaceGitService.Restore.cs:148-160`, `WorkspaceRepository.cs:310-347` and `:441-481`. They differ for a Feature repository with no state row (Part E, F7 tests).

## 3. Part B - App: routes, lock, service-side checks

Short names used in Parts B and D:

- OpsEp   = src/GrayMoon.App/Api/Endpoints/WorkspaceOperationsEndpoints.cs
- BrEp    = src/GrayMoon.App/Api/Endpoints/BranchEndpoints.cs
- Runner  = src/GrayMoon.App/Services/Jobs/WorkspaceOperationRunner.cs
- BJS     = src/GrayMoon.App/Services/Jobs/BackgroundJobService.cs
- Keys    = src/GrayMoon.App/Services/Jobs/WorkspaceJobKeys.cs
- BrOps   = src/GrayMoon.App/Services/Application/WorkspaceBranchOperations.cs
- Writer  = src/GrayMoon.App/Services/Workspaces/WorkspaceRepositoryStateWriter.cs
- GitBr   = src/GrayMoon.App/Services/Git/WorkspaceGitService.Branches.cs
- BrHand  = src/GrayMoon.App/Services/Orchestration/WorkspaceBranchHandler.cs
- WsRepo  = src/GrayMoon.App/Repositories/WorkspaceRepository.cs
- Reg     = src/GrayMoon.App/GrayMoonServiceRegistration.cs (exact folder not re-checked; line numbers verified)
- Pages   = src/GrayMoon.App/Components/Pages

### Endpoint inventory

Lock legend: "RunExclusive" = file-local WorkspaceCommandHttp.RunExclusiveAsync (OpsEp:479-504), which calls
the legacy IWorkspaceOperationRunner.TryStart (OpsEp:487-497) = structural lock (Runner:55-62), and answers
409 "A workspace operation is already running." when refused (OpsEp:499-500). Every OpsEp and BrEp handler
runs in the special Workspace context (OpsEp:13-17). No endpoint uses TryStartContext.

#### WorkspaceOperationsEndpoints.cs

| Route | Calls | Mutates repositories | Lock | Repository ids |
|---|---|---|---|---|
| GET /api/workspaces (:21) | IWorkspaceCatalogOperations.ListAsync (:47-48) | read-only | none | n/a |
| GET /api/workspaces/{id} (:22) | catalog.GetAsync (:50-54) | read-only | none | n/a |
| GET .../repos (:25) | IWorkspaceRepositoryLinkListQueryService.GetAllSnapshotsAsync (:56-66) | read-only | none | n/a |
| GET .../operations (:26) | runner.GetRunning (:68-82) | read-only | none | n/a |
| POST .../update (:27) | IWorkspaceUpdateOperations.UpdateAsync (:84-113) | yes: dependency and version file edits + commit | RunExclusive | optional (RepositoryIds?.ToHashSet()). null = all repositories that are not on a tag (DependencyUpdateOrchestrator.cs:429-432). Path from UpdateAsync to the orchestrator: not verified |
| POST .../push (:28) | ids -> IWorkspacePushOperations.PushAsync, none/empty -> PushPendingAsync (:115-138) | yes: push | RunExclusive | optional; none = all pending |
| POST .../update-and-push (:29) | IWorkspaceUpdateAndPushOperations.ExecuteAsync(StartUpdateAndPush) (:145-168) | yes: update, commit, push | RunExclusive | no ids in request |
| POST .../update-and-push/approve (:30) | ExecuteAsync(ApproveNormalPushContinuation) (:170-190) | yes: push | RunExclusive | no ids; pendingApprovalId required |
| POST .../prepare-workspace (:31) | IWorkspacePreparationOperations.PrepareAsync, then maybe PushPendingAsync (:212-266) | yes: create/checkout branch, update, commit, push | RunExclusive | optional. null = ALL repositories, including ones on a tag (see item 7). Empty set = stopped (PrepareWorkspaceOrchestrator.cs:38-42) |
| POST .../sync (:32) | IWorkspaceSyncOperations.SyncAsync (:268-288) -> WorkspaceSyncHandler.RunSyncAsync (WorkspaceSyncOperations.cs:17-34) | yes: clone/fetch/state sync | RunExclusive | optional; meaning of null inside RunSyncAsync: not verified |
| POST .../return-to-default (:33) | IWorkspaceSyncOperations.ReturnToDefaultAsync (:290-307) | yes: checkout default, delete branch | RunExclusive | REQUIRED, 400 "repositoryIds is required." (:298-299) |
| POST .../pull (:34) | Count > 1 -> PullLevelAsync, else PullAsync (:309-324) | yes: pull (merge) | RunExclusive | no id -> repository id 0 (:309-324) -> "Repository not found." repository error (WorkspaceCommitSyncHandler.cs:49-54) -> "Pull failed." (WorkspaceSyncOperations.cs:91-92). GetByIdAsync(0) returning null: not verified |
| POST .../undo-push (:35) | IWorkspaceSyncOperations.UndoPushAsync (:326-338) | yes: reverts outgoing commits | RunExclusive | no ids; handler takes links not on a tag with outgoing commits (WorkspaceUndoPushHandler.cs:27) |
| POST .../restore-packages (:36) | IWorkspaceUpdateOperations.RestorePackagesAsync (:340-351) | dotnet restore (working tree effect not verified) | RunExclusive | no ids |
| POST .../pull-requests (:37) | IWorkspacePullRequestOperations.CreateAsync (:353-368) | remote only: creates PRs | RunExclusive | requests required |
| POST .../pull-requests/merge (:38) | MergeAsync (:370-391) | remote only: merges PR | RunExclusive | repositoryId required |
| GET .../git-changes (:39) | IWorkspaceGitChangesOperations.GetAsync (:393-402) | read-only | none | n/a |
| POST .../git-changes/commit (:40) | CommitAsync (:404-421) | yes: commit (optional stage all) | RunExclusive | repositoryId + message required |
| POST .../git-changes/stage (:41) | StageAsync (:423-440) | yes: index | RunExclusive | repositoryId required |
| POST .../git-changes/unstage (:42) | UnstageAsync (:442-459) | yes: index | RunExclusive | repositoryId required |
| POST .../files/update-versions (:43) | IWorkspaceFileOperations.UpdateVersionsAsync (:461-474) | yes: writes version files | RunExclusive | no ids |

#### BranchEndpoints.cs (all POST, routes at :11-20, all call IWorkspaceBranchOperations directly, NO lock on any route)

| Route | Calls | Mutates | Repository ids |
|---|---|---|---|
| /api/branches/get | GetBranchesAsync (:37-48) | read-only (DB) | single id required |
| /api/branches/refresh | RefreshBranchesAsync (:93-106) | Worker fetch + DB writes, no working tree change | single id required |
| /api/branches/checkout | CheckoutAsync (:50-69), IsTag flag | yes: checkout branch or tag | single id required |
| /api/branches/return-to-default | ReturnToDefaultAsync (:71-91); DeleteRemoteBranch; AllowForceDeleteLocalBranch default true (:241) | yes: checkout default, delete local (+remote) branch | single id required |
| /api/branches/common | GetCommonBranchesAsync (:186-197) | read-only | none (whole Workspace) |
| /api/branches/exists-in-workspace | CountReposWithLocalBranchAsync (:24-35) | read-only | none |
| /api/branches/create | CreateBranchAsync (:108-127) | yes: creates + checks out branch | single id required |
| /api/branches/set-upstream | SetUpstreamAsync (:129-147) | yes: pushes / sets upstream | single id required |
| /api/branches/delete | DeleteBranchAsync (:149-169), IsRemote, Force | yes: deletes local or remote branch | single id required |
| /api/branches/update-from-default | UpdateBranchFromDefaultAsync (:171-184) | yes: merges default into current branch | single id required |

#### SyncEndpoints.cs, WorkspaceEndpoints.cs, WorkerEndpoints.cs (under src/GrayMoon.App/Api/Endpoints)

| Route | Calls | Mutates | Lock | Ids |
|---|---|---|---|---|
| POST /api/sync (SyncEndpoints.cs:12, :18-72) | SyncBackgroundService.EnqueueSync (:65), 202 | queued state sync (DB writes) | none | repositoryId + workspaceId required |
| GET /api/sync/queue (:13, :74-78) | queue depth | read-only | none | n/a |
| GET /api/workspaces/{id}/files (WorkspaceEndpoints.cs:16) | IWorkspaceFileOperations.ListAsync | read-only | none | n/a |
| POST /api/workspaces/{id}/files (:18, :63-73) | IWorkspaceFileOperations.AddAsync | DB file config rows; repository change not verified | none | n/a |
| GET /api/workspaces/{id}/files/search (:20) | SearchAsync (via Worker) | read-only | none | n/a |
| GET /workspaces/{id}/capabilities (:25) | IWorkspaceCapabilitiesResolver.GetAsync | read-only | none | n/a |
| GET /api/worker/download, /install, /uninstall (WorkerEndpoints.cs:14-16) | installer files | none | none | n/a |
| POST /api/worker/pair (:17), /install-failure (:18) | WorkerPairingService.TryRedeem, WorkerConnectionTracker | none | none | n/a |

### F18 lock audit

#### How the runner works

- One singleton implements both IWorkspaceOperationRunner and IWorkspaceOperationLock (Reg:197-198).
- Single gate (Runner:15). Structural op per Workspace (:16), context op per context id (:17-18).
- TryStart (legacy, :55-62) and TryStartStructural (:64-81) share TryStartStructuralCore (:136-178): refused when the
  Workspace has a structural op (:146-150) or any context op (:152-156).
- TryStartContext (:83-134): refused when the Workspace is structurally busy (:94-98) or the same context is busy
  (:100-104). Different contexts may run at once.
- On refusal the work delegate is never invoked and the out operation is the running one. Work runs on Task.Run
  (:180-215); lock released in finally (:209-214).
- Plan correction: REST uses the legacy TryStart, not TryStartStructural. Same effect.
- Production callers: TryStartStructural at WorkspaceFeatureOperations.cs:62 (create-feature), :1793 (repair-feature),
  :1863 (rollback-feature); WorkspaceExternalWorktreeOperations.cs:144; WorkspaceCheckoutRefresh.cs:61;
  WorkspaceRepositoryOperations.cs:60. TryStartContext: only BJS:135. TryStart: BJS:152 and OpsEp:487.
- The context lock is unreachable in production: it needs a key /workspaces/{id}/ctx/{contextId}... (Keys:28-43) and
  the builders of such keys (Keys:54-55, :60-61, :70-71) have no production caller (grep). Every page job is structural.

#### (a) Blazor page jobs

- BJS.StartJob (:77-121): a mutation key goes to StartWorkspaceJob (:89-90, :123-165) = lock. Any other key runs on a
  plain Task.Run with NO lock (:92-120).
- Mutation key = any non-scan key of the form /workspaces/{id}[/...] (Keys:9-23, :45-46).
- Page keys are the lowercased URL path (WorkspaceRepositories.State.cs:98, WorkspaceGitChanges.JobHelper.cs:18).
  /workspaces/{id} (WorkspaceRepositories.razor:1) and /workspaces/{id}/changes (WorkspaceGitChanges.razor:1) are both
  mutation keys. So YES, /workspaces/{id}/changes takes the structural lock.
- Scan key /workspaces/{id}/changes:scan takes no lock (JobHelper.cs:26, :127-159). Read-only scan.
- WorkspaceRepositories page: every StartPageJob call goes through the lock (definition JobHelper.cs:26-62). Call sites
  (bodies not read): Branches.cs:143,185,239,295,338,380,503; CommitSync.cs:105,130; Dependencies.cs:145,182;
  FileVersions.cs:18,69,202; Merge.cs:471; BulkMerge.cs:412; Push.cs:188,201,311,346,395; PrepareWorkspace.cs:54;
  PullRequests.cs:247; ReturnToDefault.cs:41,139,239,303,374,442; UndoPush.cs:46; Sync.cs:55,82,148;
  Update.cs:99,305,325; razor.cs:247.

#### (b) Git Changes page (Pages/WorkspaceGitChanges*.cs)

| Action | Site | Lock |
|---|---|---|
| File/folder Stage | WorkspaceGitChanges.razor.cs:823-835 -> RunMutationAsync (:888-928) -> StageAsync (:832) | NONE (only per-repository UI guard :896, :808) |
| File/folder Unstage | razor.cs:837-848 -> UnstageAsync (:845) | NONE |
| File/folder Undo (discard) | Discard.cs:75-132 -> WorkerClient.DiscardAsync (:129) | NONE, and no facade (page calls the Worker client) |
| Repository-scope Stage/Unstage | razor.cs:850-886, StartPageJob (:864) | structural |
| Repository-row Undo | Discard.cs:222-254, StartPageJob (:235), DiscardAsync (:244) | structural, no facade |
| Undo all changed | Discard.cs:289-336, StartPageJob (:300) | structural, no facade |
| Undo selected | Discard.cs:382-431, StartPageJob (:392) | structural, no facade |
| Multi-select stage/unstage | MultiSelect.cs:60, ops :93-94 | structural |
| Commit | MultiRepo.cs:201, CommitAsync (:267) | structural |
| Section bulk stage/unstage | MultiRepo.cs:447, ops :475-476 | structural |

A separate single-repository commit path outside MultiRepo.cs: none found by grep; not verified further.

#### (c) WorkspaceActionNotificationPanel (src/GrayMoon.App/Components/Shared/WorkspaceActionNotificationPanel.razor)

- Key is /workspaces/{id} (:317), a mutation key, so structural lock.
- All nine job starts use it: :454 (undo push), :619 (update), :713, :791 (file versions), :852, :1034 (push),
  :1068 (set upstream), :1100, :1141 (sync commits).
- Job bodies and any panel action that does not use StartJob: not verified.

#### (d) FeatureRemovalQueue (src/GrayMoon.App/Services/Features/FeatureRemovalQueue.cs)

- Singleton hosted service; runs IWorkspaceFeatureOperations.RemoveFeatureAsync in its own scope (:182-190).
- It takes no runner lock itself (no lock dependency, constructor :18-22).
- RemoveFeatureAsync starts at WorkspaceFeatureOperations.cs:672; the only operationLock calls in that file are at
  :62, :1793, :1863 (grep), none of them inside the remove path by name. So Feature removal runs OUTSIDE the lock.
  Whether another gate (lifecycle state Removing) serializes it: not verified.
- WorkspaceFeatureReconciler (:162, :247, :312) and FeatureFolderCleanupService (:272) only CHECK
  IsWorkspaceStructurallyBusy and skip; they do not take the lock.

#### (e) REST

- All OpsEp POST routes: structural lock (item 1).
- All /api/branches/* routes, POST /api/sync, POST /api/workspaces/{id}/files: no lock (item 1).

#### (f) Hook-driven writes

- SyncCommandHandler (src/GrayMoon.App/Services/Worker/SyncCommandHandler.cs) has no lock dependency (:17-20) and
  writes through the state writer (:82) and ApplyTagRefreshAsync (:104). Outside the lock.
- SyncBackgroundService (same folder) has no lock dependency (:11-14); EnqueueSync :35-59. Outside the lock.
- How the hook notification reaches SyncCommandHandler.HandleAsync: not verified.

#### Entry points OUTSIDE the lock (mutating or state-writing)

1. Git Changes file/folder Stage (WorkspaceGitChanges.razor.cs:832).
2. Git Changes file/folder Unstage (razor.cs:845).
3. Git Changes file/folder Undo (WorkspaceGitChanges.Discard.cs:129), also bypasses the facade.
4. SwitchBranchModal Delete Branch, direct BranchOperations.DeleteBranchAsync
   (src/GrayMoon.App/Components/Modals/SwitchBranchModal.razor:1464; folder not re-checked). RefreshBranchesAsync direct
   at :1617 and NewPullRequestModal.razor:654.
5. All mutating /api/branches/* routes: checkout, return-to-default, create, set-upstream, delete,
   update-from-default, refresh.
6. POST /api/sync and the hook path (SyncCommandHandler) - DB state writes only.
7. POST /api/workspaces/{id}/files.
8. Feature removal (FeatureRemovalQueue -> RemoveFeatureAsync).
9. Workspace membership saves: WorkspaceRepository.UpdateAsync / AddRepositoriesAsync from Workspaces.razor:704, :767,
   :1254 and WorkspaceRepositories.RepositoriesModal.cs:76 (DB only).
10. Any job started with a key that is not /workspaces/{id}...: RestoreWorkspaceModal.razor.cs:354-363 uses the current
    page path (which page hosts it: not verified).
11. Actions page bulk run/rerun (GitHub API only, item 7).
12. Pages with no StartJob at all: WorkspaceFiles, WorkspaceActions, WorkspaceDependencies, WorkspaceProjects,
    WorkspacePackages. Their mutating calls: not verified.

### F16 / F17: where link rows are created

| Site | Shape | Notes |
|---|---|---|
| WsRepo:515 in AddRepositoriesAsync (:493-526) | WorkspaceId, RepositoryId, SyncStatus = NeedsSync; Role defaults to Source (WorkspaceRepositoryLink.cs:25) | scoped context; refused when Features exist (:495-499) |
| WsRepo:617 in ReplaceRepositoriesCoreAsync (:540-634) | same shape | existing links untouched; Workspace-role link never removed (:564-570); no-op when unchanged (:579-580); refused when Features exist (:582-586) |
| WorkspaceRepositoryOperations.cs:370 (Restore) | Role = Source, SyncStatus = NeedsSync | |
| WorkspaceRepositoryOperations.cs:563-570 in LinkAndAttachAsync (:542) | Role = Workspace | not a Source |

- Plan correction: WsRepo.AddAsync (:60-94) constructs no link; it calls ReplaceRepositoriesAsync (:92) -> Core
  (:528-533). The plan's ":60" is the method start.
- Not persisted: WorkspaceRepositoryLinkListMapper.cs:9, WorkspaceRepositoryLink.cs:149.
- Callers: Workspaces.razor:675 (AddAsync with an empty list on create), :704-711 (Edit Workspace save), :767
  (repositories modal), :1254 (import, AddRepositoriesAsync); WorkspaceRepositories.RepositoriesModal.cs:76-80.
- Edit Workspace save: the page calls WorkspaceRepository.UpdateAsync directly (Workspaces.razor:704-711), then
  SyncWorkspaceDefinitionAsync (:712) and PendingActionsService.RefreshAsync (:713). UpdateAsync is WsRepo:96-161
  (fresh factory context, one transaction, Core at :158). No facade, no lock. F17 confirmed; the plan's ":629" is
  stale (now :704).

### Service-side tag checks before the Worker call

| Flow | Tag check on the service side | Worker call |
|---|---|---|
| Pull, single (CommitSyncAsync) | NONE (WorkspaceCommitSyncHandler.cs:31-88) | CommitSyncRepository (:88) |
| Pull, level (CommitSyncLevelAsync) | NONE (:124-197); facade adds none (WorkspaceSyncOperations.cs:69-108) | CommitSyncRepository (:197) |
| Bulk Create Branch | NONE (BrHand:49-72, GitBr:121-209). null or empty id set = ALL links (GitBr:138-144) | CreateBranch (GitBr:209) |
| Workspace-wide checkout | NONE (BrHand:235-340); only FeatureBranchGuard (:247-253) and occupancy (:262-293) | CheckoutBranch via BrOps.CheckoutAsync (BrOps:388) |
| Prepare Workspace, null ids | NONE in branch phase: null passes through (PrepareWorkspaceOrchestrator.cs:45-46, PrepareWorkspacePhases.cs:50-66) and means all repositories, including ones on a tag. Empty set is stopped (:38-42). Update phase skips tag repositories (DependencyUpdateOrchestrator.cs:420-431, :370). Push phase filters on CheckedOutTag (WorkspacePushService.cs:1527, :1572; context not read) | CreateBranch (GitBr:209) |
| Update Branch from Default | NONE (WorkspaceBranchUpdateHandler.cs:31-99). currentBranchName falls back to wr.BranchName (:83-84); value for a repository on a tag: not verified | UpdateBranchFromDefault (:99) |
| Git Changes commit / stage / unstage | NONE (WorkspaceGitChangesOperations.cs:39-105); only Workspace and link existence | workerClient.CommitAsync / StageAsync / UnstageAsync (:47, :57, :67) |
| /api/branches/checkout | NONE; FeatureBranchGuard only (BrOps:320) | CheckoutBranch (BrOps:388) or CheckoutTag (:350) |
| /api/branches/create | NONE; FeatureBranchGuard only (BrOps:636) | CreateBranch (BrOps:680) |
| /api/branches/set-upstream | NONE, no guard (BrOps:710-764) | SetUpstreamBranch (:744) |
| /api/branches/delete | NONE, no guard; refuses the current local branch (BrOps:787-793) | DeleteBranch (:804-815) |
| /api/branches/return-to-default | NONE in ReturnToDefaultDirectAsync (WorkspaceGitService.ReturnToDefault.cs:80-139); guard at BrOps:469 | ReturnToDefaultBranch (:139) |
| /api/branches/update-from-default | NONE (delegates at BrOps:852 to the handler above) | UpdateBranchFromDefault |
| Actions page bulk run / rerun | NONE, and no Worker call. GitHub API only: RerunWorkflowAsync (Pages/WorkspaceActions.BulkActions.cs:114), RerunFailedJobsOnlyAsync (:192), RunWorkflowAsync (:272). No lock. Eligibility helpers IsLineFailedForBranch / CanRunAgain: not read | none |

Where tag checks DO exist:

- FeatureBranchGuard (src/GrayMoon.App/Services/Features/FeatureBranchGuard.cs:29-65) always allows the special
  Workspace (:49-50); it only reads the Feature's PinnedTag (:52-57). It is not a tag eligibility check.
- Return to Default batch path skips repositories on a tag (WorkspaceSyncHandler.cs:273-275, :383, :546;
  ReturnBatch.cs:184). The single-repository direct path does not.
- Update: DependencyUpdateOrchestrator.cs:370, :420-431, :446. These read the shared link's CheckedOutTag from
  workspaceRepository.GetByIdAsync, not the per-context state.
- Undo push: WorkspaceUndoPushHandler.cs:27. Update and Push default-branch review: WorkspaceUpdateAndPushOperations.cs:84.
- Commit helpers: WorkspaceGitService.Commit.cs:34, :134 (context not read).
- GetCommonBranchesAsync filters out tag repositories (BrOps:524), read-only.
- UI-only: WorkspacePendingActionsService.cs:89-138.

## 4. Part C - Worker commands

Paths in this part are relative to `src/GrayMoon.Worker` unless they start with `src/`.

### Inventory of Worker command handlers

Dispatch names are the keys in `Services/CommandDispatcher.cs:69-124`. Pool from `Services/WorkerCommandPoolRouter.cs:19-42`
(everything not listed there is main). "Writes" is from reading the handler and the `GitService` method it calls; rows marked (nv) were
classified from the handler's calls and names only, not from reading the called service.

Common side effects referred to below:
- "divergence file" = `graymoon-divergence-base` in the git dir, written/deleted by `GitService.SetDivergenceBaseBranchAsync` (Services/GitService.cs:874-892).
- "safe.directory" = `git config --global --add safe.directory` (Services/GitService.cs:119-152), global git config, not the repository.
- "hooks" = `GitService.WriteSyncHooksAsync` (Services/GitService.cs:1213), files in the repository's hooks directory.
- "fetch" = `fetch origin --prune [--tags]` (Services/GitService.cs:262-269): remote-tracking refs, tags, objects.
- "GitVersion" = the version provider process; whether it writes a cache under the git dir was not verified.

| Command | File (Commands/) | Writes | Pool |
|---|---|---|---|
| SyncRepository | SyncRepositoryCommand.cs | Creates workspace folder; may clone (working tree); safe.directory; long-paths config; fetch; detached checkout of pinned commit (new clone only); divergence file; hooks; may `remote set-head`; GitVersion; may delete a failed clone | main |
| RefreshRepositoryVersion | RefreshRepositoryVersionCommand.cs | Divergence file (line 49); GitVersion; `ls-remote` (no write) | main |
| GetCommitCounts | GetCommitCountsCommand.cs | Nothing | main |
| RefreshRepositoryProjects | RefreshRepositoryProjectsCommand.cs | Nothing (file scan) | main |
| EnsureWorkspace | EnsureWorkspaceCommand.cs | Creates the workspace folder (line 14) | main |
| GetWorkspaceRepositories | GetWorkspaceRepositoriesCommand.cs | Nothing | main |
| GetRepositoryVersion | GetRepositoryVersionCommand.cs | Nothing in git; GitVersion | main |
| GetGitVersionAtDefaultTip | GetGitVersionAtDefaultTipCommand.cs | Nothing in git; GitVersion (nv) | main |
| GetWorkspaceExists | GetWorkspaceExistsCommand.cs | Nothing | main |
| GetHostInfo | GetHostInfoCommand.cs | Nothing (version probes) | main |
| SyncRepositoryDependencies | SyncRepositoryDependenciesCommand.cs | Working tree files: package versions in project / props files (line 46) | main |
| CommitSyncRepository | CommitSyncRepositoryCommand.cs | Divergence file; fetch; pull (working tree, index, local ref); `merge --abort` on conflict; remote (push) | main |
| GetBranches | GetBranchesCommand.cs | Fetch (line 34, conditional) (remote-tracking refs, tags); else nothing | main |
| CheckoutBranch | CheckoutBranchCommand.cs | Working tree, index, HEAD; may create a local tracking branch | main |
| CheckoutTag | CheckoutTagCommand.cs | Working tree, index, HEAD (detached at `refs/tags/<name>`, GitService.cs:836) | main |
| ReturnToDefaultBranch | ReturnToDefaultBranchCommand.cs | Optional remote branch delete; fetch; checkout default; local branch delete; pull | main |
| RefreshBranches | RefreshBranchesCommand.cs | Fetch (line 34, conditional); else nothing | main |
| CreateBranch | CreateBranchCommand.cs | Local ref + HEAD (`checkout -b <new> --no-track <start>`, GitService.cs:611); minimal fetch; GitVersion | main |
| SetUpstreamBranch | SetUpstreamBranchCommand.cs | Remote (`push -u origin <branch>`, line 40) and git config (upstream) | main |
| DeleteBranch | DeleteBranchCommand.cs | Local ref, or remote branch (+ maybe a remote-tracking ref) | main |
| StageAndCommit | StageAndCommitCommand.cs | Index and local ref (commit) | main |
| PushRepository | PushRepositoryCommand.cs | Fetch; pull (working tree, index, local ref); `merge --abort` on conflict; remote (push); git config when `-u` | main |
| SearchFiles | SearchFilesCommand.cs | Nothing | main |
| UpdateFileVersions | UpdateFileVersionsCommand.cs | Working tree file (line 65) | main |
| GetFileContents | GetFileContentsCommand.cs | Nothing | main |
| ValidatePath | ValidatePathCommand.cs | May create the directory being validated (line 22) | main |
| DotnetRestore | DotnetRestoreCommand.cs | `dotnet restore --force --no-cache` (obj folders, NuGet cache); no git | main |
| UndoPush | UndoPushCommand.cs | `reset --mixed|--hard origin/<branch>` (local ref, index, working tree when hard); pushes `-u` first when `origin/<branch>` is missing (GitService.cs:990-1005) | main |
| SelfUpdate | SelfUpdateCommand.cs | Starts the installer process; nothing in a repository | main |
| CheckFileVersions | CheckFileVersionsCommand.cs | Nothing | main |
| GetHeadCommits | GetHeadCommitsCommand.cs | Nothing | main |
| ResolveGeneratedPackageReferences | ResolveGeneratedPackageReferencesCommand.cs | Nothing (nv) | main |
| UpdateBranchFromDefault | UpdateBranchFromDefaultCommand.cs | Fetch; `merge --no-edit <remoteBranch>` (working tree, index, local ref); divergence file (line 84) | main |
| FetchCommits | FetchCommitsCommand.cs | safe.directory; fetch; divergence file (line 46, only when not on a tag) | main |
| GetGitChangeStatus | GetGitChangeStatusCommand.cs | Nothing in the repository (status scan; starts a watcher) | read |
| EnsureGitChangesWatchers | EnsureGitChangesWatchersCommand.cs | Nothing (file watchers only, no git) | read |
| GetGitFileDiff | GetGitFileDiffCommand.cs | Nothing | diff |
| StageGitChanges | StageGitChangesCommand.cs | Index (`git add`) (nv) | main |
| UnstageGitChanges | UnstageGitChangesCommand.cs | Index (`git restore --staged`) (nv) | main |
| DiscardGitChanges | DiscardGitChangesCommand.cs | Working tree (and index) (nv - service not read) | main |
| CommitGitChanges | CommitGitChangesCommand.cs | Local ref, index (`git commit -F -`) (nv) | main |
| ListGitWorktrees | ListGitWorktreesCommand.cs | Nothing | read |
| CreateGitWorktree | CreateGitWorktreeCommand.cs | Hooks in the main repository (line 28); `git worktree add` (new folder, new local branch unless detached); long-paths config; divergence file (line 51) | main |
| RemoveGitWorktree | RemoveGitWorktreeCommand.cs | `git worktree [unlock|remove [--force]]`; deletes the worktree folder; refuses the primary worktree (GitWorktreeService.cs:256-261) | main |
| InspectWorktree | InspectWorktreeCommand.cs | Nothing | read |
| CleanupFeatureFolder | CleanupFeatureFolderCommand.cs | Deletes or marks a Feature folder (nv) | main |
| SweepPendingFeatureFolders | SweepPendingFeatureFoldersCommand.cs | Deletes marked Feature folders (nv) | main |
| AttachWorkspaceRepository | AttachWorkspaceRepositoryCommand.cs | Creates folder; clone or `init` + `remote add` + fetch + tracking checkout / unborn HEAD; safe.directory; hooks; may delete a `.git` it created (184-189) | main |
| DiscardWorkspaceRoot | DiscardWorkspaceRootCommand.cs | Deletes the workspace root folder after a clean-status check (49-58) | main |
| WriteRepositoryFile | WriteRepositoryFileCommand.cs | Working tree file (65-72) | main |
| InitCodeGraph | InitCodeGraphCommand.cs | `.codegraph/` in the target (Feature) root | main |
| UninitCodeGraph | UninitCodeGraphCommand.cs | Kills index processes; deletes `.codegraph/` | main |
| WatchCodeGraphInit | WatchCodeGraphInitCommand.cs | Nothing | main |
| CollectGitEvidence | CollectGitEvidenceCommand.cs | Nothing (nv) | ai |
| GetAiProviders | AiCommands.cs | Nothing | ai |
| GenerateAiText | AiCommands.cs | Nothing in a repository (runs an AI CLI in a temp folder) (nv) | ai |

Hook handlers (not dispatched by name; they run as `INotifyJob` through `HookSyncDispatcher.cs`, pool not verified):

| Handler | File | Writes |
|---|---|---|
| CommitHookSync | CommitHookSyncCommand.cs | Nothing in git (state probe; GitVersion) |
| CheckoutHookSync | CheckoutHookSyncCommand.cs | Minimal fetch (line 58); `fetch origin --tags` when on a tag (line 87) |
| MergeHookSync | MergeHookSyncCommand.cs | Nothing in git (state probe) |
| PushHookSync | PushHookSyncCommand.cs | Nothing in git (state probe, delayed recount) |
| RefsHookSync | RefsHookSyncCommand.cs | Nothing |

`RepositorySyncProjectMapper.cs` is a static mapper, not a handler.

#### Handlers that check a checked-out tag themselves

| File:line | What it does with it |
|---|---|
| Commands/StageAndCommitCommand.cs:26-28 | **Skips**: on a tag returns `Success = true, Committed = false` without staging or committing. The only handler that refuses to mutate because of a tag. |
| Commands/FetchCommitsCommand.cs:31, 40 | Reports `currentTag`; **skips** the commit counts and the divergence-file write when on a tag. The fetch already ran. |
| Commands/CreateBranchCommand.cs:69-71 | Runs AFTER the branch was created (line 29). Only sets the reported `branch` to "-" (skips counts). Does not refuse. |
| Commands/RefreshRepositoryVersionCommand.cs:39-41 | Report only: reported `branch` becomes "-"; counts skipped. |
| Commands/GetBranchesCommand.cs:51 | Report only (`currentTag`). |
| Commands/GetHeadCommitsCommand.cs:67-69 | Report only (per-repository tag map). |
| Commands/SyncRepositoryCommand.cs:517, 599, 617 (read) and 243-258, 275, 331-332 (use) | Report: `Tag`, `Commit`, branch "-"; fails the sync when the tag/commit pair does not normalize (247-257); still writes hooks on a tag (275). |
| Commands/CheckoutHookSyncCommand.cs:85-90, 107 | On a tag also fetches remote tags; reports `Tag`. |
| Commands/PushHookSyncCommand.cs:50-52, 67, 146 | On a tag skips the deferred post-push recount (no pushed branch); reports `Tag`. |
| Commands/CommitHookSyncCommand.cs:55, Commands/MergeHookSyncCommand.cs:53 | Report only. |
| Commands/RefreshBranchesCommand.cs:63, Commands/ReturnToDefaultBranchCommand.cs:115 | Report only (`CurrentTag` from the state probe after the work). |

No tag check at all in: PushRepository, CommitSyncRepository, UndoPush, UpdateBranchFromDefault, SyncRepositoryDependencies,
UpdateFileVersions, WriteRepositoryFile, CheckoutBranch, CheckoutTag, DeleteBranch, ReturnToDefaultBranch (before mutating),
SetUpstreamBranch, DotnetRestore, Stage/Unstage/Discard/CommitGitChanges handlers. PushRepository and CommitSyncRepository fail on a
detached HEAD indirectly: `GitRemoteIntegrateService` returns "Could not determine branch name" (Services/GitRemoteIntegrateService.cs:41-53),
but only after the fetch at line 27.

#### Precise answers

**(a) `GitService.PullAsync`** - Services/GitService.cs:398-427.
- Arguments: `{hooksPrefix}pull origin {branchName}` (line 405), run through `runner.RunRemoteAsync(GitRemoteOperation.Pull, ...)` (line 409).
  No `--ff-only`, no `--rebase`, no `--no-edit`; `hooksPrefix` comes from `GetHooksConfigPrefix(skipHooks)` (line 403). Merge versus rebase
  is therefore whatever the user's git config says.
- Non-zero exit: if `GitResiliencePipelines.IsMergeConflict(stdout, stderr)` -> returns `(false, MergeConflict: true, output)` (415-419);
  otherwise `(false, false, output)` (421-422). `PullAsync` itself does NOT abort the merge.
- The abort is done by the caller `GitRemoteIntegrateService.IntegrateAsync` (Services/GitRemoteIntegrateService.cs:74-89): `git merge --abort`
  (GitService.cs:452-462), then a second fetch, recount, and `Success = false, MergeConflict = true`.
- `ReturnToDefaultBranchCommand` calls `PullAsync` directly (Commands/ReturnToDefaultBranchCommand.cs:86) and does NOT abort on conflict:
  it returns `Success = false` and leaves the repository mid-merge.

**(b) `CommitSyncRepositoryCommand` push condition** - Commands/CommitSyncRepositoryCommand.cs:48-77.
- Line 48 writes the divergence file first. Line 50 `IntegrateAsync` (fetch; pull only when `incoming > 0`, GitRemoteIntegrateService.cs:57, 72).
- Integrate failed -> returns without pushing (51-65).
- `if (!outgoing.HasValue || outgoing.Value <= 0)` -> returns success WITHOUT pushing (70-71).
- Otherwise pushes: `git.PushAsync(repoPath, branch, bearerToken, setTracking: false)` (line 73) = `push origin <branch>`.
- So it pushes exactly when integration succeeded AND `integrate.Outgoing > 0`, where Outgoing is recounted after the pull
  (GitRemoteIntegrateService.cs:104). It never sets tracking and does not look at `HasUpstream`.

**(c) `PushRepositoryCommand`** - yes, it fetches and (when there are incoming commits) pulls before pushing.
- Commands/PushRepositoryCommand.cs:55 `remoteIntegrate.IntegrateAsync` (fetch always; pull when `incoming > 0`; abort on conflict).
- Integrate failed -> `Success = false`, no push (56-66).
- `if (outgoing <= 0 && hasUpstream)` -> success without pushing (83-87).
- Otherwise `git.PushAsync(..., setTracking: !hasUpstream)` (89-90) = `push [-u ]origin <branch>`. `branch` is `request.BranchName` when
  given, else the current branch (68-70).

**(d) `CheckoutBranchCommand`** - Commands/CheckoutBranchCommand.cs:8-49 and Services/GitService.cs:553-593.
- Never forces and never stashes: the only commands are `checkout <branch>` (562, 582) and `checkout -b <branch> origin/<branch>` (573),
  each with the hooks prefix. With local changes that conflict, git refuses and the handler returns `Success = false` with git's output.
  `skipHooks` is not passed by this handler, so the post-checkout hook fires (handler comment line 38).
- Remote-only branch: yes. When `refs/heads/<branch>` does not exist (560) and `origin/<branch>` does (571), it runs
  `checkout -b <branch> origin/<branch>` (573), which creates a local branch that tracks the remote one (git's default
  `branch.autoSetupMerge`; not forced with `--track`). If neither exists it runs plain `checkout <branch>` (582).
- Note: a request `branchName` of `origin/x` is passed to git unchanged (line 28); only the reported `CurrentBranch` strips `origin/` (39-41).
  Then 560 tests `refs/heads/origin/x`, 571 tests `origin/origin/x`, and 582 runs `checkout origin/x`, which detaches HEAD. Read from the
  code, not run.
- Request fields (Jobs/Requests/CheckoutBranchRequest.cs): `workspaceName`, `repositoryName`, `branchName`, plus the base
  `WorkspaceCommandRequest` fields (`workspaceRoot`, `workspaceRepositoryName`, `maxParallelOperations`, `capabilities`, ...).
- Response fields (Jobs/Response/CheckoutBranchResponse.cs:5-15): `success`, `currentBranch`, `errorMessage`. Nothing else (no commit, no tag).

**(e) `SyncRepositoryCommand` pin** - Commands/SyncRepositoryCommand.cs.
- Condition, line 178: `if (clonedNow && request.ApplyRepositoryTagPin)`. `clonedNow` is set true only at line 126, after this command's own
  successful `git.CloneAsync` (111) in the branch where the folder did not exist (101-127). An existing repository, or an existing empty
  folder, never gets the pin applied. It runs after a successful fetch (157-176).
- What it does, 432-451: reads the Workspace definition from `workspacePath`, `ReadTagPinCommit(definition, cloneUrl)`; null commit -> nothing;
  else `git.CheckoutCommitAsync` = `-c advice.detachedHead=false checkout --detach <sha>` (Services/GitService.cs:856). A pin error or a failed
  checkout removes the fresh clone (`RemoveFailedClone`, 453-475) and fails the sync (181-190).
- `Commit` on the response: set once, line 332 (`Commit = headCommit`). `headCommit` is non-null only when `refs.CurrentTag != null`
  (242-247): it is `reader.GetHeadCommitAsync` normalized by `WorkspaceDefinitionTagPin.TryNormalize`; a failed normalization fails the sync
  (247-257). On a branch, and on a detached HEAD that is not exactly on a tag, `Commit` is null. Early-return responses (fetch failure 164-175,
  pin failure 183-189, not cloned) do not set it.

**(f) `DeleteBranchCommand`** - Commands/DeleteBranchCommand.cs:8-52, Jobs/Requests/DeleteBranchRequest.cs:5-33, Services/GitService.cs:633-805.
- Request fields: `workspaceName`, `repositoryName`, `branchName` (trimmed, required), `isRemote` (bool), `force` (bool), `bearerToken`,
  `expectedSha`, plus base fields (`workspaceRoot` is required here, line 17-18). Response: `success`, `errorMessage`.
- Remote (`isRemote = true`): strips a leading `origin/`; refuses the repository default branch ("Cannot delete the repository default branch.", 646-652).
  - With `expectedSha`: `ls-remote --heads origin refs/heads/<name>` (663, 1563); if absent on the remote, `update-ref -d refs/remotes/origin/<name>`
    (667) and success. Else minimal fetch `fetch origin --prune <refs>` (674, 364), then
    `push --force-with-lease=refs/heads/<name>:<expectedSha> origin :<name>` (686-688).
  - Without `expectedSha`: `push origin --delete <name>` (716-717). "Already deleted" output counts as success (721-725).
  - `force` is ignored for a remote delete.
- Local (`isRemote = false`): fails when the branch does not exist (739-743) or is the current branch (`branch --show-current`, 745-750).
  - `force = true`: `branch -D <name>` (754).
  - `force = false`: `merge-base --is-ancestor refs/heads/<name> HEAD` (767, 1556). If merged into HEAD: `branch -d <name>` (796).
    If not, but merged into `origin/<default>`: `branch -D <name>` (786). If neither: no delete, error
    "error: The branch '<name>' is not fully merged." (780-784).
- The handler passes no `skipHooks`, so the pre-push hook runs for a remote delete.

## 5. Part D - open facts

### F8: whitespace-only CheckedOutTag

Conclusion: no current writer can store a whitespace-only CheckedOutTag from Worker or user input. It can only exist
if an old row already holds one, in which case three paths copy it verbatim. PinnedTag has one unguarded writer.

| Writer | Normalization |
|---|---|
| Writer.ApplyIdentity :273-297 | blank -> null (:291); non-blank stored untrimmed (:280). Blank = IsNullOrWhiteSpace (:665) |
| Writer.MirrorIdentityToLink :303 (special Workspace only, :602-604) | copies the already normalized state value |
| Writer.StageTagRefresh :214-271 | set only when non-blank (:224-226), cleared to null (:249), mirror (:260) |
| Writer.ApplyTagRefreshAsync new state row :203 | verbatim copy of the link value |
| src/GrayMoon.App/Data/Migrations.Features.cs:364 (folder not re-checked) | verbatim copy of the link value into the state row |
| WorkspaceFeatureOperations.cs:2450, :2482 | verbatim copy of featureRow.PinnedTag (:2442); onTag = pinnedTag != null (:2443) |
| WorkspaceFeatureOperations.cs:283 (PinnedTag) | normalized: blank -> null, else Trim (:270-271) |
| BrOps:888 (PinnedTag, only rows where PinnedTag != null, :884-888) | NOT normalized. Value = tagCheckout?.CurrentTag ?? branchName.Trim() (BrOps:358); "??" does not catch an empty or whitespace CurrentTag |
| WorkspaceSyncOperations.cs:162 | in-memory only (links loaded AsNoTracking, WsRepo:33-43), never saved |
| WorkspaceRepositoryLink.cs:159, WorkspaceRepositoryLinkListMapper.cs:16 | in-memory objects, not persisted |

- Snapshot builders that feed the writer (so they are normalized by it): SyncCommandHandler.cs:216,
  WorkspaceGitService.Sync.cs:479, .ReturnToDefault.cs:183, .ResponseParsing.cs:116 and :169, GitBr:258 (null).
- Readers disagree on empty handling: IsOnTag uses IsNullOrWhiteSpace (WorkspaceRepositoryLink.cs:39), Restore uses
  != null && != "" (WorkspaceGitService.Restore.cs:154, :159), Feature seeding uses != null
  (WorkspaceFeatureOperations.cs:2443). A whitespace-only value would be treated differently by these.
- Not verified: whether the Worker CheckoutTag response can return an empty CurrentTag; raw SQL in the link column's own
  migration; whether the database holds legacy whitespace rows.

### F22: tag pin after checkout

Conclusion: F22 is a real bug by code reading. After a tag -> branch checkout in the special Workspace, the pin in
graymoon.json is NOT cleared. Confirmed by test in 0A and fixed since (see "F22 confirmed by test, then fixed" below; line numbers here are those of `1515d32b`).

- AppDbContext is one instance per DI scope (Reg:68-70). BrOps (Reg:176) and the state writer (Reg:146) are scoped,
  so they share it.
- CheckoutAsync takes wr from TryResolveLinkedRepoAsync (BrOps:330), loaded TRACKED from the scoped context
  (BrOps:1001-1002). The writer loads the same row tracked from the same context (Writer:107-108). EF identity
  resolution makes them the same object. The comment at BrOps:891-897 says the same.
- Branch path: Worker CheckoutBranch (BrOps:388), then stateWriter.ApplyAsync with IdentityProbed = true and no tag
  (BrOps:405-409). The writer sets state.CheckedOutTag = null (Writer:291) and mirrors it onto the link
  (Writer:303, :602-604).
- Only then does BrOps:412 test wr.CheckedOutTag. It is already null, so ClearRepositoryTagPinAsync (BrOps:412-418)
  is skipped.
- Exception: a blank localBranchName skips the state write (BrOps:402), and the clear would then run.
- Feature context: the link is not mirrored, but pins are only recorded for the special Workspace anyway
  (BrOps:974-985).
- Single Create Branch (BrOps:624-708) does no state write before its check (:691-697), so its clear DOES run.
- Workspace-wide checkout calls CheckoutAsync per repository in a fresh scope (BrHand:308-310): same bug.
- Bulk Create Branch never clears the pin. Chain: WorkspaceRepositories.Branches.cs:295 StartPageJob (body not read)
  -> BrHand.CreateBranchesWithOutcomeAsync (:49-72) -> WorkspaceGitService.CreateBranchesWithOutcomeAsync
  (GitBr:121-288) -> Worker CreateBranch (GitBr:209) -> stateWriter.ApplyAsync with CheckedOutTag = null
  (GitBr:254-273). The DB tag is cleared; the manifest is not touched. The only manifest pin callers are BrOps:946,
  BrOps:967 and WorkspaceGitService.Sync.cs:268 (grep).
- Side effect not verified: whether the next Sync rewrites or removes the stale pin
  (RecordCheckedOutTagPinsAsync, WorkspaceGitService.Sync.cs:242-271).

### WorkspaceRepositoryRole visibility

Conclusion: NOT visible to GrayMoon.Application.

- Defined in src/GrayMoon.App/Models/WorkspaceRepositoryRole.cs:1-8, namespace GrayMoon.App.Models,
  values Source = 0, Workspace = 1.
- src/GrayMoon.Application/GrayMoon.Application.csproj:11-14 references only GrayMoon.Abstractions and GrayMoon.Common.
- GrayMoon.App.csproj:15-17 references Abstractions, Application and Common, so Application cannot reference App.
- No match for the type name in Application, Abstractions, Common or Worker (grep).
- The policy core needs its own enum or bool, or the enum must move down. Precedent: WorkspaceFeatureBaseKindApplication
  mirrors the App enum (WorkspaceFeatureOperations.cs:47, :57, :230).


### F13 - InspectWorktree on a MAIN checkout (paths relative to `src/GrayMoon.Worker`)

**Answer: yes, it works.** Passing the main checkout path as both `mainRepositoryPath` and `worktreePath` gives a full,
correct answer. Verified by the new test class (8 tests, all pass) and by reading the code.

#### Request shape (Jobs/Requests/InspectWorktreeRequest.cs:10-37)

| JSON field | Type | Meaning |
|---|---|---|
| `mainRepositoryPath` | string? | ABSOLUTE path of the main checkout (or any worktree of the repo). Required. |
| `worktreePath` | string? | ABSOLUTE path of the folder to inspect. Required. |
| `defaultBranch` | string? | Short name ("main"); only used for `aheadOfDefault`. |
| `expectedBranch` | string? | Declared, never read by the handler or the service. |
| `featureBranch` | string? | Optional; fills the `featureBranch*` fields. |

It is NOT a `WorkspaceCommandRequest`: no workspaceRoot / workspaceName / repositoryName, no capabilities.

Validation:
- Commands/InspectWorktreeCommand.cs:13-14 - only null checks (`ArgumentException` "mainRepositoryPath required" / "worktreePath required").
- Services/GitWorktreeService.cs:612-615 - main path empty or not an existing directory -> result with `error = "Repository not found."`; empty worktree path -> `error = "worktreePath is required."`.
- Services/GitWorktreeService.cs:617-625 - `Path.GetFullPath(worktreePath)`; an exception becomes `error`.
- No `GitRepositoryPathValidator` and no containment check. The convention test exempts both fields on purpose:
  src/GrayMoon.Worker.Tests/Conventions/RequestPathValidationConventionTests.cs:30-31 ("Absolute path ...; read only.").
- Pool: read pool (Services/WorkerCommandPoolRouter.cs:20). It never fetches; every number is against local refs.

#### Response fields (Jobs/Response/InspectWorktreeResponse.cs:9-98)

`isRegistered` (bool), `exists` (bool), `isLocked` (bool), `lockReason`, `headSha`, `branch`, `isDirty` (bool?),
`stagedCount`, `unstagedCount`, `untrackedCount`, `conflictCount` (int?), `hasUpstream` (bool?), `aheadOfUpstream`,
`behindUpstream`, `aheadOfDefault` (int?), `error`, `featureBranchExists` (bool?), `featureBranchSha`,
`featureBranchAheadOfDefault`, `featureBranchHasUpstream`, `featureBranchAheadOfUpstream`.
There is no `success` field and no tag field.

#### How each value is computed (Services/GitWorktreeService.cs)

- `isRegistered` (627-632): path found in `git worktree list --porcelain` run in `mainRepositoryPath`. The main checkout is
  the first entry of that list, so it is `true` for a main checkout (asserted by the tests).
- `exists` (636-639): the folder exists. When it does not, every later field is null and `error` is null.
- `headSha` / `branch` (641-642): `branch` is `git branch --show-current` (Services/GitCliRepositoryReader.cs:62-73); empty output -> null.
- `isDirty` and counts (644-645, 821-863): `git --no-optional-locks status --porcelain=v1`. Dirty = staged + unstaged + untracked + conflicts > 0.
  Default untracked mode, so an untracked folder counts as ONE entry; ignored files do not count.
- `hasUpstream` / `aheadOfUpstream` / `behindUpstream` (647-656, 871-899): probed ONLY when `branch` is not empty.
  Upstream = `for-each-ref --format=%(upstream:short) refs/heads/<branch>` (GitCliRepositoryReader.cs:561-573). If it is empty, or the
  upstream ref does not exist locally -> `(false, null, null)`. Otherwise `rev-list --left-right --count <upstream>...HEAD`; if that
  command fails -> `(true, null, null)`.
- `aheadOfDefault` (666-668, 722-739, 901-922): `rev-list --count <ref>..HEAD` where ref is the recorded divergence base if one exists,
  else `origin/<defaultBranch>`; null when `defaultBranch` was not sent or the ref does not exist.
- `error` (706): only the git status failure text, or one of the early failures above. Null in every case below.

#### Results per case (all verified by tests on a main checkout cloned from a bare origin, `defaultBranch = "main"`)

| Case | branch | isDirty | hasUpstream | aheadOfUpstream | behindUpstream | aheadOfDefault | error |
|---|---|---|---|---|---|---|---|
| Clean, upstream set, not ahead | "main" | false | true | 0 | 0 | 0 | null |
| Dirty (1 modified, 1 staged new, 1 untracked) | "main" | true (staged 1, unstaged 1, untracked 1, conflicts 0) | true | 0 | 0 | 0 (not asserted) | null |
| One unpushed local commit | "main" | false | true | 1 | 0 | 1 | null |
| Local branch, no upstream, 1 commit | "local-only" | false | **false** | **null** | **null** | 1 | null |
| Detached HEAD (`checkout --detach`) | **null** | false | **null** | **null** | **null** | 0 | null |
| Tag checkout (`checkout v1.0.0`) | **null** | false | **null** | **null** | **null** | 0 | null |
| Detached HEAD + 1 local commit | null | (not asserted) | null | null | (not asserted) | 1 | null |
| Clean, no `defaultBranch` in request | "main" (not asserted) | (not asserted) | true | 0 | (not asserted) | **null** | null |

In all cases `isRegistered = true`, `exists = true`, `isLocked = false`.

Points a caller must handle (none of these is an error in the response):
1. "No upstream" is `hasUpstream = false` with NULL counts. An unpushed commit on such a branch is visible only through `aheadOfDefault`.
2. Detached HEAD and tag checkout are indistinguishable from each other and report `hasUpstream = null` (unknown), not false.
   The response carries no tag name; use `GetHeadCommits` / `GetBranches` (`GetCheckedOutTagAsync`) for that.
3. A commit made on a detached HEAD is visible only through `aheadOfDefault`, and only when `defaultBranch` is sent and
   `origin/<defaultBranch>` exists locally; otherwise it is null.
4. Nothing is fetched, so "not ahead" means "not ahead of the local remote-tracking ref".

No case behaved unexpectedly relative to the code reading; the tests passed on the first run.

#### New test

File: `C:\Workspace\GrayMoon\GrayMoon\src\GrayMoon.Worker.Tests\InspectWorktreeMainCheckoutTests.cs` (CRLF, ASCII, sealed class).
Setup: `GitScenarios.ClonedRepository(root, push: true)` per test; cleanup with `TestDirectory.Delete`.

Command:

    dotnet test src/GrayMoon.Worker.Tests/GrayMoon.Worker.Tests.csproj --filter "FullyQualifiedName~InspectWorktreeMainCheckoutTests"

Result: `Passed!  - Failed: 0, Passed: 8, Skipped: 0, Total: 8, Duration: 11 s`.

### F28 - where `codegraph init` runs, and can it change a Source repository

**Short answer: GrayMoon runs it only in a Feature root, never in a Source repository folder, and it does not leave a tracked or
untracked change inside a Source's working tree.** The only repository whose working tree can receive `.codegraph/` is the
Workspace-role repository (when the Workspace has one), whose Feature worktree IS the Feature root.

Verified from GrayMoon code:
- The only sender of `InitCodeGraph` is `src/GrayMoon.App/Services/Features/FeatureCodeGraphService.cs:54-59`;
  `targetRoot = pathResolver.GetContextRootAsync(featureContextId)`. Its only caller is
  `src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs:1328` (Create / Repair Feature).
- `GetContextRootAsync` for a Feature returns `{ManagedFeatureStorageRoot}\{FeatureName}` or `{storageRoot}\{WorkspaceName}\features\{FeatureName}`
  (`src/GrayMoon.App/Services/Features/WorkspaceContextPathResolver.cs:25-44`). Source worktrees are subfolders `{root}\{RepositoryName}` (same file, 102-103).
  A Workspace-role repository's working tree is the context root itself (same file, 67-82).
- Worker: `Commands/InitCodeGraphCommand.cs:17-35` passes `targetRoot` straight to `CodeGraphCli.TryStartInit`, which runs
  `codegraph init -y -v <root>` with `<root>` as working directory (`Services/CodeGraphCli.cs:147-148`). Nothing iterates Source repositories.
- Before init, `FeatureCodeGraphService.InitializeAsync` writes `codegraph.json` to the Workspace root and the Feature root
  (FeatureCodeGraphService.cs:50-52). Not verified: the exact write path inside `WriteConfigAsync` (I did not read it); the class
  comment and `CodeGraphConfigFile` say it is the Workspace repository's file.
- The managed `.gitignore` section of the Workspace repository gets `/.codegraph/` when the `codegraph` switch is true
  (`src/GrayMoon.App/Services/WorkspaceManifest/ManagedGitIgnoreSection.cs:16`; docs/architecture/03-data-model-and-persistence.md:340).

Verified from the installed CodeGraph CLI (version 1.6.2, `codegraph init --help`, and reading its bundled JS; `codegraph init` was NOT run):
- `init [path]` creates `<path>/.codegraph/` and writes `<path>/.codegraph/.gitignore` if absent (content: `*` then `!.gitignore`),
  then builds `codegraph.db` there (`lib/dist/directory.js` `createDirectory` / `ensureGitignore`, `lib/dist/bin/codegraph.js` `runInit`).
  It writes only under `<path>/.codegraph`. A user-authored `.gitignore` there is left alone.
- With `-y`, `offerWatchFallback` installs git sync hooks in `<path>`'s own hooks directory, but only when live file watching is
  disabled there (WSL `/mnt` drive or `CODEGRAPH_NO_WATCH`) and `<path>` is a git repository. On a normal Windows/Linux path it returns early.
  Not verified at runtime.

Verified with plain git in a throwaway scratch repository (deleted afterwards):
- A `.codegraph/` holding that `.gitignore` plus a database, INSIDE a repository that does not track it: `git status` shows exactly one
  untracked entry, `?? .codegraph/.gitignore` (the `!.gitignore` line un-ignores the file itself). This is what the CodeGraphCli.cs:14
  comment means by "committed": projects are expected to commit that one file (the GrayMoon repo itself tracks `.codegraph/.gitignore`).
- The same folder in the PARENT of a nested repository: the nested repository's `git status` is empty.

Inferred (not run end to end):
- Source repositories in a Feature are nested repositories below the Feature root, so their status is unaffected: no new tracked or
  untracked file. A reference (read-only) Source therefore needs no special handling for CodeGraph init.
- In a Workspace WITH a Workspace repository, the Feature root is that repository's worktree, so `?? .codegraph/.gitignore` would appear
  there unless `/.codegraph/` is ignored. It is ignored when the `codegraph` switch is true. When Create Feature starts the index from
  the New Feature checkbox while the switch is off or absent (`honorDefinitionSwitch: false`), the managed entry is not written, so the
  Workspace repository worktree would show that one untracked file. The Workspace repository is never read-only, so this is outside F28's concern.
- The comment at CodeGraphCli.cs:14 ("is committed, so the folder itself is present in every worktree") describes a repository that
  has committed the file; GrayMoon does not commit it anywhere.


### F22 confirmed by test, then fixed

`TagPinCharacterizationTests` runs the paths against a Workspace that has a Workspace repository (the condition for recording pins) with a recording `IWorkspaceManifestService`. The "0A" column is what the tests pinned at `1515d32b`; the last column is the behavior after the fix of 2026-11-12, which the same tests now assert.

| Path | Database tag afterwards | Pin change in 0A | Pin change after the fix |
|---|---|---|---|
| Tag checkout (`CheckoutAsync`, `isTag: true`) | set | one change, tag and commit | unchanged |
| Branch checkout from a tag (`CheckoutAsync`) | cleared | none - the pin stayed | one change, tag null |
| Workspace-wide checkout from a tag | cleared | none - the pin stayed | one change, tag null |
| Bulk Create Branch from a tag | cleared | none - the pin stayed | one change, tag null |
| Single Create Branch from a tag | still set (this path writes no state; the hook does) | one change, tag null | unchanged |

Before the fix, leaving a tag by three of the four paths left `graymoon.json` pinning the repository to the tag. A Sync does not remove a pin (`RecordCheckedOutTagPinsAsync` only records repositories that are on a tag) and a pin is applied when a repository is cloned (`SyncRepositoryCommand.cs:178`), so a fresh clone of the Workspace returned to the tag.

The fix reads the tag before the state write in `CheckoutAsync` and in `CreateBranchesWithOutcomeAsync`, and serializes pin changes per Workspace in `WorkspaceManifestService.SetRepositoryTagPinsAsync` (the Workspace-wide checkout runs repositories in parallel, each in its own scope). Also tested: a checkout from a branch sends no change; a refused checkout or branch creation keeps the pin; a pin that cannot be cleared is reported as the error of that repository; a Feature and a Workspace without a Workspace repository send nothing. `WorkspaceManifestTagPinConcurrencyTests` holds the first change inside its file read and proves the second waits; it fails when the gate is removed.

Not changed: a pin left behind by the old defect is not repaired, and leaving a tag outside the app does not clear the pin.

### Not mapped yet

Worker commands that write to a repository working tree or its Git directory and have no policy operation in the plan's operation list: `InitCodeGraph`, `UninitCodeGraph`, `UpdateFileVersions`, `ResolveGeneratedPackageReferences`, `DotnetRestore`. See the Worker command table in Part C for what each one writes. 0B must either give each an operation or record why it needs none (F28 answers `InitCodeGraph` for a Source repository: it does not write there).

## 6. Part E - characterization tests

All in `src/GrayMoon.App.Tests/Policy/Characterization/` (79 tests) plus `src/GrayMoon.Worker.Tests/InspectWorktreeMainCheckoutTests.cs` (8 tests). They call the real services over SQLite with a recording Worker bridge; nothing reaches the network or Git. A test named `..._has_no_service_side_tag_check_today` or `..._does_not_clear_the_pin_today` pins a gap or a defect: it is expected to be rewritten, on purpose, by the phase that closes it.

| Class | What it pins |
|---|---|
| `PolicyHarness` | Shared seed: a producer and a consumer repository, a Feature with its own state rows, a tag pin that is mirrored onto the special Workspace state row as production does. |
| `PushCharacterizationTests` | Push plan (Basic and .NET) leaves a tag-pinned repository out and ignores the role; a Feature reads its own state, not the Workspace link; single push is refused with "Repository is pinned to a tag. Checkout a branch before pushing." and sends nothing; needs-push counter; F7 for a Feature repository with no state row. |
| `UpdateCharacterizationTests` | Update plan, sync dependencies, single update (silent no-op), commit helpers and restore leave a tag-pinned repository out; **F6**: in a Feature, update and commit read the Workspace link while restore reads the Feature state; default-branch review ignores a tag-pinned repository. |
| `SyncCharacterizationTests` | Undo push skips a tag-pinned repository and reads the tag per context; F7 for undo push; return to default (analysis, execution, unattended, batch) reports a tag-pinned repository as not blocking and sends no `ReturnToDefaultBranch`. |
| `ServiceGapCharacterizationTests` | **F5 gaps**, each reaching the Worker for a tag-pinned repository: pull (single and level), bulk Create Branch, Prepare Workspace with no ids, Workspace-wide checkout, Git Changes commit, stage and unstage, Update Branch from Default; the six write actions of `IWorkspaceBranchOperations` (checkout, create, set upstream, delete local, delete remote, return to default); common branches is the one branch read that filters. |
| `TagPinCharacterizationTests` | F22, table above. After the fix it asserts the corrected behavior (13 tests). |
| `CounterCharacterizationTests` | The four header projections (special Workspace or Feature, with or without the dependency graph) for a branch, a tag, a never-pushed branch and a Workspace-role repository; F7 for the header; out-of-sync is not tag-filtered; notification and notification panel sets; the three known differences below. |
| `OperationLockCharacterizationTests` | A REST write is refused with 409 while a structural or a context operation runs in the same Workspace, and never reaches its operation. |
| `InspectWorktreeMainCheckoutTests` (Worker) | F13: `InspectWorktree` on a main checkout for clean, dirty, unpushed, never-pushed, detached and tag checkouts. |

### Known differences between counters (pinned, not changed)

1. The header recommends a push for a branch that was never pushed upstream (`BranchHasUpstream == false`); the notification and the notification panel count outgoing commits only.
2. `NotificationPanelRepoSets.NeedingUpdate` counts a repository whose only problem is an out-of-date version file; `UpdateOnDefaultBranch` counts unmatched dependencies only.
3. The header's creatable-PR counter does not require a verified PR state; `PRBadge.ShowsCreateBadge` does.

### Facts the tests turned up

- **`SyncStatus` insert sentinel.** `AppDbContext` maps `SyncStatus` with `HasDefaultValue(NeedsSync).HasSentinel(InSync)` on the link and on the context state. A row **inserted** as `InSync` is stored as `NeedsSync`; `InSync` can only be set by an update. Tests that need an in-sync row must update it after the insert. `SyncStateTestContext` seeds its link as `InSync` by insert, so that link is in fact `NeedsSync`.
- **Common branches** removes the shared default branch from its result, so two repositories with nothing but the default in common have no common branch.
- **Tag checkout needs the full commit hash** in the Worker response when the pin is recorded; without it the checkout is reported as failed after the Worker has already moved the checkout.

### Not covered in 0A

- Notification panel job bodies and the Files, Dependencies, Projects and Packages pages (lock audit, "not verified" in Part B).
- Page filters as such (see Part A, "Page filters").
- The manual smoke list (owner, at the gates).
