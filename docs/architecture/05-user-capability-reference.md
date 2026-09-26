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
install Worker
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
AgentHub
AgentConnectionTracker
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

Current main Workspace Branch menu:

```text
Prepare Workspace
New Branch
Switch Branch
Create PRs
Return to Default
```

The menu controls the current single-checkout Workspace.

### New Branch

Creates the selected branch across repositories.

### Switch Branch

Computes compatible/common branches and performs coordinated checkout.

### Create PRs

Starts multi-repository PR creation.

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
GitChangesAgentClient
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

The web/container application remains fully functional without Desktop.

---

## 31. Current intentional limitations

Current GrayMoon does **not** yet provide worktree-backed Feature contexts.

A Workspace has one checkout per repository.

There is no MCP server.

Those are future architecture projects and must not be described as current product behavior in this documentation.
