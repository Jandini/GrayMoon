# 01 - Product Model and User Workflows

## 1. Purpose of GrayMoon

GrayMoon is designed for development environments where one logical product is distributed across many Git repositories.

A typical Workspace may contain:

```text
shared libraries
NuGet packages
microservices
executables
test projects
infrastructure repositories
version/configuration repositories
```

Without GrayMoon, a cross-repository change often requires a developer to repeat the same work manually:

```text
clone repository
find current branch
create or switch branch
discover package dependencies
update PackageReference versions
update version files
restore
commit
push
wait for packages
create pull requests
check GitHub Actions
merge
return repositories to default branches
```

GrayMoon treats the repository collection as one coordinated Workspace.

Its purpose is not to replace Git or GitHub. It orchestrates them with knowledge of the Workspace dependency graph and persists enough state to present a coherent multi-repository view.

---

## 2. Core terminology

### Workspace

A Workspace is a named collection of repositories that belong together for development.

It owns:

```text
repository membership
workspace root
custom dependency declarations
configured files
version-file patterns
workspace-level settings
```

Today, every Workspace repository has one normal physical checkout under the Workspace root.

### Repository

A Repository is a Git repository discovered from a configured source connector, normally GitHub.

The same underlying Repository entity may be linked into a Workspace through `WorkspaceRepositoryLink`.

### WorkspaceRepositoryLink

This is the current Workspace-to-Repository membership record and, in today's implementation, also the main persisted projection of the repository's current checkout state.

It includes data such as:

```text
GitVersion
BranchName
CheckedOutTag
DefaultBranchName
OutgoingCommits
IncomingCommits
DefaultBranchAheadCommits
DefaultBranchBehindCommits
BranchHasUpstream
DependencyLevel
Dependencies
UnmatchedDeps
RepositoryType
```

### Project

A Workspace Project is a discovered `.csproj` or a generated/virtual package project.

Projects provide package identity, framework, project type, and package references used by dependency analysis.

### Dependency level

GrayMoon transforms repository dependency relationships into dependency levels.

Repositories in the same level can generally be processed in parallel.

Operations that must respect dependency order process one level before the next.

### Prepare Workspace

`Prepare Workspace` is the current coordinated branch workflow.

It creates a branch across the intended Workspace repositories, optionally updates dependencies, commits generated changes, and can continue into synchronized push.

It replaced the older product term "New Feature". It is not a worktree Feature.

### Return to Default

`Return to Default` safely moves selected Workspace repositories back to their default branches and cleans up the previous working branches according to explicit safety rules.

The safety architecture is now headless and shared:

```text
AnalyzeReturnToDefaultAsync
→ ReturnToDefaultPlan
→ explicit ReturnToDefaultOptions
→ ExecuteReturnToDefaultAsync
```

The UI and unattended/REST path use the same analysis.

---

## 3. Main application navigation

At the global level GrayMoon provides areas such as:

```text
Workspaces
Connectors
Worker
Settings
```

Inside a Workspace the main navigation is:

```text
Repositories
Changes
Projects
Packages
Files
Deps
Actions
```

### Repositories

The operational control plane.

This page combines current Git state, version state, dependency information, pull-request state, and action status.

It is the primary place from which users initiate coordinated repository operations.

### Changes

The multi-repository Git Changes experience.

It provides:

```text
changed-file tree
staged / unstaged state
multi-selection
diff view
stage / unstage
discard
commit
commit all
optional push
```

### Projects

Displays discovered projects across the Workspace.

Typical columns include:

```text
Name
Type
Framework
File
```

### Packages

Displays package-producing projects and matched package registries.

Used to understand which Workspace projects publish packages consumed by other Workspace repositories.

### Files

Displays user-configured repository files that GrayMoon should track or version-manage.

Supports file discovery, viewing, version-pattern configuration, and version updates.

### Deps

Visualizes the repository dependency graph.

Dependencies come from more than `.csproj` references. GrayMoon can combine:

```text
PackageReference relationships
configured version-file token references
generated package relationships
custom user-declared dependencies
```

### Actions

Shows GitHub Actions workflows for the current repository branches across the Workspace.

Supports filtering, rerun/start behavior, cancellation where available, progress refresh, and logs.

---

## 4. Workspace creation and import

A Workspace has a name and a root path.

Repositories are selected from configured connectors.

GrayMoon supports existing local repository layouts as well as repository creation/cloning through the Worker.

The App stores the Workspace configuration; the Worker inspects and modifies the local filesystem.

Important behavior:

- Workspace root is per Workspace.
- The global root setting acts as a default, not as the permanent source of truth for every Workspace.
- Repository membership is explicit.
- Save/import flows may take time because the Worker validates local repositories and obtains Git state.

---

## 5. Repository synchronization

Workspace synchronization refreshes GrayMoon's knowledge of each local repository.

A full repository sync may:

```text
ensure repository exists
clone if needed
configure Git safe-directory handling
fetch refs and tags
run GitVersion
determine branch/default branch
calculate incoming/outgoing commits
calculate divergence from default
determine upstream state
discover projects
refresh branches/tags
install/update GrayMoon Git hooks
persist resulting state
```

The UI is persistence-first: pages normally render SQLite state instead of running Git synchronously during rendering.

---

## 6. Branch workflows

### New Branch

GrayMoon can create a branch across Workspace repositories.

The branch operation can use a selected base and coordinates state persistence after checkout.

### Switch Branch

GrayMoon can switch repositories to a common branch.

Common branch computation intentionally ignores incompatible states such as repositories pinned to tags.

Individual repositories can also be switched independently.

### Tags

Repositories can be checked out at tags.

A tagged repository is considered pinned/read-only for many write operations.

GrayMoon records:

```text
CheckedOutTag
HasNewerTag
```

and blocks inappropriate push/update/commit workflows until the user returns to a branch.

### Update branch from default

GrayMoon can update a current working branch from the repository default branch.

The user sees divergence and conflict results per repository.

---

## 7. Prepare Workspace workflow

Prepare Workspace is a coordinated multi-repository workflow.

Conceptually:

```text
choose new branch name
choose base
create branch across target repositories
persist branch state
optionally update dependencies
optionally commit generated changes
optionally synchronized push
```

The operation deliberately suppresses or controls hook-driven races while it is making coordinated changes.

It is designed to turn a many-repository branch preparation into one intentional action.

---

## 8. Dependency discovery and graph

GrayMoon discovers `.csproj` projects and package references through the Worker.

For each project it can persist:

```text
project name
project file path
project type
target framework
PackageId
PackageReference relationships
matched package connector
```

The graph is then mapped from projects to repositories.

Dependency sources include:

1. project package references;
2. configured file tokens;
3. generated/virtual package relationships;
4. custom repository dependencies.

The graph drives:

```text
repository ordering
dependency levels
update planning
push planning
package waiting
dependency mismatch badges
```

---

## 9. Dependency update workflow

GrayMoon compares dependency versions used by a repository with the current GitVersion of the referenced Workspace repositories.

When updates are required, GrayMoon can:

```text
refresh project state
calculate required package/version changes
update project references
update configured version-file lines
restore packages
commit resulting changes
process repositories in dependency order
```

Repositories pinned to tags are protected from write operations.

The workflow is designed to make dependency/version synchronization deterministic across a multi-repository Workspace.

---

## 10. Push and synchronized push

A normal push operates on repositories with:

```text
outgoing commits
or
a branch without upstream
```

GrayMoon can push repositories in parallel when dependency ordering is not required.

Synchronized push is the more important orchestration path.

Conceptually:

```text
Level 1
  push package-producing repositories
  wait for required package versions to appear

Level 2
  push dependent repositories
  wait as required

Level N
  continue
```

This prevents downstream builds from starting before the package versions they reference are actually available.

Connector credentials are passed at runtime. GrayMoon does not persist credentials into repository Git configuration.

---

## 11. Pull and commit synchronization

GrayMoon tracks:

```text
IncomingCommits
OutgoingCommits
BranchHasUpstream
```

and uses that state to guide pull/sync/push decisions.

If incoming commits exist, the appropriate operation synchronizes the local branch before blindly pushing outgoing changes.

Operations can run across a repository or a dependency level.

---

## 12. Undo Push

GrayMoon can undo outgoing commits across applicable repositories.

The user chooses whether to keep the reverted changes in the working tree.

The operation is safety-sensitive and updates persisted repository state afterward.

---

## 13. Configured files and version patterns

Users can add arbitrary files from Workspace repositories to GrayMoon's file catalog.

A configured file stores:

```text
repository
relative path
display file name
optional version pattern
```

Version patterns reference Workspace repositories through tokens.

GrayMoon can validate a file against the expected repository versions and update the file when needed.

The file system remains Worker-owned.

Configured-file references also contribute dependency relationships to the Workspace graph.

---

## 14. Projects, packages, and generated packages

### Projects

Physical `.csproj` projects discovered from the checkout.

### Packages

Projects with package identities that can be matched to configured package registries.

### Generated/virtual packages

GrayMoon can model packages that are produced by CI or configuration even when there is no normal physical package-producing `.csproj` in the checkout.

Generated package rows participate in dependency and push planning and are protected from deletion by normal physical project reconciliation.

---

## 15. Pull requests

GrayMoon persists PR state for the current branch of each Workspace repository.

It can:

```text
discover current PR
create PR
create PRs for multiple repositories
refresh mergeability/check state
merge
close
update title
```

The repository grid displays PR state without requiring a fresh GitHub query for every render.

PR state is reconciled when branch state changes.

---

## 16. Merge workflow

The merge dialog combines remote GitHub state and local Workspace state.

It can show:

```text
GitHub checks
review state
mergeability / conflict status
allowed merge methods
changed files
local uncommitted changes
unpushed commits
incoming commits
```

The dialog may link to the Actions page with a repository filter.

After merge, users can optionally continue into Return to Default for the Workspace.

---

## 17. GitHub Actions

The Actions page retrieves and persists workflow state for the current repository branch.

Capabilities include:

```text
status filters
text/repository/workflow search
run workflow
rerun
rerun failed
abort/cancel where supported
bulk operations
view logs
adaptive polling
targeted refresh after repository synchronization
```

Workspace setting `ExcludeAiWorkflows` controls whether AI-driven workflows are included in the operational grid/status accounting.

---

## 18. Git Changes

Git Changes is a Workspace-wide source-control page.

It is designed to feel immediate while still being correct across many repositories.

Capabilities include:

```text
staged and unstaged groups
repository/file tree
multi-selection
search
change statistics
Monaco diff
stage/unstage
discard
commit
Commit All
optional push
copy path
keyboard shortcuts
```

The page reads a persisted SQLite projection.

The Worker owns Git status scanning, diff generation, mutations, and filesystem watchers.

---

## 19. Hook-driven state updates

GrayMoon writes Git hooks into managed repositories so state can update even when Git is changed outside the GrayMoon UI.

Hooks include operations such as:

```text
post-commit
post-checkout
post-merge
pre-push
```

The hook calls the local Worker HTTP listener.

The Worker gathers the required repository state and sends a synchronization notification back to the App.

This is how an IDE or command-line Git operation can update GrayMoon without the user pressing Refresh.

---

## 20. Return to Default

Return to Default is a cleanup workflow for the current Workspace checkout.

The shared preflight analyzes:

```text
current/default branch
tag state
commits ahead of default
upstream presence
pull request state
whether explicit destructive confirmation is required
```

The UI then asks for appropriate choices such as remote branch deletion or force deletion where the current policy permits it.

Execution is separate from analysis.

This pattern is important for future automation because safety logic is not owned solely by the modal.

---

## 21. Typical end-to-end development flow

A common GrayMoon workflow is:

```text
1. Open Workspace
2. Synchronize current repository state
3. Prepare Workspace on a coordinated branch
4. Make code changes in external tools
5. Observe Git Changes update through watchers/hooks
6. Update dependency/package versions
7. Restore and commit
8. Push in dependency order
9. Wait for package publication
10. Create PRs
11. Monitor GitHub Actions
12. Merge
13. Return repositories to default branches
```

GrayMoon's product value is the coordination between these steps, not any single Git command in isolation.
