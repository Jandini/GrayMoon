# 05 - User Capability Reference

This document maps current user capabilities to the main implementation areas that own them.

It is intentionally practical. A developer should be able to find the responsible code from the user behavior.

---

## 1. Workspaces

### User capabilities

```text
list Workspaces
search
create (Add Workspace)
restore from a Workspace repository ("Restore from repository")
edit name/path
choose the Workspace profile
choose, change or remove the Workspace repository
set default
delete (also with Features, after an acknowledgement)
select repositories
import/use existing local repositories
```

### Primary implementation areas

```text
Workspaces page (Workspaces.razor), WorkspaceModal, RestoreWorkspaceModal
Shared RepositoryPicker
WorkspaceService
WorkspaceRepository
WorkspaceProfileTransition / WorkspaceProfileDefaults
IWorkspaceRepositoryOperations
Worker repository/filesystem commands
Workspace catalog/query services
```

### Persistence

```text
Workspace (Type, VersioningMode, CiProvider, ManifestDriftDetectedAt, PinnedAiProviderId, ...)
WorkspaceRepositoryLink
Repository
Settings/default root
```

### Workspace profile

The Workspace modal has a type radio (**Basic** / **.NET Dependency**) and two checkboxes, **Calculate versions with GitVersion** and **GitHub Actions**. Picking a type sets defaults (`WorkspaceProfileDefaults.ForType`: .NET Dependency turns both on, Basic turns both off); the user may override either. The three axes (`Type`, `VersioningMode`, `CiProvider`) are persisted on `Workspace`; behavior is derived from them through `WorkspaceCapabilities`, never from `Type` directly.

```text
Rules
  Features inherit the profile; there is no per-Feature profile
  the type cannot change while Features exist (versioning and CI can)
  Basic -> .NET Dependency: projects are discovered on the next Sync
  .NET Dependency -> Basic: projects, project dependencies, dependency levels and
    repository types are cleared (links and Feature context states)
  versioning off keeps GitVersion values and {@Repo} file configurations (treated as not applicable)
  CI off keeps Actions rows
  a profile change rewrites graymoon.json (SyncDefinitionToDiskAsync)
```

Page and action gating:

```text
Repositories, Changes, Files     always
Projects                         .NET Dependency (DiscoversDotNetProjects)
Packages                         .NET Dependency (UsesNuGetPackages)
Dependencies                     .NET Dependency (UsesDependencyGraph)
Actions                          CI provider set (UsesCiIntegration); follows CI, not the type
```

`WorkspacePageAccess` is the single policy for the left navigation and the pages. Direct navigation to an unavailable page renders `WorkspacePageGate` with "Projects/Packages/Dependencies are only available for .NET Dependency workspaces." or "CI is not enabled for this workspace." and a **Back to Repositories** link; it never redirects.

On the Repositories grid (`WorkspaceGridPresentation`), a Basic Workspace has no Version column when versioning is off, a flat list (no dependency level groups), no dependency badges, no Update / Push Updated / Level Only / Update & Push controls, no Restore in the Sync menu, a standalone **Update Files** button shown only while a version file is out of date, and **Merge PRs...** in the header Branch menu (a flat grid has no level-header menus).

### Workspace dialogs and the repository picker

Add Workspace and Restore Workspace use the shared `RepositoryPicker` (`Components/Shared/RepositoryPicker.razor`, `RepositoryPickerState`, `wwwroot/js/repository-picker.js`):

```text
one input that becomes the search box; in-memory, case-insensitive filter
Up / Down / Enter / NumpadEnter / Escape (Escape closes only the list)
list is position: fixed and placed from the input, so it never resizes the modal;
  it opens upward only when there is no room below
choices read "org/repository"; the connector name is added only when two choices collide
Fetch next to the picker runs RepositoryService.RefreshRepositoriesAsync (Workspaces.FetchRepositoriesAsync)
  under the page LoadingOverlay, GitHub request log in the overlay terminal;
  the selection follows a rename merge (MergedRepositoryIdMap) and clears when the repository is gone
Add Workspace offers "None" (the Workspace repository is optional); Restore has no "None"
```

When the Add Workspace folder already exists, the modal inspects it: a folder that is already a Git repository whose origin matches an imported repository auto-selects that repository as the Workspace repository, and nested repositories found on disk are offered for import (`TrySuggestImportsAsync`, matched by origin URL). Adopting an existing Workspace repository imports its `versionFiles` (`ImportVersionFilesAsync`) instead of rewriting `graymoon.json`.

### Deleting a Workspace

The Remove Workspace dialog removes only the GrayMoon Workspace; repositories on disk stay untouched. When the Workspace has Features, the dialog shows "Removing this workspace loses track of its Features. Folders on disk are left in place." and the delete button stays disabled until the acknowledgement checkbox is ticked. The delete drops tracking only (`WorkspaceRepository.DeleteAsync`, which clears `BaseWorkspaceFeatureId` first); no worktree, branch or folder is touched.

### Workspace repository

```text
User can
  choose a Workspace repository in the Workspace modal (enable, change or disable)
  restore a Workspace from a Workspace repository
  see it first on the Repositories page, under its own "Workspace" group header
  resolve a definition-drift banner with "Write Workspace definition to disk" or "Dismiss"

Rules
  at most one per Workspace; Role = Workspace on WorkspaceRepositoryLink
  working tree is the Workspace root folder; Sources are nested folders ignored by a managed .gitignore section
  enable and disable are refused while Features exist; disable never deletes files or .git
  enable and restore are refused unless the Worker is connected with the same GrayMoon version (App/Worker version lock)
  restore validates graymoon.json before creating anything and rolls back a Workspace whose restore fails
  takes part in the dependency graph (own projects, version files, custom dependencies); always the top level; edges into it are dropped
  Feature create: root worktree first, then Sources; remove and rollback: Sources first, root last
```

The grid header for the Workspace-role row is titled **Workspace** ("Workspace repository" is only the modal field label). In a flat (Basic) grid, a second **Repositories** header (`VirtualSlotKind.SourcesHeader`) precedes the Source rows when there is at least one; a level-grouped grid shows the level groups instead. Each of the two headers has its own menu with **Merge PRs...** over its own repository set.

Implementation areas:

```text
IWorkspaceRepositoryOperations / WorkspaceRepositoryOperations (enable, disable, restore)
IWorkspaceManifestService / WorkspaceManifestService (graymoon.json, managed .gitignore, drift, version files)
IRemoteWorkspaceManifestReader / GitHubRemoteWorkspaceManifestReader (read-only graymoon.json preflight)
RestoreDefinitionEvaluator (definition validation and local resolution, shared by preflight and post-clone check)
WorkerVersionPolicy + WorkerBridge (version lock: only SelfUpdate and GetHostInfo run on a version mismatch)
Worker: AttachWorkspaceRepository, DiscardWorkspaceRoot, WriteRepositoryFile, WorkerRepositoryPaths.Resolve
WorkspaceFeatureOperations (two-phase Feature create, Sources-first removal)
```

**Enable.** The Worker command `AttachWorkspaceRepository` attaches the repository to the Workspace root:

```text
root has .git with a matching origin    adopt (AdoptedExisting); finishes the default-branch checkout if HEAD is unborn
root has .git with another origin       refused: "Root already has a different Git repository"
root is empty or missing                clone into it
root not empty, no .git                 git init, add origin, fetch tags, check out the tracking default branch
                                        (a file collision is returned verbatim and .git is kept for a retry),
                                        or an unborn main for an empty remote
then                                    safe.directory, long paths, hooks
```

Only a fresh attach (not an adopt) then writes the managed `.gitignore` section and `graymoon.json` through `WriteRepositoryFile`. An adopted folder keeps its `graymoon.json`, because rewriting it from the database would drop version files and repositories not imported yet. On a fresh attach a failed `.gitignore` or definition write fails the operation with a message but keeps the link and the attached repository; the drift banner repairs it. Enable then scans Git Changes for the repository so the Changes dot updates. Worker commands carry `WorkspaceRepositoryName` so `WorkerRepositoryPaths.Resolve` maps the Workspace repository to the Workspace root and every other repository to a nested folder.

**Restore** (Workspaces page "Restore from repository", dialog title "Restore Workspace") is two steps.

`PreflightRestoreAsync` reads `graymoon.json` from the repository's default branch through the GitHub contents API (no Worker, no clone, no database change), validates it (a missing file, invalid JSON, a newer schema and unknown profile values are blocking errors) and previews the profile, the Source repository count and the connectors and repositories missing on this computer. Restore is enabled only when the preflight is valid and the name, the folder and the Worker are all OK.

`RestoreFromRepositoryAsync`:

```text
1  repeat the definition check; refuse a non-empty destination folder; refuse when the Worker is unavailable
2  create the Workspace row as Basic / None / None          (from here on a failure rolls back)
3  clone the repository into the empty root (requireEmptyRoot)
4  re-read and re-validate the cloned graymoon.json (the remote may have moved since the preflight)
5  apply the profile (WorkspaceProfileTransition)
6  link every Source repository that resolves by normalized URL (one batch)
7  merge versionFiles into the database (file wins; entries of repositories not here are skipped)
8  write the managed .gitignore                                (failure = warning, no rollback)
9  rewrite graymoon.json only when required                    (failure = warning, no rollback)
10 the caller starts the initial Sync
```

Rollback: an exception, a failed step or a cancellation (`OperationCanceledException`) in steps 3-7 calls `RollbackRestoreAsync`: the Worker command `DiscardWorkspaceRoot` deletes the root only when it is empty or a clean clone of the restored repository (a folder that existed, empty, before the restore is emptied but kept), then the Workspace row and links are deleted. Anything left behind is reported as `CleanupResidue`.

Warnings: steps 8 and 9 run after the Workspace is usable, so a failure there is a warning on a successful restore ("Workspace restored, but .gitignore could not be updated: ..." or "Workspace restored, but the Workspace definition could not be written: ..."); the drift banner repairs it.

The definition is rewritten only when nothing is missing on this computer (repositories, connectors, version files) and its canonical form differs from the file (for example duplicates, or the Workspace repository listed as its own Source). A different Workspace name alone never triggers a rewrite. Missing Source repositories and connectors never block a valid definition; they are reported as "owner/name" and host names, and drift reports them afterwards. "Write Workspace definition to disk" would drop them, so import them first.

Dialog result: a clean success opens the Workspace directly. The result panel appears only when something needs action (missing items, a warning, or "Workspace restored, but the initial Sync did not complete."). The Update Worker button opens the Worker page.

**Version lock.** App/Worker compatibility is the version lock, not a per-feature check: `WorkerConnectionTracker` compares the Worker's product SemVer with the App's (build metadata ignored, see `WorkerVersionPolicy`), and `WorkerBridge` refuses every command except `SelfUpdate` and `GetHostInfo` while the state is `VersionMismatch`. The Worker update therefore works against any Worker version, and the global Worker badge and Worker page direct the user to update (section 3).

The Git Changes watcher ignores events under nested repository folders, and Workspace file search skips nested repositories, so the root's view does not include Source repositories.

---

## 2. Connectors

### User capabilities

```text
configure GitHub source connector
configure package/registry connectors
validate connection
store credentials securely
use connector for repository/GitHub/package operations
```

A connector cannot be deleted while a Feature uses its repositories ("Remove the Features that use this connector's repositories first."), and a connector refresh keeps repositories that Features use.

### Implementation areas

```text
Connector models/repository
connector services/factory
ConnectorHealthService
token protection
GitHubRepositoryService
package registry services
```

---

## 3. Worker management

### User capabilities

```text
see Worker presence/status (global badge, Worker page)
install Worker (copy the install command, Download, or one-click Install from Desktop)
pair a manually installed Worker (one-time pairing code, section 33)
update a Worker whose version does not match the App ("Update Now")
inspect host prerequisites (Host tab)
run as service
cancel commands where supported
```

### Worker page

```text
Install tab       labelled "Update" and shown red while the state is VersionMismatch (also red while Offline);
                  shows the PowerShell install command (served by GET /api/worker/install),
                  the .NET 10 runtime note, the "Reinstall the Worker to finish securing GrayMoon" notice,
                  "Show pairing code", and a stale Windows service password notice
                  footer: "Update Now" on a mismatch (sends SelfUpdate), "Install" in Desktop mode, "Download"
Uninstall tab
Host tab          only while Online or VersionMismatch; Worker version, .NET SDK, Git, GitVersion,
                  CodeGraph (codeGraphVersion, optional, informational only),
                  Claude (claudeVersion; listed with its install command, never required),
                  Codex (codexVersion; listed with its install command, never required),
                  Cursor Agent (cursorAgentVersion; listed with its install command, never required)
                  footer: "Install All" for missing prerequisites in Desktop mode
```

`GetHostInfo` (one of the two commands allowed on a version mismatch) supplies the host versions and which AI CLIs are installed. Required prerequisites follow the Workspace profiles (`HostPrerequisiteRequirements.For`): Git always, GitVersion when any Workspace uses it, the .NET SDK when any Workspace uses GitVersion, package restore or dependency-aware update. Missing optional tools are labelled "(optional)" and never raise the attention badge; Claude, Codex and Cursor Agent count as missing but are never required.

### Install script guards

`App/Resources/install-worker.ps1` is shared by the manual command, the Desktop elevated install and `SelfUpdate` (`irm .../api/worker/install | iex`), so its guards cover all three:

```text
machine-wide mutex Global\GrayMoonWorkerInstall, held from before the service stops until the script ends;
  an abandoned mutex counts as acquired; released on every exit; a second install waits, then does its own full install
unique download zip %TEMP%\graymoon-worker-windows-install-<guid>.zip
after Expand-Archive every archive entry must exist on disk; otherwise the script fails
  before "graymoon-worker.exe install" (a partial install missing git2-*.dll breaks every
  LibGit2Sharp call until the Worker restarts)
```

The guards are pinned by `GrayMoon.App.Tests`. The App tracks a running self-update (`WorkerConnectionTracker.IsSelfUpdateInProgress`, `BeginSelfUpdate` / `EndSelfUpdate` in `WorkerBridge`); it ends only when a Worker reports the same product version.

### Worker CLI

```text
run
install
uninstall
start
stop
```

### Implementation areas

```text
Worker.razor
WorkerHub
WorkerConnectionTracker / WorkerVersionPolicy
WorkerPairingService
HostPrerequisiteRequirements / HostPrerequisiteState
install-worker.ps1
Worker CLI
SignalRConnectionHostedService
Worker queue state services
Desktop: DesktopNotificationHub.UpgradeWorker (SelfUpdate), WorkerInstaller
```

---

## 4. Repositories page

### User sees

Per repository:

```text
name
GitVersion (when versioning is on)
branch/tag
divergence
PR
dependency status (.NET Dependency only)
incoming/outgoing state
sync/status, including "not cloned" / "error" with a short reason
Actions-related status (CI on)
"off feature branch" badge (Feature context)
```

The Workspace-role repository is pinned first under a **Workspace** header. Source repositories are grouped by dependency level in a .NET Dependency Workspace, or listed flat under a **Repositories** header otherwise (section 1).

### User can initiate

```text
sync/fetch
branch operations
tag operations
update branch from default
dependency updates (.NET Dependency)
push
pull/sync commits
Undo Push
custom dependencies
configured-file dependency diagnostics
create PR
merge PR, bulk Merge PRs...
restore packages (.NET Dependency)
Prepare Workspace
Return to Default
Authorize SSO (after an SSO clone failure)
```

### Implementation areas

```text
WorkspaceRepositories.razor + partials
WorkspaceRepositoriesHeader / WorkspaceRepositoriesLevelHeader / WorkspaceRepositoriesRow
WorkspaceGridPresentation
IWorkspaceRepositoryLinkListQueryService
WorkspaceGitService
Workspace*Operations
WorkspaceProjectRepository
WorkspacePushService
WorkspacePullRequestService
WorkspaceStateRecomputeScope
BackgroundJobService
```

---

## 5. Synchronize repository / Workspace

### User goal

Refresh GrayMoon so the persisted grid reflects the real local Git state.

### Worker work

May include:

```text
clone/validate repository
fetch
GitVersion (only when the profile uses it)
branch/default branch
commit counts
tags/refs
project discovery (.NET Dependency only)
hook installation
```

### App work

```text
persist repository snapshot
merge project state
recompute dependencies
refresh PR as needed
import versionFiles from graymoon.json when the Sync includes the Workspace repository (special context)
detect definition drift when the Sync includes the Workspace repository
broadcast Workspace sync
```

### Clone failures and GitHub SSO

A failed clone stays failed: the row shows **not cloned** (or **error** when the repository was already cloned and the fetch failed) with a short sanitized reason, never InSync. A folder created by the failed clone that holds only `.git` (or nothing) is deleted; other content is left and the message says to move it aside.

When GitHub rejects the token for SAML SSO (`GitHubSsoAuthorization`, `GitRemoteFailureKind.ssoAuthorizationRequired`, detected only from explicit markers and winning over a generic 403), the row shows "GitHub SSO authorization required" with an explanation naming the OAuth app and organization, and an **Authorize SSO** link:

```text
shown only for an exact https://github.com URL on the default port with path /orgs/{slug}/sso or
  /enterprises/{slug}/sso (TryValidateAuthorizationUrl, re-validated in RepositorySsoPresentation)
target=_blank rel="noopener noreferrer" referrerpolicy="no-referrer"
available on clone and clone-into only, not on fetch
retry = the normal per-repository Sync; GrayMoon never opens the browser, polls or preflights the GitHub API
SSO failures are never retried automatically (GitRetryClassifier)
authorization_request is redacted in logs and the overlay terminal
the URL is never persisted: the sentence lasts for the page session and is dropped on reload or
  context switch; the not-cloned / error status persists
one token across N repositories gives N independent rows; one authorization covers the organization
```

---

## 6. Branch menu

Branch menu (header split button, `WorkspaceRepositoriesHeader.razor`) in the special Workspace context:

```text
Prepare Workspace
New Branch
Switch Branch
Create PR
Merge PRs...        (flat grid only; a level-grouped grid has it in each level-header menu)
Return to Default
```

The menu controls the Workspace's main checkouts.

In a Feature context the primary button reads **Feature** and the menu offers:

```text
Create PR
Merge PRs...
Remove Feature
```

The primary button follows `DeterminePrimaryAction`: **Create PR** when at least one repository has a creatable PR; otherwise **Branch** in the Workspace; in a Feature **Remove** only when `AllFeaturePrsCompleted` (at least one PR, none open, nothing creatable), else **Feature**. A fresh Feature with no commits and no PR shows **Feature**. The level header hides Return to Default in a Feature context, and Return to Default is refused service-side in a Feature ("Return to Default is a Workspace action.").

### New Branch

Creates the selected branch across repositories. Not offered in a Feature.

### Switch Branch

Computes compatible/common branches and performs coordinated checkout. Not offered as a bulk action in a Feature.

### Create PR

Starts multi-repository PR creation (section 24). In a Feature context, a repository is creatable when its worktree HEAD differs from the Feature's base commit, and the PR targets the Feature's parent branch.

### Merge PRs...

Opens `BulkMergePullRequestsModal` over the scope (a level, the Workspace or Repositories header set, or all repositories). Candidates are resolved from the persisted PR table, not from the virtualized render cache. The "Return to default branch after merging" checkbox shares its remembered value with the single-PR dialog and is hidden in a Feature. Returning after a bulk merge uses `ReturnToDefaultUnattendedBatchAsync`: ids deduped, branches refreshed in parallel, one PR refresh, eligible repositories returned in parallel, one recompute only when at least one returned.

### Return to Default

Runs shared preflight and cleanup execution (section 14).

---

## 7. Per-repository branch/tag controls

The user can:

```text
inspect branches/tags
refresh refs
checkout branch
checkout tag
set upstream
delete branch where supported
update branch from default
```

Tagged repositories are intentionally protected from mutating workflows.

Switch Branch marks branches held elsewhere (`WorkspaceBranchOccupancyService`, `BranchOccupancyKind`): **Current**, **Feature** (checked out by a Feature), **Workspace** (held by the special Workspace checkout), **Worktree** (an external worktree). Checkout is allowed only for unoccupied or current branches; an ordinary delete only for unoccupied ones. Deleting a Feature-held branch opens Remove Feature; deleting an external-worktree branch runs `AnalyzeExternalWorktreeCleanupAsync` with an inline confirmation (a dirty worktree needs a second confirmation) and `RemoveExternalWorktreeAsync`. Delete errors show the server's error text. The Tags-tab entry point passes the repository id so badges appear there too.

### Feature branch guard

A Feature keeps every repository on its Feature branch (the Feature name), or detached at its tag when the repository is tag-pinned. `FeatureBranchPolicy` / `IFeatureBranchGuard` enforce this service-side for the dialog, REST and bulk paths:

```text
create branch               refused ("A Feature keeps every repository on its Feature branch...")
checkout another branch     refused; remote branches too
checkout a tag              only in tag-pinned repositories (moves the pin)
tag-pinned repo -> branch   refused ("This repository is pinned to a tag in this Feature...")
Return to Default           refused ("Return to Default is a Workspace action.")
fetch, delete, set upstream, Update Branch from Default    always allowed
```

In a Feature, Switch Branch hides New Branch, shows the own branch as **This Feature** (checkable), shows **Return to Feature branch** when the repository has drifted, and warns that a delete affects a branch shared by the Workspace and all Features. A repository that is off its Feature branch (for example after a terminal checkout) shows an **off feature branch** badge on its row; clicking it opens Switch Branch. Drift is a warning only, never NeedsRepair.

Implementation:

```text
IWorkspaceBranchOperations
WorkspaceBranchOperations
WorkspaceGitService.Branches
RepositoryBranchWriter
WorkspaceBranchOccupancyService
FeatureBranchPolicy / IFeatureBranchGuard
SwitchBranchModal
Worker branch commands
```

---

## 8. Prepare Workspace

### User input

```text
branch name
based on (default branches, or a branch common to every repository)
skip repos on tags (shown only when a repository is on a tag)
update dependencies
push changes (enabled only with "Update dependencies")
commit message options
```

### Flow

```text
1 create branches in parallel with inline state sync, hooks suppressed
  (WorkspaceBranchHandler.CreateBranchesWithOutcomeAsync -> BranchCreationOutcome)
2 branch gate: every targeted repository must be provably on the new branch
3 then one of: branches only | update only | update and push as two lanes
```

The branch gate (`PrepareWorkspaceOrchestrator.GetBranchFailures`) counts as a failure a reported error, a requested repository that was never targeted, an attempt with no result, and a success whose checked-out branch is unknown or different. Any failure, or zero targeted repositories, stops the workflow with "Prepare Workspace stopped: branches could not be created in all selected repositories. No dependency updates or pushes were started." (`PrepareWorkspaceResult.BranchesStoppedMessage`), or "Prepare Workspace stopped: no repositories were selected."; nothing is updated, committed or pushed, and branches already created are left in place.

Update and push runs as the two-lane pipeline (`UpdateAndPushOrchestrator`, section 11). When the push lane cannot run (`CanRunPushLaneAsync` finds a required package without a registry mapping) it returns `NotPipelined` without changing anything; the orchestrator then updates and hands the push back (`PushPending`), and the page pushes with its own checks and the "Continue with normal push?" confirmation. `PrepareWorkspaceResult` carries `BranchesCreated`, `BranchErrors`, `Update`, `Pipeline` and `PushPending`. REST `/prepare-workspace` returns the same stop message.

### Implementation

```text
PrepareWorkspaceModal
WorkspaceRepositories.PrepareWorkspace
IWorkspacePreparationOperations
PrepareWorkspaceOrchestrator
IPrepareWorkspacePhases / PrepareWorkspacePhases
UpdateAndPushOrchestrator
IWorkspacePushOperations
```

---

## 9. Dependency update

Available only when the profile has dependency-aware update (.NET Dependency). A Workspace without it updates version files only, through **Update Files**.

### User can

```text
update all pending repositories (Update)
update one repository
update by dependency level (Level N Only)
update version files only (Update Files)
Update & Push... (two lanes)
choose commit-message behavior
optionally continue to push
```

While dependencies are unmatched and nothing is incoming, the header shows a yellow **Push Updated** split button instead of Update and Push (`DetermineUpdateControl`; menu: Level N Only, Update Only, Update Files, Push Only, and Undo Push Commits when a push is recommended).

### Internal plan

Based on:

```text
project dependencies
current GitVersion values
configured-file tokens
custom dependencies
generated packages
tag/write eligibility
```

In a Feature context, repositories whose Feature PR is merged get their GitVersion from the default-branch tip before the update ("versions from default", section 32).

### Implementation

```text
IWorkspaceUpdateOperations
DependencyUpdateOrchestrator
UpdateAndPushOrchestrator
WorkspaceProjectRepository
WorkspaceFileVersionService
Worker file/project/restore commands
```

---

## 10. Restore packages

### User can

Restore package state from the Workspace or dependency-level workflows (.NET Dependency only; restore methods do nothing without `UsesPackageRestore`).

### Internal behavior

Uses discovered project paths and calls `dotnet restore` through the Worker.

Restore also runs in `PushOrchestrator` after each level's push. `DependencyUpdateOrchestrator` never restores.

---

## 11. Push

### User can

```text
push one repository
push pending repositories
run synchronized push
run Push Updated workflow
run Update & Push (two lanes)
```

### Synchronized push

Uses dependency levels and package registry waiting (.NET Dependency). A Basic Workspace pushes in parallel with no levels, registry wait or restore (`WorkspacePushStrategySelector` -> `BasicGitPushStrategy` / `DotNetDependencyPushStrategy`).

### Update and Push (two lanes)

`UpdateAndPushOrchestrator` runs the update lane (`DependencyUpdateOrchestrator`, raising `onLevelCompleted` after every level, including levels with nothing to update) and the push lane (`WorkspacePushService.RunPushLaneAsync`, own DI scope and DbContext) connected by a single-reader channel, so level N is pushed while level N+1 is being updated and a level's CI/package wait overlaps later updates. `TwoLaneProgress` shows whichever lane reported last in the normal overlay. When `CanRunPushLaneAsync` fails nothing runs and the page falls back to the sequential update-then-push with its "Continue with normal push?" prompt. On an update exception the channel is completed, the push lane settles, and the exception is rethrown.

### Implementation

```text
IWorkspacePushOperations
WorkspacePushOperations
WorkspacePushService
PushOrchestrator
UpdateAndPushOrchestrator
package registry services
Worker Git push command
```

---

## 12. Pull / synchronize commits

### User signal

Incoming/outgoing badges and sync actions.

### Behavior

Pull/sync logic avoids simply pushing when incoming commits first require reconciliation.

Implementation:

```text
IWorkspaceSyncOperations
WorkspaceCommitSyncHandler
Worker commit-sync command
```

---

## 13. Undo Push

### User can

Revert outgoing commits while choosing whether to keep changes.

### Implementation

```text
WorkspaceRepositories.UndoPush
IWorkspaceSyncOperations.UndoPushAsync
WorkspaceUndoPushHandler
Worker undo/reset command path
```

---

## 14. Return to Default

Workspace context only; refused in a Feature.

### Analysis

`AnalyzeReturnToDefaultAsync` returns:

```text
ReturnToDefaultPlan
ReturnToDefaultRepositoryPlan
```

It refreshes enough state to determine:

```text
already on default
tag checkout
ahead of default
upstream
PR merged/closed/open
blocking reason
whether explicit discard confirmation is needed
```

### Execution

`ExecuteReturnToDefaultAsync` receives explicit `ReturnToDefaultOptions`.

### UI policy

Single-repository, level, and all-repositories flows render the appropriate existing GrayMoon confirmation UI.

### Unattended policy

REST/post-merge flows call the same analysis first and abort safely when analysis/PR refresh is insufficient. The unattended policy deletes the remote branch only when there is an upstream, force-deletes the local branch and never closes PRs. Bulk merge uses the batch form (section 6); single-repository callers keep abort-all behavior (`ReturnToDefaultUnattendedAsync`).

---

## 15. Projects page

Available only for .NET Dependency Workspaces (section 1).

### User sees

Discovered Workspace projects with:

```text
name
type
framework
file
```

### Implementation

```text
WorkspaceProjects page
IWorkspaceProjectListQueryService
WorkspaceProjectRepository
Worker project discovery
```

---

## 16. Packages page

Available only for .NET Dependency Workspaces (section 1).

### User sees

Package-producing projects and matched registry.

### User can

Synchronize registry matching.

### Implementation

```text
WorkspacePackages page
WorkspaceProjectRepository package queries
connector/package-registry services
```

---

## 17. Dependencies page

Available only for .NET Dependency Workspaces (section 1).

### User sees

Repository dependency graph.

### Sources

```text
PackageReference
configured file tokens
generated packages
custom dependencies
```

### Implementation

```text
WorkspaceDependencies page
WorkspaceProjectRepository
dependency graph calculation
repository dependency-level projection
```

---

## 18. Custom dependencies

### User can

Add or remove manual repository dependency edges.

### UI rules

```text
implicit edges shown locked
circular choices prevented
custom edges editable
```

### Persistence

`WorkspaceRepositoryCustomDependencies`

---

## 19. Files page

Available in every profile.

### User can

```text
search configured files
find/add files from repositories
view content
remove configuration
configure version patterns
update versions
```

Tokens `{@Repo:branch}` and `{@Repo:commit}` always resolve. `{@Repo}` (the GitVersion version) needs repository versioning: the dialog rejects it with "requires repository versioning (GitVersion) to be enabled for this workspace", and existing `{@Repo}` lines are skipped while versioning is off.

### Version files in graymoon.json

Version file configurations are stored in `graymoon.json` as `versionFiles`. After a configuration save, a configured file's removal and Update, the page calls `WriteVersionFilesAsync`, which replaces only `versionFiles` (entries of repositories not linked here are preserved). They come back into the database (file wins; entries of repositories not linked here are skipped) when:

```text
a Sync includes the Workspace repository in the special context (ImportVersionFilesFromDefinitionAsync)
an existing Workspace folder is adopted in Add Workspace (ImportVersionFilesAsync)
a Workspace is restored (MergeVersionFilesIntoDatabaseAsync)
```

Drift detection ignores them.

### Implementation

```text
WorkspaceFiles
IWorkspaceFileOperations
WorkspaceFileSearchService
WorkspaceFileVersionService
WorkspaceFileRepository
WorkspaceFileVersionConfigRepository
WorkspaceManifestService (WriteVersionFilesAsync, version-file import/merge)
Worker SearchFiles / file commands
```

---

## 20. Version-file dependencies

Configured version tokens do two jobs:

1. validate/update file values;
2. create dependency relationships between repositories.

Mismatch state contributes to repository dependency badges.

---

## 21. Git Changes page

### Navigation

`/workspaces/{WorkspaceId}/changes`

### User can

```text
view all changed repositories/files
filter
expand/collapse tree
multi-select
view diff
navigate previous/next diff
stage
unstage
discard
commit with the adaptive Commit button (Commit Staged / Commit All)
generate a commit message with an AI provider (section 34)
optionally Push committed / Generate commit message on commit (Commit menu, "On Commit")
copy path
resize splitter
use Ctrl/Cmd+Enter commit shortcut
```

### Commit control

There is one primary Commit button: **Commit Staged** when any repository has staged content, otherwise **Commit All** (`WorkspaceCommitModeHelper.ResolveOfferedMode`). The keyed alternatives carry the captured mode, so a watcher refresh that flips the label mid-click cannot commit the wrong set. The button is joined to an AI provider segment and a chevron menu (`[Commit Staged|All][provider][chevron]`). The menu's **On Commit** header holds **Generate commit message** and **Push committed**, both remembered per Workspace across a reload (F5 or Ctrl+F5) until the App restarts. **Generate commit message** starts ticked until you change it; **Push committed** starts unticked. Ctrl/Cmd+Enter targets only the primary commit element, never the AI segment or the chevron. While the message box is empty the control attaches to the right edge of the box; below about 20rem it stays under the box.

### Directory rows

`git status --untracked-files=all` reports a nested, non-ignored Git repository as one untracked entry ending in `/`. `GitChangesTreeBuilder` decides the node kind from the entry itself (`IsDirectoryPath`), never from whether the node has children. Such an entry renders as a directory row (`IsDirectoryEntry`): folder icon, "untracked folder" hint, no status letter, no diff, no Stage or Undo; Copy path still works. When a parent folder has the same name, the folder row wins. Staging a parent folder still stages a nested repository inside it, as git does.

### Diff states

The UI handles cases such as:

```text
normal text
new/deleted/renamed
binary
large file
unavailable/unsupported diff
```

### Implementation

```text
WorkspaceGitChanges.razor + partials (WorkspaceGitChanges.Ai.cs for generation)
GitChangesTree / GitChangesTreeBuilder
WorkspaceCommitModeHelper
IWorkspaceGitChangesOperations
GitChangesWorkerClient
WorkspaceGitChangesWriteQueue
GitChangesSnapshotPushHandler
Worker Git Changes commands
Monaco integration
```

---

## 22. Git Changes watcher behavior

Opening relevant Workspace pages activates watcher leases.

File edits from IDEs trigger Worker-side filesystem events.

Events are debounced and result in new snapshots.

Watchers eventually expire after inactivity.

Explicit refresh requests a real status rescan.

---

## 23. Pull request column

### States

Repository grid can display:

```text
none
create
open PR number
merged
closed/other state
mergeability/check-related styling
```

### Persistence

`WorkspaceRepositoryPullRequests` (special Workspace) and `WorkspaceRepositoryContextPullRequests` (per context).

### Refresh

Driven by branch/current state and GitHub API refresh. PR state is looked up by head branch name, so a PR created or merged from any machine or tool is found.

---

## 24. Create Pull Requests

### User can

Create PRs for:

```text
single repository
dependency level
Workspace set
Feature
```

### Dialog

`NewPullRequestModal` uses one layout for one or many repositories: tabs **Details** (title, description, **Draft pull request**), **Branch** (one row per repository: head branch and target picker, plus a **Refresh** link) and **Review** (optional reviewers with a filter), with **Create** and **Push & Create**. The AI provider selector sits at the right of the tab bar (section 34) and writes one title and one Markdown description shared by every PR in the dialog.

### Loading behavior

```text
first paint      one bulk read, IWorkspaceBranchOperations.GetBranchesForRepositoriesAsync: one fresh DbContext,
                 a fixed number of queries whatever the repository count; no Worker, no remote, no persistence,
                 no WorkspaceSynced (guarded by an N+1 test)
default target   NewPullRequestTargetBranch.Resolve: the Feature parent branch if it still exists remotely,
                 else the default branch, else another remote branch; never the head branch
background       repositories with no persisted remote branches are refreshed in the background
Refresh link     fetches every target's branches (RefreshBranches, one WorkspaceSynced per repository)
                 and keeps selections that are still valid
reviewers        loaded only when the Review tab is first opened; PullRequestService.GetReviewerCandidatesAsync
                 asks each distinct GitHub repository once and caches for 5 minutes (failures not cached)
```

---

## 25. Merge Pull Requests

### User sees

```text
checks
reviews
mergeability
conflicts
local working state
unpushed/incoming state
changed files
merge method
```

### User can

```text
merge
close PR
optionally Return to Default (hidden in a Feature)
navigate to repository Actions
bulk merge through Merge PRs... (section 6)
```

PR check runs in the merge dialog come from source control, independent of the Workspace CI setting.

---

## 26. Actions page

Available only when the Workspace has a CI provider (GitHub Actions), independent of the Workspace type (section 1).

### User can

```text
search repository/workflow
filter status
refresh
start workflow
rerun
rerun failed
abort/cancel
bulk run/re-run
open logs
```

### Status categories

```text
error
failed
running
aborted
success
none
```

### Polling

Adaptive by page/browser activity.

Repository hook sync can trigger targeted refresh.

---

## 27. Pending action notifications

GrayMoon derives Workspace notifications from state such as:

```text
unmatched dependencies
out-of-date version files
outgoing commits
new branch without upstream
incoming commits
```

The service can identify the lowest dependency level that needs work.

---

## 28. Loading overlays and terminal output

Long-running jobs display progress through the GrayMoon loading overlay.

Worker command output can be streamed into a terminal-style area.

Operations remain process-wide even if the initiating page is disposed.

---

## 29. Search/filter language

Workspace grids use the shared filter-expression parser.

Capabilities include:

```text
words
implicit AND
explicit AND
OR
parentheses
field filters such as repo:
```

Pages may layer domain-specific predicates over the shared parser.

---

## 30. Desktop integration

When running in Desktop mode, GrayMoon may provide native actions such as opening a repository folder or external URL.

The context selector also shows an **Open in...** flyout for the selected context: Cursor, Claude CLI, Codex CLI, VS Code and Visual Studio (each only when Desktop detects it), Terminal and Explorer, with a per-repository sub-flyout. Each tool can be added to or removed from the button row next to the selector (persisted per Workspace); using a tool does not add it. The App resolves the paths (`IWorkspaceNativeLaunchService`); Desktop only launches the tool.

The web/container application remains fully functional without Desktop.

---

## 31. Current intentional limitations

There is no MCP server.

Feature limitations in the current code:

```text
Features can only be based on the current Workspace (no "default branches" or "from another Feature" base)
every Workspace repository always gets a worktree (no repository picker)
one branch per repository per Feature (the Feature name); no adopting an existing branch
Workspace repository membership, rename and root change are blocked while Features exist
REST endpoints always act on the special Workspace context; there are no Feature endpoints
```

Those are future architecture projects and must not be described as current product behavior in this documentation.

### Known gaps

```text
"Versions from default" (section 32) runs GitVersion with /c at the origin/<default> tip, which GitVersion
  ignores for a local repository, so it returns the Feature branch version (GitVersionParityTests, skipped)
bulk New Branch / Switch Branch (BranchModal) has no worktree-occupancy awareness; a bulk switch can hit
  "already checked out" for a branch held by a Feature
the context selector has no type-to-filter
the drift banner offers only Write / Dismiss (no diff review)
the Workspace repository must be an imported GitHub repository (no local-only Workspace repository)
Restore does not import unknown repositories; missing ones are only reported
```

---

## 32. Features (worktree contexts)

### User sees

The context selector at the top of every Workspace page lists **Workspace** plus every Feature with a state label (`FeatureSelectorPresentation`):

```text
Ready          no label; selectable
Creating       "Setting up..."; not selectable, still removable (escape hatch for a stuck create)
Removing       "Removing..."; not selectable, not removable
NeedsRepair    "Needs attention", or "Removal incomplete" when a removal stopped part-way; selectable, read-only
```

The selector also has **+** (New Feature), **-** per Feature (Remove Feature), a **Switch Workspace** flyout on the Workspace row (other Workspaces, each with a Feature sub-flyout loaded on hover), and the Open in... flyout and pinned tool buttons (section 30). The selected context is in the URL (`?context=<id>`) and is remembered per Workspace.

Workspace pages read the selected context's own persisted state (checkout projection, Git Changes, projects, file status, PRs, Actions). Generated packages stay global to the Workspace.

A NeedsRepair Feature opens read-only (Sync, Push and Update disabled) with the banner "This Feature needs attention. Use Repair to continue." and a **Repair** button that opens the Feature Status dialog.

### User can

```text
create a Feature ("+" in the selector, New Feature dialog)
switch between Workspace and Features
remove a Feature ("-" in the selector or the Feature menu)
repair, roll back or continue removing a Feature (Feature Status dialog)
open the Feature in a native tool (Desktop only)
work in the Feature with the normal Workspace pages
```

### New Feature

The dialog asks for a Feature name, which is also the branch name. "Based on" is fixed to **Current Workspace**. The name is validated on every keystroke (`FeatureNameValidator`): non-empty, at most 100 characters, no spaces or control characters, not `@`, none of `~ ^ : ? * [ \`, no `..`, `@{`, leading `-`, trailing `/` or `.`, `//`, none of `< > " |`, no Windows reserved names (CON, PRN, AUX, NUL, COM1-9, LPT1-9), none of `& ; % ! $` or a backtick; anything accepted also passes `git check-ref-format --branch`. Duplicate Feature names are checked case-insensitively on Create.

Creation (structural lock `create-feature`):

```text
reject a name that is already a branch in any repository (listed as "X of Y" repositories) or an existing Feature
finish a pending-delete folder of the same name first
record HEAD of every Workspace repository (one GetHeadCommits)
commit the intent in one transaction: Feature Creating + context + one Pending row per repository
git worktree add, Workspace repository first (its worktree is the Feature root
  {ManagedFeatureStorageRoot}\{FeatureName}), then the Sources in parallel to
  {ManagedFeatureStorageRoot}\{FeatureName}\{RepositoryName},
  on a new branch from the recorded commit, or detached when the repository is on a tag
seed the new context from the Workspace: checkout structure, projects, dependency levels and file statistics;
  GitVersion is left empty and marked pending (GitVersionPending) until the Feature's own first GitVersion probe,
  and dependency edges into a pending repository show as pending, not matched or mismatched
select the new Feature
optional: start the CodeGraph index build when graymoon.json has "codegraph": true
  (overlay offers Background instead of Abort)
```

Uncommitted Workspace changes are not copied. If any repository fails, or the create is aborted after the intent is committed, the Feature is kept in **NeedsRepair** with the error.

### Feature Status (repair)

`FeatureStatusPanel` ("Feature Status" dialog) shows each repository's state and error and offers:

```text
Retry              RepairFeatureAsync (structural repair-feature): re-creates only Pending / NeedsRepair
                   worktrees (idempotent), keeps Ready ones, then re-seeds idempotently
Roll back          RollbackFeatureAsync (structural rollback-feature): refuses dirty repositories, removes
                   created worktrees and branches non-force, Sources first and root last
Remove             opens Remove Feature
Continue removal   when a removal stopped part-way (IsRemoveIncomplete); replaces Retry / Roll back
```

`WorkspaceFeatureReconciler` (on every Worker connect, throttled) marks Features stuck in Creating or Removing as NeedsRepair ("Interrupted while creating" / "Interrupted while removing"), marks a Ready Feature whose worktree is confirmed missing as NeedsRepair ("Worktree is missing"), and returns a repaired row to Ready when its worktree is registered. It never deletes worktrees.

### Remove Feature

Opening the dialog analyzes the Feature live (`AnalyzeRemoveFeatureAsync`): per repository a Worker `InspectWorktree` probe and status check, plus one live PR refresh. The Feature is classified completed, abandoned or active, with per-repository status. The dialog shows:

```text
Delete local Feature branches       checkbox, on by default when applicable
Delete remote Feature branches      checkbox when a Feature branch has an upstream;
                                    otherwise "No remote Feature branches to delete (this Feature was never pushed)."
risk confirmations, each shown only when its risk is present:
  Permanently discard uncommitted changes in N repositories          (dirty, staged or conflicted)
  Delete local Feature branches that have commits not in the default branch (N)  (ahead of default, no merged PR)
  Unlock and remove N locked worktrees                                (git worktree lock)
```

Remove is enabled only when every shown confirmation is ticked, and disabled while any repository's disk state is unknown ("Could not check this repository. Make sure the Worker is running, then try again."). An unknown PR state shows a warning but does not block.

Confirming (`FeatureRemovalDeparture`) queues the removal in `FeatureRemovalQueue` (up to 4 removals at once, the rest in order), selects the special Workspace, starts a checkout refresh of it without waiting for the removal, and navigates to the same page without `?context=`. Removal takes no Workspace lock, so Sync and other operations keep running. The outcome is a toast on whatever page is open, with a per-repository report.

Per repository, Sources first and the root last: CodeGraph is stopped and uninitialized first; `git worktree remove` (`--force` only when discard was authorized, unlock only when authorized), guarded residue cleanup, then the local branch is deleted (`-D` only when authorized) and, when requested, the remote branch with a lease (`DeleteBranch` with the expected SHA; refused when the remote tip moved). Remove judges the Feature branch (`refs/heads/<FeatureName>`), not whatever the worktree has checked out; a drifted branch is reported as kept. A repository whose removal fails stays Removing with its error and the Feature becomes NeedsRepair ("Removal incomplete"), with metadata kept; the Workspace context is refreshed without checking out, switching branch or pulling.

GrayMoon never looks for or ends the programs using a Feature (other than CodeGraph's own index processes). When files are still in use, the Feature is removed anyway and its folder is left with a `GRAYMOON-PENDING-DELETE.md` marker (readable by a person or an AI agent working there); the dialog result shows this as information, not a warning. The folder is deleted silently later: when the Worker connects (every GrayMoon start), when the Workspace is opened (at most every 15 minutes, skipped while the Workspace is structurally busy), or before a new Feature reuses the name. Only marked folders under the Workspace's managed Feature storage root are deleted, never one that holds a Git repository, a still-registered worktree, or a Feature that exists.

### Versions from default

Inside a Feature, merge the deepest PRs first, then Sync: for repositories whose Feature PR is merged, `WorkspaceGitService.ApplyDefaultTipVersionsForMergedFeatureReposAsync` runs Worker `GetGitVersionAtDefaultTip` (GitVersion at the `origin/<default>` tip, no checkout) and replaces the context GitVersion, so Update Dependencies writes default-branch versions into higher levels. Only when the profile uses repository versioning. This is why per-level Return to Default is not needed in a Feature (see the known gap in section 31).

### Branch ownership

Switch Branch marks branches that are checked out by a Feature, the Workspace or another worktree, and refuses to delete a Feature-owned branch (use Remove Feature instead). Branch rules inside a Feature are in section 7.

### Settings

**Feature storage root** on the Settings page (`FeatureStorageRootPath`). When empty, GrayMoon uses `{userprofile}\.graymoon` (or `{userprofile}/.graymoon` on a Linux Worker) as reported by the Worker and saves it on first Feature creation. The managed root of a Workspace is `{FeatureStorageRootPath}\{WorkspaceName}\features`; existing Features keep the root recorded when the Workspace's first Feature was created.

Divergence badges in a Feature are computed against the Feature's parent branch, not the default branch.

### Implementation

```text
WorkspaceContextBar / WorkspaceFeatureSelector / FeatureSelectorPresentation
CreateFeatureModal / RemoveFeatureModal / FeatureStatusPanel
IWorkspaceFeatureOperations -> WorkspaceFeatureOperations
FeatureRemovalQueue / FeatureRemovalDeparture
FeatureFinalizationCoordinator (seed barrier)
WorkspaceFeatureReconciler
FeatureBranchPolicy / IFeatureBranchGuard
FeatureCodeGraphService
IWorkspaceFeatureContextResolver
WorkspaceService.ResolveFeatureStorageRootPathAsync
WorkspaceBranchOccupancyService
IWorkspaceExternalWorktreeOperations
Worker GetHeadCommits / CreateGitWorktree / RemoveGitWorktree / ListGitWorktrees / InspectWorktree
Worker GetGitVersionAtDefaultTip / InitCodeGraph / WatchCodeGraphInit / UninitCodeGraph
FeatureFolderCleanupService -> Worker CleanupFeatureFolder / SweepPendingFeatureFolders (FeatureFolderCleaner)
```

---

## 33. Local network security (no user login)

GrayMoon has no user accounts. These rules stop another program or web page on the same machine from
reading tokens or triggering actions through the browser GrayMoon's UI runs in.

### REST API (`/api/...`, `/repos/...`)

```text
a non-GET request with no Origin header passes unchanged (scripts, curl, the Worker)
a non-GET request with an Origin header is rejected (403) unless:
  the Origin's host is this app's own host, a loopback name, or a configured Security:AllowedOrigins entry
  and the request also carries X-GrayMoon-Request: 1
    (a cross-site page cannot add that header without a CORS preflight, which GrayMoon does not allow)
GET requests are never affected
```

### SignalR hubs

```text
/hub/worker (and the legacy /hub/agent alias): any request carrying an Origin header is rejected (403); the Worker's .NET client sends none
/hubs/workspace-sync, /hubs/desktop, /_blazor: a request with no Origin header passes;
  one with an Origin header passes only when its host is this app's own host, a loopback name,
  or a configured Security:AllowedOrigins entry
```

"Own host" also accepts `X-Forwarded-Host` (reverse proxies) and any host listed in the optional
`Security:AllowedOrigins` setting. Desktop sets `AllowedHosts` to loopback names only for the App process
it launches; the shared `appsettings.json` keeps `AllowedHosts = "*"` for Docker and manual installs, which
may be reached by host name or LAN IP.

Git hooks post to the **Worker's** local listener (`127.0.0.1:<port>/hook/*`), not to the App, so they are
outside this middleware.

### Worker secret (`/hub/worker`, `/repos/{id}/connector`, `/workspaces/{id}/capabilities`)

Only the real Worker may open the Worker hub connection, fetch a connector token, or read a Workspace's
capabilities (the Worker's hook-sync path uses them when no App command has warmed its cache).

```text
secret: generated on first start, kept in graymoon-worker.secret next to the database
  (plain text; file mode 600 on Linux; if the file is missing at startup a new secret is generated)
header: X-GrayMoon-Worker-Secret, sent by the Worker on the hub connection and on these requests
wrong secret                      -> 401, always (constant-time comparison)
missing secret                    -> accepted with a warning, UNLESS
                                     Security:RequireWorkerSecret is true, or
                                     some Worker has already presented the correct secret
                                     (setting Security:WorkerSecretSeen), then 401
/repos/{id}/connector, /workspaces/{id}/capabilities and /api/worker/pair
  with any Origin header          -> 403 (the Worker and the install script never send one)
```

How a Worker gets the secret (the install script returned by `GET /api/worker/install` never contains it):

```text
GrayMoon Desktop   the elevated installer copies the secret file to %ProgramData%\GrayMoon\worker.secret
manual / Docker    GrayMoon > Worker shows a one-time pairing code (valid 10 minutes, works once,
                   discarded after 5 wrong attempts); the install script asks for it and calls
                   POST /api/worker/pair { "code": "..." }  ->  { "secret": "..." }  (401 if wrong or expired;
                   403 if the request has an Origin header)
```

The Worker reads the secret from the `GRAYMOON_WORKER_SECRET` environment variable, then from
`worker.secret` under its machine-wide GrayMoon data folder (`%ProgramData%\GrayMoon` on Windows). The file
sits outside the install folder so a Worker update keeps it. A Worker installed before this feature keeps
working without a secret (the App shows "Reinstall the Worker to finish securing GrayMoon") until a Worker
has connected with the secret, or `Security:RequireWorkerSecret` is set (default false).

### Self-update and hook listener hardening

```text
SelfUpdate rejects an installUrl that is not http(s) or contains ' " % ^ \ ` $ & | < > ; ( ) or CR/LF;
  it runs cmd.exe /c start "" /b powershell.exe ... irm '<url>' | iex (Windows only)
the Worker hook listener allows at most 64 in-flight requests (503 above that); a port bind failure
  gives an actionable error and fails startup
```

### Implementation

```text
RequestSecurityMiddleware
WorkerSecretMiddleware / WorkerSecretService / WorkerPairingService
SecurityOptions (Security:AllowedOrigins, Security:RequireWorkerSecret)
Worker: WorkerSecretProvider, SignalRConnectionHostedService, WorkerTokenProvider, IWorkspaceCapabilityProvider,
        SelfUpdateCommand, HookListenerHostedService
Desktop: WorkerInstaller (secret hand-off)
```

---

## 34. AI-generated commit messages and PR text

### User can

```text
Changes page      click the provider segment of the Commit control to write ONE commit message for exactly
                  what the button would commit (Commit Staged or Commit All); it only fills the message box
                  "Generate commit message" (On Commit) starts ticked, so Commit writes the message first when the box is empty; untick it to stop that
New PR dialog     click the provider selector at the right of the tab bar to write ONE title and ONE Markdown
                  description shared by every PR in the dialog
both              pick a provider in the menu (pins it for the Workspace on this machine; choosing the
                  provider that is already pinned removes it until another is pinned; never generates)
```

Providers are Claude (`claude`), Codex (`codex`) and Cursor Agent (`agent`, then `cursor-agent`; the editor's `cursor` command is not the agent). Each CLI must be installed and signed in for the Worker's user. Credentials never go in `graymoon.json`.

### Behavior

```text
generate on commit   with "Generate commit message" ticked, a provider pinned and an empty box, the commit job
                     writes the message, then commits, then pushes if "Push committed" is ticked; the overlay
                     terminal shows each step labelled "ai" (the CLI call, never the prompt); if the provider
                     fails nothing is committed; a typed message is always committed as typed
survives navigation  clicking the provider keeps writing when you leave Changes, reload, or switch Feature /
                     Workspace; a Feature and the Workspace can be written at the same time; each context keeps
                     its own message (typed or written); Cancel stops only the context on screen. While that
                     context is still on screen and the scope has not moved, the text goes straight into the box;
                     otherwise it is kept for that context (the message, or a suggestion if the box was edited)
checkboxes           Generate commit message starts ticked and Push committed starts unticked; both stay as set across F5 and Ctrl+F5 until the App restarts
PR comparison        each repository compares its head with the target selected in the Branch tab, never the
                     default branch; PR generation does not run in an overlay job
apply policy         AiTextApplyPolicy.Decide: text goes straight into the field only if the field was empty at
                     the click, has not been edited since and the scope is unchanged; otherwise it is offered as a
                     suggestion with Replace, Copy or Dismiss and the reason (edited, changes moved on, branch
                     selection changed)
all-or-nothing       if any repository cannot be compared (missing target ref, no merge base, no commits ahead,
                     conflicts), nothing is generated and the message names the repositories
failures             stay inside the AI control as one redacted line (not signed in, unsupported model,
                     unsupported CLI, timeout, invalid output); Commit and Create still work
```

### Configuration

`graymoon.json` may hold `"ai": { "providers": [ { "provider": "claude|codex|cursor", "model": "..." } ] }`; it is file-owned (kept on every rewrite, ignored by drift) and Feature contexts read it from the special Workspace. Unknown providers are kept and shown as a notice; a duplicate or an unsafe model disables that provider. Default models: Claude `haiku`, Cursor `grok-4.7-low-fast`, Codex its CLI default; an explicit model wins. The pinned provider is `Workspaces.PinnedAiProviderId`, local only.

### Flow

```text
resolve provider and model, check availability (the startup GetHostInfo; menus do not probe)
Worker CollectGitEvidence   one request for all repositories, bounded fan-out, never mutates or fetches
AiPromptBuilder             one prompt under a hard budget (commit 4,000 / PR 8,000 estimated tokens),
                            secrets redacted, evidence wrapped as untrusted data
Worker GenerateAiText       one CLI process in its own empty temp folder, no tools (Cursor: read-only ask mode),
                            timeout AiGeneration:TimeoutSeconds (default 120)
AiGeneratedTextParser       strict JSON check; the result is never partial
```

All three Worker commands run on the main pool. The App never runs git or an AI CLI.

### Implementation

```text
Components/Ai: AiProviderControl, AiMenuCheckItem, AiSuggestionPanel; wwwroot/css/ai-generation.css, js/gm-ai-menu.js
WorkspaceGitChanges.Ai.cs / NewPullRequestModal.Ai.cs
WorkspaceGitChangesCommitGeneration   click-to-write runs outside the page, one run per Workspace and Feature context
WorkspaceGitChangesCommitMessageMemory   the message, per Workspace and Feature context
WorkspaceGitChangesPushAfterCommitMemory / WorkspaceGitChangesGenerateMessageOnCommitMemory   the two On Commit checkboxes, per Workspace
IWorkspaceAiGenerationOperations -> WorkspaceAiGenerationOperations
AiTextApplyPolicy, AiPromptBuilder, AiGeneratedTextParser (Common)
Worker: GitEvidenceCollector, AiCliRunner, AiCliAdapters
```
