# 05 - User Capability Reference

This document maps current user capabilities to the main implementation areas that own them.

It is intentionally practical. A developer should be able to find the responsible code from the user behavior.

---

## 1. Workspaces

### User capabilities

```text
list Workspaces
search
create a new, empty Workspace (Add Workspace)
restore from a Workspace repository (Add Workspace menu: "Restore from repository")
import a folder that is already on disk (Add Workspace menu: "Import from disk")
edit name/path
choose the Workspace profile
choose, change or remove the Workspace repository
set default
remove (refused while the Workspace has Features; GrayMoon's Git hooks are removed from the repositories)
select repositories
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

The Workspace modal has tabs: **Details** (**Name** and **Location**), **Type** (**Workspace Type**: **Basic** / **.NET Dependency**), **Options** (titled **Features**: **Calculate versions with GitVersion**, **Show GitHub Actions**, and **Use CodeGraph** when the CodeGraph CLI is installed), **Repo** (**Workspace Repository**) and **Lock** (**Repository Lock**). The panels share the height of the tallest one, so switching tabs does not resize the dialog. Picking a type sets the GitVersion and GitHub Actions defaults (`WorkspaceProfileDefaults.ForType`: .NET Dependency turns both on, Basic turns both off); the user may override either. Use CodeGraph is not changed by the type. The dialog opens without waiting for the Worker's host check: until the check has answered (`CodeGraphOfferCheck`, re-read every 2 seconds while the dialog is open), Use CodeGraph is shown with its stored value and disabled; an answer enables it, or hides it when the CodeGraph CLI is not installed. Save has no spinner; it is disabled while a save runs. It is stored in `graymoon.json` (`SetCodeGraphEnabledAsync`), not on `Workspace`. Saving it on starts a CodeGraph index for the Workspace folder when the Worker says one is missing; the overlay offers Background. The same dialog is Edit Workspace on the Workspaces page and Edit... in the context menu. A save writes the files of the context it was opened from: Edit Workspace on the Workspaces page writes `graymoon.json`, the managed `.gitignore` block and `codegraph.json` in the special Workspace and indexes the Workspace folder; Edit... in the context menu passes the selected context (`WorkspaceSettingsHost.OpenAsync(workspaceId, contextId)`, `IWorkspaceManifestService.ForContext`), so inside a Feature it reads Use CodeGraph from, and writes those files in, that Feature's Workspace repository worktree, indexes the Feature folder, and leaves the special Workspace's files alone. The name, the profile, the Workspace repository choice and the Lock tab are stored in the database and are not per context. Add Workspace refuses a ticked Use CodeGraph with no Workspace repository before it creates the Workspace (`WorkspaceCodeGraphActivation.MissingWorkspaceRepositoryMessage`). On the Workspaces list, whose path has no Workspace id, `WorkspaceSettingsHost` shows the index overlay for `WorkspaceCodeGraphActivation.ActiveWorkspaceId`, so it covers Add Workspace as well as Edit. Enter saves from the fields; on the tabs, the Lock tab and the footer buttons the key stops at the focused button. The dialog body scrolls when the dialog would be taller than the window. Saving from the context menu reloads the page, unless a job is running in that tab (`IBackgroundJobService.HasRunningJobs`): a reload would end the circuit and the job, so a warning toast asks the user to reload later. The three axes (`Type`, `VersioningMode`, `CiProvider`) are persisted on `Workspace`; behavior is derived from them through `WorkspaceCapabilities`, never from `Type` directly.

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
Import from disk uses the same picker for its Folder field (no Fetch)
```

The Workspaces page header has one split button (`wwwroot/css/split-button.css`): **Add Workspace**, and a chevron that opens a menu with **Restore from repository** and **Import from disk**. The three paths do not overlap:

| | Folder before | Definition comes from | Clones |
|---|---|---|---|
| Add Workspace | none or empty | the dialog | the Workspace repository, if chosen; Sources at Sync |
| Import from disk | exists with repositories | the folder's `graymoon.json`, or the dialog when it has none | only Sources that are missing on disk |
| Restore from repository | none or empty | the remote repository | everything |

Add Workspace only adds. While the name is typed the modal asks the Worker for the folder's state (`WorkspaceService.GetDirectoryStateAsync`, the check Restore uses) and `ExistingWorkspaceFolder.AddBlockedReason` decides: a folder that exists and is not empty shows "This folder already exists and is not empty. Use Import from disk to add it as a Workspace." and Save stays disabled. A folder that could not be checked is blocked too; it is never assumed to be free. The check runs again right before the Workspace is created.

### Deleting a Workspace

Remove Workspace removes the GrayMoon Workspace and the Git hooks GrayMoon installed in its repositories. Repositories, folders, branches and everything else on disk stay. It goes through `IWorkspaceRepositoryOperations.RemoveWorkspaceAsync`:

```text
1  Worker connected and version-matched?   no  -> refused, nothing changed (the dialog disables Remove and says why)
2  Workspace structural lock               held -> refused "Another operation is running in this workspace..."
3  Workspace has Features?                 yes -> refused "This workspace has N Features: a, b. Remove the Features first..."
4  DetachRepositories for every link (04, section 7): the Worker releases its file watchers on the folders,
   removes GrayMoon's hooks and puts the user's own replaced hooks back
5  any repository Failed                   -> nothing is deleted; the dialog stays open with one line per repository
6  delete the Workspace row (cascades), while Git Changes monitoring of the Workspace is still paused
```

A missing folder, a folder that is not a repository and a repository without GrayMoon hooks are successes. Only a real problem stops the removal (a hook file that cannot be deleted, the user's own hook that cannot be renamed back, a folder the Worker cannot let go of, Git unable to read the repository), and the message names the repository, the file and what to do (`DetachFailureText`). The repositories that were already cleaned get their hooks back on the next Sync if the Workspace is kept. After a success a toast says which repositories got the user's own hooks back.

The same detach runs before every other path that ends GrayMoon's management of a folder, with the same messages: removing a Source repository in the Repositories dialog or settings (`SaveWorkspaceAsync`; a save that removes nothing needs neither the Worker nor the lock), disabling the Workspace repository (`DisableWorkspaceRepositoryAsync`), a Source that becomes the Workspace repository (its old folder; a failure there is a warning, the move stands), a rolled-back restore or import, and a link undone after a lost attach answer. `WorkspaceLinkDeletionConventionTests` fails when Workspace or link rows are deleted anywhere else.

### Import from disk

`IWorkspaceImportOperations` (`WorkspaceRepositoryOperations.Import.cs`), dialog `ImportWorkspaceModal` (rules in `ImportWorkspaceFlow`). Shaped like Restore: read-only preflight, preview, one operation with rollback, result panel, then the normal full Sync.

```text
Folder          picker of the folders under the workspace root that are not a Workspace (Worker ListWorkspaceRootFolders);
                the Workspace is named like its folder
Preflight       Worker InspectWorkspaceFolder (reads only): root repository and origin, child repositories and origins,
                the text of graymoon.json; origins are matched to the imported repositories (RepositoryUrlIdentity)
```

| Folder | Import |
|---|---|
| Git repository with a valid `graymoon.json` | The file decides profile, Source repositories, read only flags and version files (`RestoreDefinitionEvaluator`, the code Restore uses), read from disk as it is now. The root is adopted as the Workspace repository, never cloned over. Sources on disk are linked as they are (no checkout, reset or pull); listed Sources that are missing are cloned by the Sync; repositories on disk that the file does not list stay unmanaged and are shown. `graymoon.json` is rewritten only under Restore's rule. |
| Folder that is not a Git repository | The user picks the profile. Child repositories whose origin matches an imported repository are linked. Nothing is written into the folder. |
| Git repository without `graymoon.json` | As the row above for the children. The root repository is not linked and nothing is written into it; the preview says it can be chosen in settings afterwards. |

Refused before anything is created: the Workspace repository is not imported into GrayMoon ("Import it through Connectors, then import this folder."), an invalid definition, a newer schema, an unknown profile, a name that is already a Workspace, a folder with no repository GrayMoon knows. A repository in a folder whose name is not the repository's name is not linked and the preview says why. A failure after the Workspace row was created takes it back: the hooks the adopt wrote (detach), then the rows. The folder's content is never deleted or changed, and `DiscardWorkspaceRoot` is never sent. The hooks are written by the Sync at the end, with the new Workspace's ids.

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
root has .git with a matching origin    adopt (AdoptedExisting). When HEAD is unborn and the remote has a default
                                        branch (an earlier attach stopped before its checkout), the checkout is
                                        finished and the result is a fresh attach, not an adopt
root has .git with another origin       refused: "Root already has a different Git repository"
root is empty or missing                clone into it
root not empty, no .git                 git init, add origin, fetch tags, check out the tracking default branch
                                        or an unborn main for an empty remote
then                                    safe.directory, long paths, hooks
```

The default-branch checkout never overwrites a file that is already in the folder. `git checkout -b <branch> --track origin/<branch>` runs first; when git refuses (an untracked file would be overwritten), `GitService.CheckoutTrackingKeepingFilesAsync` sets the branch, its upstream and the index without touching the working tree (`update-ref`, `symbolic-ref`, `branch --set-upstream-to`, `reset`), writes only the missing files (`checkout-index -a`) and returns the existing files that differ (`ls-files --modified`). The response carries them as `keptFiles` (at most 20 names) and `keptFileCount`; the App reports them as a warning on the successful `OperationResult` (`WorkspaceRepositoryOperations.KeptFilesMessage`), which both dialogs show as a toast.

A Source repository of the Workspace can be chosen (`WorkspaceModal.BuildWorkspaceRepositoryChoices` lists it with the **Source** badge and the Repo tab shows `SourceRepositoryHint`). `LinkAndAttachAsync` then changes the existing link's role to Workspace instead of adding a link, so nothing is deleted before the attach has succeeded: a failed or cancelled attach sets the role back to Source and the link keeps its state. After a successful attach the link and its dependent rows (`WorkspaceRepositoryLinkCleanup.DeleteDependentsAsync`) are replaced by a new Workspace-role link in one transaction, because the old state described the Source checkout. That checkout is never deleted; the result carries `MovedFromSourceMessage` as a warning.

Only a fresh attach (not an adopt) then writes the managed `.gitignore` section and `graymoon.json` through `WriteRepositoryFile`. When the dialog passed a setup branch (`EnableWorkspaceRepositoryAsync(..., setupBranchName)`, the Repo tab's **New Branch** field, default `WorkspaceSetupBranch.DefaultName` = `graymoon/workspace-setup`), the files are written on that new branch: the name is validated (`WorkspaceSetupBranch.Validate`) before the lock is taken, the remote branches are read through the connector (`IRemoteRepositoryBranchReader`) before anything is linked or cloned and a name that exists there (ignoring case) fails with nothing changed, and after the attach the existing `CreateBranch` Worker command creates the branch from the branch the attach checked out. The files are written only when the Worker reports HEAD on the new branch; otherwise the operation fails, nothing is written, and the link and the clone stay (removing the link would make a retry adopt the folder without a branch). No setup branch is created for an adopted folder or a repository with no commits. GrayMoon does not commit or push the branch. A remote with more branches than the connector reads (`RemoteBranchList.Truncated`) is only checked against the ones that were read. An adopted folder keeps its `graymoon.json`, because rewriting it from the database would drop version files and repositories not imported yet. On a fresh attach a failed `.gitignore` or definition write fails the operation with a message but keeps the link and the attached repository; the drift banner repairs it. Enable then scans Git Changes for the repository so the Changes dot updates. Worker commands carry `WorkspaceRepositoryName` so `WorkerRepositoryPaths.Resolve` maps the Workspace repository to the Workspace root and every other repository to a nested folder.

**Restore** (Workspaces page, **Restore from repository** in the **Add Workspace** menu, dialog title "Restore Workspace") is two steps.

`PreflightRestoreAsync` reads `graymoon.json` from the selected branch through the GitHub contents API (`?ref=`; the default branch when no branch is given) (no Worker, no clone, no database change), validates it (a missing file, invalid JSON, a newer schema and unknown profile values are blocking errors) and previews the profile, the Source repository count and the connectors and repositories missing on this computer. Choosing a repository clears the previous preview immediately and lists its remote branches (`IRemoteRepositoryBranchReader`, paginated, default first; a list over 2000 branches is flagged `Truncated`, never cut silently); the provider's default branch is selected, and only then, and after each committed branch choice (never while typing or filtering), is one manifest preflight started. A late answer for an earlier repository or branch is dropped. Restore stays disabled while that read is in progress, and is enabled only when the preflight is valid and the name, the folder and the Worker are all OK.

`RestoreFromRepositoryAsync` takes the same `branchName`: the definition is validated at that branch, the Worker `AttachWorkspaceRepository` clones the root and checks out that branch (`branchName` in the request; a missing or unsafe branch fails the attach, never falls back to the default), and the definition is read again from the cloned copy, so a branch tip that moved since the preflight is validated again rather than trusted. Only the Workspace repository uses the branch; Source repositories keep their own rules, and the branch is not written to `graymoon.json`. Steps:

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

Deleting a connector never removes a repository that a Workspace uses. The delete dialog says how many such repositories there are. They stay in their Workspaces, with their branches, pull requests and Features, and show a "no connector" badge in the grid ("No connector" in the Repositories page connector column). Local work keeps working; work that needs a token is refused with "Repository X has no valid connector. Add or repair a connector that can access it, then fetch repositories." The next Fetch Repositories by any connector that returns the same clone URL takes the repository over automatically, and the removed connector is purged once none of its repositories is left. Repositories of the deleted connector that no Workspace uses are deleted with it. A repository its connector no longer returns shows a "not found on provider" badge in the grid.

A repository refresh never removes a repository that a Workspace uses: when the connector no longer returns it, it stays in the Workspace and the Repositories page lists it under "Not found on the provider" with the Workspaces that use it. Only repositories no Workspace uses leave the list, and only for a connector that returned at least one repository. A connector that answers with no repositories at all changes nothing and is named in the connector error list.

### Connector dialog

The Add / Edit Connector dialog (`ConnectorModal`) accepts what the user knows in **API Base URL**: the endpoint itself, a repository or organization URL, an SSH clone address, or a host. `ConnectorUrlResolver` (text only, no network) turns it into the endpoint candidates:

| Typed | GitHub connector | NuGet connector |
|---|---|---|
| `github.com/...` | `https://api.github.com/` | `https://nuget.pkg.github.com/OWNER/index.json` (the owner is the first path segment) |
| `SUBDOMAIN.ghe.com/...` | `https://api.SUBDOMAIN.ghe.com/` | the generic feed candidates |
| any other host | `HOST/api/v3/`, then `api.HOST/`, then the address as typed | `.../v3/index.json`, then `.../index.json` |
| `nuget.org` | - | `https://api.nuget.org/v3/index.json` |

A GitHub Packages feed needs its owner (organization or user): a feed address without one is refused with a message and no request is made. Each owner needs its own connector.

**ProGet** is a third connector type (`ConnectorType.ProGet`): a NuGet feed on a ProGet server, read through `NuGetService` like a NuGet connector (`ConnectorHelpers.IsPackageRegistry`). Its dialog has **Server URL**, **Feed** and **API Key** (no User Name, no Create button). Server URL takes the server (`https://proget.example.com`) or any feed address; the feed is read from `/nuget/FEED/...` or `/feeds/FEED/...`, else from the Feed box. The connector is saved with the feed endpoint `SERVER/nuget/FEED/v3/index.json` as its API Base URL; the dialog shows that as server and feed again when it is edited (`ConnectorFormAutoFill.SplitProGetAddress`). Each feed needs its own connector, and the untouched name becomes "ProGet FEED".

Test for ProGet: `SERVER/health?format=json` (no key needed) must name the application ProGet, otherwise the test fails on the address and the API key is never sent. Then the feed endpoint is read, first without the key (a public feed needs none) and, when refused, with Basic sign-in as the user `api` with the key as password. The same sign-in is used for package lookups. Without a feed name, the key is used to list the server's NuGet feeds (`api/management/feeds/list`): a single feed is taken, several are offered in the Feed dropdown, and none (most keys may not list feeds) asks the user to type the name. The Feed box has an attached dropdown button: opening it loads the server's NuGet feeds on demand (`ConnectorEndpointTester.ListProGetFeedsAsync`, once per server address and key, the key again sent only after the health page identified ProGet) and lists them under the box, drawing past the bottom of the dialog so the dialog does not cut the list off; a click or the Down / Up keys put a feed in the box, typing narrows the loaded list to the names that contain the text, and any typed name is still accepted. When there is nothing to list, the dropdown says why (no Server URL, no API key, not a ProGet server, or a key that may not list feeds). The Feed box has its own mark: a tick when the feed was found, an X with the reason when it is missing or not on the server. A NuGet connector that points at ProGet is left as it is (it keeps its previous sign-in); switching its type to ProGet in Edit moves it to this one.

The **Test** button next to the box (`ConnectorEndpointTester`) probes the candidates in order, writes the first one that answers as the right kind of API into the box, and shows the result as marks at the end of the boxes: a green tick in the address box when an endpoint was found, a red X with the reason under the box when not; a green tick in the token box when the endpoint accepted the token, a red X with the reason under it when it refused the token, and no token mark while no token is entered. The result in words is the tooltip of the marks. Changing the address clears both marks and drops a running test; changing the token clears the token mark only. A GitHub token is sent only to a host that has already answered as a GitHub API without it; each answer is read up to 256 KB and each request times out after 10 seconds. The address may name a private host, because an on-premises GitHub or feed lives there.

**Add** / **Update** run the same step first when the address is not the endpoint yet, and stay open when no endpoint is found. An address that already is the endpoint is saved as typed with no request, so existing connectors are not rewritten. The test after saving (status column) is unchanged.

**Create** next to the token opens the "new personal access token" page of the site the address belongs to (`github.com`, the Enterprise Server host, or the `ghe.com` tenant). Without a usable address it opens nothing and says "API Base URL is required to create a token." under the address box. It is offered for every GitHub connector and for a NuGet connector that is blank or on GitHub Packages (scope `read:packages` only).

`ConnectorFormAutoFill` holds what the dialog fills in by itself. In Add mode the name follows the type ("GitHub", "NuGet", then "NuGet OWNER" once a GitHub Packages owner is known) until the user types a name. On a type switch the address is replaced only when it is blank or still the previous type's default; NuGet starts blank.

### Implementation areas

```text
Connector models/repository
connector services/factory
ConnectorUrlResolver / ConnectorEndpointTester / ConnectorFormAutoFill (connector dialog)
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
Install tab       labelled "Update" and shown red while the state is VersionMismatch (also red while Offline,
                  except during a Desktop restart);
                  shows the PowerShell install command (served by GET /api/worker/install),
                  the .NET 10 runtime note, the "Reinstall the Worker to finish securing GrayMoon" notice,
                  "Show pairing code", and a stale Windows service password notice
                  footer: "Update Now" on a mismatch (sends SelfUpdate), "Install" in Desktop mode
                  (gray "Installing" while that install runs, gray "Restarting" while a restart runs), "Download"
Uninstall tab
Host tab          only while Online or VersionMismatch; Worker version, .NET SDK, Git, GitVersion,
                  CodeGraph (codeGraphVersion, optional, informational only),
                  Claude (claudeVersion; listed with its install command, never required),
                  Codex (codexVersion; listed with its install command, never required),
                  Cursor Agent (cursorAgentVersion; listed with its install command, never required)
                  footer in Desktop: "Install Now" for missing prerequisites, and "Restart"
                  (restarts the Worker service so an AI agent installed afterwards is on its PATH)
Environment tab   always shown; a grid of the environment variables GrayMoon sets when it starts an AI agent:
                  Scope (All, or an installed agent: Claude, Codex, Cursor; every agent while the Worker is
                  offline; a row keeps a scope whose agent is "(not installed)"), Name, Value, and a remove
                  button (x in a circle). The Scope box shows the icon of the chosen scope: the agent's
                  own icon, or the AI glyph (stars) for All; the open list is the browser's and is text only.
                  The grid shows exactly three rows and scrolls the rest under sticky column headers,
                  so the text above, "Add" and the footer stay in place and the tab itself does not scroll.
                  "Add" appends a row with scope All. Typing a name suggests the
                  variables the installed agents are known to read (all of them for All, one agent's for that
                  agent); picking one fills its usual value when the value is empty.
                  footer: the first problem (a name that is not letters, digits and underscores, a name
                  listed twice in one scope, a value that is not one line), "Revert" and "Save"; nothing is
                  stored until Save. A list GrayMoon changed meanwhile reloads when the grid has no edits,
                  else a note says so.
                  At startup (the first host check of the App) a known variable that is already set on the
                  Worker host (NODE_OPTIONS for Cursor, CLAUDE_CODE_DISABLE_BG_SHELL_PRESSURE_REAP for Claude)
                  is listed for its agent with the value found there, when that CLI is installed and the
                  agent does not already get the name from the list (its own row or an All row). A row
                  removed afterwards stays removed until the App starts again.
```

`GetHostInfo` (one of the two commands allowed on a version mismatch) supplies the host versions and which AI CLIs are installed. It probes .NET, Git, GitVersion, CodeGraph, and each AI CLI together, at most four at a time. Required prerequisites follow the Workspace profiles (`HostPrerequisiteRequirements.For`): Git always, GitVersion when any Workspace uses it, the .NET SDK when any Workspace uses GitVersion, package restore or dependency-aware update. Missing optional tools are labelled "(optional)". The Home nav circle is red only while the Worker is down, and it clears as soon as the Worker is up; it does not check host prerequisites or connectors. Claude, Codex and Cursor Agent count as missing but are never required.

### Environment variables for AI agents

The Environment tab accepts any variable. Only two are offered as name suggestions (they come from the agent adapters, `IAiCliAdapter.KnownEnvironmentVariables`): `NODE_OPTIONS` for Cursor and `CLAUDE_CODE_DISABLE_BG_SHELL_PRESSURE_REAP` for Claude. The tables below list other variables the agents' own documentation describes and that are useful for how GrayMoon runs them; type them by hand. They were read from the vendor pages on 2026-11-20 and are not checked by GrayMoon, so confirm a variable against the installed CLI version before relying on it. "(path)", "(URL)", "(key)" and "(number)" mean you supply the value.

Commit message and pull request text runs start the CLI without tools, so variables about commands, shells and terminals only matter for Open in Claude CLI, Codex CLI and Cursor.

Cursor (Cursor Agent CLI; source: cursor.com/docs/cli/reference/configuration, authentication):

```text
NODE_OPTIONS            --use-system-ca   trusts the Windows certificate store as well as the bundled list; fixes
                                          "unable to get local issuer certificate" behind a TLS-inspecting proxy.
                                          GrayMoon adds it for Cursor by itself on that error,
                                          after the options already listed (or found on the host at startup). Not on Cursor's pages;
                                          verified on the Node 24.5 the CLI bundles (NODE_USE_SYSTEM_CA=1 is ignored
                                          there, it is read from Node 24.6 on)
NODE_EXTRA_CA_CERTS     (path)            one extra PEM certificate to trust; Cursor's documented setting for proxies
                                          that inspect SSL
HTTPS_PROXY             (URL)             proxy for HTTPS traffic
HTTP_PROXY              (URL)             proxy for HTTP traffic
NODE_USE_ENV_PROXY      1                 needed for the CLI to honor HTTP_PROXY and HTTPS_PROXY
CURSOR_API_KEY          (key)             signs in without a browser; useful when the Worker runs as a service
CURSOR_CONFIG_DIR       (path)            moves the CLI configuration folder
```

Cursor's pages list no variable for telemetry, update checks or `NO_PROXY`. A proxy that does not support HTTP/2 streaming (Zscaler) is handled in the CLI configuration file (`network.useHttp1ForAgent`), not by a variable.

Codex (source: the Codex "Environment variables" page, learn.chatgpt.com/docs/config-file/environment-variables):

```text
CODEX_CA_CERTIFICATE    (path)            PEM bundle for corporate TLS interception or private root certificates;
                                          takes precedence over SSL_CERT_FILE. Codex's counterpart of the Cursor fix
SSL_CERT_FILE           (path)            fallback PEM bundle when CODEX_CA_CERTIFICATE is unset
CODEX_API_KEY           (key)             API key for non-interactive runs (codex exec, which GrayMoon uses)
CODEX_HOME              (path)            root of Codex state (config, sign-in, logs, sessions); default ~/.codex,
                                          and the folder must already exist
RUST_LOG                debug             log filter and verbosity (error, warn, info, debug, trace); codex exec
                                          defaults to error
```

Codex's pages document no proxy variable (`HTTPS_PROXY`, `NO_PROXY`) and no variable that turns off telemetry or update checks; those are settings in its `config.toml`. GrayMoon starts `codex exec` with `--ignore-user-config`, so a commit message or pull request text run does not read that file.

Claude (Claude Code; source: code.claude.com/docs/en/env-vars, which lists about 400 variables):

```text
CLAUDE_CODE_DISABLE_BG_SHELL_PRESSURE_REAP   1          stops Claude Code from ending background shell commands
                                                        under memory pressure. macOS and Linux only: Windows has
                                                        no memory-pressure signal, so it has no effect there.
                                                        Needs Claude Code 2.1.193 or later
HTTPS_PROXY                                  (URL)      proxy for HTTPS traffic
HTTP_PROXY                                   (URL)      proxy for HTTP traffic
NO_PROXY                                     (hosts)    domains and addresses that bypass the proxy
DISABLE_AUTOUPDATER                          1          stops background self-updates; "claude update" still works
CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC     1          turns off auto-update, telemetry, error reporting, feedback
                                                        and release notes together. Any non-empty value disables,
                                                        "0" and "false" included
DISABLE_TELEMETRY                            1          telemetry only. Any non-empty value opts out
API_TIMEOUT_MS                               600000     time for one API attempt to return response headers
                                                        (default 10 minutes)
CLAUDE_CODE_MAX_OUTPUT_TOKENS                (number)   output tokens per request
CLAUDE_CODE_EFFORT_LEVEL                     low        reasoning effort: low, medium, high, xhigh, max
CLAUDE_CODE_GIT_BASH_PATH                    (path)     bash.exe on Windows, when Claude Code cannot find it
BASH_DEFAULT_TIMEOUT_MS                      120000     default timeout of a foreground command (default 2 minutes)
CLAUDE_CODE_DISABLE_TERMINAL_TITLE           1          stops Claude Code from renaming the terminal tab
ANTHROPIC_BASE_URL                           (URL)      sends requests through a proxy or company gateway
ANTHROPIC_API_KEY                            (key)      API key instead of the subscription sign-in
CLAUDE_CODE_USE_BEDROCK                      1          use Amazon Bedrock (CLAUDE_CODE_USE_VERTEX, _USE_FOUNDRY
                                                        for the other providers)
CLAUDE_CONFIG_DIR                            (path)     moves the Claude Code configuration folder (default ~/.claude)
```

Claude Code needs no certificate variable for a TLS-inspecting proxy: `CLAUDE_CODE_CERT_STORE` defaults to `bundled,system`, so it already trusts the operating system's store. A client certificate is set with `CLAUDE_CODE_CLIENT_CERT`, `CLAUDE_CODE_CLIENT_KEY` and `CLAUDE_CODE_CLIENT_KEY_PASSPHRASE`.

A proxy or certificate needed by every agent can be listed once with scope All; an agent that does not read a variable ignores it. Values are stored encrypted, so a key or token may be listed, but it is then passed to every start of that agent.

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

When a local branch of that name is already in a repository, nothing is created there: the Worker (`GitService.CreateBranchAsync`) checks the existing branch out as it is, the chosen base is not used, and the response says `alreadyExisted`. This is decided before the create, so a create that fails for any other reason (a base that does not exist, local changes in the way) is a failure and never ends on another branch. The caller reports it as a warning built by `BranchAlreadyExistedWarning.Describe`, for example "Branch 'feature/x' already existed in 2 repositories (api, web). It was checked out as it is and was not created from the base branch.": New Branch for one repository (`CreateBranchApiResult.AlreadyExisted`), Create Branches (`BranchCreationOutcome.AlreadyExistedByRepositoryId`) and Prepare Workspace (`PrepareWorkspaceResult.BranchAlreadyExisted`, also in the REST response as `branchAlreadyExisted`). Test anchors: `GitServiceCreateBranchTests`, `CreateBranchesOutcomeTests`.

A repository without commits has no base to branch from (git answers "'main' is not a commit"). When HEAD is unborn and the base does not exist either, the Worker runs `git checkout -b <new>` without a start point: the new branch takes the place of the unborn one, and the index and the files stay. The base is not used. Git runs no post-checkout hook there, so New Branch for one repository (`WorkspaceBranchOperations.CreateBranchAsync`) records the branch the Worker reports checked out itself, as a checkout does. A checkout in such a repository (`GitService.CheckoutBranchAsync`) is a success for the branch it is already on and moves the unborn HEAD for any other name.

GitVersion cannot compute a version for such a repository. The Version cell shows "unresolved" and the row shows one sentence, "Repository has no commits yet, so it has no version.", instead of GitVersion's output. For any other GitVersion failure the row shows the message of the exception GitVersion printed ("GitVersion: ..."), without the log lines and the stack trace (`GitVersionFailureText`); the full output is in the Worker log.

### Switch Branch

Computes compatible/common branches and performs coordinated checkout. Not offered as a bulk action in a Feature.

The list of common branches (`WorkspaceBranchOperations.GetCommonBranchesAsync`) is the intersection over the repositories the checkout depends on. Three kinds of repository are not asked: one on a tag, a read only one (`RepositoryOperationPolicy.IsOptional`, `ReferenceRule.Optional`), and one with no branch at all, which is a repository without commits. The checkout (`WorkspaceBranchHandler.CheckoutBranchForWorkspaceAsync`) switches a read only repository when it has a local or remote branch of that name and otherwise leaves it where it is: not attempted, not counted, not a skip and not an error. A repository without commits is still attempted and shows git's answer on its row. Test anchors: `ReferenceRepositoryEnforcementTests`, `ServiceGapCharacterizationTests`.

### Create PR

Starts multi-repository PR creation (section 24). In a Feature context, a repository is creatable when its worktree HEAD differs from the Feature's base commit, and the PR targets the Feature's parent branch.

### Merge PRs...

Opens `BulkMergePullRequestsModal` over the scope (a level, the Workspace or Repositories header set, or all repositories). Candidates are resolved from the persisted PR table, not from the virtualized render cache. The "Return to default branch" checkbox shares its remembered value with the single-PR dialog and is hidden in a Feature. When checked, **Merged** (default) or **All repositories** chooses the return set: Merged returns only repositories whose PR just merged; All repositories also returns every other repository in the same dialog scope that had no open PR and is not on a tag (tag checkouts are never returned). Returning after a bulk merge uses `ReturnToDefaultUnattendedBatchAsync`: ids deduped, branches refreshed in parallel, one PR refresh, eligible repositories returned in parallel (tag checkouts completed as untouched), one recompute only when at least one returned. Overlay progress shows counts only (no "(failed)" annotation); repository failures surface on the workspace afterward. When every row succeeded but a branch could not be deleted, the closing toast is the warning of section 14 instead of the success message; repositories the return left out by policy are appended to the success message.

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

Tagged repositories are intentionally protected from mutating workflows: a repository checked out on a tag (including a Workspace tag pin in `graymoon.json`) is never a candidate for Push, Sync Commits, Update Dependencies, Return to Default, Prepare Workspace branch work, or bulk Merge PRs... return-to-default follow-ups. Those operations leave the tag checkout untouched rather than failing it.

### Read only repositories and the operation policy

Whether an operation runs on a repository is decided in one place, `RepositoryOperationPolicy` (`GrayMoon.Application/Repositories/Policy`). How the policy is put together (subject, sources, the three ways to ask, skips) is in 02, section 7B. The table is its rule table. "all" is an action on several repositories (a repository that is not allowed is skipped and named), "one" is an action on one named repository (it is refused with a message).

```text
operation                              on a tag   read only   snapshot in a Feature
Sync (Workspace), Fetch                yes        yes         yes
Sync from a row, Sync of a level       no         yes         yes
Refresh projects, Restore packages     no         yes         yes
Pull                                   no         yes (fast-forward only)   no
Switch Branch (one repository)         yes        yes         no
Checkout in all repositories           yes        when it has the branch (never required)   no
Create branch                          yes        no          no
Return to Default                      no         one: plain switch to the default branch; all: no   no
Return to Default cleanup              no         no          no
Delete local branch                    yes        yes         no
Delete remote branch / forced delete   yes        no          no
Update Branch from Default             no         no          no
Update dependencies, version files     no         no          no
Commit by GrayMoon (after an update)   no         no          no
Commit in Changes                      no         no          no
Push                                   no         no          no     (also no for a repository archived on the provider)
Set upstream                           yes        no          no
Undo push                              no         no          no
Create / merge pull request            no         no          no
Run workflow                           yes        no          no
Stage, unstage, discard                yes        one: yes; all: no         one: yes; all: no
Create Feature branch                  no (detached at the tag)   no (snapshot)   no
```

The Workspace repository is never read only: the read only columns do not apply to it. The archived rule does.

Changes treats a repository on a tag like a read only one. A tag checkout has no branch, so a commit there would leave the tag behind on a detached HEAD that no push can send. Commit is refused ("Repository is on a tag. Checkout a branch before committing."), and Commit, Stage All, Unstage All and Undo across all repositories leave the repository out and name it; staging, unstaging and discarding that one repository still work. The page reads the tag state from the policy (`WorkspaceGitChangesRepositoryView.IsOnTag`, `IsLocked` for read only or on a tag).

The buttons of a row ask the same table. In a Feature the commits badge of a read only snapshot is off, because a snapshot allows neither pull nor push; a repository on a tag has it off for the same reason. A branch without an upstream can become read only when its commit is on the remote.

Messages: "Repository is on a tag; checkout a branch first.", "Repository is pinned to a tag. Checkout a branch before pushing.", "Repository is on a tag. Checkout a branch before committing.", "Repository is read only.", "Repository is a read only snapshot in this Feature.", "Repository is archived on the provider. Pushes are rejected.". A bulk action reports its skips in one sentence built by `RepositorySkipSummary` (read only, tag and archived repositories counted apart); skips are shown as information and never in place of an error or a warning. The REST responses carry them in `Skipped` with the reason by name.

Read only is set in Edit Workspace's Lock tab ("Repository Lock": Lock All, Unlock All, "New repositories are locked as read only") and with Lock / Unlock in the Switch Branch dialog; both call `IWorkspaceReferenceOperations` (`SetAllAsync`, `SetRepositoryAsync`, `SetDefaultAsync`). Lock All and Unlock All apply immediately and are not part of Save. Enter in the Workspace dialog does not save while a lock confirmation is open. Turning it on is refused, for every selected repository, when one of them has unpushed commits or an unreadable upstream state in the Workspace checkout or in a Feature worktree; uncommitted changes are a warning. The search box takes `ro:yes` and `ro:no`; the Workspaces list shows None, Some or All; the definition banner names repositories whose read only state differs from `graymoon.json`; the Restore Workspace preview has a Read only row.

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
restore packages (starts unticked; when unticked, the push does not restore locally)
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

A repository where the branch was already there passes the gate (it is on the branch), but the branch was not created from the chosen base. The run goes on, the orchestrator logs it, and `PrepareWorkspaceResult.BranchAlreadyExisted` names those repositories: the page shows the warning of New Branch (above) and REST returns `branchAlreadyExisted`.

Update and push (both options) runs through the same shared combined operation as the Repositories page and the popup (`IWorkspaceUpdateAndPushOperations`, origin `PrepareWorkspace`): the two-lane pipeline when possible, otherwise the sequential update followed by a synchronized push. The dialog's Restore packages choice is `RestorePackages` on that run. The box starts unticked, and an unticked box does not restore during the combined run or a later normal push. Push only uses the same choice. When a synchronized push is not possible, the run stops after the update with "Continue with normal push?" (the page asks; REST returns 409 with a `pendingApprovalId`) and pushes only after the user agrees. Update only and Push only stay standalone. `PrepareWorkspaceResult` carries `BranchesCreated`, `BranchErrors`, `Update`, `Combined` and `PushPending` (Push only). REST `/prepare-workspace` returns the same stop message. REST `POST /api/workspaces/{id}/update-and-push` runs the same operation for the Workspace context (200 done or nothing to push, 409 approval required or refused, 400 failed) and `POST .../update-and-push/approve` with `pendingApprovalId` continues a normal push.

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

While dependencies are unmatched and nothing is incoming, the header shows a yellow **Push Updated** split button instead of Update and Push (`DetermineUpdateControl`; menu: Level N Only, Update Only, Update Files, Push Only, and Undo Push Commits when a push is recommended). The header of that level N in the grid also shows a small yellow **Push Updated** button in front of its repository count (`WorkspaceRepositoriesLevelHeader.DetermineShowsPushUpdated`); it runs Level N Only and is disabled by the same rule as the header button (`DetermineUpdateDisabled`). No other level shows it, and it is absent while the header shows Update.

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

### Effective versions

Producers are compared through their **effective version**, not the calculated GitVersion. A repository whose branch GrayMoon created and that still sits on that branch point reuses the version its parent branch already published (data in `03-data-model-and-persistence.md`, section 22). `EffectiveVersionResolver` decides per repository and context:

| Kind | When |
|---|---|
| `InheritedVerified` | allowed by `RepositoryOperationPolicy` (Push), a recorded branch point, `HeadCommit` equals it, at least one required package, the Worker's `GetGitVersionAtBranchPoint` says the commit is on `origin/<parent>` and returns the parent's version there, and every required package is `Available` at that version on its mapped connector |
| `InheritedUnverified` | the same first conditions, no required packages, and at least one `{@Repo}` version-file token uses its version (a service image stamped in a compose `.env`, for example): there is no package to look up, so it inherits the parent version without a registry call (reason `TokenConsumersOnly`) |
| `Own` | everything deterministic that is not inheritance: protected by policy, no branch point, not on it, no version consumer at all (or no GitVersion in the profile), not on the remote parent, parent version not computable, or a required package `VersionNotFound` (also: no registry has it after a registry sync that answered) |
| `Unresolved` | on the branch point and otherwise eligible, but a lookup answered with anything else (401/403, timeout, 5xx, malformed, a failed registry sync); also a read only snapshot whose source branch version cannot be calculated while something uses its version |

**Read only snapshots.** A Feature holds a read only repository as a detached worktree (`IsReferenceSnapshot`). GitVersion on a detached HEAD versions "(no branch)", not the branch the commit was taken from: a commit three commits after tag `1.2.0` on `main` is `1.2.1-main.3` in the Workspace and `1.2.0` in the snapshot. A snapshot is therefore never `Own` because of policy. When it was taken from a branch (`SourceBranchName`), its effective version is that branch's version at the snapshot's `HeadCommit`, asked from the Worker with the same `GetGitVersionAtBranchPoint` (`RepositoryBranchBaselineReader.LoadReferenceSnapshotSourceBranchesAsync` gives the branch): `InheritedUnverified`, reason `ReferenceSnapshot`, with or without a version consumer, and without a registry lookup (a snapshot never publishes, and the Workspace uses that version without a lookup too). When the Worker gives no version (the commit is not on `origin/<source>`, or GitVersion fails) and a repository in the Feature uses the snapshot's version, it is `Unresolved` and operations stop with "Cannot calculate the version of the read only snapshot {repository}: ... Nothing was changed."; when nothing uses it, it is `Own` with reason `BaselineNotOnRemoteParent` or `ParentVersionUnavailable`. A snapshot taken from a tag or from a detached Source has no source branch and stays `Own` (`PolicyProtected`): its detached GitVersion is what the Workspace calculates too. The stored version is reused for the same HEAD like any inherited decision; a decision stored before this rule (`PolicyProtected`) is replaced on the next refresh.

Required packages are the package ids of the repository (physical and generated, `PackageId` else `ProjectName`) that csproj consumers in the context reference; version consumers also include `{@Repo}` version-file tokens. `PackageAvailabilityVerifier` deduplicates lookups per (connector, package, version), runs them bounded by `MaxParallelOperations`, and caches only `Available` for 2 minutes (`PackageAvailabilityCache`, injected `TimeProvider`); a fresh answer of any other kind clears the cached one.

It runs after every state write for a context (`WorkspaceStateRecomputeScope.RecomputeAsync`, which runs in the hook sync queue that "Applying pending repository changes..." waits for: a decision that came from a branch point check of the current HEAD for the same kind of consumers is kept without a Worker or registry call, anything else is checked with cached answers; a failure is logged and leaves the stored decisions), after each update level's version refresh for the repositories that committed (they left the branch point, so no Worker or registry call), and at the start of an operation with fresh answers (`EffectiveVersionStartCheck`): Update, Update and Push (both lanes and the sequential path run the same update) and Prepare Workspace check after reading the project files and before the first level; bulk Push checks in `WorkspacePushOperations.GetPlanForLinksAsync`. When a repository in scope is `Unresolved` the operation stops before any change with "Cannot verify the published package {id} {version} for {repository}: connector {name} returned HTTP 403. Fix the connector and run again. Nothing was changed." (`WorkspacePushPlan.StopMessage` for Push). A repository that must publish because a package is missing is reported as "{repository}: package {id} {version} is not published; publishing {own version} instead." Push of one named repository does not run the check.

A consumer that references the inherited version gets no update and no unmatched badge; a real mismatch (an older pin) is still rewritten. `{@Repo}` resolves to the effective version, `{@Repo:branch}` and `{@Repo:commit}` are unchanged, and a rewrite is a real commit, after which the repository has its own version. A file that stamps its own repository's `{@Repo}` gets no special rule: it is compared with the effective version while the repository inherits, and with the calculated GitVersion after a commit.

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

Restore also runs in `PushOrchestrator` after each level's push. `DependencyUpdateOrchestrator` never restores. A restore that fails after a pushed level does not fail the push: when it is the only problem, the run ends as done with a warning (`OperationResult.Warnings`, "Push finished, but package restore failed in N repositories.", shown with `IToastService.ShowWarning`: amber, 6 seconds like an error), and the repository keeps its restore error in the grid. Next to a real push failure it stays part of the failed run.

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

### Which repositories a bulk push selects

`WorkspaceRepository.GetRepositoryIdsNeedingPushAsync` (called by the push plan, the package wait and the push lane, which adds what the update committed) decides from the context's state row, with no Worker call:

| # | Condition | Result |
|---|---|---|
| 1 | Not allowed by `RepositoryOperationPolicy` | not pushed (policy skip) |
| 2 | Outgoing commits | push |
| 3 | `BranchHasUpstream` is not `false` | not pushed |
| 4 | No upstream, and no recorded branch point or `HeadCommit` is not on it | push (the rule before effective versions) |
| 5 | No upstream, on the branch point, inheritance valid for this HEAD | not pushed: "Skipped push of {repository}: no commits, required packages verified at {version}." (`InheritedUnverified`: "Skipped push of {repository}: no commits, version files use the parent version {version}.") |
| 6 | No upstream, on the branch point, `Own` for this HEAD, no version consumer | not pushed: "Skipped push of {repository}: no commits, and no repository in this context uses its version." |
| 7 | No upstream, on the branch point, `Own`, a version consumer | push: it must publish |

The Repositories grid Version cell shows the effective version when the inheritance is valid (`WorkspaceRepositoryLink.IsVersionInherited`), in `.version-inner--inherited` (the theme's secondary text color, a little brighter than the muted calculated version), titled "Inherited published version {effective} from {parent branch} at {short SHA}. Calculated GitVersion: {calculated}. Click to copy effective version." (for `InheritedUnverified`: "Inherited version ... (used by version files only, no package to check). ..."; for a read only snapshot: "Version {effective} of {source branch} at {short SHA}, the commit this read only snapshot stays at. GitVersion of the detached checkout: {calculated}. Click to copy version.") Otherwise it shows the calculated GitVersion as before; for `Unresolved` the title adds the reason. Click copies the displayed value, and the clicked state is keyed by repository and displayed value (`WorkspaceRepositories.VersionClickKey`). REST and other read models keep `GitVersion`.

An unresolved, missing or stale decision gets row 4's result. Rows 5 and 6 are "no work" (`WorkspacePushPlan.NoWork`, `GetPushNoWorkReasonsAsync`), not policy skips. A consumer that references an inherited version passes the package wait on its first lookup. A push of one repository (`PushSingleAsync`) always pushes, subject to policy. Because a skipped branch has no commits, no pull request can exist for it.

### What the overlay shows while the plan is built

The Push button builds its plan inside the page job (`WorkspaceRepositories.OnPushClickAsync` -> `PreparePushAsync`, scopes of its own through `ScopedExecutor`), so the loading overlay is up from the click and Abort stops the plan ("Push cancelled."). `IWorkspacePushOperations.GetPlanAsync` takes an optional `IProgress<OperationProgress>` and reports "Checking versions..." (`WorkspacePushOperations.CheckingVersionsMessage`), then "Checked {x} of {y} repositories..." (`EffectiveVersionResolver.CheckedMessage`) as each branch point GitVersion run on the Worker finishes; Update shows the same count. Nothing is prepared while it runs: the plan only checks archived state, effective versions and the selection. No repository names and no other steps are shown. A repository whose stored decision is inherited for its current HEAD keeps that parent version even at an operation start (`bypassCache`): the parent version at a fixed commit does not change, so the Worker is not asked and it is not counted; only the registry is asked again. A Push with nothing new therefore makes registry calls and no GitVersion runs. With progress the resolver also writes one line per repository to the job's overlay terminal (label `versions`, `EffectiveVersionResolver.TerminalLine`): "{repository}: uses the published parent version {version} (...)", "uses the parent version" for `InheritedUnverified`, "own version {version} (...)" with the reason, and an unverifiable package as a red (stderr) line. The background refresh passes no progress and writes nothing. Without the synchronized-push dialog the same job goes on into the push. When the dialog is needed the job ends and the dialog opens with the plan; Proceed starts the push job without building the plan again. A stop message is a toast when the job ends, and so is "No repositories to push.", which counts the "no work" branches in one sentence instead of listing them (`WorkspaceRepositories.NothingToPushMessage`; each one's sentence is in the log). Prepare Workspace's push stage, the notification panel's push and Update and Push report the same plan steps; Update's own start check reports the counted step too.

### When Push asks for attention

`RepositoryOperationPolicyExpressions.HasCommitsToPush(outgoingCommits)` is the one rule: a repository asks for a push only when it has outgoing commits. The header's yellow Push button (`IsPushRecommended`, the four header queries of `WorkspaceRepositoryLinkListQueryService`, expanded by `ExpandRepositoryPolicy`), the yellow commits badge (`CommitsBadge.DetermineColors`) and the notification panel (`WorkspacePendingActionsService`) all use it, after the Push policy. A branch without an upstream and without commits does not count; its commits badge is the neutral gray cloud (`--badge-neutral-bg`, the gray of the PR "none" and dependencies "none" badges). Push stays available as the plain button and still pushes such a branch when the selection table above says so (rows 4 and 7). Clicking the gray cloud badge first asks to confirm (`WorkspaceRepositories.UpstreamOnlyPushMessage`: no commits were made, push the branch anyway to set its upstream?); Set upstream goes on with the usual single-repository push.

### Synchronized push

Uses dependency levels and package registry waiting (.NET Dependency). A Basic Workspace pushes in parallel with no levels, registry wait or restore (`WorkspacePushStrategySelector` -> `BasicGitPushStrategy` / `DotNetDependencyPushStrategy`).

### Update and Push (two lanes)

`UpdateAndPushOrchestrator` runs the update lane (`DependencyUpdateOrchestrator`, raising `onLevelCompleted` after every level, including levels with nothing to update) and the push lane (`WorkspacePushService.RunPushLaneAsync`, own DI scope, plus a fresh scope and DbContext for every level it receives, so it never reads back entities it tracked before the update rewrote them) connected by a single-reader channel, so level N is pushed while level N+1 is being updated and a level's CI/package wait overlaps later updates. Before each level, the lane matches registries only for required package ids that still have none, so a package created by an earlier level of this run is found after that level is pushed. `TwoLaneProgress` shows whichever lane reported last in the normal overlay. When `CanRunPushLaneAsync` fails (an unmatched package whose producer is not in this run, or no registry is reachable) nothing runs and the page falls back to the sequential update-then-push with its "Continue with normal push?" prompt. On an update exception the channel is completed, the push lane settles, and the exception is rethrown.

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

The Worker (`UndoPushCommand`, `GitService.ResetToRemoteAsync`) runs `git reset --mixed` (keep changes) or `--hard` to `origin/<branch>`. When that ref does not exist because the branch was never pushed, the target is `git merge-base <compare ref> HEAD`, where the compare ref is the one the outgoing count uses (`IGitRepositoryReader.GetNoUpstreamCompareRefAsync`: the Feature divergence base as a local branch, otherwise the default origin branch). With no compare ref or no common commit the undo fails and changes nothing. The command never pushes or fetches, so the request carries no connector token and `ResetToRemoteAsync` takes none. The Undo Push Commits dialog says the same: a branch that is on the remote goes back to its `origin` branch, a branch that was never pushed goes back to where it left its base branch, and nothing is pushed.

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
tag checkout (excluded - never mutated, does not block the plan)
ahead of default
upstream
PR merged/closed/open
blocking reason
whether explicit discard confirmation is needed
```

### Execution

`ExecuteReturnToDefaultAsync` receives explicit `ReturnToDefaultOptions`. Repositories on a tag are skipped (untouched), not reported as errors.

### A branch that could not be deleted

The Worker (`ReturnToDefaultBranchCommand`) deletes the remote branch (when asked) before the fetch and the local branch after the checkout. A delete that fails does not stop the return: the repository still ends on the default branch and is pulled. The failure is reported, not only logged: the response carries `remoteBranchDeleteError` and `localBranchDeleteError`, and `WorkspaceGitService.ReturnToDefaultDirectAsync` returns a `ReturnToDefaultOutcome` whose `Warning` is one sentence, for example "graymoon-api: returned to the default branch, but the remote branch 'feature/x' was not deleted (remote: Permission denied)." (`ReturnToDefaultOutcome.DescribeBranchDeleteFailure`).

Every caller passes it on as a warning of a run that succeeded:

```text
ExecuteReturnToDefaultAsync          OperationResult.Warnings + WarningSummary
ReturnToDefaultUnattendedAsync       UnattendedReturnToDefaultResult.Warnings
ReturnToDefaultUnattendedBatchAsync  UnattendedReturnToDefaultRepositoryResult.Warning, batch.Warnings
IWorkspaceBranchOperations
  .ReturnToDefaultAsync (REST)       ReturnToDefaultApiResult.warning
```

The Repositories page shows an amber warning toast (`ToastService.ShowWarning`) after a single, level or all-repositories return, after a merge that returns, and after a bulk merge whose rows all succeeded; several warnings are summed up by `ReturnToDefaultWarnings.Summarize`. When a run also has a failed repository, the failure is what is shown and the warnings go to the App log. Test anchors: `ReturnToDefaultBranchCommandTests`, `ReturnToDefaultUnattendedBatchTests`.

### UI policy

Single-repository (the yellow Return to {branch} button in the Branch dialog), level, and all-repositories flows run analysis under the page loading overlay, including the command terminal, then render the appropriate existing GrayMoon confirmation UI. When that single repository needs no confirmation, the return continues on the same overlay.

### Unattended policy

REST/post-merge flows call the same analysis first and abort safely when analysis/PR refresh is insufficient. The unattended policy deletes the remote branch only when there is an upstream, force-deletes the local branch and never closes PRs. Bulk merge uses the batch form (section 6); single-repository callers keep abort-all behavior (`ReturnToDefaultUnattendedAsync`). Both results name the repositories the policy left out (on a tag, read only) in `Skipped`, as `ExecuteReturnToDefaultAsync` does; the page shows them as information when there is no error and no warning to show.

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

Package-producing projects and matched registry, plus a **Used by** count: the other workspace repositories that restore the package in the selected context. That includes repositories that get it transitively through another workspace package, because `dotnet restore` downloads it there too. The package's own repository is never counted. Before the selected context has been synced the cell is empty. After a sync, a package with no referencing repositories shows 0 in a darker badge. When several projects publish the same package id, Used by still lists those repositories on each of them. Dependencies and push order leave that package out until one publisher remains; the next sync of a referencing repository then stores the normal dependency and removes the extra list. Hovering the count lists the repositories, marking transitive ones "via <package>", with a copy button. The count is gray whether or not it opens GitHub settings. The color does not report Actions access.

A **Needs** count on the same row lists the other workspace packages that projects in this package's repository restore, including packages reached through another workspace package, because `dotnet restore` downloads those too. Other packages published by that package's repository are included as well, marked via that package. Packages that repository itself publishes are left out, and every package row from that repository shows the same list. Before the selected context has been synced the cell is empty. After a sync, a repository that restores no other workspace package shows 0 in a darker badge. Hovering the count lists the packages, marking transitive ones "via <package>", with a copy button. A package name opens that package's settings on GitHub when it is a GitHub package. The note under the list says to open a package, or click the badge to open them all. Clicking the badge opens each of those settings pages. The badge stays gray, and the color does not report Actions access.

### User can

Synchronize registry matching. When the package is on GitHub Packages, clicking the count opens the package settings page on GitHub (`/orgs/{owner}/packages/nuget/{id}/settings`; GitHub serves this route for user-owned packages too). The user adds the listed repositories there under "Manage Actions access", which `GITHUB_TOKEN` restores in GitHub Actions need for private packages. GitHub has no public API for that list, so GrayMoon neither reads nor changes it.

### Implementation

```text
WorkspacePackages page
WorkspaceProjectRepository package queries
  GetPackageDependentRepositoriesAsync / ResolvePackageDependentRepositories (Used by)
  GetPackageAccessNeedsAsync / ResolvePackageAccessNeeds (Needs)
  AmbiguousPackageReferences (consumers of a package id published by several projects; not a graph edge)
RepositoryUrlHelper.GetGitHubNuGetPackageSettingsUrl
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
a folder with a definition is imported with Import from disk (MergeVersionFilesIntoDatabaseAsync)
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

There is one primary Commit button: **Commit Staged** when any repository has staged content, otherwise **Commit All** (`WorkspaceCommitModeHelper.ResolveOfferedMode`). The keyed alternatives carry the captured mode, so a watcher refresh that flips the label mid-click cannot commit the wrong set. The button is joined to an AI provider segment and a chevron menu (`[Commit Staged|All][provider][chevron]`). The menu's **On Commit** header holds **Generate commit message** and **Push committed**, both remembered per Workspace across a reload (F5 or Ctrl+F5) until the App restarts. **Generate commit message** starts ticked until you change it; **Push committed** starts unticked. Only providers the Worker can run are listed above that header; when none are available the divider above it is omitted. Ctrl/Cmd+Enter targets only the primary commit element, never the AI segment or the chevron. While the message box is empty the control attaches to the right edge of the box; below about 20rem it stays under the box.

The click fixes the mode, the message source and what the default-branch dialog confirmed. The repositories are decided when the commit job starts, after the dialog and after the AI wrote the message: the job re-reads the context's persisted Changes state and `WorkspaceCommitTargetPlanner` plans the run. A repository that got changes after the click is committed too and the terminal names it (and says the AI message does not describe it); a repository of the click whose counts are now zero is still sent and comes back as skipped ("no changes to commit"); a repository now on its default branch that the user did not confirm is not committed and is reported as skipped with that reason; a repository that left the context is reported as skipped. When the re-read fails, the repositories of the click are committed and the terminal says so. Test anchor: `WorkspaceCommitTargetPlannerTests`.

Commit All stages with `git add --all` and then checks that the working tree has nothing left unstaged. If files are still settling it tries again, twice, pausing 250 ms and then 750 ms. A submodule whose own files are modified or untracked cannot be staged from the parent, so that case fails at once and the message names the submodule; a submodule that only points at a new commit is staged with the rest.

Commit, Stage All, Unstage All and the multi-selection Stage/Unstage fan out over the repositories in parallel (bounded by `MaxParallelRepositoryOperations`) and are not atomic. Every repository records exactly one result (`RepositoryOutcomes`): committed or staged, skipped (nothing to commit), or failed with a reason. A repository that records nothing is reported as failed, never dropped. The toast, the job terminal and one `Workspace commit finished` log line carry the same per-repository summary. The commit message is cleared only when no repository failed; if any failed it stays in the box and in memory. `WorkspaceGitChangesOperations` reads what it needs through a short-lived `AppDbContext` per call, because the page calls it in parallel on one scoped instance and a single EF Core context allows only one operation at a time.

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

`.md`, `.markdown`, `.mdown`, `.mkdn`, `.mkd`, `.mdwn`, `.mkdown`, `.mdtxt`, `.mdtext`, `.markdn`, and `.mdc` (Cursor project rules) open on Preview, with Source still available as the Monaco diff. Other extensions stay on the Monaco diff only.

Preview images are embedded by the App as data URIs (`MarkdownImageEmbedder`), because the page has no access to the repository files and no GitHub session. Repository-relative images are read through the Worker; remote `http(s)` images are downloaded by the App from any host. Arbitrary hosts are a deliberate choice (README badges and screenshots live on CDNs), bounded instead by size and count: at most 24 images per document, 2 MB per remote image, 256 KB per SVG, and an SVG with script or event handlers is not embedded. The `src` is read out of rendered HTML, so it is HTML-decoded for the request or the Worker path (`a.svg?x=1&amp;y=2` is requested as `?x=1&y=2`) and matched in its escaped form only when the output is rewritten. A remote image that could not be embedded becomes a small text chip with its `alt` text.

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

### Inventory and tree (current behavior)

Opening `/workspaces/{id}/changes` reads SQLite only (`IWorkspaceGitChangesReadService`) - never `GetGitChangeStatus` for the initial paint. Small inventories hydrate fully and open expanded; larger ones (above `FullHydrationMaxEntries`) open summary-first with repositories collapsed and load folder children on expand via `GetEntryPageAsync`. `GitChangesTree.razor` uses `Virtualize`. Live `ContextGitChangesUpdated` patches the affected repository only, and does nothing when the content digest is unchanged. The filter runs in-circuit in Full mode and as a paged server-side search in Summary mode ("N matches", more pages load on scroll). End-to-end detail: `07-git-changes-projection-and-tree.md`.

---

## 22. Git Changes watcher behavior

Opening relevant Workspace pages activates watcher leases.

File edits from IDEs trigger Worker-side filesystem events.

Events are debounced and result in new snapshots.

Watchers eventually expire after inactivity.

Explicit refresh requests a real status rescan.

---

## 22A. Forked repositories

A repository that is a fork shows a teal **Fork** badge in Workspace Repositories (clickable, opens the original repository), Repositories and the repository modal. Pull request lookup for a fork checks the fork (`head=forkOwner:branch`) and the upstream repository. One found PR shows one badge, as for any repository; PRs in both show two badges, the second labelled with the upstream repository. A PR that exists only upstream is link-only: GrayMoon does not merge, close, edit or review it, and it is not an "open PR" for merge actions, but it counts as an existing PR (Create PR is not offered) and an open upstream PR blocks "all Feature PRs completed". Create PR always creates the PR inside the fork.

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

`NewPullRequestModal` uses one layout for one or many repositories: tabs **Details** (title, description, **Draft pull request**), **Branch** (one row per repository: head branch and target picker, plus a **Refresh** link) and **Review** (optional reviewers with a filter), with **Create** and **Push & Create**. Each row of a target menu has **Apply all**, which sets that branch on every repository whose candidates include it and whose head is not that branch; the others keep their target. The open menu is a top-layer popover placed on the target cell (`newPullRequestModal.placeOpenTargetMenu`), so the repository list keeps its own scroll and the dialog does not grow to fit the menu. The AI provider selector sits at the right of the tab bar (section 34) and writes one title and one Markdown description shared by every PR in the dialog.

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
Apply all        NewPullRequestTargetBranch.BaseToApply: the clicked branch, in that repository's own
                 spelling, when it is a candidate and not the head; otherwise the row is left unchanged
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
run/re-run one dependency level or the Workspace repository
open logs
```

Each repository row shows the runs of one branch, its CI branch (`UnpublishedBranchRules.CiBranch`, resolved per row from the snapshot of the viewed context by `WorkspaceActions.ResolveCiBranch`). It is the checked-out branch, except while that branch has no upstream and no commits (the gray cloud badge): GitHub has never run anything on it, so the row shows, refreshes, re-runs and starts the workflows of the recorded parent branch (the Feature row or the branch baseline, without `origin/`), and the Branch column shows that parent with an arrow icon and a tooltip naming both branches. A branch with no recorded parent stays on its own branch. Once the branch has a commit or an upstream the row switches to it on the next sync.

In a .NET Dependency Workspace the grid is grouped like the Repositories grid: the Workspace repository first (when it has a connector), then `Level N` from the highest level down, then `No dependencies`. A Feature uses its own levels. Inside a group the order is unchanged (errors, failed, aborted, running, success, then name), so a running workflow moves to the top of its own level and never leaves it; each header appears once. A group whose rows are all hidden by the status chips or the search is not rendered, header included. A Basic Workspace keeps the flat list with no headers.

Each group header shows, right after its title, how many workflows of the group failed or are running (there is no dependency-graph link here), and ends with "N repositories" (all repositories of the group, hidden or not). Hovering it opens a menu:

- **Run** starts every workflow of the group that supports `workflow_dispatch` and is not already running, on the CI branch of each repository (above). It is disabled when there is none.
- **Re-run** is listed only when the group has failed workflows, and re-runs each of them.
- **Abort** cancels every running workflow of the group on GitHub. It is disabled when nothing in the group is running. Read only and tag-pinned repositories are included, as with Abort on a single row; the overlay then waits until the runs stop showing as running.

All three ignore the status chips and the search and respect the AI toggle. Run and Re-run leave out read only and tag-pinned repositories (`RepositoryOperationPolicy`, bulk scope). All workflows of the group start at once; GrayMoon does not wait for one level before the next. The menu is disabled during a refresh or another bulk action. "Re-run Failed Jobs Only" and "Run again" stay on the page header button and act on the whole grid.

Every bulk action (page header or group menu) has two phases under the loading overlay. First the workflows are started ("Starting 3 of 8"). Then the overlay waits for GitHub to list the runs ("Waiting for actions to start...", or "Waiting for actions to stop..." after Abort, then "Waiting for 3 of 8" once 3 of the 8 are listed), refreshing only the started repositories, for up to about 30 seconds, so the grid shows them running and the live monitoring is on when the overlay closes. Abort ends only this wait: nothing that was started is cancelled, and GrayMoon keeps looking for the missing runs out of sight. The action ends with one toast, for example "Started 7 of 8 workflows in Level 2. Failed: Api.", with "GitHub does not show N of them yet." added when the wait ran out. A workflow that could not be started shows `error` with GitHub's message on its repository row; the others still start.

Last Run shows when that workflow last updated ("2 min ago", or the date and time after a week), and the wall-clock length of the run in a smaller gray font aligned to the right of the cell (for example "4m 12s"). When the column is too narrow for both, the relative time ellipsizes and the length stays visible. GitHub's run list has no duration field. The length is `updated_at` minus `run_started_at` once the run has finished, and now minus `run_started_at` while it is still running. A run that has not started yet shows only the relative time. Billable runner time is not requested.



### Status categories

```text
error
failed
running
aborted
success
none
```

The success chip starts on. When the page loads, or a refresh updates the grid, and any workflow is running, success turns off so those rows are hidden. Clicking the chip shows them again for as long as something is running; when nothing is running, success turns back on, and the next run hides it again. A refresh that is still in progress does not turn success back on until it finishes. Turning the chip off while nothing is running leaves success hidden until the next click. The other chips stay as the user set them.

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

The popup lists at most 100 repositories, but its actions (Update, Push, Pull, Undo Push Commits) and the default-branch warning in front of Update and Push work on every repository of the selected context, read from that context's own branch, tag and counters. The context is fixed when the button is pressed. Pull with nothing left to pull reports "Nothing to pull."; an action whose repositories cannot be loaded reports "<action> could not be started." and starts nothing.

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

Limits: a query is at most 1000 characters (`FilterSearchExpression.MaxQueryLength`, the box's `maxlength`) and parentheses nest at most 64 deep (`MaxNestingDepth`); deeper nesting is a syntax error, like a missing `)`.

### The filter box

`FilterSearchInput` is the one search box of every grid. The browser owns it while the user types: `wwwroot/js/filterSearchInput.js` colors `and`, `or`, parentheses and `field:value`, turns the text red when the syntax is invalid, shows the clear button and follows the horizontal scroll, all in the same task as the keystroke. No keystroke is sent to the server. The typed text reaches the server (`ValueChanged`) after 150 ms without a keystroke, and at once for Escape, Enter (`OnCommandKeyDown`, raised for these two keys only, after the text on screen was committed) and the clear button.

The component's markup is constant: it renders the input's `value` once and never renders the colored copy or `data-filter-state` (`empty`, `valid`, `invalid`, set by the script on the wrapper and read by the stylesheet). A value changed by the page (Escape clears the filter) is handed to the script, not rendered. A render that arrives late can therefore never overwrite what was typed since.

The script has its own copy of the grammar (one pass, no allocation per token) because validity must be known without a round trip. `FilterSearchInputScriptTests` runs the script in a JavaScript engine against `FilterSearchExpression`, so the two cannot drift: change the parser and the script together.

---

## 30. Desktop integration

When running in Desktop mode, GrayMoon may provide native actions such as opening a repository folder or external URL.

The context selector also shows an **Open in...** flyout for the selected context: Cursor, Claude CLI, Codex CLI, VS Code and Visual Studio (each only when Desktop detects it), Terminal and Explorer, with a per-repository sub-flyout. Each tool can be added to or removed from the button row next to the selector (persisted per Workspace); using a tool does not add it. The button row stays usable while the selector itself is disabled (a running job, or a page load): the selector trigger and New Feature follow that block, but launching a tool only posts a path to Desktop and takes no Workspace lock, so only the tool just clicked locks itself until its "Starting..." toast closes. The Open in... flyout lives inside the dropdown, so it is reachable only while the selector is enabled; pin a tool to keep it available during a job. The App resolves the paths (`IWorkspaceNativeLaunchService`); Desktop only launches the tool. Cursor, Claude CLI and Codex CLI (`OpenInTool.AgentScope`) are also posted with the environment variables listed for that agent and for All on the Worker page (Environment tab); VS Code, Visual Studio, Terminal and Explorer start with an unchanged environment, and so does an AI tool with nothing listed. The tools render only when this client's WebView bridge (window.graymoonDesktop) answers; Desktop:PipeName is process-wide and only triggers the installed-tool scan, so a browser tab on the Desktop-launched App never shows them.

The web/container application remains fully functional without Desktop.

---

## 31. Current intentional limitations

There is no MCP server.

Feature limitations in the current code:

```text
Features can only be based on the current Workspace (no "default branches" or "from another Feature" base)
every Workspace repository always gets a worktree (no repository picker)
one branch per repository per Feature (the Feature name); no adopting an existing branch
Workspace repository membership, rename and root change are blocked while Features exist.
  The Repositories dialog explains that a Feature keeps a worktree for every repository from
  when it was created, so the list cannot change until the Feature is removed
REST endpoints always act on the special Workspace context; there are no Feature endpoints
```

Those are future architecture projects and must not be described as current product behavior in this documentation.

### Known gaps

```text
"Versions from default" (section 32) needs the local default branch to contain the origin/<default> tip:
  GitVersion versions the local branch, so a default branch that is behind fails with "Pull <default>"
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
open Workspace settings (Edit..., the last item in the context menu; a divider between Open in... and Edit... when Open in... is shown, and one divider above Edit... when it is not)
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
reject a name that nests with an existing Feature (NestedFeatureName: "team" vs "team/login", either direction, case-insensitive)
finish a pending-delete folder of the same name first
reject the name when its folder already exists on disk (FeatureFolderExists; any file, folder or link, checked read only)
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
optional: start the CodeGraph index build when New Feature's "Create CodeGraph index" is ticked
  (shown when the CodeGraph CLI is installed, including with no Workspace repository and no graymoon.json;
  hidden when graymoon.json has "codegraph": false; ticked by default;
  overlay offers Background instead of Abort)
```

Uncommitted Workspace changes are not copied. If any repository fails, or the create is aborted after the intent is committed, the Feature is kept in **NeedsRepair** with the error. An Abort before that creates nothing: the New feature dialog stays open and a "New feature was cancelled." toast is shown, not an error.

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

GrayMoon never looks for or ends the programs using a Feature (other than CodeGraph's own index processes). When files are still in use, the Feature is removed anyway and its folder is left with a `GRAYMOON-PENDING-DELETE.md` marker (readable by a person or an AI agent working there); the dialog result shows this as information, not a warning. The folder is deleted silently later: when the Worker connects (every GrayMoon start), when the Workspace is opened (at most every 15 minutes, skipped while the Workspace is structurally busy), or before a new Feature reuses the name. While a folder is still in use, GrayMoon also retries it every 30 minutes; this timer only runs after a removal left a folder behind and stops once nothing is left. Only marked folders under the Workspace's managed Feature storage root are deleted, never one that holds a Git repository, a still-registered worktree, or a Feature that exists.

### Versions from default

Inside a Feature, merge the deepest PRs first, then Sync: for repositories whose Feature PR is merged, `WorkspaceGitService.ApplyDefaultTipVersionsForMergedFeatureReposAsync` runs Worker `GetGitVersionAtDefaultTip` (GitVersion at the `origin/<default>` tip, no checkout) with `/b <default> /c <sha> /nocache`; the answer counts only when GitVersion versioned the default branch at that commit, so a local default branch that is behind `origin/<default>` gives a failure with "Pull <default> and try again" instead of another version) and replaces the context GitVersion, so Update Dependencies writes default-branch versions into higher levels. Only when the profile uses repository versioning. This is why per-level Return to Default is not needed in a Feature (see the known gap in section 31).

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
  the Origin's host is a loopback name (localhost, 127.0.0.1, ::1, 0.0.0.0) or a listed host (below)
  and the request also carries X-GrayMoon-Request: 1
    (a cross-site page cannot add that header without a CORS preflight, which GrayMoon does not allow)
GET requests are not affected by the Origin rule; the request host rule below covers them
```

### SignalR hubs

```text
/hub/worker (and the legacy /hub/agent alias): any request carrying an Origin header is rejected (403); the Worker's .NET client sends none
/hubs/workspace-sync, /hubs/desktop, /_blazor: a request with no Origin header passes;
  one with an Origin header passes only when its host is a loopback name or a listed host
```

### Listed hosts (`GRAYMOON_AllowedHosts`)

```text
listed hosts = the AllowedHosts setting (';' separated; set with the GRAYMOON_AllowedHosts environment variable)
               plus the optional Security:AllowedOrigins entries
a bare * is not a host and is ignored; *.example.com matches any subdomain
empty list   -> only loopback Origins and loopback request hosts are accepted, and the App logs one
                warning at startup that names GRAYMOON_AllowedHosts
```

An Origin whose host equals the request's `Host` or `X-Forwarded-Host` header is **not** accepted on that
ground: under DNS rebinding the hostile page's own name arrives in both. A reverse-proxy or LAN host name
has to be listed.

- **Desktop** starts the App with `GRAYMOON_AllowedHosts=localhost;127.0.0.1;[::1]`.
- **Docker** ships no `AllowedHosts` value (`appsettings.json` has none), so a container works from a
  browser on the same machine (`http://localhost:8384`) and rejects browser pages served under any other
  name until that name is listed, for example `-e "GRAYMOON_AllowedHosts=localhost;nas.local;192.168.1.10"`.
  The same setting drives ASP.NET Core host filtering, so once it is set every name used to reach the App
  (including `localhost`, which a Worker on the Docker host uses) must be in the list.

### Request host (every path, every method)

```text
a request whose Host header is a loopback name or a listed host passes
a request with no Host header passes (no browser sends one)
/health and the Worker paths (/hub/worker, /hub/agent, /repos/{id}/connector,
  /workspaces/{id}/capabilities, /api/worker/pair) are not checked: a container probe and a Worker on
  another machine use names nobody lists, and the Worker paths require the Worker secret (below)
any other request is rejected (403) with a plain-text message that names GRAYMOON_AllowedHosts;
  the first rejection is logged as a warning with the host, later ones are not logged
```

This is what stops a DNS-rebinding page from reading data. Such a page is same-origin with GrayMoon, so
its GET requests carry no Origin header and the rules above never see them; the `Host` header still names
the hostile host. The check runs with an empty list too (loopback only), so pages and `GET /api/...` data
are not readable through a rebound name in any configuration.

Behind a reverse proxy, list the name the proxy sends as `Host` as well as the public name: a proxy that
replaces `Host` with its upstream name (the nginx default without `proxy_set_header Host $host`) is
otherwise rejected.

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
missing secret                    -> 401 (Security:RequireWorkerSecret defaults to true), from the first start
  explicit opt-out Security:RequireWorkerSecret=false:
                                     /hub/worker and /workspaces/{id}/capabilities accept a missing secret
                                     with a warning until some Worker has presented the correct secret
                                     (setting Security:WorkerSecretSeen), then 401
  /repos/{id}/connector            -> always 401 without the valid secret, whatever the setting says;
                                     the endpoint checks the secret itself as well as the middleware
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
sits outside the install folder so a Worker update keeps it. A Worker without a secret (installed before
this feature, or installed by hand without a pairing code) is refused with 401 and shows as offline; the
fix is to reinstall it from GrayMoon > Worker with a pairing code (Desktop: Install Worker). Setting
`Security:RequireWorkerSecret=false` (`GRAYMOON_Security__RequireWorkerSecret=false`) lets such a Worker
connect again with the "Reinstall the Worker to finish securing GrayMoon" notice, but it still cannot
fetch connector tokens, so remote git operations fail until it is paired.

### Self-update and hook listener hardening

```text
SelfUpdate rejects an installUrl that is not http(s) or contains ' " % ^ \ ` $ & | < > ; ( ) or CR/LF;
  it runs cmd.exe /c start "" /b powershell.exe ... irm '<url>' | iex (Windows only)
the Worker hook listener allows at most 64 in-flight requests (503 above that); a port bind failure
  gives an actionable error and fails startup
it answers 400 to a body that is not application/json or whose repositoryPath is not a managed
  working tree, and 413 to a body over 64 KB; nothing is queued for a rejected request
```

### Implementation

```text
RequestSecurityMiddleware
WorkerSecretMiddleware / WorkerSecretService / WorkerPairingService
SecurityOptions (Security:AllowedOrigins, Security:RequireWorkerSecret); AllowedHosts (GRAYMOON_AllowedHosts)
ConnectorEndpoints.IsPairedWorkerRequest
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
                  description shared by every PR in the dialog; when the branch name holds a ticket id
                  (ABC-123, the one the default title leads with) and the written title does not name it,
                  the ticket id is put in front of the title
both              pick an available provider in the menu (pins it for the Workspace on this machine; choosing the
                  provider that is already pinned removes it until another is pinned; never generates);
                  a CLI that is not installed is omitted; when none are available the Changes menu opens on On Commit
                  with no divider above it
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
PR text remembered   the New PR title and description are kept per Workspace and Feature context across closing
                     the dialog, leaving Repositories, F5 and Ctrl+F5 (not an App restart), until every PR of a
                     Create succeeds or both fields are emptied; closing the dialog does not stop a write, and its
                     text, suggestion or error shows when the dialog next opens for that context; Cancel stops it
PR comparison        each repository compares its head with the target selected in the Branch tab, never the
                     default branch; PR generation does not run in an overlay job
apply policy         AiTextApplyPolicy.Decide: text goes straight into the field only if the field was empty at
                     the click, has not been edited since and the scope is unchanged; otherwise it is offered as a
                     suggestion with Replace, Copy or Dismiss and the reason (edited, changes moved on, branch
                     selection changed)
all-or-nothing       if any repository cannot be compared (missing target ref, no merge base, no commits ahead,
                     conflicts), nothing is generated and the message names the repositories
failures             stay inside the AI control as one redacted red callout (not signed in, unsupported model,
                     unsupported CLI, timeout, invalid output); Commit and Create still work
recovered Cursor     when Cursor returns a verified success result but the Windows CLI exits nonzero, and the
exit                 App validates the commit/PR shape, the text is kept and an amber dismissible warning
                     callout appears instead of the red failure (role=status); dismiss clears only the
                     warning, not the text; Commit / Create are not blocked; a later normal generation for
                     that context clears the warning. Invalid or incomplete text still fails red with nothing
                     applied. Claude and Codex are unchanged. The Cursor launcher root cause is unproven
                     (installer/update state is a hypothesis only).
```

### Configuration

`graymoon.json` may hold `"ai": { "providers": [ { "provider": "claude|codex|cursor", "model": "..." } ] }`; it is file-owned (kept on every rewrite, ignored by drift) and Feature contexts read it from the special Workspace. Unknown providers are kept and shown as a notice; a duplicate or an unsafe model disables that provider. Default models: Claude `haiku`, Cursor `grok-4.7-low-fast`, Codex its CLI default; an explicit model wins. The pinned provider is `Workspaces.PinnedAiProviderId`, local only.

### Flow

```text
resolve provider and model, check availability (the startup GetHostInfo; menus do not probe); a provider
                            the Worker's host check does not list (older Worker) is filled in by HostInfoCache
                            as not installed, so it is shown as unavailable with a reason, never an exception
Worker CollectGitEvidence   one request for all repositories, bounded fan-out, never mutates or fetches;
                            PR: also every commit's subject and shortstat, oldest first (git log --reverse
                            --shortstat, no bodies), up to 200 per repository (60 above 10 repositories);
                            a longer branch keeps its oldest and newest halves and counts the middle
CommitHistoryCondenser      PR: folds fixup!/squash!/amend! into their commit, drops reverted pairs, merges a
                            subject committed in several repositories into one line, marks small low-signal
                            commits, redacts subjects, orders by committer time
AiPromptBuilder             one prompt under a hard budget (commit 4,000 / PR 8,000 estimated tokens),
                            secrets redacted, evidence wrapped as untrusted data (every piece of repository
                            text, including the commit prompt's recent subjects used as a style reference, is
                            redacted and cannot close the evidence block); PR splits it into headers
                            and facts (45%), history (20%, low-signal then small middle commits dropped first,
                            the first and last 10 kept longest) and excerpts (the rest)
Worker GenerateAiText       one CLI process in its own empty temp folder, no tools (Cursor: read-only ask mode),
                            timeout AiGeneration:TimeoutSeconds (default 120); Cursor may return success=true
                            with optional warning metadata (ProviderExitedAfterResult) when a verified success
                            envelope was recovered after a nonzero exit — never on timeout, cancel, start
                            failure or truncated stdout; the process gets the environment variables
                            listed for the provider and for All (Worker page, Environment tab)
App: known failure          when Cursor fails with "unable to get local issuer certificate" the Worker names
                            NODE_OPTIONS=--use-system-ca; the App stores it for Cursor (added to a
                            NODE_OPTIONS already listed, never a second row), reports "Added the environment
                            variable ... Trying again..." and sends GenerateAiText once more. The text then
                            shows a notice naming the variable; a second failure is the error, with the
                            same note. No other failure adds a variable or retries.
AiGeneratedTextParser       strict JSON check in the App; the result is never partial; a Worker warning is
                            attached to AiGenerationResult only after this validation succeeds
```

All three Worker commands run on the main pool. The App never runs git or an AI CLI.

### Implementation

```text
Components/Ai: AiProviderControl, AiMenuCheckItem, AiSuggestionPanel; wwwroot/css/ai-generation.css (gm-ai-error / gm-ai-warning), js/gm-ai-menu.js
WorkspaceGitChanges.Ai.cs / NewPullRequestModal.Ai.cs
AiTextGenerationRuns<TText>          app-wide click-to-write runs, one per Workspace and Feature context, outside any page or circuit; Error, Warning and Offer are per context; Cancel and the end of a run share one locked handle, so a late Cancel is a no-op
WorkspaceGitChangesCommitGeneration   the commit message runs (stores into WorkspaceGitChangesCommitMessageMemory)
WorkspacePullRequestTextGeneration    the New PR title and description runs (stores into WorkspacePullRequestDraftMemory)
WorkspacePullRequestDraftMemory       the New PR title and description, per Workspace and Feature context
WorkspaceGitChangesCommitMessageMemory   the message, per Workspace and Feature context
WorkspaceGitChangesPushAfterCommitMemory / WorkspaceGitChangesGenerateMessageOnCommitMemory   the two On Commit checkboxes, per Workspace
IWorkspaceAiGenerationOperations -> WorkspaceAiGenerationOperations (AiGenerationResult may carry AiGenerationWarning)
AiTextApplyPolicy, AiPromptBuilder, CommitHistoryCondenser, GitCommitLogParser, AiGeneratedTextParser, AiGenerationWarning (Common)
Worker: GitEvidenceCollector, AiCliRunner, AiCliAdapters, AiCliDiagnostics (correlated recovery logs; anomaly-only Cursor launcher snapshot)
```
