# Push wait and live-update connection: open items

Status: mostly shipped; only the items below are open. Originally investigated 2026-10-23 (a Push Updated that stopped at Level 3, browser circuits that ended with "GrayMoon needs to reload", and Open in buttons that painted and vanished in a browser).

## What is built

Current behavior is in `docs/architecture/01`, `04` and `05`:

- The package wait never shrinks its deadline (`WorkspacePushService`), and its timeout message names the missing packages and says to run Push Synchronized again, with no CI wording.
- `WorkspaceSyncHubConnectionHelper.TryStartAsync` is not awaited from the Repositories, Actions and Git Changes pages, and `ResolveHubUri` rewrites `localhost` to `127.0.0.1`.
- Open in buttons paint only for the Desktop WebView (the `GrayMoonDesktop` user-agent token).
- Operation failures are kept per Workspace and context in `WorkspaceOperationOutcomeStore` and shown by the outcome banner on Repositories, with Dismiss.

Design constraint that still applies: GrayMoon is not CI-aware. The package wait is decided only by what the package registry reports, and no fix may query, assume or name a CI product.

## Still open

1. **Hub start hardening.** `TryStartAsync` has no retry with back-off, no cancellation token tied to the page lifetime, and no debug log of circuit id, page, host, elapsed time and outcome (`started`, `cancelled by dispose`, `failed`). `WithAutomaticReconnect` does not cover a failed first start.
2. **Tests.** No test references `WorkspaceSyncHubConnectionHelper`. Needed: one per outcome (started, cancelled, failed), and one showing that a cancelled start does not throw out of a render callback.
3. **Top bar writes from browser tabs.** `WorkspaceTopBarService.PushToDesktopAsync` still lets a browser tab on a workspace rewrite the Desktop window title and tray label. Use the same Desktop-client flag as Open in. `Worker.razor` also still uses a different probe name (`graymoonDesktopBridge.isAvailable`).
4. **Toast.** The toast is still only a hint; keep the banner as the record (no change needed unless the banner is removed).

## Smaller risks seen in the original investigation

- In a grouped grid with virtual scrolling, a level callout row can be off-screen; the page banner is the record that covers it.
- `AppendErrorText` drops a message already contained in the current text.

After any shipped change, update architecture `04` and add the Desktop README bullet.
