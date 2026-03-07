# GrayMoon Parallelism Analysis

This document describes how parallelism and concurrency are controlled in GrayMoon: app-side workers, agent-side workers, and the unified **MaxParallelOperations** setting for workspace operations.

---

## 1. Unified workspace setting: MaxParallelOperations

**Configuration:** `Workspace:MaxParallelOperations` (see `WorkspaceOptions`), default 16.

A single value controls parallelism for all workspace-level operations in the app and is passed to the agent for commands that do parallel work:

| Consumer | Use |
|----------|-----|
| **WorkspaceGitService** | Git batch operations (sync, refresh projects, push, create branches), commit-sync batches, post–commit-sync refresh, and push-wait dependency checks. |
| **PackageRegistrySyncService** | Parallel package lookups when matching workspace packages to NuGet connectors. |
| **SyncBackgroundService** | Number of sync workers when `Sync:MaxConcurrency` is not set (defaults to `MaxParallelOperations`). |
| **Agent (when app passes it)** | `GetWorkspaceRepositories` (parallel repo discovery) and `RefreshRepositoryProjects` (parallel .csproj discovery/parsing) use `maxParallelOperations` from the request; when omitted, agent uses its default (e.g. 8). |

So one appsetting (`Workspace:MaxParallelOperations`) drives Git concurrency, package registry concurrency, sync worker count (unless overridden), and agent-side workspace command parallelism when the app sends the value.

---

## 2. App Workers: SyncBackgroundService

**Location:** `GrayMoon.App/Services/SyncBackgroundService.cs`  
**Type:** `BackgroundService` (hosted service), registered in `Program.cs`.

### Responsibility

- Process *sync* requests (one repository in one workspace) from an unbounded channel with a fixed number of worker tasks.
- Triggered by `POST /api/sync` or `SyncBackgroundService.EnqueueSync(...)`. Each worker calls `WorkspaceGitService.SyncSingleRepositoryAsync(repositoryId, workspaceId)`.

### Concurrency and configuration

| Setting | Config path | Default | Purpose |
|--------|-------------|---------|--------|
| Worker count | `Sync:MaxConcurrency` | (none) → falls back to `Workspace:MaxParallelOperations` | Number of worker tasks that read from the sync channel. |
| Deduplication | `Sync:EnableDeduplication` | true | Only one request per `(repositoryId, workspaceId)` in-flight or queued. |

---

## 3. App: WorkspaceGitService

**Location:** `GrayMoon.App/Services/WorkspaceGitService.cs`

Uses `_maxConcurrent = MaxParallelOperations` (from `WorkspaceOptions`) for:

1. **SyncAsync** – parallel `SyncRepository` commands.
2. **RefreshWorkspaceProjectsAsync** – parallel `RefreshRepositoryProjects` commands (and passes `maxParallelOperations` to the agent).
3. **PushReposAsync** – parallel `PushRepository` commands.
4. **CreateBranchesAsync** – parallel `CreateBranch` commands.
5. Commit-sync batch (parallel `StageAndCommit`).
6. Post–commit-sync refresh (parallel `SyncSingleRepositoryAsync`).
7. Push wait loop (parallel NuGet “package exists” checks per round).

All of these use the same `MaxParallelOperations` value; there are no separate local constants for workspace operations.

---

## 4. App: Other parallelism

- **PackageRegistrySyncService** – Uses `WorkspaceOptions.MaxParallelOperations` for `MaxDegreeOfParallelism` when syncing workspace package registries or selected package IDs.
- **WorkspaceService** – Single `SemaphoreSlim(1, 1)` for workspace root path cache access.

---

## 5. Agent Workers: JobBackgroundService

**Location:** `GrayMoon.Agent/Hosted/JobBackgroundService.cs`

- Consumes jobs (NotifySync or Command) from a bounded queue with a fixed number of workers.
- **AgentOptions.MaxConcurrentCommands** (default `ProcessorCount * 2`, overridable via `--concurrency`) sets the worker count. This is **separate** from workspace parallelism: it limits how many jobs the agent runs at once; within a single command, parallelism is controlled by the `maxParallelOperations` value the app sends (or the agent’s default).

---

## 6. Agent: Commands that use maxParallelOperations

- **GetWorkspaceRepositoriesCommand** – When the request includes `maxParallelOperations`, uses it for the semaphore that limits parallel repo discovery; otherwise uses default 8.
- **RefreshRepositoryProjectsCommand** – When the request includes `maxParallelOperations`, passes it to `ICsProjFileService.FindAsync` for parallel .csproj discovery and parsing; otherwise the service uses default 8.

The base request type `WorkspaceCommandRequest` defines optional `MaxParallelOperations`; the app sets it from `WorkspaceOptions.MaxParallelOperations` when calling these commands.

---

## 7. Summary

| Limit | Where | Why |
|-------|--------|-----|
| **Workspace:MaxParallelOperations** | App (and passed to agent) | Single knob for all workspace operation parallelism: Git batches, package registry sync, sync workers (if not overridden), and agent workspace commands that do parallel work. |
| **Sync:MaxConcurrency** | App – SyncBackgroundService | Optional override for sync worker count; when not set, uses `MaxParallelOperations`. |
| **AgentOptions.MaxConcurrentCommands** | Agent – JobBackgroundService | How many jobs (commands + notify syncs) the agent processes at once. |
| **Job queue capacity** | Agent – JobQueue | Bounded channel; back-pressure when enqueue is faster than workers. |

Together, this keeps a single appsetting for workspace parallelism while still allowing an optional override for sync workers and a separate agent concurrency setting for total job throughput.
