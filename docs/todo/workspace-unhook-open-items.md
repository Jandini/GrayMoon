# Workspace unhooking and Add / Import / Restore - open items

Status: the feature is built (2026-11-21). How it works is in `docs/architecture/` (01 section 19 and the Workspace life cycle, 02, 04 section 7 "Hook removal and detaching repositories", 05 section 1, 06 section 20G) and in `AGENTS.md`, "Destructive and merge operations", rule 6. This note keeps only what is still open.

## Built

- The Worker's `GitSyncHooks` is the single owner of the sync hooks and can remove them; `DetachRepositories` releases the file watchers on the folders and removes the hooks in one command; `GetGitChangesMonitoring` reports which folders are watched for which Workspace.
- Every path that deletes a Workspace or a Workspace link row detaches the folder first: Remove Workspace, a Source repository removed, the Workspace repository disabled, a Source that becomes the Workspace repository, a rolled-back restore or import, and a link undone after a lost attach answer.
- Remove Workspace needs the Worker, is refused while Features exist, and stops with the repository and file named when a folder cannot be cleaned.
- A hook of a Workspace the App no longer has is dropped by the Worker before any work.
- The Workspaces page has one split button: Add Workspace (new, empty folder only), with Restore from repository and Import from disk in its menu. Import from disk is its own dialog and operation.

Decisions taken on 2026-11-21, for the record: all six unlink paths detach; the Worker is required; a folder that cannot be cleaned stops the operation; the user's own replaced hook is renamed back (newest when there are several); the Worker never deletes a hook on its own, so hooks orphaned before this change stay; Import does not link a Git repository that has no `graymoon.json` as the Workspace repository.

## Open

### 1. Manual smoke

The automated tests cover the Worker commands against real repositories and the App operations against a scripted Worker. Nothing was run in a browser. Check by hand:

- The split button and its menu on the Workspaces page: look, Escape, click outside, both menu items.
- Add Workspace with the name of an existing non-empty folder: the message shows and Save is disabled; with a new name it works as before.
- Remove Workspace: the new text; with the Worker stopped (message, Remove disabled); with a Feature (message, Remove disabled); a normal removal, then `git commit` in one of the folders produces no line in the Worker log and no file in `.git/hooks` carries the marker.
- A repository that had its own `post-commit` before GrayMoon: the hook is back after the removal.
- Removing a Source in the Repositories dialog, and deleting its folder in Explorer right after.
- Import from disk for the three kinds of folder, and that the hooks carry the new Workspace id after the Sync.

### 2. Hook install outcome is not reported (review finding R6)

`GitSyncHooks.InstallAsync` catches every failure and logs it, and the Sync response carries no hook outcome, so a user never learns that live updates are off for a repository (for example when `core.hooksPath` points outside the Git directory). Report the outcome in `SyncRepositoryResponse` and show it on the repository row.

### 3. Two scoped copies of the split toggle rules

`wwwroot/css/split-button.css` is the shared home for the split button (`gm-split-toggle`, `gm-split-menu`, `gm-split-backdrop`). `WorkspaceActions.razor.css` and `WorkspaceRepositoriesHeader.razor.css` still carry their own copies of the toggle rules and of `.branch-menu-backdrop`. Move them to the global file when those components are next changed; they were left alone here because the change could not be checked on screen.

### 4. Hooks orphaned before this change

By decision they stay on disk. Each call costs one lookup and one log line on the Worker, and a hook of a repository removed from a Workspace that still exists still runs its probe before the App drops the snapshot. If that shows up as noise, the options are a manual "clean up old GrayMoon hooks" action, or a per-repository check on the Worker; neither is planned.

### 5. Wiki

`Getting-Started.md` and `Restore-Workspace.md` in the wiki describe the old Add Workspace dialog and the old "Restore from repository" button, and the Add Workspace screenshots show the old dialog. A page for Import from disk does not exist.
