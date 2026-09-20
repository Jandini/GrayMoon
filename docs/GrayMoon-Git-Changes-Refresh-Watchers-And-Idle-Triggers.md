# Git Changes Refresh, File Watchers, and Idle Triggers

This document describes **how Git Changes actually refreshes today**, **when Agent-side file watchers
are alive versus disposed**, and **every Idle / Active / Hidden trigger GrayMoon implements**. It is
the current source of truth for those three topics. The older
`GrayMoon-Git-Changes-Current-Implementation-Design.md` still describes the overall feature, but several
of its refresh/monitor details are stale (Refresh now does a real rescan; monitoring only sweeps
workspaces that have a recent Git Changes viewer; `ForceRefresh` no longer exists on the wire).

GrayMoon is two processes. The App never watches files. The Agent never decides on its own which
workspaces exist. Refresh is a conversation between them.

---

## 1. Git Changes refresh: what "refresh" means

There are two different refreshes. Mixing them up is the usual source of confusion.

| Kind | What it does | Who runs it | Does it run `git status`? |
|---|---|---|---|
| **UI reload** (`LoadAsync`) | Re-reads the persisted SQLite projection and rebuilds the tree | App, on the Git Changes page | No |
| **Status scan** (`GetGitChangeStatus` / watcher-driven coordinator scan) | Runs `git status --porcelain=v2` and produces a versioned snapshot | Agent | Yes |

The page is a **read model**. Opening `/workspaces/{id}/changes` never scans. It always shows whatever
SQLite last stored. A scan only happens when something explicitly asks the Agent, or when a live
`FileSystemWatcher` on the Agent marks a repository dirty.

Once a scan succeeds, the snapshot is persisted and the UI is told to reload. That second hop is
shared by every scan source (watcher, background sweep, warm-up, manual Refresh, mutation response).

```text
scan source
    -> Agent GitStatusRefreshCoordinator (debounce / coalesce / bounded git status)
    -> GitChangeSnapshot (versioned)
    -> App WorkspaceGitChangesWriteQueue
    -> GitChangesSnapshotPushHandler (SQLite upsert, reject stale versions)
    -> WorkspaceSyncHub "GitChangesUpdated(workspaceId, repositoryId)"
    -> open Git Changes page LoadAsync()  (and Repositories page row snapshot refresh)
```

---

## 2. How a status scan is triggered

### 2.1 The only Agent entry points

On the Agent, a repository is scanned in exactly two ways, both owned by
`GitStatusRefreshCoordinator`:

1. **`MarkDirty(repoPath)`** - watcher hint. Starts (or coalesces into) a **debounced** scan.
2. **`RefreshNowAsync(repoPath)`** - explicit request. Bypasses the debounce timer, but still
   coalesces with a scan already in flight for that same repository.

Nothing else on the Agent starts a Git Changes status scan. Stage / unstage / discard / commit call
`IRepositoryGitChangesService` directly, return a post-mutation snapshot in the command response, and
do **not** go through the coordinator.

### 2.2 Watcher-driven refresh (`MarkDirty`)

`GitRepositoryWatcher` raises `Changed` (relevant path changed) or `Overflowed` (watcher error; the
watcher was disposed and recreated). Both are wired by `GitRepositoryWatcherManager.CreateEntry` to:

```csharp
watcher.Changed += () => refreshCoordinator.MarkDirty(repoPath);
watcher.Overflowed += () => refreshCoordinator.MarkDirty(repoPath);
```

The watcher event is an **invalidation hint only**. It never carries enough information to update the
tree. The source of truth is always a fresh `git status`.

Per-repository state machine (`RepositoryRefreshTracker`):

| Current state | Incoming dirty event | What happens |
|---|---|---|
| `Clean` | `MarkDirty` | Move to `Dirty`, start debounce timer (`WatcherDebounceMilliseconds`, default **400 ms**) |
| `Dirty` | `MarkDirty` | No-op (scan already scheduled) |
| `Refreshing` | `MarkDirty` | Move to `RefreshingAndDirty` (exactly one follow-up scan after the current one) |
| `RefreshingAndDirty` | `MarkDirty` | No-op (follow-up already pending) |
| `Disposed` | anything | Ignored |

When the debounce timer fires it calls `RunScanAsync`. If a dirty event arrived **while** a scan was
running, `EndRefresh` returns `runFollowUp = true` and the same loop runs one more scan immediately
without going back through `TryBeginRefresh` (doing so would mistake the follow-up for a duplicate
caller and drop it).

A repository never has more than **one active scan and one pending follow-up**. Across repositories,
scans share one `SemaphoreSlim` sized to `GitChangesOptions.MaxParallelRepositoryOperations`
(default **8**).

### 2.3 Explicit refresh (`RefreshNowAsync`) via `GetGitChangeStatus`

`GetGitChangeStatusCommand` is the **only** Agent command that:

1. Registers the path in `GitChangesRepositoryRegistry` as `(workspaceId, repositoryId)` so a later
   watcher-driven scan can be attributed back to the App's database IDs.
2. Acquires a `GitRepositoryWatcherManager` lease (this is the **only** call site that creates a
   watcher).
3. Calls `coordinator.RefreshNowAsync`.

```csharp
registry.Register(repoPath, request.WorkspaceId, request.RepositoryId);
using var lease = watcherManager.Acquire(repoPath);
var result = await coordinator.RefreshNowAsync(repoPath, cancellationToken);
```

The `using` releases the lease before the handler returns. That does **not** tear the watcher down;
it starts (or resets) the idle-grace countdown described in Section 4. Effectively every
`GetGitChangeStatus` means: "scan now, and keep watching this path for another
`WatcherIdleGraceMinutes`."

There is no `ForceRefresh` flag on the current wire. Every `GetGitChangeStatus` already bypasses
debounce. If a scan is already in flight for that repo, additional callers share its result via a
`TaskCompletionSource` rather than starting a second concurrent `git status`.

### 2.4 App-side sources that call `GetGitChangeStatus`

All of them go through `IGitChangesWorkspaceScanner.ScanWorkspaceAsync` (or, equivalently, the same
`IGitChangesAgentClient.GetStatusAsync` it uses). That method:

- No-ops if the Agent is not connected.
- Loads every `WorkspaceRepositoryLink` for the workspace.
- Resolves the workspace root; links with no root are skipped silently.
- Fans out `GetGitChangeStatus` with bounded parallelism (`MaxParallelRepositoryOperations`).
- Enqueues each successful snapshot onto `WorkspaceGitChangesWriteQueue`.

#### A. Periodic background sweep (keeps watchers alive)

`GitChangesMonitoringBackgroundService` (App hosted service):

- Waits 5 seconds after host start, then loops forever.
- Each iteration: if Agent is connected, call `ScanWorkspaceAsync` for **every currently active
  workspace** (see Section 4.2), sequentially (each scan is already internally bounded).
- Sleeps `WatcherRenewalIntervalMinutes` (default **3**, clamped to
  `[1, WatcherIdleGraceMinutes - 1]` so a sweep always happens before leases would idle out).
- Wakes immediately when `AgentConnectionTracker` transitions to `Online` (Agent connect/reconnect),
  instead of waiting out the remaining interval.

This sweep is why watchers stay alive while someone is looking at Git Changes. It is **not** a
full-database poll: workspaces with no recent Git Changes viewer are not scanned.

#### B. On-open warm-up scan (cold start)

`WorkspaceGitChanges.Activity.EnsureActivitySubscription` runs on `OnInitializedAsync` and
`OnParametersSetAsync`:

1. Subscribe this page instance as a viewer of `WorkspaceId` (ref-counted).
2. If the workspace was **not** already active **and** the Agent is connected **and** no scan job is
   already running, start a one-shot `StartScanJob("Refreshing repositories...")`.

"Already active" includes the App-side activity grace window (Section 4.2). Navigating away and back
within that window does **not** kick another warm-up; background monitoring is assumed to still be
covering the workspace.

The warm-up job uses `ScanJobKey` (URL path + `":scan"`), not the page overlay key. The tree/empty
state stays visible; a header/empty-state spinner binds to `IsScanRunning`. The job is circuit-scoped
and survives navigating away.

#### C. Manual Refresh button

`ManualRefreshAsync`:

- Refuses if the Agent is not connected (toast: start the Agent).
- Refuses if any scan/page job is already running.
- Otherwise `StartScanJob("Refreshing repositories...")` - the same non-overlay workspace-wide
  `ScanWorkspaceAsync` as the warm-up.

This **does** ask the Agent for a fresh `git status` of every repository in the workspace, not just
the ones currently showing changes. Incremental tree updates still arrive via `GitChangesUpdated` as
each repository's snapshot is persisted.

#### D. Agent reconnect

Same hosted service as (A). `connectionTracker.OnStateChanged` -> `TryWake()` when state is `Online`.
The next loop iteration scans all currently active workspaces immediately. If no workspace is active,
the wake is a no-op (nothing to scan, no watchers to seed).

### 2.5 Mutation-driven UI refresh (not a coordinator scan)

Stage / unstage / discard / commit return a `GitChangeSnapshot` in the Agent command response. The
page enqueues that snapshot onto the **same** `WorkspaceGitChangesWriteQueue`. After a short 150 ms
wait (single-repo path) it calls `LoadAsync` so the tree reflects the just-persisted write rather
than racing the background worker. Multi-repo fan-out reloads once at the end.

These mutations **do not** acquire a watcher lease and **do not** publish via
`GitChangesSnapshotPublisher`. Monitoring for that repository continues only if a `GetGitChangeStatus`
has already seeded a watcher (warm-up, sweep, or Refresh).

### 2.6 How the open page actually re-renders

`LoadAsync` is SQLite-only. Callers:

| Event | What happens |
|---|---|
| Page init / workspace parameter change | `StartInitialLoadJob` -> `LoadAsync` |
| `GitChangesUpdated` on `/hubs/workspace-sync` | `WorkspaceGitChanges.Realtime` -> `LoadAsync` |
| Scan job finished | `StartScanJob` completion -> `LoadAsync` |
| Single-repo mutation persisted | `PersistMutationResultAsync` -> delay 150 ms -> `LoadAsync` |
| Manual Refresh | scan job; incremental `GitChangesUpdated` plus a final `LoadAsync` |

The Repositories page also listens for `GitChangesUpdated` and refreshes that one row's list snapshot
(staged/changed counts on the grid), unless a background job is running (then it defers).

### 2.7 Watcher-driven push path (unsolicited)

`GitChangesSnapshotPublisher` (Agent hosted service, constructed eagerly) subscribes to
`GitStatusRefreshCoordinator.SnapshotReady`. For each snapshot:

1. Look up `(workspaceId, repositoryId)` in `GitChangesRepositoryRegistry`. No entry -> drop
   (the App never asked about this path).
2. If the Agent's SignalR connection is not `Connected` -> drop silently.
3. Invoke `GitChangesSnapshotUpdated` on the App hub.

`AgentHub.GitChangesSnapshotUpdated` enqueues onto `WorkspaceGitChangesWriteQueue`. Persistence and
broadcast then match every other snapshot source.

If the Agent is disconnected from the App, watcher scans may still run locally (the Agent process is
still watching the disk) but the App will not see them until a later connected publish, a sweep, or a
manual Refresh.

---

## 3. File watchers: when they are active

### 3.1 What is watched

One `GitRepositoryWatcher` per leased repository path, created on first `Acquire`. It owns two native
`FileSystemWatcher` instances, started immediately in the constructor:

**Work-tree watcher** (repo root, recursive):

- `NotifyFilters`: `LastWrite | FileName | DirectoryName | Size`
- Events: Created / Changed / Deleted / Renamed
- Ignores any path under `.git` so object writes do not double-fire with the git-dir watcher
- Every other event raises `Changed`

**Git-dir watcher** (`<repo>\.git`, recursive, only if that directory exists):

- `NotifyFilters`: `LastWrite | FileName | DirectoryName`
- Only raises `Changed` when the event touches a **relevant** name:
  `index`, `HEAD`, `packed-refs`, `MERGE_HEAD`, `CHERRY_PICK_HEAD`, `rebase-merge`, `rebase-apply`,
  anything whose parent is `refs`, or any path containing `\refs\`
- Loose objects, `logs`, etc. are ignored

On `Error` (buffer overflow or watcher failure):

1. Log a warning
2. Dispose both watchers (`EnableRaisingEvents = false`, unsubscribe, `Dispose`)
3. Raise `Overflowed` (treated like `Changed`: mark dirty, full rescan)
4. If not disposed, `Start()` again (recreate both watchers)

A failed `Start()` (permissions, missing path, etc.) is logged and leaves the instance with no live
watchers until the next recreation.

These are the **only** `FileSystemWatcher` instances in GrayMoon.

### 3.2 Lease lifetime (when a watcher exists)

`GitRepositoryWatcherManager` is an Agent singleton. Keyed by normalized full path
(`Path.GetFullPath`, trailing separators stripped, case-insensitive).

```text
Acquire(path)
    GetOrAdd entry; if new -> create GitRepositoryWatcher, wire Changed/Overflowed to MarkDirty
    CancelIdleDisposal()
    LeaseCount++
    return IDisposable lease

Release (lease.Dispose)
    LeaseCount--
    if LeaseCount > 0: keep watching
    if LeaseCount == 0: start idle timer (WatcherIdleGraceMinutes, default 10, minimum 1)
```

Ref-counting details that matter in practice:

- Two overlapping `GetGitChangeStatus` calls for the same path share **one** watcher.
- Releasing the last lease does **not** dispose the watcher. Disk events during the grace period
  still mark dirty and still scan (covered by tests).
- A new `Acquire` during the grace period cancels the idle timer; `LeaseCount` goes from 0 to 1
  and the same watcher keeps running.
- `manager.Dispose()` (Agent process shutdown) drops every watcher immediately, grace ignored.

Because `GetGitChangeStatus` acquires-and-releases in one `using`, the steady state while a workspace
is being monitored is:

```text
LeaseCount == 0, watcher still alive, idle timer ticking
every WatcherRenewalIntervalMinutes (and on warm-up / Refresh / reconnect)
    GetGitChangeStatus -> Acquire (cancel idle timer) -> scan -> Release (restart idle timer)
```

As long as the App keeps asking about the path more often than `WatcherIdleGraceMinutes`, the watcher
never dies.

### 3.3 What "active" means in practice

A watcher is **active** when all of the following are true:

1. The Agent process is running.
2. Someone has called `GetGitChangeStatus` for that repository at least once since Agent start
   (or since the last idle disposal), which created the watcher.
3. Either `LeaseCount > 0` **or** the idle-grace timer has not yet fired.
4. `Start()` succeeded (the native watchers are raising events).

The Git Changes page being open is **not** what creates the watcher. The page only marks the
workspace as "actively viewed" on the App. The App's scanner / sweep is what causes `Acquire`.

---

## 4. File watchers: when they go idle / "offline"

"Offline" is used in two different ways in this feature. They are not the same thing.

### 4.1 Watcher idle-out (disposed, no longer watching disk)

When the last lease is released, `ScheduleIdleDisposal` starts a one-shot timer of
`TimeSpan.FromMinutes(Math.Max(1, WatcherIdleGraceMinutes))` (default **10 minutes**).

When that timer fires, **only if** `LeaseCount` is still `<= 0`:

1. Remove the entry from the manager dictionary
2. Dispose the `GitRepositoryWatcher` (stops both `FileSystemWatcher`s; further disk events are lost)
3. `GitStatusRefreshCoordinator.RemoveTracker(path)` - in-flight scan still completes (it holds the
   tracker instance), later `MarkDirty`/`RefreshNowAsync` would create a fresh tracker
4. `GitChangesSnapshotCache.Remove(path)` - version counter and latest snapshot dropped
5. `GitChangesRepositoryRegistry.Remove(path)` - watcher-driven publishes for this path will now be
   dropped until the App asks again

This is also the Agent's **only** process-local signal that a repository or workspace went away.
The Agent has no database. If the App stops asking about a path (user left Git Changes long enough,
workspace deleted, repository removed), the expired lease is treated as "we are done with this path"
and the dictionaries are pruned so they do not grow for the lifetime of the Agent process.

After idle-out, **nothing** watches that repository until the next `GetGitChangeStatus`.

Typical ways a watcher idles out:

| Situation | Why the lease is not renewed |
|---|---|
| Last Git Changes viewer left, and App-side activity grace also elapsed | Background sweep stops including that workspace, so no more `Acquire` |
| Agent running, but no Git Changes page has been opened since Agent start | No `GetGitChangeStatus` ever ran; no watcher was created in the first place |
| Workspace/repository removed from the App | App stops asking about that path; idle timer completes |
| Agent process exiting | `GitRepositoryWatcherManager.Dispose` tears watchers down immediately |

`WorkspaceActivityGraceMinutes` (default **8**) is kept **just under** `WatcherIdleGraceMinutes`
(default **10**) on purpose: the App stops sweeping a workspace slightly before the Agent would have
disposed the watchers anyway, so you do not pay for a last useless sweep, and you also do not leave
orphaned watchers for long after the last viewer left.

### 4.2 App-side workspace activity (who the sweep is willing to scan)

`WorkspaceGitChangesActivityTracker` (App singleton) is the Git Changes analogue of the Agent watcher
lease, keyed by `workspaceId` rather than filesystem path.

- `Subscribe(workspaceId)` from the Git Changes page; disposed in `DisposeAsync`.
- Ref-counted: two browser tabs on the same workspace keep it active until both leave.
- After the last subscriber leaves, the workspace stays active for
  `WorkspaceActivityGraceMinutes` (default **8**, clamped to at least 1).
- `IsActive` = `RefCount > 0` **or** `now - LastActiveUtc < GraceWindow`.
- `GetActiveWorkspaceIds()` also garbage-collects entries whose refcount is 0 and whose grace has
  expired. That list is exactly what the background sweep iterates.

So the chain from "user closed the page" to "watchers actually stop" is:

```text
page DisposeAsync
    -> Release activity lease (refcount--)
    -> if last viewer: LastActiveUtc = now, workspace still "active" for 8 minutes
    -> sweeps continue, each GetGitChangeStatus resets the Agent idle timer to 10 minutes
    -> after 8 minutes, workspace drops out of GetActiveWorkspaceIds()
    -> sweeps skip it
    -> Agent idle timer is no longer reset
    -> ~10 minutes after the last GetGitChangeStatus, watchers dispose
```

The grace window only applies after `RefCount` reaches 0. While any lease is still held
(`RefCount > 0`), `IsActive` stays true with **no** wall-clock expiry of that live ref. The
intended release path is the page's `IAsyncDisposable` (`ReleaseActivitySubscription`). A circuit
that never disposes its lease would keep the workspace in the sweep set until the App process
recycles. The grace exists so a **clean** navigate-away still gets ~8 more minutes of monitoring
(and so a quick navigate-away-and-back is not treated as a cold start).

### 4.3 Agent connection "offline" (UI and sweep, watchers may still be running)

This is **not** the same as watcher disposal.

`AgentConnectionTracker` states: `Connecting`, `Online`, `Offline`, `VersionMismatch`.

When the Agent is not connected:

| Surface | Behavior |
|---|---|
| Git Changes tree `OfflineNotice` | `"Agent is offline"` or `"Agent is offline, showing state from HH:mm"` using the latest `PersistedAt` in the projection |
| Manual Refresh / warm-up / mutations | Refused or no-op (`IsAgentConnected` checks) |
| Background sweep | Returns immediately, scans nothing, therefore **does not renew leases** |
| `GitChangesSnapshotPublisher` | Drops pushes if hub state is not `Connected` |
| Agent-side `FileSystemWatcher`s | **Keep running** if the Agent process is still up. Disk events still debounce and scan locally. Results sit in the Agent cache until a connected publish or the next App-side `GetGitChangeStatus`. |
| Watcher idle timer | Still ticking. If the App stays disconnected longer than `WatcherIdleGraceMinutes` after the last `GetGitChangeStatus`, watchers **will** idle out even though the Agent process is alive. |

On reconnect (`Online`):

1. Sweep wakes immediately.
2. If any workspace is still App-side active, `GetGitChangeStatus` re-acquires leases (creating
   watchers if they already idled out) and rescans.
3. If no workspace is active, reconnect does **not** recreate watchers. The next Git Changes page
   open runs the warm-up scan, which seeds them.

If the **Agent process** itself exits, every watcher is gone immediately. The App shows Agent
offline. There is no residual watching on the machine.

### 4.4 Overflow / start failure (temporarily not watching, then recovered)

A native watcher `Error` is a brief hole: events in the overflow window can be missed. The code
treats that as `Overflowed` -> `MarkDirty` -> full `git status`, then recreates the watchers. The
lease is unchanged; this is not an idle-out.

If `Start()` throws, that repository is leased but not actually watching until the next overflow
recreate or a new watcher instance (next `Acquire` after idle-out). There is no automatic retry loop
beyond the overflow path.

### 4.5 Snapshot versions after idle-out or Agent restart

`GitChangesSnapshotCache.NextVersion` is seeded from `DateTime.UtcNow.Ticks`, not from `1`, so a
restarted Agent (empty in-memory counter) still produces versions above whatever the App last
persisted. Idle-out `Remove`s the counter for that path; the next scan after a new `Acquire` starts
a fresh tick-based version. The App rejects `snapshot.Version <= existing.SnapshotVersion`.

---

## 5. End-to-end timelines

### 5.1 First open of Git Changes (cold)

```text
User opens /workspaces/42/changes
    LoadAsync            -> tree from SQLite (possibly empty / stale)
    Subscribe(42)        -> workspace becomes active (was not)
    StartScanJob         -> ScanWorkspaceAsync
        for each repo: GetGitChangeStatus
            Agent: registry.Register, Acquire (CREATE watcher), RefreshNowAsync (git status)
            App: enqueue snapshot -> SQLite -> GitChangesUpdated -> LoadAsync
    Background sweep now includes 42 every ~3 minutes (renews leases, catch-up scan)
```

### 5.2 File edited in an editor while the page is open

```text
FileSystemWatcher (work-tree) -> Changed -> MarkDirty
    Clean -> Dirty, 400 ms debounce
    RunScanAsync -> git status -> SnapshotReady
    Publisher -> GitChangesSnapshotUpdated
    WriteQueue -> persist -> GitChangesUpdated
    Open page LoadAsync -> tree updates
```

A burst of saves coalesces: still one scan after the first 400 ms quiet period, plus at most one
follow-up if edits continued during that scan.

### 5.3 User leaves Git Changes

```text
DisposeAsync -> Release activity lease
    RefCount 0, still IsActive for 8 minutes  -> sweeps continue, watchers stay (idle timer reset every 3 min)
    After 8 minutes: workspace leaves active set -> sweeps skip it
    Last GetGitChangeStatus's idle timer (10 min) runs out -> watchers disposed, tracker/cache/registry pruned
```

### 5.4 Agent disconnects while page is open

```text
App: OfflineNotice, sweep skips, Refresh disabled
Agent process still running: watchers still fire, local scans still run, publishes drop
If disconnect lasts > ~10 minutes after last GetGitChangeStatus: watchers idle out
On Online: sweep wakes; because the page still holds the activity lease, ScanWorkspaceAsync
    re-seeds watchers and refreshes SQLite
```

---

## 6. Configuration (`GitChangesOptions`, `"GitChanges"` section)

Not currently present in `appsettings.json`; defaults apply.

| Setting | Default | Role |
|---|---|---|
| `MaxParallelRepositoryOperations` | 8 | Shared bound: Agent coordinator gate, App workspace scan fan-out, and (by policy) other Git Changes batch work |
| `WatcherDebounceMilliseconds` | 400 | Quiet period after a watcher event before `git status` |
| `WatcherIdleGraceMinutes` | 10 | How long a watcher survives with `LeaseCount == 0`. Clamped to at least 1 at use sites |
| `WatcherRenewalIntervalMinutes` | 3 | Background sweep period. Clamped to `[1, WatcherIdleGraceMinutes - 1]` |
| `WorkspaceActivityGraceMinutes` | 8 | How long a workspace stays sweep-eligible after the last Git Changes viewer leaves. Clamped to at least 1 |

The intended inequality is:

```text
WatcherRenewalIntervalMinutes  <  WorkspaceActivityGraceMinutes  <  WatcherIdleGraceMinutes
         3 min                            8 min                            10 min
```

Sweep often enough to renew leases; stop sweeping a deserted workspace before the Agent would have
dropped its watchers anyway.

---

## 7. File map (refresh and watchers)

| Process | File | Role |
|---|---|---|
| Agent | `Services/GitChanges/GitRepositoryWatcher.cs` | Two `FileSystemWatcher`s; `Changed` / `Overflowed` hints |
| Agent | `Services/GitChanges/GitRepositoryWatcherManager.cs` | Ref-counted leases, idle disposal, prune coordinator/cache/registry |
| Agent | `Services/GitChanges/GitStatusRefreshCoordinator.cs` | Dirty/debounce/coalesce/bounded `git status` |
| Agent | `Services/GitChanges/GitChangesSnapshotPublisher.cs` | Unsolicited `GitChangesSnapshotUpdated` |
| Agent | `Services/GitChanges/GitChangesRepositoryRegistry.cs` | Path -> `(workspaceId, repositoryId)` |
| Agent | `Services/GitChanges/GitChangesSnapshotCache.cs` | Per-repo version + latest snapshot |
| Agent | `Commands/GetGitChangeStatusCommand.cs` | **Only** `Acquire` call site |
| App | `Services/GitChanges/GitChangesWorkspaceScanner.cs` | Workspace-wide `GetGitChangeStatus` fan-out |
| App | `Services/GitChanges/GitChangesMonitoringBackgroundService.cs` | Periodic + reconnect sweep of **active** workspaces |
| App | `Services/GitChanges/WorkspaceGitChangesActivityTracker.cs` | Which workspaces currently have (or recently had) a Git Changes viewer |
| App | `Components/Pages/WorkspaceGitChanges.Activity.cs` | Page subscribe + cold-start warm-up scan |
| App | `Components/Pages/WorkspaceGitChanges.JobHelper.cs` | `StartScanJob` (Refresh / warm-up) |
| App | `Components/Pages/WorkspaceGitChanges.Realtime.cs` | `GitChangesUpdated` -> `LoadAsync` |
| App | `Components/Pages/WorkspaceGitChanges.razor.cs` | `LoadAsync`, `ManualRefreshAsync`, `OfflineNotice` |
| App | `Hubs/AgentHub.cs` | Receives `GitChangesSnapshotUpdated` |
| App | `Services/GitChanges/WorkspaceGitChangesWriteQueue.cs` | Serializes snapshot persistence |
| App | `Services/GitChanges/GitChangesSnapshotPushHandler.cs` | SQLite + `GitChangesUpdated` broadcast |
| Common | `Git/GitChangesOptions.cs` | The knobs in Section 6 |

---

# Idle / non-idle triggers in GrayMoon

This section is separate from Git Changes watcher idle. GrayMoon has **several** things named idle.
Only one of them is the product-wide user-activity system (`Active` / `Idle` / `Hidden`). Git Changes
does **not** subscribe to it.

---

## 8. Browser-tab activity: Active / Idle / Hidden

This is the Idle / non-idle system people usually mean.

### 8.1 Detection (`wwwroot/js/idleActivity.js` + `AppActivityTracker`)

Mounted once per Blazor circuit from `MainLayout.razor` (`<AppActivityTracker />`). One activity
state per browser tab, not per page.

JavaScript:

- Passive listeners: `mousemove`, `mousedown`, `keydown`, `wheel`, `touchstart`, `scroll`. These
  **only** stamp `lastActivityTs`. They do not call into .NET.
- `document.visibilitychange` plus, in GrayMoon Desktop only, a WebView2 `postMessage`
  `{ type: "windowVisibility", hidden }` from `MainWindow.NotifyWebViewVisibility`. Minimizing to the
  taskbar or hiding to the tray is treated as Hidden; a normal browser tab uses `document.hidden`.
- Every **2 seconds** (`TIER_CHECK_INTERVAL_MS`) the script computes a tier and calls
  `OnActivityStateChanged` **only when the tier changed**.

Idle timeout is **20_000 ms**, hard-coded in `AppActivityTracker.razor` (not `GitChangesOptions`).

Tier rules:

| Tier | Condition |
|---|---|
| `Hidden` | `document.hidden` **or** Desktop posted `hidden: true` |
| `Idle` | Tab visible, but no activity event for >= 20 seconds |
| `Active` | Tab visible, and last activity was within 20 seconds |

Initial state is `Active` (fresh `lastActivityTs` at `init`).

### 8.2 App-side service (`AppActivityStateService`)

Registered **scoped** (one instance per circuit) in `Program.cs`.

```csharp
public enum AppActivityState { Active, Idle, Hidden }
```

- `State` - current tier
- `StateChanged` - every transition
- `BecameActive` - **only** when entering `Active` from `Idle` or `Hidden`

`BecameActive` exists so a poller can **interrupt a long Idle/Hidden delay** and refresh immediately
when the user comes back, instead of sitting on a leftover 5 s or 30 s `Task.Delay`.

### 8.3 Who actually uses it

Only two UI pollers today. Both use the same pattern: delay scales with `State`; `BecameActive`
cancels the in-flight delay (`_wakeCts.Cancel()`), then the loop polls immediately.

#### Pull request badges (`WorkspaceRepositories.PrPolling`)

Runs for the lifetime of the Repositories page. Each tick refreshes **visible (virtualized) rows
only**, via ETagged GitHub GETs (304s are free).

| State | Interval |
|---|---|
| Active | 2_000 ms |
| Idle | 5_000 ms |
| Hidden | 30_000 ms |

Action-triggered refreshes (push, return-to-default, create PR) still fire immediately with `force:
true` and do not go through this loop.

#### GitHub Actions grid auto-poll (`WorkspaceActions.AutoRefresh`)

Runs **only while at least one row shows a running workflow**. When nothing is running the loop
exits; `EnsureAutoPollRunning` starts it again when a refresh sees a running line.

| State | Interval |
|---|---|
| Active | 2_000 ms |
| Idle | 5_000 ms |
| Hidden | 30_000 ms |

The comment on the Actions page is the design intent in one sentence: poll fast while the user is
looking, back off while idle, way down while backgrounded, and always wake immediately on resume so
a running workflow is not stuck on a stale badge for up to 30 seconds.

### 8.4 What does **not** use Active / Idle / Hidden

| Feature | What it uses instead |
|---|---|
| Git Changes background sweep | Fixed `WatcherRenewalIntervalMinutes`, plus Agent reconnect wake. Independent of whether the tab is Idle or Hidden. If the Git Changes page is still mounted, the workspace stays active even if the user has not touched the mouse for hours. |
| Git Changes watcher idle | Agent lease timer (`WatcherIdleGraceMinutes`), see Section 4.1 |
| Git Changes workspace activity | Page subscribe + `WorkspaceActivityGraceMinutes`, see Section 4.2 |
| GHA live terminal feed (`GhaWorkflowLiveFeedService`) | Delays named `PollIntervalActiveMs` (4 s) / `PollIntervalWaitingJobsMs` (8 s) / `PollIntervalIdleMs` (15 s) based on **whether jobs are in progress**, not on tab activity. "Idle" here means "workflow looks finished, poll slowly until the parent removes the terminal." |
| Workspace mutation lock (`IWorkspaceOperationRunner.TryStart`) | "Idle" means **no mutation is currently running for that workspace**. Unrelated to the user or to file watchers. |

Git Changes specifically does not back off when the tab is Hidden. Closing the page (or waiting out
the 8 minute activity grace) is what stops the sweep; hiding the tab does not.

---

## 9. Other "idle" timers (not AppActivityState)

Listed so they are not confused with Section 8.

### 9.1 Agent watcher idle grace

See Section 4.1. Timer on `GitRepositoryWatcherManager` after the last `GetGitChangeStatus` lease
release. Default 10 minutes. Disposes `FileSystemWatcher`s.

### 9.2 App Git Changes workspace activity grace

See Section 4.2. Timer-less: `IsActive` is computed from `LastActiveUtc` on each query. Default 8
minutes after the last Git Changes page subscriber leaves. Controls who the background sweep scans.

### 9.3 GHA live-feed "idle" poll delay

`GhaWorkflowLiveFeedService.DeterminePollDelayMs`:

- Any job `in_progress` / `queued` / `waiting`, or any step `in_progress` -> **4 s** (`PollIntervalActiveMs`)
- Jobs assigned but none of the above (typically all `completed`) -> **15 s** (`PollIntervalIdleMs`)
- No jobs yet -> **8 s** (`PollIntervalWaitingJobsMs`)
- Rate-limit pause -> wait until reset (+ jitter), not an activity tier

This is workflow-progress backoff, not user-idle backoff.

### 9.4 Workspace operation "idle"

`IWorkspaceOperationRunner.TryStart` starts work only when that workspace has no mutation in flight.
A second caller gets the existing operation and does not run. This serializes commits/pushes/updates
per workspace. It does not start or stop Git Changes watchers.

---

## 10. Quick answers

**What triggers a Git Changes tree update while I am looking at the page?**

A persisted snapshot broadcast (`GitChangesUpdated`), which itself was produced by (a) a debounced
watcher scan, (b) the 3-minute sweep, (c) the on-open warm-up, (d) manual Refresh, or (e) a
stage/unstage/discard/commit response.

**When are file watchers created?**

The first `GetGitChangeStatus` for that repository path after Agent start (or after that path last
idled out). That call is issued by warm-up, the background sweep, or Refresh - never by `LoadAsync`.

**When do file watchers go away?**

About 10 minutes after the last `GetGitChangeStatus` for that path, which in the normal "user left
Git Changes" path is about 10 minutes after the last sweep, which itself stops ~8 minutes after the
last viewer left. Agent process exit disposes them immediately. Agent SignalR disconnect does **not**
dispose them by itself, but it also stops lease renewal, so they will idle out if the disconnect
lasts long enough.

**Does Idle / Hidden in the browser stop Git Changes monitoring?**

No. Only leaving the Git Changes page (and waiting out `WorkspaceActivityGraceMinutes`) stops the
sweep. Idle / Hidden only slow PR-badge polling and GHA running-row auto-poll.
