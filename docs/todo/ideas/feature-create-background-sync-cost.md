# Why the grid keeps updating for a minute after Create Feature

Status: analysis, 2026-11-24, of the code before the fix. Options A, B and D are built (see `../sync-performance-fix-plan.md`, which also lists what is open); this note is kept as the measurement the fix is compared with. It follows the visibility change ("Applying N repository updates"), which made this work visible.

## Summary

After Create Feature finishes, the App still applies one hook sync notification per worktree. In the measured run below (27 worktrees) that took 51.8 seconds, about twice as long as creating the Feature itself (26.6 seconds).

Three facts explain it:

1. **The notifications are applied one at a time.** `WorkerSyncNotificationQueue` has a single reader and waits for each item to finish.
2. **Each item reruns a recompute of the whole Feature**, not of its own repository. 27 notifications run the same whole-Feature recompute 27 times. Only the last result is needed.
3. **One step inside that recompute costs 87% of the time**: a Worker call (`GetHeadCommits`) that starts about 57 git processes to read commit SHAs the App already has in its database.

Fixing 2 or 3 alone removes most of the wait. Both are described under "What can be optimized".

## Measured run

Source: `%ProgramData%\GrayMoon\Logs\graymoon-app-20261123.log` and `graymoon-worker-20261123.log`. Workspace 14 (Edx1), Feature `file-audio`, 26 source repositories plus the Workspace repository.

| Time | What happened |
|---|---|
| 01:46:45 | Create Feature starts |
| 01:47:06.3 | 26 source worktrees created (17.1 s, 16 in parallel) |
| 01:47:06.7 | Projections seeded (0.36 s) |
| 01:47:06.8 | Worker releases the 27 checkout syncs it held back during creation |
| 01:47:11.1 | First notification reaches the App; it waits for Create to finish |
| 01:47:11.6 | Create Feature finished (26.6 s). The overlay closes and the page is usable |
| 01:47:17.8 | Worker has sent the last of the 27 notifications (6.7 s for all, GitVersion 2.2 to 3.0 s each, in parallel) |
| 01:48:02.9 | App has applied the last notification |

So the Worker needed 6.7 seconds and the App needed 51.8 seconds for the same 27 repositories. For 45 seconds the App was the only thing still working.

Where the 51.8 seconds went:

| Step | Calls | Total | Share |
|---|---|---|---|
| Worker `GetHeadCommits` (19 repositories each time) | 27 | 45.0 s | 87% |
| Worker `CheckFileVersions` | 27 | 0.2 s | under 1% |
| Everything else: state write, dependency recompute, database, broadcasts | 27 | about 6.6 s | 13% |

One notification took 1.4 to 4.8 seconds, 1.9 seconds on average.

## The App's background jobs

These run without an overlay. Only the first one is involved in the wait after Create Feature.

| Job | What it does | Runs when |
|---|---|---|
| `WorkerSyncNotificationQueue` | Applies a hook sync notification from the Worker: repository state, then the Feature-wide recompute (below) | Every git hook: checkout, commit, merge, push, branch created or deleted |
| `WorkspaceGitChangesWriteQueue` | Saves a Git Changes snapshot (changed files of one repository) | A file changes in a watched repository; a new Feature is scanned once |
| `GitChangesAuditScheduler` | Asks the Worker for a full status scan of a context | Periodically, on Worker reconnect, when a context becomes active |
| `GitChangesWatcherKeeper` | Renews the Worker's file watchers | Periodically |
| `SyncBackgroundService` | Syncs one repository on request | Only the REST endpoint `POST /api/sync` |
| `FeatureRemovalQueue` | Removes Features in the background, 4 at once | Remove Feature |
| `FeatureFolderCleanupService` | Deletes Feature folders that were still in use at removal | Worker connect, Workspace page open |
| `WorkspaceFeatureReconciler` | Compares Features in the database with worktrees on disk | Worker connect |
| `TokenHealthBackgroundService`, `GitHubApiUsageLoggerBackgroundService` | Connector token check, API usage log | Periodically |

During Create Feature the log also shows dozens of `GetGitChangeStatus` calls (0.4 to 2.9 s each) for Git Changes. They run on the Worker's read pool at the same time as the GitVersion runs, so they compete for CPU and disk, but they do not go through the sync queue.

## What one sync notification does

Worker side (`CheckoutHookSyncCommand`), for a fresh Feature worktree:

1. Read the default branch ref and the current branch.
2. Skip the fetch, the project scan and the commit counts (the Feature seed already has them).
3. Run GitVersion (2.2 to 3.0 s). This is the only new information: the Feature's own version, such as `0.1.0-file-audio.6`.
4. Send the notification.

App side (`SyncCommandHandler.HandleAsync`), one notification at a time:

1. Find the repository link and the context the path belongs to.
2. Wait if the Feature is still being created.
3. Drop the parts a newer operation already wrote (`RepositoryStateFreshness`).
4. Write the repository's state (`WorkspaceRepositoryStateWriter.ApplyAsync`).
5. Update the context's "in sync" flag.
6. Run `WorkspaceStateRecomputeScope.RecomputeAsync` for the whole context:
   - **Effective versions** of every repository (`EffectiveVersionResolver`).
   - **File version check** of every configured version file (`CheckAndPersistFileVersionStatusAsync`). When a pattern has a `:commit` token it first calls `GetHeadCommits` on the Worker for all those repositories, then `CheckFileVersions`.
   - **Dependency stats** of every repository: levels, dependency counts, unmatched counts.
7. Broadcast `ContextRepositorySynced` and `ContextSynced` to every browser.

Steps 1 to 5 and 7 are per repository and cheap (20 to 500 ms in the log). Step 6 is per Feature and costs about 1.7 seconds here.

## Why it is slow

### 1. The recompute runs once per notification

The handler asks `SyncRecomputeCoalescer` to share one recompute between callers, and its comment says "Create Feature releases a sync per repository at once: share one recompute instead of running N in a row". That cannot happen from this queue: the queue has one reader and awaits each handler, so there is never a second caller to share with. The log confirms it: 27 notifications, 27 `CheckFileVersions` calls, 27 `GetHeadCommits` calls.

The document and the code differ here; the code comment is the wrong one.

### 2. `GetHeadCommits` is called for data the App has

`ResolveTokenValuesAsync` asks the Worker for the HEAD commit of each repository named in a `:commit` token. The Worker runs three git processes per repository (`rev-parse HEAD`, current branch, checked-out tag): 19 repositories is 57 processes per call, and 27 calls is about 1,500 git processes. The check uses only the SHA.

The context state row already stores `HeadCommit` (the effective version resolver reads it from there).

### 3. The queue is serial for the whole App

One slow item delays every later item of every Workspace. Update, Update and Push and Prepare Workspace wait for the Workspace's items (`IWorkerSyncNotificationBarrier`), so pressing Update right after Create Feature waits for the whole burst.

## The same cost elsewhere

Create Feature is only the most visible case. The same logs show it in every flow that fires hooks for many repositories, and inside Update.

| Flow (from the logs) | What ran | App time |
|---|---|---|
| Switch branch in 27 repositories (22 Nov 20:28) | 27 checkout hooks, 27 `GetHeadCommits`, 27 `CheckFileVersions` | 27.5 s after the checkouts |
| Update, 22 commits (22 Nov 20:32 to 20:34) | 71 `GetHeadCommits`, 69 `CheckFileVersions` | 62 s of sync items, plus the checks the Update runs itself |
| Update, 18 commits (23 Nov 09:46 to 09:49) | 75 `GetHeadCommits`, 75 `CheckFileVersions` | 72 s of sync items |
| Push of 5 repositories (23 Nov 00:38, 01:05) | 16 to 17 `GetHeadCommits` each | 21 to 27 s |

Over the whole of 23 Nov the App called `GetHeadCommits` 512 times, 496 seconds of Worker time in total (178 calls and 169 seconds on 22 Nov).

Affected:

- **Every hook sync**: Switch branch, Push, a commit from Git Changes, and any commit, checkout or branch change made in a terminal or IDE. Each one runs the whole-context recompute.
- **Update and Update and Push**: the file version check runs about 70 times in one Update, each with its own `GetHeadCommits`. Which callers produce each of those was not traced; `DependencyUpdateOrchestrator` calls the check in three places per level, and the Update's pushes and branch refs add hook syncs on top.

Not affected:

- Flows that switch hooks off and write state themselves: Return to Default, the commits of a dependency update, Create Feature's own branch work.
- Pull: it holds the recompute back and runs it once at the end (`CompletePullAsync`).
- A Workspace without `:commit` tokens: the recompute still repeats per item, but an item costs about 0.3 s instead of 1 to 2 s (Workspace 15 in the logs).

So option A below helps all of these at once, and option B helps every hook burst, not only Create Feature.

## What can be optimized

In order of value for the effort.

**A. Read `:commit` values from the stored `HeadCommit` in the check path.**
Removes the `GetHeadCommits` call from every hook sync. Expected: 51.8 s becomes about 7 s in the measured run. Small change in one method. To confirm first: every path that moves HEAD writes `HeadCommit` for that context, and the Update path (which writes files) may still want the live value.

**B. One recompute per burst.**
The queue applies the state write of every notification already waiting for the same context, then runs the recompute once and broadcasts once. Expected: 27 recomputes become 1 or 2. With A it brings the App's part to about 3 seconds, so the grid settles when the Worker's last notification arrives. Needs care with the barrier: an item stays pending until the recompute that covers it has finished.

**C. Deliver the Feature's GitVersion without a hook sync.**
For a fresh worktree the notification carries only the version. The Worker could send all 27 in one message, or Create Feature could return the version from the worktree command so the seed writes it. Removes the burst entirely, but changes the Create flow and the Worker protocol. Only worth it if A and B are not enough.

**D. Fewer broadcasts.** Each item sends two events to every browser. How much the page does per event was not measured; B reduces `ContextSynced` to one per burst anyway.

Not worth changing: the Worker side. 27 GitVersion runs in 6.7 seconds is already parallel, and GitVersion itself is the cost.

## Not measured

- **Effective versions.** The build that wrote this log did not log effective version resolution, so that step is not in the numbers above. It matters for a new Feature in particular: every repository is still on its branch point, which is the one case where the resolver asks the Worker for a GitVersion run at the parent branch (`GetGitVersionAtBranchPoint`) and asks the package registry. The first recompute pays that for each repository with a version consumer; later ones reuse the stored decision for the same HEAD. The earlier note of 2026-11-23 recorded runs of 5 to 50 seconds before that reuse existed.
- **Browser reloads** per broadcast.
- **Other Workspaces.** The 4 second Create Features in the log (Workspace 2, few repositories) were not analysed.

## How to measure again

The queue now logs a warning for every item that waited plus applied for more than 2 seconds:

```text
Slow sync notification: workspace {WorkspaceId} repo {RepositoryId} waited {WaitMs} ms in the queue and took {ApplyMs} ms to apply
```

`ApplyMs` is the cost of one item; `WaitMs` grows through the burst and its last value is the total wait. For the split inside one item, the Debug lines `SyncCommand dependency stats persisted in`, `Worker command GetHeadCommits ... completed in`, `Effective versions resolved ... in` and the Worker's `GetHeadCommits timing` give each step.
