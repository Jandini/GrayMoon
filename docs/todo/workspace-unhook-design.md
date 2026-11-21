# Workspace unhooking and the Add / Import / Restore split - design

Status: proposal, nothing built. Written 2026-11-21 against branch `unhook-workspace`.
Implementation steps: [workspace-unhook-implementation-plan](workspace-unhook-implementation-plan.md).

This is the first part of a larger change to how a Workspace enters and leaves GrayMoon:

- **Part 1 - unhooking.** When a Workspace is removed, and whenever a repository stops being managed by a Workspace, GrayMoon removes the Git hooks it installed there.
- **Part 2 - entry points.** "Add Workspace" only adds a new Workspace. Adopting a folder that is already on disk becomes its own path, "Import from disk". The header gets one split button: **Add Workspace** with a chevron menu holding **Restore from repository** and **Import from disk**.

Part 1 comes first because Part 2 depends on it: an import adopts folders that can carry hooks of an earlier Workspace, and a failed import has to take back the hooks it wrote.

## 1. How hooking works today

### 1.1 Install

One method writes hooks: `GitService.WriteSyncHooksAsync` (`src/GrayMoon.Worker/Services/GitService.cs:1329`). Three Worker commands call it:

| Caller | When |
|---|---|
| `SyncRepositoryCommand.cs:276` | Tail of every Sync, when the checkout is on a branch or a tag. Not reached when the fetch fails. |
| `AttachWorkspaceRepositoryCommand.FinishAsync` (`:226`) | Last step of every successful attach of the Workspace repository (clone, init, or adopting an existing folder). |
| `CreateGitWorktreeCommand.cs:28` | Before `git worktree add` for a Feature, on the main checkout. |

What it does:

1. Resolves the hooks directory with `git rev-parse --git-common-dir --git-path hooks` (`ResolveGitHooksLocationAsync`), cached per repository by `GitHooksLocationCache`.
2. Writes only inside the common Git directory. When `core.hooksPath` points outside it (husky, lefthook), nothing is written and a warning is logged.
3. Writes five files: `post-commit`, `post-checkout`, `post-merge`, `pre-push`, `reference-transaction`. Each starts with `#!/bin/sh`, then the marker line `# Created by GrayMoon.Agent at <utc>`, then a script that posts `{"repositoryId":N,"workspaceId":N,"repositoryPath":"<git rev-parse --show-toplevel>"}` to `http://127.0.0.1:<port>/hook/<kind>` with `curl -s --connect-timeout 1 --max-time 2 ... || true`.
4. A hook file that is already there and is not GrayMoon's is renamed to `<name>.replaced-by-graymoon` (or `<name>.replaced-by-graymoon-<yyyyMMddHHmmss>` when that name is taken) and GrayMoon's file takes its place (`InstallSyncHook`, `MoveForeignHookAside`).
5. A GrayMoon hook with the same body is left alone; a different body (other ids, other port) is rewritten.

The two ids are baked into the file text. The path is not: the script resolves the working tree at run time, which is why one set of hooks in the common Git directory serves the main checkout, every Feature worktree and every worktree the user made by hand.

### 1.2 What a hook does at run time

`HookListenerHostedService` accepts the POST, `HookRepositoryPathPolicy` accepts the path when it lies under a workspace root the App has named, or when the repository's `hooks` folder carries a GrayMoon hook with the same two ids. A `NotifySyncJob` goes to the main pool. The hook command (`CommitHookSyncCommand`, `CheckoutHookSyncCommand`, ...) asks `IWorkspaceCapabilityProvider` for the Workspace profile, probes the repository (GitVersion, project scan, commit counts; a checkout also fetches) and sends `SyncCommand` to the App. There `WorkspaceHookContextAttributor` maps ids and path to a context, or returns null and the snapshot is dropped.

### 1.3 There is no removal

Nothing in the Worker deletes a hook, and nothing in the App asks for it. Every path that ends GrayMoon's management of a folder is database-only:

| # | Path | Code | What stays on disk |
|---|---|---|---|
| U1 | Remove Workspace | `Workspaces.razor` `DeleteWorkspaceAsync` -> `WorkspaceRepository.DeleteAsync` (`:171`) | Hooks of every repository of the Workspace, including the Workspace repository. |
| U2 | Source repository removed from a Workspace | `WorkspaceRepository.ReplaceRepositoriesCoreAsync` (`:572`), reached through `UpdateAsync` from `Workspaces.razor:858`, `WorkspaceSettingsHost.razor:314`, `WorkspaceRepositories.RepositoriesModal.cs:76` | Hooks of each removed repository. |
| U3 | Workspace repository disabled | `WorkspaceRepositoryOperations.DisableWorkspaceRepositoryAsync` (`:96`) | Hooks in the Workspace root's `.git`. |
| U4 | Source repository becomes the Workspace repository | `LinkAndAttachAsync` -> `ReplaceMovedSourceLinkAsync` (`:768`) | Hooks in the old Source folder, which "is no longer managed by GrayMoon" (`MovedFromSourceMessage`). |
| U5 | Restore rolled back | `RollbackRestoreAsync` (`:559`) | Hooks written by the attach, when `DiscardWorkspaceRoot` leaves the folder in place. |
| U6 | Link undone after an attach whose answer was lost | `UndoLinkAsync` / `RemoveLinkAsync` (`:748`, `:845`) | Hooks, only if the Worker finished the attach and the App did not hear it. A failed attach writes none: `FinishAsync` is the last step before success. |

Not unlink paths: Remove Feature (the hooks belong to the main checkout and stay correct), removing a connector (linked repositories keep their links), and the catalog clean-up in `RepositoryRepository` (`:401`, only repositories no Workspace links).

The Remove Workspace dialog says "Repositories on disk will stay untouched". The hooks GrayMoon wrote are not mentioned.

### 1.4 What a hook left behind costs

Read from the code, not measured:

1. **Every Git command in that folder keeps calling GrayMoon.** Commit, checkout, merge, push, and every branch created or deleted run `curl`. With the Worker stopped that is up to 1 s per hook (`--connect-timeout 1`).
2. **The Worker does the full work for a Workspace that no longer exists.** The path is still accepted (it is under a known root, or the hook itself vouches for it). `WorkspaceCapabilityProvider.GetAsync` asks `GET /workspaces/{id}/capabilities`, gets 404 (`WorkspaceEndpoints.cs:43`), treats that like any other failure and returns `LegacyFullEnrichment`: GitVersion and a project scan run, a checkout hook also fetches. The result is sent to the App and dropped by the attributor ("link not found"). This repeats on every Git command, with a warning in both logs each time.
3. **The user's own hook stays switched off.** A hook GrayMoon renamed to `.replaced-by-graymoon` is never renamed back.
4. **The hook is a standing credential for the listener.** `HookRepositoryPathPolicy.CarriesSyncHook` accepts any path whose repository holds a marked hook with matching ids. A folder GrayMoon no longer manages keeps that pass.

### 1.5 Other findings from the review of the install code

| # | Finding | Handled in |
|---|---|---|
| R1 | No removal exists (1.3). | Part 1 |
| R2 | Foreign hooks are renamed aside and never restored. | Part 1 |
| R3 | A 404 for a removed Workspace is treated as "could not ask" and answered with full enrichment (1.4 item 2). | Part 1 |
| R4 | The marker text and the five hook names are written twice, in `GitService` and in `HookRepositoryPathPolicy`, kept equal only by a test. | Part 1 (one owner) |
| R5 | Hook code (about 300 lines: script text, install, location, cache use) lives in the 1600-line `GitService`. Adding removal there makes it worse. | Part 1 (own class) |
| R6 | `WriteSyncHooksAsync` catches every failure and logs it; the Sync response carries no hook outcome, so the user never learns that live updates are off for a repository. This is the "swallowed best-effort step" pattern from the 2026-11-10 review. | Named here, own follow-up (section 9) |
| R7 | Install writes only to the directory Git resolves today. Hooks written to `<common>/hooks` before the user set `core.hooksPath` stay there unused. Removal has to look in both places. | Part 1 |

## 2. Goals

1. After Remove Workspace, no GrayMoon hook is left in any repository of that Workspace.
2. The same holds for every other path in 1.3: a folder GrayMoon stops managing carries no GrayMoon hook.
3. A hook the user had before GrayMoon is put back.
4. One code path removes hooks. Every unlink path goes through it. No path deletes link rows without it.
5. Removal is exact and reported: the operation either removed the hooks (or found none) for every repository, or it says which file stopped it. Nothing is skipped silently.
6. Add, Import and Restore are three separate paths with one job each.

Not goals:

- Deleting repositories, folders, branches or worktrees. Remove Workspace still leaves everything else on disk.
- Removing the other things GrayMoon wrote: `graymoon.json`, the managed `.gitignore` section, `codegraph.json`, the `graymoon-divergence-base` file in a Git directory, `core.longpaths`, global `safe.directory` entries. They do nothing once GrayMoon is gone, and an import reuses them.
- Closing handles that other programs hold on a folder (an IDE, a terminal, CodeGraph). The Worker lets go of its own file watchers (4.10); it never touches programs it did not start.

## 3. Rules the design follows

- **Ownership by folder.** A Workspace name is unique, and the Workspace folder is `<root>/<name>`. So a folder belongs to at most one Workspace, and every GrayMoon-marked hook in the hook directories of a repository in that folder belongs to that Workspace. Removal does not compare ids; the marker decides.
- **Only GrayMoon's files.** A file is removed only when its first non-blank line after the shebang starts with the marker (`IsGrayMoonHook`, the test install already uses). Anything else is left exactly as it is.
- **Only inside the common Git directory.** Install never writes outside it; removal never looks outside it.
- **Unhook before unlink.** The link rows are the only record of which folders were managed. Hooks go first, rows second. If the hooks cannot be removed, the rows stay and the user can retry.
- **Strict, not best effort.** A missing folder, a folder that is not a repository, and a repository without GrayMoon hooks are all a success ("nothing to remove"). A hook file that exists and cannot be removed is a failure that stops the unlink and names the file.
- **Idempotent.** Running removal twice gives the same disk state and a success.

## 4. Part 1 design - unhooking

### 4.1 Worker: one owner for sync hooks

New `IGitSyncHooks` / `GitSyncHooks` (`src/GrayMoon.Worker/Services/GitSyncHooks.cs`), taking everything hook-related out of `GitService`:

```
GitSyncHooks
  constants   Marker, ReplacedSuffix, HookNames (the five)         <- single definition
  IsGrayMoonHook(content)                                           <- used by install, removal, HookRepositoryPathPolicy
  InstallAsync(repoPath, workspaceId, repositoryId, ct)             <- today's WriteSyncHooksAsync, moved unchanged
  RemoveAsync(repoPath, ct) -> SyncHookRemovalResult                <- new
  location    ResolveLocationAsync + GitHooksLocationCache          <- moved
```

`IGitService.WriteSyncHooksAsync` is deleted; its three callers take `IGitSyncHooks`. `HookRepositoryPathPolicy` reads `Marker` and `HookNames` from `GitSyncHooks` instead of its own copies. The marker text does not change: it is in users' hook files.

Install and removal for one common Git directory never interleave: `GitSyncHooks` holds a keyed lock per common directory around both.

### 4.2 Worker: the removal algorithm

`RemoveAsync(repoPath)` for one repository:

1. `repoPath` does not exist -> `NotFound`. No `.git` (directory or file) -> `NotARepository`. Both are successes: there is nothing of GrayMoon's to remove.
2. Ask Git, uncached: `git rev-parse --git-common-dir --git-path hooks`. A non-zero exit or an unreadable answer -> `Failed` with Git's message. No guessing of `.git/hooks`.
3. Candidate directories: `<common>/hooks`, plus the resolved hooks directory when it is inside the common directory and differs (R7). Deduplicated. A directory outside the common directory is never a candidate.
4. In each candidate, for each of the five names:
   - no file -> nothing;
   - a file that is not a GrayMoon hook -> left alone, listed in `foreignHooks`;
   - a GrayMoon hook -> deleted, listed in `removedHooks`. Then the replaced file for that name, if there is one, is renamed back to the hook name and listed in `restoredHooks` (4.3).
5. Verify: read the five names again in each candidate. If any is still a GrayMoon hook -> `Failed`.
6. Drop the repository's entry from `GitHooksLocationCache`.
7. Outcome: `Removed` when at least one file was deleted, else `NothingToRemove`.

An `IOException` or `UnauthorizedAccessException` on any file -> `Failed` for that repository, with the file path. Files already handled stay handled; the retry finishes the rest.

The file work runs under a shared `IRepositoryAccess` lease like every other Worker file operation, and the Git call goes through `GitProcessRunner` with `ArgumentList`.

Feature worktrees need no step of their own: they execute the hooks of the main checkout's common directory, so removing them there silences every worktree. This is intended: with the Workspace gone, its Features are no longer tracked either.

### 4.3 Putting a replaced hook back

Install renames a foreign `post-commit` to `post-commit.replaced-by-graymoon`, or to `post-commit.replaced-by-graymoon-<yyyyMMddHHmmss>` when the first name is taken. After removal deletes GrayMoon's `post-commit`:

- no replaced file -> nothing;
- one replaced file -> renamed to `post-commit`;
- several (the user restored their hook by hand and GrayMoon replaced it again) -> the one with the latest timestamp in its name is renamed back, the plain `.replaced-by-graymoon` counting as oldest. The others are left and listed in `leftoverReplacedHooks`, which the App shows to the user.

`File.Move` keeps the Unix mode, so a restored hook is executable as before.

### 4.4 Worker: command `DetachRepositories`

One command for a set of repositories of one Workspace, so Remove Workspace is one round trip.

Request (`DetachRepositoriesRequest : WorkspaceCommandRequest`):

| Field | Meaning |
|---|---|
| `workspaceRoot`, `workspaceName` | The Workspace folder, as in every other command. |
| `workspaceRepositoryName` | Name of the Workspace repository, or null. A repository with this name resolves to the Workspace root. |
| `workspaceId` | For the log, and for `workspaceRemoved`. |
| `repositories[]` | `{ repositoryId, repositoryName }`. Paths are resolved by the Worker with `WorkerRepositoryPaths.Resolve`; the App sends names, never absolute paths. |
| `workspaceRemoved` | True when the Workspace itself is going: the Worker also drops its capability cache entry (`IWorkspaceCapabilityProvider.Invalidate`). |

Response: `success` (true only when no repository is `Failed`) and `results[]` with `repositoryId`, `repositoryName`, `outcome` (`Removed` / `NothingToRemove` / `NotFound` / `NotARepository` / `Failed`), `removedHooks`, `restoredHooks`, `foreignHooks`, `leftoverReplacedHooks`, `errorMessage`.

Main pool (it writes). Repositories are processed in parallel with the usual bound. One repository failing does not stop the others, so one retry after fixing the cause finishes the job.

It ships with the matching App, as every Worker command does; no capability flag.

### 4.5 App: one seam

New internal service `WorkspaceRepositoryDetacher` (`src/GrayMoon.App/Services/Workspaces/`):

```
DetachAsync(workspaceId, links | all, workspaceRemoved, ct) -> SyncHookRemovalSummary
```

It reads the links and repository names, resolves the special Workspace context's Worker arguments (`IWorkspaceContextPathResolver.GetWorkerArgsAsync`), sends one `DetachRepositories`, and maps the answer. It deletes nothing in the database. It is the only App code that sends this command.

Every path in 1.3 calls it **before** its rows go. The same call makes the Worker let go of its file watchers on those folders (4.10): "these repositories leave this Workspace" is the one signal for both.

### 4.6 App: Remove Workspace (U1)

New facade method `IWorkspaceRepositoryOperations.RemoveWorkspaceAsync(workspaceId, progress, ct) -> OperationResult`. The page stops calling `WorkspaceRepository.DeleteAsync` (rule: pages call facades).

```
1  Worker available and version-matched?      no -> fail, nothing changed
2  TryStartStructural(workspaceId, "remove-workspace")
                                              refused -> fail "a Workspace operation is already running"
3  Workspace still exists?                    no -> success (already removed)
3a Workspace has Features (or one being removed)?
                                              yes -> fail "Remove the Features first", nothing changed (D2a)
4  WorkspaceRepositoryDetacher.DetachAsync(all links, workspaceRemoved: true)
                                              any Failed -> fail, list repository + file, nothing deleted
5  Delete the rows (today's DeleteAsync logic, on a factory context)
6  Result: success, with notes (hooks put back; replaced files left over)
```

Why the lock: between step 4 and step 5 nothing may reinstall hooks. Hooks are written only by Sync, the attach, and Create Feature, and all three take the Workspace's structural or context lock. Hook-driven jobs already running in the Worker read; they never write hooks.

Why no id check is needed: the Workspace name stays taken until step 5, so no other Workspace can adopt the folder while step 4 runs.

If step 5 fails after step 4 succeeded, the Workspace is still there with no hooks. Its next Sync writes them again. No data is lost and the user is told the removal failed.

Dialog changes (`Workspaces.razor`):

- Text: "Only the GrayMoon workspace is removed. Repositories and folders on disk stay. GrayMoon removes the Git hooks it installed in them."
- Worker not available: an error callout and a disabled Remove button, the same way the Restore dialog does it.
- Failure: the dialog stays open with the error and the list of repositories and files; Remove can be pressed again.
- Success with notes: a toast per kind of note (for example "Your own hooks were put back in 2 repositories").

**Messages.** A failed removal must tell the user three things: that nothing was removed from GrayMoon, which folder stopped it and why, and what to do. The text is built in one place (`DetachFailureText`, App) from the Worker's per-repository result, so the Remove dialog, the Repositories dialog and the settings dialog say the same thing.

Headline, always:

> The workspace was not removed. GrayMoon could not finish cleaning up 1 of 12 repositories. Fix the problem below and press Remove again.

One entry per failed repository, with the repository name, the full path, and one of these reasons:

| Cause | Text |
|---|---|
| Hook file cannot be deleted | "GrayMoon.Core: the Git hook file `C:\...\.git\hooks\post-commit` could not be deleted (access denied). Close programs that use this repository, or check the file's permissions." |
| Your own hook cannot be put back | "GrayMoon.Core: your own hook `post-commit.replaced-by-graymoon` could not be renamed back to `post-commit` (the file is in use). GrayMoon's hook is already gone; rename the file yourself or close the program that has it open." |
| The Worker cannot let go of the folder | "GrayMoon.Core: the Worker is still working in `C:\...\GrayMoon.Core` and could not stop in time. Wait a moment and press Remove again." |
| Git could not answer | "GrayMoon.Core: Git could not read this repository (`<first line of git's message>`). If the folder is no longer a repository you want to keep, delete or repair it." |

Under the list, when some repositories were already cleaned: "The other 11 repositories are already cleaned up. They get their hooks back on the next Sync if you keep the workspace."

Before anything is tried:

| Situation | Text |
|---|---|
| Worker not connected | "The GrayMoon Worker is not running. Start it to remove this workspace: it has to remove the Git hooks from the repositories." |
| Worker version mismatch | "Update the GrayMoon Worker before removing this workspace." with the **Update Worker** button the Restore dialog has. |
| Another operation holds the Workspace | "Another operation is running in this workspace (<its name>). Wait for it to finish and press Remove again." |

After a success, only when there is something to tell: "Your own Git hooks were put back in 2 repositories: A, B." and "Older replaced hook files were left in 1 repository: A (`post-commit.replaced-by-graymoon-20260114093000`)."

The same headline pattern is used for the other paths, with the object changed ("The repository was not removed from the workspace.", "The Workspace repository was not disabled.").

### 4.7 App: the other unlink paths

| Path | Change |
|---|---|
| U2 membership | New facade method `SetSourceRepositoriesAsync` (and the combined rename + membership save used by settings). It computes the links that will be removed with the same rule `ReplaceRepositoriesCoreAsync` uses (extracted so there is one copy), and when that set is not empty: Worker check, structural lock, `DetachAsync(those links)`, then `WorkspaceRepository.UpdateAsync`. A failed removal stops the save. The three page call sites move to the facade. A save that removes nothing needs no Worker and no lock, as today. |
| U3 disable | `DisableWorkspaceRepositoryAsync` takes the structural lock and calls `DetachAsync` for the Workspace-role link before its transaction. The Source repositories under the root have their own `.git` and are not touched. |
| U4 Source -> Workspace repository | After the attach succeeded and before `ReplaceMovedSourceLinkAsync`: `DetachRepositories` for the old Source folder. The request passes `workspaceRepositoryName: null` for this call so the name resolves to `<root>/<name>`, not to the root (which now holds the new checkout with fresh hooks). A failure here does not undo the move (the attach is done); it is returned as a warning that names the file, next to the existing "folder was left on disk" note. |
| U5 restore rollback | `RollbackRestoreAsync` calls `DetachAsync` first, then `DiscardWorkspaceRoot`, then deletes the rows. Rollback never throws; a failed removal is added to the residue text it already returns. |
| U6 lost attach answer | `UndoLinkAsync` / `RemoveLinkAsync` call `DetachAsync` for the link before deleting it. Normally the answer is `NothingToRemove`. |

A convention test fails when code deletes `WorkspaceRepositories` or `Workspaces` rows outside the classes that go through the seam (4.9).

### 4.8 Worker: a removed Workspace is not "unknown" (R3)

`WorkspaceCapabilityProvider` separates two answers it merges today:

- the App could not be asked (no URL, network error, 401, 5xx) -> full enrichment for this one operation, as now;
- the App answered 404 -> the Workspace does not exist. `GetAsync` reports that, and `HookSyncDispatcher` drops the job with one Information line. No GitVersion, no scan, no fetch, no `SyncCommand`.

This is needed apart from removal at unlink time, because hooks orphaned before this change shipped are already on users' disks and GrayMoon has no record of where.

### 4.9 Hooks orphaned before this change

Workspaces removed with today's code left their hooks. The database no longer knows those folders, so removal at unlink time cannot reach them. Two things limit what they cost:

1. An import of such a folder (Part 2) ends with a Sync, which rewrites the hooks with the new ids.
2. 4.8 stops the wasted work the moment such a hook fires.
Nothing else is done for them (decision D4, 2026-11-21). The Worker does not delete hooks on its own: a file is only ever removed as part of an operation the user started. An orphaned hook therefore stays on disk and keeps calling the Worker; with 4.8 each call costs one lookup and one log line. A user who wants them gone imports the folder and removes the Workspace again, or deletes the five files by hand.

A repository that was removed from a Workspace which still exists (U2 before this change) is not caught by a Workspace-level 404: its jobs still run and the App drops the snapshot, as today. That is accepted.

### 4.10 Worker: file watchers let go of the folders (added 2026-11-21)

**Today.** For the Changes page the Worker keeps one `GitRepositoryWatcher` per repository path (`GitRepositoryWatcherManager`): a recursive `FileSystemWatcher` on the working tree and one on the Git directory. Both hold an open directory handle. `GitChangesRepositoryRegistry` records which Workspace and repository each path belongs to. An entry leaves only when its deadline passes (10 minutes idle by default, plus up to 30 s for the sweeper), when an exclusive removal claim covers its folder (`IRepositoryAccess.AcquireExclusiveAsync`, used by Remove Feature), or at shutdown. The manager's own comment says the Worker "has no direct signal for this repository/workspace was deleted". Two things follow:

- The Worker cannot be asked what it is watching. The App only sees two counters in a log line.
- After Remove Workspace the watchers stay for about 10 minutes. The folders cannot be deleted or moved in that time, and the App's `GitChangesWatcherKeeper` can renew them as long as a tab still counts the Workspace as active.

**The interface.** `IGitChangesMonitoring`, implemented by `GitRepositoryWatcherManager` with the registry:

```
GetMonitored(workspaceId?) -> MonitoredRepository[]   { path, workspaceId, repositoryId, healthy, leaseCount, expiresAt }
```

It is the one answer to "which Workspaces is the Worker monitoring, and which folders". It includes Feature worktree paths, because they are registered under the same Workspace id. A read-only command `GetGitChangesMonitoring` (read pool) exposes it to the App.

**Release is part of `DetachRepositories`, not a second round trip.** A check from the App followed by a separate release would leave a gap in which a queued status scan or a keeper renewal creates the watcher again. So the Worker does both in one command, in this order:

```
1  Paths = the requested repository folders
           + when workspaceRemoved: every path GetMonitored(workspaceId) returns (this adds the Feature worktrees)
2  Exclusive claim on those paths (IRepositoryAccess.AcquireExclusiveAsync)
     - releases every watcher on or under them and prunes the status tracker, snapshot cache and registry
     - cancels the Worker's own readers there at once, gives its writers the grace period, then stops them
     - while the claim is held, Acquire and Touch answer UnderRemoval: nothing can start watching again
3  Remove the hooks (4.2), inside the claim
4  Check: GetMonitored returns no entry on or under those paths; any left -> Failed
5  Lift the claim
```

The claim is the mechanism Remove Feature already uses to make the Worker let go of a folder; nothing new is invented for it. It deletes nothing. A claim that cannot clear the Worker's own holders throws `RepositoryAccessException`, which is a `Failed` result for the command and, by D2, stops the removal.

The response gains `releasedWatchers[]` (the paths that were being watched), so the App can log and show what was let go.

**The App side, so nothing asks for the watchers again.** `WorkspaceRepositoryDetacher`:

1. Pauses monitoring for every context of the Workspace through `IWorkspaceGitChangesMonitoringPause`, the switch Remove Feature uses. The keeper skips paused contexts.
2. Sends `DetachRepositories`.
3. On success the caller deletes the rows; the contexts no longer exist, so there is nothing left to renew. On failure the pause is lifted and the next keeper tick puts the watchers back, because the Workspace is still there.

For the paths where the Workspace stays (U2-U4) only the repositories that leave are paused and claimed; the rest keep their watchers.

**Still to confirm (plan, Phase 0).** When a Workspace has a Workspace repository, its watcher is recursive from the Workspace root and so also sees every Source folder under it. For U2 (a Source is removed, the Workspace stays) that root watcher stays alive. Whether it blocks deleting the removed subfolder has to be measured; this is the repro the watcher defect note asks for. It does not affect Remove Workspace, where the root watcher is released too.

This section replaces the fix plan in `defects/removed-repository-folder-locked-by-watchers.md`. That note is deleted when this ships.

## 5. Part 2 design - Add, Import, Restore

### 5.1 Today

`ShowCreateModalAsync` / `SaveWorkspaceAsync` in `Workspaces.razor` do two jobs in one dialog:

1. While the name is typed, the page asks the Worker whether `<root>/<name>` exists and what is in it (`GetWorkspaceExists`, `GetWorkspaceRepositories`), shows a warning callout ("The workspace directory exists with 3 repositories."), and pre-selects the Workspace repository when the folder is one (`ApplyDetectedWorkspaceRepositoryAsync`).
2. Save creates the Workspace with the profile from the dialog, attaches the Workspace repository (adopting the folder), and then opens a second dialog, `WorkspaceImportModal`: "Found N repositories ... Do you want to import these?". Yes links them (`AddRepositoriesAsync`, `ReferenceImportGuard`, version files).

Problems:

- The Workspace already exists when the import question is asked. "No" leaves a Workspace over a full folder with nothing linked.
- The profile comes from the dialog even when the folder holds a `graymoon.json` with its own profile, read only flags and repository list. Restore reads all of that; this path reads only the version files.
- `ConfirmImportAsync` catches every exception, logs it and closes the dialog. `TrySuggestImportsAsync` logs at Debug. The user gets no outcome.
- No rollback: a failure after the row was created leaves a half-made Workspace.
- About 250 lines of page state (`existingRepositoryCount`, `folderIsWorkspaceRepository`, `detectedWorkspaceRepositoryId`, `adoptedExistingWorkspaceRepository`, `importRepositoryIds`, ...) serve the import side of a dialog named "Add".

### 5.2 The header

```
[ search ................ ]   [ Add Workspace | v ]
                                              +------------------------+
                                              | Restore from repository |
                                              | Import from disk        |
                                              +------------------------+
```

A Bootstrap split button: `btn-group`, primary button **Add Workspace**, and a `dropdown-toggle dropdown-toggle-split` chevron that opens the menu. The app already draws this in `WorkspaceActions.razor:155` and `MergePullRequestModal.razor:310`. The `.dropdown-toggle-split` rules are scoped in `WorkspaceActions.razor.css:35`; a second user means they move to a global stylesheet once (CSS rule: one home per rule), they are not copied. Labels do not wrap (`text-nowrap`). The menu closes on Escape, on an outside click and after a choice.

Dialog titles: **Add Workspace**, **Import Workspace**, **Restore Workspace**.

### 5.3 Add Workspace - only adds

- The folder `<root>/<name>` must not exist, or must be empty. The check is `WorkspaceService.GetDirectoryStateAsync`, the one Restore uses.
- A folder with content: an error callout, "This folder already exists and is not empty. Use Import from disk to add it as a Workspace.", and Save stays disabled.
- Choosing a Workspace repository stays: it is cloned into the new folder, with the setup branch, as today.
- Removed from the page: the existing-folder detection, the auto-selection, `WorkspaceImportModal` and its three handlers, `ExistingWorkspaceFolder.Callout`. `ExistingWorkspaceFolder.MatchRepositoryId` moves to the import code.
- `WorkspaceModal` loses `DirectoryAlreadyExists`, `ExistingRepositoryCount`, `FolderIsWorkspaceRepository`, `WorkspaceRepositoryImported`.

The adopt branch of `AttachWorkspaceRepository` (the folder already is that repository) stays in the Worker: Import uses it, and so does choosing a Workspace repository in settings.

### 5.4 Import from disk - a path of its own

Shape copied from Restore, which already solves the same problem cleanly: read-only preflight, preview, one operation with rollback, a result panel, then a Sync.

**Contract** (`src/GrayMoon.Application/Workspaces/IWorkspaceImportOperations.cs`, implemented in `src/GrayMoon.App/Services/Application/`):

```
ListImportableFoldersAsync(ct)                    -> folders under the workspace root that are not a Workspace yet
PreflightImportAsync(folderName, ct)              -> ImportWorkspacePreflight   (reads only)
ImportFromDiskAsync(folderName, options, progress, ct) -> ImportWorkspaceResult
```

**Dialog** (`ImportWorkspaceModal`, flow logic in a testable `ImportWorkspaceFlow` like `RestoreWorkspaceFlow`):

1. Folder: a picker of the folders under the workspace root that no Workspace uses. The Workspace name is the folder name; it is shown, not typed, because the Workspace path is `<root>/<name>`.
2. Preview from the preflight (below).
3. Profile controls, only when the folder has no definition.
4. Import -> background job with the overlay -> result panel -> Open Workspace.

**Preflight.** One new read-only Worker command, `InspectWorkspaceFolder`, returns: whether the root is a Git repository and its origin URL; each child repository with its origin URL; the text of `graymoon.json` when present; and for each repository whether GrayMoon hooks are present. The App matches origin URLs to the catalog (`RepositoryUrlIdentity`) and, when there is a definition, evaluates it with `RestoreDefinitionEvaluator`, the same code Restore uses.

Three folder shapes:

| Shape | What the import does |
|---|---|
| A. Workspace repository with a valid `graymoon.json` | The definition decides: profile, Source repositories, read only flags, version files. No profile controls. Sources of the definition that are not on disk are linked and cloned by the Sync, as in a restore. Repositories on disk that the definition does not list are shown and left unmanaged. Missing connectors and repositories are listed, as in Restore. |
| B. Plain folder with repositories, root is not a Git repository | The user picks the profile. Every child repository whose origin matches the catalog is linked as a Source. Unmatched ones are listed with "import them through Connectors". |
| C. Root is a Git repository without `graymoon.json` (or with an invalid one) | Treated as B for the children. The root repository is not linked; the preview says so and points to settings, where it can be chosen as the Workspace repository. An invalid `graymoon.json` is reported with the parser's error and never overwritten. |

A folder with no Git repository at all cannot be imported; the preflight says so.

Shape A in detail (the folder is a Workspace with `graymoon.json`):

- The definition is read from the file on disk as it is now, on whatever branch is checked out, committed or not. Restore reads the remote branch instead; Import trusts the folder.
- The root repository must already be in GrayMoon's catalog (matched by its origin URL). If it is not, the preflight stops: "The Workspace repository `<origin>` is not imported into GrayMoon. Import it through Connectors, then import this folder." The dialog has the same **Open Connectors** button as Restore.
- A newer schema, an unknown profile or invalid JSON stops the preflight with the evaluator's message, as in Restore. The file is never changed by a failed preflight.
- Repositories already on disk are linked as they are: no checkout, no reset, no pull. A recorded tag or commit in the definition is applied only to a repository the Sync has to clone, which is today's rule for a pin.
- A Source the definition marks read only that holds commits existing only on this machine is kept writable and reported (`ReferenceImportGuard`).
- `graymoon.json` is left byte for byte unless the definition is complete on this machine and its canonical form differs (Restore's rule). The `.gitignore` section is brought up to date.

**Operation.**

```
1  Preflight again (the folder may have changed)             fail -> nothing changed
2  Create the Workspace row                                   name taken -> fail
3  Shape A: attach the Workspace repository (adopt, no clone)
4  Apply profile and read only default
5  Link Source repositories; ReferenceImportGuard keeps a repository with local-only commits writable
6  Merge version files from the definition (shape A)
7  Shape A: .gitignore section; rewrite graymoon.json only under Restore's rule (complete here and canonical form differs)
   Shape B/C: nothing is written to the folder
8  Sync (installs hooks with the new ids, replacing any stale GrayMoon hooks)
```

A failure in steps 3-7 rolls back: `WorkspaceRepositoryDetacher.DetachAsync` for what was linked (the attach wrote hooks), then the rows. **Nothing on disk is deleted**; the folder was there before the import and stays as it was. A failed or cancelled Sync does not undo the import; it is reported in the result panel with a retry hint, as in Restore.

### 5.5 Restore from repository

Unchanged, apart from where it is opened from. Its folder rule stays: the folder must not exist or must be empty.

After Part 2 the three paths do not overlap:

| | Folder before | Definition comes from | Clones |
|---|---|---|---|
| Add | none or empty | the dialog | the Workspace repository, if chosen; Sources at Sync |
| Import | exists with repositories | the folder (`graymoon.json`) or the dialog | only Sources that are missing on disk |
| Restore | none or empty | the remote repository | everything |

## 6. Decisions to confirm

| # | Decision | Recommendation | Why |
|---|---|---|---|
| D0 | Which paths unhook. | **Decided 2026-11-21: all six (U1-U6).** No link row is deleted without unhooking first. Import from disk hooks the repositories again through its Sync. | The hooks left by any of the six behave the same; one rule is easier to hold than a list of exceptions. |
| D1 | Remove Workspace needs a connected, version-matched Worker. | **Decided 2026-11-21: yes.** | The hooks are on the Worker's disk. Add, Restore and Enable already need the Worker. The alternative, a queue of pending removals replayed later, is a second path with its own failure states. |
| D1a | Watcher check and release happen inside the one `DetachRepositories` command (4.10), not as two round trips. | **Decided 2026-11-21: yes.** | A separate check can be stale by the time the release runs. |
| D2 | A folder that cannot be detached (hook file or the Worker's own handles) stops the removal; nothing is deleted in the database. The message names the repository, the file and what to do (4.6, "Messages"). | **Decided 2026-11-21: yes.** | The rows are the only record of the folders. A missing folder or repository is not a failure, so the block only happens on a real file error, which the message names. |
| D2a | Remove Workspace is refused while the Workspace has Features (including one whose removal is still running). The acknowledgement checkbox is removed. Message: "This workspace has 3 Features: a, b, c. Remove the Features first, then remove the workspace." | **Decided 2026-11-21: yes.** | Same rule as rename, root change and membership. No worktree is left that Git lists and GrayMoon does not know. Consequence: 4.6 gains a Features check as its first step after the lock, the Feature-loss text and `acknowledgeFeatureLoss` state leave `Workspaces.razor`, and the Feature self-reference clean-up in the row delete is no longer reached. In 4.10 the Workspace's monitored paths are then only its repository folders. |
| D3 | The user's replaced hook is renamed back. | **Decided 2026-11-21: yes; with several replaced files the newest is restored and the others are left and named in the success message.** | It undoes what install did. With several replaced files the newest is restored and the rest are reported. |
| D4 | The Worker reaps hooks whose Workspace the App answers 404 for. | **Decided 2026-11-21: no.** Orphans from before this change stay; the Worker only drops their jobs (4.8). | No file is deleted without a user action; no extra risk for folders shared between two GrayMoon databases. |
| D5 | Import shape C does not link the root repository. | **Decided 2026-11-21: yes.** | A repository without a definition is not known to be a Workspace repository; guessing would write `graymoon.json` and `.gitignore` into someone's repository. |
| D6 | Membership changes that remove a repository need the Worker and the structural lock. | **Decided 2026-11-21: yes** (follows from D0 and D1). | Same reason as D1. Saves that only add or rename are unaffected. |

## 7. Edge cases

| Case | Result |
|---|---|
| Repository folder deleted by the user before Remove Workspace | `NotFound`, success. |
| `core.hooksPath` points outside the Git directory | Nothing is written there by install, nothing is looked at by removal. `<common>/hooks` is still cleaned (R7). |
| Hook file is read only or locked | `Failed` with the path; the Workspace stays; retry after fixing. |
| User edited a GrayMoon hook but kept the marker line | It is a GrayMoon hook and is deleted. The marker is the contract. |
| User removed the marker line from a GrayMoon hook | It is theirs now and is left; listed in `foreignHooks`. |
| Workspace with Features | Removing the hooks in the main checkouts silences all worktrees. Feature folders stay on disk, as today. |
| Worker restarts between removal and row delete | Removal already happened on disk; the retry answers `NothingToRemove` and the rows go. |
| Two tabs press Remove | The second is refused by the structural lock, or finds the Workspace gone and succeeds. |
| Sync running when Remove is pressed | The structural lock refuses the removal until the Sync ends. |
| Docker App and Desktop App used against the same folder at different times | Ids of the other database are dead here. 4.8 drops their jobs; the hook files are left alone. |

## 8. Tests the design requires

Worker (`GitScenarios`, `TestDirectory.Delete`):

- install then remove leaves no GrayMoon hook; a second remove is `NothingToRemove`;
- a foreign hook installed before GrayMoon is back under its name with its content and mode;
- several replaced files: newest restored, others reported;
- a foreign hook that GrayMoon never replaced is untouched;
- `core.hooksPath` inside and outside the Git directory; hooks in `<common>/hooks` from before the setting are removed;
- a linked worktree path removes the hooks of the common directory;
- missing folder, non-repository folder;
- a hook file that cannot be deleted -> `Failed`, other repositories of the same request still processed;
- `HookRepositoryPathPolicy` rejects a path whose hooks were removed and is not under a known root;
- 404 from the capabilities endpoint drops the hook job and runs no probe; a network error still enriches.

App (`CreateSchemaAsync`, seeded rows that a failure could destroy):

- Remove Workspace sends `DetachRepositories` for every link, including the Workspace-role link, before any row is deleted;
- a `Failed` repository: no row deleted, seeded links, Features and context rows all still there;
- Worker unavailable: nothing sent, nothing deleted;
- lock refused: nothing sent, nothing deleted;
- each of U2-U6 sends the command for exactly the links that go;
- U4 targets the Source folder, not the root;
- convention test: no `Workspaces` / `WorkspaceRepositories` delete outside the seam's callers.

Part 2: `ImportWorkspaceFlow` tests mirroring `RestoreWorkspaceFlowTests`; operation tests for shapes A, B, C; rollback leaves the folder and removes hooks; Add refuses a non-empty folder.

## 9. Follow-ups named here, not built here

- **R6**: report the hook install outcome in the Sync response and show it on the repository row ("live updates off: <reason>").

## 10. Documents to update when this ships

- `docs/architecture/01-product-model-and-user-workflows.md`: section 19 (hooks are removed, when), the Workspace life cycle (Add / Import / Restore).
- `docs/architecture/02-system-architecture.md`: Worker services (`GitSyncHooks`), hook listener policy, the two new commands.
- `docs/architecture/04-runtime-communication-and-concurrency.md`: section 7 (removal, 404 handling), the structural lock list, the command pool table.
- `docs/architecture/05-user-capability-reference.md`: Remove Workspace, removing a repository, Import from disk, the header menu.
- `docs/architecture/06-developer-extension-guide.md`: "a new unlink path goes through `WorkspaceRepositoryDetacher`".
- `GrayMoon/AGENTS.md` "Destructive and merge operations": the unhook-before-unlink rule.
- `GrayMoon/CLAUDE.md`: Worker to App sync paragraph, command name list.
- Wiki: `Restore-Workspace.md`, `Getting-Started.md` (the Add Workspace screenshots show the old dialog).
- `GrayMoon.Desktop/README.md` "Recent GrayMoon changes": one bullet per shipped part.
- This note and the plan are deleted, and `docs/todo/README.md` is updated, in the same change.
