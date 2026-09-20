# GrayMoon Worktree Features - Detailed Implementation Design

**Status:** Implementation design  
**Code baseline reviewed through:** `43f79b6484a3a99c65a04a3a55c80f7b615527be`  
**Required companion documents in the same implementation folder:**

1. `GrayMoon-Features-Final-Pre-Design-Decision-Summary.md`
2. `GrayMoon-Workspace-Current-Features-Baseline-Appendix.md`

This document translates the approved product decisions into an implementation architecture and staged migration plan.

---

# 1. Document Authority and Conflict Rules

Every implementation agent must read all three documents before changing code.

Use this precedence if anything appears inconsistent:

```text
1. GrayMoon-Features-Final-Pre-Design-Decision-Summary.md
   = approved product and architectural decisions

2. GrayMoon-Workspace-Current-Features-Baseline-Appendix.md
   = existing behavior that must not regress

3. This detailed implementation design
   = concrete implementation plan
```

If this design accidentally contradicts an approved pre-design decision, the pre-design decision wins.

If an implementation choice would change an existing Workspace behavior not explicitly changed by the pre-design, treat it as a regression.

Do not silently "improve" existing UX while implementing Features.

---

# 2. Implementation Objective

GrayMoon currently assumes one physical checkout for each `WorkspaceRepositoryLink`.

The implementation must evolve that model to support:

```text
Workspace
├─ special Workspace context
├─ Feature BAM-2856
├─ Feature search-index
└─ Feature ...
```

where every context has an independent working tree for every Workspace repository, while Workspace-level configuration remains shared.

The special Workspace context must continue behaving like GrayMoon does today.

The implementation succeeds only when:

```text
no Feature exists
→ GrayMoon is behaviorally indistinguishable from current GM

Feature A exists
→ applicable GM operations act on Feature A when Feature A is selected

Feature A + Feature B exist
→ their checkout-derived state and operations are isolated

Workspace + Feature A + Feature B operate concurrently
→ ordinary operations do not block each other
```

---

# 3. Hard Non-Negotiable Invariants

These invariants must be encoded in tests and code review checklists.

## 3.1 Workspace remains special

`Workspace` is the existing mutable checkout.

It retains:

```text
Prepare Workspace
New Branch
Switch Branch
Create PRs
Return to Default
tag behavior
all current Workspace workflows
```

A Feature does not replace or mutate the special Workspace checkout.

## 3.2 Feature identity equals branch identity

```text
Feature.Name == Git branch name
```

There is no second display-name concept.

## 3.3 Feature contains every Workspace repository

A ready Feature has exactly one linked worktree for every current Workspace repository.

Do not implement partial Features.

## 3.4 Configuration is shared, derived checkout state is context-specific

Shared:

```text
Workspace identity/configuration
repository membership
connectors
custom dependencies
configured Workspace files
version-file patterns
AI workflow exclusion
other user-authored Workspace settings
```

Context-specific:

```text
current branch/tag/HEAD
GitVersion
incoming/outgoing
divergence
upstream
sync status
project discovery
packages
dependency graph and levels
dependency mismatch diagnostics
version-file observations
file missing state
Git Changes
PR state
Actions state
pending-action notification inputs
```

## 3.5 No ambient execution context

Application services must never infer execution context from:

```text
current browser Feature
Blazor circuit state
current route
Desktop state
last selected context
```

Every context-sensitive command/query receives explicit context identity.

## 3.6 Physical paths are not domain identity

Never use a physical path as the primary application identity.

The normal identity is:

```text
WorkspaceFeatureContextId
+ WorkspaceRepositoryId where repository-specific
```

Path is resolved at the boundary.

## 3.7 MCP is deferred

Do not implement an MCP server or MCP tools in this project.

The architecture must remain MCP-ready by keeping operations/query contracts headless and explicit.

---

# 4. Target Architecture

```text
                           GrayMoon UI
                               │
                               │ explicit context id
                               ▼
                    GrayMoon.Application
                     commands / queries
                               │
                   ┌───────────┴───────────┐
                   │                       │
                   ▼                       ▼
          context/path resolver     orchestration/services
                   │                       │
                   └───────────┬───────────┘
                               ▼
                       GrayMoon.Agent
                               │
                               ▼
                 physical Workspace/worktree
```

SQLite separates shared Workspace configuration from per-context projections.

```text
Workspace
│
├─ WorkspaceRepositoryLink
│    shared membership/default-ref metadata
│
├─ WorkspaceFeatureContext  [Workspace]
│    └─ WorkspaceRepositoryContextState × N
│
├─ WorkspaceFeatureContext  [Feature]
│    ├─ WorkspaceFeature
│    ├─ WorkspaceFeatureRepository × N
│    └─ WorkspaceRepositoryContextState × N
│
└─ shared configuration
     ├─ WorkspaceFiles / version patterns
     └─ custom dependency declarations
```

---

# 5. Terminology in Code

Use the approved names consistently.

```text
Workspace
Feature
WorkspaceFeature
WorkspaceFeatureContext
WorkspaceRepositoryContextState
WorkspaceFeatureRepository
PrepareWorkspace
ReturnToDefault
```

Do not introduce:

```text
Environment
Sandbox
WorktreeContext
ActiveFeature   // implies only one Feature may be active globally
WorkingFeature
NewFeature      // old workflow terminology
SyncToDefault
```

"Selected Feature" is acceptable as a UI concept.

"Active context" is acceptable internally for watcher lease/activity tracking, but it must not imply external tools stop when GM selects another Feature.

---

# 6. Target Database Schema

The migration must use **expand → backfill → switch → contract**.

Do not destructively rewrite existing tables and runtime in one migration.

## 6.1 WorkspaceFeature

New table/model:

```text
WorkspaceFeature
- WorkspaceFeatureId            PK
- WorkspaceId                   FK -> Workspace, cascade
- Name                          required, max 200
- LifecycleState                enum
- BaseKind                      enum
- BaseWorkspaceFeatureId?       nullable FK, restrict
- CreatedAt                     UTC
- UpdatedAt                     UTC
- LastError?                    nullable diagnostic
```

Unique:

```text
UNIQUE (WorkspaceId, Name)
```

Initial lifecycle enum:

```text
Creating
Ready
Removing
NeedsRepair
```

Do not add many speculative states.

Initial base enum:

```text
CurrentWorkspace
```

The schema may reserve future values for:

```text
DefaultBranches
Feature
```

but first-release UX must expose only implemented bases.

## 6.2 WorkspaceFeatureContext

New table/model:

```text
WorkspaceFeatureContext
- WorkspaceFeatureContextId     PK
- WorkspaceId                   FK -> Workspace, cascade
- Kind                          Workspace | Feature
- WorkspaceFeatureId?           nullable FK -> WorkspaceFeature, cascade
- CreatedAt                     UTC
- LastSyncedAt?                 nullable
- IsInSync                      bool
```

Rules:

```text
Kind == Workspace
    => WorkspaceFeatureId is null

Kind == Feature
    => WorkspaceFeatureId is not null
```

Uniqueness must not rely on SQLite treating `NULL` as unique.

Use explicit constraints/indexes equivalent to:

```text
one Kind=Workspace context per Workspace
one context per WorkspaceFeature
```

Recommended implementation:

```text
filtered unique index on WorkspaceId WHERE Kind = Workspace
filtered unique index on WorkspaceFeatureId WHERE WorkspaceFeatureId IS NOT NULL
```

Add a check constraint for Kind/FeatureId consistency if EF/SQLite migration support is reliable; otherwise enforce it in application code plus tests.

Every existing Workspace receives exactly one persisted Workspace context during backfill.

## 6.3 WorkspaceFeatureRepository

New table/model for Feature physical worktrees:

```text
WorkspaceFeatureRepository
- WorkspaceFeatureRepositoryId  PK
- WorkspaceFeatureContextId     FK -> context, cascade
- WorkspaceRepositoryId         FK -> WorkspaceRepositoryLink, cascade
- WorktreePath                  required for Feature rows
- BaseCommitSha                 required, 40/64-safe max length
- CreatedAt                     UTC
- State                         Pending | Ready | NeedsRepair
- LastError?                    nullable
```

Unique:

```text
UNIQUE (WorkspaceFeatureContextId, WorkspaceRepositoryId)
```

Do not create these rows for the special Workspace context.

The special Workspace repository path is resolved from the normal Workspace root.

`WorktreePath` is authoritative for an already-created Feature worktree. Do not reconstruct an existing path from Feature name every time.

## 6.4 WorkspaceRepositoryContextState

New table/model:

```text
WorkspaceRepositoryContextState
- WorkspaceRepositoryContextStateId PK
- WorkspaceFeatureContextId
- WorkspaceRepositoryId

- BranchName?
- CheckedOutTag?
- HeadCommit?
- HasNewerTag?
- GitVersion?

- Projects?
- OutgoingCommits?
- IncomingCommits?
- DefaultBranchBehindCommits?
- DefaultBranchAheadCommits?
- BranchHasUpstream?
- SyncStatus

- DependencyLevel?
- Dependencies?
- UnmatchedDeps?

- OutOfDateFileLines?
- OutOfDateFileRepos?
- TotalFileConfigRepos?
- HasSelfFileVersionToken?
- TotalFileLines?

- RepositoryType?
```

Unique:

```text
UNIQUE (WorkspaceFeatureContextId, WorkspaceRepositoryId)
```

This is the target owner for the mutable checkout/derived fields currently stored on `WorkspaceRepositoryLink`.

## 6.5 Fields that remain on WorkspaceRepositoryLink

Long-term shared membership/reference metadata should remain on the link.

At minimum:

```text
WorkspaceRepositoryId
WorkspaceId
RepositoryId
DefaultBranchName
```

`DefaultBranchName` represents repository/default-ref metadata and is not current-HEAD state.

All context-derived fields listed in §6.4 stop being authoritative on the link after cutover.

During rollback window, old columns may remain physically present, but application runtime must not indefinitely dual-write them.

## 6.6 Shared ref inventory

Keep `RepositoryBranches` repository/link scoped.

It represents:

```text
local branch inventory
remote branch inventory
tags
default marker
last-seen ordering
```

It must not represent "current branch".

Current checkout branch/tag/HEAD belongs in `WorkspaceRepositoryContextState`.

This matches Git worktree semantics: branch refs are shared while each worktree has an independent `HEAD`.

## 6.7 Context projects and project dependencies

Do not let a Feature project scan overwrite the special Workspace project graph.

Preferred migration:

1. add `WorkspaceFeatureContextId` to project persistence in an additive migration;
2. backfill every existing project to the Workspace context;
3. update uniqueness from:

```text
WorkspaceId + RepositoryId + ProjectName
```

to:

```text
WorkspaceFeatureContextId + RepositoryId + ProjectName
```

4. switch every project query/merge/dependency operation to context;
5. only later remove obsolete Workspace-only assumptions.

`ProjectDependency` remains naturally context-scoped through the project IDs it references; dependency creation must enforce that both projects belong to the same context.

Generated/virtual package rows are also context projections.

Never allow project refresh in one context to delete generated/project rows in another context.

## 6.8 PR persistence

Current `WorkspaceRepositoryPullRequests` is 1:1 with `WorkspaceRepositoryId`.

Target must be 1:1 with:

```text
WorkspaceFeatureContextId + WorkspaceRepositoryId
```

For migration safety, prefer a new context-aware table/model rather than immediately rebuilding the existing PK table:

```text
WorkspaceRepositoryContextPullRequests
```

Backfill the current PR projection into the special Workspace context.

Switch service reads/writes.

Drop old table only in contract phase.

## 6.9 Actions persistence

Use the same pattern:

```text
WorkspaceRepositoryContextActions
- WorkspaceFeatureContextId
- WorkspaceRepositoryId
- existing action fields
```

Unique one row per context/repository.

Existing `WorkflowsJson` semantics remain.

## 6.10 Git Changes persistence

Use context-aware status/entry persistence.

Target:

```text
WorkspaceGitContextRepositoryStatus
- WorkspaceFeatureContextId
- WorkspaceRepositoryId
- snapshot fields

WorkspaceGitContextChangeEntry
- PK
- WorkspaceFeatureContextId
- WorkspaceRepositoryId
- path fields
```

Every snapshot update replaces only the entries for one:

```text
(context, workspace repository)
```

Never delete entries for another context.

## 6.11 Workspace file observations

`WorkspaceFile` remains shared configuration:

```text
RepositoryId
FileName
relative FilePath
version config
```

Move physical observation state out of the shared row.

New:

```text
WorkspaceFileContextState
- WorkspaceFeatureContextId
- FileId
- IsMissingOnDisk?
- LastCheckedAt?
```

Unique `(ContextId, FileId)`.

`WorkspaceFileLineStatus` is context-derived.

Add/contextualize it so uniqueness includes `WorkspaceFeatureContextId`.

A Feature may report a configured file missing while Workspace reports it present.

## 6.12 Workspace legacy sync fields

Keep:

```text
Workspace.LastSyncedAt
Workspace.IsInSync
```

with their current **special Workspace** meaning.

Feature sync status comes from `WorkspaceFeatureContext`.

When special Workspace context changes sync state, update both context and legacy Workspace fields for compatibility.

When a Feature context changes, do not update legacy Workspace fields.

## 6.13 Feature storage root

Add a stable Workspace-level managed storage root or equivalent persisted setting.

Default on first Feature creation:

```text
<parent of Workspace root>\.graymoon\<WorkspaceName>\features
```

Example:

```text
C:\Workspace\AVR
→
C:\Workspace\.graymoon\AVR\features
```

Persist the resolved managed Feature storage root so later Workspace renames do not silently relocate existing worktrees.

Existing `WorkspaceFeatureRepository.WorktreePath` remains authoritative.

Do not automatically move existing linked worktrees during ordinary Workspace rename.

---

# 7. Migration Strategy

## 7.1 Migration Wave A - additive context schema

Add:

```text
WorkspaceFeatures
WorkspaceFeatureContexts
WorkspaceFeatureRepositories
WorkspaceRepositoryContextStates
Feature storage root field/preference
```

Backfill:

```text
one WorkspaceFeatureContext(kind=Workspace) per existing Workspace

one WorkspaceRepositoryContextState
per existing WorkspaceRepositoryLink
copying all current mutable state
```

Runtime continues using old fields.

Acceptance gate:

```text
existing database migrates
new database creates
all existing tests pass
no UI behavior changes
```

## 7.2 Migration Wave B - additive context projection tables

Add/backfill:

```text
context project identity
context PR rows
context Actions rows
context Git Changes rows
file context state
context file line status
```

Feature UI is not wired yet in this migration wave; it follows later in the same continuous delivery.

Acceptance gate:

- row counts for special Workspace projection match old data;
- no existing behavior changes;
- rollback to old runtime remains possible during the approved rollback window.

## 7.3 Switch phase

Move reads/writes one subsystem at a time to context tables.

Once a subsystem is switched, the **new context store is its only runtime source of truth**.

Do not maintain indefinite dual-write.

Old tables/columns may remain physically for rollback but must not be treated as authoritative by switched code.

## 7.4 Contract phase

Only after the Workspace-only context architecture has been manually verified:

- remove obsolete mutable columns/tables;
- tighten nullable context FKs;
- rebuild indexes if needed;
- remove compatibility code.

Contract is a separate PR/wave, never bundled with initial cutover.

---

# 8. Core Context Services

Introduce a small, explicit context service layer.

## 8.1 IWorkspaceFeatureContextResolver

Responsibilities:

```text
Get special Workspace context for WorkspaceId
Get context by ContextId
validate context belongs to Workspace
get Feature metadata for Feature context
ensure backfilled Workspace context exists
```

It must not read Blazor UI state.

## 8.2 IWorkspaceContextPathResolver

Responsibilities:

```text
GetContextRoot(contextId)
GetRepositoryPath(contextId, workspaceRepositoryId)
GetRepositoryPath(contextId, repositoryId)
```

Rules:

### Workspace context

```text
context root = existing Workspace root
repository path = Workspace root / repository name
```

### Feature context

```text
context root = persisted Feature root / common Feature folder
repository path = WorkspaceFeatureRepository.WorktreePath
```

Do not let callers manually concatenate `.graymoon` paths.

## 8.3 Path canonicalization

Normalize paths consistently:

- full path;
- trim trailing separator except root;
- use OS-appropriate comparison;
- Windows path matching case-insensitive;
- resolve only paths belonging to the expected context.

Create one canonical path helper and use it in:

```text
hook attribution
Git Changes registry
worktree reconciliation
Desktop launch
context resolver validation
```

---

# 9. Application Contract Migration

Context-sensitive application contracts must accept explicit context identity.

Example target:

```text
IWorkspaceUpdateOperations.UpdateAsync(
    int workspaceFeatureContextId,
    ...)

IWorkspacePushOperations.PushAsync(
    int workspaceFeatureContextId,
    ...)

IWorkspaceGitChangesOperations.GetAsync(
    int workspaceFeatureContextId,
    ...)

IWorkspaceFileOperations.UpdateVersionsAsync(
    int workspaceFeatureContextId,
    ...)
```

Repository-specific operations additionally receive `repositoryId` or `workspaceRepositoryId`.

Do not pass both `workspaceId` and `contextId` unless the method genuinely performs Workspace-structural work.

Resolve Workspace from the context.

## 9.1 Workspace-only application operations

The following remain Workspace-only product workflows:

```text
Prepare Workspace
Return to Default
Workspace repository membership
Workspace import
Feature creation/removal structural orchestration
```

They may continue taking `workspaceId`, but their internal mutable-state work must explicitly resolve the special Workspace context.

## 9.2 REST compatibility

Do not break existing Workspace REST routes.

Legacy routes:

```text
/api/workspaces/{workspaceId}/update
/api/workspaces/{workspaceId}/push
...
```

continue to mean **special Workspace context**.

The REST adapter resolves the special context then calls the final context-aware application operation.

Do not make application services infer that behavior.

Context-aware REST routes can be added later if useful, but are not required for first Feature UX.

---

# 10. Context-Aware Repository State Writer

`WorkspaceRepositoryStateWriter` is the primary migration seam.

Refactor it to apply snapshots to:

```text
WorkspaceRepositoryContextState
```

using:

```text
WorkspaceFeatureContextId
WorkspaceRepositoryId
```

It must retain the existing "probed groups only" semantics.

A partial Agent response must never blank unrelated state.

Recommended signature:

```text
ApplyAsync(
    int workspaceFeatureContextId,
    int repositoryId,
    RepositoryStateSnapshot snapshot,
    RepositoryStateWriteOptions options,
    CancellationToken)
```

The writer resolves `WorkspaceRepositoryId` from context.WorkspaceId + repositoryId and validates membership.

Any special Workspace legacy mirrors are written in one compatibility function only.

Tests must prove:

```text
apply Feature A snapshot
→ Workspace state unchanged
→ Feature B state unchanged
```

---

# 11. Context-Aware Recompute Boundary

Refactor:

```text
WorkspaceStateRecomputeScope
```

from Workspace-wide derived-state ownership to context ownership.

Target:

```text
CompleteAsync(workspaceFeatureContextId)
RecomputeAsync(workspaceFeatureContextId)
```

It must run exactly once at the end of a batch and recompute only:

```text
selected context file-version observations
selected context dependency stats
selected context sync aggregate
```

Then broadcast a context-specific sync event.

If context is special Workspace, mirror legacy Workspace sync fields and legacy notifications as required for compatibility.

Shared configuration changes such as custom dependencies may intentionally schedule recomputation for every existing context, but that fanout is a separate explicit action.

---

# 12. Git Ref Inventory and Branch Operations

`RepositoryBranches` remains shared per `WorkspaceRepositoryLink`.

Ref refresh from any context may update shared:

```text
local branches
remote branches
tags
default marker
```

Current branch/tag/HEAD must never be written into shared inventory.

Workspace Branch UX remains special-Workspace-only.

Feature contexts do not expose:

```text
New Branch
Switch Branch
Return to Default
tag checkout
```

Feature branch identity is immutable during the normal Feature lifecycle.

---

# 13. Git Hooks - Worktree-Safe Attribution

This area must be implemented before Feature worktrees are used.

Git linked worktrees have independent `HEAD`s but use the repository's common Git data for shared refs and hooks.

Therefore GrayMoon must not write a different static hook script for each Feature.

## 13.1 Hook script rule

The installed hook is context-agnostic.

At execution time it obtains the actual working-tree root dynamically, e.g. through Git:

```text
git rev-parse --show-toplevel
```

The hook POST includes:

```text
workspaceId
repositoryId
repositoryPath = actual top-level path executing the hook
hook kind
```

Do not embed a static `WorkspaceFeatureContextId` in a shared hook file.

Do not enable `extensions.worktreeConfig` solely to store GrayMoon context metadata. Older Git compatibility and existing repository config safety are more important.

## 13.2 App attribution

The App resolves notification path to exactly one context:

### special Workspace path

match against:

```text
Workspace root + repository name
```

### Feature path

match against:

```text
WorkspaceFeatureRepository.WorktreePath
```

The resulting identity is:

```text
WorkspaceFeatureContextId
WorkspaceRepositoryId
```

If the path cannot be resolved unambiguously:

- do not write any context state;
- log a structured warning;
- request a reconciliation/full sync if appropriate.

Never default an unknown hook to the special Workspace.

## 13.3 RepositorySyncNotification

Extend notification contracts with enough information for verification:

```text
WorkspaceId
RepositoryId
RepositoryPath
optional resolved WorkspaceFeatureContextId
state snapshot
```

If Agent supplies context id from a cache, App still validates it against the path before persistence.

Correctness must not depend on Agent in-memory registry surviving restart.

---

# 14. Git Changes

Preserve the current watcher-driven architecture.

## 14.1 Registry

Change Agent registry mapping from:

```text
repoPath -> WorkspaceId + RepositoryId
```

to:

```text
repoPath -> WorkspaceFeatureContextId + WorkspaceRepositoryId
```

The registration message from App must be explicit.

## 14.2 Snapshot identity

Every pushed snapshot must carry context identity.

The App validates:

```text
context exists
context belongs to workspace
repository belongs to workspace
registered path matches resolved repository path
```

before enqueuing persistence.

## 14.3 Persistence

The write queue remains serialized, but replacement is scoped to one context/repository.

## 14.4 Monitoring activity

Refactor activity tracking from Workspace to context.

A context is active when:

```text
selected by a GM page
recently used by a GM operation
explicitly refreshed/scanned
watcher activity renews lease
still within existing grace window
```

Do not permanently watch every Feature.

When a dormant Feature is selected, trigger the existing immediate/on-open activation/scan behavior.

## 14.5 Repositories page activation

Preserve the newly implemented behavior where opening Repositories activates Git Changes watchers.

Apply it to the selected context.

---

# 15. Project Discovery and Dependency Graph

This is a core migration, not a follow-up.

Every method in `WorkspaceProjectRepository` that currently takes `workspaceId` and reads checkout-derived projects must become context-aware.

Examples include:

```text
MergeWorkspaceProjectsAsync
MergeWorkspaceProjectDependenciesAsync
GetSyncDependenciesPayloadAsync
GetPushPlanPayloadAsync
GetPushDependencyInfoForRepoAsync
GetPushDependencyInfoForRepoSetAsync
RecomputeAndPersistRepositoryDependencyStatsAsync
project/package list queries
dependency graph queries
```

## 15.1 Project merge isolation

Merge key:

```text
ContextId + RepositoryId + ProjectName
```

A refresh of RepoA in Feature A may delete/update only Feature A's physical project rows for RepoA.

It must not touch:

```text
Workspace RepoA project rows
Feature B RepoA project rows
generated rows owned by another context
```

## 15.2 Dependency edge isolation

Only create an edge if:

```text
DependentProject.ContextId == ReferencedProject.ContextId
```

Add an invariant test.

## 15.3 Custom dependencies

Remain shared configuration between `WorkspaceRepositoryLink`s.

During context graph construction:

```text
context discovered edges
+ context file-version-derived edges
+ context generated-package edges
+ shared custom edges
→ context graph
```

## 15.4 Dependency levels

Store computed level on `WorkspaceRepositoryContextState`.

The Repositories page groups by selected context level.

## 15.5 Generated packages

Treat generated package rows as context projections.

They may be regenerated from shared config, but each context owns its own projection.

Preserve today's rule that a normal physical project scan does not accidentally delete generated rows.

---

# 16. Files and Version Files

`WorkspaceFile` and `WorkspaceFileVersionConfig` remain shared.

All filesystem operations require context path resolution.

Refactor:

```text
WorkspaceFileOperations
WorkspaceFileSearchService
WorkspaceFileVersionService
WorkspaceFiles page
```

to take/resolve explicit context.

## 16.1 Token resolution

`{@repo}`, `{@repo:commit}`, `{@repo:branch}` values must come from the selected context.

Commit retrieval may remain on-demand where planned.

## 16.2 File missing state

Never persist a single shared `WorkspaceFile.IsMissingOnDisk` as authoritative after cutover.

Use `WorkspaceFileContextState`.

## 16.3 Line status

Every line status row belongs to one context.

A Feature version mismatch must not affect Workspace badge counts.

## 16.4 Updating versions

Update only files physically inside the selected context.

Commit of updated files must commit the selected context branch.

---

# 17. PR State

Refactor `WorkspacePullRequestService` and repository persistence to accept context.

Branch lookup comes from `WorkspaceRepositoryContextState`.

Cache key must include context or otherwise guarantee branch/context correctness.

Recommended:

```text
(ContextId, RepositoryId, Branch)
```

A branch change within special Workspace clears/reconciles only special Workspace PR projection.

Feature PR remains untouched.

The Repositories header query must compute `HasCreatablePr` from the same
selected-context eligibility semantics used by the PR create badge/flow:

```text
not on tag
AND commits ahead of default > 0
AND no open/merged/closed PR
```

This drives the existing Workspace primary Branch/Create PR shortcut and must
not read PR/divergence state from another context.

`Create PRs` in a Feature uses the Feature branch in every repository.

Merge dialog local-state checks use the selected context's Git Changes and commit state.

---

# 18. GitHub Actions

Actions page/state is context-relative because the branch differs by context.

Refactor:

```text
WorkspaceActionService
WorkspaceActions loading/refresh
persisted action repository
RepositorySynced reaction
background polling
```

to context identity.

Persist:

```text
ContextId + WorkspaceRepositoryId
```

The page must verify persisted branch matches selected context branch exactly as today, but within context.

AI-workflow exclusion remains Workspace-shared preference.

---

# 19. Pending Action Notifications

`WorkspacePendingActionsService` currently derives notifications from mutable link state.

Refactor its computation input to selected context snapshots.

Notification identity must include context.

Do not show a special Workspace pending notification as though it belongs to Feature A.

If global shell UI displays multiple notifications, include Feature name where needed using existing GM presentation patterns; do not invent decorative UI.

---

# 20. Context-Aware SignalR Events

Introduce context identity into context-specific browser broadcasts.

Conceptual events:

```text
ContextSynced(workspaceId, contextId)
RepositoryContextSynced(workspaceId, contextId, repositoryId)
RepositoryContextError(...)
GitChangesUpdated(workspaceId, contextId, repositoryId?)
```

Compatibility:

- special Workspace may temporarily emit legacy `WorkspaceSynced` / `RepositorySynced` too;
- Feature events must never be sent as legacy Workspace events that make old handlers refresh the wrong state.

Migrate pages one by one, then remove compatibility emissions when no longer needed.

---

# 21. Concurrency and Operation Runner

The current one-mutation-per-Workspace rule must become hierarchical.

## 21.1 Operation scopes

Represent operation scope explicitly:

```text
Workspace structural scope
Context mutation scope
```

A context mutation has both:

```text
WorkspaceId
WorkspaceFeatureContextId
```

## 21.2 Rules

Context operation may start when:

```text
no Workspace structural operation is running
AND
no mutation is running for the same context
```

Two different contexts in the same Workspace may run concurrently.

Workspace structural operation may start only when:

```text
no structural operation is running
AND
no context mutation is running for any context in that Workspace
```

This prevents Feature creation/removal/repository-membership changes racing normal context work.

## 21.3 Required tests

```text
Feature A push + Feature B update -> both start
Workspace push + Feature A update -> both start
Feature A push + Feature A commit -> second blocked
Feature creation + any context mutation -> mutually exclusive
repository-membership edit + Feature operation -> mutually exclusive
```

## 21.4 BackgroundJobService

Overlay/job keys include context where context-specific.

Do not allow Feature A overlay to appear as Feature B's operation.

Preserve the existing rule that page-scoped overlays do not magically follow unrelated routes.

---

# 22. UI Context Selection

## 22.1 Shared component

Create:

```text
WorkspaceContextBar
    ├─ WorkspaceFeatureSelector
    └─ existing page FilterSearchInput
```

Do not put Feature semantics into the generic `FilterSearchInput`.

The control must follow existing GM styling exactly.

No new decorative icon system.

## 22.2 Selector options

```text
Workspace
────────────
Feature A
Feature B
...
```

Typing filters existing Features.

Exact match + Enter selects.

No exact match + valid Git branch name + Enter opens Create Feature dialog with name prefilled.

Escape cancels editing.

## 22.3 Route identity

Preserve all existing Workspace page routes.

Use an explicit query parameter for Feature context deep links:

```text
?context=<WorkspaceFeatureContextId>
```

Special Workspace may omit the query parameter.

Resolution order on page load:

1. valid explicit query context belonging to Workspace;
2. persisted last-selected context if no explicit query;
3. special Workspace context fallback.

If persisted Feature is selected and route has no query, canonicalize with `NavigationManager.NavigateTo(..., replace: true)` so back/forward/deep links become explicit.

Do not pass the selected context to application services through a scoped ambient service; the page resolves it and passes the id.

## 22.4 Persisted last selection

Use a small preference service backed by existing Settings/persistence patterns.

It stores last-selected ContextId per Workspace.

It is only an initial-navigation preference, never execution authority.

## 22.5 Navigation

Workspace nav links preserve the selected context query parameter.

Search/filter query parameters such as Actions `q=` must coexist.

---

# 23. Context-Aware Workspace Pages

All of these pages must resolve the same selected context:

```text
Repositories
Changes
Projects
Packages
Files
Dependencies
Actions
```

For `Workspace` context, behavior remains the baseline.

This includes current header quick-action behavior: when the selected Workspace
context has at least one creatable PR, the primary Branch button becomes yellow
`Create PR` and invokes the existing multi-repository PR creation flow while
the split caret continues to open the Branch menu.

For Feature context, each page uses context projections and paths.

Do not create separate `/features/...` page implementations.

---

# 24. Branch vs Feature Menu

## Workspace selected

Keep the existing split Branch control and its current shortcut behavior.

Normal state:

```text
[ Branch ][▼]
```

When at least one Workspace repository is eligible to create a pull request
(ahead of default, not on a tag, and without an open/merged/closed PR), the
primary half becomes the existing yellow quick action:

```text
[ Create PR ][▼]
```

Behavior:

```text
primary "Branch"
→ opens Branch dialog

primary "Create PR"
→ opens the existing Create PR flow for all eligible repositories

caret ▼
→ always opens the Branch menu
```

The Branch dropdown remains the Workspace lifecycle menu:

```text
Prepare Workspace
New Branch
Switch Branch
Create PR
Return to Default
```

This is a UX shortcut over the existing PR eligibility/query state. It does not
change branch or PR domain architecture.

After context migration, `HasCreatablePr` must be computed from the selected
context's divergence/tag/PR projection. For the special Workspace context, the
behavior must remain identical to commit `43f79b6484a3a99c65a04a3a55c80f7b615527be`.

## Feature selected

Use:

```text
Feature ▼
├─ Create PRs
└─ Remove Feature
```

`Remove Feature` has no ellipsis.

Desktop may append native launch actions.

Do not show:

```text
Prepare Workspace
New Branch
Switch Branch
Return to Default
```

inside a Feature.

---

# 25. Feature Storage and Creation

## 25.1 First-release base support

First Feature release implements:

```text
Based on: Current Workspace
```

Architecture may reserve future base kinds, but do not expose unfinished options.

"Current Workspace" means the **special Workspace context's committed HEAD per repository**, not dirty files.

## 25.2 Creation preflight

Feature creation is a Workspace-structural operation.

Before mutation:

1. validate branch name using Git-authoritative branch/ref validation;
2. reject duplicate `WorkspaceFeature.Name`;
3. resolve every Workspace repository;
4. read special Workspace `HEAD` SHA for every repo;
5. inspect `git worktree list --porcelain`;
6. detect local branch with same name;
7. detect remote branch with same name if remote refs are available locally;
8. detect branch checked out by any worktree;
9. compute target paths;
10. ensure target directories are absent or safely empty;
11. verify each repository is a usable Git checkout.

Normal creation stops on an existing branch-name collision.

Do not auto-adopt, force, or rename in first release.

Remote collision discovered later during push remains a reconciliation condition.

## 25.3 Dirty Workspace

Do not reject Feature creation because special Workspace is dirty.

Persist/use:

```text
git rev-parse HEAD
```

per repository as the base.

Dirty/staged files remain in Workspace only.

Create dialog must show the approved explanatory text.

## 25.4 Persist creation intent before worktree mutation

Transaction:

```text
create WorkspaceFeature state=Creating
create WorkspaceFeatureContext kind=Feature
create WorkspaceFeatureRepository rows state=Pending with BaseCommitSha + intended path
commit DB
```

Then perform Git worktree creation.

This makes crash recovery possible.

## 25.5 Agent worktree command

Add a narrow Agent command for one repository, e.g.:

```text
CreateGitWorktree
```

Input:

```text
mainRepositoryPath
worktreePath
branchName
baseCommitSha
```

Execution:

```text
git worktree add -b <branch> <worktreePath> <baseCommitSha>
```

Never use `--force` for normal creation.

Return structured result including:

```text
success
canonical worktree path
HEAD SHA
current branch
error code/message
```

## 25.6 Bounded orchestration

Create worktrees with existing bounded concurrency conventions.

Persist each repository row `Ready` only after Agent confirms the expected branch/path/HEAD.

If any repository fails:

```text
Feature.LifecycleState = NeedsRepair
failed repository row = NeedsRepair
successful rows stay Ready
Feature is not selectable as Ready
```

Do not delete evidence of partial creation.

## 25.7 Retry/idempotency

Retry creation must:

- inspect every expected worktree;
- if expected path already exists and is the expected branch/Feature, mark it Ready;
- if missing, create it;
- if path/branch points somewhere unexpected, stop and mark repair required.

Never create a second worktree for the same Feature repository merely because the first attempt's DB update failed.

## 25.8 Initial context sync

After all worktrees exist:

1. run repository sync against each Feature worktree;
2. write context state;
3. discover context projects;
4. compute context dependency graph;
5. compute file-version observations;
6. initialize PR/Actions caches as empty/unverified;
7. optionally activate Git Changes when context becomes selected.

Only after required initialization succeeds:

```text
Feature.LifecycleState = Ready
```

Then switch GM UI to the new Feature.

Feature creation does not push.

---

# 26. Worktree Git Semantics

Agents must not treat a linked worktree as a normal independent clone.

Important rules:

```text
HEAD is per worktree
refs/heads/* are shared inside one Git repository
remote refs are shared
hooks are in the common Git directory
each linked worktree has a .git file pointing to private worktree metadata
```

Do not:

- copy `.git` directories;
- edit `.git/worktrees/*` manually;
- move worktrees with ordinary filesystem move;
- bypass `git worktree` commands;
- force checkout the same branch in two worktrees.

Use Git commands for worktree lifecycle.

---

# 27. Feature Removal - Analyze → Authorize → Execute

Implement Feature removal from day one using the Return-to-Default pattern.

## 27.1 IWorkspaceFeatureOperations

Application contract should expose concepts equivalent to:

```text
AnalyzeRemoveFeatureAsync(featureContextId)
RemoveFeatureAsync(featureContextId, options)
```

No modal logic in the domain operation.

## 27.2 Removal analysis

Refresh enough state to report per repository:

```text
worktree exists?
current branch/HEAD
uncommitted changes
staged changes
conflicts
outgoing/unpushed commits
upstream/remote branch
PR number/state/merged
branch used by another worktree?
local branch deletable safely?
remote cleanup possible/known?
```

Aggregate classification:

```text
Completed       // merged PR path
Abandoned       // closed unmerged or explicit discard required
Active          // not completed
NeedsRepair     // physical/DB mismatch
```

Do not require internet to analyze local safety.

Remote PR refresh failure means remote completion state is unknown; local facts still remain usable.

## 27.3 Options

Explicit choices should include only necessary policy, for example:

```text
AllowDiscardUncommitted
AllowForceDeleteLocalBranches
DeleteRemoteBranches
CloseOpenPullRequests if product flow requires
```

Exact DTO names can follow existing Return-to-Default conventions.

Do not infer destructive permission because the caller is UI/REST/future MCP.

## 27.4 Merged PR

Merged PR is the clean completion signal.

Allow:

```text
remove worktree
delete local Feature branch safely
delete remote branch when requested/allowed by guardrail
```

## 27.5 Closed but unmerged PR

Treat as abandoned work.

Removal is allowed only through explicit destructive confirmation.

## 27.6 No completed PR / abort

Show the approved warning of what will be lost.

User may explicitly abandon.

## 27.7 Offline behavior

Local cleanup must remain possible offline.

Remote branch deletion is optional.

If remote deletion was requested but fails after local cleanup:

- do not recreate the local worktree;
- retain Feature metadata in `NeedsRepair` / removal-incomplete state;
- store enough error information to retry remote cleanup or finalize local-only cleanup.

## 27.8 Execution order per repository

Safe order:

1. revalidate worktree/path/branch;
2. if dirty and discard authorized, remove worktree using appropriate Git worktree removal force only after explicit authorization;
3. otherwise clean remove:
   ```text
   git worktree remove <path>
   ```
4. verify worktree no longer listed;
5. delete local branch:
   ```text
   git branch -d <feature>
   ```
   or `-D` only when explicit safety rule authorizes;
6. optionally delete remote branch:
   ```text
   git push origin --delete <feature>
   ```
7. mark repository cleanup complete.

After all repository cleanup succeeds:

- delete Feature/context projections;
- delete context state;
- delete `WorkspaceFeature`;
- remove empty Feature root directories.

On partial failure, keep metadata for retry.

---

# 28. Worktree Reconciliation and Recovery

Add an Agent command equivalent to:

```text
ListGitWorktrees
```

using:

```text
git worktree list --porcelain
```

GrayMoon recovery compares:

```text
DB expected worktrees
vs
Git actual worktrees
vs
filesystem paths
```

Cases:

## DB row + matching Git worktree

Healthy.

## DB row + missing worktree

Mark Feature/row `NeedsRepair`.

Do not silently recreate.

## Git worktree under GM managed Feature root + missing DB

Recognize as a recovery candidate only.

Do not auto-adopt.

First-release UX may surface repair required without implementing full import.

## arbitrary external worktree

Ignore except where it creates a branch collision.

## stale/prunable Git worktree record

Do not call destructive `git worktree prune` globally without a targeted user/recovery operation.

---


# 28A. Branch Dialog Worktree Awareness

The special Workspace branch dialog must classify local branches against actual Git worktree occupancy.

This is required both for GrayMoon Features and for external worktrees created by tools such as Claude, IDEs, or manual Git commands.

## 28A.1 Agent worktree inventory

Extend the Agent worktree inventory primitive so branch UI can consume structured worktree occupancy.

Use:

```text
git worktree list --porcelain
```

Return entries equivalent to:

```text
GitWorktreeInfo
- WorktreePath
- HeadSha
- BranchRef?
- BranchName?
- IsDetached
- IsBare
- IsPrunable
```

Do not infer worktree occupancy by parsing branch-delete errors.

## 28A.2 Local branch classification

When opening or refreshing the Locals tab, combine:

```text
persisted/shared local branch inventory
+
Git worktree inventory
+
WorkspaceFeatureRepository ownership
```

to produce a branch presentation model equivalent to:

```text
LocalBranchView
- Name
- IsCurrent
- WorktreeKind = None | GrayMoonFeature | External
- WorktreePath?
- WorkspaceFeatureContextId?
- FeatureName?
```

Classification:

```text
branch checked out in current special Workspace
→ Current

branch checked out in worktree path owned by a GrayMoon Feature
→ GrayMoonFeature

branch checked out in another linked worktree not owned by GrayMoon
→ External

otherwise
→ None
```

## 28A.3 UI presentation

Use existing GM badge styling.

```text
main                              [Current]
BAM-2856                          [Feature]
external-experiment               [Worktree]
normal-local-branch
```

No new decorative icons.

The exact badge color should follow existing GrayMoon badge conventions; yellow is appropriate for the non-current informational worktree/Feature state if it matches the existing palette.

## 28A.4 Checkout rules

A branch classified as:

```text
GrayMoonFeature
External
```

must remain visible/searchable but must not be selectable for checkout from the special Workspace.

Do not let the user reach the predictable Git error:

```text
branch is already checked out at <path>
```

The row should explain why the branch is unavailable using existing GM tooltip/help conventions.

## 28A.5 Deletion rules

Normal branch deletion remains only for:

```text
WorktreeKind=None
AND branch is not current
```

For `GrayMoonFeature`:

```text
ordinary Delete Branch is not available
cleanup action routes to AnalyzeRemoveFeature / RemoveFeature
```

For `External`:

```text
ordinary Delete Branch is replaced by Remove Worktree cleanup
```

Never call `git branch -d/-D` first for an occupied worktree branch.

---

# 28B. External Worktree Cleanup

External worktrees are Git worktrees known to Git but not owned by GrayMoon Feature metadata.

This flow is intentionally smaller than GrayMoon Feature removal.

## 28B.1 Application contract

Add a headless operation pattern equivalent to:

```text
AnalyzeExternalWorktreeCleanupAsync(...)
RemoveExternalWorktreeAsync(..., options)
```

Do not put safety logic only inside `SwitchBranchModal`.

## 28B.2 Analysis

Resolve and validate the target from Git worktree inventory.

Report:

```text
repository
branch
worktree path
HEAD SHA
staged changes
unstaged changes
conflicts
upstream state
unpushed commits
is dirty
can remove normally
requires force
```

Do not require GitHub or network.

## 28B.3 Safety

Clean worktree:

```text
git worktree remove <path>
```

Dirty worktree:

```text
block normal removal
require explicit destructive confirmation
then git worktree remove --force <path>
```

After successful worktree removal, optionally remove the local branch:

```text
git branch -d <branch>
```

Use:

```text
git branch -D <branch>
```

only when the user explicitly authorized destructive branch deletion.

## 28B.4 Ownership

External cleanup must not create:

```text
WorkspaceFeature
WorkspaceFeatureContext
WorkspaceFeatureRepository
```

and must not silently adopt the worktree.

## 28B.5 Partial failure

If worktree removal succeeds but branch deletion fails:

- report that exact result;
- do not attempt to recreate the worktree;
- refresh branch/worktree inventory;
- leave the branch visible as a normal local branch if no longer occupied.

## 28B.6 Detached worktrees

Detached linked worktrees do not need a row in the Branch dialog because there is no local branch row to decorate.

They may be surfaced later in a dedicated recovery/worktree-management UI if needed.


# 29. External Branch Collision Policy

A Feature can be local-only.

When a push later discovers:

```text
origin/<FeatureName>
```

already exists unexpectedly:

- do not force push;
- return structured `RemoteBranchCollision`;
- keep local Feature healthy;
- allow user to continue working locally;
- future UX can support rename/adopt/reconcile.

Do not solve "taken branch rename" in first worktree implementation unless explicitly added later.

---

# 30. Push and Upstream

Feature branch upstream is optional.

On normal push:

```text
no upstream
→ push -u origin <branch>
```

using existing authentication rules.

Remote publication is not part of Feature creation.

All dependency-aware push planning uses selected context's graph.

Synchronized push still waits on package versions exactly as baseline.

---

# 31. Workspace Repository Membership Changes

Repository membership is shared configuration and therefore structural.

Do not allow membership mutation concurrently with context operations.

## Adding repository

Before completing membership change when Features already exist, choose one deterministic policy.

Recommended first-release policy:

```text
block adding/removing Workspace repositories while any Feature exists
```

unless the implementation includes fully transactional fanout across every Feature.

Reason: the hard invariant says every Feature contains every Workspace repository.

This is safer than silently leaving existing Features partial.

If product later wants live membership changes, implement a dedicated structural migration that creates/removes corresponding worktrees in every Feature.

The UI message must use existing GM callout/dialog style.

## Removing repository

Same rule.

Do not delete a WorkspaceRepositoryLink while Feature worktrees/context state still reference it.

---

# 32. Workspace Rename / Root Change

Feature worktree paths are persisted.

Workspace rename must not rename Feature storage automatically.

Workspace root changes can invalidate Git linked-worktree metadata if underlying main repositories physically move.

After root change:

- normal sync validates every linked worktree;
- if Git reports broken worktrees, mark Feature `NeedsRepair`;
- do not silently edit `.git/worktrees` metadata;
- a future repair action may use `git worktree repair`.

Do not bundle complex automatic worktree relocation into first implementation.

---

# 33. Desktop Integration

Core Features work without Desktop.

For Desktop, extend the existing native message bridge.

Add a typed command conceptually:

```text
OpenDevelopmentTool
- tool: Cursor | VisualStudio | Terminal | Explorer
- path: resolved Feature root
```

App resolves path.

Desktop validates the path exists before launching.

Do not expose these actions in web/container deployments.

The base Feature menu remains:

```text
Create PRs
Remove Feature
```

---

# 34. Feature Context and Desktop Workspace Context

If Desktop title/context synchronization currently receives Workspace identity, it may be extended with:

```text
WorkspaceFeatureContextId
ContextKind
FeatureName?
```

Do not make Desktop the authoritative holder of selected Feature.

The browser/App context remains authoritative for its own commands.

---

# 35. No Product Feature Flag

There is no product feature flag such as `WorkspaceFeaturesOptions.Enabled`.

Features work proceeds as one continuous implementation effort.

Waves in this document are an internal sequencing aid for schema, context migration, worktrees, and UX. They are not a ship-Workspace-only-first product gate, and Feature creation is not held behind a config switch until a later enablement PR.

Preserve Workspace regression quality through wave gates while completing Feature worktrees and UX in the same continuous delivery.

---

# 36. Detailed Implementation Waves

Each wave should be a separate reviewable PR or clearly isolated commit series.

Do not parallelize agents across files with overlapping ownership unless explicitly coordinated.

## Wave 0 - baseline references

Goals:

- freeze baseline commit/reference;
- ensure companion docs are in the implementation folder;
- add/update architectural tests where useful.

No product feature flag. No behavior change required beyond documentation/baseline scaffolding if needed.

Gate:

```text
full build
full tests
manual Workspace smoke test
```

## Wave 1 - schema expand/backfill

Implement section 6 and Migration Wave A.

Runtime still old.

Add migration tests/backfill tests.

Gate: no behavior change.

## Wave 2 - context resolver/path resolver/application identity

Implement:

```text
WorkspaceFeatureContext resolver
context path resolver
special context selection
last-context preference service
```

All runtime still resolves special Workspace context.

Begin converting application contracts to explicit ContextId where safe.

Gate: all existing Workspace paths identical to baseline.

## Wave 3 - context repository state writer

Switch `WorkspaceRepositoryStateWriter` and mutable repository state consumers to context state.

Special Workspace only.

Update repository grid query DTOs to join special context state.


Gate:

- full Workspace Repositories baseline;
- branch/tag/current-version/count/status display unchanged;
- hook/sync baseline unchanged.

## Wave 4 - hook attribution + shared ref split

Implement path-attributed common hooks and context-aware `SyncCommandHandler`.

Confirm `RepositoryBranches` remains shared.

Gate with hook tests for post-commit/post-checkout/post-merge/pre-push.


## Wave 5 - projects/packages/dependency graph

Contextualize project persistence and dependency computations.

Switch:

```text
Projects
Packages
Dependencies
Update
Push Updated
restore planning
custom dependency merge
generated packages
```

to special Workspace context.

Gate against entire dependency section of baseline appendix.

## Wave 6 - files/version observations

Contextualize:

```text
WorkspaceFile missing state
WorkspaceFileLineStatus
version checks
version updates
file search/content path resolution
```

Gate Files + dependency badges + configured-file update.

## Wave 7 - PR + Actions + Git Changes + notifications

Contextualize:

```text
PR persistence/service
Actions persistence/service
Git Changes registry/write/read
pending notifications
SignalR event identity
```


Gate all respective baseline pages.

## Wave 8 - hierarchical operation runner

Replace Workspace-only mutation lock with structural/context scopes.

Run existing operation tests plus new concurrency matrix.

Special Workspace must remain functionally identical.

## Wave 9 - Workspace UI through context architecture

Add `WorkspaceContextBar` and Feature selector framework. Early in this wave the selector may only list Workspace until Feature contexts exist later in the same continuous delivery.

All Workspace pages resolve explicit special ContextId and pass it to commands/queries.

This is the **critical Workspace regression gate** within the continuous Features delivery.

Manual test every item in the baseline appendix before proceeding to Feature worktrees/UX.

## Wave 10 - Agent worktree primitives + Feature lifecycle backend

Implement:

```text
worktree preflight
create worktree
list/reconcile worktrees
remove worktree
worktree-aware branch occupancy
AnalyzeExternalWorktreeCleanup
RemoveExternalWorktree
Feature create orchestration
AnalyzeRemoveFeature
RemoveFeature
```

Complete backend Feature lifecycle tests, then wire Feature UX in Wave 11 of the same continuous delivery.

## Wave 11 - Feature UX

Ship:

```text
selector listing Features
typed Create Feature
Create Feature dialog
Feature menu
Remove Feature dialog
worktree-aware Branch dialog badges/actions
external Worktree cleanup dialog
context navigation persistence
```

Run full isolation matrix with at least two Features.

## Wave 12 - Desktop native actions

Implement Desktop-only launch actions.

No core behavior dependency.

## Wave 13 - hardening / recovery

Test:

```text
App crash during create
Agent crash during create
partial create
partial remove
missing worktree
stale DB row
external branch collision
Agent restart
App restart
offline mode
remote unavailable
Workspace rename/root change detection
```

## Wave 14 - contract cleanup

After user/manual approval:

- remove obsolete old mutable columns/tables;
- remove compatibility broadcasts;
- remove old root assumptions;
- tighten schema constraints;
- update architecture docs.

No new product functionality.

---

# 37. Required Code Search Gates

Before enabling Features, these searches must be reviewed.

## 37.1 Direct Workspace root usage

Search:

```text
GetRootPathForWorkspaceAsync
```

Every context-sensitive caller must either:

- be Workspace-structural, or
- be migrated to `IWorkspaceContextPathResolver`.

No exceptions hidden in page code.

## 37.2 Mutable WorkspaceRepositoryLink fields

Search reads/writes of:

```text
GitVersion
BranchName
CheckedOutTag
HasNewerTag
Projects
OutgoingCommits
IncomingCommits
DefaultBranchBehindCommits
DefaultBranchAheadCommits
BranchHasUpstream
SyncStatus
DependencyLevel
Dependencies
UnmatchedDeps
OutOfDateFileLines
OutOfDateFileRepos
TotalFileConfigRepos
HasSelfFileVersionToken
RepositoryType
```

After cutover they must come from context state, except explicit migration/compatibility code.

## 37.3 WorkspaceRepositoryId-only caches

Search:

```text
WorkspaceRepositoryPullRequest
WorkspaceRepositoryAction
WorkspaceGitRepositoryStatus
WorkspaceGitChangeEntry
```

No runtime context cache may still assume WorkspaceRepositoryId alone identifies a checkout.

## 37.4 Workspace-only SignalR contracts

Search:

```text
WorkspaceSynced
RepositorySynced
GitChangesUpdated
```

Ensure Feature emissions/handlers are context-aware.

## 37.5 Repositories header PR shortcut

Search:

```text
HasCreatablePr
WorkspaceRepositoryHeaderStateDto
HandleBranchPrimaryClick
```

Verify the special Workspace preserves the current `Branch` → yellow
`Create PR` quick-action behavior and that eligibility is sourced from the
selected context after migration.

## 37.6 Workspace-only operation locks

Search:

```text
runner.TryStart(
WorkspaceJobKeys
IsMutationKey
```

Ensure context mutations use context scope.

---

# 38. Unit Test Matrix

At minimum add tests for:

## Context schema

- one special context per Workspace;
- one Feature context per Feature;
- duplicate Feature name rejected;
- context state uniqueness.

## Resolver

- special Workspace path;
- Feature root;
- Feature repository persisted path;
- invalid cross-Workspace context rejected.

## State writer

- Feature A write isolation;
- partial snapshot preservation;
- special Workspace legacy mirror only.

## Hook attribution

- special Workspace path;
- Feature A path;
- Feature B path;
- unknown path rejected;
- Agent restart/path-only resolution.

## Project graph

- project exists only in Feature A;
- dependency edge stays Feature A;
- custom shared edge appears in every context graph;
- generated package rows isolated.

## File/version state

- shared config;
- context missing state differs;
- line mismatch differs;
- token values use context versions.

## PR/Actions

- same repository different context branch has independent row;
- refresh Feature A does not clear Workspace PR/Action.

## Git Changes

- snapshots isolated by context;
- watcher registration path maps correctly;
- stage/commit target context path.

## Locking

Use matrix in §21.3.

## Feature lifecycle

- valid create;
- dirty Workspace create;
- duplicate GM Feature;
- local branch collision;
- remote ref collision known locally;
- worktree branch already occupied;
- partial failure/retry;
- removal merged;
- removal abandoned;
- dirty removal blocked without authorization;
- offline local cleanup;
- remote delete failure leaves repairable state.
- GM Feature branch appears as `Feature` in Branch dialog;
- external linked worktree branch appears as `Worktree`;
- occupied worktree branch cannot be checked out from Workspace;
- GM Feature branch cleanup routes to Remove Feature;
- clean external worktree removal;
- dirty external worktree blocked without explicit destructive authorization;
- external worktree removed but branch delete fails -> inventory refreshes correctly.

---

# 39. Integration Test Scenarios

Use a temporary set of real Git repositories.

## Scenario A - two Features

```text
Workspace RepoA -> main
Feature A RepoA -> feat-a
Feature B RepoA -> feat-b
```

Change different files in each.

Assert independent:

```text
HEAD
GitVersion
Git Changes
project discovery
dependency stats
```

## Scenario B - cross-repo dependency

Feature A changes package producer version/project reference.

Workspace graph stays baseline.

Feature B graph stays baseline.

Feature A Update/Push plan changes.

## Scenario C - hook isolation

Commit in Feature A.

Wait for hook/sync.

Only Feature A state changes.

## Scenario D - Actions/PR

Publish Feature A only.

Feature A gets upstream/PR/Actions.

Workspace and Feature B remain independent.

## Scenario E - concurrent operations

Run long operation in Feature A and another in Feature B.

Both proceed subject to Agent bounded concurrency.

## Scenario F - crash recovery

Kill App/Agent between worktree 1 and worktree N creation.

Restart.

Feature is not Ready.

Retry/reconcile without duplicate worktrees.

---

# 40. Manual Regression Gate Before First Feature UI

The user must manually verify the special Workspace against:

```text
GrayMoon-Workspace-Current-Features-Baseline-Appendix.md
```

At minimum:

- Workspace import/edit/repository membership;
- Repositories grid;
- New/Switch Branch;
- Prepare Workspace;
- Return to Default;
- sync/fetch;
- Projects;
- Packages;
- Dependencies;
- custom dependencies;
- Update;
- Push Updated;
- restore;
- Pull/commit sync;
- Undo Push;
- PR creation;
- PR merge/close;
- Actions;
- Git Changes;
- Files/version config;
- pending notifications;
- loading overlays/jobs;
- tags;
- offline local operation.

Do not proceed to Feature worktrees/UX if this Workspace regression gate fails. There is no product feature flag; fix the regression gap first.

---

# 41. Manual Feature Acceptance Gate

After Feature UX lands, test at least:

```text
Workspace
Feature A
Feature B
```

simultaneously.

Open external IDEs against separate roots.

Switch GM selector while tools remain open.

Verify no external environment is paused or changed.

Test:

- edits;
- commits;
- hooks;
- Git Changes;
- dependency update;
- version-file update;
- restore;
- push/upstream;
- PR;
- Actions;
- merge;
- remove Feature;
- abort Feature;
- offline Feature;
- App restart;
- Agent restart.

---

# 42. Performance Requirements

Do not implement "all Features × all repositories" permanent scanning.

Use:

```text
persisted projections
activity-based Git Changes watchers
bounded Agent concurrency
context-local refresh
```

Queries must include ContextId in indexes where context filtering is frequent.

Add indexes for:

```text
ContextId + WorkspaceRepositoryId
ContextId + dependency level
ContextId + project name
ContextId + repository id
ContextId + Git change repository id
```

Avoid N×context EF query loops where a single batch query can be used.

Preserve virtual-scroll query patterns.

---

# 43. Security and Safety Requirements

- no credentials stored in Feature folders;
- connector tokens remain runtime-only;
- no force push for collision recovery;
- no `git worktree add --force` in normal creation;
- no `git worktree remove --force` without explicit discard authorization;
- no arbitrary filesystem path accepted from UI/MCP-like callers;
- Agent commands validate all resolved paths are expected;
- Git Changes path traversal validation remains;
- never auto-adopt arbitrary external worktrees;
- never default ambiguous hook path to Workspace.

---

# 44. Logging Requirements

Every context-sensitive log should include where practical:

```text
WorkspaceId
WorkspaceFeatureContextId
FeatureName when available
RepositoryId
RepositoryName
```

Feature lifecycle logs include:

```text
FeatureId
LifecycleState
worktree path
base SHA
operation id/run id
```

Do not log tokens.

This is essential for debugging multiple simultaneous contexts.

---

# 45. Failure and Recovery Principles

Prefer durable intent over hidden cleanup.

A partially completed structural operation must leave enough DB state to understand what happened.

Do not:

```text
catch exception
delete Feature rows
hope Git cleanup succeeded
```

Instead:

```text
persist lifecycle state
persist per-repo state/error
reconcile Git reality
allow retry
```

Git remains authoritative for actual worktree existence.

SQLite remains authoritative for GM ownership/intent and cached projections.

---

# 46. Feature Ready Definition

A Feature is `Ready` only when:

```text
Feature row exists
Feature context exists
one FeatureRepository row exists per Workspace repository
every expected worktree exists and matches branch/path
every repo context state has completed initial sync
project/dependency projection is initialized
no repository row is NeedsRepair
```

PR/Actions/Git Changes need not have fresh remote/live data before Ready; they are caches that can populate after selection.

---

# 47. Feature Removal Complete Definition

Removal is complete only when:

```text
all managed worktrees are removed
local branches handled according to authorized policy
requested remote cleanup is completed OR explicitly waived/local-only
no context operation is running
context projections are removed
Feature/context metadata is removed
managed empty directories cleaned
```

If any required step fails, keep recoverable metadata.

---

# 48. What Must Not Be Implemented in This Project

Unless the user explicitly reopens scope, do not add:

```text
MCP server
MCP tools/resources
automatic Feature branch rename on collision
automatic adoption of external worktrees
partial-repository Features
Feature branch switching
Feature tag mode
per-Feature duplicated Workspace configuration
automatic worktree relocation on Workspace rename
new Search indexing engine
new design system / icons / visual language
```

---

# 49. Final Implementation Principle

The safest implementation sequence is:

```text
make today's Workspace run through the future architecture
prove nothing broke
then add one more context
```

Do not attempt to "add worktrees" directly to the current single-checkout model.

The central success criterion is not merely that `git worktree add` works.

It is that GrayMoon's existing multi-repository intelligence - dependency graph, version files, Git Changes, PRs, Actions, hooks, package synchronization, restore, push orchestration, and safety workflows - becomes **context-correct** before worktree-backed Features are exposed.

