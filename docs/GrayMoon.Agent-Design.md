# GrayMoon.Agent Design Document

## Overview

GrayMoon.Agent is a host-side executable that executes all git and repository I/O operations on behalf of GrayMoon.App (which runs in Docker). It receives commands via SignalR from the app and pushes responses (and sync notifications) back. This design simplifies the app by removing git, GitVersion, and workspace filesystem access from the container.

- **GrayMoon.App** (Docker): Web UI, DB, orchestration, SignalR hub — no git/filesystem.
- **GrayMoon.Agent** (Host): All git commands, hooks, workspace I/O — runs as console app or Windows/Linux service.

---

## 1. Agent Application

- **Console application** with Serilog for logging
- **Designed to run as a service** (Windows Service / systemd)
- **Packaging**: Distributed as a `dotnet tool` (recommended) or standalone executable

### 1.1 Agent Presence Badge

A badge next to the app version indicates agent connection state:

| State       | Badge color | Label      | Meaning                                   |
|-------------|-------------|------------|-------------------------------------------|
| **Online**  | Green       | online     | Agent connected and ready                 |
| **Offline** | Red         | offline    | No agent connected                        |
| **Connecting** | Gray     | connecting | Agent connection in progress (reconnecting) |

When offline: sync operations fail with a clear message. No silent failures.

---

## 2. Concurrency & Queuing

- **One agent per host** — single process, single connection to the app
- **Up to 8 concurrent commands** — configurable parallelism
- **Command queue** — incoming commands are queued; when a slot frees, the next command is processed

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
    "AppHubUrl": "http://host.docker.internal:8384/hubs/agent",
    "ListenPort": 9191,
    "WorkspaceRootPath": "C:\\Projectes",
    "MaxConcurrentCommands": 8,
    "PostCommitHookBaseUrl": "http://host.docker.internal:8384"
  }
}
```

| Setting | Description | Default |
|---------|-------------|---------|
| `AppHubUrl` | SignalR hub URL the agent connects to | `http://host.docker.internal:8384/hubs/agent` |
| `ListenPort` | HTTP port for hook notifications | `9191` |
| `WorkspaceRootPath` | Root path for workspace directories | `C:\Projectes` (Win) / `/var/graymoon/workspaces` (Linux) |
| `MaxConcurrentCommands` | Max parallel command executions | `8` |
| `PostCommitHookBaseUrl` | Base URL for hooks to reach the app (used in WriteHooks) | Same as AppHubUrl base |

Environment variables override: `GrayMoon__AppHubUrl`, `GrayMoon__ListenPort`, etc.

---

## 4. Hook Flow: Hooks → Agent → SignalR

```
post-commit → curl http://127.0.0.1:9191/notify → Agent → SignalR push to App → App enqueues → App sends GetVersion to Agent
```

- Hooks always use `http://127.0.0.1:{ListenPort}/notify` — no Docker URL config
- Agent runs on the host where repos live; hooks run in the same environment
- Agent receives the notify, then **pushes via SignalR** to the app: `SyncRequested(repositoryId, workspaceId)`
- App enqueues the sync and sends commands (GetVersion, etc.) back to the agent
- Single network concern: agent must reach the app; hooks only reach localhost

**Flow:**

1. User commits → post-commit runs `curl -X POST http://127.0.0.1:9191/notify -d '{"repositoryId":1,"workspaceId":1}'`
2. Agent HTTP listener receives the request
3. Agent invokes hub method: `SyncRequested(repositoryId, workspaceId)`
4. App hub handles it, enqueues to SyncBackgroundService
5. Sync worker sends `GetVersion` (and other commands) to agent
6. Agent executes, returns `CommandResponse`
7. App persists, broadcasts `WorkspaceSynced` to UI

---

## 5. Packaging as dotnet tool

```bash
dotnet tool install --global GrayMoon.Agent
graymoon-agent
```

---

## 6. Solution Layout

```
GrayMoon.sln
├── src/
│   ├── GrayMoon.App/
│   └── GrayMoon.Agent/
```

---

## 7. SignalR Contract

### Hub: `AgentHub` (path: `/hubs/agent`)

**Server → Agent (invoke on agent client):**

| Method | Args | Purpose |
|--------|------|---------|
| `ReceiveCommand` | `requestId`, `command`, `args` | Execute command and respond |

**Agent → Server (invoke from agent):**

| Method | Args | Purpose |
|--------|------|---------|
| `CommandResponse` | `requestId`, `success`, `data`, `error` | Return command result |
| `SyncRequested` | `repositoryId`, `workspaceId` | Notify app that a hook fired |

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
├── Program.cs
├── appsettings.json
├── Hosted/
│   ├── SignalRConnectionHostedService.cs   # Connect to hub, handle ReceiveCommand
│   └── HookListenerHostedService.cs        # HTTP listener on ListenPort for /notify
├── Handlers/
│   └── (CloneHandler, GetVersionHandler, WriteHooksHandler, ...)
├── Queue/
│   └── CommandQueueProcessor.cs            # Bounded channel, 8 workers
└── Models/
```

---

## 9. Running as a Service

- **Windows**: `UseWindowsService()`, install via `sc create GrayMoonAgent binPath="..."`
- **Linux**: `UseSystemd()`, unit file runs the agent process

The same binary supports both console and service modes.
