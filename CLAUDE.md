# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Line endings

Always use **Windows (CRLF)** in generated or edited code. Do not use LF-only.

## Task completion

When you complete a task, write a single sentence summarizing what was done.

## Commands

```powershell
# Build the solution
dotnet build GrayMoon.sln

# Run tests
dotnet test src/GrayMoon.Common.Tests/GrayMoon.Common.Tests.csproj

# Run a single test class
dotnet test src/GrayMoon.Common.Tests/GrayMoon.Common.Tests.csproj --filter "FullyQualifiedName~FilterSearchExpressionTests"

# Run the App locally (port 8384)
dotnet run --project src/GrayMoon.App/GrayMoon.App.csproj

# Run the Agent locally
dotnet run --project src/GrayMoon.Agent/GrayMoon.Agent.csproj

# Restore .NET tools (includes GitVersion)
dotnet tool restore

# Docker build (version injected via arg, GitVersion disabled inside container)
docker build --build-arg VERSION=1.0.0 -t graymoon .
```

CI pushes to Docker Hub on commits to `main`, on tag pushes, or on any branch commit whose message contains `prerelease`.

## Architecture

GrayMoon is a **two-process** system — never combine them:

- **GrayMoon.App** (`src/GrayMoon.App`) — ASP.NET Core 8 + Blazor Server, runs in Docker. Exposes the web UI and two SignalR hubs (`/hub/agent`, `/hubs/workspace-sync`). Never touches the local filesystem or runs git directly. SQLite database via EF Core at `/app/db` (volume-mounted). Uses `EnsureCreated()` — no incremental migrations yet.
- **GrayMoon.Agent** (`src/GrayMoon.Agent`) — .NET console app / tool that runs on the developer's host machine. Connects to the App via SignalR, executes all git and filesystem operations, and exposes a local HTTP listener on `127.0.0.1:9191` for git hook callbacks.
- **GrayMoon.Abstractions** (`src/GrayMoon.Abstractions`) — Shared interfaces and DTOs used by both App and Agent.
- **GrayMoon.Common** (`src/GrayMoon.Common`) — Shared utilities (e.g. the boolean filter-search expression parser tested in `GrayMoon.Common.Tests`).

### App → Agent command flow

The App sends commands to the Agent over SignalR using a `requestId` (GUID). `AgentBridge` calls `AgentResponseDelivery.WaitAsync(requestId)` (a `TaskCompletionSource`) then fires `RequestCommand` to the Agent. The Agent enqueues a `JobEnvelope` into a bounded `Channel<JobEnvelope>`, runs it in one of up to 8 concurrent workers, and calls back `ResponseCommand` with the same `requestId`. `AgentResponseDelivery.Complete` resolves the TCS, unblocking the App's awaiter.

### Agent → App sync (hook-driven)

Git hooks (`post-commit`, `post-checkout`, `post-merge`, `pre-push`) POST JSON to the Agent's local HTTP listener (`/hook/notify` or `/hook/push`). The Agent processes these as `NotifyJobs` and pushes partial state updates back to the App via `SyncCommand` on the SignalR connection. `SyncCommandHandler` persists only non-null fields (partial update semantics) then broadcasts `WorkspaceSynced` to all browser clients.

### Dependency levels

Workspace repositories are topologically sorted into dependency levels (Kahn's algorithm). Level 1 = no dependencies; higher levels depend on lower ones. This drives: grid grouping, push ordering (synchronized push waits for NuGet availability level-by-level), and the dependency update flow.

### Token encryption

Connector tokens are AES-256-GCM encrypted at rest via `AesGcmTokenProtector` (backed by ASP.NET Core Data Protection). Keys live in `/app/db/DataProtection-Keys/`. All git remote operations pass the token at runtime via `-c http.extraHeader="Authorization: Basic ..."` — tokens are never written to disk by the Agent.

### Database schema

Schema is owned by EF Core but applied via `EnsureCreated()`. Core tables: `Connectors`, `Repositories`, `Workspaces`, `WorkspaceRepositories` (join with live state), `WorkspaceRepositoryPullRequest` (1:1 with link), `WorkspaceProject`, `ProjectDependency`, `WorkspaceFile`, `WorkspaceFileVersionConfig`, `RepositoryBranch`, `Settings`.
