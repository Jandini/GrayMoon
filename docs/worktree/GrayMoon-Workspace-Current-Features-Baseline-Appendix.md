# GrayMoon Workspace Baseline Appendix

## Current Workspace Capabilities That Must Survive the Worktree Feature Implementation

**Baseline repository:** `Jandini/GrayMoon`  
**Baseline commit:** `cd9a17cb21e3ba2757542cc9f55f6fc19e2538c8`  
**Baseline date:** 2026-09-20  
**Companion document:** `GrayMoon-Features-Pre-Design-Decision-Summary-v2.md`

---

# 1. Purpose

This appendix records how GrayMoon's **current Workspace** behaves before worktree-backed Features are introduced.

It is a regression baseline.

The first Feature implementation is successful only if the existing Workspace behavior described here continues to work, except where the approved Feature pre-design document explicitly changes the behavior.

This document therefore answers four questions for each Workspace capability:

1. **What does the user currently see or do?**
2. **How does GrayMoon currently implement it?**
3. **What state or external systems does it depend on?**
4. **What behavior must remain true after `WorkspaceFeatureContext` is introduced?**

This is not a proposal to preserve today's internal schema. Several internals must change for Features. The requirement is to preserve the current **product behavior and guarantees** while moving those internals behind context-aware abstractions.

---

# 2. Current Workspace Mental Model

Today a GrayMoon Workspace represents one coordinated local multi-repository checkout.

Conceptually:

```text
Workspace
├─ RepoA working tree
├─ RepoB working tree
├─ RepoC working tree
└─ ...
```

For every linked repository GrayMoon persists a current projection including:

```text
branch/tag identity
GitVersion
incoming/outgoing commits
divergence from default
upstream state
project discovery
dependency state
version-file state
pull request state
GitHub Actions state
Git Changes state
```

The existing implementation assumes one checkout per Workspace repository.

The Feature design intentionally changes that internal assumption to:

```text
Workspace
├─ WorkspaceFeatureContext: Workspace
├─ WorkspaceFeatureContext: Feature A
├─ WorkspaceFeatureContext: Feature B
└─ ...
```

but the special `Workspace` context must continue to provide today's behavior.

---

# 3. Runtime Architecture Baseline

GrayMoon is a two-process system.

## GrayMoon.App

The App:

- is ASP.NET Core / Blazor Server;
- owns the web UI;
- owns SQLite persistence;
- owns workspace orchestration/business logic;
- communicates with GitHub/connectors;
- sends filesystem/Git commands to the Agent;
- does not directly operate the developer's local Git working trees.

## GrayMoon.Agent

The Agent:

- runs on the developer machine;
- owns local Git and filesystem execution;
- receives commands from the App over SignalR;
- executes Git, GitVersion, dotnet restore, file reads/writes, project scans, and Git Changes commands;
- exposes the local hook listener;
- pushes hook-driven synchronization and Git Changes snapshots back to the App.

## Regression invariant

The Feature implementation must preserve this boundary.

`GrayMoon.App` must not become responsible for direct local Git/process work simply because Feature paths are introduced.

The App should resolve the selected `WorkspaceFeatureContext` and physical root; the Agent should continue receiving concrete paths/identities and executing the local operation.

---

# 4. Workspace Management

The global **Workspaces** page currently supports:

- listing Workspaces;
- searching Workspaces;
- adding a Workspace;
- editing its name/path;
- marking one Workspace as default;
- deleting a Workspace;
- editing repository membership;
- fetching repositories from configured connectors;
- detecting an existing directory during Workspace creation/import;
- importing matching existing repositories from disk.

The table displays:

```text
Name
Path
Repos
Projects
Actions
```

Workspace creation requires the Agent because GrayMoon must inspect the local workspace root/directory.

A Workspace may use the global root or its own persisted `RootPath`.

## Repository membership

Repository membership is persisted independently from the physical checkout state.

The user can add/remove repository links through the repository-selection modal.

Changing membership triggers recomputation/refresh of derived Workspace state where required.

## Regression invariants

After Features:

- Workspace creation/import remains a Workspace-level operation.
- Repository membership remains shared by every Feature context.
- Adding a repository means that repository belongs to the Workspace configuration and therefore to newly created Feature environments.
- Removing a repository must not leave orphaned context state/worktrees.
- Existing Workspace import semantics must continue to operate on the special Workspace checkout.
- The count of repositories and projects on the Workspaces page must remain meaningful for the special Workspace baseline unless a later design explicitly changes how the project count is presented.

---

# 5. Workspace Navigation

Inside a Workspace, the current navigation exposes:

```text
Repositories
Changes
Projects
Packages
Files
Deps
Actions
```

These are all first-class Workspace views.

The Feature pre-design changes their execution scope:

> Once a Feature context is selected, every applicable Workspace page must instantly represent that same context.

Therefore the baseline behavior documented below must become **context-relative**, not duplicated as unrelated Feature-specific pages.

---

# 6. Repositories Page - Core Workspace Dashboard

Route:

```text
/workspaces/{WorkspaceId}
```

This is the main operational dashboard.

It groups repositories by computed dependency level and displays each repository's current persisted state.

The main columns are:

```text
Repository
Version
Branch
Divergence
Pull Request
Dependencies
Outgoing/Incoming commits
Status
```

The page supports virtual scrolling and persisted/resizable table layout.

It uses persisted SQLite state for fast rendering and refreshes through App/Agent operations and SignalR broadcasts.

## Repository row behaviors

A repository row can expose or trigger:

- GitVersion copy;
- current branch/tag state;
- branch switching;
- tag checkout;
- update branch from default;
- dependency mismatch details;
- custom dependency editing;
- version-file mismatch details;
- push from outgoing-commit badge;
- pull/synchronize from incoming-commit badge;
- single-repository sync;
- create PR;
- merge PR;
- tag upgrade path.

## Level-header behaviors

Dependency level headers provide group operations including:

- Return to Default for the level;
- synchronize commits;
- create PRs;
- merge PRs;
- sync level;
- fetch level;
- restore packages for level.

## Regression invariants

The Repositories page must remain the operational summary of the **selected context**.

When context is `Workspace`, today's behavior must remain unchanged.

When context is a Feature:

- version, branch, divergence, commit counts, dependency state, PR state, and status must come from that Feature context;
- group operations must target only that Feature context;
- another Feature's refresh or Git hook must not overwrite the row;
- repository ordering/membership remains Workspace-shared;
- dependency-level grouping must come from the selected context's dependency graph.

---

# 7. Search and Filtering Baseline

Workspace grids consistently use `FilterSearchInput` and GrayMoon's shared boolean expression parser.

The parser supports:

- words;
- implicit `and`;
- explicit `and`;
- `or`;
- parentheses;
- field-prefixed terms such as `repo:`;
- graceful fallback when the expression cannot be parsed.

Examples of Workspace pages with search/filter behavior include:

- Repositories;
- Changes;
- Projects;
- Packages;
- Files;
- Actions;
- Dependencies.

## Regression invariants

Feature context selection must not replace or weaken existing search.

The approved UX joins the Feature selector to the existing search area.

The right-hand search control must preserve the page's existing semantics.

Changing Feature context should ideally preserve the current search expression so users can compare the same filter across contexts.

---

# 8. Workspace Branch Management

The special Workspace currently supports coordinated branch management.

The main Branch menu includes:

```text
Prepare Workspace
New Branch
Switch Branch
Create PRs
Return to Default
```

The Branch modal computes common branches across repositories and supports workspace-wide creation/checkout.

Repository rows also support repository-specific branch/tag operations.

## New Branch

GrayMoon can create a branch:

- across multiple Workspace repositories;
- from a selected base branch;
- optionally skipping repositories currently pinned to tags.

State is refreshed/persisted after branch creation.

## Switch Branch

GrayMoon can:

- fetch branch information;
- compute branches common across repositories;
- switch the Workspace repositories to a common branch;
- switch a single repository independently;
- checkout tags for individual repositories.

## Update branch from default

For a non-default current branch, GrayMoon can update it from the repository's default branch.

The UI shows commits behind and can link to a GitHub comparison.

Conflict results are surfaced per repository and direct the user to resolve conflicts in the IDE.

## Intentional Feature-era change

Per the Feature pre-design:

- these branch-mutating controls belong to the special Workspace;
- a Feature is itself branch identity;
- normal Feature UX must not casually switch its repositories to unrelated branches/tags;
- the Feature menu replaces the Branch menu while a Feature is selected.

This is an approved behavior change, not a regression.

---

# 9. Prepare Workspace

`Prepare Workspace` is today's coordinated workflow formerly known as New Feature.

The current modal describes the operation as creating a new branch across Workspace repositories, updating package dependencies with commits, and using synchronized push.

The orchestrator performs:

```text
1. create branches across the targeted repositories
2. persist state inline while hooks are suppressed
3. optionally run dependency update
4. optionally commit dependency changes
5. continue into the configured push flow
```

The branch-creation phase intentionally avoids racing asynchronous hooks against the orchestrator's own state persistence.

Repositories pinned to tags are accounted for by the workflow's branch preparation behavior.

## Regression invariants

`Prepare Workspace` must remain behaviorally identical for the special Workspace.

The Feature implementation must not repurpose this workflow as Feature creation.

New worktree Feature creation is a separate workflow.

---

# 10. Return to Default

`Return to Default` is a special-Workspace cleanup/reset workflow.

Before returning a repository to its default branch, GrayMoon can inspect:

- current branch;
- commits ahead of default;
- upstream state;
- PR state;
- whether branch deletion would discard work.

The Agent's Return-to-Default command:

- checks out the default branch;
- updates/pulls as required;
- deletes the previous local branch under the allowed safety rules;
- may delete the remote branch when requested and allowed;
- returns authoritative branch/version/count/project state.

The App writes the resulting snapshot through `WorkspaceRepositoryStateWriter`, including:

- branch identity;
- GitVersion;
- commit counts;
- upstream;
- branch rows;
- projects;
- PR reconciliation.

Existing safety behavior allows force-deleting the local branch when the user has explicitly confirmed destructive cleanup, or where the PR is merged/closed.

## Intentional Feature-era change

`Return to Default` remains Workspace-only.

Features use `Remove Feature` with the lifecycle rules approved in the pre-design document.

However, the **safety philosophy and analysis** of Return to Default are a baseline that Remove Feature should reuse.

---

# 11. Repository Synchronization

Repository synchronization is a core GM capability.

A full repository sync can:

- ensure the local repository exists;
- clone when missing;
- add safe-directory configuration;
- fetch refs/tags;
- run GitVersion;
- discover projects;
- install/update GrayMoon Git hooks;
- determine branch/default branch;
- compute outgoing/incoming commits;
- compute divergence against default;
- compute upstream state;
- persist branches/tags/projects and repository state.

The App centralizes denormalized checkout-state writes through `WorkspaceRepositoryStateWriter`.

That writer applies only state groups that the Agent actually probed, preventing partial refreshes from blanking unrelated persisted fields.

## Regression invariants

All existing sync capabilities remain available in `Workspace`.

For Features the same behavior must operate against the selected context path without overwriting other contexts.

The existing central writer is the preferred migration seam for context-awareness.

---

# 12. Git Hook Driven Synchronization

GrayMoon installs hooks including:

```text
post-commit
post-checkout
post-merge
pre-push
```

Hooks POST to the Agent's local HTTP listener.

Today the hook identity includes:

```text
WorkspaceId
RepositoryId
RepositoryPath
```

The Agent executes the appropriate notification sync and pushes a `SyncCommand`/repository notification back to the App.

The App then:

- updates persisted repository state;
- reconciles PR state where applicable;
- refreshes tags/remote refs when supplied;
- recomputes workspace dependency/file-version state;
- broadcasts repository/workspace sync events to browsers.

## Required Feature migration

This is a core requirement from the pre-design review.

Hook identity must become context-aware.

A hook fired from:

```text
Feature A / RepoA
```

must never mutate:

```text
Workspace / RepoA
Feature B / RepoA
```

The detailed design must carry `WorkspaceFeatureContextId` or equivalent unambiguous context identity through hook installation, notification DTOs, App persistence, recomputation, and browser notifications.

---

# 13. Projects Page

Route:

```text
/workspaces/{WorkspaceId}/projects
```

The Projects page lists projects discovered from repository contents.

It displays:

```text
Name
Type
Framework
File
```

Project discovery currently comes from Agent scans of checked-out repository files.

A repository project may provide:

- project name;
- project type;
- project file path;
- target framework;
- package ID;
- package references.

The page supports search and virtual scrolling.

Generated projects/packages are represented distinctly; generated entries do not require a physical project file.

## Required Feature migration

Discovered projects are **not Workspace-global facts** once multiple worktrees exist.

A Feature may add/remove/change `.csproj` files.

Therefore the Projects page must show the selected context's project projection.

This is a core persistence migration requirement.

---

# 14. Packages Page

Route:

```text
/workspaces/{WorkspaceId}/packages
```

The Packages page lists package-producing Workspace projects.

It displays:

```text
Package Id
Framework
Registry
```

It supports:

- search by package ID;
- registry matching;
- `Sync registries`.

Package registry matching links discovered package projects to configured package connectors/registries.

Packages are subsequently used by dependency synchronization and synchronized push.

## Regression invariants

The visible package set must be derived from the selected context's discovered project graph.

Connector/registry configuration remains shared.

A package introduced only in Feature A must not appear as though it exists in Workspace or Feature B.

Registry matching behavior must continue to function for the selected context.

---

# 15. Dependency Graph - Core GrayMoon Behavior

GrayMoon computes repository dependency levels using a topological sort.

Level 1 contains repositories without dependencies; higher levels depend on lower levels.

The dependency graph is central to:

- Repositories page grouping;
- dependency mismatch badges;
- Update;
- Push Updated;
- synchronized push;
- package wait ordering;
- restore planning;
- Prepare Workspace.

Dependency edges currently come from several sources:

```text
1. csproj PackageReference relationships
2. configured version-file token relationships
3. generated package relationships
4. user-declared custom repository dependencies
```

## Shared vs context-specific after Features

Shared Workspace configuration:

```text
repository membership
custom dependency declarations
version-file configuration definitions
connector configuration
```

Context-derived state:

```text
discovered projects
PackageIds
PackageReferences
generated observations that depend on checked-out files
version-file current values
computed graph edges
dependency levels
mismatch diagnostics
```

## Required Feature migration

Each `WorkspaceFeatureContext` needs an independent derived project/dependency graph.

Refreshing Feature A must not alter the dependency graph used by Workspace or Feature B.

This is one of the most important migration areas in the Feature implementation.

---

# 16. Dependencies Page

Route:

```text
/workspaces/{WorkspaceId}/dependencies
```

The Dependencies page visualizes repository dependency relationships.

It derives nodes/edges from persisted project/dependency data and Workspace repository versions.

The page supports search/filtering and reports repository/dependency counts.

## Regression invariant

The graph must be the graph for the selected context.

Switching contexts should switch the graph without mutating other contexts' data.

---

# 17. Custom Dependencies

GrayMoon lets users manually declare repository ordering dependencies.

The Custom Dependencies modal distinguishes:

- dependencies already implied by project references;
- dependencies implied by version files;
- dependencies implied by generated packages;
- user-selectable custom dependencies;
- choices that would create circular dependencies.

Implicit dependencies are locked in the custom dependency UI because they come from discovered/configured state rather than the manual declaration.

Saving custom dependencies triggers dependency recomputation and Workspace refresh.

## Regression invariants

Custom dependency declarations remain Workspace-shared configuration.

Their effect is merged into each selected context's derived graph.

Circular-dependency protection and locked implicit dependency presentation must continue to work.

---

# 18. Dependency Mismatch / Update

GrayMoon compares dependency versions in dependent projects against the referenced repository's current GitVersion.

The repository grid exposes mismatch counts and detailed lines.

A user can update:

- a single repository;
- all applicable repositories;
- selected dependency levels.

The update flow can:

- refresh project information;
- calculate required version updates;
- modify project/package references;
- update configured version files;
- run `dotnet restore`;
- commit generated changes;
- proceed level-by-level.

Repositories on tags are protected from write operations.

The UI warns when changes would be committed directly to a default/protected branch.

## Regression invariants

All calculations and modifications must occur within the selected context.

The dependency plan and current GitVersion values must come from the same context.

No update in Feature A may modify Workspace files or Feature B files.

---

# 19. Restore Packages

GrayMoon can invoke `dotnet restore` through the Agent.

Restore operations use discovered project paths.

Restore is available:

- at Workspace scope;
- at dependency-level scope;
- as part of update/push workflows where configured.

The Push Updated flow supports optional restore behavior.

## Regression invariants

Project paths must be resolved from the selected context's project projection and physical root.

Restore in one context must not use project paths discovered from another context.

---

# 20. Push and Synchronized Push

Push behavior is dependency-aware.

GrayMoon determines repositories needing push from state such as:

```text
OutgoingCommits > 0
or
BranchHasUpstream == false
```

Repositories on tags are excluded.

For a normal push, repositories can be pushed with bounded parallelism.

For synchronized push, GrayMoon pushes in dependency order and can wait for required package versions to appear in package registries before proceeding to dependent levels.

The push pipeline can:

- ensure connector health;
- push with authentication supplied at runtime;
- establish upstream for a new branch;
- detect non-fast-forward rejection;
- fetch/refresh after rejection;
- refresh commit counts/upstream after success;
- monitor relevant running GitHub Actions;
- stream Agent/GHA progress into the loading overlay terminal;
- restore packages where the workflow requests it.

## Regression invariants

The plan must be computed from the selected context's graph and repository state.

Push from Feature A must push Feature A's branches only.

The Feature pre-design additionally establishes:

- Feature creation itself does not require a remote;
- upstream is created opportunistically when the branch is naturally pushed;
- GM must never force-overwrite an unexpected existing remote Feature branch.

---

# 21. Pull and Commit Synchronization

The Repositories page uses incoming/outgoing commit state to guide synchronization.

Where incoming commits exist, the click behavior favors synchronization/pull because it must reconcile incoming state before simply pushing outgoing commits.

Commit synchronization is able to operate across repositories and uses the current persisted commit/upstream state as the plan input.

## Regression invariant

Incoming/outgoing state and any pull/sync execution must be scoped to the selected context.

---

# 22. Undo Push

GrayMoon provides an undo-push workflow with confirmation/safety behavior.

It operates through the Agent against local Git state and updates persisted Workspace state after the operation.

## Regression invariant

Undo Push must target only the selected context's branch/worktree and must not rewind another Feature or Workspace checkout.

---

# 23. Pull Requests - Discovery and Persistence

GrayMoon persists PR state for each current repository branch.

PR refresh:

- determines the currently checked-out branch;
- clears PR state when there is no valid branch/tag context;
- queries GitHub for PR state for the current branch;
- uses a short cache to absorb duplicate requests;
- persists the latest result;
- distinguishes refreshed/cache-hit/cleared/failed outcomes.

A branch change invalidates/reconciles the previous PR projection so the old branch's PR badge is not displayed on the new branch.

## Required Feature migration

PR state is context-specific.

Feature A and Workspace may have different branches and therefore different PRs for the same repository.

PR persistence must be keyed by context repository state, not solely `WorkspaceRepositoryId`.

---

# 24. Create PRs

GrayMoon can create PRs:

- for an individual repository;
- across Workspace repositories;
- by dependency level.

PR creation uses the checked-out branch as the source and the repository default branch as the base unless the workflow specifies otherwise.

The UI coordinates title/description/reviewer flow according to existing GM modal patterns.

## Regression invariants

`Create PRs` must continue to work for Workspace.

For a Feature it becomes a primary Feature lifecycle operation and must use that Feature's branches in every repository.

The operation must not accidentally use the special Workspace branch state.

---

# 25. Merge Pull Request

The merge dialog combines GitHub and local GrayMoon information.

It can display:

- checks state;
- merge conflicts / mergeability;
- review status;
- allowed merge methods;
- local uncommitted changes;
- unpushed commits;
- incoming commits;
- changed-file information;
- links into Actions for repository-specific checks.

The local Git state is informational and can produce a warning without necessarily making GitHub merge impossible.

The dialog supports:

- merge method selection;
- merge;
- close pull request;
- optional Return to Default behavior for the special Workspace.

GitHub may take time to resolve mergeability/check state, and the UI accounts for pending states.

## Feature-era behavior

For Workspace, existing behavior remains.

For a Feature:

- merge operates on the Feature's PR;
- Return to Default is not the Feature cleanup action;
- Feature cleanup is `Remove Feature`;
- merged PR state is a strong safe-cleanup signal.

---

# 26. GitHub Actions Page

Route:

```text
/workspaces/{WorkspaceId}/actions
```

The Actions page builds a per-repository/per-workflow view for the repository's current branch.

It supports:

- persisted action state for fast initial display;
- branch-match validation of persisted action state;
- live refresh from GitHub;
- repository/workflow search;
- status filters;
- error/failed/running/aborted/success/no-run categories;
- exclusion/inclusion of AI workflows according to Workspace setting;
- workflow dispatch;
- rerun behavior;
- abort/cancel where supported;
- bulk rerun/start operations;
- run logs modal;
- repository-targeted query filter from the merge dialog.

Current polling adapts to browser/user activity:

```text
active page: fast
idle: slower
hidden tab: much slower
```

Repository sync events trigger targeted action refresh.

Recent code also displays refresh progress across repositories.

## Required Feature migration

Actions state is branch/context-specific.

The selected Feature's branch must determine workflow run state.

Persisted Actions for Workspace must not be reused as verified Feature state merely because the repository is the same.

SignalR repository-sync notifications must include context identity so a hook in Feature A does not trigger a misleading branch refresh in Feature B.

---

# 27. Git Changes Page

Route:

```text
/workspaces/{WorkspaceId}/changes
```

Git Changes is a combined multi-repository source-control view.

It includes:

- changed repository/file tree;
- staged and unstaged grouping;
- multi-selection;
- search/filtering;
- change statistics in the header;
- refresh/abort refresh;
- persisted splitter position;
- copy path;
- stage;
- unstage;
- stage selected;
- unstage selected;
- stage all changed;
- unstage all staged;
- discard file/folder;
- discard selected;
- discard all changed;
- commit staged changes;
- explicit Commit All;
- optional Push committed;
- `Ctrl+Enter` commit shortcut;
- Monaco diff viewer;
- previous/next diff navigation;
- handling for binary/large/unavailable diff content.

The commit message applies across repositories participating in the Workspace commit operation.

Filtered/staged behavior respects the selected paths rather than blindly committing unrelated files.

---

# 28. Git Changes Persistence and Refresh Model

Git Changes is intentionally persistence-first.

The browser page reads the persisted SQLite projection.

It does not synchronously shell out to Git whenever the page renders.

Agent-side behavior:

```text
GetGitChangeStatus
→ acquire/renew repo watcher lease
→ git status --porcelain=v2
→ cache/version snapshot
→ watcher events mark repo dirty
→ debounced/coalesced rescan
→ push fresh snapshot to App
```

App-side behavior:

```text
snapshot push
→ serialized Git Changes write queue
→ WorkspaceGitRepositoryStatus / change rows
→ lightweight GitChangesUpdated broadcast
→ page re-reads persisted projection
```

Stage/unstage/commit results feed the same authoritative snapshot/write path rather than maintaining a separate optimistic browser state.

## Required Feature migration

The watcher is already keyed by physical repository path, which maps well to worktrees.

The attribution/persistence identity must change to:

```text
WorkspaceFeatureContextId + WorkspaceRepositoryId
```

Each Feature repo path receives an independent watcher/refresh tracker.

Inactive contexts should use the existing activity/lease/grace approach rather than permanent polling of every worktree.

---

# 29. Git Changes Safety

Agent Git Changes commands validate paths to prevent:

- absolute path misuse;
- `.` / `..` traversal;
- operations escaping the repository.

Large staging path sets avoid Windows command-line limits by using Git pathspec stdin where supported.

## Regression invariant

Context-aware root resolution must not weaken these path-safety guarantees.

A path belonging to Feature A must never be accepted against Feature B merely because the repository name matches.

---

# 30. Files Page

Route:

```text
/workspaces/{WorkspaceId}/files
```

The Files page lets users maintain a Workspace-level catalog of arbitrary files from linked repositories.

The user can:

- search configured files;
- add files by searching repository contents;
- view file contents;
- configure version patterns;
- update versions;
- remove file configuration.

The persisted file identity/configuration includes repository and relative path and is Workspace-shared configuration.

A configured file may be marked `Missing on disk`.

## Regression invariants

The **configured file list and version pattern** remain shared Workspace configuration.

Viewing/checking/updating the physical file must resolve through the selected context.

A file may exist in one context and be missing in another, so `IsMissingOnDisk` cannot remain a single global mutable observation after Features.

---

# 31. Version Files

A configured version file uses a multi-line pattern with repository tokens.

Conceptually:

```text
KEY={RepositoryName}
```

GrayMoon resolves token values from repository versions and can:

- validate configured pattern lines against the physical file;
- identify missing pattern lines;
- detect current vs expected values;
- persist out-of-date line detail;
- update configured lines in place;
- derive dependency edges from repository token references.

Version-file status contributes to dependency badges and dependency planning.

Concurrent file-version checks for a Workspace are coalesced unless a caller explicitly requests a fresh check.

## Required Feature migration

Configuration remains shared.

All observations are context-specific:

```text
file exists/missing
current token value
expected value
out-of-date lines
out-of-date referenced repositories
derived dependency observations
```

This was already approved in the pre-design and is a regression-critical area.

---

# 32. Generated / Virtual Packages

GrayMoon can model package dependencies that are generated in CI even where no physical `.csproj` produces the package in the checkout.

Generated package/project entries participate in:

- dependency graph computation;
- push planning;
- package availability waiting;
- restore/update behavior where appropriate.

The current project persistence deliberately prevents a normal physical project rescan from deleting generated virtual package rows owned by generated-package synchronization.

## Required Feature review

The detailed Feature design must classify generated-package state into:

- Workspace-shared configuration-derived facts;
- context-derived observations.

Whatever schema is chosen, today's generated-package behavior must continue to work in Workspace and must not be accidentally deleted when another Feature refreshes its project graph.

---

# 33. Package Registry Matching

GrayMoon can match package-producing projects to configured registries/connectors.

Registry association is used by synchronized push to determine where required package versions should appear.

The Packages page exposes `Sync registries`.

## Regression invariant

Registry/connector definitions remain shared.

Project/package identities come from the selected context's graph.

Synchronized push must wait against the correct package versions produced by the selected context.

---

# 34. Pending Action Notifications

`WorkspacePendingActionsService` computes Workspace notifications from repository state.

A Workspace may generate notifications for:

- unmatched dependencies;
- out-of-date configured version files;
- outgoing commits;
- a new branch with no upstream;
- incoming commits.

It also computes the lowest dependency level requiring work.

These notifications power floating/actionable UI outside the main repository table.

## Required Feature migration

This is an additional context-awareness requirement discovered during the baseline review.

Pending-action notifications must be scoped to the selected `WorkspaceFeatureContext`.

A notification derived from the special Workspace must not be presented as though it describes Feature A.

The underlying product behavior remains the same; only the state scope changes.

---

# 35. Loading Overlay and Background Jobs

Workspace operations use a process/circuit job model so long-running work can survive page navigation.

The current model includes:

- `WorkspaceOperationRunner` as the process-wide mutation lock;
- `BackgroundJobService` handles;
- loading overlays keyed to page routes;
- streamed Agent command output;
- cancellation/abort behavior;
- continued operation even after a page component is disposed.

A job started on Repositories is intentionally not shown as the Changes page overlay simply because the user navigated there.

Code is careful not to let a background job touch disposables owned by a page instance after that page has been destroyed.

## Required Feature migration

Ordinary mutations must lock by `WorkspaceFeatureContextId`.

Structural operations continue to use a Workspace-wide lock.

Overlay/job identity must include context sufficiently to prevent one Feature's operation from blocking or attaching to another Feature.

Two isolated Features must be able to perform ordinary Git mutations concurrently.

---

# 36. Workspace Sync Broadcasts

Current browser refresh signals include concepts such as:

```text
WorkspaceSynced
RepositorySynced
RepositoryError
GitChangesUpdated
```

Pages debounce and selectively reload state in response.

For example, Actions uses repository-specific sync events to refresh only affected GitHub Actions rows.

## Required Feature migration

Context-specific events must include context identity.

Feature A events must not make Feature B or Workspace interpret the event as their own state change.

Workspace-wide configuration changes may still legitimately broadcast to all contexts.

---

# 37. Agent Concurrency Baseline

The Agent separates work into bounded queues/pools.

Git Changes status and diff work have dedicated pools so they do not sit behind long-running mutations.

Repository scans also use bounded concurrency.

## Regression invariant

Adding worktrees must not collapse this into one global serialized queue.

Context-level concurrency on the App side should complement, not defeat, existing Agent bounded concurrency.

---

# 38. Connector Authentication Baseline

Git remote operations use connector tokens at runtime.

Tokens are protected at rest and are not written into repository configuration by the Agent.

Git commands receive authentication headers dynamically.

## Regression invariant

Feature worktrees must reuse the same connector/authentication model.

Creating a Feature must not copy tokens into `.git/config`, worktree metadata, generated files, or the Feature directory.

---

# 39. Tag Behavior Baseline

Today individual Workspace repositories may be pinned to Git tags.

A tag checkout is treated as a read-only/detached state for many mutating operations.

GrayMoon:

- clears branch-specific fields where appropriate;
- blocks push/update/commit-style operations on tagged repos;
- tracks whether a newer tag exists;
- offers tag upgrade/navigation behavior.

## Intentional Feature-era change

A Feature is branch identity.

Feature contexts should not casually become tagged/detached working environments.

The special Workspace retains today's tag behavior.

---

# 40. Repository Ref Inventory

Today `RepositoryBranches` persists:

- local branches;
- remote branches;
- tags;
- default-branch markers.

The current implementation stores those rows under `WorkspaceRepositoryId`.

Checkout identity such as `BranchName` / `CheckedOutTag` is also stored on the Workspace repository link.

## Required Feature migration

The approved direction is to split:

```text
repository/ref inventory
from
context-specific checkout state
```

Because Git worktrees share the repository ref namespace but have independent HEADs.

Detailed design must preserve all current branch/tag discovery UX while eliminating the single-current-checkout assumption.

---

# 41. Workspace Sync Status

The Workspace entity currently carries:

```text
LastSyncedAt
IsInSync
```

Current hook/full-sync processing updates these from special Workspace repository state.

## Approved Feature behavior

These existing fields retain their special-Workspace meaning for migration compatibility.

Feature sync status belongs on or is derived from `WorkspaceFeatureContext`.

Do not redefine `Workspace.IsInSync` to mean that every Feature is clean/synchronized.

---

# 42. Error Presentation and Partial Failure

Many Workspace operations are multi-repository and can partially fail.

GrayMoon generally preserves per-repository errors rather than collapsing the entire operation into one generic failure.

Examples include:

- sync;
- branch operations;
- push;
- dependency update;
- PR operations;
- restore;
- Actions refresh.

Level-level errors are also supported where orchestration operates by dependency level.

## Regression invariant

Feature context introduction must preserve per-repository/level error visibility.

An error in Feature A must not become an error callout on Feature B.

---

# 43. Offline / Agent Availability Baseline

Local Git operations require the Agent but not necessarily internet connectivity.

GitHub/registry operations naturally require the relevant external service.

Core local Workspace behaviors are designed around an Agent that can work with local Git repositories.

## Feature-era requirement

The same remains true for Features.

Feature creation and local Feature work must not require GitHub connectivity or a remote branch.

---

# 44. Desktop Boundary

Core Workspace behavior is implemented in GrayMoon.App + GrayMoon.Agent and must work in web/container deployments.

GrayMoon Desktop adds native-host integration, such as opening local folders and, in the future, Feature roots in external tools.

## Regression invariant

No existing Workspace capability may become Desktop-only as a side effect of implementing Feature launch actions.

---

# 45. Data Ownership Classification for Feature Migration

The code analysis supports the following ownership split.

## Workspace-shared configuration

```text
Workspace identity/name/root configuration
repository membership
connectors
custom dependency declarations
Workspace file catalog
version-file patterns
AI workflow exclusion preference
other user-authored Workspace settings
```

## Repository/ref shared or repository-level facts

```text
local branch inventory
remote branch inventory
tags
default branch
connector/repository metadata
```

## WorkspaceFeatureContext-specific derived state

```text
current branch/tag/HEAD
GitVersion
incoming/outgoing commits
default divergence
upstream state
sync state
discovered projects
PackageIds / PackageReferences
derived dependency graph
dependency levels
version mismatch diagnostics
version-file current/missing/out-of-date observations
Git Changes status and entries
PR state
Actions state
pending-action notification state
```

This classification should be treated as the default during detailed design unless a specific code path proves that a field belongs elsewhere.

---

# 46. Regression Matrix

The first worktree Feature implementation must pass at least the following behavioral matrix.

| Capability | Workspace context | Feature context | Isolation requirement |
|---|---|---|---|
| Repository grid | unchanged current behavior | same context-relative data | no state overwrite |
| Branch menu | unchanged | replaced by Feature menu | intentional behavior change |
| Prepare Workspace | unchanged | unavailable | intentional behavior change |
| Return to Default | unchanged | unavailable | Feature uses Remove Feature |
| Sync/fetch | special Workspace checkout | selected Feature worktrees | isolated state |
| Git hooks | update Workspace | update owning Feature | exact attribution |
| Projects | current Workspace projects | Feature projects | independent projections |
| Packages | Workspace packages | Feature packages | independent project source |
| Dependency graph | Workspace graph | Feature graph | independent levels/edges |
| Custom dependencies | shared config | shared config | merged per context |
| Version files config | shared config | shared config | observations isolated |
| Update dependencies | Workspace files | Feature files | no cross-context write |
| Restore | Workspace project paths | Feature project paths | no cross-context path |
| Push | Workspace branches | Feature branches | independent upstream |
| Pull/sync commits | Workspace branches | Feature branches | independent |
| PR state | Workspace branches | Feature branches | independent |
| Create PRs | Workspace branches | Feature branches | independent |
| Merge | Workspace PR | Feature PR | context-specific cleanup |
| Actions | Workspace branch runs | Feature branch runs | independent persistence |
| Git Changes | Workspace worktrees | Feature worktrees | path/context isolation |
| Pending notifications | Workspace state | Feature state | independent |
| Jobs/locks | Workspace context | Feature context | Features do not block each other |
| Desktop launch | current Workspace folder behavior | Feature root | Desktop-only enhancement |

---

# 47. Specific Compatibility Tests the Detailed Plan Should Require

The detailed implementation plan should include regression tests for scenarios such as:

## Same repository, three contexts

```text
Workspace / RepoA       -> main
Feature A / RepoA       -> BAM-100
Feature B / RepoA       -> BAM-200
```

Verify independently:

- branch display;
- GitVersion;
- Git Changes;
- PR badge;
- Actions runs;
- incoming/outgoing counts;
- dependency diagnostics.

## Hook isolation

Commit in Feature A.

Expected:

```text
Feature A updates
Workspace unchanged
Feature B unchanged
```

Repeat for checkout, merge, and push hooks.

## Project graph isolation

Add a `.csproj` and PackageReference only in Feature A.

Expected:

- Projects page shows it only in Feature A;
- package page reflects it only in Feature A;
- dependency levels change only in Feature A;
- Push Updated plan changes only in Feature A.

## Version-file isolation

Change a configured version file only in Feature A.

Expected:

- out-of-date line state changes only in Feature A;
- shared version-file configuration itself is unchanged;
- Workspace/Feature B diagnostics remain based on their physical files.

## Concurrent operations

Run Push in Feature A and Update in Feature B.

Expected:

- both may run concurrently subject to Agent concurrency limits;
- no Workspace-wide lock blocks the second operation;
- logs/overlays/errors remain attached to the correct context.

## Special Workspace regression

Create no Features.

Exercise the entire current Workspace flow.

Expected:

- behavior is indistinguishable from the pre-Feature release except where the pre-design explicitly changes wording/internals.

---

# 48. Additional Findings From This Baseline Review

The code review did not invalidate the approved Feature architecture, but it surfaced several areas that the detailed design must explicitly include.

## 48.1 Pending-action notifications are context-specific

This was not originally highlighted as a separate persistence consumer.

`WorkspacePendingActionsService` reads:

```text
dependency mismatches
version-file mismatches
outgoing/incoming commits
upstream state
dependency level
```

All of those become context-derived.

Therefore notifications must follow `WorkspaceFeatureContext`.

No new product decision is required.

## 48.2 Workspaces-page project count needs an explicit source after project migration

The global Workspaces grid currently displays:

```text
workspace.Repositories.Sum(r => r.Projects)
```

Once project discovery moves to context-specific persistence, the global Workspaces page still needs a stable meaning for `Projects`.

Recommended interpretation:

> The Workspaces page project count continues to represent the special Workspace context.

This matches the migration philosophy already approved for `Workspace.IsInSync` / `LastSyncedAt`.

This should be locked in the detailed design unless intentionally changed.

## 48.3 File `IsMissingOnDisk` cannot remain globally mutable

The file catalog is shared, but physical presence can differ by Feature.

Therefore the existing `WorkspaceFile.IsMissingOnDisk` observation must become context-specific or derived at read/check time.

This follows the already-approved version-file observation rule.

## 48.4 Action refresh broadcasts must carry context

The Actions page currently reacts to `RepositorySynced(workspaceId, repositoryId)`.

That becomes ambiguous with multiple worktrees.

This reinforces the already-approved context-aware notification change.

## 48.5 Dependency recomputation boundary becomes context-scoped

`WorkspaceStateRecomputeScope` currently performs one whole-Workspace recomputation after batches of repository writes.

The same pattern should remain, but its unit becomes:

```text
WorkspaceFeatureContext
```

for context-derived file/dependency state.

Shared configuration changes may trigger recomputation of more than one context if required.

---

# 49. Items That Do Not Require More Product Discussion

Based on the current code and approved pre-design, these can be treated as implementation design problems rather than unresolved product questions:

- hook context attribution;
- context-aware project graph persistence;
- context-aware PR/Actions/Git Changes persistence;
- context-aware pending notifications;
- context-aware root resolution;
- context-aware Agent command payloads;
- ref inventory vs checkout-state separation;
- context-level operation locks;
- context-aware SignalR broadcasts;
- special-Workspace meaning for legacy Workspace sync fields/project count;
- file presence/version diagnostics by context.

The product intent is already clear.

---

# 50. Baseline Preservation Rule

The governing rule for the detailed Feature implementation is:

> **If the user selects `Workspace`, GrayMoon must continue to behave like the current Workspace implementation at baseline commit `cd9a17cb21e3ba2757542cc9f55f6fc19e2538c8`, unless the approved Feature pre-design explicitly states otherwise.**

For a selected Feature:

> **The same applicable Workspace capabilities should operate on that Feature's isolated files and persisted context state, while Workspace-shared configuration remains shared.**

Any implementation wave that cannot satisfy these two rules must not proceed to Feature worktrees/UX until the regression gap is fixed. There is no product feature flag; this is a technical sequencing rule within one continuous delivery.

---

# 51. Detailed Design Gate

Before Feature worktrees/UX land in the continuous Features delivery, the implementation plan should demonstrate concrete migration coverage for:

```text
WorkspaceFeatureContext schema/backfill
root/path resolution
repository checkout state
hook attribution
project discovery
dependency graph
version files
Git Changes
PR state
Actions state
pending notifications
jobs/locks
SignalR notifications
special Workspace compatibility
```

The safest sequencing remains:

```text
1. introduce context schema
2. migrate the existing Workspace onto it
3. prove all existing Workspace behavior
4. then create additional worktree Feature contexts in the same continuous delivery
```

This appendix should be used as the acceptance baseline for step 3. There is no product feature flag between these steps.
