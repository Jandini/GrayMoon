# GrayMoon.Agent Design Document

## Overview

GrayMoon.Agent is a host-side executable that executes all git and repository I/O operations on behalf of the GrayMoon.App (which runs in Docker). It receives commands via SignalR from the app and pushes responses (and sync notifications) back. This design simplifies the app by removing git, GitVersion, and workspace filesystem access from the container.

- **GrayMoon.App** (Docker): Web UI, DB, orchestration, SignalR hub — no git/filesystem.
- **GrayMoon.Agent** (Host): All git commands, hooks, workspace I/O — runs as console app or Windows/Linux service.

---

## 1. Agent Application

### 1.1 Project Type

- **Console application** with Serilog for logging
- **Designed to run as a service** (Windows Service / systemd)
- **Packaging**: Distributed as a `dotnet tool` (recommended) or standalone executable

### 1.2 When Agent Is Not Present

If no agent is connected to the app's SignalR hub:

- **App shows an error** in the UI (e.g. "Agent not connected. Start GrayMoon.Agent to sync repositories.")
- **Sync operations fail** with a clear, user-facing message
- **No silent failures** — the app does not proceed with git-dependent flows

---

## 2. Concurrency & Queuing

- **One agent per host** — single process, single connection to the app
- **Up to 8 concurrent commands** — configurable parallelism
- **Command queue** — incoming commands are queued; when a slot frees, the next command is processed
- **Bounded concurrency** — prevents overload while allowing parallel clone/version operations

```
[SignalR] → [Command Queue] → [Worker Pool (8)] → [Git / GitVersion / File I/O]
```

---

## 3. Agent Configuration (appsettings.json)

```json
{
  "Serilog": {
    "MinimumLevel": { "Default": "Information" },
    "WriteTo": [{ "Name": "Console" }]
  },
  "GrayMoon": {
    "AppHubUrl": "http://host.docker.internal:8384/hubs/git-agent",
    "ListenPort": 9191,
    "WorkspaceRootPath": "C:\\Projectes",
    "MaxConcurrentCommands": 8,
    "PostCommitHookBaseUrl": "http://host.docker.internal:8384"
  }
}
```

| Setting | Description | Default |
|---------|-------------|---------|
| `AppHubUrl` | SignalR hub URL the agent connects to | `http://host.docker.internal:8384/hubs/git-agent` |
| `ListenPort` | HTTP port for hook notifications (when hooks call agent) | `9191` |
| `WorkspaceRootPath` | Root path for workspace directories | `C:\Projectes` (Win) / `/var/graymoon/workspaces` (Linux) |
| `MaxConcurrentCommands` | Max parallel command executions | `8` |
| `PostCommitHookBaseUrl` | Base URL for hooks to reach the app (used in WriteHooks) | Same as AppHubUrl base |

Environment variables override: `GrayMoon__AppHubUrl`, `GrayMoon__ListenPort`, etc.

---

## 4. Hook Pattern: Agent vs App

### Option A: Hooks → App (direct)

```
post-commit → curl http://APP_URL/api/sync → App enqueues → App sends GetVersion to Agent
```

- Hooks need the app URL (port mapping, host.docker.internal, etc.)
- Workspace path differs between host (repos) and container
- Requires correct Docker networking for hooks to reach the app

### Option B: Hooks → Agent (recommended)

```
post-commit → curl http://127.0.0.1:9191/notify → Agent → SignalR push to App → App enqueues → App sends GetVersion to Agent
```

**Advantages:**

- Hooks always use `http://127.0.0.1:{ListenPort}/notify` — no Docker URL config
- Agent runs on the host where repos live; hooks run in the same environment
- Agent receives the notify, then **pushes via SignalR** to the app: `SyncRequested(repositoryId, workspaceId)`
- App enqueues the sync and sends commands (GetVersion, etc.) back to the agent
- Single network concern: agent must reach the app; hooks only reach localhost

**Flow:**

1. User commits → post-commit runs `curl -X POST http://127.0.0.1:9191/notify -d '{"repositoryId":1,"workspaceId":1}'`
2. Agent HTTP listener receives the request
3. Agent invokes hub method: `SyncRequested(repositoryId, workspaceId)` (agent is SignalR client calling server)
4. App hub handles it, enqueues to SyncBackgroundService
5. Sync worker sends `GetVersion` (and other commands) to agent
6. Agent executes, returns `CommandResponse`
7. App persists, broadcasts `WorkspaceSynced` to UI

**Conclusion:** **Hooks → Agent → SignalR push to App** is the better pattern.

---

## 5. Packaging as dotnet tool

GrayMoon.Agent will be installable as a .NET tool:

```bash
# Install
dotnet tool install --global GrayMoon.Agent

# Run
graymoon-agent

# Or with config
graymoon-agent --config /path/to/appsettings.json
```

- **Project**: `DotnetTool` package type in `.csproj`
- **Entry point**: Tool entry point that runs the agent host
- **Distribution**: NuGet feed (global or custom)

---

## 6. Solution Layout

GrayMoon.Agent lives in the same solution as GrayMoon.App:

```
GrayMoon.sln
├── src/
│   ├── GrayMoon.App/
│   └── GrayMoon.Agent/     ← new project
```

Shared contracts (command/response DTOs) can live in a small `GrayMoon.Contracts` project or be duplicated if kept minimal.

---

## 7. SignalR Contract

### Hub: `GitAgentHub` (hosted by App)

**Server → Agent (invoke on agent client):**

| Method | Args | Purpose |
|--------|------|---------|
| `ReceiveCommand` | `requestId`, `command`, `args` | Execute command and respond |

**Agent → Server (invoke from agent):**

| Method | Args | Purpose |
|--------|------|---------|
| `CommandResponse` | `requestId`, `success`, `data`, `error` | Return command result |
| `SyncRequested` | `repositoryId`, `workspaceId` | Notify app that a hook fired (hooks→agent pattern) |

### Commands

| Command | Args | Response `data` |
|---------|------|-----------------|
| `Clone` | `workingDir`, `cloneUrl`, `bearerToken?` | `{ }` |
| `AddSafeDirectory` | `repositoryPath` | `{ }` |
| `GetHeadSha` | `repositoryPath` | `{ sha }` |
| `GetVersion` | `repositoryPath` | `{ semVer, fullSemVer, branchName, ... }` |
| `CreateDirectory` | `path` | `{ }` |
| `DirectoryExists` | `path` | `{ exists }` |
| `GetDirectories` | `path` | `{ directories[] }` |
| `WriteHooks` | `repoPath`, `workspaceId`, `repoId`, `postCommitBaseUrl` | `{ }` |

---

## 8. Agent Components

```
GrayMoon.Agent
├── Program.cs              # Entry, Serilog, host builder
├── appsettings.json
├── Hosted/
│   ├── SignalRConnectionHostedService.cs   # Connect to hub, handle ReceiveCommand
│   └── HookListenerHostedService.cs        # HTTP listener on ListenPort for /notify
├── Handlers/
│   ├── ICommandHandler.cs
│   └── (CloneHandler, GetVersionHandler, WriteHooksHandler, ...)
├── Queue/
│   └── CommandQueueProcessor.cs            # Bounded channel, 8 workers
└── Models/
    └── (CommandRequest, CommandResponse, etc.)
```

---

## 9. Running as a Service

### Windows

- Use `Microsoft.Extensions.Hosting` with `UseWindowsService()`
- Install: `sc create GrayMoonAgent binPath="..."`

### Linux (systemd)

- Use `UseSystemd()` in host builder
- Unit file runs the agent process

The same binary supports both console and service modes; the host detects the environment.
