# BUG - Git Changes Displays Directories as Files

## Classification

**BUG**

## User observation

In the Git Changes tree for the Workspace repository, directories are sometimes displayed as if they were files.

Observed examples include entries such as:

- `GrayMoon.Release`
- `GrayMoon.wiki`

These entries exist on disk as directories, but the Git Changes UI presents them with file-style rows/icons/actions.

The underlying workspace contains a mixture of:

- normal files
- normal directories
- nested AI/configuration directories
- regular child Git repositories
- the Workspace Git repository itself

This makes correct path classification especially important.

## Why this is a bug

The Git Changes view is expected to represent Git paths as a tree, where intermediate directory nodes are structural UI nodes and leaf nodes are files or Git changes.

Showing a directory as a file breaks that model and can mislead the user about:

- what Git actually considers changed
- whether the item can be opened as a file
- whether file actions are valid
- whether the item is a repository folder, directory, or changed file
- whether the Workspace repository ignore rules are working correctly

The issue is particularly visible with the new Workspace-as-Git-repository feature because the workspace root now contains both tracked Workspace files and ignored child repository directories.

## Likely cause area

The bug should be investigated in the Git Changes tree-building/model code rather than treated as a visual/icon-only problem.

A probable failure mode is that a path entry is being materialized as a leaf change row without verifying whether the path resolves to a directory or whether it should exist only as a parent node in the generated tree.

Possible contributing factors:

1. A Git status/change path is being treated as a file path unconditionally.
2. Directory nodes are being generated from path segments, but later overwritten/replaced by a leaf node with the same name.
3. A repository-directory path is being surfaced because ignore handling returns a path-like status that the UI interprets as a file.
4. Workspace-root Git handling introduced paths that were not exercised by the original repository-only Git Changes implementation.
5. The tree model does not carry an explicit node kind and infers file/folder from children only, which fails for empty or unusual nodes.
6. A path ending at a child repository directory is being represented as a change even though the actual changed content is underneath or ignored.

## Required investigation

Trace one concrete example end-to-end, such as `GrayMoon.Release`:

1. Capture raw Git status output / LibGit2Sharp status entry for the Workspace repository.
2. Identify the exact path string delivered to the App.
3. Identify where the Git Changes tree node is created.
4. Determine whether the node is intended to be:
   - a real changed file,
   - a directory container,
   - an ignored child repository,
   - or an invalid/spurious change entry.
5. Verify whether the filesystem says the path is a directory.
6. Verify whether the Workspace repository `.gitignore` should exclude it.
7. Verify whether the UI model has enough information to distinguish directories from files.

## Desired behavior

The tree should always present:

- directories as folder/container nodes
- files as file/change nodes
- repository directories according to the intended Workspace-repository rules
- ignored child repositories as absent from Workspace Git Changes unless there is a deliberate Workspace-level reason to show them

No file action should be offered for a directory node.

## Recommended fix direction

Prefer fixing node semantics at the model/tree-building layer.

### Recommended node model

Each node should have an explicit kind, for example:

```csharp
enum GitChangeTreeNodeKind
{
    Directory,
    File
}
```

Do not infer node type solely from:

- whether it has children
- file extension
- UI rendering state

When constructing the tree:

1. Create intermediate path segments as `Directory`.
2. Create status leaf entries as `File` only when the Git change refers to a real file-like path.
3. If a path already exists as a directory node, do not silently replace it with a file node.
4. If Git reports a path that resolves to a directory, investigate why rather than papering over it in rendering.

## Important interaction with Workspace-as-Git-repository

This fix must be tested with a workspace root similar to:

```text
WorkspaceRoot/
  .claude/
  .codegraph/
  .gitignore
  .graymoon.json
  CLAUDE.md
  GrayMoon/
  GrayMoon.Desktop/
  GrayMoon.Release/
  GrayMoon.wiki/
```

where:

- WorkspaceRoot itself is a Git repository
- child repositories are intentionally ignored by the Workspace repository
- tracked Workspace files coexist with ignored directories

The fix must not accidentally reintroduce child repositories into Workspace Git Changes.

## Tests

Add regression coverage for at least:

### Tree-building unit tests

- nested file creates parent directory nodes
- parent directory node cannot be replaced by a file node
- directory with same name as a repository is rendered as directory
- ignored child repository directory does not appear as a changed file
- tracked file beside ignored repository directories is shown correctly
- nested tracked file under `.claude/` produces correct directory hierarchy

### Integration test

Create a temporary Workspace Git repository containing:

- a tracked root file
- a tracked nested file
- an ignored child Git repository directory

Run the Git Changes discovery path and assert:

- tracked files appear
- folder hierarchy is correct
- ignored repository directory does not appear as a file

## Acceptance criteria

- No real directory is rendered with file semantics in Git Changes.
- Child repository folders ignored by the Workspace repository are not shown as changed files.
- Existing Git Changes file actions remain unchanged for real files.
- Workspace and normal repository Git Changes both continue to work.
- Regression tests cover the Workspace-as-Git-repository layout.
