# Show the App's own background work, as the Worker's is shown

Status: decided, not started (2026-11-23).

## Problem

A user sees when the Worker is busy, but not when the App is. Work the App queues for itself runs with no sign in the UI. An operation that waits for it shows a message ("Applying pending repository changes...") and nothing else: the overlay terminal stays empty, no count, no progress. It looks frozen.

Found while testing effective versions (2026-11-23): after Prepare Workspace created branches in about 30 repositories, every checkout hook queued a sync notification. Each one ran a state recompute that, at that time, re-resolved effective versions with about 14 Worker GitVersion calls (5-50 s each, 29 runs in one log). The next Update waited on `IWorkerSyncNotificationBarrier` for minutes with an empty overlay. The resolver cost is fixed (a refresh reuses the decision stored for the same HEAD), but the lack of visibility stays: any slow notification, a burst of hooks, or a long write queue shows the same frozen overlay.

## What exists for the Worker today

- The Worker reports `ReportQueueStatus(total, byWorkspace)` over SignalR whenever its job queue changes.
- `WorkerQueueStateService` (singleton) holds the counts and raises `OnQueueStateChanged`.
- The UI shows them in three places:
  - `WorkerStatusBadge`: activity frame and the count in the title.
  - `WorkspaceRepositoriesHeader`: "Worker is completing N tasks" with a spinner (`WorkspaceRepositories.WorkerTasksPendingCount`).
  - `LoadingOverlay`: reads the total, but only to decide whether to clear the shared terminal.

## App background work that has no visibility

| Queue / service | What it does | Per Workspace | Operations that wait for it |
|---|---|---|---|
| `WorkerSyncNotificationQueue` | Applies hook sync notifications (`SyncCommandHandler`): state rows, dependency recompute, file version check, effective versions | Yes (`_pendingByWorkspace`) | Update, Update and Push, Prepare Workspace (`WaitForPendingAsync`, "Applying pending repository changes...") |
| `WorkspaceGitChangesWriteQueue` | Persists Git Changes snapshots | Yes (in the notification) | Git Changes stage / commit (`EnqueueAndWaitAsync`) |
| `SyncBackgroundService` | Repository syncs requested by the App (`EnqueueSync`) | Yes | Sync |
| `FeatureRemovalQueue` | Remove Feature in the background (at most 4 at once) | Yes | none (toast when done) |
| `GitChangesAuditScheduler`, `GitChangesWatcherKeeper` | Periodic Git Changes audits and watcher leases | Yes | none |
| `TokenHealthBackgroundService`, `GitHubApiUsageLoggerBackgroundService`, `TokenReencryptionService` | Process-wide maintenance | No | none |

## Proposal

1. **One App-side state service**, `AppBackgroundWorkStateService` (singleton), shaped like `WorkerQueueStateService`. It holds pending counts per queue, per Workspace and per context, plus a process-wide count, and raises one change event, throttled so a burst of hooks does not render the UI hundreds of times. Each queue reports to it when it enqueues and when it finishes an item (also when the item fails). `WorkerSyncNotificationQueue` already counts per Workspace for the barrier, so it reports from `MarkPending` / `MarkApplied`.
2. **Show it** as decided below: a number segment on the Worker badge, and one line on the Repositories page where the Worker takes priority.
3. **Overlay terminal lines:** the state service raises an "item applied" event with Workspace, context, queue, a short description and the duration. A job that shows an overlay subscribes for its own Workspace and context while it runs and writes those lines to its `JobTerminalBuffer` (subscription removed when the job ends).
4. **Waits report progress:** `IWorkerSyncNotificationBarrier.WaitForPendingAsync` takes an optional progress callback that gets the remaining count each time it changes. The orchestrator passes its `setProgress`.
5. **Slow items are logged** above a threshold (for example 2 s per notification) with the repository, trigger and duration, so the next slowdown like the effective-version one shows in the log without a profiler.

## Constraints

- The state service is a singleton, and every component that subscribes removes its handler in `Dispose` (AGENTS.md "Service lifetimes and event subscriptions").
- UI updates go through `InvokeAsync` (AGENTS.md "Blazor page jobs"). Counts are read on the dispatcher, never written from the queue thread into page fields.
- Feature contexts: every item carries its context id (decision 3); a Feature page never shows the special Workspace's count, and the reverse.
- No new Worker command or protocol change: this is App-only, so the App and Worker version lock is unaffected.
- Docker mode: everything stays inside the App; nothing depends on Desktop.

## Decisions (2026-11-23)

1. **Which queues:** all of them, including the process-wide maintenance services in the table above. A process-wide item counts toward the badge total but toward no Workspace or Feature.
2. **Where it shows:**
   - **Worker badge:** add a number segment to the existing badge, like the count segment of the badges on the Actions page (confirm the exact component when building), carrying the App's pending count. The Worker count and activity frame stay as they are.
   - **Repositories page, top right** (where "Worker is completing N tasks" shows today, `WorkspaceRepositoriesHeader`): one line, never both. While the Worker has tasks for this Workspace, the Worker line shows. When the Worker has none and the App has some, it shows "Waiting for N pending background tasks...". With neither, nothing.
3. **Granularity:** the count shown follows where the user is: the special Workspace's count on the Workspace's pages, a Feature's count inside that Feature (`?context=`). Every queued item therefore carries a context id, resolved when it is queued (sync notifications through `WorkspaceHookContextAttributor`, Git Changes snapshots from their notification, App syncs and Feature removal from their caller). An item whose context cannot be resolved counts toward the Workspace.
4. **Overlay terminal:** only while an overlay is visible. Then each applied item of that Workspace or context writes one line to the job's terminal ("Applied sync of {repository} ({trigger}) in 640 ms"), and a barrier wait updates the message with the remaining count ("Applying 12 pending repository changes..."). Without an overlay nothing is written anywhere but the log.

## Known limitation (deferred)

The Worker reports its task counts per Workspace, not per Feature. Inside a Feature, a Worker task of any context of that Workspace therefore takes priority and hides the App line. Splitting the Worker count by context needs a Worker change (`ReportQueueStatus`); decided 2026-11-23 to see how it works in practice first.

## Tests to add when built

- The state service: counts per queue and Workspace, change event throttling, a cleared count after the queue drains.
- `WorkerSyncNotificationQueue`: reports pending and applied, including the failure path (a handler that throws still decrements).
- The barrier progress callback: receives decreasing counts and completes at zero; cancellation stops reporting.
- Component tests: the header line appears with a pending count and disappears at zero; subscriptions are removed on dispose.
- Header priority: Worker tasks for this Workspace show the Worker line only; with no Worker tasks the App line shows; a Feature page counts only that Feature's items.
- Overlay lines: a job with an overlay gets one line per applied item of its own context, none of another context, and none after it ends.
