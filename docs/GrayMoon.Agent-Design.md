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
    "AppHubUrl": "http://host.docker.internal:8384/agent",
    "ListenPort": 9191,
    "WorkspaceRootPath": "C:\\Workspace",
    "MaxConcurrentCommands": 8
  }
}
```

| Setting | Description | Default |
|---------|-------------|---------|
| `AppHubUrl` | SignalR hub URL the agent connects to | `http://host.docker.internal:8384/agent` |
| `ListenPort` | HTTP port for hook notifications (`/notify`) | `9191` |
| `WorkspaceRootPath` | Root path for workspace directories | `C:\Workspace` (Win) / `/var/graymoon/workspaces` (Linux) |
| `MaxConcurrentCommands` | Max parallel command executions | `8` |

Environment variables override: `GrayMoon__AppHubUrl`, `GrayMoon__ListenPort`, etc.

---

## 4. Hook Flow: Hooks → Agent (runs GitVersion) → SignalR push to App

```
post-commit → curl http://127.0.0.1:9191/notify → Agent runs GitVersion → Agent pushes workspaceId, repositoryId, version, branch to App → App persists
```

- Hooks always use `http://127.0.0.1:{ListenPort}/notify` — no Docker URL config
- Agent runs on the host where repos live; hooks run in the same environment
- Agent runs GitVersion immediately when `/notify` is hit, then pushes the result directly to the app — no round-trip, no enqueue
- Single network concern: agent must reach the app; hooks only reach localhost

**Flow:**

1. User commits → post-commit runs `curl -X POST http://127.0.0.1:9191/notify -d '{"repositoryId":1,"workspaceId":1,"repositoryPath":"/path/to/repo"}'`
2. Agent HTTP listener receives the request (repositoryPath is embedded in the hook when written during SyncRepository)
3. Agent runs AddSafeDirectory + GitVersion in repositoryPath
4. Agent invokes hub method: `RepositoryVersionRefreshed(workspaceId, repositoryId, version, branch)`
5. App persists to DB, broadcasts `WorkspaceSynced(workspaceId)` to UI clients

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

### Hub: `AgentHub` (path: `/agent`)

Hub path is `/agent` (no `hubs/` prefix required — map via `MapHub<AgentHub>("/agent")`).

**Server → Agent (invoke on agent client):**

| Method | Args | Purpose |
|--------|------|---------|
| `ReceiveCommand` | `requestId`, `command`, `args` | Execute command and respond |

**Agent → Server (invoke from agent):**

| Method | Args | Purpose |
|--------|------|---------|
| `CommandResponse` | `requestId`, `success`, `data`, `error` | Return command result |
| `RepositoryVersionRefreshed` | `workspaceId`, `repositoryId`, `version`, `branch` | Hook flow: agent ran GitVersion and pushes result directly |

### Commands (domain-specific, Option A: App sends full details)

The app sends all required data in each command. AddSafeDirectory is performed internally as part of Clone when needed.

| Command | Args | Response `data` |
|---------|------|-----------------|
| **SyncRepository** | `workspaceName`, `repositoryId`, `repositoryName`, `cloneUrl`, `bearerToken?`, `workspaceId` | `{ version, branch, wasCloned }` |
| **RefreshRepositoryVersion** | `workspaceName`, `repositoryName` | `{ version, branch }` |
| **EnsureWorkspace** | `workspaceName` | `{ }` |
| **GetWorkspaceRepositories** | `workspaceName` | `{ repositories: string[] }` |
| **GetRepositoryVersion** | `workspaceName`, `repositoryName` | `{ exists, version?, branch? }` |

**SyncRepository** — Full sync for one repo (used by workspace sync): ensures workspace dir exists, clones if repo missing (AddSafeDirectory included), adds safe dir, runs GitVersion, writes post-commit/post-checkout hooks. Hooks curl `http://127.0.0.1:{ListenPort}/notify` with `{ repositoryId, workspaceId, repositoryPath }` (repositoryPath embedded at hook-creation time so the agent can run GitVersion directly when the hook fires).

**RefreshRepositoryVersion** — Manual/API-triggered single-repo refresh: adds safe dir, runs GitVersion. Hook flow uses `/notify` instead (agent runs GitVersion and pushes result directly).

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
│   └── (SyncRepositoryHandler, RefreshRepositoryVersionHandler, EnsureWorkspaceHandler, ...)
├── Queue/
│   └── CommandQueueProcessor.cs            # Bounded channel, 8 workers
└── Models/
```

---

## 9. Running as a Service

- **Windows**: `UseWindowsService()`, install via `sc create GrayMoonAgent binPath="..."`
- **Linux**: `UseSystemd()`, unit file runs the agent process

The same binary supports both console and service modes.
