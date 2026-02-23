# GrayMoon.Agent — Parallel Jobs System

## Overview

The Agent processes work through a single shared **job queue** consumed by a configurable pool of concurrent workers. Two distinct job kinds flow through the same queue: **Command jobs** (request/response, initiated by the App) and **Notify jobs** (fire-and-forget, initiated by external git hooks).

---

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│  GrayMoon.App                                                       │
│                                                                     │
│  API Endpoint / Service                                             │
│    │                                                                │
│    │  IAgentBridge.SendCommandAsync(command, args)                  │
│    │    ├── generates requestId (GUID)                              │
│    │    ├── AgentResponseDelivery.WaitAsync(requestId)              │
│    │    └── IHubContext<AgentHub>.Clients.Client(id)                │
│    │          .SendAsync("RequestCommand", requestId, command, args) │
│    │                                                                │
│    │                 ┌─── SignalR ────────────────────────────┐     │
│    │                 │                                         │     │
│    │  AgentHub                                                │     │
│    │    ├── ResponseCommand(requestId, success, data, error)  │     │
│    │    │     └── AgentResponseDelivery.Complete(requestId)   │     │
│    │    └── SyncCommand(workspaceId, repoId, version, ...)    │     │
│    │          └── SyncCommandHandler.HandleAsync(...)         │     │
│    │                                                          │     │
└────┼──────────────────────────────────────────────────────────┼─────┘
     │                                                          │
     │  SignalR WebSocket (/hub/agent)                          │
     │                                                          │
┌────▼──────────────────────────────────────────────────────────▼─────┐
│  GrayMoon.Agent                                                     │
│                                                                     │
│  SignalRConnectionHostedService                                     │
│    ├── .On("RequestCommand")                                        │
│    │     └── CommandJobFactory.CreateCommandJob(...)                │
│    │           └── JobQueue.EnqueueAsync(JobEnvelope.Command(...))  │
│    └── .InvokeAsync("ResponseCommand", ...) ◄── from workers       │
│                                                                     │
│  HookListenerHostedService  (HTTP POST /notify)                     │
│    └── JobQueue.EnqueueAsync(JobEnvelope.Notify(...))               │
│                                                                     │
│  ┌──────────────────────────────────────────────────────────┐      │
│  │  JobQueue  (Channel<JobEnvelope>, bounded)                │      │
│  │  capacity = max(MaxConcurrentCommands × 2, 64)           │      │
│  │  FullMode = Wait                                          │      │
│  └────────────────────────┬─────────────────────────────────┘      │
│                           │ IAsyncEnumerable                        │
│            ┌──────────────┴──────────────────┐                     │
│            │ JobBackgroundService             │                     │
│            │  MaxConcurrentCommands workers   │                     │
│            │  (default: 8)                    │                     │
│            │                                  │                     │
│   Worker 0 │  Worker 1  │  ...  │  Worker N   │                     │
│     ▼      │    ▼       │       │    ▼        │                     │
│  JobKind?  │  JobKind?  │       │  JobKind?   │                     │
│  Command ──┼──────────► ICommandDispatcher    │                     │
│            │              └── ICommandHandler<TReq,TRes>            │
│            │                    └── InvokeAsync("ResponseCommand")  │
│  Notify  ──┼──────────► INotifySyncHandler                         │
│            │              └── NotifySyncCommand                     │
│            │                    └── InvokeAsync("SyncCommand")      │
└────────────────────────────────────────────────────────────────────┘
```

---

## Job Kinds

### `JobKind` Enum

```csharp
public enum JobKind { Command = 0, Notify = 1 }
```

### `JobEnvelope` — Queue Unit of Work

The `JobEnvelope` is the single type that flows through `JobQueue`. It's a discriminated union with factory statics:

| Property | Type | Description |
|---|---|---|
| `Kind` | `JobKind` | Discriminator |
| `CommandJob` | `ICommandJob?` | Set when `Kind == Command` |
| `NotifyJob` | `INotifyJob?` | Set when `Kind == Notify` |

```csharp
// Construction
JobEnvelope.Command(commandJob)   // app-initiated request/response
JobEnvelope.Notify(notifyJob)     // hook-initiated fire-and-forget
```

---

## Job Types

### 1. Command Job — `ICommandJob` / `CommandJob`

**Origin**: App → SignalR `RequestCommand`

**Purpose**: Execute a named command on the agent's file system / git, then send the result back.

| Property | Type | Description |
|---|---|---|
| `RequestId` | `string` | Unique GUID correlating request to response |
| `Command` | `string` | Command name (e.g., `"SyncRepository"`) |
| `Request` | `object` | Typed, deserialized request object |

**Lifecycle**:
1. App calls `IAgentBridge.SendCommandAsync(command, args)`
2. App registers `TaskCompletionSource` in `AgentResponseDelivery` keyed by `requestId`
3. App sends SignalR `RequestCommand(requestId, command, argsJson)` to agent
4. Agent's `SignalRConnectionHostedService` receives it → `CommandJobFactory` deserializes args → enqueues `JobEnvelope.Command(...)`
5. A worker in `JobBackgroundService` dequeues → `ICommandDispatcher.ExecuteAsync(command, request)`
6. Agent sends SignalR `ResponseCommand(requestId, success, data, error)` back to app
7. App's `AgentHub.ResponseCommand(...)` calls `AgentResponseDelivery.Complete(requestId, ...)` → TCS resolves → `SendCommandAsync` returns

**Error handling**: If a worker throws, the worker catches the exception and sends `ResponseCommand` with `Success=false` and the exception message.

---

### 2. Notify Job — `INotifyJob` / `NotifySyncJob`

**Origin**: External git post-receive hook → HTTP POST `/notify`

**Purpose**: React to a pushed commit by re-computing git version, fetching, and pushing live data to the app. No response required.

| Property | Type | Description |
|---|---|---|
| `RepositoryId` | `int` | DB id of the repository |
| `WorkspaceId` | `int` | DB id of the workspace |
| `RepositoryPath` | `string` | Absolute path on the agent host |

**Lifecycle**:
1. Git post-receive hook sends `POST http://127.0.0.1:{ListenPort}/notify` with `NotifyPayload` JSON
2. `HookListenerHostedService` receives it → constructs `NotifySyncJob` → enqueues `JobEnvelope.Notify(...)`
3. A worker in `JobBackgroundService` dequeues → `INotifySyncHandler.ExecuteAsync(notifyJob)`
4. `NotifySyncCommand`:
   - Runs `git.GetVersionAsync(repositoryPath)` (GitVersion)
   - Runs `git.FetchAsync(repositoryPath, includeTags: true)`
   - Runs `git.GetCommitCountsAsync(repositoryPath, branch)` for ahead/behind counts
   - Sends SignalR `SyncCommand(workspaceId, repoId, version, branch, outgoing, incoming)` to app
5. App's `AgentHub.SyncCommand(...)` → `SyncCommandHandler.HandleAsync(...)` → updates DB + broadcasts UI refresh

---

## Infrastructure

### `JobQueue`

```
Channel<JobEnvelope>  (bounded)
  capacity = max(MaxConcurrentCommands × 2, 64)
  FullMode = BoundedChannelFullMode.Wait
```

Backpressure: producers (`SignalRConnectionHostedService`, `HookListenerHostedService`) await when the channel is full. This naturally limits in-flight work to the configured capacity.

### `JobBackgroundService`

Spawns exactly `MaxConcurrentCommands` independent workers (default **8**), each reading from the shared `IAsyncEnumerable<JobEnvelope>` provided by `JobQueue.ReadAllAsync()`. Because `Channel` is multi-reader-safe, all workers compete for the next available envelope — work is distributed automatically.

```
Worker count = Math.Max(1, AgentOptions.MaxConcurrentCommands)   // default 8
```

Workers are started together via `Task.WhenAll(workers)` inside `ExecuteAsync`. The service shuts down cleanly when the `CancellationToken` fires, which also closes the channel reader.

### `CommandJobFactory`

Deserializes raw `JsonElement?` args at the edge (in `SignalRConnectionHostedService`, before enqueueing) into a strongly-typed request object. All downstream code works with `object` (actual typed instance), cast at dispatch time.

### `CommandDispatcher`

Routes a command name string to its `ICommandHandler<TReq, TRes>` executor via a pre-built `IReadOnlyDictionary<string, Func<object, CancellationToken, Task<object?>>>`. Unknown commands throw `NotSupportedException`.

### `HubConnectionProvider`

Singleton mutable holder for the `HubConnection`. `SignalRConnectionHostedService` sets it on startup. Workers in `JobBackgroundService` and `NotifySyncCommand` read `.Connection` to invoke hub methods.

### `AgentResponseDelivery` (App side)

Static `ConcurrentDictionary<string, TaskCompletionSource<AgentCommandResponse>>`. Allows `IAgentBridge.SendCommandAsync` (which runs on an HTTP request thread) to await a response that arrives later on the SignalR hub thread, without any shared lock.

---

## Concurrency Model

```
App HTTP request
   └─ SendCommandAsync ──► SignalR ──► SignalRConnectionHostedService
                                           └─ Channel.WriteAsync (producer)
                                                    │
                           Channel<JobEnvelope>  ◄──┘
                                    │
                          N workers read concurrently
                                    │
                       ICommandDispatcher.ExecuteAsync
                                    │
                         ICommandHandler.ExecuteAsync
                         (git, file system, etc.)
                                    │
                         SignalR ResponseCommand ──► AgentHub
                                                        └─ TCS.SetResult
                                                               │
                                                         SendCommandAsync
                                                           returns ✓
```

Multiple HTTP requests can be in-flight simultaneously; each gets its own `requestId` and `TaskCompletionSource`. The workers process them concurrently up to `MaxConcurrentCommands`.

Notify jobs compete for the same worker pool as command jobs. A heavy burst of git hooks will not starve command jobs — they interleave in FIFO order as workers free up.

---

## All Registered Commands

| Command Name | Description |
|---|---|
| `SyncRepository` | Full git sync: fetch, merge/rebase, update version in DB |
| `RefreshRepositoryVersion` | Re-run GitVersion for a single repo and update DB |
| `RefreshRepositoryProjects` | Scan `.csproj` files in a repo and update DB |
| `EnsureWorkspace` | Create workspace directory on disk if missing |
| `GetWorkspaceRepositories` | List subdirectories (repos) inside a workspace, with origin URLs |
| `GetRepositoryVersion` | Return current GitVersion output for a repo |
| `GetWorkspaceExists` | Check if the workspace root path exists on disk |
| `GetWorkspaceRoot` | Return the configured workspace root path |
| `GetHostInfo` | Return OS + machine name of the agent host |
| `SyncRepositoryDependencies` | Update NuGet/npm dependency versions across repos |
| `CommitSyncRepository` | Stage, commit, and push dependency version bumps |
| `GetBranches` | List local + remote branches for a repo |
| `CheckoutBranch` | Checkout an existing or new tracking branch |
| `SyncToDefaultBranch` | Fetch + reset to `origin/main` (or configured default) |
| `RefreshBranches` | Re-fetch branch list and update DB |
| `CreateBranch` | Create and push a new branch |
| `StageAndCommit` | Stage all changes and create a commit with a message |
| `PushRepository` | Push current branch to remote |
| `SearchFiles` | Find files under a workspace matching a glob/name pattern |
| `UpdateFileVersions` | Rewrite version strings in tracked files using configured patterns |
| `GetFileContents` | Read and return the text content of a workspace file |

---

## Configuration (`AgentOptions`)

| Option | Default | Description |
|---|---|---|
| `AppHubUrl` | `http://host.docker.internal:8384/hub/agent` | SignalR hub URL in the App |
| `ListenPort` | `9191` | HTTP port for git hook `/notify` endpoint |
| `WorkspaceRoot` | `C:\Workspace` | Root directory containing all workspaces |
| `MaxConcurrentCommands` | `8` | Number of parallel job workers + channel capacity multiplier |

---

## Hosted Services Startup Order

| Service | Interface | Role |
|---|---|---|
| `SignalRConnectionHostedService` | `IHostedService` | Connects to app hub, registers `RequestCommand` handler, retries on disconnect |
| `HookListenerHostedService` | `IHostedService` | Starts `HttpListener` on `ListenPort` for git hook POSTs |
| `JobBackgroundService` | `BackgroundService` | Spawns N workers that consume `JobQueue` |

All three are registered as singletons and start concurrently. `JobBackgroundService` workers will drain the queue as soon as it starts, even before the first SignalR connection is established.
