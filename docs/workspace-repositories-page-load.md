# Workspace Repositories page: why the grid sometimes renders late

Page: `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.razor` (+ `.razor.cs`, `.Loading.cs`, `.Display.cs`, `.Realtime.cs`).
Route: `/workspaces/{WorkspaceId}`.

> **Status:** this is a code-reading analysis. Nothing was profiled or measured. The causes below are
> ranked by how likely they are to produce a *multi-second, intermittent* delay. Section 5 proposes
> measuring first.

## 1. What happens today, step by step

The page is `InteractiveServerRenderMode(prerender: false)`, so the browser first gets an empty shell. Nothing
of the page is rendered until the Blazor circuit (WebSocket + JS boot) is up. Then `OnParametersSetAsync` runs
and **awaits everything below, in sequence, before the first paint**. The comment in the code says this is
deliberate ("no Branch<->Create PR or column jump"):

| # | Step | Where | Cost type |
|---|------|-------|-----------|
| 0 | Browser loads shell, boots Blazor, opens circuit | framework | network + JS |
| 1 | `GetHeaderAsync` (workspace row) | `LoadWorkspaceHeaderAsync` | SQLite |
| 2 | `CapabilitiesResolver.GetAsync` | same | SQLite |
| 3 | `RefreshWorkspaceRepositoryBannerStateAsync`: `AnyAsync(...)`, **then `WorkerFeatureSupport.SupportsAsync`** | `Display.cs:32` | SQLite **+ Worker round trip** |
| 4 | `ContextNavigation.ResolveForPageAsync` (several context/selection reads, and a write via `SetSelectedAsync`) | `ResolveSelectedContextAsync` | SQLite (read + write) |
| 5 | `LoadClientPreferencesAsync` (JS interop) | `OnParametersSetAsync` | circuit round trip |
| 6 | `LoadPendingRestoreScrollTopAsync` (JS interop) | same | circuit round trip |
| 7 | `GetHeaderStateAsync`: about 10-12 separate `AnyAsync`/`CountAsync`/`ToListAsync` queries, each with its own `DbContext` | `LinkListQueryService` | SQLite, sequential |
| 8 | `CountAsync` (duplicates the count already done in step 7) | `ResetAndLoadFromTopAsync` | SQLite |
| 9 | `GetIndexAsync` (all rows, sorted, with a correlated subquery per row in Feature context) | same | SQLite |
| 10 | `BuildSlots`, then `GetByIdsAsync` for the initial visible window | `EnsureSlotsHydratedAsync` | SQLite |
| 11 | Render. The table is gated on `workspace != null && hasLoadedOnce && !isInitialLoading` (`.razor:121`) | | |
| 12 | After render: `AttachVirtualScrollAsync` (JS interop), SignalR hub connect, PR polling loop starts | `OnAfterRenderAsync` | async, after paint |

There is **no loading placeholder** in the body: while steps 1-10 run, the page shows only the header chrome
(workspace name empty), and no table. A stale comment at `.razor:539` still says "Initial open uses thead +
in-grid 'Loading repositories...'", but that text no longer exists in this page. It was lost during the Feature
work (commit `cc0e7b3`). So the user sees an empty area for as long as the whole chain takes.

## 2. What is most likely causing the *occasional* multi-second delay

The delay is described as "sometimes", which points to something conditional or cached, not to plain SQLite
reads (those are normally milliseconds).

### Cause A (most likely): Worker round trip inside the page-load path
`Display.cs:32-41`:

```csharp
_hasWorkspaceRepositoryLink = await db.WorkspaceRepositories...AnyAsync(... Role == Workspace);
_workerSupportsWorkspaceRepository = !_hasWorkspaceRepositoryLink
    || await WorkerFeatureSupport.SupportsAsync(WorkerFeatures.WorkspaceRepository);
```

For any workspace that has a Workspace-role repository, the page **blocks on a `GetHostInfo` command to the
Worker**. `WorkerFeatureSupportService` caches the answer for only **60 seconds**. So:

- Open the page within 60 s of a previous open: instant (cache hit).
- Open it after a pause: the call goes out to the Worker over SignalR and the process boundary.
- If the Worker is busy (running a sync/fetch/push, queue backed up), `GetHostInfo` waits in line behind that work.
- Worst case, the Worker is disconnected or unresponsive. A disconnected Worker returns immediately (not cached),
  but an unresponsive one waits for `CommandTimeoutSeconds`, which is **240 s** by default.

This matches "sometimes a few seconds" very well, and it is used only to decide whether to show a rarely-needed
banner ("Worker does not support Workspace repositories"). It should not block the grid.

### Cause B: Blazor Server cold start (`prerender: false`)
On a fresh load, hard refresh, or Desktop app start, the user waits for the circuit before *any* page code runs.
This is a fixed cost every time the browser connects, and it is worse on the first navigation after the
App/Desktop starts. In-app navigation between pages does not pay it again.

### Cause C: Serial work and no parallelism in the critical path
Steps 1-10 are strictly sequential, and every step opens a new `DbContext` and a SQLite connection.
`GetHeaderStateAsync` alone is about a dozen queries. Several are independent of each other and of the grid index
(header, capabilities, banner state, context resolution, header state, count, index). Each is small, but they add
up, and they get worse with more repositories, in Feature context (correlated subqueries per row), or while the
Worker or sync is writing to the same SQLite file (writers can block readers, depending on the journal mode).

### Cause D: Extra circuit round trips (steps 5 and 6)
Two JS-interop calls (`graymoonStorageGet`, `graymoonSessionStorageGet`) are awaited before first paint. Each
is a network round trip over the circuit. That is small on localhost, but it becomes noticeable over a
slow link or while the circuit is busy.

### Cause E: Duplicated and wasteful work
- `CountAsync` repeats `totalCount`, which `GetHeaderStateAsync` already computes.
- `ResolveForPageAsync` performs a **write** (`SetSelectedAsync`) on every open, even if the selection is unchanged.
- `GetOrCreateSpecialWorkspaceContextIdAsync` can insert rows on first use.

### Cause F: No feedback while waiting
Regardless of the cause, the user sees a blank body. Even a 700 ms wait feels longer than it is when nothing
indicates progress. This is the main *perceived* problem and it is the cheapest to fix.

## 3. Improvement ideas

Ordered roughly by value-to-effort. Items 1-3 together probably remove most of what the user feels.

### 1. Take the Worker call off the critical path (fixes Cause A)
Do not await `SupportsAsync` before first paint. Options:
- **a. Fire and forget, then re-render.** Render the grid immediately; start the check in the background, and when
  it returns, set `_workerSupportsWorkspaceRepository` and `StateHasChanged`. The banner appears a moment later
  only in the rare unsupported case. The default is already "supported", so there is no layout shift in the common case.
- **b. Prefetch and long-lived cache.** Have the App cache the Worker feature list when the Worker *connects*
  (it already tracks connection in `WorkerConnectionTracker`) and invalidate on reconnect/disconnect, instead of
  a 60 s TTL. The page then reads a value from memory and never waits.
- **c. Short timeout** (about 1 s) on this specific call, treating a timeout as "unknown, assume supported".

Recommendation: **a + b**. The banner is informational and must never gate content.

### 2. Show a skeleton immediately (fixes Cause F, hides B-D)
Render the page chrome and a table skeleton (header row with the final column layout and N shimmer rows) as soon as
the component exists, then swap in real rows. This is what the dead comment at `.razor:539` intended.
- Problem the code already avoids: column jumping. The skeleton must use the same column set. The columns depend on
  `_presentation`, which comes from `_capabilities`, i.e. one cheap SQLite read. So load **capabilities first**
  (step 2) and show the skeleton with the correct columns, then load the rest.
- Keep the workspace-name placeholder behaviour that exists today.

### 3. Split the critical path into "first paint" and "everything else"
Render the grid as soon as it has what it truly needs: capabilities (columns), the context, and the first window of
rows. Defer everything else:

| Needed before first paint | Can load after (re-render when ready) |
|---|---|
| header row, capabilities, selected context, index + first hydrated window | `GetHeaderStateAsync` (toolbar flags, counts), banner state, JS preferences, scroll-restore (only if possible, see below), SignalR connect, PR polling |

Toolbar buttons that depend on `_headerState` (Push, Create PR, etc.) can render disabled or with a small
placeholder, then enable. Note this trades the "no jump" goal for speed. Reserve the space (fixed-size slots) to
avoid layout shift.

### 4. Run independent loads in parallel
After the workspace header is loaded, `capabilities`, `context resolution`, `header state`, `count` and `index` do
not depend on each other (header state needs capabilities and context, so two stages, not seven). Use
`Task.WhenAll`. This is safe here because every query service already creates its own `DbContext` from the
factory (see `AGENTS.md` "DbContext handling"). Expected gain: several sequential SQLite round trips become about
two stages.

### 5. Make the JS reads cheaper (fixes Cause D)
- Read `sync-mode` and scroll offset in **one** JS call returning both, or
- do it in `OnAfterRenderAsync(firstRender)` after first paint. The sync-mode preference only changes which button is
  primary, so a late update is invisible. Scroll offset *is* needed to choose the first window, so keep it, but
  merge it into the single call.

### 6. Trim redundant DB work (Cause E)
- Reuse `_headerState.TotalCount` for `totalCount` when no search filter is active, and drop the extra `CountAsync`.
- In `GetHeaderStateAsync`, fold the ~10 `AnyAsync` calls into **one** query that returns all flags (e.g. a single
  `GroupBy`/conditional aggregate: `SUM(CASE WHEN ... THEN 1 ELSE 0 END)`). SQLite handles this in one scan.
- Skip `SetSelectedAsync` when the stored selection already equals the resolved one.

### 7. Cache across navigations
Keep a per-circuit (or per-workspace) cache of the last loaded index + header state. When the user comes back to
the page, **paint from the cache instantly** (stale-while-revalidate) and refresh in the background. The page
already has the machinery for background refresh (`RefreshVisibleRowsAsync`, SignalR `WorkspaceSynced`).
This gives an "instant" feel on repeat visits, at the cost of a short flash of possibly stale data.

### 8. Reduce Blazor cold-start cost (Cause B)
- Enable prerender with a static **skeleton** only (not the full data load), so the user sees the layout while the
  circuit connects. Requires the page to avoid doing DB/JS work in the prerender pass, or to use
  `PersistentComponentState`.
- Or, for the Desktop shell, keep the circuit warm: navigate the WebView to the app earlier, or pre-load the page
  hidden. This is a Desktop-side decision.

### 9. SQLite hygiene (only if measurement points here)
- Confirm WAL mode (`PRAGMA journal_mode=WAL`) so readers do not wait on the Worker/sync writer.
- Check `EXPLAIN QUERY PLAN` for the Feature-context index query (correlated subquery per row) and for the
  `WorkspaceRepositoryContextStates` index on `(WorkspaceFeatureContextId, WorkspaceRepositoryId)`.

## 4. Options at a glance

| Option | Effort | User-visible effect | Risk |
|---|---|---|---|
| **Minimum:** 1a (async worker check) + 2 (skeleton) | Small | Delay hidden or removed in the Worker case; never a blank body | Low; banner may appear late (rare) |
| **Recommended:** above + 4 (parallel) + 5 + 6 | Medium | Real reduction of the remaining load time | Low-medium; needs care with ordering/cancellation |
| **Maximum:** above + 3 (deferred toolbar) + 7 (SWR cache) + 8 (prerender skeleton) | Large | Near-instant on repeat visits; fast cold start | Medium; stale data and layout-shift handling |

## 5. Suggested next step: measure before choosing

To confirm Cause A and put numbers on the rest, add temporary `Stopwatch` logging (or `Activity`/`ILogger` debug
timings) around each step in `OnParametersSetAsync` / `ResetAndLoadFromTopAsync`:
`header`, `capabilities`, `banner (incl. worker)`, `context`, `prefs js`, `scroll js`, `headerState`, `count`,
`index`, `hydrate`, and total time from `OnInitialized` to first render. Then reproduce the slow case: open the page,
wait over 60 s, open it again. If the slow open lines up with the `banner` step, Cause A is confirmed
and idea 1 alone should remove the "sometimes".

## 6. Decisions needed from you

1. Is a short-lived skeleton acceptable, or must the final layout appear in a single paint (the current intent)?
2. Is it acceptable for toolbar state (Push/Create PR flags) and the Worker-support banner to appear a moment
   *after* the grid?
3. Do you want stale-while-revalidate on repeat visits (idea 7), or always-fresh data?
4. Do you want me to add the timing instrumentation first (section 5), or go straight to the recommended option?
