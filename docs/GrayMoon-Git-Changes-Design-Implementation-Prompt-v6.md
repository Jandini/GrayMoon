# GrayMoon Git Changes Feature - Design and Implementation Prompt

## Important context

The following document contains **directions and architectural recommendations**, not rigid implementation requirements. Before changing code, inspect the existing GrayMoon solution, conventions, services, Agent command infrastructure, SignalR flow, UI components, styling, repository models, and operation coordination. Reuse and extend existing patterns where they are suitable.

Do not introduce parallel abstractions when GrayMoon already has an appropriate implementation. Where the current architecture conflicts with a recommendation below, explain the conflict and implement the cleanest solution consistent with GrayMoon's established design.

The implementation must be production-quality, maintainable, lightweight, secure, and designed for workspaces that may contain hundreds or thousands of repositories.

---

# Role

Act as a senior UX architect, senior Blazor engineer, senior .NET 10 engineer, and Git integration specialist working on GrayMoon.

Design and implement a new workspace page named **Git Changes**. It should provide a polished multi-repository Git changes experience inspired by Visual Studio's Git Changes window while fitting GrayMoon's existing visual language and architecture.

This is both:

1. A front-end UX feature.
2. A backend and GrayMoon Agent feature.

Do not implement a mock-only UI. The final solution must use real repository state and real Git operations through the GrayMoon Agent.

---

# Primary objectives

Implement a Git Changes page that allows a user to:

- View changed files across repositories in the current workspace.
- Distinguish staged and unstaged changes.
- Filter changed files quickly.
- Stage or unstage a file, folder, section, or repository.
- Inspect staged and unstaged diffs.
- Commit changes for one repository.
- Receive lightweight near-real-time updates when repository files change.
- Continue to work correctly when files are modified externally by Visual Studio, VS Code, Cursor, command-line Git, build tools, or GrayMoon itself.
- Remain responsive with large workspaces and repositories containing many changed files.

The browser must not access the filesystem or execute Git directly.

The GrayMoon Agent must remain the authority for:

- Repository status.
- Change detection.
- Reading Git/index/worktree content.
- Diff source retrieval.
- Stage and unstage operations.
- Commit execution.
- Repository operation state.

---

# Navigation

Add a new workspace navigation item:

```text
Workspace
  Repositories
  Git Changes
  Projects
```

Place **Git Changes** directly under **Workspace Repositories**.

Follow existing GrayMoon routing, workspace context, authorization, navigation highlighting, icons, and component conventions.

---

# Page layout

Create a professional two-panel page with a horizontally resizable splitter.

Recommended initial proportions:

- Changes panel: 35-40%.
- Diff panel: 60-65%.

Persist the splitter position using the existing GrayMoon client preference mechanism where available. Otherwise use a small isolated browser-storage abstraction.

The page should resemble:

```text
+-----------------------------------+--------------------------------------+
| Git Changes                       | Diff                                 |
| repository summary                | selected repository and path         |
| commit message                    | diff toolbar                         |
| commit action                     |                                      |
| filter                            | Monaco Diff Editor                   |
| repository/change tree            |                                      |
+-----------------------------------+--------------------------------------+
```

Use responsive behaviour:

- At normal desktop widths, use side-by-side panels.
- At narrower widths, allow the diff to become an overlay, lower panel, or dedicated detail view according to existing GrayMoon responsive patterns.
- The Git Changes experience is primarily desktop-oriented.

---

# Visual design

The page must look native to GrayMoon rather than like an unstyled third-party Git client.

Use:

- Existing GrayMoon spacing and typography.
- Existing toolbar, button, input, badge, tree, menu, tooltip, empty-state, error-state, loading, and splitter components where suitable.
- Compact developer-tool density without becoming crowded.
- Clear hierarchy between repository, staged section, unstaged section, folder, and file.
- Icons plus text/status letters so colour is never the only indicator.
- Subtle borders and surfaces rather than excessive cards.

Avoid:

- Large empty cards.
- Excessive gradients.
- Bright colours for normal states.
- A spinner that blocks the whole page.
- Rendering every repository or every tree node eagerly.

---

# Monaco Diff Editor requirement

Use the **Monaco Diff Editor** for text diffs.

Monaco is the editor technology used by Visual Studio Code and supports a dedicated diff editor. Do not write a custom diff renderer unless a specific technical constraint makes Monaco impossible.

## Required theme

**For the initial implementation, use Monaco's built-in `vs-dark` theme without modification.**

The goal of the first release is correctness, stability, and seamless Monaco integration rather than custom editor styling.

Requirements:

- Use the built-in `vs-dark` theme.
- Do not define a custom Monaco theme yet.
- Ensure the Diff Editor consistently initializes using `vs-dark`.
- Ensure the theme survives component recreation and page navigation.
- Keep the theme selection encapsulated in the Monaco wrapper rather than scattering it throughout the UI.

Future enhancement:

Once the feature is complete and stable, introduce a GrayMoon-specific theme (for example `graymoon-dark`) derived from `vs-dark` to better match the rest of the application. The theme implementation should be isolated so changing it requires modifying only the Monaco wrapper rather than the page itself.

Use:

```javascript
monaco.editor.setTheme("vs-dark");
```

for the first release.

## Monaco wrapper

Create a focused Blazor-to-JavaScript wrapper rather than placing Monaco calls throughout Razor code.

Suggested structure:

```text
Components/GitChanges/GitDiffViewer.razor
Components/GitChanges/GitDiffViewer.razor.cs
Components/GitChanges/GitDiffViewer.razor.js
```

Expose a small API similar to:

```csharp
Task SetDiffAsync(GitDiffDocument document);
Task SetViewModeAsync(GitDiffViewMode mode);
Task SetOptionsAsync(GitDiffViewerOptions options);
Task GoToNextChangeAsync();
Task GoToPreviousChangeAsync();
Task ClearAsync();
ValueTask DisposeAsync();
```

Keep one Monaco diff editor instance alive for the selected file and replace its models when selection changes. Do not create an editor instance per file.

Dispose Monaco models and JavaScript resources correctly when replacing files or leaving the page.

Configure the editor as read-only for the first version.

Recommended options include:

- Side-by-side diff by default.
- Inline mode option.
- Automatic layout.
- Synchronized scrolling.
- Read-only original and modified models.
- Render side-by-side according to page width.
- Collapse or hide unchanged regions where supported and appropriate.
- Previous/next difference navigation.
- Configurable line wrapping.
- Optional whitespace-ignore mode.
- No editing commands.
- No unnecessary minimap by default if it creates visual noise.
- Accessible keyboard focus.

Detect the Monaco language from file extension. At minimum support:

- C#
- Razor
- JSON
- XML
- YAML
- JavaScript
- TypeScript
- CSS
- HTML
- SQL
- PowerShell
- Markdown
- Plain text fallback

---

# Changes panel

## Header

Show:

- Page title.
- Compact workspace Git status summary.
- Refresh action.
- Agent/repository state where relevant.

Preferred compact examples:

```text
26 repositories • 148 staged • 37 changed • main
```

```text
26 repositories • 148 staged • 37 changed • multiple
```

Rules:

- Show the common branch name when all affected repositories share one branch.
- Show `multiple` when affected repositories are on different branches.
- Omit the word `branches` so the summary fits within the panel width.
- Show branch distribution in a tooltip or compact details popover when the value is `multiple`.
- Keep the summary on one line where practical.
- Allow graceful truncation or responsive wrapping only when necessary.
- Treat this as a workspace change summary, not a single-repository selector.

Do not block the whole page while one repository refreshes. Repositories must update independently.

## Repository scope

Support:

- All repositories with changes.
- A selected repository.

The page may initially show all changed repositories, but commit execution must target one repository because a Git commit cannot span repositories.

Show each repository's current branch as read-only context.

Do not add branch switching to this first implementation.

## Commit message

Place a commit message input at the top of the changes panel.

Requirements:

- Multiline text area.
- Appropriate maximum length guard without unnecessarily limiting legitimate commit messages.
- Preserve draft text while the user changes file selection within the same repository.
- Prefer a separate draft per repository.
- Support `Ctrl+Enter` for the currently selected commit action.
- Disable commit while the Agent is disconnected, repository is busy, operation is running, repository has conflicts that prevent the operation, or there are no applicable changes.

Primary button behaviour:

- If staged changes exist: **Commit Staged**.
- If no staged changes exist but unstaged changes exist: **Commit All**.
- If no changes exist: disabled.

Recommended dropdown actions:

- Commit Staged.
- Commit All.

Defer Commit and Push and Amend unless GrayMoon already has clean supporting abstractions.

## Multi-repository commit behaviour

The commit message applies to all staged files across all repositories currently represented under **Staged**.

A Git commit remains repository-specific internally, so GrayMoon must create one commit per staged repository using the same commit message.

Example:

```text
Repository 1 -> one Git commit
Repository 2 -> one Git commit
Repository 3 -> one Git commit
```

The primary action should communicate scope clearly:

```text
Commit Staged in 18 Repositories
```

When only one repository is staged:

```text
Commit Staged
```

When nothing is staged but changed repositories exist:

```text
Commit All in 26 Repositories
```

`Commit All` means:

1. Stage all changed files in all applicable repositories.
2. Commit each repository independently using the same commit message.

Show a compact scope preview near the action:

```text
148 files staged across 18 repositories
```

Use a tooltip such as:

```text
Creates one commit in each staged repository using the same message.
```

Multi-repository commit is not atomic.

Required behaviour:

- Execute repository commits independently through the bounded mutation scheduler.
- Preserve per-repository mutation serialization.
- Stream each repository result as it completes.
- Continue processing other repositories when one repository fails.
- Do not automatically roll back successful commits.
- Keep failed repositories under **Staged** after refresh.
- Remove successful repositories from **Staged** when they no longer contain staged changes.
- Present a result summary such as:

```text
Committed in 16 repositories
Failed in 2 repositories
```

Provide expandable per-repository results and actionable failure messages.

---

# Filter

Add a compact filter input immediately above the tree.

Filter against:

- Repository name.
- Folder name.
- File name.
- Relative path.

The first version may use local filtering over the current change snapshot because only changed entries are transferred, not every tracked file.

Debounce filter input around 100-150 ms.

Preserve matching ancestors so a matching file remains visible in its repository/folder hierarchy.

Optionally design the filter parser so future tokens can be added:

```text
repo:GrayMoon
status:modified
status:added
status:deleted
status:conflict
staged:true
ext:cs
```

Do not run a Git status command for each filter keystroke.

---

# Change tree

Use **workspace-level change sections first**, followed by repositories beneath each section.

Recommended hierarchy:

```text
Staged
  Repository 1
    Folder
      File
  Repository 2
    File

Changed
  Repository 1
    Folder
      File
  Repository 3
    File
```

A repository appears under **Staged** only when it contains at least one staged entry.

A repository appears under **Changed** only when it contains at least one unstaged or untracked entry.

If one file in a repository is staged, create the repository branch under **Staged** and include only the staged paths there. The same repository may also appear independently under **Changed** when it still contains unstaged changes.

Do not use this hierarchy:

```text
Repository
  Staged
  Changed
```

The workspace-level section-first structure is preferred because it:

- Makes staged versus unstaged commit scope immediately visible.
- Lets the user stage or unstage across one or many repositories from a consistent section.
- Avoids repeatedly opening every repository to determine what is staged.
- Mirrors the mental model of preparing a workspace-wide set of changes while preserving repository boundaries.
- Allows one repository to be represented in both sections without mixing its index and worktree states.

Use the labels:

- **Staged**
- **Changed**

Tooltips or secondary labels may explain:

- Staged = Git index
- Changed = working-tree and untracked changes

Each file row should include:

- Expand affordance where applicable.
- File/folder/repository icon.
- Name or relative path.
- Change status.
- Right-aligned stage or unstage action.

Status indicators:

- `M` modified.
- `A` added.
- `D` deleted.
- `R` renamed.
- `U` unmerged/conflict.
- `?` untracked.

Use accessible tooltips and labels.

## Stage and unstage actions

Support:

- Stage file.
- Unstage file.
- Stage folder descendants.
- Unstage folder descendants.
- Stage an entire repository from its node under **Changed**.
- Unstage an entire repository from its node under **Staged**.
- Stage multiple selected repositories.
- Unstage multiple selected repositories.
- Stage all items in the **Changed** section.
- Unstage all items in the **Staged** section.

Repository-level actions operate on the entire repository scope, not only currently expanded or filtered descendants.

When staging a complete repository:

- Stage all tracked modifications, deletions, and untracked files according to the selected operation semantics.
- Remove entries from that repository's **Changed** branch when no unstaged changes remain.
- Create or update that repository's branch under **Staged**.

When unstaging a complete repository:

- Remove its entries from **Staged** when no staged changes remain.
- Recreate or update the repository under **Changed** where working-tree changes still exist.

For multi-repository stage or unstage operations:

- Send an explicit collection of repository IDs and operation scopes.
- Execute safely through the bounded cross-repository scheduler.
- Preserve per-repository mutation serialization.
- Stream individual repository results and failures.
- Do not treat the operation as one atomic Git transaction because repositories are independent.

Right-align `+` and `-` actions consistently.

Do not let visual filtering silently change the meaning of a repository or folder action.

For example, when the filter shows only `.cs` files:

- `Stage repository` must still stage the repository.
- A future separate action may be called `Stage filtered files`.

The Agent request must use an explicit operation scope or an explicit normalized path set. Do not infer operation scope from currently rendered DOM nodes.

## Tree performance

For large change sets:

- Build the hierarchy once per snapshot.
- Preserve expanded node state by repository and normalized path.
- Flatten only visible nodes for rendering.
- Use virtualization if the existing GrayMoon component library supports it cleanly.
- Avoid rendering collapsed descendants.
- Avoid rebuilding the entire workspace tree when one repository snapshot changes.

---

# Diff selection semantics

Selecting a file under **Changes** displays:

```text
Index -> Working tree
```

Selecting a file under **Staged Changes** displays:

```text
HEAD -> Index
```

A file may appear in both sections if it contains both staged and unstaged modifications. These are separate comparisons and must not be merged into one misleading diff.

The diff header should show:

- Repository.
- Relative path.
- Change type.
- Staged or unstaged context.
- Previous/next difference controls.
- Side-by-side/inline toggle.
- Whitespace option where supported.
- Wrap toggle where supported.
- Stage or unstage action for the selected item.

---

# Special diff states

Handle these without crashing Monaco:

## New file

- Empty original model.
- Working/index content in modified model.

## Deleted file

- Original content.
- Empty modified model.

## Renamed file

Show original and new paths and use the correct Git sources.

## Binary file

Do not attempt to render binary content as text.

Show:

- Binary file changed.
- Before and after sizes where available.
- File type.
- Optional future image comparison hook.

## Oversized text file

Do not automatically transfer extremely large file content.

Use configurable limits.

Recommended direction:

- Normal full diff up to approximately 2-5 MB per side.
- Warning state beyond the soft limit.
- Optional explicit `Load large diff` under a hard safety limit.
- Otherwise show metadata or a patch summary.

## Generated/minified content

Detect excessive line length or generated/minified content and show a safe warning or simplified representation rather than freezing the browser.

## Unsupported encoding

Detect text encoding safely. Prefer UTF-8 where appropriate, but do not corrupt files. Return an unsupported-encoding state when the content cannot be represented safely.

---

# Git implementation recommendation

Use the native Git CLI through the GrayMoon Agent as the primary implementation.

Reasons:

- Consistency with the user's installed Git.
- Correct `.gitattributes` behaviour.
- Git filters and line endings.
- Git LFS compatibility.
- Sparse checkout.
- Submodules.
- Better compatibility with newer Git features.
- Commands can be reproduced manually for diagnostics.
- GrayMoon already uses Agent-executed Git commands.

Create or extend one abstraction, for example:

```csharp
public interface IRepositoryGitChangesService
{
    Task<GitChangeSnapshot> GetStatusAsync(
        RepositoryDescriptor repository,
        CancellationToken cancellationToken);

    Task<GitFileDiff> GetDiffAsync(
        RepositoryDescriptor repository,
        GitDiffRequest request,
        CancellationToken cancellationToken);

    Task<GitMutationResult> StageAsync(
        RepositoryDescriptor repository,
        GitStageRequest request,
        CancellationToken cancellationToken);

    Task<GitMutationResult> UnstageAsync(
        RepositoryDescriptor repository,
        GitUnstageRequest request,
        CancellationToken cancellationToken);

    Task<GitCommitResult> CommitAsync(
        RepositoryDescriptor repository,
        GitCommitRequest request,
        CancellationToken cancellationToken);
}
```

Suggested implementation name:

```text
GitCliRepositoryGitChangesService
```

Do not add LibGit2Sharp solely for this page unless existing GrayMoon architecture already depends on it and there is a strong technical reason.

---

# Git status

Use machine-readable Git status:

```bash
git status --porcelain=v2 -z --branch --untracked-files=all
```

Do not parse the human-oriented `git status` output.

Parse:

- Branch information.
- Ordinary changed entries.
- Renamed/copied entries.
- Unmerged entries.
- Untracked entries.
- Index status.
- Worktree status.
- Original path where applicable.

Use NUL-delimited parsing so paths containing spaces and unusual characters are handled correctly.

Suggested model:

```csharp
public sealed record GitChangeEntry
{
    public required string Path { get; init; }
    public string? OriginalPath { get; init; }

    public GitChangeKind IndexChange { get; init; }
    public GitChangeKind WorktreeChange { get; init; }

    public bool IsTracked { get; init; }
    public bool IsConflicted { get; init; }
    public bool IsSubmodule { get; init; }
}
```

A single Git entry may have both an index change and a worktree change. The UI projection may therefore show it in both sections.

Write focused parser tests using realistic porcelain v2 fixtures, including:

- Spaces.
- Unicode.
- Rename records.
- Untracked files.
- Deleted files.
- Conflicts.
- Both staged and unstaged modification.
- Detached HEAD.
- Unborn branch.

---

# Diff content retrieval

Status loading must return metadata only. Do not transfer all changed file contents with every status refresh.

Load a diff lazily when a user selects a file.

## Unstaged comparison

Concept:

```text
Index -> Working tree
```

Retrieve:

- Original from the Git index.
- Modified from the local working-tree file.

Suggested Git source:

```bash
git show :relative/path
```

Read the working-tree side directly through the Agent after validating the path.

## Staged comparison

Concept:

```text
HEAD -> Index
```

Retrieve:

- Original from `HEAD`.
- Modified from the Git index.

Suggested sources:

```bash
git show HEAD:relative/path
git show :relative/path
```

Handle:

- New files.
- Deleted files.
- Renames.
- Unborn branches with no `HEAD`.
- Symlinks.
- Submodules.
- Binary files.
- Encoding.
- File size limits.

Git patch generation may also be used for metadata, validation, binary detection, line statistics, or a fallback representation, but Monaco requires original and modified content models for its normal diff experience.

---

# Stage operations

For explicit paths:

```bash
git add -- path1 path2
```

For repository-wide stage all:

```bash
git add --all
```

Use `ProcessStartInfo.ArgumentList` or GrayMoon's equivalent safe process abstraction.

Never build a shell command by concatenating user-controlled repository paths, file paths, or commit messages.

---

# Unstage operations

Prefer:

```bash
git restore --staged -- path1 path2
```

Support a compatibility fallback where GrayMoon's minimum Git version requires it:

```bash
git reset -- path1 path2
```

Handle repositories with no initial commit correctly.

Detect the installed Git version through existing Agent capability reporting or add a focused capability if needed.

---

# Large path collections and command-line safety

Do not append an unbounded collection of file paths directly to Git command arguments.

This is especially important on Windows, where the total process command line has a finite length and a repository may contain thousands of changed paths.

For explicit file selections, prefer Git pathspec input through standard input.

Recommended staging command:

```bash
git add --pathspec-from-file=- --pathspec-file-nul
```

Recommended unstaging command:

```bash
git restore --staged --pathspec-from-file=- --pathspec-file-nul
```

Write normalized repository-relative paths to standard input as UTF-8, separated by NUL bytes.

Conceptually:

```text
src/File1.cs\0
src/Folder/File 2.cs\0
README.md\0
```

This avoids:

- Windows command-line length limits.
- Shell quoting issues.
- Problems with spaces, quotes, Unicode, and unusual filenames.
- Arbitrary batching by file count.

Repository-wide operations must not enumerate every file.

Use:

```bash
git add --all
```

for staging an entire repository.

Use a repository-wide unstage operation appropriate for the repository state and supported Git version, for example:

```bash
git restore --staged :/
```

Handle unborn repositories separately where `HEAD` does not yet exist.

Operation contracts must preserve scope explicitly:

```csharp
public enum GitChangeOperationScope
{
    ExplicitPaths,
    Folder,
    Repository,
    MultipleRepositories,
    EntireSection
}
```

Rules:

- Stage repository -> one repository-wide Git operation.
- Unstage repository -> one repository-wide Git operation.
- Stage selected files -> one NUL-delimited pathspec input operation where supported.
- Unstage selected files -> one NUL-delimited pathspec input operation where supported.
- Stage or unstage multiple repositories -> one independent Git operation per repository through the bounded mutation scheduler.
- Do not flatten file selections from multiple repositories into one Git invocation.

## Compatibility fallback

If the installed Git version does not support `--pathspec-from-file`:

- Use safe bounded batches.
- Batch by encoded command-line character count, not only by file count.
- Keep a conservative safety margin below the operating-system process limit.
- Continue to use `ProcessStartInfo.ArgumentList`.
- Preserve `--` before paths.
- Return partial-operation diagnostics if a later batch fails.

Suggested fallback direction:

```text
Target no more than approximately 20,000 encoded command-line characters
per Git invocation on Windows.
```

Treat this value as configurable and benchmark-driven.

Do not split into multiple Git calls within one repository unless:

- Compatibility requires batching.
- An explicit internal safety limit is reached.
- Controlled progress reporting is required.
- Git reports a recoverable input-size limitation.

Prefer one stdin-based pathspec operation per repository whenever possible.

## Large path collection tests

Add tests for:

- Thousands of explicit paths.
- Paths containing spaces.
- Unicode paths.
- Quotes and unusual valid filename characters.
- NUL-delimited stdin serialization.
- Repository-wide actions avoiding path enumeration.
- Compatibility batching based on encoded command length.
- Partial batch failure reporting.
- Cross-repository path isolation.


# Commit operations

Commit on the GrayMoon Agent.

Use a temporary UTF-8 commit message file:

```bash
git commit --file=<temporary-message-file>
```

Do not invoke `git commit -m "<user text>"` through a shell string.

Recommended flow:

1. Acquire the repository operation lock.
2. Refresh or validate repository state.
3. Confirm there are changes appropriate for the requested action.
4. For Commit All, stage the intended repository changes explicitly.
5. Validate merge/rebase/conflict state.
6. Write a temporary commit message file.
7. Execute Git with a safe argument list.
8. Capture output with size limits.
9. Remove the temporary file in `finally`.
10. Refresh status.
11. Return commit SHA, summary, operation output, and the new snapshot.

Be aware that Git commit hooks may execute. Preserve normal Git behaviour, but apply timeouts, cancellation, output limits, and clear error reporting.

Do not automatically bypass hooks.

---

# Agent change detection

Use a hybrid model:

```text
FileSystemWatcher event
  -> repository may be dirty
  -> debounce/coalesce
  -> authoritative Git status refresh
  -> versioned snapshot
```

`FileSystemWatcher` must be treated only as an invalidation hint.

The source of truth is always a new Git status scan.

Do not try to reconstruct Git state from watcher events.

---

# Lightweight watcher lifecycle

Do not create permanent watchers for every repository known to GrayMoon.

Use subscription-based watcher leases.

A repository watcher should be active according to GrayMoon's background workspace monitoring policy, not only while the page is open.

Recommended triggers:

- The repository belongs to an actively monitored workspace.
- A GrayMoon operation requires repository monitoring.
- An explicit workspace or Agent background-monitoring setting enables it.

Opening the Git Changes page must not be required to start monitoring.

If GrayMoon chooses lease-based monitoring to control resource use, the lease should belong to the workspace background service rather than the browser page.

When monitoring is disabled or the workspace becomes inactive:

- Keep the watcher alive for a short idle grace period where useful.
- Dispose it after the grace period.

Use one recursive working-tree watcher per active repository where practical.

Watch relevant Git metadata either through:

- A carefully filtered root watcher that includes `.git`, or
- A second shallow watcher for selected `.git` paths.

Relevant metadata includes:

```text
.git/index
.git/HEAD
.git/refs
.git/packed-refs
.git/MERGE_HEAD
.git/CHERRY_PICK_HEAD
.git/rebase-merge
.git/rebase-apply
```

Ignore noisy Git internals where they do not affect displayed state, for example object writes and routine logs.

Do not use an excessively large watcher buffer as the primary reliability strategy.

---

# Debouncing and refresh coordination

Editors commonly emit several filesystem events for one save.

Use a per-repository refresh coordinator:

```text
event
  -> mark dirty
  -> debounce approximately 300-500 ms
  -> run one Git status scan
```

If another event occurs during a scan, schedule at most one additional scan.

Do not queue an unlimited number of status operations.

Suggested internal states:

```text
Clean
Dirty
Refreshing
RefreshingAndDirty
Disposed
```

A repository must have no more than:

- One active refresh.
- One pending follow-up refresh.

---

# Watcher overflow and recovery

Subscribe to watcher errors.

On overflow or watcher failure:

1. Mark the current snapshot as potentially stale.
2. Recreate the watcher.
3. Run a full Git status refresh.
4. Record a diagnostic metric/log entry.
5. Do not display a disruptive error if automatic recovery succeeds.

The page may briefly show:

```text
Refreshing repository state...
```

A missed watcher event must never cause permanent stale state because Git status remains authoritative.

---

# Periodic reconciliation

While the page has active subscribers, add low-frequency reconciliation to catch missed events and external Git activity.

Recommended starting points:

- Currently selected repository: every 15-30 seconds.
- Other visible active repositories: around every 60 seconds.
- No active subscribers: no periodic polling.

Stagger scans across repositories. Do not run Git status against hundreds of repositories simultaneously.

Immediately refresh after:

- Stage.
- Unstage.
- Commit.
- GrayMoon file modifications.
- Checkout or branch-changing operations elsewhere in GrayMoon.
- Merge/rebase-related operations.
- Agent reconnect.

---

# Snapshot model

Return a versioned repository snapshot.

Suggested model:

```csharp
public sealed record GitChangeSnapshot
{
    public required long Version { get; init; }
    public required long RepositoryId { get; init; }

    public required string BranchName { get; init; }
    public string? HeadCommit { get; init; }

    public bool IsDetachedHead { get; init; }
    public bool IsUnbornBranch { get; init; }
    public bool IsMerging { get; init; }
    public bool IsRebasing { get; init; }
    public bool IsCherryPicking { get; init; }

    public required IReadOnlyList<GitChangeEntry> Changes { get; init; }
    public required DateTimeOffset ScannedAt { get; init; }
}
```

Increment the version whenever a new authoritative snapshot is published.

The UI and application layer must reject or ignore older snapshots that arrive out of order.

Persist the latest lightweight Git Changes projection in the GrayMoon App SQLite database.

The Agent and Git remain authoritative, but the application database is the read model used by the front end.

The page must not issue repository status commands merely because it was opened. It should load the latest persisted state immediately, in the same architectural style as Workspace Repositories.

Persist only lightweight status and summary data. Do not persist file contents or diff contents.

---

# Agent command contracts

Follow GrayMoon's existing request/response command conventions.

Recommended capabilities:

```text
SubscribeGitChanges
UnsubscribeGitChanges
GetGitChangeStatus
RefreshGitChanges
GetGitFileDiff
StageGitChanges
UnstageGitChanges
CommitGitChanges
```

Avoid creating one command per visual button if a clean operation contract already supports explicit scopes.

Example stage request:

```csharp
public sealed record StageGitChangesRequest
{
    public required long RepositoryId { get; init; }
    public required GitChangeOperationScope Scope { get; init; }
    public IReadOnlyList<string> Paths { get; init; } = [];
    public required long ExpectedSnapshotVersion { get; init; }
}
```

Possible scopes:

```text
ExplicitPaths
Folder
Section
Repository
```

Example result:

```csharp
public sealed record GitMutationResult
{
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
    public GitChangeSnapshot? Snapshot { get; init; }
}
```

Use stable error codes and user-friendly messages.

---

# Optimistic concurrency

Repository state may change after it was rendered.

Include an expected snapshot version with mutations.

The Agent should validate relevant current state before:

- Stage.
- Unstage.
- Commit.
- Any future discard operation.

A stale version does not always need to fail. For example, staging an explicit path may remain safe if it still exists and the intended operation is unambiguous. However:

- Commits should refresh state before execution.
- Destructive operations must use strict validation.
- The returned snapshot must always reflect post-operation truth.

When state changed materially, return a result such as:

```text
RepositoryChanged
```

The UI should refresh and explain:

```text
The repository changed since it was last loaded. The change list has been refreshed.
```

---

# Repository operation coordination

Serialize mutating Git operations per repository.

Reuse or extend GrayMoon's existing repository operation coordination so Git Changes does not compete with:

- Workspace synchronization.
- Dependency updates.
- Branch checkout.
- Merge/rebase operations.
- Agent commands that edit repository files.
- Other Git commands.

Do not use a single global lock for all repositories.

Separate repositories should remain concurrent.

Recommended repository states:

```text
Available
Refreshing
Mutating
Committing
BusyByWorkspaceSync
Unavailable
```

When a GrayMoon operation is modifying a repository:

- Show the last snapshot.
- Display a concise busy state.
- Disable stage, unstage, and commit.
- Refresh automatically when the operation completes.

---



# Parallel repository processing

GrayMoon must be able to inspect and refresh Git state for multiple workspace repositories concurrently.

The design direction is to support **up to 16 repositories being processed in parallel**, subject to a configurable maximum parallelism limit.

Treat `16` as the default upper bound, not as a requirement to always run 16 operations.

Suggested configuration:

```csharp
public sealed class GitChangesOptions
{
    public int MaxParallelRepositoryOperations { get; init; } = 16;
    public int MaxParallelRepositoryMutations { get; init; } = 4;
    public int MaxParallelDiffLoads { get; init; } = 4;
}
```

Validate configuration:

```text
Minimum: 1
Default: 16 repository status operations
Maximum supported value: 16 unless explicitly changed after performance testing
```

## Concurrency model

Use bounded concurrency rather than starting one task per repository.

Recommended implementation choices:

- `System.Threading.Channels`
- `Parallel.ForEachAsync`
- A bounded work queue with `SemaphoreSlim`
- Existing GrayMoon job/command scheduling infrastructure, if it already provides bounded execution

Prefer a shared scheduler abstraction that supports:

- Configurable maximum parallelism
- Cancellation
- Backpressure
- Priority
- Deduplication
- Fair scheduling
- Per-repository serialization
- Metrics
- Graceful shutdown

Suggested abstraction:

```csharp
public interface IGitRepositoryWorkScheduler
{
    ValueTask EnqueueAsync(
        GitRepositoryWorkItem workItem,
        CancellationToken cancellationToken);
}
```

A work item should include:

```csharp
public sealed record GitRepositoryWorkItem
{
    public required long RepositoryId { get; init; }
    public required GitRepositoryWorkKind Kind { get; init; }
    public required GitRepositoryWorkPriority Priority { get; init; }
    public long? ExpectedSnapshotVersion { get; init; }
}
```

Possible work kinds:

```text
InitialStatus
WatcherRefresh
PeriodicReconciliation
ManualRefresh
DiffLoad
Stage
Unstage
Commit
PostMutationRefresh
```

## Parallelism rules

Repository operations may run concurrently across different repositories.

Operations within the same repository must remain coordinated and safe.

Required behaviour:

- Up to 16 repository status scans may run in parallel.
- Never run two status refreshes for the same repository simultaneously.
- Mutating operations must be serialized per repository.
- A commit must not overlap with stage, unstage, checkout, sync, dependency update, merge, rebase, or another mutation for the same repository.
- Status and diff reads may run concurrently only when the repository operation coordinator determines that this is safe.
- A post-mutation status refresh should run immediately after the mutation and should not be delayed behind low-priority reconciliation work.
- A manual refresh should have higher priority than background periodic reconciliation.
- The currently selected repository should have higher refresh priority than repositories that are merely visible.
- Repositories outside the current visible/subscribed workspace should not consume Git Changes worker capacity.

Do not use one global repository lock. Use:

1. A global bounded scheduler controlling total concurrency.
2. A per-repository coordinator controlling correctness for each repository.

Conceptually:

```text
Global scheduler: maximum 16 active repository jobs
    |
    +-- Repository A coordinator: one safe operation sequence
    +-- Repository B coordinator: one safe operation sequence
    +-- Repository C coordinator: one safe operation sequence
    ...
```

## Separate read and mutation limits

Status scans are generally read-heavy and may use the full configured parallelism.

Mutations should use a lower global limit because Git mutations may trigger:

- Antivirus scanning
- Filesystem writes
- Git hooks
- Build-tool reactions
- FileSystemWatcher event bursts
- Expensive index updates

Recommended defaults:

```text
Status/refresh concurrency: 16
Diff retrieval concurrency: 4
Mutation concurrency across repositories: 4
Mutation concurrency per repository: 1
```

These are directions and initial recommendations. Measure real GrayMoon Agent workloads and make the limits configurable.

## Adaptive scheduling

Do not assume the optimal worker count is always 16.

The scheduler should use:

```text
effectiveParallelism =
    min(
        configuredMaximum,
        activeRepositoryCount,
        environmentSafeLimit)
```

The environment-safe limit may initially be the configured maximum. Design the abstraction so it can later account for:

- Logical processor count
- Available memory
- Current Agent command load
- Disk saturation
- Repository location
- Network-backed filesystems
- Existing workspace synchronization activity

A reasonable initial calculation is:

```csharp
var effectiveParallelism = Math.Min(
    options.MaxParallelRepositoryOperations,
    Math.Max(1, Environment.ProcessorCount * 2));
```

However, do not reduce the configured value unnecessarily if testing shows Git status operations are primarily I/O-bound. Keep the final choice configurable and benchmark-driven.

The hard requirement is bounded execution, not a specific CPU formula.

## Work deduplication

Many watcher events may target the same repository while it is already queued or refreshing.

Coalesce duplicate status work.

For each repository, allow at most:

- One active status refresh.
- One pending follow-up refresh.

Do not enqueue one work item per filesystem event.

If a repository already has a pending refresh:

- Merge additional watcher refresh requests into the existing pending request.
- Raise its priority if a higher-priority reason arrives.
- Preserve a dirty flag so one final authoritative scan occurs.

For example:

```text
Periodic refresh queued
    + watcher event
    + manual refresh
= one queued refresh promoted to manual priority
```

## Queue priority

Recommended priority order:

1. Commit/stage/unstage completion refresh
2. User-requested mutation
3. User-requested manual refresh
4. Selected-file diff load
5. Selected repository status refresh
6. Initial visible repository status
7. Watcher-triggered refresh
8. Periodic reconciliation

Use separate queues or a priority queue where appropriate.

Prevent starvation:

- Background repositories must eventually refresh.
- Continuous activity in one repository must not block every other repository indefinitely.
- Apply fair scheduling across repository IDs.

## Initial workspace loading

When the page opens with many repositories:

1. Load cached snapshots immediately where available.
2. Identify repositories most likely to need refresh.
3. Queue status scans rather than starting all repositories simultaneously.
4. Process at most 16 concurrently.
5. Stream each completed repository snapshot to the UI independently.
6. Prioritize:
   - Selected repository
   - Repositories already known to have changes
   - Visible repositories
   - Remaining subscribed repositories

Do not wait for all repositories before displaying results.

The page should progressively populate as each repository completes.

## Cancellation

Cancel queued work when:

- The workspace subscription ends.
- A repository is removed from the workspace.
- The Agent disconnects or shuts down.
- A newer operation supersedes an obsolete queued operation.
- The user changes workspace and the result is no longer relevant.

Do not cancel an in-progress Git mutation merely because the page closes unless the operation was explicitly designed to be safely cancellable.

Status and diff reads should support cancellation.

## Error isolation

A failure in one repository must not stop processing other repositories.

Each repository job should return an isolated result:

```csharp
public sealed record GitRepositoryWorkResult
{
    public required long RepositoryId { get; init; }
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}
```

Continue processing the queue after:

- Missing repository
- Git failure
- Access denied
- Corrupt repository
- Timeout
- Watcher failure

Apply retry only for transient conditions and use bounded backoff. Do not continuously retry a permanently invalid repository.

## Timeouts

Use operation-specific timeouts.

Recommended starting directions:

```text
Git status: 30 seconds
Diff content retrieval: 30 seconds
Stage/unstage: 60 seconds
Commit: configurable, longer because hooks may execute
```

Do not use a single short timeout for all Git operations.

Timeouts should produce a repository-specific error and release scheduler capacity.

## Metrics and diagnostics

Record:

- Configured parallelism
- Effective parallelism
- Active repository jobs
- Queued jobs
- Queue wait time
- Job execution duration
- Jobs by work kind
- Deduplicated refresh requests
- Cancelled queued jobs
- Repository-specific failures
- Scheduler saturation
- Mutation wait time
- Maximum observed concurrency

Use these metrics to tune whether 16 is optimal for actual GrayMoon environments.

## Parallel processing acceptance criteria

The feature must demonstrate that:

1. No more than 16 repository status operations run concurrently by default.
2. The concurrency limit is configurable.
3. Repository results are streamed independently as they finish.
4. One slow repository does not block all other repositories.
5. One repository never runs conflicting mutations concurrently.
6. Duplicate watcher refreshes are coalesced.
7. Manual and selected-repository work receives higher priority.
8. Background work does not starve.
9. Cancellation removes obsolete queued work.
10. Failures are isolated per repository.
11. Scheduler shutdown is graceful.
12. Tests verify actual maximum observed parallelism.
13. Tests verify per-repository serialization.
14. Tests verify cross-repository concurrency.
15. Tests verify refresh deduplication and priority promotion.

## Required concurrency tests

Add tests that prove:

- 32 repositories with a maximum parallelism of 16 never exceed 16 active status operations.
- 32 repositories eventually all complete.
- Two operations for the same repository do not overlap.
- Operations for different repositories do overlap.
- Mutation concurrency respects its lower global limit.
- Repeated watcher events create at most one active and one pending refresh per repository.
- Manual refresh promotes existing queued background work.
- Cancelling a workspace subscription removes queued nonessential work.
- A failed repository does not stop remaining work.
- The selected repository is scheduled before background repositories.


# SQLite persistence and background projection

Design Git Changes as a persisted background projection, following the same architectural direction as GrayMoon Workspace Repositories.

The page must be a read-only consumer of persisted status until the user performs an explicit action such as:

- Manual refresh.
- Stage.
- Unstage.
- Commit.
- Load diff.

Normal page navigation must not enqueue Agent commands.

## Data flow

Recommended flow:

```text
GrayMoon Agent
  -> background Git status monitoring
  -> versioned repository snapshot event
  -> GrayMoon App
  -> validate snapshot version
  -> persist SQLite projection
  -> broadcast UI update
  -> Git Changes page reads persisted projection
```

Page load:

```text
User opens Git Changes
  -> query SQLite
  -> render persisted workspace summary and tree
  -> subscribe to application update events
  -> no repository status command is sent
```

Diff load:

```text
User selects file
  -> request diff from Agent
  -> return original/modified content
  -> render Monaco
  -> do not persist diff
```

## Persistence model

Prefer a normalized lightweight read model.

Suggested entities:

```text
WorkspaceGitChangesState
WorkspaceGitRepositoryState
WorkspaceGitChangeEntry
```

Possible schema direction:

```csharp
public sealed class WorkspaceGitRepositoryState
{
    public long WorkspaceId { get; set; }
    public long RepositoryId { get; set; }

    public long SnapshotVersion { get; set; }

    public string? BranchName { get; set; }
    public string? HeadCommit { get; set; }

    public bool IsDetachedHead { get; set; }
    public bool IsUnbornBranch { get; set; }
    public bool IsMerging { get; set; }
    public bool IsRebasing { get; set; }
    public bool IsCherryPicking { get; set; }

    public int StagedCount { get; set; }
    public int ChangedCount { get; set; }
    public int ConflictCount { get; set; }

    public DateTimeOffset AgentScannedAt { get; set; }
    public DateTimeOffset PersistedAt { get; set; }

    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
}
```

```csharp
public sealed class WorkspaceGitChangeEntry
{
    public long WorkspaceId { get; set; }
    public long RepositoryId { get; set; }

    public string Path { get; set; } = string.Empty;
    public string? OriginalPath { get; set; }

    public GitChangeKind IndexChange { get; set; }
    public GitChangeKind WorktreeChange { get; set; }

    public bool IsTracked { get; set; }
    public bool IsConflicted { get; set; }
    public bool IsSubmodule { get; set; }
}
```

Adapt naming and key conventions to existing GrayMoon entities.

Do not duplicate repository metadata already stored elsewhere. Reference existing repository records by ID.

## Snapshot replacement

Persist repository snapshots transactionally.

For one repository update:

1. Reject the update when `SnapshotVersion` is older than or equal to the currently persisted version.
2. Update repository summary state.
3. Replace that repository's change-entry projection.
4. Commit in one database transaction.
5. Publish a UI update only after persistence succeeds.

Do not delete and rebuild the entire workspace projection when one repository changes.

Only replace entries for the affected repository.

## Efficient persistence

Repository snapshots may contain many changed files.

Use efficient bulk-style persistence consistent with GrayMoon's SQLite and EF Core conventions.

Recommended directions:

- One transaction per repository snapshot.
- Delete old rows for the repository and insert the new snapshot rows.
- Use `ExecuteDeleteAsync` where appropriate.
- Use batched inserts.
- Avoid calling `SaveChangesAsync` once per file.
- Avoid tracking large old and new graphs simultaneously.
- Use `IDbContextFactory<AppDbContext>` or an equivalent fresh DbContext per background persistence operation.
- Never share one scoped DbContext across concurrent Agent update handlers.

This is important because up to 16 repositories may be processed concurrently.

Each persistence operation should use its own DbContext instance or pass through a bounded persistence writer.

## Persistence write concurrency

Agent status computation may run with up to 16 repositories in parallel, but SQLite has limited write concurrency.

Do not allow 16 independent SQLite writers to contend without coordination.

Recommended architecture:

```text
Up to 16 parallel Agent status computations
  -> snapshot update channel
  -> bounded application persistence queue
  -> one SQLite writer, or a very small controlled writer count
  -> persisted update event
```

For SQLite, prefer a single ordered writer unless existing GrayMoon measurements demonstrate that a different configuration is safe.

This separates:

- Parallel Git computation.
- Serialized or tightly bounded SQLite writes.

The persistence queue must support:

- Backpressure.
- Repository-level update coalescing.
- Snapshot version ordering.
- Graceful shutdown.
- Failure isolation.
- Retry for transient SQLite busy/locked errors.
- Metrics for queue depth and write duration.

If several snapshots for the same repository are queued, persist only the newest snapshot that has not yet been written.

## Database indexes

Recommended indexes:

```text
Unique: WorkspaceId, RepositoryId
WorkspaceGitChangeEntry: WorkspaceId, RepositoryId
WorkspaceGitChangeEntry: WorkspaceId, IndexChange
WorkspaceGitChangeEntry: WorkspaceId, WorktreeChange
```

Avoid over-indexing the file path text unless a measured query requires it.

Filtering can remain in memory after loading the persisted changed-file projection for the workspace, or use server-side filtering if change sets become very large.

## Page queries

The page should query persisted data through a dedicated read service.

Suggested abstraction:

```csharp
public interface IWorkspaceGitChangesReadService
{
    Task<WorkspaceGitChangesView> GetWorkspaceAsync(
        long workspaceId,
        CancellationToken cancellationToken);
}
```

The query should return:

- Compact workspace summary.
- Repository states.
- Changed entries needed to build the Staged and Changed trees.
- Last scanned and persisted timestamps.
- Agent connectivity/staleness indicators.

Do not load diff bodies.

## Staleness

Persist and expose timestamps:

- Agent scan time.
- App persistence time.
- Agent connectivity state where available.

The UI may show:

```text
Last updated 18 seconds ago
```

or:

```text
Agent offline • showing persisted state from 10:42
```

Do not automatically trigger a refresh solely because the persisted state is old.

Background monitoring should refresh it according to policy. A user may still choose Manual Refresh.

## Startup and reconciliation

On application or Agent startup:

- The existing persisted projection should remain available immediately.
- The Agent should reconcile active/configured workspaces in the background.
- New snapshots should replace stale persisted state incrementally.
- Missing repositories or removed workspace associations should be cleaned from the projection through explicit lifecycle handling.

Do not clear all persisted Git Changes state at startup.

## Explicit actions

The following user actions may invoke the Agent:

- Manual refresh.
- Stage.
- Unstage.
- Commit.
- Diff retrieval.

After stage, unstage, or commit:

1. The Agent performs the operation.
2. The Agent computes the resulting authoritative snapshot.
3. The App persists the snapshot.
4. The UI updates from the persisted result/event.

Avoid maintaining a separate optimistic front-end truth that can drift from persistence.

A short operation-in-progress state may be held in page state, but the final tree state must come from the persisted snapshot.

## Persistence cleanup

Remove persisted Git Changes rows when:

- A repository is removed from the workspace.
- A workspace is deleted.
- A repository record is deleted.
- The projection is explicitly rebuilt.

Use appropriate foreign keys and cascade behaviour consistent with GrayMoon.

## Persistence acceptance criteria

The implementation must prove that:

1. Opening Git Changes sends no Agent status command.
2. Reloading the page renders the last persisted state while the Agent is offline.
3. Agent updates are persisted before UI broadcast.
4. Older snapshot versions cannot overwrite newer persisted versions.
5. One repository update does not rebuild unrelated repositories.
6. Parallel Agent scans do not create unsafe concurrent DbContext usage.
7. SQLite writes are serialized or tightly bounded.
8. Multiple queued snapshots for one repository are coalesced to the newest version.
9. Diffs are never stored in SQLite.
10. Stage, unstage, and commit update the page through the new persisted snapshot.
11. Workspace and repository deletion cleans related projection rows.
12. Tests verify page load performs no Agent command.


# SignalR and application flow

The Agent should not broadcast all repository state globally.

Recommended flow:

```text
Background Agent monitoring
  -> Agent computes repository snapshots
  -> App persists snapshots in SQLite
  -> App broadcasts persisted repository updates to relevant sessions

User opens Git Changes
  -> App reads SQLite projection
  -> Blazor subscribes to future persisted update events
  -> no Agent status command is sent
```

On file change:

```text
watcher event
  -> debounce
  -> Git status
  -> snapshot version increment
  -> Agent sends repository update
  -> app sends update to subscribed workspace sessions
```

Send updates per repository. Do not rebuild and transmit one giant workspace snapshot every time one file changes.

Ensure all Blazor UI state updates triggered by SignalR or background callbacks use the component dispatcher, for example:

```csharp
await InvokeAsync(() =>
{
    State.ApplySnapshot(snapshot);
    StateHasChanged();
});
```

Do not repeat the existing GrayMoon dispatcher/threading problem in this feature.

---

# Caching

## Agent snapshot cache

Cache only the latest lightweight status snapshot for an active repository.

```text
RepositoryId -> GitChangeSnapshot
```

## Diff cache

Use a small bounded cache keyed by:

```text
RepositoryId
Path
Comparison type
Snapshot version
```

Use:

- LRU or size-limited memory cache.
- Byte-based limit.
- Short expiration.
- Invalidation when snapshot version changes.

Do not cache every changed file's contents.

## Application persistence

The GrayMoon App must persist the latest lightweight Git Changes state in SQLite.

This persistence acts as the front-end read model.

Required behaviour:

- The Agent computes Git status in the background.
- The Agent sends repository snapshot updates to the GrayMoon App.
- The App validates snapshot ordering and persists the newest snapshot.
- The App broadcasts the persisted update to connected UI sessions.
- When the Git Changes page opens, it reads from SQLite only.
- Opening or reloading the page must not trigger Git status commands.
- The page may show the persisted snapshot as stale when the Agent is offline or the last update is old.
- Background Agent subscriptions and reconciliation continue independently of whether the page is currently open, according to workspace monitoring policy.

Git and the Agent remain authoritative. SQLite is a durable projection, not the source of Git truth.

Do not persist:

- Original file contents.
- Modified file contents.
- Monaco models.
- Full diff payloads.
- Binary file bodies.
- Temporary Git command output.

Diffs remain lazy and on-demand.

---

# Agent disconnection and failures

## Agent disconnected

- Keep the last snapshot visible but mark it stale.
- Disable mutating actions.
- Disable new diff retrieval.
- Previously loaded diff may remain visible.
- Refresh automatically after reconnect.

## Repository missing

Show a clear repository-unavailable state.

## Git unavailable

Show that Git is unavailable on the owning Agent and include actionable diagnostics.

## Git lock

If `.git/index.lock` or an equivalent Git lock error is encountered:

- Explain that another Git operation appears to be running.
- Do not automatically delete lock files.

## Merge/rebase/cherry-pick

Detect and display repository operation state.

The first version may allow safe commits only where Git allows them and where GrayMoon's flow is unambiguous.

## Conflicts

Group conflicts above normal changes:

```text
Conflicts
Staged Changes
Changes
```

Render conflict information safely, but defer a full conflict-resolution editor unless it can be implemented cleanly.

---

# Security

All Agent-side paths and process arguments are untrusted inputs from a remote application boundary.

For every repository-relative path:

1. Reject absolute paths.
2. Normalize path separators.
3. Resolve against the known repository root.
4. Verify the resolved path remains inside the repository root.
5. Reject traversal through `..`.
6. Handle symbolic links and junctions carefully.
7. Pass Git arguments through an argument-list API.
8. Use `--` before file paths.
9. Apply file-size, process-output, and execution-time limits.
10. Never interpolate a commit message or path into a shell command.
11. Avoid exposing secrets that Git hooks, filters, credentials, remotes, or process output might emit.
12. Apply existing workspace and Agent authorization rules.

Do not allow a client to submit an arbitrary filesystem repository root. Resolve repository IDs through GrayMoon's trusted repository records.

---

# Suggested front-end structure

Adapt this to GrayMoon's existing organization:

```text
Components/Pages/
  WorkspaceGitChanges.razor
  WorkspaceGitChanges.razor.cs

Components/GitChanges/
  GitChangesHeader.razor
  GitCommitPanel.razor
  GitChangesFilter.razor
  GitChangesTree.razor
  GitChangesRepositoryNode.razor
  GitChangesFolderNode.razor
  GitChangesFileNode.razor
  GitDiffPanel.razor
  GitDiffToolbar.razor
  GitDiffViewer.razor
  GitDiffViewer.razor.js
  GitRepositoryStateBanner.razor

Services/GitChanges/
  WorkspaceGitChangesState.cs
  GitChangesClientService.cs
  GitChangesTreeBuilder.cs
  GitChangeFilterParser.cs
```

Avoid a single multi-thousand-line Razor component.

Use partial classes or focused services according to GrayMoon conventions.

---

# Suggested Agent structure

Adapt this to existing Agent service and command architecture:

```text
Services/GitChanges/
  GitChangesSubscriptionService.cs
  GitRepositoryWatcher.cs
  GitRepositoryWatcherManager.cs
  GitStatusRefreshCoordinator.cs
  GitCliRepositoryGitChangesService.cs
  GitPorcelainV2Parser.cs
  GitDiffContentReader.cs
  RepositoryOperationCoordinator.cs
```

Do not duplicate an existing repository command runner, process executor, subscription manager, or operation coordinator.

---

# Page-scoped state

Use a dedicated page state object.

Example:

```csharp
public sealed class WorkspaceGitChangesState
{
    public long WorkspaceId { get; }

    public IReadOnlyDictionary<long, RepositoryGitChangesState> Repositories { get; }

    public long? SelectedRepositoryId { get; set; }
    public GitChangeSelection? SelectedChange { get; set; }

    public string Filter { get; set; } = string.Empty;
    public bool IsConnected { get; set; }
}
```

The state should support incremental replacement of a single repository snapshot without rebuilding unrelated repositories.

Preserve:

- Selected repository.
- Selected file where still present.
- Expanded paths.
- Filter.
- Diff view mode.
- Commit drafts by repository.
- Splitter position.

---

# Testing requirements

Add meaningful tests, not only compilation fixes.

## Unit tests

Test:

- Porcelain v2 parser.
- Rename and conflict records.
- Stage/unstage request validation.
- Path normalization and traversal rejection.
- Snapshot version ordering.
- Tree construction.
- Filter behaviour.
- File extension to Monaco language mapping.
- New/deleted/renamed diff source resolution.
- Unborn branch handling.
- Watcher refresh state machine.
- Debounce/coalescing behaviour.
- Per-repository operation serialization.
- Cache size/invalidation behaviour.

## Integration tests

Where GrayMoon test infrastructure allows, create temporary Git repositories and test:

- Modified file.
- Staged file.
- File with both staged and unstaged changes.
- New untracked file.
- Deleted file.
- Rename.
- Commit.
- Commit All.
- Unstage.
- Repository with no commits.
- Conflict state.
- External file modification followed by watcher refresh.
- Watcher overflow/failure recovery path where practical.
- Agent disconnect/reconnect snapshot refresh.
- Page load from SQLite while Agent is offline.
- Page load issuing no Agent command.
- Snapshot version persistence ordering.
- Coalesced SQLite projection writes.
- Independent DbContext usage during concurrent Agent updates.

Do not make tests depend on a developer's global Git configuration. Configure temporary repository identity locally.

## UI tests

Test:

- Empty state.
- Loading repositories independently.
- Filter preserving ancestors.
- Correct staged/unstaged diff context.
- Commit button state.
- Busy repository state.
- Agent disconnected state.
- Large-file warning.
- Binary-file state.
- Keyboard navigation and `Ctrl+Enter`.
- Monaco dark graphite theme initialization.

---

# Logging and diagnostics

Add structured logs for:

- Subscription start/stop.
- Watcher creation/disposal.
- Watcher overflow.
- Status duration.
- Status entry count.
- Diff retrieval duration and byte count.
- Stage/unstage/commit duration.
- Repository busy conflicts.
- Stale snapshot requests.
- Monaco initialization failures where they can be reported.

Do not log:

- Full file contents.
- Full commit messages at normal information level.
- Credentials.
- Sensitive Git output without sanitization.

Add metrics where GrayMoon already has a metrics mechanism:

- Active repository watchers.
- Status scans per minute.
- Status scan duration.
- Watcher overflow count.
- Diff bytes transferred.
- Mutation failures.
- Snapshot subscribers.

---

# First-release scope

Implement:

- Workspace Git Changes page.
- Workspace-level **Staged** and **Changed** sections.
- Repository branches recreated independently under each applicable section.
- Multi-repository changed-file tree.
- Filtering.
- Stage file/folder/section/repository.
- Unstage file/folder/section/repository.
- Commit Staged across all staged repositories using one shared commit message.
- Commit All across all changed repositories using one shared commit message.
- Monaco Diff Editor.
- GrayMoon graphite-dark theme inherited from `vs-dark`.
- Side-by-side and inline diff modes.
- Staged and unstaged comparisons.
- Lazy diff loading.
- Binary and oversized-file states.
- Background workspace monitoring with SQLite-backed status projection.
- Debounced authoritative Git status.
- Periodic reconciliation.
- Versioned snapshots.
- Agent disconnected and repository busy states.
- Security validation.
- Tests.

Defer unless already easy within existing architecture:

- Hunk staging.
- Line staging.
- Editing within Monaco.
- Conflict-resolution editor.
- Discard changes.
- Commit and push.
- Amend.
- Multi-repository commit.
- Image diff.
- Git history.
- Branch switching.

---

# Implementation process

Work in deliberate stages.

## Stage 1 - Inspect and report

Before modifying code:

1. Inspect relevant GrayMoon projects and components.
2. Identify existing workspace navigation and routing.
3. Identify repository models and workspace-repository relations.
4. Identify Agent command request/response infrastructure.
5. Identify SignalR subscription and push-update patterns.
6. Identify Git process execution abstractions.
7. Identify repository-level operation locks/coordinators.
8. Identify existing splitters, tree controls, virtualization, toolbars, inputs, badges, and dark-theme styling.
9. Identify how JavaScript modules are loaded and disposed in Blazor.
10. Identify test projects and temporary Git repository test helpers.

Produce a concise implementation plan listing:

- Existing components/services to reuse.
- New components/services required.
- Data contracts.
- Migrations, if any.
- Risks or conflicts with these recommendations.

Do not create database migrations merely to store transient Git state.

## Stage 2 - Contracts and backend core

Implement:

- Git models.
- Status parser.
- Git service abstraction.
- CLI implementation.
- Safe path validation.
- Snapshot versioning.
- Stage/unstage/commit operations.
- Unit and Git integration tests.

## Stage 3 - Agent monitoring

Implement:

- Watcher leases.
- Debounce/coalescing coordinator.
- Overflow recovery.
- Periodic reconciliation.
- Command handlers.
- Incremental snapshot events.
- Integration with existing repository operation coordination.

## Stage 4 - Application integration

Implement:

- Workspace subscription routing.
- Authorization.
- Agent command correlation.
- Per-repository snapshot fan-out.
- Reconnect handling.
- Stale/out-of-order response protection.

## Stage 5 - Front end

Implement:

- Navigation and route.
- Page state.
- Resizable panels.
- Commit panel.
- Filter.
- Repository tree.
- Incremental updates.
- Loading, busy, offline, empty, and failure states.

## Stage 6 - Monaco integration

Implement:

- Monaco loading according to GrayMoon's asset strategy.
- Diff editor wrapper.
- `graymoon-dark` theme inherited from `vs-dark`.
- Lazy models.
- Language mapping.
- Resource disposal.
- Diff toolbar.
- Binary/large-file fallbacks.
- Theme verification.

## Stage 7 - Hardening

Verify:

- Large workspace behaviour.
- Large change set behaviour.
- Concurrent external edits.
- Stage/unstage during incoming watcher events.
- GrayMoon sync interaction.
- Agent reconnect.
- Cancellation.
- No DbContext concurrency misuse.
- No Blazor dispatcher misuse.
- No unbounded caches.
- No permanent watchers without subscribers.
- No shell command injection.
- No stale snapshot overwrites.
- Correct cleanup/disposal.

---

# Acceptance criteria

The work is complete when:

1. Git Changes appears under Workspace Repositories.
2. Opening or reloading the page reads SQLite persistence and sends no Agent status command.
2. The page displays changed repositories incrementally.
3. The tree is section-first: **Staged -> Repository -> Folder -> File** and **Changed -> Repository -> Folder -> File**.
4. A repository appears independently in either or both sections according to its current index and worktree state.
5. Staged and unstaged states are correct for files with mixed state.
4. The user can filter changed files without a server round trip per keystroke.
5. Stage and unstage operations work for files, folders, sections, and repositories.
6. Commit Staged and Commit All apply one shared message across all applicable repositories.
7. GrayMoon creates one independent Git commit per repository and clearly communicates that the operation is not atomic.
8. File diffs load lazily.
9. Monaco uses the GrayMoon dark graphite theme derived from `vs-dark`.
10. C# and other common GrayMoon file types are syntax highlighted.
11. Side-by-side diff scrolling remains synchronized.
12. Binary and oversized files do not freeze or corrupt the UI.
13. External file modifications appear after debounce without manual refresh.
14. Missed watcher events are corrected by reconciliation.
15. Watchers exist only for active subscriptions or operations.
16. Mutating Git operations are serialized per repository.
17. The page handles Agent disconnect and reconnect.
18. Old snapshots cannot overwrite newer snapshots.
19. All remote paths are validated securely.
20. Large explicit path collections use stdin-based NUL-delimited pathspec input where supported and never rely on an unbounded command line.
21. Tests cover Git parsing, operations, monitoring, state projection, pathspec handling, multi-repository commits, and critical UI behaviour.
21. The implementation follows existing GrayMoon conventions and does not introduce unnecessary parallel infrastructure.
22. The solution builds cleanly and existing tests remain passing.

---

# Final deliverables

At completion provide:

1. A summary of the implemented architecture.
2. A list of files added and modified.
3. The final Agent command and event contracts.
4. Any package or asset changes, including Monaco integration.
5. A description of the `graymoon-dark` Monaco theme.
6. Test coverage added.
7. Known limitations intentionally deferred.
8. Manual verification steps.
9. Any recommendations that were adjusted because GrayMoon's existing architecture provided a better solution.

Do not claim completion for functionality that remains mocked, disconnected, or untested.
