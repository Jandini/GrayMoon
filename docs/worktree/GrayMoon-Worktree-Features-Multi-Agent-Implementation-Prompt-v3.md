# GrayMoon Worktree Features - Multi-Agent Implementation Prompt

## Role

You are implementing GrayMoon worktree-backed **Features** in `Jandini/GrayMoon`.

This is a high-risk architectural migration touching GrayMoon's core multi-repository behavior.

Do not optimize for speed of implementation.

Optimize for:

```text
no regression
context isolation
migration safety
small reviewable waves
testability
recovery
exact adherence to existing GrayMoon UX
```

---

# Required Documents

These files are in the same folder as this prompt.

Read all of them completely before modifying code:

```text
GrayMoon-Features-Final-Pre-Design-Decision-Summary.md
GrayMoon-Workspace-Current-Features-Baseline-Appendix.md
GrayMoon-Worktree-Features-Detailed-Implementation-Design.md
```

Authority order:

```text
1. Final Pre-Design Decision Summary
2. Current Workspace Baseline Appendix
3. Detailed Implementation Design
4. This prompt
```

If implementation code appears to conflict with the documents, stop and investigate the current code before changing behavior.

Do not reinterpret approved decisions.

---

# Code Baseline

The design review was performed through:

```text
43f79b6484a3a99c65a04a3a55c80f7b615527be
```

The repository may have moved forward.

Before each wave:

1. inspect current HEAD;
2. search for changes in the files/services named in the design;
3. reconcile newer code with the same approved invariants;
4. do not blindly apply stale patches.

---

# Absolute Product Rules

Never violate these rules:

```text
Workspace remains the permanent special checkout.
Feature = Git branch.
Every Feature contains every Workspace repository.
Feature creation works without internet.
Dirty Workspace changes are not copied into a Feature.
Feature starts from committed HEAD per repository.
Workspace configuration is shared.
Checkout-derived state is context-specific.
Feature contexts do not block each other for ordinary operations.
Structural Workspace operations block context mutations safely.
No application operation infers context from browser state.
No MCP implementation is part of this work.
No new GrayMoon design language is introduced.
```

---

# Existing Behavior Is a Contract

The entire document:

```text
GrayMoon-Workspace-Current-Features-Baseline-Appendix.md
```

is a regression contract.

If the user selects `Workspace`, behavior must remain equivalent to the current application unless the pre-design explicitly approves a change.

Do not remove or simplify existing functionality to make Features easier.

Examples of core behavior that must survive:

```text
Prepare Workspace
Return to Default
Branch split-button quick access to Create PR when eligible
branch/tag behavior
dependency graph
custom dependencies
generated packages
version files
Push Updated
synchronized push/package waits
restore
Git Changes
Git hooks
PR state/create/merge
Actions
pending notifications
background jobs/loading overlay
Workspace import/repository management
```

---

# Implementation Strategy

Implement Features as one continuous delivery. There is no product feature flag and no ship-Workspace-only-first gate.

Waves from the detailed design remain an internal sequencing aid only:

```text
GrayMoon-Worktree-Features-Detailed-Implementation-Design.md
```

A later wave should not start until the previous wave's technical gate is satisfied within the same continuous effort.

Sequence context migration ahead of Feature worktrees/UX for correctness, but complete both in the same continuous Features delivery.

---

# Required Wave Order

Implement in this order:

```text
Wave 0  baseline references (no product feature flag)
Wave 1  additive schema + special Workspace context backfill
Wave 2  context/path resolver + explicit application context
Wave 3  context repository state writer + repository queries
Wave 4  Git hooks attribution + shared ref inventory
Wave 5  projects/packages/dependency graph
Wave 6  Files/version-file observations
Wave 7  PR/Actions/Git Changes/notifications/SignalR
Wave 8  hierarchical operation locking
Wave 9  all current Workspace pages through context architecture
         full manual Workspace regression gate (same continuous delivery)
Wave 10 Agent worktree primitives + backend Feature lifecycle + external worktree cleanup
Wave 11 Feature UX + worktree-aware Branch dialog
Wave 12 Desktop native launch actions
Wave 13 recovery/hardening
Wave 14 contract cleanup only after approval
```

Do not move Feature creation earlier.

---

# Multi-Agent Coordination

## Foundational waves are sequential

Waves 1-4 have heavy overlap in:

```text
AppDbContext
models
WorkspaceRepositoryStateWriter
SyncCommandHandler
application contracts
root resolution
```

Only one lead agent should own these foundational files at a time.

Do not assign multiple agents to independently redesign the same schema or context identity.

## Parallel work is allowed only after interfaces are fixed

After the context schema/resolver/state writer contracts are merged, separate agents may work on isolated subsystems such as:

```text
Agent A: projects/dependencies/packages
Agent B: Files/version files
Agent C: Git Changes
Agent D: PR/Actions
Agent E: UI context selector/navigation
Agent F: Agent worktree commands/lifecycle
Agent G: Desktop integration
```

Each agent must use the shared context contracts created by the foundation.

They must not create subsystem-specific alternatives.

---

# Required Implementation Report Per Wave

At the end of every wave, produce a report containing:

```text
1. exact files changed
2. migrations added
3. contracts added/changed
4. behavior intentionally changed
5. behavior intentionally NOT changed
6. tests added/updated
7. build result
8. test result
9. manual test instructions
10. known risks / deferred items
11. code-search checks performed
```

Then STOP.

Do not automatically continue to the next wave unless explicitly instructed.

---

# Database Rules

Use:

```text
expand
→ backfill
→ switch
→ contract
```

Never combine destructive schema contraction with initial context cutover.

Every existing Workspace must receive one real:

```text
WorkspaceFeatureContext Kind=Workspace
```

Do not represent the special Workspace as "context = null".

Context-state uniqueness must use:

```text
WorkspaceFeatureContextId + WorkspaceRepositoryId
```

Do not rely on nullable unique-index behavior for the special context.

---

# Context Identity Rules

For normal context-sensitive application operations use:

```text
WorkspaceFeatureContextId
```

as authoritative identity.

Resolve Workspace from context.

Do not pass/use:

```text
current selected Feature service
current page
current route
Desktop current context
```

inside application/domain operations.

The UI is responsible for resolving its explicit ContextId and passing it.

Future MCP must be able to call the same application operation without browser state.

---

# Path Rules

All context-aware local operations must go through the shared context path resolver.

Search the code for:

```text
GetRootPathForWorkspaceAsync
```

Every context-sensitive use must be migrated.

Remaining direct calls must be demonstrably Workspace-structural only.

Do not manually construct Feature paths in individual services.

Do not use physical paths as database/domain identity.

---

# WorkspaceRepositoryLink Rules

The current model contains many mutable checkout fields.

After state cutover these fields must no longer be runtime authoritative:

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

They move to context state as specified by the design.

Do not indefinitely dual-write old and new state.

A temporary rollback window is acceptable; two sources of truth are not.

---

# Git Hook Rules

Git worktrees share common Git hook infrastructure.

Do not write Feature-specific static hook files.

Hooks must determine the actual executing worktree root dynamically and send that physical path with repository/workspace identity.

App-side code resolves/validates the path to:

```text
WorkspaceFeatureContextId
WorkspaceRepositoryId
```

Unknown/ambiguous hook path:

```text
must not write state
must not fall back to Workspace
must log/reconcile safely
```

Correctness cannot depend only on Agent in-memory state surviving restart.

---

# Git Ref Rules

Do not duplicate repository ref inventory per Feature.

Shared:

```text
local branch list
remote branch list
tags
default branch
```

Context-specific:

```text
current branch
current tag
HEAD
upstream
ahead/behind
```

Do not add "current branch" semantics to `RepositoryBranches`.

---

# Project / Dependency Rules

This is a core Feature requirement.

Any project scan/update in one context must not mutate another context.

Dependency graph calculations must use only projects from one context plus shared custom configuration.

Tests must prove:

```text
Feature A project added
→ Feature A Projects/Packages/Deps change
→ Workspace unchanged
→ Feature B unchanged
```

Generated/virtual packages remain supported.

Do not let ordinary project merge delete generated rows or rows from another context.

---

# Files / Version Files Rules

Configuration is shared:

```text
file list
relative file path
version pattern
```

Observations are context-specific:

```text
file missing
current line values
expected values
mismatch lines
derived dependency observations
```

Updating configured files must resolve the selected context path.

---

# Git Changes Rules

Do not redesign Git Changes.

Extend the existing watcher/lease architecture.

Registry becomes context-aware.

Snapshot writes replace only one context/repository projection.

Do not poll every Feature permanently.

Preserve:

```text
watcher debounce/coalescing
persisted projection
single SQLite write queue
path validation
large pathspec handling
existing commit/stage/unstage behavior
Repositories-page watcher activation
```

---

# PR / Actions Rules

PR and Actions state belong to the context branch.

Do not store/retrieve them by `WorkspaceRepositoryId` alone after cutover.

A refresh from Feature A must never clear or overwrite Workspace/Feature B.

Actions branch-match validation remains.

AI workflow exclusion remains shared Workspace configuration.

---

# SignalR Rules

Feature-specific state changes must include context identity.

Never broadcast Feature A as a plain legacy:

```text
WorkspaceSynced(workspaceId)
RepositorySynced(workspaceId, repositoryId)
```

if an old handler could interpret it as the special Workspace.

Compatibility broadcasts are allowed only for the special Workspace during migration.

---

# Operation Locking Rules

Implement hierarchical locking exactly:

## Context mutation

Can start when:

```text
no structural Workspace mutation
no mutation already running for same context
```

Different contexts may run concurrently.

## Structural Workspace mutation

Can start only when:

```text
no structural mutation
no mutation in any context of that Workspace
```

Feature create/remove are structural.

Repository membership changes are structural.

Tests are mandatory.

---

# UI Rules

Reuse exact GrayMoon patterns.

Do not introduce new:

```text
icons
button style
modal layout
callout style
spinner style
keyboard conventions
menu interaction patterns
```

unless they already exist in GM.

Use:

```text
WorkspaceContextBar
    WorkspaceFeatureSelector + existing FilterSearchInput
```

Workspace selected:

Preserve the current split-button behavior exactly.

Normal:

```text
[ Branch ][▼]
```

If the selected Workspace context has at least one repository eligible to
create a PR:

```text
[ Create PR ][▼]
```

The yellow primary `Create PR` opens the existing multi-repository PR creation
flow. The caret always opens the Branch dropdown.

Eligibility must remain equivalent to the current header query:

```text
not on tag
AND ahead of default > 0
AND no open/merged/closed PR
```

After context migration this eligibility must come from the selected context's
state. Do not regress this shortcut while replacing `WorkspaceRepositoryLink`
state with context projections.

Feature selected:

```text
Feature ▼
├─ Create PRs
└─ Remove Feature
```

`Remove Feature` has no ellipsis.

---

# URL / Selection Rules

Use explicit Feature context query identity:

```text
?context=<WorkspaceFeatureContextId>
```

Preserve existing routes.

Special Workspace may omit query.

Persist last selected ContextId per Workspace for initial navigation only.

Never let that preference become implicit execution authority.

Navigation must preserve context across:

```text
Repositories
Changes
Projects
Packages
Files
Deps
Actions
```

---


# Branch Dialog Worktree Awareness

The special Workspace Branch dialog must become worktree-aware.

A local branch row may be:

```text
normal branch
Current
Feature
Worktree
```

Classification must use:

```text
git worktree list --porcelain
+
GrayMoon WorkspaceFeatureRepository ownership
```

Presentation:

```text
main                         [Current]
ABC-2856                     [Feature]
external-experiment          [Worktree]
```

Use existing GrayMoon badge styling only.

## Feature branch

`Feature` means GrayMoon owns the worktree through Feature metadata.

Rules:

```text
do not allow checkout from special Workspace
do not allow ordinary branch delete
cleanup must route to the same AnalyzeRemoveFeature / RemoveFeature lifecycle
```

Do not implement Feature cleanup twice.

## External worktree branch

`Worktree` means Git reports the branch checked out in a linked worktree that GrayMoon does not own.

Rules:

```text
do not allow checkout from special Workspace
do not call ordinary branch delete first
offer safe Remove Worktree flow
```

Implement headless:

```text
AnalyzeExternalWorktreeCleanup
RemoveExternalWorktree
```

Analysis must include:

```text
worktree path
HEAD
staged/unstaged changes
conflicts
upstream
unpushed commits
dirty state
```

Clean removal:

```text
git worktree remove <path>
```

Dirty removal requires explicit destructive authorization before:

```text
git worktree remove --force <path>
```

Local branch deletion is optional and occurs only after successful worktree removal.

Use `git branch -D` only after explicit destructive authorization.

Do not auto-adopt external worktrees into GrayMoon Feature metadata.

Detached external worktrees do not need to appear in the Branch list in the first implementation.


# Feature Creation Rules

First release base:

```text
Current Workspace
```

means committed special-Workspace HEAD per repository.

Dirty/staged Workspace files are not copied.

Normal creation must preflight every repository before mutation.

Reject:

```text
invalid branch name
duplicate GM Feature
existing local branch with same name
known remote branch collision
same branch already checked out
invalid/missing source repository
unsafe target path
```

Do not:

```text
force
auto-adopt
auto-rename
push
require internet
```

Persist creation intent before Git mutation.

Partial create must remain recoverable and retryable.

Feature becomes `Ready` only after all repository worktrees and initial context projections are valid.

---

# Worktree Command Rules

Use Git worktree commands.

Do not copy `.git`.

Do not manually edit `.git/worktrees`.

Normal create:

```text
git worktree add -b <feature> <path> <base-sha>
```

Normal remove:

```text
git worktree remove <path>
```

Use force removal only after explicit destructive authorization.

Use:

```text
git worktree list --porcelain
```

for reconciliation.

Do not globally prune worktrees as a shortcut.

---

# Remove Feature Rules

Implement:

```text
AnalyzeRemoveFeature
→ user/application authorization
→ RemoveFeature
```

Reuse the architectural pattern established by Return to Default.

Analysis must include local changes, unpushed/unmerged work, PR state, upstream/remote state, and physical worktree health.

Merged PR:

```text
clean completion path
```

Closed unmerged / explicit abort:

```text
destructive abandonment path with explicit warning
```

Offline local cleanup must work.

Remote deletion is optional.

Partial remote/local cleanup leaves recoverable metadata.

---

# Workspace Membership Rule While Features Exist

For the first release, unless a fully transactional fanout implementation is explicitly added:

```text
block add/remove Workspace repository while any Feature exists
```

This preserves the hard invariant that every Feature contains every Workspace repository.

Do not silently create partial Features.

---

# Feature Storage Rule

Default managed root:

```text
<parent Workspace root>/.graymoon/<WorkspaceName>/features
```

Persist the resolved storage root/worktree paths.

Do not automatically move existing Features when Workspace is renamed.

If main repository movement breaks worktree metadata, detect and mark repair state rather than manually editing Git internals.

---

# Remote / Upstream Rules

Feature existence is local-first.

Remote/upstream is optional.

First natural push may establish:

```text
git push -u origin <feature>
```

If an unexpected remote branch already exists:

```text
do not force push
return structured collision
keep local Feature healthy
```

Branch rename/reconciliation UX is deferred.

---

# Desktop Rules

Core Feature behavior must work without Desktop.

Desktop may add:

```text
Open in Cursor
Open in Visual Studio
Open Terminal
Open in Explorer
```

through the existing native bridge.

App resolves the Feature root.

Desktop validates and launches.

Do not put OS process launching into GrayMoon.App.

---

# MCP Rules

Do not implement MCP.

Do make all new Feature operations suitable for later MCP by ensuring:

```text
headless application contracts
explicit context id
structured analysis/results
no Blazor dependency
no Desktop dependency
no path construction by caller
same operation locking as UI/REST
```

Do not create parallel `Mcp*Service` domain logic.

---

# No Product Feature Flag

There is no `WorkspaceFeaturesOptions.Enabled` (or equivalent) product flag.

Do not add Wave 0 feature-flag scaffolding.

Do not ship or leave Features disabled behind a config switch while foundational waves complete.

Implement Features in one continuous effort; waves are sequencing only.

---

# Required Search Audit Before Feature Worktrees/UX

Perform and report searches for:

```text
GetRootPathForWorkspaceAsync

WorkspaceRepositoryLink.GitVersion
WorkspaceRepositoryLink.BranchName
WorkspaceRepositoryLink.CheckedOutTag
WorkspaceRepositoryLink.OutgoingCommits
WorkspaceRepositoryLink.IncomingCommits
WorkspaceRepositoryLink.DependencyLevel
WorkspaceRepositoryLink.UnmatchedDeps

WorkspaceRepositoryPullRequest
WorkspaceRepositoryAction
WorkspaceGitRepositoryStatus
WorkspaceGitChangeEntry

WorkspaceSynced
RepositorySynced
GitChangesUpdated

HasCreatablePr
WorkspaceRepositoryHeaderStateDto
HandleBranchPrimaryClick

SwitchBranchModal
DeleteBranchAsync
GetBranchesAsync
git worktree list
WorkspaceFeatureRepository

WorkspaceOperationRunner
WorkspaceJobKeys
```

For every remaining occurrence, state why it is valid.

Do not leave unexplained single-checkout assumptions.

---

# Testing Requirements

Do not accept "build passes" as sufficient.

Every wave needs tests for the migrated subsystem.

Before Feature UI:

```text
full automated test suite
full baseline manual Workspace regression
```

After Feature UI:

test at minimum:

```text
Workspace
Feature A
Feature B
```

with independent edits and operations.

Must prove:

```text
state isolation
hook isolation
project/dependency isolation
version-file isolation
Git Changes isolation
PR/Actions isolation
context-level concurrency
crash/retry recovery
offline creation/work
safe removal
worktree-aware Branch dialog
safe cleanup of external linked worktrees
```

---

# No Opportunistic Refactors

Do not use this implementation to broadly:

```text
rename unrelated classes
rewrite page layout
replace EF patterns
change CSS
change API response formats
redesign Git Changes
redesign dependency algorithms
redesign Actions polling
```

unless required by context correctness.

Keep diffs narrow.

---

# Failure Policy

Never hide a partially completed structural operation by deleting its metadata.

Persist:

```text
Creating
Removing
NeedsRepair
per-repository state/error
```

and reconcile against actual Git worktrees.

Git is authoritative for physical reality.

SQLite is authoritative for GM ownership/intent and cached projections.

---

# STOP Conditions

Stop implementation and report instead of guessing if:

```text
an approved decision appears impossible in current code
schema migration would destroy existing data
a newer code change materially invalidates the documented migration seam
worktree behavior differs from expected Git semantics
a change would require weakening existing safety
an existing baseline feature must be removed to proceed
```

Do not invent a workaround that violates the documents.

---

# Completion Definition

The Feature implementation is not complete merely when a worktree can be created.

It is complete when:

```text
Workspace regression baseline passes
Feature A and Feature B remain isolated
all applicable GM pages follow selected context
core orchestration uses context-specific project/dependency/version state
hooks update the correct context
Git Changes watches the correct worktree
PR/Actions are context-specific
ordinary contexts can operate concurrently
Feature create/remove is crash-recoverable
offline local workflow works
existing GM UX remains coherent
```

Only after this has been heavily user-tested should MCP implementation begin.

