# TODO: Changes nav notification dot

Status: **implemented** on `change-watcher` with option 1 (selected context); manual
verification (section 7) pending. Originally pulled from the `install-worker` release.

## Goal

Show the red `nav-notification-dot` on the **Changes** item in the workspace sidebar whenever the
workspace has uncommitted work, and keep it live as the Agent's file watchers push new snapshots -
without the user having to open Repositories or Changes first.

"Uncommitted work" = at least one repository with `StagedCount > 0`, `ChangedCount > 0`, or
`ConflictCount > 0` in the persisted Git Changes projection.

## Why it was deferred

A working version was built on `install-worker` and then removed, because `worktree` rewrites the
same subsystem:

- **Per-context storage.** `worktree` persists snapshots per feature context
  (`WorkspaceGitContextRepositoryStatuses` / `WorkspaceGitContextChangeEntries`). The legacy
  `WorkspaceGitRepositoryStatuses` table is only dual-written for the special Workspace context.
  A dot reading the legacy table would miss changes in every other worktree context.
- **Broadcasts.** `GitChangesSnapshotPushHandler` now sends `ContextGitChangesUpdated(workspaceId,
  contextId, repositoryId)` for every snapshot and `GitChangesUpdated(workspaceId, repositoryId)`
  only for the special Workspace context.
- **Activity leases.** `worktree` adds `WorkspaceGitChangesActivityBinder` (mounted in
  `MainLayout`) + `WorkspaceGitChangesRouteActivity`, so any `/workspaces/{id}/...` page keeps the
  workspace active and Agent watchers warm. On `install-worker` only Repositories and Changes did,
  so the dot went stale roughly 18 minutes after navigating to Projects/Files/Deps/Actions
  (8 min `WorkspaceActivityGraceMinutes` + 10 min `WatcherIdleGraceMinutes`).
- **NavMenu.** `worktree` changes the Changes link to `WorkspaceHref("changes")` (carries
  `?context=`), which conflicts textually with the markup change.

Building it on `worktree` avoids a redesign after the merge.

## Recommended changes from the `install-worker` review

The first implementation was reviewed before removal. These are the findings and what this plan
does about each one.

| # | Finding on `install-worker` | Recommendation | Where in this plan |
| --- | --- | --- | --- |
| R1 | Watchers only stay warm while Repositories or Changes is open, so the dot goes stale on other workspace pages after ~18 minutes. | **Already solved on `worktree`** by `WorkspaceGitChangesActivityBinder` + `WorkspaceGitChangesRouteActivity`. The dot must **not** take its own activity lease; rely on the route binder. Verify it in manual test 2. | "What already exists", step 7.2 |
| R2 | Each dot opened its own server-side SignalR `HubConnection` back to `/hubs/workspace-sync`: an extra loopback connection per circuit, and a failed first `StartAsync` never retried, so the dot silently stopped updating. | Replace with an in-process singleton `IWorkspaceGitChangesNotifier`, published by `GitChangesSnapshotPushHandler` next to the `ContextGitChangesUpdated` broadcast. No hub connection in the dot. | Step 2, step 3 |
| R3 | `WorkspaceRepositoryLinkCleanup` deletes status rows without any broadcast, so the dot stayed on after repositories were removed until the next navigation. | Publish from the cleanup path for each affected workspace (all-contexts sentinel). `WorkspaceRepositoryLinkCleanup.cs` is unchanged on `worktree`, so this is a low-conflict edit. | Step 2, step 6 (cleanup test) |
| R4 | `.nav-notification-dot` CSS was copied into a second component (`ChangesNotificationDot.razor.css`). | Keep a single copy: move `.nav-notification-dot` into `NavMenu.razor.css` under `::deep` and delete the per-component copies, or at most keep the one in `AgentUpgradeNotificationDot`. Do this on `worktree`, where the Home dot already lives, so there is no merge cost. | Step 3 (CSS) |
| R5 | The dot read the legacy `WorkspaceGitRepositoryStatuses` table and listened only for `GitChangesUpdated`. On `worktree` both only cover the special Workspace context. | Read `WorkspaceGitContextRepositoryStatuses` and react to per-context publishes. Decide selected-context vs any-context semantics before coding. | "Design decision needed first", step 1 |
| R6 | A repository scan publishes one update per repository back-to-back, so each one triggered its own DB query. | Debounce refreshes in the component (~250 ms) and only re-render when the boolean flips. | Step 3 |
| R7 | NavMenu's Changes link differs between branches (`workspaces/{id}/changes` vs `WorkspaceHref("changes")`). | Build on `worktree` using `WorkspaceHref("changes")` and pass the parsed `?context=` to the dot. | Step 4 |

## What already exists (reuse, don't rebuild)

| Piece | Where | Notes |
| --- | --- | --- |
| Dot styling | `Components/Shared/AgentUpgradeNotificationDot.razor.css` | `.nav-notification-dot`, absolute, `#ff3b30` |
| Icon wrapper | `NavMenu.razor` / `NavMenu.razor.css` / `MainLayout.razor.css` | `<span class="nav-link-icon">` with `position: relative`; collapsed-sidebar rules already cover `.nav-link-icon` |
| Watchers stay warm on any workspace page | `WorkspaceGitChangesActivityBinder` (worktree) | No extra lease needed from the dot |
| Snapshot write path | Agent `GitChangesSnapshotPublisher` -> `AgentHub.GitChangesSnapshotUpdated` -> `WorkspaceGitChangesWriteQueue` -> `GitChangesSnapshotPushHandler` | Watcher pushes, sweeps, warm-up scans and manual Refresh all go through here |
| Selected context | `IWorkspaceSelectedFeatureContextService`, `?context=` query in NavMenu | Navigation preference only |

## Design decision needed first

**What does "has changes" mean when a workspace has several contexts?**

1. **Selected context** (recommended): dot reflects the context the Changes link will open
   (`?context=` or `IWorkspaceSelectedFeatureContextService.GetSelectedAsync`, falling back to the
   special Workspace context). Clicking the dot always lands on a page that shows those changes.
2. **Any context**: dot shows if any context of the workspace has changes. More "nothing is
   forgotten", but clicking may open a context that is clean.

Tooltip should say which, e.g. `Uncommitted changes` vs `Uncommitted changes in 2 worktrees`.

## Implementation plan

### 1. Read side - `WorkspaceGitChangesReadService`

Add to `IWorkspaceGitChangesReadService`:

```csharp
/// <summary>True when the context has any staged, unstaged, or conflicted file.</summary>
Task<bool> HasAnyChangesAsync(int workspaceId, WorkspaceFeatureContextId contextId, CancellationToken cancellationToken = default);

// Only if option 2 is chosen:
Task<int> CountContextsWithChangesAsync(int workspaceId, CancellationToken cancellationToken = default);
```

- Query `WorkspaceGitContextRepositoryStatuses` joined to `WorkspaceRepositories` on
  `WorkspaceRepositoryId`, filtered by `WorkspaceId` (and `WorkspaceFeatureContextId` for option 1),
  `AnyAsync(s => s.StagedCount > 0 || s.ChangedCount > 0 || s.ConflictCount > 0)`.
- Use `IDbContextFactory<AppDbContext>` per call (AGENTS.md DbContext rule) - the service already does.
- Do **not** read the legacy table; it is only authoritative for the special Workspace during cutover.

### 2. In-process change notifier (replaces a per-dot SignalR loopback)

The first attempt opened a server-side `HubConnection` back to `/hubs/workspace-sync` per circuit.
That duplicates the page connections, and a failed first `StartAsync` never retries. Instead:

- New singleton `IWorkspaceGitChangesNotifier` in `Services/GitChanges`:
  ```csharp
  public interface IWorkspaceGitChangesNotifier
  {
      event Action<int /*workspaceId*/, int /*contextId*/>? Changed;
      void Publish(int workspaceId, int contextId);
  }
  ```
- Call `Publish` in `GitChangesSnapshotPushHandler.HandleAsync` right next to the
  `ContextGitChangesUpdated` broadcast (after the transaction commits).
- Call it from `WorkspaceRepositoryLinkCleanup` after deleting status rows (repos removed from a
  workspace), per affected workspace - otherwise the dot stays on until the next navigation.
  Use context id `0` / a sentinel meaning "all contexts of this workspace".
- Register as singleton in `Program.cs` next to the other Git Changes services.
- Handlers run on the write-queue worker thread - subscribers must marshal with `InvokeAsync`.

### 3. Component - `Components/Shared/ChangesNotificationDot.razor`

- `@rendermode @(new InteractiveServerRenderMode(prerender: true))` - NavMenu is static SSR, so the
  dot must be its own interactive island (same as `AgentUpgradeNotificationDot`).
- Parameters: `WorkspaceId` (int) and, for option 1, `ContextId` (int?) parsed from the current
  URI's `?context=`; resolve null via `IWorkspaceSelectedFeatureContextService` / special context.
- `OnParametersSetAsync` -> refresh. Enhanced navigation updates parameters of the SSR-rendered
  interactive root when switching workspace/context; guard stale results with a generation counter
  and a `workspaceId == WorkspaceId` check after the await.
- Subscribe to `IWorkspaceGitChangesNotifier.Changed` only when `RendererInfo.IsInteractive`;
  filter by workspace (and context for option 1); `InvokeAsync(RefreshAsync)`.
- Debounce refreshes (~250 ms) - a scan of N repositories publishes N events back-to-back.
- Only call `StateHasChanged` when the boolean flips.
- `IDisposable`: unsubscribe, bump generation.
- Markup identical to the Home dot:
  ```razor
  <span class="nav-notification-dot" title="@Title" aria-label="@Title" role="status"></span>
  ```
- CSS: either copy `AgentUpgradeNotificationDot.razor.css` into `ChangesNotificationDot.razor.css`,
  or (cleaner) move `.nav-notification-dot` into `NavMenu.razor.css` under `::deep` and delete both
  per-component copies. Pick one; do not leave three copies.

### 4. NavMenu wiring

```razor
<div class="nav-item px-3" data-nav-label="Changes">
    <NavLink class="nav-link" href="@WorkspaceHref("changes")" Match="NavLinkMatch.All" aria-label="Changes">
        <span class="nav-link-icon">
            <i class="bi bi-git" aria-hidden="true"></i>
            <ChangesNotificationDot WorkspaceId="workspaceId.Value" ContextId="contextId" />
        </span>
        <span class="nav-link-text">Changes</span>
    </NavLink>
</div>
```

- NavMenu already parses `?context=` into `contextQuerySuffix`; expose the parsed int as
  `contextId` too (or reuse `WorkspaceRouteParser` + a small query helper).
- Workspace-only: the dot lives only in the `isWorkspaceView` branch.

### 5. Edge cases

- **Agent offline**: persisted counts are last-known state. Keep showing them (the Changes page does
  the same). Optionally dim or hide when `AgentConnectionTracker.State != Online` - decide with UX.
- **No snapshot yet** (workspace never scanned): no rows -> no dot. The route binder's cold-start
  warm-up scan will populate and publish.
- **Snapshot errors** (`LastErrorCode` set): not "changes"; do not show the dot for errors alone.
- **Stale version rejected** by the push handler: no publish, no refresh - correct.
- **Commit/stage/unstage from the Changes page**: Agent post-mutation refresh pushes a snapshot ->
  publish -> dot updates. Verify committing everything clears the dot.
- **Multiple tabs**: each circuit has its own dot; the singleton notifier fans out to all.

### 6. Tests (`GrayMoon.App.Tests`)

Read service (extend `WorkspaceGitChangesReadServiceTests`, seed via the push handler):
- no rows -> false
- clean snapshot -> false
- unstaged only / staged only / conflict only -> true
- other workspace -> false
- option 1: changes in context A, query context B -> false
- option 2: count across contexts

Notifier / push handler (extend `GitChangesSnapshotPushHandlerTests`):
- successful persist publishes once with workspace + context
- stale snapshot rejected -> no publish
- unknown path (attributor returns null) -> no publish

Cleanup:
- removing workspace repositories publishes for that workspace

### 7. Manual verification

1. Open a workspace on **Projects** (not Repositories/Changes). Edit a tracked file in a repo ->
   dot appears within the watcher debounce, no navigation needed.
2. Stay there > 20 minutes, edit again -> dot still updates (route binder keeps watchers warm).
3. Stage everything, then commit from Changes -> dot clears.
4. Collapse the sidebar -> dot sits on the icon corner.
5. Switch workspace via the top bar -> dot reflects the new workspace, no flicker of the old state.
6. Worktree context: create changes only in a non-default context; confirm behaviour matches the
   chosen option (1: dot only when that context is selected; 2: dot always).
7. Stop the Agent -> dot keeps last-known state (or the chosen offline behaviour).

### 8. Docs / release notes

- Add a bullet to `../GrayMoon.Desktop/README.md` "Recent GrayMoon changes" (AGENTS.md rule).
- Mention in `docs/architecture/04-runtime-communication-and-concurrency.md` that
  `IWorkspaceGitChangesNotifier` is the in-process fan-out for persisted snapshot changes.

## Estimate

About half a day on `worktree`: read-service query + notifier (~1 h), component + NavMenu (~1-2 h),
tests (~1-2 h), manual verification (~1 h). Most of the risk is the context semantics decision, not
the code.
