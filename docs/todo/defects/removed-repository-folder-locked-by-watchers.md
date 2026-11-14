# Removed repository folder cannot be deleted while the Worker still watches it

Status: not started. Written 2026-11-14. The cause below is read from the code; the symptom has not been reproduced with a handle trace yet.

## Symptom

A repository is removed from a Workspace (or a Source repository is chosen as the Workspace repository, which leaves its earlier folder on disk and unmanaged). GrayMoon leaves the folder on disk for the user to delete. Deleting it in Explorer or a terminal can fail with "folder is in use" for some minutes, because the Worker still holds file watchers on it.

## Cause

- `GitRepositoryWatcherManager` keeps one `GitRepositoryWatcher` per repository path: a recursive `FileSystemWatcher` on the working tree and a second one on the git directory. Both keep a directory handle open.
- The Worker has no signal for "this repository left the Workspace". The class comment says so: an entry leaves only when its deadline passes (`SweepExpired`, reason `IdleExpired`), when an exclusive removal claim releases it (`OnEntryReleased`, reason `ExplicitRemoval`), or at shutdown.
- The exclusive claim (`IRepositoryAccess.AcquireExclusiveAsync`) is only taken where GrayMoon deletes a folder itself: `GitWorktreeService` and `FeatureFolderCleaner`. Unlinking a repository deletes nothing, so nothing claims the folder.
- After the unlink the App's `GitChangesWatcherKeeper` no longer renews the path, so the watcher lives until the idle grace runs out: `GitChangesOptions.WatcherIdleGraceMinutes` (default 10) plus up to 30 s for the sweeper (`SweepInterval`). Any request that still names the path in that time (`Acquire` or `Touch`) pushes the deadline out again.
- When the Workspace has a Workspace repository, its own watcher is recursive from the Workspace root, so it also covers every Source folder beneath it, removed or not. Whether that watcher blocks deleting a subfolder has to be checked; `RegisterAncestorObserver` exists to pause a watcher while a folder beneath it is removed, which suggests it does.

## Code involved

- Worker: `Services/GitChanges/GitRepositoryWatcherManager.cs`, `GitRepositoryWatcher.cs`, `GitStatusRefreshCoordinator.cs`, `GitChangesSnapshotCache.cs`, `GitChangesRepositoryRegistry.cs`, `Services/RepositoryAccess.cs`.
- App: the unlink paths (`WorkspaceRepository` update that drops links, `WorkspaceRepositoryOperations.RemoveLinkAsync`, `ReplaceMovedSourceLinkAsync` for a Source that became the Workspace repository), `GitChangesWatcherKeeper`, `GitChangesWorkerClient`.

## Fix plan

1. Confirm first: remove a repository, try to delete its folder at once, and record which process holds the handle (Resource Monitor or `handle.exe`). Repeat for a Workspace with a Workspace repository to see whether the root watcher alone blocks the delete.
2. New Worker command `ReleaseGitChangesWatchers` (paths of one Workspace context): removes the watcher entries for those paths at once, disposes them outside the manager lock, and prunes the coordinator, snapshot cache and registry the same way an expired entry is pruned. It deletes nothing. Paths are validated like every other Worker command, and a path that is not tracked is a success.
3. The App sends it after the links are removed, for every context of the Workspace (the special Workspace and each Feature has its own checkout path). The unlink never fails because of it: a failed or timed-out release is logged and reported as a warning ("the folder may stay locked for up to 10 minutes"), and the idle expiry stays as the fallback.
4. Nothing may re-create the watcher afterwards: check that no page, keeper cycle or queued scan still names the removed path after the unlink (an in-flight `GetGitChangeStatus` could `Acquire` it again). If one can, the release must win, for example by a short "recently released" guard in the manager.
5. If step 1 shows the Workspace root watcher blocks the delete, decide separately: the root watcher cannot simply be dropped. Options are to pause it for the removed folder as `RegisterAncestorObserver` does during a removal, or to accept and document the limit.
6. Tests (Worker): an entry released by the command is gone and its caches are pruned; releasing an untracked path succeeds; a `Touch` right after a release creates a fresh entry only when the App asks again. Tests (App): unlinking sends the release for every context; a failed release does not fail the unlink and the seeded links are still removed.
7. Document in `docs/architecture/04-runtime-communication-and-concurrency.md` (watcher lifetime) and `05-user-capability-reference.md` (removing a repository), then delete this note.

## Not in scope

GrayMoon still does not delete the folder itself, and it does not close handles held by other programs (an IDE, a terminal, CodeGraph).