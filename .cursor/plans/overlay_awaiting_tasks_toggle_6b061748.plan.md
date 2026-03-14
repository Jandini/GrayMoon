---
name: Overlay awaiting tasks toggle
overview: Add a toggle (default off) so that when on, the overlay shows "Awaiting x tasks" only after all app-side steps are complete (e.g. "9 of 9") and agent tasks are still pending; use workspace-scoped count and hide the number in the spinner via the same toggle.
todos: []
isProject: false
---

# Overlay "Awaiting x tasks" toggle (app-side done, agent pending)

## Clarified condition

Show **"Awaiting x tasks"** in the overlay only when **all three** are true:

1. **All app-side tasks are completed** – the progress message is in the form "X of Y" with **X == Y** (e.g. "Committed 9 of 9", "Pushed 2 of 2", "Synchronized 10 of 10"), not "3 of 9".
2. **There are agent tasks still to complete** – `AgentTasksPendingCount > 0` for the current workspace.
3. **Toggle is on** – `UseAwaitingTasksMessageInOverlay` is true (default false).

So while the app is still doing its part (e.g. "Committed 3 of 9"), the overlay keeps showing "Committed 3 of 9". Only when we reach "Committed 9 of 9" and the overlay is still visible because we're waiting on the agent do we switch the text to "Awaiting x tasks".

## Key files

- [WorkspaceRepositories.razor](src/GrayMoon.App/Components/Pages/WorkspaceRepositories.razor) – LoadingOverlay usages
- [WorkspaceRepositories.razor.cs](src/GrayMoon.App/Components/Pages/WorkspaceRepositories.razor.cs) – progress state, `AgentTasksPendingCount`, `OnQueueStateChanged`
- [LoadingOverlay.razor](src/GrayMoon.App/Components/Shared/LoadingOverlay.razor) – spinner, optional count in ring, Message display

## Implementation

### 1. Toggle and overlay message logic (WorkspaceRepositories)

- Add `**UseAwaitingTasksMessageInOverlay`** = **false** (toggle off by default).
- Add `**IsCompletedNOfNProgressMessage(string? msg)`**: return true only when the message matches "X of Y" **and** X equals Y (e.g. regex capture both numbers, then compare). Covers "Committed 9 of 9", "Pushed 2 of 2", "Synchronized 10 of 10", "Synchronized commits 5 of 5", "Synchronized 3 of 3 to default branch", etc.
- Add `**GetOverlayMessage(string progressMessage, bool overlayVisible)`**:
  - If overlay not visible, return `progressMessage`.
  - If `!UseAwaitingTasksMessageInOverlay`, return `progressMessage`.
  - If `!IsCompletedNOfNProgressMessage(progressMessage)`, return `progressMessage` (app still working – e.g. "3 of 9").
  - If `AgentTasksPendingCount == 0`, return `progressMessage` (no agent tasks to wait for).
  - Otherwise return **"Awaiting {AgentTasksPendingCount} task(s)"**.
- Use `GetOverlayMessage(...)` for the Message passed to LoadingOverlay for: sync, push, update, commit sync, sync-to-default. Other overlays keep their static message.

### 2. LoadingOverlay: workspace count and hide number in spinner

- Add `**[Parameter] public int? PendingAgentTasksCount { get; set; }`** for workspace-scoped count.
- Add `**[Parameter] public bool ShowTaskCountInSpinner { get; set; } = false`** (default off).
- Show the number inside the spinner only when `IsVisible && ShowTaskCountInSpinner && (PendingAgentTasksCount ?? GetTotalPendingCount()) > 0`.

### 3. Wire parameters in WorkspaceRepositories.razor

- Pass `**PendingAgentTasksCount="@AgentTasksPendingCount"**` and `**ShowTaskCountInSpinner="@UseAwaitingTasksMessageInOverlay"**` to workspace-scoped LoadingOverlays.
- For sync, push, update, commit sync, sync-to-default, pass `**Message="@GetOverlayMessage(..., ...)"**`.

No backend or handler changes; only the displayed overlay message and optional spinner count are driven by the page.