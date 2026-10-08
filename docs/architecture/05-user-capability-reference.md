# 05 - User Capability Reference

This document maps current user capabilities to the main implementation areas that own them.

It is intentionally practical. A developer should be able to find the responsible code from the user behavior.

---

## 1. Workspaces

### User capabilities

```text
list Workspaces
search
create
edit name/path
set default
delete
select repositories
import/use existing local repositories
```

### Primary implementation areas

```text
Workspaces page/components
WorkspaceService
WorkspaceRepository
Worker repository/filesystem commands
Workspace catalog/query services
```

### Persistence

```text
Workspace
WorkspaceRepositoryLink
Repository
Settings/default root
```

### Workspace repository

```text
User can
  choose a Workspace repository in the Workspace modal (enable, change or disable)
  see it first on the Repositories page, in its own group with a Workspace badge
  resolve a definition-drift banner with "Write Workspace definition to disk" or "Dismiss"

Rules
  at most one per Workspace; Role = Workspace on WorkspaceRepositoryLink
  working tree is the Workspace root folder; Sources are nested folders ignored by a managed .gitignore section
  enable and disable are refused while Features exist; disable never deletes files or .git
  enable and restore are refused unless the Worker is connected with the same GrayMoon version (App/Worker version lock)
  restore validates .graymoon.json before creating anything and rolls back a Workspace whose restore fails
  no dependency level, no projects
  Feature create: root worktree first, then Sources; remove and rollback: Sources first, root last
```

Implementation areas:

```text
IWorkspaceRepositoryOperations / WorkspaceRepositoryOperations (enable, disable, restore)
IWorkspaceManifestService / WorkspaceManifestService (.graymoon.json, managed .gitignore, drift)
IRemoteWorkspaceManifestReader / GitHubRemoteWorkspaceManifestReader (read-only .graymoon.json preflight)
RestoreDefinitionEvaluator (definition validation and local resolution, shared by preflight and post-clone check)
WorkerVersionPolicy + WorkerBridge (version lock: only SelfUpdate and GetHostInfo run on a version mismatch)
Worker: AttachWorkspaceRepository, DiscardWorkspaceRoot, WriteRepositoryFile, WorkerRepositoryPaths.Resolve
WorkspaceFeatureOperations (two-phase Feature create, Sources-first removal)
```

Enable attaches the repository to the Workspace root through the Worker (`AttachWorkspaceRepository`), then writes the managed `.gitignore` section and `.graymoon.json` through `WriteRepositoryFile`. Worker commands carry `WorkspaceRepositoryName` so `WorkerRepositoryPaths.Resolve` maps the Workspace repository to the Workspace root and every other repository to a nested folder.

Restore is two steps. `PreflightRestoreAsync` reads `.graymoon.json` through the GitHub connector (no Worker, no clone, no database change), validates it (a missing file, invalid JSON, a newer schema and unknown profile values are blocking errors) and previews the profile, the Source repository count and the connectors and repositories missing on this computer. `RestoreFromRepositoryAsync` repeats that check, refuses a non-empty destination folder, then creates the Workspace, clones the repository into the empty root, validates the cloned definition again, applies the profile, links every Source repository it can resolve by normalized URL and writes the managed `.gitignore`. The definition is rewritten only when it is complete on this computer and not already canonical. Any failure after the Workspace was created rolls it back: the Worker command `DiscardWorkspaceRoot` deletes the root only when it is empty or a clean clone of the restored repository, then the Workspace row and links are deleted. Missing Source repositories and connectors never block a valid definition; they are reported as "owner/name" and host names.

App/Worker compatibility is the version lock, not a per-feature check: `WorkerConnectionTracker` compares the Worker's product SemVer with the App's (build metadata ignored, see `WorkerVersionPolicy`), and `WorkerBridge` refuses every command except `SelfUpdate` and `GetHostInfo` while the state is `VersionMismatch`. The Worker update therefore works against any Worker version, and the global Worker badge and Worker page direct the user to update.

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
see Worker presence/status
install Worker (download/install scripts on the Worker page; one-click install from Desktop)
run as service
receive version/update state
cancel commands where supported
```

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
WorkerHub
WorkerConnectionTracker
Worker CLI
SignalRConnectionHostedService
Worker queue state services
```

---

## 4. Repositories page

### User sees

Per repository:

```text
name
GitVersion
branch/tag
divergence
PR
dependency status
incoming/outgoing state
sync/status
Actions-related status
```

Repositories are grouped by dependency level.

### User can initiate

```text
sync/fetch
branch operations
tag operations
update branch from default
dependency updates
push
pull/sync commits
Undo Push
custom dependencies
configured-file dependency diagnostics
create PR
merge PR
restore packages
Prepare Workspace
Return to Default
```

### Implementation areas

```text
WorkspaceRepositories.razor + partials
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
GitVersion
branch/default branch
commit counts
tags/refs
project discovery
hook installation
```

### App work

```text
persist repository snapshot
merge project state
recompute dependencies
refresh PR as needed
broadcast Workspace sync
```

---

## 6. Branch menu

Branch menu in the special Workspace context:

```text
Prepare Workspace
New Branch
Switch Branch
Create PR
Return to Default
```

The menu controls the Workspace's main checkouts.

In a Feature context the primary button reads **Feature** and the menu only offers:

```text
Create PR
Remove Feature
```

The primary button label changes to **Create PR** when at least one repository has a creatable PR, and to **Remove** in a Feature context with no creatable or open PR (`WorkspaceRepositoriesHeader.razor`). The level header hides Return to Default in a Feature context.

### New Branch

Creates the selected branch across repositories.

### Switch Branch

Computes compatible/common branches and performs coordinated checkout.

### Create PR

Starts multi-repository PR creation. In a Feature context, a repository is creatable when its worktree HEAD differs from the Feature's base commit, and the PR targets the Feature's parent branch.

### Return to Default

Runs shared preflight and cleanup execution.

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

Implementation:

```text
IWorkspaceBranchOperations
WorkspaceBranchOperations
WorkspaceGitService.Branches
RepositoryBranchWriter
Worker branch commands
```

---

## 8. Prepare Workspace

### User input

```text
new branch name
base
whether to update dependencies
whether to push
commit message options
```

### Flow

```text
create branches
persist checkout state
dependency update if requested
commit if needed
synchronized push if requested
```

### Implementation

```text
PrepareWorkspaceModal
WorkspaceRepositories.PrepareWorkspace
IWorkspacePreparationOperations
PrepareWorkspaceOrchestrator
IWorkspacePushOperations
```

---

## 9. Dependency update

### User can

```text
update all pending repositories
update one repository
update by dependency level
choose commit-message behavior
optionally continue to push
```

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

### Implementation

```text
IWorkspaceUpdateOperations
DependencyUpdateOrchestrator
WorkspaceProjectRepository
WorkspaceFileVersionService
Worker file/project/restore commands
```

---

## 10. Restore packages

### User can

Restore package state from the Workspace or dependency-level workflows.

### Internal behavior

Uses discovered project paths and calls `dotnet restore` through the Worker.

Restore may also be part of update/push orchestration.

---

## 11. Push

### User can

```text
push one repository
push pending repositories
run synchronized push
run Push Updated workflow
```

### Synchronized push

Uses dependency levels and package registry waiting.

### Implementation

```text
IWorkspacePushOperations
WorkspacePushOperations
WorkspacePushService
PushOrchestrator
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

REST/post-merge flows call the same analysis first and abort safely when analysis/PR refresh is insufficient.

---

## 15. Projects page

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

### User can

```text
search configured files
find/add files from repositories
view content
remove configuration
configure version patterns
update versions
```

### Implementation

```text
WorkspaceFiles
IWorkspaceFileOperations
WorkspaceFileSearchService
WorkspaceFileVersionService
WorkspaceFileRepository
WorkspaceFileVersionConfigRepository
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
commit staged
Commit All
optionally Push committed
copy path
resize splitter
use Ctrl+Enter commit shortcut
```

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
WorkspaceGitChanges.razor + partials
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

`WorkspaceRepositoryPullRequests`

### Refresh

Driven by branch/current state and GitHub API refresh.

---

## 24. Create Pull Requests

### User can

Create PRs for:

```text
single repository
dependency level
Workspace set
```

The dialog supports normal PR metadata such as title, description, reviewers, and draft behavior where available.

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
optionally Return to Default
navigate to repository Actions
```

---

## 26. Actions page

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

The context selector also shows an **Open in...** flyout for the selected context: Cursor, Claude CLI, VS Code, and Visual Studio (each only when Desktop detects it), Terminal, and Explorer, with a per-repository sub-flyout. The App resolves the paths (`IWorkspaceNativeLaunchService`); Desktop only launches the tool.

The web/container application remains fully functional without Desktop.

---

## 31. Current intentional limitations

There is no MCP server.

Feature limitations in the current code:

```text
Features can only be based on the current Workspace (no "default branches" or "from another Feature" base)
Workspace repository membership cannot change while Features exist
REST endpoints always act on the special Workspace context
Remove Feature never deletes remote branches from the UI
```

Those are future architecture projects and must not be described as current product behavior in this documentation.

---

## 32. Features (worktree contexts)

### User sees

The context selector at the top of every Workspace page lists **Workspace** plus each Feature that is Ready or NeedsRepair. The selected context is in the URL (`?context=<id>`) and is remembered per Workspace.

Workspace pages read the selected context's own persisted state (checkout projection, Git Changes, projects, file status, PRs, Actions). Generated packages stay global to the Workspace.

### User can

```text
create a Feature ("+" in the selector, New Feature dialog)
switch between Workspace and Features
remove a Feature ("-" in the selector or the Feature menu)
open the Feature in a native tool (Desktop only)
work in the Feature with the normal Workspace pages
```

### New Feature

The dialog asks for a Feature name, which is also the branch name. "Based on" is fixed to **Current Workspace**.

Creation:

```text
validate the name as a branch name
reject a name that is already a branch in any repository or an existing Feature
record HEAD of every Workspace repository
git worktree add, in parallel, to {ManagedFeatureStorageRoot}\{FeatureName}\{RepositoryName}
  on a new branch from that commit, or detached when the repository is on a tag
copy the Workspace's checkout projection and projects into the new context
select the new Feature
```

Uncommitted Workspace changes are not copied. If any repository fails, the Feature is kept in **NeedsRepair** with the error.

### Remove Feature

The dialog first analyzes each worktree (live Git status probe) and reports it as completed, abandoned, active, or needs repair. When the result is not automatically safe, the user must tick at least one of:

```text
discard uncommitted changes
delete the local branch even if it has unpushed commits
```

Removal runs `git worktree remove` and deletes the local Feature branch per repository, then refreshes the special Workspace context without checking out, switching branch, or pulling. Any failure leaves the Feature in NeedsRepair with its metadata kept.

### Branch ownership

Switch Branch marks branches that are checked out by a Feature or another worktree, and refuses to delete a Feature-owned branch (use Remove Feature instead).

### Settings

**Feature storage root** on the Settings page (`FeatureStorageRootPath`). When empty, GrayMoon uses `{userprofile}\.graymoon` as reported by the Worker and saves it on first Feature creation. Existing Features keep the root recorded when the Workspace's first Feature was created.

Divergence badges in a Feature are computed against the Feature's parent branch, not the default branch.

### Implementation

```text
WorkspaceContextBar / WorkspaceFeatureSelector
CreateFeatureModal / RemoveFeatureModal
IWorkspaceFeatureOperations -> WorkspaceFeatureOperations
IWorkspaceFeatureContextResolver
WorkspaceService.ResolveFeatureStorageRootPathAsync
WorkspaceBranchOccupancyService
IWorkspaceExternalWorktreeOperations
Worker GetHeadCommits / CreateGitWorktree / RemoveGitWorktree / ListGitWorktrees
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

### Worker secret (`/hub/worker`, `/repos/{id}/connector`)

Only the real Worker may open the Worker hub connection or fetch a connector token.

```text
secret: generated on first start, kept in graymoon-worker.secret next to the database
  (plain text; file mode 600 on Linux; if the file is missing at startup a new secret is generated)
header: X-GrayMoon-Worker-Secret, sent by the Worker on the hub connection and on the connector request
wrong secret                      -> 401, always (constant-time comparison)
missing secret                    -> accepted with a warning, UNLESS
                                     Security:RequireWorkerSecret is true, or
                                     some Worker has already presented the correct secret
                                     (setting Security:WorkerSecretSeen), then 401
/repos/{id}/connector with any Origin header -> 403 (the Worker never sends one)
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
has connected with the secret, or `Security:RequireWorkerSecret` is set.

### Implementation

```text
RequestSecurityMiddleware
WorkerSecretMiddleware / WorkerSecretService / WorkerPairingService
SecurityOptions (Security:AllowedOrigins, Security:RequireWorkerSecret)
Worker: WorkerSecretProvider, SignalRConnectionHostedService, WorkerTokenProvider
Desktop: WorkerInstaller (secret hand-off)
```
