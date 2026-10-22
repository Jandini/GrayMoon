# Push Updated stops at Level 3, and browser circuits crash with "needs to reload"

Investigated 2026-10-23 on Workspace 42 (`C:\Workspace\Edx1`, `http://localhost:8384/workspaces/42`). All times are +05:30. Logs read: `C:\ProgramData\GrayMoon\Logs\graymoon-app-20261023.log`, `graymoon-worker-20261023.log`, and `%LOCALAPPDATA%\GrayMoon\Logs\desktop-20261023.log`.

Four findings, in the order the user hit them (the fourth, section 5, is why the timeout was not visible on screen):

1. Push Updated stopped at Level 3 because the package wait ran out before GitHub had even started the CI run that publishes the package. The csproj parser is correct.
2. Browser sessions crash the Blazor circuit ("GrayMoon needs to reload") because a real-time hub connection fails to start inside `OnAfterRenderAsync` and nothing catches it.
3. The "Open in" buttons paint and then vanish in a browser because the App decides "Desktop" per process, not per client.

Nothing was changed in code. Section 4 lists the fixes.

## 1. Why Push Updated stopped at Level 3

### What happened

| Time | Event |
|---|---|
| 00:30:03 | Commit `55d5d10` made in `Edx1.FileSystem` (the producer of `Edx1.FileSystem.Abstractions`). |
| 00:30:14 | Push Updated starts (op `fbfb899a`). |
| 00:30:33 | Level 2: `git push origin multirepo-next` for `Edx1.FileSystem`. |
| 00:30:41 | Level 3 starts for `Edx1.Disk` (338) and `Edx1.FileSystem.Linux` (347). They need 3 packages: `Edx1.IO@.6`, `Edx1.IO.Abstractions@.6`, `Edx1.FileSystem.Abstractions@0.1.0-multirepo-next.7`. Two exist already. |
| 00:35:42 | Wait times out: "Found 2 of 3". Level 3 and everything above it is blocked. `Edx1.FileSystem.Abstractions@.7` is `VersionNotFound` after 106 lookups. Diagnostics say `WorkflowNotFound`, `PublicationStatus=Unverified`. |
| 00:37:29 | GitHub creates the workflow run for `55d5d10` (run 39837340748). **7 minutes after the push.** |
| 00:38:35 | The run publishes `Edx1.FileSystem.Abstractions 0.1.0-multirepo-next.7` (package `created_at` 19:08:35Z). |
| 00:38:35 | The second run (Push Synchronized, op `d48f6f4f`) finds all 3 packages for Level 3 at once and carries on through Level 6. |

So nothing was wrong with the repositories, the version numbers or the registry lookups. The package simply did not exist for 8 minutes after the push, because GitHub started the build late. GrayMoon cannot see why, and that part is outside GrayMoon.

### Why GrayMoon gave up

`WorkspacePushService.cs` (the package wait at about lines 349-527):

- The wait window starts at `requiredPackages x PushWaitDependencyTimeoutMinutesPerDependency` (default 5.0 min, `WorkspaceOptions.cs:15`). Here that is 3 x 5 = 15 min, which would have been enough (it would have ended at 00:45).
- When some packages are found, the window is **reset to `stillWaiting x 5 min`** (line 508-510). The two old packages were found on the first poll, so at about 00:30:42 the deadline moved from 15 min to 5 min.
- The one package still missing was the one this same operation had just pushed, the only one that depends on CI. A 5 min budget for "GitHub starts a run, builds, tests and publishes" is tight even when GitHub is prompt.
- `ciRunWatch.TickAsync(...)` only draws the CI terminal. The wait is, and stays, registry-based (see the P2 note in section 4).

The same `PackageWaitTimeout` also ended runs on 2026-10-21 at 22:17 (Level 7, 602 s) and 22:38 (Level 2, 602 s). Those were not analysed here, but the shrinking window and the blind wait apply to them too.

### Did the parser read the csproj files correctly? Yes

Level 3 is `Edx1.Disk` (`Edx1.Disk.Partition`) and `Edx1.FileSystem.Linux` (Btrfs, Erofs, Ext4, F2fs, Squashfs, Xfs). Each project has `<PackageReference Include="Edx1.FileSystem.Abstractions" />` with no `Version`, and each repository's `Directory.Packages.props` pins `Edx1.FileSystem.Abstractions` to `0.1.0-multirepo-next.7` (central package management). GrayMoon's expected version (`.7`, source `ProjectDependency.Version`), the producer (`Edx1.FileSystem`, project `src\Edx1.FileSystem.Abstractions\...csproj`, level 2) and the required-package count (3) all match the files. The registry then published exactly `.7`.

## 2. Why the browser circuit crashes

### The visible symptom

`MainLayout.razor` shows `#blazor-error-ui` ("GrayMoon needs to reload") when Blazor ends a circuit because of an unhandled exception. Four circuits ended that way today, all after 00:37, none on 21-22 Oct:

| Time | Page | Seen just before the crash (Desktop log) |
|---|---|---|
| 00:37:41.989 | `WorkspaceRepositories` | `Workspace changed: 42 Edx1` at 00:37:41.867 |
| 00:38:22.185 | `WorkspaceRepositories` | `Workspace changed: 42` at 00:38:22.146 |
| 00:40:37.219 | `WorkspaceActions` | `Workspace changed: 42` at 00:40:37.191 |
| 00:44:11.165 | `WorkspaceGitChanges` | (same pattern) |

Every one has the same exception: `AggregateException: Unable to connect to the server with any of the available transports (WebSockets/ServerSentEvents/LongPolling failed: A task was canceled)`, thrown from `HubConnection.StartAsync` inside `OnAfterRenderAsync`.

### The defect (certain)

Four components open a SignalR connection to the App's own `/hubs/workspace-sync` from inside the circuit:

| Component | Start call | Guarded? |
|---|---|---|
| `WorkspaceRepositories.Realtime.cs:96` | `await _hubConnection.StartAsync();` | **No** |
| `WorkspaceActions.razor.cs:124` | `await _hubConnection.StartAsync();` | **No** |
| `WorkspaceGitChanges.Realtime.cs:36` | `await _hubConnection.StartAsync();` | **No** |
| `WorkspaceActionNotificationPanel.razor:511` | `try { await StartAsync(); } catch { LogDebug }` | Yes |

`WithAutomaticReconnect()` does not cover a failed first start. An exception from `OnAfterRenderAsync` is fatal to the circuit, so a live-update nicety takes the whole session down. The notification panel shows the right pattern: at 00:37:41.999 it failed in the same circuit, logged at debug and carried on.

### The trigger (strong evidence, not proven)

- One task token cancels all three transports within 3 ms. That is the signature of the connection being stopped or disposed while `StartAsync` is still in flight (the page's `Dispose` calls `DisposeFireAndForget`, which calls `StopAsync`), not three separate network timeouts.
- `StartAsync` has a long window to be caught in. The browser uses `http://localhost:8384`, so the circuit connects to itself at `localhost`. Kestrel in Desktop mode listens on `127.0.0.1` only. Measured now: a negotiate to `localhost:8384` takes about 2,400 ms (Windows tries `::1`, waits about 2 s for the refusal, then uses IPv4); `127.0.0.1:8384` takes 12 ms. The Desktop WebView uses `http://127.0.0.1:8384` (Desktop log: "GrayMoon.App is ready at http://127.0.0.1:8384"), so it never has this window. That fits the report: browser sessions crash, the Desktop window does not.
- Each crash is 30-120 ms after the Desktop log shows `Workspace changed: 42`. That line is written when any circuit lands on a `/workspaces/42` route (see section 3), so it marks a new page load or navigation. A reload or navigation while the previous page's `StartAsync` is still waiting out the 2 s `::1` delay cancels it and throws.

What is not proven: whether the cancelled connection belonged to a circuit the user was still looking at in each case. The code defect is certain; the exact race needs the diagnostic in section 4 (C5) or a repro (open `http://localhost:8384/workspaces/42`, press F5 or switch page within 2 s, several times, with the Desktop app running).

### Does Desktop cause it?

Not directly. Desktop never connects to this hub and the failed connection is made by the circuit. Desktop's part:

- The App process is Desktop-owned and binds only IPv4 loopback, which creates the slow `localhost` path for browsers.
- Any circuit, browser or WebView, running `WorkspaceTopBarService.ApplyNavigationAsync` pushes `WorkspaceChanged` to the shared `DesktopWorkspaceContextTracker` singleton and to all Desktop hub clients. A browser tab on workspace 42 therefore rewrites the Desktop window title and tray label too (last writer wins). This is a side effect, not the crash.

## 3. Why the "Open in" tools render and then disappear

`WorkspaceFeatureSelector.razor` decides "Desktop" in two incompatible ways:

1. First paint: `ApplyDesktopOpenInState()` (line 453-461) sets `_desktopAvailable = true` and loads installed tools whenever `Configuration["Desktop:PipeName"]` is set. That is a **process** setting: the App was launched by Desktop. It is true for every client of that App, including a plain browser tab.
2. After the first render: `OnAfterRenderAsync` (line 508-530) asks the browser `window.graymoonDesktop.isAvailable()`. In a plain browser the answer is false, so `_desktopAvailable` is set back to false and the buttons are removed.

So in a browser the buttons are drawn from the server (for the "no flicker in Desktop" goal) and then taken back. The JS probe is wrapped in `try/catch`, so it cannot crash the circuit. It is the same underlying problem as section 2, though: the App cannot tell a Desktop WebView client from a browser client of the same Desktop-owned process, so the code paints first and corrects later. A circuit-time (not process-time) answer is needed.

Only one other place, `Worker.razor:265`, asks the client with `graymoonDesktopBridge.isAvailable`, so a different probe name is also in use there.

## 4. Required fixes

### Push wait

**P1 (required). Never shrink the wait below the time already left.** In the reset at `WorkspacePushService.cs:505-511`, use `max(remaining, stillWaiting x minutesPerDep)`, or do not reset at all when a package is found. Add a unit test: 3 required, 2 found on the first poll, assert the deadline is not earlier than the original.

**Design constraint: GrayMoon is not CI-aware and must not be tied to GitHub Actions.** The package may be published by any build system. The wait is decided only by what the package registry reports, and no fix below may query, assume or name a CI product (runs, workflows, head SHAs). Observations about GitHub in section 1 describe this one incident, not a behaviour GrayMoon relies on.

**P2 (dropped).** A CI-aware wait was considered and rejected for the reason above.

**P3 (required, registry only). Say why it stopped.** The timeout message names the packages not yet in the registry and says the build or publish may not have finished, then "Run Push Synchronized again to continue". No CI wording. Implemented in `PackageWaitTimeout.FormatMessage`.

**P4 (optional). A "Continue waiting" action** on the failed-level callout that resumes the same operation at that level without re-pushing. A future configurable timeout covers slow publishers.

**P5. Configuration.** Document `PushWaitDependencyTimeoutMinutesPerDependency` in the user wiki and state it is per package, not per level.

### Circuit crash

**C1 (required). Guard every hub start.** Add `WorkspaceSyncHubConnectionHelper.TryStartAsync(HubConnection, ILogger, CancellationToken)` that catches `OperationCanceledException`, `ObjectDisposedException`, `InvalidOperationException` and any other exception, logs at debug, and returns whether the connection started. Use it in `WorkspaceRepositories.Realtime.cs`, `WorkspaceActions.razor.cs`, `WorkspaceGitChanges.Realtime.cs` and `WorkspaceActionNotificationPanel.razor` (replace its inline copy).

**C2 (required). Do not await the start in `OnAfterRenderAsync`.** Run it as a tracked background task (`_ = StartRealtimeAsync()`), so a slow or failing start can neither delay the first render nor surface as a render exception. Retry a few times with back-off when the first start fails, because `WithAutomaticReconnect` does not.

**C3 (required). Cancel on dispose, quietly.** Give each page a lifetime `CancellationTokenSource`, pass its token to `StartAsync`, set `_disposed` and cancel it first in `Dispose`, and let the cancelled start end silently.

**C4 (required). Remove the 2 s IPv6 stall.** In `WorkspaceSyncHubConnectionHelper.Create`, when the page host is `localhost`, connect to `127.0.0.1` for the same port. This is only a server-to-itself connection, so the browser's address does not matter. Alternative: also listen on `[::1]:8384` in `DesktopHostingExtensions.UseDesktopMode`; this touches the fixed-port and `AddressInUseExitCode` rules, so prefer the helper change.

**C5. Make the next occurrence provable.** Log at debug, once per start, the circuit id, page, URL host and elapsed time of the hub start and how it ended (`started`, `cancelled by dispose`, `failed`).

**C6. Tests.** A helper test for each outcome in C1; a test that a cancelled start does not throw out of the page's render path. Update `docs/architecture/04-runtime-communication-and-concurrency.md` (browser real-time connections must never be awaited or fail inside a render callback) in the same change.

### Open in tools

**O1 (required). Decide per client before the first paint, not per process.** Have Desktop mark its own WebView and let the App read the mark when the circuit is created:

- In `GrayMoon.Desktop/MainWindow.xaml.cs`, add a request header to every request the WebView makes to the App's origin (`CoreWebView2.AddWebResourceRequestedFilter` plus `WebResourceRequested`, for example `X-GrayMoon-Desktop: 1`), or append a token to the user agent (`CoreWebView2.Settings.UserAgent`).
- In the App, read it once from the initial HTTP request (the root component's `HttpContext`, or an `IHttpContextAccessor` read in a `CircuitHandler.OnCircuitOpenedAsync`) and keep it in a circuit-scoped `IDesktopClientState { bool IsDesktopWebView }`.
- `WorkspaceFeatureSelector.ApplyDesktopOpenInState` (and any other Desktop-only UI) then requires **both** `Desktop:PipeName` set **and** `IsDesktopWebView`. A browser tab never renders the buttons, and the Desktop WebView still paints them on the first frame.
- Keep the JS probe `graymoonDesktop.isAvailable()` as a fallback if the header is missing (older Desktop build), but it must only be able to turn the buttons **on**, never off.

Check first that enhanced navigation and the SignalR upgrade do not drop the mark: the circuit is created from the document request, which carries the header. A Desktop that cannot send it falls back to today's behaviour (buttons painted, then JS probe), which is why the version-lock rule in `CLAUDE.md` makes a new App+Desktop pair safe.

**O2 (optional, same pattern).** Use the same circuit-scoped flag for `WorkspaceTopBarService.PushToDesktopAsync` so a browser tab stops rewriting the Desktop window title (section 2), and replace the `Worker.razor` probe.

### Order of work

1. P1, T1-T3 and C1-C3 first: small, safe, remove the three user-visible failures (premature stop, invisible stop, crash).
2. C4, then O1.
3. P3, then P4.
4. After any shipped change, add the matching bullet to `## Recent GrayMoon changes` in `GrayMoon.Desktop/README.md`.

## 5. Why the timeout callout is not shown

The orchestrator does record the timeout. For op `fbfb899a` the log has `Required package unavailable`, `Push operation summary ... FailedLevel=3`, and `UpdateAndPushOrchestrator: finished ... PushSuccess=false ... FailedPushLevel=3`; `PackageWaitTimeout.Report` called `onLevelError(3, "Timed out waiting for package dependencies after 5 min (2 of 3 found).")` and `UpdateAndPushOrchestrator` stored it in `pushLevelErrors` and in the returned `UpdateAndPushResult.Push.LevelErrors`. The loss happens on the page side.

### Where the message goes

1. Live path: `WorkspaceRepositories.Update.cs:493` passes `(level, msg) => SafeInvoke(() => SetLevelError(level, msg))`.
2. Replay path (added in #478 "surface push failures"): after the run, `ApplyPipelinedUpdateAndPushResultAsync` (`Update.cs:536-546`) calls `ApplyRepositoryErrors`, `ApplyLevelErrors` and a toast, again through `SafeInvoke`.
3. Both write only to `levelErrors` / `repositoryErrors`, which are **fields of the page component instance** (`WorkspaceRepositories.State.cs:92`). The callout is drawn from them under the matching level header (`WorkspaceRepositoriesLevelHeader.LevelError`) or, in a flat grid, as `UngroupedLevelErrorMessage`.
4. `SafeInvoke` does nothing once `_disposed` is set (`Realtime.cs:119-123`), and `Dispose()` sets it as soon as the user leaves the page.

Nothing else keeps the failure. The circuit-scoped `BackgroundJobService` keeps the job alive across navigation, but the job handle holds only a progress line and the terminal buffer, not the operation's outcome. `BackgroundJobOverlay` is keyed by exact URL path, so away from `/workspaces/42` there is no indication either. The toast is also sent through `SafeInvoke`, so it is lost the same way, and it lasts a few seconds.

Consequence: a stop that happens while the user is not on that page instance is invisible and unrecoverable. This is not unlikely for this failure. The wait is the CI wait, so the user is naturally somewhere else (GitHub, the Actions or Changes page, the other Desktop window) or reloads the page. The Desktop log shows ten `Workspace changed: 42` events between 00:35:34 and 00:37:43 (each is a circuit loading a workspace 42 page; section 2), the first 8 s before the timeout. A new page instance starts with empty `levelErrors`, and `ClearRepositoryErrors()` also runs when the context is applied (`WorkspaceRepositories.razor.cs:158`).

What I could not establish from the logs is which page instance the user was looking at at 00:35:42, so this is the confirmed mechanism, not a proof of the user's exact sequence.

Smaller things that can also hide it even on a live page:

- In a grouped grid the level callout is a row under the Level N header. With virtual scrolling, a header outside the visible window is not rendered, so a failure at Level 3 is invisible while scrolled to Level 6.
- `AppendErrorText` drops a message already contained in the current text, so a repeat does not show again after a partial clear.

### Fixes

**T1 (required). Keep the outcome outside the page.** Record the last Update/Push/Prepare outcome per Workspace and context (`WorkspaceFeatureContextId`) in a singleton, for example `IWorkspaceOperationOutcomeStore`, written by the orchestrators themselves (`UpdateAndPushOrchestrator`, `PushOrchestrator`, `DependencyUpdateOrchestrator`): operation name, run id, UTC time, `LevelErrors`, `RepoErrors`, failure category (for example `PackageWaitTimeout`) and the one-line description. It must not depend on any component being alive. In memory is enough (the App process outlives a reload); persist it only if a restart should keep it.

**T2 (required). Page reads the store.** On init, on context change, on `JobService.Changed` and after every refresh, `WorkspaceRepositories` loads the stored outcome for its context and applies it to `levelErrors` / `repositoryErrors`. Entering the page after a failure then shows the same callouts as staying on it. Clear the entry when the user starts the next operation, or with a visible Dismiss on the callout; never on page init or context apply.

**T3 (required). Make the timeout unmistakable.** Show the cause (section 4, P3) in a page-level banner that is always rendered at the top of the grid, in addition to the level row. It names the level, the missing package, its producer repository and commit, and what to do next ("Run Push Synchronized again to continue"). A level row that may be off-screen cannot be the only place for it.

**T4. Toast.** Keep the toast as a hint, but a stop that needs action must not rely on it; the banner from T3 is the record.

**T5. Tests.** Orchestrator test: a package wait timeout records the outcome in the store. Page-level test: a failure recorded while no page is mounted shows up when the page is created. Test that starting the next operation clears it.

**T6. Docs.** Update `docs/architecture/04-runtime-communication-and-concurrency.md` and the Repositories section of `05-user-capability-reference.md`: operation failures are kept per Workspace and context, not on the page.
