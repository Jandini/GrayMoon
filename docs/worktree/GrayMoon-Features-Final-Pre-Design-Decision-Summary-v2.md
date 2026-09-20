# GrayMoon Features - Final Pre-Design Decision Summary

**Status:** Final approved pre-design baseline before detailed implementation design  
**GrayMoon code reviewed through:** `b10994375a8fd7711a92e34dc4f82070e992ea46`  
**Date:** 2026-09-20  
**Companion regression baseline:** `GrayMoon-Workspace-Current-Features-Baseline-Appendix.md`

This document supersedes the earlier pre-design summaries and the MCP-readiness pre-start supplement.


## Purpose

This document captures the **approved product, UX, persistence, lifecycle, execution-context, Git Changes, and desktop-integration decisions** made before the full end-to-end Feature design begins.

It is intentionally a **pre-design decision record**, not yet the implementation plan.

The goal is to make the later architecture/design phase operate from a clean, stable vocabulary and a set of locked product rules.

## Vocabulary

The codebase rename has already been completed. The canonical terms are:

| Concept | Canonical term |
|---|---|
| Existing old “New Feature” workflow | **Prepare Workspace** |
| Existing old “Sync to Default” workflow | **Return to Default** |
| Existing special checkout | **Workspace** |
| New worktree-backed isolated environment | **Feature** |
| Internal execution context concept | **WorkspaceFeatureContext** |

**Feature** is now reserved for the worktree-backed environment concept. **Prepare Workspace** remains the existing coordinated branch/dependency/push workflow. **Return to Default** remains the existing cleanup/reset-to-default workflow for the special Workspace checkout. GrayMoon's existing **Sync** terminology remains unchanged and continues to mean Agent/state refresh.

## Workspace remains special

GrayMoon must continue to work exactly as it does today even if the user never creates a Feature.

The current Workspace remains the **special mutable checkout**:

```text
Workspace
→ existing checkout
→ normal branch switching
→ Prepare Workspace
→ Return to Default
→ current GrayMoon behavior
```

A Feature is additional, optional, and isolated. Creating Features must not convert or replace the normal Workspace.

## Feature = branch

A Feature name is the same thing as its Git branch name.

Examples:

```text
BAM-2856
search-index
refactor-auth
```

There is no separate user-facing “Feature name” and “Branch name” concept. GrayMoon should enforce valid Git branch/ref naming rules in the Feature creation UX.

A Feature name must be unique within a Workspace. If a Feature named `BAM-2856` already exists, typing `BAM-2856` into the Feature selector and pressing Enter selects that Feature; GrayMoon must not create a duplicate Feature.

## Feature creation entry point

The Feature selector is part of the shared workspace search/context bar.

Conceptually:

```text
┌──────────────────┬────────────────────────────────────────────┐
│ BAM-2856       ▾ │ Search repositories...                    │
└──────────────────┴────────────────────────────────────────────┘
```

When the special checkout is selected:

```text
┌──────────────────┬────────────────────────────────────────────┐
│ Workspace      ▾ │ Search repositories...                    │
└──────────────────┴────────────────────────────────────────────┘
```

The Feature segment is editable. Expected behavior:

- click into the Feature segment;
- type a Feature name;
- existing Features are filtered while typing;
- Up/Down navigates matches;
- Enter on an existing exact match switches context;
- Enter on a new valid name opens the **Create Feature** dialog with the typed name prefilled;
- Escape cancels editing.

Feature creation does **not** happen immediately on Enter. The dialog still exists because base selection and important creation semantics must be explicit.

## Create Feature dialog

The Create Feature dialog must use existing GrayMoon modal and control patterns only.

No new visual vocabulary should be invented. Use the existing GM theme: same modal structure, button styles, spacing, typography, callout patterns, disabled/loading behavior, keyboard behavior, and existing icon conventions only where GM already uses them.

Do not introduce decorative icons, novel control styles, one-off button treatments, or a new design language.

The dialog should contain at least:

```text
Create Feature

Feature name
[ BAM-2910 ]

Based on
[ Current Workspace ▾ ]
```

The default base is **Current Workspace**. Other valid bases may include **Default branches** and another existing Feature.

## Current Workspace base semantics

When creating a Feature **Based on Current Workspace**, the Feature starts from the current committed state of each repository.

It does **not** copy staged or unstaged changes.

This must be made clear to the user in the Create Feature dialog:

> The Feature starts from the current committed state. Uncommitted changes in Workspace are not copied and remain in Workspace.

Do not silently copy dirty working-tree state. Do not require the Workspace to be clean just to create a Feature.

## Every Feature contains every Workspace repository

A Feature is workspace-wide. Every Feature contains a worktree for every repository belonging to the Workspace.

Example:

```text
BAM-2856/
    RepoA/
    RepoB/
    RepoC/
```

This remains true even if only one repository is expected to change. This gives a stable filesystem shape, complete multi-repo environments for IDEs/agents, coherent dependency analysis, automatic version-file behavior, and consistent GrayMoon operations.

## Physical folder layout

Feature worktrees must **not** live inside the normal Workspace repository tree.

Approved layout:

```text
C:\Workspace\AVR\
    RepoA\
    RepoB\
    RepoC\

C:\Workspace\.graymoon\AVR\
    features\
        BAM-2856\
            RepoA\
            RepoB\
            RepoC\
        search-index\
            RepoA\
            RepoB\
            RepoC\
```

`.graymoon` is GrayMoon-managed infrastructure. The normal Workspace root remains clean. Feature worktrees do not appear as additional Workspace repositories and should not be accidentally traversed by repository discovery/import logic.

## Shared Workspace configuration

Workspace-level configuration is not duplicated per Feature.

A Feature automatically uses the same repository membership, version-file configuration, package/dependency configuration, custom dependency configuration, project configuration, and other repository-relative workspace settings.

Core rule:

> **Workspace configuration is shared; repository state and physical files are context-specific.**

## Version files are automatically Feature-aware

Configured version files work in Features without reconfiguration.

If Workspace configuration references:

```text
RepoA/src/Directory.Build.props
RepoB/version.json
```

then when `BAM-2856` is selected, GrayMoon resolves them against:

```text
C:\Workspace\.graymoon\AVR\features\BAM-2856\RepoA\...
C:\Workspace\.graymoon\AVR\features\BAM-2856\RepoB\...
```

When `Workspace` is selected, the same configuration resolves against the normal Workspace root.

The same principle applies to project discovery, package dependency analysis, Git Changes, file operations, restore, search, configured-file updates, and other repository-relative operations.

## WorkspaceFeatureContext is the core internal concept

The selected execution context is represented internally as:

```text
WorkspaceFeatureContext
    WorkspaceId
    Kind = Workspace | Feature
    FeatureId?
```

The final persisted model should use a real context row for both Workspace and Feature contexts.

Important architectural rule:

> The selected Feature is not a page filter. It is the current Workspace execution context.

Every applicable operation resolves paths and state through `WorkspaceFeatureContext`.

## Context follows the user across pages

Once the user selects a Feature, that context follows them everywhere inside the Workspace.

```text
Repositories
→ Git Changes
→ Files
→ Search
→ Actions
→ back to Repositories
```

If `BAM-2856` is selected, all applicable pages continue to operate against `BAM-2856`. Switching to `Workspace` returns all applicable pages and operations to the special Workspace checkout.

This is a hard architectural requirement.

The selected context should also persist per Workspace across app restarts when still valid.

## Context-aware root resolution

The current codebase contains many direct calls to `GetRootPathForWorkspaceAsync(...)`. The Feature design must remove the assumption that Workspace has only one executable root.

Target concept:

```text
WorkspaceFeatureContext
        │
        ├─ Workspace
        │    → C:\Workspace\AVR
        │
        └─ Feature BAM-2856
             → C:\Workspace\.graymoon\AVR\features\BAM-2856
```

A context-aware root/path resolver should become the normal execution boundary. Individual services should not independently decide which physical root to use.

## Branch / Feature UX

When the special Workspace is selected, keep the existing **Branch** control:

```text
Branch ▼
├─ Prepare Workspace
├─ New Branch
├─ Switch Branch
├─ Create PRs
└─ Return to Default
```

When a Feature is selected, the contextual control becomes:

```text
Feature ▼
```

The Feature control is intentionally short and deployment-neutral.

## Feature menu

Base Feature actions should be available in all deployments:

```text
Feature ▼
├─ Create PRs
└─ Remove Feature
```

`Remove Feature` has **no ellipsis**.

Desktop may append native external-tool actions because only Desktop can launch local processes, for example:

```text
Open in Cursor
Open in Visual Studio
Open Terminal
Open in Explorer
```

Do not expose unavailable native launch actions in containerized/web-only GrayMoon.

## Prepare Workspace and Return to Default are Workspace-only

**Prepare Workspace** belongs to the special Workspace checkout. It should not become the way Features are prepared.

**Return to Default** also remains Workspace-only. It should not be offered inside a Feature because a Feature is defined by its branch/worktree identity; returning its repositories to default branches would break that invariant and may conflict with the default branch already being checked out in Workspace.

Feature cleanup is handled by **Remove Feature**.

## Feature removal lifecycle

Remove Feature must use the same safety philosophy as Return to Default.

### Completed Feature

If the PR is merged, this is the clean completion path. GrayMoon may safely clean up GM-managed worktrees, remove local Feature branches when safe, remove remote Feature branches when allowed by the same guardrail philosophy already used by Return to Default, and delete persisted Feature/context state after cleanup succeeds.

### Closed but unmerged PR

This represents abandonment, not successful completion. GrayMoon may allow Feature removal, but must clearly communicate that unmerged work is being discarded. Remote branch cleanup may be allowed under explicit safe guardrails.

### Explicit abort

A user may abandon a Feature even without a completed PR. GrayMoon should perform Return-to-Default-style safety analysis and clearly show what will be lost, including where applicable uncommitted changes, unpushed commits, unmerged work, local branches, and remote branches.

The existing GrayMoon confirmation/modal style should be reused.

## Remote / upstream policy

Features must work perfectly with local Git only.

Internet access is not required to create a Feature, work in it, commit, inspect Git Changes, or remove local Feature worktrees/branches when safe.

A remote branch is optional.

Approved lifecycle:

```text
Feature exists locally first
    ↓ optional later
upstream branch established when a natural push occurs
```

GrayMoon should establish upstream naturally when appropriate, conceptually:

```text
git push -u origin BAM-2856
```

Publication “locks in” the branch name at origin when connectivity exists, but publication is not a prerequisite for Feature existence.

## Remote name collision / external branches

If a Feature was created locally/offline and GrayMoon later discovers `origin/BAM-2856` already exists with unexpected lineage, GrayMoon must not force-push over it.

This becomes a reconciliation state. Potential future recovery options may include:

```text
Rename Feature
Use existing remote branch
Cancel publishing
```

Exact recovery UX can be designed later.

Important invariant:

> GrayMoon must never assume that lack of a remote branch means it owns that remote name forever.

External Git-created collisions are treated as validation/reconciliation/recovery behavior. GrayMoon must not use force tricks to bypass Git's worktree branch protections.

## Persistence strategy - approved

Use a low-risk **expand → migrate → switch → contract** approach.

Do not mutate the existing model destructively in one step.

Within one continuous Features delivery, migrate the special Workspace onto the same context architecture before introducing Feature worktrees/UX. This proves the context model with existing behavior first. There is no product feature flag.

## Target persistence shape

Approved conceptual model:

```text
Workspace
├─ WorkspaceRepositoryLink
│    └─ workspace-level membership/configuration
│
├─ WorkspaceFeatureContext
│    ├─ Workspace context
│    └─ Feature contexts
│
└─ WorkspaceFeature
```

Context-specific repository state hangs off the context.

Conceptually:

```text
WorkspaceFeatureContext
- Id
- WorkspaceId
- Kind
- FeatureId?
- CreatedAt

WorkspaceFeature
- Id
- WorkspaceId
- Name
- BaseKind
- BaseFeatureId?
- CreatedAt
- LifecycleState

WorkspaceFeatureRepository
- Id
- WorkspaceFeatureContextId
- WorkspaceRepositoryId
- WorktreePath?
- BaseCommitSha?
- CreatedAt

WorkspaceRepositoryContextState
- WorkspaceFeatureContextId
- WorkspaceRepositoryId
- BranchName
- CheckedOutTag
- GitVersion
- IncomingCommits
- OutgoingCommits
- DefaultBranchAheadCommits
- DefaultBranchBehindCommits
- BranchHasUpstream
- GitStatus/cache references
- PR/cache references
- Action/cache references
- other context-specific state
```

The final implementation may refine exact table names/field placement, but the separation principle is approved.

## Special Workspace gets a real context row

Do not model the special Workspace as an implicit null convention scattered across application code.

The special Workspace gets a real `WorkspaceFeatureContext` row with `Kind = Workspace`. Feature contexts use `Kind = Feature` plus a `FeatureId`.

This gives the application one execution model.

## WorkspaceRepositoryLink becomes configuration/membership

`WorkspaceRepositoryLink` remains the Workspace-to-Repository relationship and shared configuration anchor. It should not remain the only home for mutable checkout state.

Context-sensitive fields such as BranchName, GitVersion, incoming/outgoing commits, divergence, Git status, PR state, Actions state, and version-file mismatch diagnostics that depend on checked-out files move behind context-scoped persistence.

The migration may temporarily leave old columns physically present, but runtime should switch to one new source of truth rather than dual-write indefinitely.

## Persisted facts vs cached observations

Durable Feature facts include Feature name, branch identity, base commit, worktree path, created date, and lifecycle state.

Cached observations include GitVersion, ahead/behind, Git Changes, PR state, current HEAD, and Actions state.

Git and external systems remain authoritative for observations. GrayMoon's DB is metadata plus durable projections/caches.

## Persistence recovery philosophy

If SQLite says a Feature exists but physical worktrees are missing, do not silently recreate them. Verify against Git, mark/reconcile the Feature, and retain enough metadata for repair/removal.

If GrayMoon-managed worktrees exist under `.graymoon/.../features` but DB records are missing, GrayMoon may detect and reconcile/import those managed worktrees. Do not automatically adopt arbitrary external worktrees.

Git is authoritative for worktree reality.

## Feature removal persistence

Feature deletion should use lifecycle/transactional semantics:

1. mark cleanup/removal in progress if needed;
2. remove worktrees safely;
3. remove local/remote branches according to safety rules;
4. delete final Feature/context persistence only after cleanup succeeds.

If cleanup partially fails, retain enough metadata to recover or resume.

## Database constraints

At minimum, the final schema should enforce uniqueness equivalent to:

```text
UNIQUE WorkspaceFeature(WorkspaceId, Name)

UNIQUE WorkspaceFeatureContext(WorkspaceId, Kind, FeatureId)

UNIQUE WorkspaceFeatureRepository(
    WorkspaceFeatureContextId,
    WorkspaceRepositoryId
)

UNIQUE WorkspaceRepositoryContextState(
    WorkspaceFeatureContextId,
    WorkspaceRepositoryId
)
```

Exact implementation may account for SQLite null semantics and the final table design.

## Migration waves

### Wave A - additive schema

- add new context/Feature tables;
- create one Workspace context row for every existing Workspace;
- backfill context-state from current `WorkspaceRepositoryLink` mutable state;
- do not change runtime behavior yet.

### Wave B - execution-context abstraction

Introduce context-aware identity/path resolution while the only active context is still Workspace. Existing behavior should remain identical.

### Wave C - context-scoped persistence

Move runtime reads/writes of mutable repository state to context-scoped state. Avoid indefinite dual-write.

### Wave D - Feature worktrees and UX

Within the same continuous delivery, after Workspace behavior runs through the new context architecture, add `Kind = Feature` worktrees and user-facing Feature creation.

This keeps Features additive instead of rewriting GrayMoon and introducing worktrees simultaneously. There is no separate product feature flag between Wave C and Wave D.

## Concurrency is per WorkspaceFeatureContext

This is a hard requirement.

Ordinary Feature operations lock on `WorkspaceFeatureContextId`, not merely `WorkspaceId`.

Therefore these may run concurrently:

```text
Workspace      → Push
BAM-2856       → Update
search-index   → Commit
```

because the physical checkouts are isolated.

Some structural operations still require a Workspace-wide lock, for example changing Workspace repository membership/configuration or creating/removing Features while shared structure is changing.

The design should explicitly support both **Workspace lock** and **WorkspaceFeatureContext lock** scopes.

## Git Changes - reuse the merged architecture

The merged Git Changes implementation already provides the right primitives for Features. The Agent has path-keyed `FileSystemWatcher` ownership, per-repository refresh trackers, debounce/coalescing, bounded scan concurrency, `GitChangesSnapshotUpdated` push back to the App, watcher lease/grace behavior, and activity-based monitoring rather than permanent global scanning.

The Feature design should extend this architecture, not replace it.

## Git Changes attribution becomes context-aware

Today the Agent registry conceptually maps:

```text
repoPath
→ WorkspaceId + RepositoryId
```

Target:

```text
repoPath
→ WorkspaceFeatureContextId + WorkspaceRepositoryId
```

Likewise Git Changes persisted state and entries become context-scoped, conceptually keyed by:

```text
WorkspaceFeatureContextId + WorkspaceRepositoryId
```

## Git Changes watcher behavior

Each physical repo path gets its own watcher/refresh tracker.

Therefore:

```text
C:\Workspace\AVR\RepoA
```

and:

```text
C:\Workspace\.graymoon\AVR\features\BAM-2856\RepoA
```

are independent monitored repositories. A change in one Feature must not dirty another Feature or the special Workspace.

## Active vs inactive Feature monitoring

Do not watch every Feature permanently.

Reuse the existing watcher lease/activity model:

- selected context receives active watcher renewal;
- recent operations in a context mark it active;
- recent Git Changes scans/refreshes mark it active;
- watcher/Git activity may keep a context active;
- recently active contexts retain leases during the existing grace period;
- completely dormant Feature watcher leases expire naturally;
- persisted snapshots remain available;
- selecting a dormant Feature triggers immediate/on-open refresh.

No new user-facing watcher setting is required initially. Reuse existing grace-period concepts.

Desktop is not the only source of context activity. Correctness must not depend on Desktop process detection.

## Desktop integration

`GrayMoon.Desktop` is available and is the correct native boundary for launching local tools.

The existing architecture already has:

```text
GrayMoon.App
   ↓ WebView2 message
GrayMoon.Desktop
   ↓ native Process.Start / shell action
external tool
```

The Feature design should extend the existing typed WebMessage bridge rather than make `GrayMoon.App` launch native processes directly.

Desktop-only actions may include:

```text
Open in Cursor
Open in Visual Studio
Open Terminal
Open in Explorer
```

Claude Code should normally be launched through a terminal whose working directory is the Feature root.

The Feature root is the workspace-wide Feature directory containing all repository worktrees, for example:

```text
C:\Workspace\.graymoon\AVR\features\BAM-2856
```

No MCP is part of the initial Feature design.

## Containerized GrayMoon remains first-class

Feature creation, Git operations, persistence, context switching, PR handling, Git Changes, and local Git workflows must not depend on `GrayMoon.Desktop`.

Desktop only adds native host capabilities such as launching local applications.

## PR state becomes context-specific

The current code persists PR state directly against `WorkspaceRepositoryLink`. That is insufficient once the same repository can simultaneously exist in Workspace and multiple Features.

PR state must be stored/read in the correct `WorkspaceFeatureContext`, including changed-file counts, mergeability, merged state, PR links, and lifecycle cleanup decisions.

## Actions state becomes context-specific

Actions/workflow status currently persists against `WorkspaceRepositoryLink` and contains branch context. It must become context-specific.

When `BAM-2856` is selected, Actions should represent the relevant state for `BAM-2856`, not overwrite or reuse Workspace/main state.

Where Actions are genuinely repository/global rather than branch-specific, the detailed design may explicitly classify them as shared instead of duplicating them blindly.

## Dependency / version diagnostics

Dependency relationships/configuration are shared Workspace-level configuration. Diagnostics that depend on actual checked-out file contents or current GitVersion are context-specific.

Examples include:

```text
UnmatchedDeps
OutOfDateFileLines
OutOfDateFileRepos
GitVersion
```

The final design must classify fields carefully into **shared configuration** versus **context-specific observation/state** rather than copying the current `WorkspaceRepositoryLink` shape wholesale.

## UX must follow the existing GrayMoon theme exactly

This is a hard requirement.

For every new modal, dropdown, button, callout, loading state, disabled state, confirmation, menu, and keyboard interaction, reuse existing GrayMoon patterns.

Do not invent “new ways” of presenting Feature UI.

The Feature system should feel like it has always belonged in GrayMoon.

## Deferred / intentionally open items

These are not blockers for the master design but may be refined during implementation planning:

- exact remote branch collision recovery UX;
- exact repair/import flow for GrayMoon-managed worktrees with missing DB metadata;
- whether Desktop remembers a preferred external launch tool;
- exact inactive Feature watcher grace duration - initial direction is to reuse the existing Git Changes grace mechanism.

## Core product model

```text
Workspace
│
├─ shared repository/configuration model
│
├─ WorkspaceFeatureContext: Workspace
│    └─ normal existing checkout
│
├─ WorkspaceFeatureContext: BAM-2856
│    └─ isolated worktree set
│
├─ WorkspaceFeatureContext: search-index
│    └─ isolated worktree set
│
└─ WorkspaceFeatureContext: ...
```

The user can work in multiple Feature environments simultaneously using different IDEs/agents.

GrayMoon simply selects which context it is currently showing. Switching the selected Feature does not pause or disable other Features.

## Guiding principles for the master design

1. **No regression for users who never create a Feature.**
2. **Workspace remains special and fully supported.**
3. **Feature = branch.**
4. **Features are workspace-wide and contain every repo.**
5. **Feature state is isolated by `WorkspaceFeatureContext`.**
6. **Shared configuration is not duplicated.**
7. **Local/offline Git is first-class.**
8. **Remote publication is optional and opportunistic.**
9. **Concurrency is per context.**
10. **Persistence migration is additive and low-risk.**
11. **Git Changes extends the existing watcher architecture.**
12. **Desktop adds native launch capabilities but is never required for core functionality.**
13. **All UX must follow existing GrayMoon design patterns.**
14. **Feature cleanup must be safe, recoverable, and explicit about data loss.**
15. **The selected Feature is the execution context across the entire Workspace UI.**

#
# 49. Git Hook Attribution Is Context-Critical

Git hook driven synchronization is a core GrayMoon behavior and must become Feature-context aware before Feature worktrees are used.

Today, hook-driven sync effectively identifies a repository by:

```text
WorkspaceId + RepositoryId
```

That identity is no longer sufficient when the same repository exists simultaneously in:

```text
Workspace
BAM-2856
search-index
...
```

A commit, checkout, merge, or push inside one Feature must update only that Feature's persisted state.

Target identity:

```text
WorkspaceFeatureContextId + WorkspaceRepositoryId
```

or an equivalent context-aware contract that resolves unambiguously to those two identities.

The detailed implementation plan must explicitly cover:

- hook installation for normal Workspace checkouts;
- hook installation for every Feature worktree;
- hook payload changes;
- `RepositorySyncNotification` changes;
- App-side `SyncCommandHandler` changes;
- context-aware state writes;
- context-aware branch/tag/head updates;
- context-aware dependency recomputation;
- context-aware browser broadcasts;
- migration/backward compatibility with pre-Feature Agents where required;
- tests proving a hook from Feature A cannot mutate Workspace or Feature B state.

The Agent must continue to operate primarily on physical repository paths and request payloads. It should not need to understand GrayMoon database relationships beyond the context identity supplied by the App/hook metadata.

This is a **core implementation requirement**, not a follow-up enhancement.

---

# 50. Discovered Projects and Dependency Graph Are Context-Specific

GrayMoon's project/dependency graph is a core product capability and must be designed explicitly for Features.

Workspace-level configuration remains shared:

```text
Shared:
- Workspace repository membership
- custom repository dependency declarations
- version-file configuration
- connector configuration
- other user-authored Workspace configuration
```

However, discovered source/project state depends on the files checked out in the current context.

A Feature may:

- add a `.csproj`;
- remove a `.csproj`;
- rename or move a project;
- change `PackageId`;
- change `PackageReference`s;
- change project type;
- change target framework;
- introduce or remove generated-package relationships;
- change version-file-derived dependency observations.

Therefore the following must be treated as context-derived projections:

```text
Context-specific:
- discovered WorkspaceProjects
- project paths
- PackageId values
- project type
- target framework
- PackageReference edges
- ProjectDependencies
- generated dependency observations that depend on checked-out files
- computed repository dependency levels
- dependency mismatch diagnostics
- Push Updated dependency planning
- restore planning derived from the checked-out graph
```

The current Workspace-wide `WorkspaceProjects` / `ProjectDependencies` persistence cannot remain a single shared mutable projection once multiple contexts can exist simultaneously.

The detailed design must provide a concrete migration plan for this graph.

Preferred direction:

```text
WorkspaceFeatureContext
    └─ context-scoped discovered project graph
```

The exact physical schema can be refined during detailed design, but the following invariants are required:

1. Refreshing projects in Feature A must not overwrite Workspace project state.
2. Refreshing projects in Feature A must not overwrite Feature B project state.
3. Workspace-level custom dependency declarations remain shared.
4. Context-specific graph computation must merge shared custom dependencies with context-specific discovered/package/version-file-derived edges.
5. Push, Update, Restore, and version synchronization must always use the selected `WorkspaceFeatureContext` graph.
6. Existing Workspace behavior must be migrated first and remain functionally identical before Feature worktrees/UX land in the same continuous delivery.
7. Generated package handling must be reviewed explicitly because some generated dependencies are configuration-derived while others depend on the current checked-out file graph.

This is a **core implementation requirement**, not an optimization.

---

# 51. Branch/Ref Inventory vs Context Checkout State

The existing `RepositoryBranches` persistence mixes repository-wide Git refs with assumptions about one current checkout.

The Feature design should separate these concepts.

Repository/ref-level information:

```text
- local branch inventory
- remote branch inventory
- tags
- default branch
```

Context checkout state:

```text
- current branch
- checked-out tag
- HEAD commit SHA
- detached/unborn state
- upstream relationship
- outgoing/incoming commits
- ahead/behind default branch
```

The detailed design should avoid blindly duplicating all ref inventory rows per Feature.

Preferred principle:

> Git ref inventory is repository-level where the underlying Git repository shares the ref namespace; HEAD/checkout/divergence state is `WorkspaceFeatureContext`-specific.

The implementation plan must explicitly review worktree Git semantics so the persistence model matches actual Git behavior.

---

# 52. Workspace Sync State Retains Special-Workspace Meaning

The existing Workspace model contains:

```text
Workspace.LastSyncedAt
Workspace.IsInSync
```

These fields should retain their existing semantics for the **special Workspace context**.

Do not redefine them to mean:

```text
all Features + Workspace are synchronized
```

That would change existing behavior and make the fields ambiguous.

Feature synchronization state should instead live on or be derived from `WorkspaceFeatureContext`.

Conceptually:

```text
WorkspaceFeatureContext
- LastSyncedAt
- IsInSync
```

or equivalent context-scoped state.

This preserves migration safety and keeps existing Workspace behavior stable.

---

# 53. Context-Aware Notifications and Background Jobs

The existing runtime assumes Workspace-wide mutation/broadcast identity in several places.

The detailed implementation plan must make these context-aware.

## Browser / SignalR notifications

Existing notifications such as:

```text
WorkspaceSynced
RepositorySynced
RepositoryError
```

must carry enough context identity that:

```text
Feature A update
```

does not cause a page currently showing:

```text
Feature B
```

to refresh its state as though it changed.

The exact contract may evolve, but `WorkspaceFeatureContextId` should be part of the normal identity for context-specific events.

## Background jobs / overlays

Existing mutation keys and `WorkspaceOperationRunner` are Workspace-scoped.

They must evolve so ordinary context-isolated operations use:

```text
WorkspaceFeatureContextId
```

while structural operations can still use:

```text
WorkspaceId
```

The detailed design must explicitly define both lock scopes and how loading overlays attach to the correct context and page.

---

# 54. Detailed Design Must Treat These as Core Migration Waves

The full implementation design must not treat hook attribution or project/dependency persistence as incidental refactors.

They must appear as explicit implementation waves with tests and rollback-safe sequencing.

At minimum, the detailed design should contain dedicated work for:

```text
1. WorkspaceFeatureContext schema and backfill
2. context-aware repository-state writer
3. context-aware hooks and Agent sync notifications
4. context-aware project discovery persistence
5. context-aware dependency graph recomputation
6. context-aware branch/head/ref persistence split
7. context-aware PR / Actions / Git Changes persistence
8. context-aware job locking and notifications
9. migration of existing Workspace runtime onto the new context architecture
10. Feature creation/worktrees as the next sequenced step in the same continuous delivery
```

Sequence Feature creation/worktrees after existing Workspace behavior has successfully run through the new context-aware state, hook, and project/dependency infrastructure. There is no product feature flag; this is technical ordering within one continuous Features delivery.



# 55. MCP Readiness - Strategic Direction

MCP is intentionally **not part of the first worktree Feature implementation**.

The approved sequence is:

```text
1. migrate existing Workspace behavior onto WorkspaceFeatureContext
2. implement worktree-backed Features
3. thoroughly validate Feature lifecycle through the human UX
4. stabilize the context-aware application contracts
5. expose proven GrayMoon capabilities through MCP
```

GrayMoon must not expose AI agents to an unproven worktree lifecycle before the same lifecycle has been exercised heavily through the UX.

However, the Feature architecture must be designed so MCP can later become an adapter over the same application capabilities without requiring a second implementation.

The long-term architecture should be:

```text
                     Blazor UX
                         │
                         │
REST API ───────── GrayMoon.Application ───────── MCP
                         │
                         ↓
                  GrayMoon.App services
                         │
                         ↓
                   GrayMoon.Agent
```

MCP must not implement Git/worktree/dependency logic independently.

Its role should be to translate MCP tool/resource calls into the same application operations and queries used by GrayMoon's own UX and REST API.

---

# 56. Application Context Must Be Explicit - Never Ambient UI State

This is the most important MCP-readiness rule.

A context-sensitive application operation must never determine its execution target from:

```text
currently selected Feature in a browser
Blazor circuit state
current page
Desktop state
last selected Feature
other ambient UI state
```

The selected Feature in the UX is only a way to choose an explicit execution context.

Conceptually:

```text
UX Feature selector
    ↓
WorkspaceFeatureContextId
    ↓
application operation
```

Later MCP should do exactly the same:

```text
MCP caller
    ↓
WorkspaceFeatureContextId
    ↓
application operation
```

The application contract should therefore evolve away from:

```text
UpdateAsync(workspaceId, ...)
PushAsync(workspaceId, ...)
CommitAsync(workspaceId, repositoryId, ...)
```

toward context-authoritative forms such as:

```text
UpdateAsync(workspaceFeatureContextId, ...)
PushAsync(workspaceFeatureContextId, ...)
CommitAsync(workspaceFeatureContextId, repositoryId, ...)
```

or an explicit application value object carrying the same stable identity.

Preferred rule:

> `WorkspaceFeatureContextId` is the authoritative execution identity for context-specific operations; Workspace identity is resolved from it rather than inferred from UI selection.

Workspace-structural operations remain explicitly Workspace-scoped.

---

# 57. Physical Paths Must Remain Internal Implementation Detail

MCP callers should eventually identify:

```text
Workspace
Feature
Repository
```

using stable GrayMoon identities/names.

They should not need to construct or understand:

```text
C:\Workspace\.graymoon\AVR\features\BAM-2856\RepoA
```

GrayMoon owns:

- worktree path layout;
- repository path resolution;
- Workspace-vs-Feature root resolution;
- path validation;
- worktree repair/reconciliation.

This reinforces the approved `WorkspaceFeatureContext` + context-aware path resolver architecture.

---

# 58. Core Operations Must Remain Headless

Any operation that may later be exposed to MCP must be executable without:

```text
Blazor component state
modal instances
toast services
JS interop
browser navigation
Desktop UI
page-owned cancellation/disposables
```

The UI may:

1. request an analysis/plan;
2. render the result using existing GrayMoon UX;
3. collect the user's decision;
4. call the headless application operation.

Business rules and safety decisions must not live only inside Razor page code.

The existing `GrayMoon.Application` assembly and `IWorkspace*Operations` facade pattern are the correct direction and must be preserved.

---

# 59. Commands and Queries Both Need Context-Aware Application Contracts

MCP is not only a mutation surface.

A future AI agent must be able to query GrayMoon for state such as:

```text
list Workspaces
list Features
describe a Feature
repository state
dependency graph
dependency updates required
version-file mismatches
Git Changes
projects
packages
PR state
Actions state
running operations
Feature cleanup safety
```

Therefore Feature migration must context-enable both:

```text
application commands
application queries
```

Do not make mutations context-aware while leaving query services implicitly bound to `WorkspaceRepositoryLink`.

Where query contracts are currently App-local, context migration should move or wrap the automation-relevant contract at the application boundary rather than create an MCP-specific query implementation later.

---

# 60. GrayMoon.Application Is the Future Automation Boundary

The codebase already contains the right architectural seam:

```text
GrayMoon.Application
```

It is a separate assembly and is already consumed by:

- Blazor-facing handlers/pages;
- thin REST endpoints;
- application service implementations.

REST already exposes many core GrayMoon operations through `IWorkspace*Operations`, including:

```text
Update
Push
Prepare Workspace
Sync
Return to Default
Pull
Undo Push
Restore Packages
Create PRs
Merge PR
Git Changes read/commit/stage/unstage
Update configured file versions
```

This is strong evidence that MCP can later be implemented as another adapter over the same contracts.

No `McpWorkspaceService` / `McpFeatureService` duplicate domain layer should be introduced.

---

# 61. Application Contract Ownership Should Improve as Context Migration Touches Types

`GrayMoon.Application` is already physically independent of `GrayMoon.App`, but several types physically located in the Application assembly still use historical namespaces such as:

```text
GrayMoon.App.Models
GrayMoon.App.Models.Api
GrayMoon.App.Services.GitHub
```

This is not currently a project-reference violation, but it makes ownership less clear for future automation consumers.

Do **not** perform a broad risky namespace rewrite only for MCP before worktrees.

Instead:

- every new Feature/context contract should use `GrayMoon.Application` ownership/namespaces;
- when an existing application contract is materially changed for context support, prefer moving its DTOs to clear Application-owned namespaces at the same time;
- avoid adding new dependencies from application contracts onto Blazor/page-specific models.

The objective is a stable application contract surface, not cosmetic renaming.

---

# 62. Structured Results and Errors Are Preferred for New Feature Operations

Future MCP callers need to distinguish conditions programmatically.

For new Feature/context lifecycle operations, results should prefer stable machine-readable conditions alongside human-readable messages.

Examples include:

```text
ContextNotFound
FeatureAlreadyExists
FeatureNameInvalid
BranchAlreadyCheckedOut
RemoteBranchCollision
UncommittedChanges
UnpushedCommits
UnmergedWork
AgentUnavailable
ConnectorUnavailable
ProtectedBranch
MergeConflict
OperationAlreadyRunning
CleanupIncomplete
```

Existing `OperationResult` may evolve or specialized result types may be used.

A broad rewrite of every current operation result is **not required before worktrees**.

But new Feature APIs should not return only opaque strings when the caller needs to make a safe decision.

---

# 63. Safety Analysis Must Be Reusable Separately From Execution

Destructive lifecycle operations should have a reusable headless analysis/preflight phase.

Conceptually:

```text
Analyze operation
    ↓
structured plan / consequences
    ↓
UX or future MCP decides whether execution is allowed
    ↓
Execute operation with explicit choices
```

This is especially important for:

```text
Remove Feature
Return to Default
Undo Push
discard Git Changes
delete branch
merge/cleanup flows
```

The human UX continues to render GrayMoon's normal confirmation dialogs.

Future MCP can consume the same structured analysis rather than recreating safety logic.

The worktree Feature design must implement `Remove Feature` this way from the start.

---

# 64. MCP and Concurrency

The approved context-level locking model is also the correct MCP model.

Multiple actors may eventually operate concurrently:

```text
Human UX  → Workspace
AI agent  → Feature A
AI agent  → Feature B
```

Ordinary operations on isolated contexts must not block each other.

Shared Workspace-structural mutations still take the Workspace-level lock.

MCP must use the same operation runner/locking rules as UX and REST; it must not bypass them.

---

# 65. MCP Must Not Bypass GrayMoon Safety or Domain Logic

Future MCP tools must call the same GrayMoon application operations responsible for:

- dependency planning;
- version-file handling;
- restore planning;
- generated packages;
- synchronized push;
- package availability waiting;
- Git Changes path validation;
- PR lifecycle;
- Feature cleanup safety;
- worktree ownership/reconciliation.

AI agents should not be given a lower-level MCP tool that merely shells arbitrary Git commands inside a GrayMoon-managed Feature as a substitute for GrayMoon domain operations.

Raw development tools can still operate in the physical Feature checkout independently, but the GrayMoon MCP surface should expose GrayMoon's higher-level capabilities.

---

# 66. Intended Future MCP Capability Shape

Exact MCP tool names are deferred, but the domain surface is expected to map naturally to operations such as:

```text
list_workspaces
list_features
get_feature_state
create_feature
analyze_remove_feature
remove_feature

get_repositories
get_projects
get_packages
get_dependency_graph
get_dependency_updates

get_version_file_status
update_version_files
restore_packages

get_git_changes
stage_changes
unstage_changes
commit_changes

sync
pull
push
push_updated

create_pull_requests
get_pull_request_status
merge_pull_request

get_actions_status
```

The important decision now is not these names.

The decision is:

> These future tools must map onto stable GrayMoon application commands/queries rather than reimplementing behavior in an MCP layer.

---

# 67. MCP Readiness Does Not Require MCP Implementation During Feature Work

The first Feature implementation should contain **no MCP server/tool implementation** unless this decision is explicitly reopened.

Feature work should only preserve the architectural properties required for MCP:

```text
explicit context identity
headless application operations
headless application queries
stable path abstraction
structured lifecycle analysis
context-level locking
machine-readable new Feature results
no duplicated domain logic
```

This is sufficient to avoid a future MCP-driven architectural rewrite.



# 68. MCP Readiness Prerequisite - Completed and Verified

The pre-start Return-to-Default prerequisite identified during the MCP-readiness review has now been implemented in GrayMoon.

Verified in code at commit:

```text
b10994375a8fd7711a92e34dc4f82070e992ea46
```

The implementation now provides shared headless lifecycle analysis through `IWorkspaceSyncOperations`:

```text
AnalyzeReturnToDefaultAsync(...)
    ↓
ReturnToDefaultPlan
```

with per-repository facts represented by:

```text
ReturnToDefaultRepositoryPlan
```

and explicit execution choices represented by:

```text
ReturnToDefaultOptions
```

Execution is exposed separately through:

```text
ExecuteReturnToDefaultAsync(...)
```

The current human UX consumes the shared analyzer instead of owning a separate Return-to-Default safety algorithm.

The unattended/REST path also calls the same analyzer first and refuses to proceed when:

```text
analysis fails
PR refresh fails
the plan is not automatically safe
```

Its unattended cleanup policy remains explicit and documented.

This establishes the desired lifecycle pattern:

```text
Analyze
    ↓
Authorize / choose options
    ↓
Execute
```

The pattern must be reused for `Remove Feature`.

There is no longer an MCP-readiness prerequisite blocking the worktree Feature implementation.

---

# 69. Final Pre-Implementation Gate

All product and architecture questions that must be settled before detailed Feature design are now considered resolved.

The detailed implementation design may proceed under these fixed constraints:

```text
1. Existing Workspace behavior is the regression baseline.
2. Workspace remains the special permanent checkout.
3. Feature = branch.
4. Every Feature contains every Workspace repository.
5. Feature worktrees live under GrayMoon-managed .graymoon infrastructure.
6. Workspace configuration is shared; checkout-derived state is context-specific.
7. WorkspaceFeatureContext is the authoritative execution context.
8. Existing Workspace is migrated onto the new context model before Feature worktrees/UX land (same continuous delivery; no product feature flag).
9. Mutable repository state, projects, dependency graph, PRs, Actions, Git Changes, version-file observations, and pending notifications are context-specific.
10. Git ref inventory is separated from context-specific HEAD/checkout state.
11. Git hooks and Agent sync notifications are context-attributed.
12. Context-sensitive commands and queries receive explicit context identity; they never infer it from browser selection.
13. Ordinary mutations lock per WorkspaceFeatureContext; structural mutations lock per Workspace.
14. Git Changes extends the existing watcher/lease architecture.
15. Feature lifecycle is local-first and works offline.
16. Remote publication/upstream is optional and opportunistic.
17. Feature cleanup uses explicit analyze -> authorize -> execute safety.
18. Desktop adds native launch actions but is not required for core Feature behavior.
19. MCP implementation is deferred until Feature UX has been thoroughly validated.
20. Future MCP will reuse GrayMoon.Application instead of reimplementing GrayMoon domain logic.
21. The special Workspace Branch dialog is worktree-aware.
22. GM-owned worktree branches are shown as `Feature` and route cleanup through Remove Feature.
23. non-GM linked worktree branches are shown as `Worktree` and use a safe external-worktree cleanup flow.
```

Implement Features as one continuous delivery: migrate the special Workspace onto the context-aware architecture, then land Feature worktrees/UX in the same effort. There is no product feature flag and no ship-Workspace-only-first gate.

The next document should be the detailed implementation design and migration plan.



# 70. Branch Dialog Must Be Worktree-Aware

The special Workspace branch dialog must become aware of Git linked worktrees.

Today a local branch checked out in another worktree is displayed like an ordinary local branch. This creates two incorrect interactions:

```text
Checkout
→ Git rejects because the branch is already checked out elsewhere

Delete
→ Git rejects because the branch is used by a linked worktree
```

After worktree-backed Features exist, this would also make GrayMoon-managed Feature branches look like ordinary branches, which is incorrect.

The local branch list must classify branches as:

```text
normal local branch
current Workspace branch
GrayMoon Feature branch
external worktree branch
```

Presentation:

```text
main                         [Current]
BAM-2856                     [Feature]
external-experiment          [Worktree]
normal-local-branch
```

Use existing GrayMoon badge styling. Do not introduce a new icon language.

`Feature` means the branch is checked out in a worktree owned by a GrayMoon `WorkspaceFeatureContext`.

`Worktree` means Git reports the branch as checked out in another linked worktree that GrayMoon does not own.

The data source must be Git worktree reality, preferably:

```text
git worktree list --porcelain
```

combined with GrayMoon's persisted Feature/worktree ownership.

A branch carrying either `Feature` or `Worktree` must not be treated as an ordinary checkout candidate from the special Workspace.

---

# 71. GrayMoon Feature Branches Must Route to Feature Lifecycle

A local branch identified as:

```text
[Feature]
```

must not use ordinary branch deletion.

Normal branch deletion would break the Feature invariant because a GrayMoon Feature owns:

```text
Feature metadata
WorkspaceFeatureContext
one worktree per Workspace repository
context-specific projections
branch identity
```

Therefore:

```text
Feature branch cleanup
→ Remove Feature lifecycle
```

The Branch dialog may expose an appropriate cleanup action, but it must call the same:

```text
AnalyzeRemoveFeature
→ authorization/confirmation
→ RemoveFeature
```

flow used by the Feature menu.

Do not implement a second Feature cleanup path inside the Branch dialog.

---

# 72. External Worktree Cleanup

A local branch identified as:

```text
[Worktree]
```

belongs to a linked Git worktree that GrayMoon does not own.

GrayMoon may provide a safe cleanup flow for this case.

The primary concept is:

```text
Remove worktree
```

not:

```text
Delete branch
```

The cleanup flow must analyze at least:

```text
worktree path
branch
HEAD
staged changes
unstaged changes
conflicts
upstream state
unpushed commits
```

It must work without internet.

A clean external worktree can be removed with normal Git worktree removal.

A dirty external worktree requires explicit destructive confirmation before force removal.

Branch deletion is a secondary option after the worktree has been removed.

Conceptually:

```text
git worktree remove <path>
git branch -d <branch>
```

or, only after explicit destructive authorization:

```text
git worktree remove --force <path>
git branch -D <branch>
```

GrayMoon must not auto-adopt the external worktree into Feature metadata.

GrayMoon must not silently delete an external worktree merely because the user clicked the ordinary branch trash icon.

Detached external worktrees that do not map naturally to a local branch row do not need to appear in the Branch dialog in the first implementation.


# Status

This is the final approved pre-design baseline unless a decision is explicitly reopened.

There are no remaining prerequisite code changes identified by the pre-design or MCP-readiness reviews. The next artifact should be the **full end-to-end GrayMoon Feature implementation design**, broken into safe implementation waves with schema changes, migration sequencing, Agent/Git commands, UI flows, path/context resolution, lifecycle/recovery behavior, Desktop integration, tests, rollback gates, and acceptance criteria.
