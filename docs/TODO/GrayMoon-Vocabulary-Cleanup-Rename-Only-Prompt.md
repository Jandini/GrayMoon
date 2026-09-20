# GrayMoon Vocabulary Cleanup — Rename-Only Implementation Prompt

## Objective

Perform a **strict rename-only refactor** across the GrayMoon codebase so the existing product vocabulary is clean before any worktree / Feature functionality is introduced.

This task is deliberately limited to terminology and symbol/file renaming.

**Do not change behavior, control flow, Git operations, persistence behavior, orchestration order, validation rules, defaults, API payload semantics, UI interaction flow, or business logic.**

The existing functionality must work exactly as it does before this refactor.

---

# Locked Product Vocabulary

The following product terminology is now canonical:

| Old term | New term |
|---|---|
| **New Feature** | **Prepare Workspace** |
| **Sync to Default** / **Sync To Default** | **Return to Default** |
| Main Branch menu **Create PRs...** | **Create PRs** |
| Dependency-level menu **Create PRs...** | **Create PRs...** — keep the ellipsis here |

Important:

- **Feature** is being reserved for a future worktree-backed concept.
- Therefore, the existing "New Feature" workflow must no longer use `Feature` terminology anywhere in active code.
- GrayMoon's existing **Sync** terminology remains unchanged. `Sync` already means refreshing/linking repository state with the GrayMoon Agent.
- Only the old **Sync to Default** branch-cleanup concept becomes **Return to Default**.

---

# Non-Negotiable Scope Rule

This is a **pure vocabulary refactor**.

Do not:

- add worktree support;
- add a Feature model;
- change branch behavior;
- change dependency update behavior;
- change synchronized push behavior;
- change PR behavior;
- change merge behavior;
- change Return-to-Default behavior;
- change UI layouts;
- change dialog structure;
- change button ordering;
- introduce new options;
- remove existing options;
- alter state persistence;
- alter Git commands;
- alter retry behavior;
- alter background job behavior;
- alter error handling except wording required by the rename;
- "clean up" unrelated code;
- opportunistically refactor implementation;
- change any algorithm;
- change any defaults.

Where practical, rename symbols/files and leave method bodies structurally identical.

If an existing test exposes unrelated broken behavior, report it rather than fixing it as part of this task.

---

# Part 1 — Rename "New Feature" to "Prepare Workspace"

The current workflow called **New Feature** is not the future worktree Feature concept.

Its behavior remains exactly as it is today:

1. create/check out the requested branch across applicable workspace repositories;
2. use the selected base branch;
3. optionally skip repositories on tags;
4. optionally update dependency versions;
5. commit the same changes it currently commits;
6. optionally perform the same synchronized push;
7. finish with the same persisted workspace state and errors.

Only its vocabulary changes.

## UI

Rename the Branch menu item:

```text
New Feature
```

to:

```text
Prepare Workspace
```

Rename the modal title:

```text
New Feature
```

to:

```text
Prepare Workspace
```

Keep the dialog fields and behavior unchanged.

In particular, keep:

```text
Branch name
Based on
Skip repos on tags
Update dependencies
Push changes
```

Do not rename `Branch name` to Feature name.

The existing workflow still prepares a branch across the current Workspace.

## Required code-level rename

Rename all active code symbols using the old New Feature vocabulary.

The exact repository state may contain additional symbols, so search exhaustively, but expected renames include at least:

```text
NewFeatureModal
    -> PrepareWorkspaceModal

NewFeatureRequest
    -> PrepareWorkspaceRequest

NewFeatureModels.cs
    -> PrepareWorkspaceModels.cs

NewFeatureModal.razor
    -> PrepareWorkspaceModal.razor

WorkspaceRepositories.NewFeature.cs
    -> WorkspaceRepositories.PrepareWorkspace.cs

NewFeatureModalState
    -> PrepareWorkspaceModalState

_newFeatureModal
    -> _prepareWorkspaceModal

ShowNewFeatureModalAsync
    -> ShowPrepareWorkspaceModalAsync

CloseNewFeatureModal
    -> ClosePrepareWorkspaceModal

HandleNewFeatureCreateAsync
    -> HandlePrepareWorkspaceAsync
       (or an equally clear PrepareWorkspace-prefixed name)

OnNewFeature
    -> OnPrepareWorkspace

HandleNewFeatureClick
    -> HandlePrepareWorkspaceClick

NewFeatureOrchestrator
    -> PrepareWorkspaceOrchestrator

NewFeatureOrchestrator.cs
    -> PrepareWorkspaceOrchestrator.cs
```

### Application facade

The existing application facade is currently named around "Feature" and must be renamed now because **Feature** will later mean a worktree-backed environment.

Expected rename:

```text
IWorkspaceFeatureOperations
    -> IWorkspacePreparationOperations

WorkspaceFeatureOperations
    -> WorkspacePreparationOperations
```

The operation currently named around creating a Feature should become preparation terminology.

For example:

```text
CreateAsync(...)
    -> PrepareAsync(...)
```

when it specifically represents this workflow.

Do not rename generic methods whose meaning is unrelated.

Update all dependency injection registrations, constructor parameters, tests, mocks, endpoint dependencies, documentation, and call sites.

### API

The current workspace operation API includes:

```text
POST /api/workspaces/{workspaceId}/new-feature
NewFeature(...)
NewFeatureApiRequest
```

Rename this vocabulary consistently:

```text
POST /api/workspaces/{workspaceId}/prepare-workspace
PrepareWorkspace(...)
PrepareWorkspaceApiRequest
```

Update all internal callers/tests at the same time.

Do **not** change the payload semantics.

Fields such as:

```text
NewBranchName
BaseBranch
RepositoryIds
UpdateDependencies
PushChanges
CommitMessage
```

remain semantically correct.

`NewBranchName` describes an actual new Git branch and should remain unless there is a direct old-product-name dependency that requires otherwise.

Do not add compatibility aliases as part of this task unless the repository contains an explicit compatibility test or documented external contract requiring one. This task is intended to establish the clean new vocabulary atomically across the codebase.

### Progress messages, logs and comments

Replace terminology such as:

```text
New Feature: orchestration failed...
new feature modal
New Feature workflow
```

with Prepare Workspace terminology.

However, factual messages that describe the current operation, such as:

```text
Creating branches...
Preparing push...
```

may remain unchanged if already accurate.

Do not rewrite messages unnecessarily.

### Documentation

Update active architectural/developer documentation such as `CLAUDE.md`, code comments, summaries, and current design descriptions so they no longer describe this operation as New Feature.

The final active architecture should refer to:

```text
PrepareWorkspaceOrchestrator
Prepare Workspace workflow
Workspace preparation
```

and must leave **Feature** available for the future worktree concept.

---

# Part 2 — Rename "Sync to Default" to "Return to Default"

The existing branch cleanup operation currently called **Sync to Default** must be renamed comprehensively to **Return to Default**.

This operation remains behaviorally identical.

It still performs exactly the same branch/default-branch cleanup operations it performs today.

Do not change:

- safety checks;
- merged-PR checks;
- ahead/behind checks;
- fetch behavior;
- local branch deletion behavior;
- remote branch deletion behavior;
- force-delete behavior;
- checkout behavior;
- pull behavior;
- persistence behavior;
- error behavior;
- merge chaining;
- bulk merge behavior.

## User-facing wording

Replace:

```text
Sync To Default
Sync to Default
Sync to default
Sync to default branch
```

when referring to this specific cleanup operation with appropriate forms of:

```text
Return to Default
Return to default
Return to default branch
```

Examples:

Main Branch menu:

```text
Return to Default
```

Options dialog title:

```text
Return to Default
```

Merge dialog checkbox:

```text
Return to default branch
```

or, where the timing is explicit:

```text
Return to default branch after merging
```

Bulk merge checkbox:

```text
Return to default branch after merging
```

Errors:

```text
Failed to return to default branch.
```

Status/toast text should use the same vocabulary naturally.

## Required code-level rename

Perform an exhaustive rename of active code symbols.

Expected examples include, but are not limited to:

```text
WorkspaceRepositories.SyncToDefault.cs
    -> WorkspaceRepositories.ReturnToDefault.cs

SyncToDefaultOptionsModal
    -> ReturnToDefaultOptionsModal

SyncToDefaultOptionsModal.razor
    -> ReturnToDefaultOptionsModal.razor

SyncToDefaultRepoItem
    -> ReturnToDefaultRepoItem

SyncToDefaultRepoItem.cs
    -> ReturnToDefaultRepoItem.cs

SyncToDefaultCheckResult
    -> ReturnToDefaultCheckResult

ShowSyncToDefaultOptions
    -> ShowReturnToDefaultOptions

SyncToDefaultLevelAsync
    -> ReturnToDefaultLevelAsync

SyncAllToDefaultAsync
    -> ReturnAllToDefaultAsync
       or another grammatically clear ReturnToDefault-prefixed equivalent

HandleSyncAllToDefaultClick
    -> HandleReturnToDefaultClick

OnSyncAllToDefault
    -> OnReturnToDefault

SyncToDefaultSingleAsync
    -> ReturnToDefaultSingleAsync

SyncToDefaultDirectAsync
    -> ReturnToDefaultDirectAsync

SyncToDefaultAsync
    -> ReturnToDefaultAsync
```

Use names that read naturally in context, but there must be **no active application concept left named `SyncToDefault`**.

## Merge / PR state names

Rename merge-related state as well.

Expected examples:

```text
MergePullRequestSyncToDefaultPreferenceService
    -> MergePullRequestReturnToDefaultPreferenceService

SyncToDefaultPreferenceService
    -> ReturnToDefaultPreferenceService

SyncToDefault
    -> ReturnToDefault

BulkMergeSyncToDefault
    -> BulkMergeReturnToDefault
```

For records such as:

```csharp
MergePullRequestChoice(MergeMethod Method, bool SyncToDefault)
```

rename the property:

```csharp
MergePullRequestChoice(MergeMethod Method, bool ReturnToDefault)
```

Update all uses without altering control flow.

---

# Part 3 — Rename the Agent Protocol Too

This vocabulary cleanup must include the GrayMoon Agent command model.

The old command is currently represented by types and protocol names such as:

```text
SyncToDefaultBranchCommand
SyncToDefaultBranchRequest
SyncToDefaultBranchResponse
"SyncToDefaultBranch"
```

Rename them atomically to:

```text
ReturnToDefaultBranchCommand
ReturnToDefaultBranchRequest
ReturnToDefaultBranchResponse
"ReturnToDefaultBranch"
```

Rename the corresponding files.

Update:

- DI registrations;
- `CommandDispatcher`;
- `CommandJobFactory`;
- CLI command registration;
- App-side command sending;
- response deserialization;
- tests;
- logs;
- comments;
- documentation.

The request/response JSON payload fields themselves should remain unchanged unless they literally encode the old product terminology.

Do not alter command execution logic.

The App and Agent must compile and operate together after the rename.

Do not retain a second old command name unless an explicit repository compatibility contract requires it.

---

# Part 4 — Workspace Operations API Rename

Rename:

```text
POST /api/workspaces/{workspaceId}/sync-to-default
```

to:

```text
POST /api/workspaces/{workspaceId}/return-to-default
```

Rename:

```text
SyncToDefault(...)
SyncToDefaultApiRequest
```

to:

```text
ReturnToDefault(...)
ReturnToDefaultApiRequest
```

The implementation and response behavior must remain equivalent.

Also rename:

```text
POST /api/workspaces/{workspaceId}/new-feature
```

to:

```text
POST /api/workspaces/{workspaceId}/prepare-workspace
```

with:

```text
NewFeature(...)
NewFeatureApiRequest
```

becoming:

```text
PrepareWorkspace(...)
PrepareWorkspaceApiRequest
```

Update repository tests/callers together.

Again: this is a vocabulary migration, not a new API design.

---

# Part 5 — "Create PRs" Ellipsis Rule

Apply this rule exactly.

## Main Branch menu

Change:

```text
Create PRs...
```

to:

```text
Create PRs
```

No ellipsis.

## Dependency-level header menu

Keep:

```text
Create PRs...
```

with the ellipsis.

Reason: this menu mixes immediate actions and actions that open dialogs. In that context the ellipsis intentionally communicates that another dialog follows.

Do not globally replace `Create PRs...`.

Check the actual component context before changing each occurrence.

Existing `Merge PRs...` in the dependency-level menu is outside the scope of this rename unless required for consistency by an already-existing naming rule. Do not change it opportunistically.

---

# Part 6 — Preserve Existing "Sync" Vocabulary

Do **not** perform a broad `Sync -> ...` rename.

GrayMoon uses **Sync** as a separate established concept for refreshing/linking repository state with the Agent.

The following kinds of names are unrelated and must remain unless they specifically represent the old Sync-to-Default operation:

```text
Sync
SyncAsync
WorkspaceSyncHandler
WorkspaceSyncHub
SyncCommand
SyncRepository
SyncStatus
Sync commits
synchronized push
```

Similarly, do not rename ordinary English uses of "synchronize" that describe package push ordering or state refresh.

Only the branch cleanup concept formerly named `SyncToDefault*` / "Sync to default" changes.

---

# Part 7 — Preserve Actual Git "Feature Branch" Meaning Where Appropriate

Do not mechanically erase every English occurrence of the word `feature`.

The purpose is to remove **New Feature as the name of this GrayMoon workflow** and free **Feature** as an application concept for future worktrees.

A comment that generically refers to a Git "feature branch" may still be technically valid.

However:

- classes;
- services;
- API operations;
- UI labels;
- modal names;
- orchestration names;
- state records;
- handlers;
- tests;

that specifically refer to the current **New Feature workflow** must be renamed.

Use semantic judgment, then verify by search.

---

# Part 8 — Files and Namespaces

Rename files alongside classes/components.

Do not leave misleading filenames such as:

```text
NewFeatureOrchestrator.cs
WorkspaceRepositories.NewFeature.cs
NewFeatureModal.razor
SyncToDefaultRepoItem.cs
WorkspaceRepositories.SyncToDefault.cs
SyncToDefaultOptionsModal.razor
SyncToDefaultBranchCommand.cs
SyncToDefaultBranchRequest.cs
SyncToDefaultBranchResponse.cs
MergePullRequestSyncToDefaultPreferenceService.cs
```

after their symbols have been renamed.

Keep existing namespaces unless renaming a namespace is strictly required by an old vocabulary segment.

Do not reorganize folders.

---

# Part 9 — Tests

Update all tests, test names, fixtures, mocks, fake registrations, API calls, and assertions that use the old vocabulary.

Rename test methods/classes when their names encode:

```text
NewFeature
SyncToDefault
```

to the new vocabulary.

Do not alter what the tests verify.

The test suite should prove that only names changed and behavior remained stable.

Run the repository's normal build/test suite.

At minimum:

1. build the full solution;
2. run all relevant GrayMoon.App tests;
3. run all relevant GrayMoon.Agent tests;
4. run API tests;
5. run orchestration tests;
6. run component tests if present;
7. run command dispatcher/factory tests if present.

If the project has a documented canonical validation command, use it.

Do not weaken or delete tests merely to make the rename pass.

---

# Part 10 — Exhaustive Search Gate

Before considering the task complete, perform case-sensitive and case-insensitive repository-wide searches for the old vocabulary.

Search at minimum for:

```text
NewFeature
New Feature
new feature

SyncToDefault
Sync To Default
Sync to Default
Sync to default
sync-to-default

SyncToDefaultBranch

/new-feature
/sync-to-default
```

Review every remaining match.

For active product/code vocabulary, the expected count should be zero.

A remaining occurrence is acceptable only if it is genuinely historical or unrelated prose and retaining it is intentional. Report every intentional exception explicitly.

Also search for renamed filenames.

Ensure no active files retain the old workflow names.

---

# Part 11 — Manual Verification Gate

This task is complete only after automated validation passes and the resulting build is ready for manual testing.

Do not claim manual testing was performed unless a human actually performed it.

Provide this manual checklist in the implementation summary:

## Prepare Workspace

- Open Workspace Repositories.
- Open Branch menu.
- Confirm the menu says **Prepare Workspace**.
- Open it.
- Confirm modal title says **Prepare Workspace**.
- Confirm all existing fields/options are unchanged.
- Run the same workflow previously known as New Feature.
- Verify branch creation behavior is unchanged.
- Verify dependency update behavior is unchanged.
- Verify synchronized push behavior is unchanged.
- Verify errors/progress behave as before.

## Create PRs

- Confirm main Branch menu says **Create PRs** with no ellipsis.
- Confirm opening it behaves exactly as before.
- Open a dependency-level menu.
- Confirm it still says **Create PRs...** with ellipsis.
- Confirm behavior is unchanged.

## Return to Default

- Confirm Branch menu says **Return to Default**.
- Open it and verify the existing options/flow are unchanged.
- Verify level-specific Return-to-Default action.
- Verify the merge PR dialog wording.
- Verify the bulk merge wording.
- Verify Return-to-Default after merge behaves exactly as before.
- Verify local/remote branch deletion options behave exactly as before.
- Verify errors/toasts use the new terminology.

## Sync

- Verify the normal GrayMoon **Sync** action still exists and behaves exactly as before.
- Verify no generic Sync behavior was accidentally renamed or modified.

---

# Part 12 — Behavioral Diff Review

Before finalizing, inspect the Git diff specifically for accidental behavioral changes.

The diff should overwhelmingly consist of:

- file renames;
- class/type renames;
- method/property/local-variable renames;
- route/command vocabulary changes;
- UI text changes;
- log/comment/doc changes;
- test symbol/name updates.

Pay special attention to accidental changes in:

- boolean conditions;
- defaults;
- LINQ predicates;
- repository selection;
- tag filtering;
- branch selection;
- dependency levels;
- commit logic;
- push sequencing;
- cancellation;
- retries;
- exception handling;
- persistence;
- Git commands.

If any such logic changed, revert it unless the change is mechanically required to compile after a symbol rename.

---

# Expected Final Vocabulary

After this task, GrayMoon should have the following clear product dictionary:

### Prepare Workspace

The existing operation that prepares branches across the current Workspace, optionally updates dependencies, commits changes, and performs synchronized push.

### Return to Default

The existing branch cleanup operation that returns repositories to their default branches using the same current safety/deletion/pull behavior.

### Create PRs

The workspace-level action for creating pull requests.

Dependency-level dialog-opening action remains:

```text
Create PRs...
```

### Sync

Unchanged GrayMoon concept for refreshing/linking repository state with the Agent.

### Feature

Not implemented in this task.

The term is intentionally being freed for the future worktree-backed Feature environment design.

---

# Completion Requirements

Do not stop after changing visible labels.

The task is complete only when:

- all New Feature workflow code uses Prepare Workspace vocabulary;
- `IWorkspaceFeatureOperations` / `WorkspaceFeatureOperations` and related old workflow symbols are gone;
- all Sync-to-Default application code uses Return-to-Default vocabulary;
- all Agent command/request/response/protocol names use Return-to-Default vocabulary;
- corresponding API routes/types use the new vocabulary;
- main Branch menu uses `Create PRs`;
- dependency-level menu retains `Create PRs...`;
- generic GrayMoon Sync terminology remains untouched;
- filenames match renamed classes/components;
- logs/comments/current docs are updated;
- all callers and tests are updated;
- build succeeds;
- automated tests pass;
- exhaustive searches for old active vocabulary have been reviewed;
- no behavior or functionality was intentionally changed.

---

# Final Response Required From the Implementing Agent

Return a concise implementation report containing:

1. files/classes renamed;
2. API routes renamed;
3. Agent protocol command renamed;
4. UI text changes;
5. tests/build commands run and their results;
6. exhaustive-search results for old vocabulary;
7. any intentional remaining matches and why;
8. explicit statement that no behavioral changes were made;
9. the manual verification checklist above, ready for the human tester.

Do not proceed into worktree or Feature implementation after completing this task.

**Stop after the vocabulary cleanup is complete and validated.**
