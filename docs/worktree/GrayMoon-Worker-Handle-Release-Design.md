# Worker handle release: one owner for "who may touch this folder"

Status: implemented (P1-P5). Broker `RepositoryAccess`, process choke point `RepositoryAccessCommandLineService`, watcher release and ancestor pause, LibGit2Sharp openers, `RemoveWorktreeAsync` and `FeatureFolderCleaner` take the exclusive claim; the App treats `PathUnderRemoval` as expected. Covered by `RepositoryAccessTests` and the end-to-end `WorktreeRemovalReleasesHandlesTests`. Supersedes the stop-gap `RepositoryPathGate` / `RepositoryPathReleaser` (commit `b734edbe`).
Relates to `GrayMoon-Worktree-Features-Remove-Pending-Cleanup.md` (what happens when an *external* program holds a file).

---

## 1. Problem

Removing a Feature deletes worktree folders. The Worker process itself can hold those folders open at that moment, so
`git worktree remove` fails with `Invalid argument` / `Permission denied`, and the residue walk then retries each
locked file (about 6 s per file) and makes the Remove dialog appear to hang. Evidence: worker log 2026-10-09 16:33
(`failed to delete '.../GrayMoon.Desktop': Invalid argument`, request `834a8a95` never answered), and the later
`failed to delete '.../parallel-create-feature'` on the Feature root.

GrayMoon must never be the reason a folder cannot be deleted. Programs outside GrayMoon are a different case and are
already handled by the pending-cleanup design.

## 2. Inventory: what can hold a Feature folder open

| # | Holder | Lifetime | Today |
|---|---|---|---|
| H1 | `FileSystemWatcher` on each repository root and its `.git` (`GitRepositoryWatcher`), kept alive by App lease renewal plus `WatcherIdleGraceMinutes` | minutes, survives between requests | not released before Remove; recreates itself in `OnError` |
| H2 | Watcher on an **ancestor** (Workspace-role root worktree is the Feature root and contains the other worktrees, `IncludeSubdirectories = true`) | as H1 | gets `InternalBufferOverflowException` during the delete and restarts mid-delete |
| H3 | Every `git` child process: its working directory is pinned for the process lifetime. Status scans (watcher driven, App sweep, on-demand), sync, version, branch, commit counts, inspect | seconds | only status scans considered |
| H4 | LibGit2Sharp `Repository` objects (open `.git`, pack files). `LibGit2SharpGitIgnoreService.Session` keeps one open for a whole session; `GitVersionInputFingerprint` and the snapshot reader dispose per call | per call / per session | not audited |
| H5 | Worker file walks: file search, csproj discovery, the residue walk itself | per call | concurrent with Remove |
| H6 | App-side callers that trigger H3 (periodic sweep, page refresh, `GetGitChangeStatus` at `WorkspaceFeatureOperations.cs:1369`). `IWorkspaceGitChangesMonitoringPause` (D2) covers only the periodic sweep | continuous | partial |
| H7 | Programs outside GrayMoon (VS Code, a running build, an `Open in` tool) | any | out of scope here, see pending-cleanup |

The stop-gap handles H1 and the H3 status-scan slice only, and does it with a shared list that three classes must
remember to consult. New code that starts a git process or opens a repository bypasses it silently.

## 3. Principles

1. **One owner.** A single Worker component decides whether a path may be watched, scanned or opened right now.
2. **Enforce at the choke point, not per feature.** The two ways the Worker touches a repository are the git process
   runner and the LibGit2Sharp openers. Gate those, and every present and future command is covered.
3. **Lifetime-scoped leases.** Anything holding a handle holds a *lease* for exactly that lifetime. Release means
   "the lease count for that path reached zero", which is checkable, not "we asked nicely".
4. **Fail closed, fail typed.** A request that arrives while its folder is being removed gets a specific result
   (`PathUnderRemoval`), never a generic git failure, and never a silent success.
5. **Removal always wins over GrayMoon's own handles (hard requirement).** Once a Feature is being removed, the Worker
   must release *everything it holds* on that Feature's folders, without exception and without asking. Read-only work
   (status, diff, log) is cancelled at once. Mutating work on those folders (sync, commit, branch changes) gets a short
   grace period to finish, then is cancelled and its git process killed. This is safe because such work can only be
   operating on the worktree that is being destroyed; mutations of the shared main repository run with that repository as
   working directory, which is never inside a removed folder. Only processes the Worker itself started are ever terminated\n   (its own git children and their process trees); a program GrayMoon did not start is never touched, whatever it holds.\n   Remove never fails, and never hangs, because of a handle
   GrayMoon itself owns. The only remaining cause of leftovers is H7 (programs outside GrayMoon).
6. **Resumes by itself.** When the removal ends (success or failure) monitoring comes back without anyone calling it.

## 4. Design

### 4.1 `IRepositoryAccess` (Worker, singleton) - the path access broker

Reader/writer semantics over **path trees**:

```
IDisposable?  AcquireShared(path, AccessKind kind, out CancellationToken yield)   // use of a folder
Task<IDisposable> AcquireExclusiveAsync(paths, timeout, ct)                        // deletion of folders
void RegisterReleasable(path, IReleasable r)                                       // watchers
```

- **Shared** (git process, LibGit2 repository/session, file walk): granted unless an exclusive claim covers `path`
  (equal or beneath). When refused the caller gets `PathUnderRemoval`. `kind` is `ReadOnly` or `Mutating`.
  `yield` is cancelled when an exclusive claim arrives (immediately for `ReadOnly`, after the grace period for`n  `Mutating`); every holder must honour it.
- **Exclusive** (Remove, residue cleanup, marker write, Feature-folder cleanup): 1) publish the claim, so new shared
  requests are refused immediately; 2) call `Release()` on every registered releasable at or beneath the paths
  (watchers dispose their OS handles); 3) cancel `ReadOnly` holders via `yield` immediately; 4) give `Mutating`
  holders the grace period, then cancel them too and kill their processes (`yield` is honoured by every holder, not only
  read-only ones); 5) wait until the shared count for those trees is zero, bounded by a hard ceiling; 6) return the
  scope. If a holder still has not released at the ceiling (a stuck process that ignores cancellation), the Worker
  force-terminates the **process it started** and records it in the log; it never reports success while its own
  holder is alive. The exclusive call therefore either returns with zero GrayMoon-owned handles or throws, and the throw
  is a defect to fix, not a normal outcome.
- **Scopes are reference counted** per path; overlapping removals under one Feature root are independent.
- **Re-entrancy:** the remover itself runs git (`worktree remove`, `worktree list`) with `cwd` = the *main*
  repository, which is never inside the claimed paths (existing guards enforce this; the broker asserts it).
  Commands that deliberately run inside a claimed tree pass the owning scope and are granted.
- **Resume:** disposing the scope lifts the claim. Watchers are not auto-recreated by the broker; the App's next lease
  renewal (or `Overflowed`-style refresh) recreates them for folders that still exist.

### 4.2 Wiring (the choke points)

| Component | Change |
|---|---|
| `GitProcessRunner` | every run takes `AcquireShared(cwd, kind)` for the process lifetime; kind derived from the subcommand (status/diff/log/rev-parse/for-each-ref/ls-remote... read-only; the rest mutating). Links `yield` into the process cancellation so a status scan is killed, not waited for. Relates to the existing per-repo `RepoLocks`: keep it for ordering mutating git calls, do not extend it. |
| `GitRepositoryWatcher` | registers as releasable at construction; `Release()` disposes both `FileSystemWatcher`s and sets a flag so `OnError` cannot restart them. Ancestor watchers (H2) are *paused*, not disposed: events and overflow are ignored while any claim lies beneath them, and one refresh is scheduled when the claim ends. |
| `GitRepositoryWatcherManager.Acquire` | asks the broker; a covered path returns a typed refusal, not a fake lease. On `Release()` the entry is removed together with its coordinator, cache and registry entries (one method, replaces `ReleaseUnder`). |
| `GitStatusRefreshCoordinator` | no gate of its own. It runs through `GitProcessRunner`, so a refused scan is just a `PathUnderRemoval` result. Both `GetStatusAsync` calls in `RefreshNowAsync` are covered automatically (closes the line-stats gap). |
| `LibGit2SharpGitIgnoreService`, `GitVersionInputFingerprint`, snapshot reader | open the repository through the broker (`AcquireShared`) and release it on dispose. `Session` leases for its whole life, so sessions must be short; audit callers (file search, csproj discovery) so none outlives its command. |
| `GitWorktreeService.RemoveWorktreeAsync`, `FeatureFolderCleaner`, marker writer | take `AcquireExclusiveAsync` on the worktree folder and the Feature root before the first delete, hold it through residue cleanup. |
| Residue walk | keeps the total time budget (15 s), because after GrayMoon's own handles are gone only H7 remains, and that case belongs to the pending-cleanup marker flow. |

### 4.3 App side

- `PathUnderRemoval` is an expected, non-error outcome of `GetGitChangeStatus`: no warning log, no repository error
  state, no stale-snapshot badge; the previous snapshot stays until the removal finishes.
- `IWorkspaceGitChangesMonitoringPause` stays, narrowed to its real job: do not spend round trips on a context being
  removed. It is an optimisation, not the safety mechanism. The Worker is authoritative.
- No new App-to-Worker "release" command is needed: the exclusive claim happens inside the Worker commands that delete.

### 4.4 Why not the alternatives

- *Keep the gate, add checks where missing*: every new git/LibGit2 caller must remember; this is the failure mode
  already seen (line-stats call). 
- *App sends Unwatch before Remove*: the App cannot know about scans started by Worker watcher events in between, and a
  second command adds a failure mode (App crashes between Unwatch and Remove). Still fine as an optimisation later.
- *Retry the delete longer*: does not work; a watcher that re-arms keeps the folder pinned however long we wait.
- *Kill the Worker for Remove*: loses unrelated in-flight work and the hub connection.

## 5. Phases (stop and check in after each)

| Phase | Content | Done when |
|---|---|---|
| P1 | `IRepositoryAccess` + unit tests (shared/exclusive, nesting, scope counting, cancellation, timeout, withdraw, re-entrancy) | semantics proven with no Worker wiring |
| P2 | Wire `GitProcessRunner` and the watcher/manager; replace `RepositoryPathGate`, `RepositoryPathReleaser`, `ReleaseUnder`, `DrainScansUnderAsync`; ancestor-watcher pause | existing 609 Worker tests pass; real worktree + live watcher + running scan can be removed |
| P3 | `RemoveWorktreeAsync` / `FeatureFolderCleaner` take the exclusive claim; typed result `PathUnderRemoval`; forced release of mutating holders after grace, process kill at the ceiling | Remove no longer produces "failed to delete" for GrayMoon-held handles |
| P4 | LibGit2Sharp openers through the broker; audit session lifetimes (ignore service, file search, csproj discovery) | no repository object outlives its command |
| P5 | App: treat `PathUnderRemoval` as expected, narrow the monitoring pause; docs (`CLAUDE.md` Worktree Features, architecture 04) | no warning noise in app log during Remove |

## 6. Verification

- Unit: broker semantics above. Include a deterministic race test: scan starts between claim publish and drain.
- Integration (real git, temp repos): create worktree, acquire a watcher lease, start a slow `git status`, run Remove;
  assert success, folder gone, no residue, completes well under the residue budget.
- Handle probe: after the exclusive scope is granted, `Directory.Move(path, path + ".probe")` must succeed (an open
  directory handle makes it fail) and be moved back; run for every holder class H1-H5.
- Forced release: a `Mutating` holder (slow fake git process) is cancelled after the grace period and a holder that ignores cancellation has its process killed; Remove still succeeds and the folder is gone.
- Resume: after a failed Remove, watchers and scans for the surviving folders come back on the next lease renewal.
- Manual: reproduce the original case (Feature with two worktrees, app running, VS Code closed) and the H7 case
  (VS Code open): the first removes cleanly, the second falls through to the pending-deletion marker without hanging.

## 7. Risks and open points

- **Deadlock:** a shared holder that awaits something the remover holds. Mitigation: shared leases never wait on the
  broker; only exclusive waits, and always with a timeout.
- **Prefix semantics:** exclusive on a child must not be blocked by a shared lease on its *parent* (Workspace-role root
  scans the parent), only by leases at or beneath the child. Covered by the tests in P1.
- **Cost:** one dictionary lookup per git process; negligible against process start.
- **Classification of git subcommands** (read-only vs mutating) is a table that needs a safe default: unknown means
  `Mutating`.
- Decision needed: grace period for `Mutating` holders before they are cancelled (proposal: 5 s) and the hard ceiling before a stuck process is killed (proposal: 15 s).
- A cancelled sync leaves a half-finished operation in a worktree that is deleted anyway; the App's structural lock already makes this
  overlap rare, so forced release is a safety net, not a routine path.
