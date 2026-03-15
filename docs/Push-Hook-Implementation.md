# Push Hook — Implementation Document

## Purpose

Notify the GrayMoon app when a **push** is about to happen (or has been triggered) so the UI can stay in sync. The app receives **version and commit-count data only** (no fetch). This uses Git’s **pre-push** hook so the app is notified before the push completes.

---

## How It Was Done

### 1. Hook kind and HTTP endpoint

- **`NotifyHookKind.Push`** was added in `GrayMoon.Agent/Abstractions/NotifyHookKind.cs` (value `3`) so notify jobs can be identified as “push”.
- **`HookListenerHostedService`** was extended to accept `POST /hook/push`. It matches the path (e.g. `/hook/push`), deserializes the same JSON body as other hooks into `NotifyPayload`, builds a `NotifySyncJob` with `HookKind = NotifyHookKind.Push`, and enqueues it. Response is **202 Accepted**; the hook script does not wait for the sync to finish.

### 2. Dedicated push handler

- **`PushHookSyncCommand`** was added in `GrayMoon.Agent/Commands/PushHookSyncCommand.cs`. It implements the push notify flow:
  - **No fetch** — uses current local state only.
  - Runs **GitVersion** and **commit counts** (outgoing/incoming vs upstream, and vs default branch).
  - Optionally resolves **has upstream** via token + remote branches.
  - Builds a **`RepositorySyncNotification`** (same type as commit/checkout/merge) and sends it to the app via **SignalR `SyncCommand`**.

So “push” is **commits information only**: version, branch, ahead/behind counts, default-branch ahead/behind, and has-upstream. No extra git operations beyond what’s needed for that.

### 3. Routing and registration

- **`HookSyncDispatcher`** was updated to route `NotifyHookKind.Push` to `PushHookSyncCommand.ExecuteAsync` (other kinds unchanged).
- **`PushHookSyncCommand`** was registered in **`RunCommandHandler.cs`** (DI) and injected into the dispatcher.

### 4. Pre-push hook script

- **`GitService.WriteSyncHooks`** was extended to write a **`pre-push`** hook in `.git/hooks/` next to `post-commit`, `post-checkout`, `post-merge`, and `post-update`.
- The script is the same pattern: `#!/bin/sh`, a comment line, then a **curl** that POSTs to `http://127.0.0.1:{ListenPort}/hook/push` with JSON body `{ "repositoryId", "workspaceId", "repositoryPath" }`. Same timeout and “|| true” so a failed POST does not block the push.
- Hooks are written when the Agent writes sync hooks (e.g. after sync/clone), so any repo that gets the existing hooks also gets **pre-push**.

---

## End-to-end flow

1. User runs **`git push`** (or an IDE/tool pushes).
2. Git runs **`.git/hooks/pre-push`** before the push.
3. The hook sends **POST** to **`http://127.0.0.1:{ListenPort}/hook/push`** with `NotifyPayload` JSON.
4. **HookListenerHostedService** receives it, creates **NotifySyncJob** with **HookKind = Push**, enqueues **JobEnvelope.Notify(notifyJob)**.
5. A worker in **JobBackgroundService** dequeues the job and calls **INotifySyncHandler.ExecuteAsync** → **HookSyncDispatcher** → **PushHookSyncCommand.ExecuteAsync**.
6. **PushHookSyncCommand** runs GitVersion and commit-count logic (no fetch), builds **RepositorySyncNotification**, and invokes **SyncCommand** on the SignalR connection to the app.
7. The app’s **AgentHub.SyncCommand** → **SyncCommandHandler.HandleAsync** updates the workspace/repo row (version, branch, outgoing/incoming, default ahead/behind, has upstream) and broadcasts **WorkspaceSynced** so the grid refreshes.

No new app-side handler was added; the existing **SyncCommand** / **SyncCommandHandler** path is reused for push.

---

## Files and types touched

| Location | Change |
|----------|--------|
| `GrayMoon.Agent/Abstractions/NotifyHookKind.cs` | Added `Push = 3`. |
| `GrayMoon.Agent/Hosted/HookListenerHostedService.cs` | Map `/hook/push` → `NotifyHookKind.Push`. |
| `GrayMoon.Agent/Commands/PushHookSyncCommand.cs` | **New** — push notify handler (version + commit counts only, no fetch). |
| `GrayMoon.Agent/Commands/HookSyncDispatcher.cs` | Route `NotifyHookKind.Push` to `PushHookSyncCommand`. |
| `GrayMoon.Agent/Cli/RunCommandHandler.cs` | Register `PushHookSyncCommand` in DI. |
| `GrayMoon.Agent/Services/GitService.cs` | In `WriteSyncHooks`, add `pushCurl` and write **pre-push** hook. |

Payload and job types are unchanged: **NotifyPayload**, **NotifySyncJob**, **INotifyJob**, **RepositorySyncNotification**, and **SyncCommand** are shared with the other hook kinds.

---

## Configuration

- **ListenPort** (e.g. in `appsettings.json` under `GrayMoon.ListenPort`, default **9191**) is the port used in the hook’s curl URL. The hook calls **127.0.0.1** only; the Agent must be running on the same machine as the repo.

---

## Design choices

- **Pre-push, not post-push:** Git has no “post-push” hook. Pre-push runs before the push; the handler reports current local state (what is about to be pushed from this repo’s perspective). To refresh again after the push, the user could run sync from the app or rely on the next sync/hook.
- **Same notification type:** Reusing **RepositorySyncNotification** and **SyncCommand** keeps one code path for “repo state changed” and avoids new hub methods and app handlers.
- **No fetch in push handler:** Keeps the pre-push path fast and avoids extra network; commit counts are from existing refs. Checkout (and full sync) still do fetch when needed.
