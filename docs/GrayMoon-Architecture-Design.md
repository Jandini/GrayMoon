# GrayMoon — Architecture & Design Reference

**Version:** 0.1.0-main.177  
**Target runtime:** .NET 8, ASP.NET Core 8, Blazor Server, EF Core 8 (SQLite)

---

## Table of Contents

1. [What GrayMoon Is](#1-what-graymoon-is)
2. [Two-Process Architecture](#2-two-process-architecture)
3. [Data Model](#3-data-model)
4. [Communication Layer](#4-communication-layer)
5. [GrayMoon.App — Service Layer](#5-graymoonapp--service-layer)
6. [GrayMoon.Agent — Command Pipeline](#6-graymoonagent--command-pipeline)
7. [Feature Workflows](#7-feature-workflows)
8. [Concurrency Model](#8-concurrency-model)
9. [Security Design](#9-security-design)
10. [Architecture Strengths](#10-architecture-strengths)
11. [Architecture Weaknesses & Risks](#11-architecture-weaknesses--risks)
12. [Known Improvements & Optimizations](#12-known-improvements--optimizations)

---

## 1. What GrayMoon Is

GrayMoon is a **workspace orchestration tool for multi-repository .NET development**. It is designed for teams and individuals working across many repositories simultaneously — microservices, shared libraries, NuGet packages — where a single feature typically requires coordinated changes in several places.

### Core problem solved

Without a tool like GrayMoon, multi-repo work requires:
- Manually tracking which repositories need branch switches, updates, or pushes.
- Ensuring pushes happen in the correct dependency order (so CI does not break due to missing package versions).
- Checking PR status, action status, divergence, and commit counts across many repos one at a time.
- Remembering to update `PackageReference` versions after a package version bump.

GrayMoon centralizes this: one operational view, one coordinated workflow.

### What GrayMoon is not

- Not a Git hosting platform (it uses GitHub as a connector).
- Not an IDE plugin (it is a standalone web app with an agent process).
- Not a package registry (it reads NuGet/GitHub registries to track availability).

---

## 2. Two-Process Architecture

GrayMoon is intentionally split into **two processes**: an **App** (web, containerized) and an **Agent** (host-side, bare metal). This separation exists because Docker containers cannot safely access the host filesystem or run git commands against local repositories.

```
┌────────────────────────────────────────────────────────────────┐
│  GrayMoon.App  (Docker container)                              │
│                                                                │
│  ASP.NET Core 8 + Blazor Server                                │
│  ├── Razor Components (UI)                                     │
│  ├── Minimal API Endpoints                                     │
│  ├── SignalR Hubs  (AgentHub, WorkspaceSyncHub)                │
│  ├── EF Core (SQLite)                                          │
│  └── Background Services  (Sync, TokenHealth)                  │
│                                                                │
│  Volume mount:  host/db → /app/db  (SQLite, Data Protection)   │
│  Exposed port:  8384                                           │
└────────────────────────────┬───────────────────────────────────┘
                             │  SignalR WebSocket  /hub/agent
                             │  (App → Agent: RequestCommand)
                             │  (Agent → App: ResponseCommand, SyncCommand)
┌────────────────────────────▼───────────────────────────────────┐
│  GrayMoon.Agent  (host process, runs on developer machine)     │
│                                                                │
│  .NET console app / background service                         │
│  ├── SignalR client  (connects to App /hub/agent)              │
│  ├── HTTP listener  (127.0.0.1:{ListenPort}  for git hooks)    │
│  ├── Job queue  (Channel<JobEnvelope>, bounded)                │
│  ├── Job workers  (up to 8 parallel, configurable)             │
│  └── Command handlers  (git, GitVersion, filesystem)           │
│                                                                │
│  Accesses:  local git repos, .csproj files, GitVersion CLI     │
└────────────────────────────────────────────────────────────────┘
```

### Process lifecycle

- The App is the orchestrator. It never touches the local filesystem directly.
- The Agent starts, connects to the App over SignalR, and waits for commands.
- The App tracks Agent connectivity via `AgentConnectionTracker`; if the Agent disconnects the App marks all operations as unavailable until reconnect.
- The Agent also exposes a local HTTP port for git hooks so hooks can fire without a running connection.

---

## 3. Data Model

All persistence is in a **SQLite database** accessed via EF Core. The database file and Data Protection keys are stored on a volume-mounted host path (`/app/db`).

### Core entities

#### `Connector`
Represents an external service credential (GitHub, NuGet, Docker).

| Field | Notes |
|-------|-------|
| `ConnectorId` | PK |
| `ConnectorName` | Display name |
| `ConnectorType` | `GitHub = 1`, `NuGet = 2`, `Docker = 3` |
| `ApiBaseUrl` | API endpoint (determines registry behavior) |
| `UserName` | Optional; used by some registries |
| `UserToken` | Nullable; AES-256-GCM encrypted at rest (Level 2) |
| `Status` | `Unknown`, `Healthy`, `Unhealthy` |
| `IsHealthy` | Cache flag; set by `ConnectorHealthService` |

#### `Repository`
A git repository known to GrayMoon, linked to a Connector.

| Field | Notes |
|-------|-------|
| `RepositoryId` | PK |
| `ConnectorId` | FK → Connector (authentication for remote operations) |
| `RepositoryName` | Short name |
| `OrgName` | GitHub org / owner |
| `CloneUrl` | Full clone URL |
| `Visibility` | `Public` / `Private` |
| `Topics` | Comma-separated GitHub topics |

#### `Workspace`
A named collection of repositories forming a coordinated development context.

| Field | Notes |
|-------|-------|
| `WorkspaceId` | PK |
| `Name` | Display name |
| `IsDefault` | One workspace is the default landing page |
| `RootPath` | Per-workspace filesystem root (overrides global setting) |
| `LastSyncedAt` | Timestamp of last full sync |
| `IsInSync` | Whether all repos are currently in sync |

#### `WorkspaceRepositoryLink` (table: `WorkspaceRepositories`)
The join entity between a Workspace and a Repository, extended with all live state.

| Field | Notes |
|-------|-------|
| `WorkspaceRepositoryId` | PK |
| `WorkspaceId` | FK → Workspace |
| `RepositoryId` | FK → Repository |
| `GitVersion` | Current SemVer from GitVersion |
| `BranchName` | Current branch |
| `DefaultBranchName` | Default branch (e.g. `main`) |
| `OutgoingCommits` | Commits ahead of remote tracking branch |
| `IncomingCommits` | Commits behind remote tracking branch |
| `DefaultBranchAheadCommits` | Commits in current branch not on default (for Divergence column) |
| `DefaultBranchBehindCommits` | Commits on default not in current branch |
| `BranchHasUpstream` | Whether current branch has a remote tracking branch |
| `SyncStatus` | `NeedsSync`, `Syncing`, `Synced`, `Error` |
| `DependencyLevel` | Graph-computed build level (lower = less dependent) |
| `Dependencies` | Count of dependency edges where this repo is dependent |
| `UnmatchedDeps` | Dependency edges where `PackageReference` version ≠ `GitVersion` |
| `Projects` | Number of `.csproj` files in the repository |

#### `WorkspaceRepositoryPullRequest`
One-to-one with `WorkspaceRepositoryLink`. Persists the last-known PR state for the current branch.

| Field | Notes |
|-------|-------|
| `WorkspaceRepositoryId` | PK + FK (cascade delete) |
| `PullRequestNumber` | GitHub PR number; null = no PR |
| `State` | `open`, `closed` |
| `Mergeable` | Tri-state: `true`, `false`, `null` (unknown) |
| `MergeableState` | `clean`, `dirty`, `unstable`, `blocked`, `unknown` |
| `HtmlUrl` | Link to PR on GitHub |
| `MergedAt` | When merged |
| `LastCheckedAt` | When the PR was last fetched from the API |

#### `WorkspaceProject` + `ProjectDependency`
Tracks parsed `.csproj` content. `WorkspaceProject` is a single project (with `PackageId`, `ProjectName`, path). `ProjectDependency` is an edge: dependent project → referenced project, with the current `Version` in the `.csproj`.

#### `WorkspaceFile` + `WorkspaceFileVersionConfig`
`WorkspaceFile` is an arbitrary file in a repository (e.g. `Directory.Build.props`). `WorkspaceFileVersionConfig` holds a version-pattern string (multi-line `KEY={repositoryname}`) used to update version strings in that file.

#### `RepositoryBranch`
Local branch metadata per repository: `BranchName`, `IsRemote`, `IsHead`, `TrackingBranch`.

#### `Setting`
Key-value store for app-wide settings (e.g. `WorkspaceRootPath` default, dark mode preference).

---

## 4. Communication Layer

### 4.1 SignalR Hubs

Two hubs, both served by the App:

| Hub | Path | Direction | Purpose |
|-----|------|-----------|---------|
| `AgentHub` | `/hub/agent` | Bidirectional | Agent connects here; App sends commands, Agent sends responses and sync notifications |
| `WorkspaceSyncHub` | `/hubs/workspace-sync` | Server → Browser | App broadcasts `WorkspaceSynced(workspaceId)` to refresh the UI |

**Agent connection tracking:** `AgentConnectionTracker` holds the active connection ID. `AgentHub.OnConnectedAsync` / `OnDisconnectedAsync` update it and call `ClearCachedRootPath` on disconnect.

**Browser SignalR:** `WorkspaceRepositories.razor` builds a `HubConnection` to `WorkspaceSyncHub`. On `WorkspaceSynced`, it calls `RefreshFromSync()` after a 200ms debounce to avoid thrashing on burst events.

### 4.2 Request/Response Command Flow

```
App service
  └── IAgentBridge.SendCommandAsync(command, argsJson)
        ├── generates requestId (GUID)
        ├── AgentResponseDelivery.WaitAsync(requestId)   ← TaskCompletionSource
        └── hubContext.Clients.Client(agentId)
              .SendAsync("RequestCommand", requestId, command, argsJson)
                            │
                     SignalR WebSocket
                            │
Agent SignalRConnectionHostedService
  └── .On("RequestCommand", handler)
        └── CommandJobFactory.CreateCommandJob(...)
              └── JobQueue.EnqueueAsync(JobEnvelope.Command(...))
                            │
                     JobBackgroundService worker
                            │
                     CommandDispatcher.ExecuteAsync(commandJob)
                            │
                     connection.InvokeAsync("ResponseCommand",
                         requestId, success, data, error)
                            │
                     SignalR WebSocket
                            │
App AgentHub.ResponseCommand
  └── AgentResponseDelivery.Complete(requestId, success, data, error)
        └── TaskCompletionSource.SetResult(...)   ← unblocks SendCommandAsync
```

Key properties:
- **Non-blocking agent side:** `On("RequestCommand")` returns immediately after enqueue; no UI thread blocking.
- **Back-pressure:** Bounded `Channel<JobEnvelope>` with `FullMode = Wait` provides natural flow control.
- **Exactly-once completion:** `TryRemove` in `AgentResponseDelivery.Complete` ensures at-most-one resolution per `requestId`.

### 4.3 SyncCommand (Agent → App)

The Agent also pushes unsolicited state updates to the App using `SyncCommand`. This is used by both git hook processing and any command that wants to push a repo state snapshot.

```
Agent PushHookSyncCommand (or other hook command)
  └── connection.InvokeAsync("SyncCommand",
          workspaceId, repositoryId, version, branch,
          outgoing, incoming, defaultBehind, defaultAhead, hasUpstream)
                  │
App AgentHub.SyncCommand
  └── SyncCommandHandler.HandleAsync(...)
        ├── Updates WorkspaceRepositoryLink (persists state values where non-null)
        ├── Recomputes dependency stats if version changed
        └── hubContext.Clients.All.SendAsync("WorkspaceSynced", workspaceId)
```

`SyncCommandHandler` uses **`HasValue` guards**: null fields in the incoming notification do not overwrite existing DB values. This is important — it allows partial-update notifications (e.g. the pre-push hook now only sends Version + Branch, leaving commit counts untouched).

### 4.4 HTTP Hook Listener (Agent)

`HookListenerHostedService` binds to `http://127.0.0.1:{ListenPort}` (default `9191`). Git hooks POST to this endpoint after relevant git events.

| URL path | Hook kind | Trigger |
|----------|-----------|---------|
| `/hook/notify` | `NotifyHookKind.CommitOrCheckout` (value 1) | post-commit, post-checkout, post-merge, post-update |
| `/hook/push` | `NotifyHookKind.Push` (value 3) | pre-push |

Each POST carries a JSON body: `{ "repositoryId", "workspaceId", "repositoryPath" }`. The listener builds a `NotifySyncJob` and enqueues `JobEnvelope.Notify(...)`. Response is **202 Accepted**; the hook script does not wait.

`HookSyncDispatcher` routes by `HookKind`:
- Push → `PushHookSyncCommand`
- CommitOrCheckout → `CommitHookSyncCommand`, `CheckoutHookSyncCommand`, `MergeHookSyncCommand` (type determined by the path subdirectory in prior versions; now unified)

---

## 5. GrayMoon.App — Service Layer

### 5.1 Agent communication

| Service | Responsibility |
|---------|----------------|
| `AgentBridge` | Sends commands to the Agent; wraps SignalR `RequestCommand` with `requestId` tracking via `AgentResponseDelivery`. |
| `AgentConnectionTracker` | Tracks current Agent SignalR connection ID; used by `AgentBridge` to find the target. |
| `AgentResponseDelivery` | Static `ConcurrentDictionary<string, TCS>` — bridges async hub callbacks back to calling code. |
| `AgentQueueStateService` | Reports agent job-queue depth for diagnostics. |

### 5.2 Workspace orchestration

| Service | Responsibility |
|---------|----------------|
| `WorkspaceGitService` | Core fanout service — sync, refresh projects, push, update, commit-sync, create branches, and dependency sync. Uses `SemaphoreSlim(MaxParallelOperations)` for all parallel work. |
| `WorkspaceService` | Workspace CRUD + root path resolution. `GetRootPathForWorkspaceAsync` returns `workspace.RootPath ?? Settings.RootPath`. |
| `WorkspaceSyncHandler` | Thin wrapper around `WorkspaceGitService.SyncAsync` with error handling and broadcast. |
| `WorkspacePushService` | Push business logic: builds push plan, runs parallel or level-ordered push, calls `UpdateCommitCountsAndUpstreamAfterPushAsync` which resets commit counts in DB and sends `WorkspaceSynced`. |
| `PushOrchestrator` | Optionally syncs package registries before push, then delegates to `WorkspacePushService`. |
| `WorkspacePushHandler` | Thin wrapper around `PushOrchestrator` with error handling; invoked from the component. |
| `DependencyUpdateOrchestrator` | Runs the full dependency-update workflow: refresh projects → build plan → sync `.csproj` files → commit → refresh versions. |
| `WorkspaceUpdateHandler` | Thin wrapper around `DependencyUpdateOrchestrator`. |
| `WorkspaceBranchHandler` | Branch operations (checkout, create, delete, set-upstream, sync-to-default). |
| `WorkspaceCommitSyncHandler` | Commit sync flow. |
| `WorkspaceDependencyService` | Recomputes dependency stats without re-syncing; reads projects from DB and re-runs the level algorithm. |
| `WorkspacePageService` | Aggregates data loading for the main repositories page. |
| `WorkspaceActionService` | GitHub Actions status fetch for workspace repositories. |
| `WorkspacePullRequestService` | GitHub PR fetch and persistence per workspace repository. |
| `PackageRegistrySyncService` | Syncs workspace packages against NuGet/GitHub connectors; used before synchronized push. |
| `SyncBackgroundService` | Background worker (unbounded channel) that processes `SyncSingleRepositoryAsync` requests from git hooks or API calls. |
| `SyncCommandHandler` | Handles incoming `SyncCommand` from Agent: persists state, recomputes deps, broadcasts `WorkspaceSynced`. |

### 5.3 Connector/repository management

| Service | Responsibility |
|---------|----------------|
| `ConnectorServiceFactory` | Factory returning `IConnectorService` for a connector's type/URL. |
| `ConnectorHealthService` | Periodic token validation; updates `Connector.IsHealthy`. |
| `GitHubService` | GitHub REST API calls (repos, PRs, actions, branches). |
| `GitHubRepositoryService` | Import repositories from GitHub into the `Repositories` table. |
| `GitHubPullRequestService` | PR fetch and mergeability. |
| `GitHubActionsService` | GitHub Actions run status per repository. |
| `NuGetService` | Package version existence checks. |
| `TokenHealthBackgroundService` | Periodically validates all connector tokens. |

### 5.4 Security services

| Service | Responsibility |
|---------|----------------|
| `Security/ITokenProtector` | Abstraction for protect/unprotect. |
| `Security/AesGcmTokenProtector` | AES-256-GCM implementation backed by ASP.NET Core Data Protection. |
| `ConnectorHelpers` | `ProtectToken` / `UnprotectToken` delegate to `ITokenProtector`; used at all token read/write sites. |

---

## 6. GrayMoon.Agent — Command Pipeline

### 6.1 Job kinds

Two job kinds flow through a single bounded `Channel<JobEnvelope>`:

- **Command jobs** — initiated by App `RequestCommand`; have a `requestId`; must send `ResponseCommand` back.
- **Notify jobs** — initiated by git hook HTTP POST; fire-and-forget; may send `SyncCommand` back to App.

### 6.2 Agent command catalog

| Command | Triggered by | Does |
|---------|-------------|------|
| `SyncRepository` | Workspace sync | `git fetch`, GitVersion, commit counts, branch detection, `.csproj` project discovery |
| `RefreshRepositoryVersion` | After dependency commit | GitVersion only |
| `RefreshRepositoryProjects` | After workspace project refresh | `.csproj` file walk + parse |
| `GetRepositoryVersion` | Version reads | GitVersion only |
| `GetWorkspaceRepositories` | Workspace import/discovery | Walk root path for `.git` dirs |
| `GetWorkspaceExists` | Workspace validation | Check root path exists |
| `EnsureWorkspace` | Workspace setup | Clone or validate repo presence |
| `GetCommitCounts` | Post-push count refresh | `git rev-list --count` vs tracking branch and vs default |
| `GetBranches` | Branch management | `git branch -a` + tracking info |
| `RefreshBranches` | Branch refresh | Same as GetBranches but updates DB |
| `CheckoutBranch` | Branch switch | `git checkout` or `git switch` |
| `CreateBranch` | Branch creation | `git checkout -b` or `git switch -c` |
| `DeleteBranch` | Branch deletion | `git branch -d/-D` / `git push --delete` |
| `SetUpstreamBranch` | After new branch push | `git push --set-upstream` |
| `SyncToDefaultBranch` | After PR merge | `git checkout <default>`, prune local feature branch, `git pull` |
| `PushRepository` | Push workflow | `git push` (with token auth via `-c http.extraHeader`) |
| `StageAndCommit` | Dependency update commit | `git add -A`, `git commit -m` |
| `CommitSyncRepository` | Commit sync | Git log query to compare commit state |
| `SyncRepositoryDependencies` | Dependency update | `ICsProjFileService.UpdatePackageVersionsAsync` |
| `UpdateFileVersions` | File version update | Token substitution in configured files |
| `GetFileContents` | File viewer | Read file bytes from disk |
| `SearchFiles` | File search | Walk directory tree for file matches |
| `ValidatePath` | Workspace validation | Check that a path exists and is accessible |
| `GetHostInfo` | Agent page | .NET version, Git version, GitVersion version, OS info |

Hook commands (Notify job path):

| Command | Hook | Does |
|---------|------|------|
| `PushHookSyncCommand` | pre-push | GitVersion only; sends `SyncCommand` with Version + Branch (no commit counts — push not yet complete) |
| `CommitHookSyncCommand` | post-commit | GitVersion + commit counts; sends `SyncCommand` |
| `CheckoutHookSyncCommand` | post-checkout | GitVersion + commit counts; sends `SyncCommand` |
| `MergeHookSyncCommand` | post-merge | GitVersion + commit counts; sends `SyncCommand` |

### 6.3 Job concurrency (agent side)

`JobBackgroundService` maintains up to `MaxConcurrentCommands` (default 8) parallel workers reading from the job queue. All workers share the same `Channel<JobEnvelope>`. There is no per-command serialization — the agent can run 8 jobs concurrently against different repositories without coordination. Callers that require serialization (e.g. dependency-level push ordering) must enforce it on the App side.

### 6.4 Git hook registration

`GitService.WriteSyncHooks(repositoryPath)` writes the following scripts to `.git/hooks/`:

| File | Trigger | Posts to |
|------|---------|---------|
| `post-commit` | After local commit | `/hook/notify` |
| `post-checkout` | After branch switch | `/hook/notify` |
| `post-merge` | After merge | `/hook/notify` |
| `post-update` | After update | `/hook/notify` |
| `pre-push` | Before push starts | `/hook/push` |

All scripts are `#!/bin/sh` with a single `curl` that POSTs JSON `{ repositoryId, workspaceId, repositoryPath }` to the agent's local HTTP port, with `|| true` so a failed POST never blocks the git operation.

Hooks are written during Sync so any repository that has been synced at least once will have them.

---

## 7. Feature Workflows

### 7.1 Workspace Sync

Sync is the primary maintenance operation. It fetches the latest state of every repository in a workspace.

```
User clicks Sync
→ WorkspaceRepositories.razor.cs ExecuteSyncAsync
→ WorkspaceSyncHandler.HandleAsync(workspaceId, repositoryIds?)
→ WorkspaceGitService.SyncAsync(workspaceId, repositoryIds, skipDependencyLevelPersistence)
  ├── Parallel: SyncRepository(repoId) × MaxParallelOperations
  │     Agent: git fetch, GitVersion, commit counts, branch detection, csproj scan
  │     Returns: RepositorySyncResult (version, branch, counts, projects, upstream)
  ├── PersistVersionsAsync: bulk-update WorkspaceRepositoryLink rows
  ├── WriteSyncHooks: write .git/hooks to every synced repo
  ├── MergeWorkspaceProjectDependenciesAsync: merge ProjectDependency rows
  └── PersistRepositoryDependencyLevelAndDependenciesAsync: recompute levels
→ WorkspaceSynced broadcast → UI refreshes
```

**Dependency-level sync** computes topological levels across the entire workspace. A repo with no dependencies gets level 1; each dependent repo gets a higher level. All repos at the same level can be built and pushed in parallel. This drives the level-header grouping in the grid.

### 7.2 Push Workflow

Push is the most complex workflow because it involves dependency ordering, optional package-registry synchronization, and precise UI refresh coordination.

```
User clicks Push
→ WorkspaceRepositories.razor.cs ExecutePushAsync
  ├── WorkspacePushHandler.HandleAsync(payload)
  │     └── PushOrchestrator.RunAsync(payload)
  │           ├── (optional) PackageRegistrySyncService: wait for packages
  │           └── WorkspacePushService.RunPushAsync or RunPushReposParallelAsync
  │                 ├── Build push plan (dependency-ordered levels or parallel)
  │                 ├── Parallel: PushRepository(repoId) × MaxParallelOperations
  │                 │     Agent: git push --set-upstream (if needed) + token auth
  │                 └── UpdateCommitCountsAndUpstreamAfterPushAsync
  │                       ├── Parallel: GetCommitCounts per pushed repo
  │                       ├── Update WorkspaceRepositoryLink rows (counts → 0)
  │                       └── WorkspaceSynced broadcast
  └── finally: RefreshFromSync() on Blazor renderer context
```

**Synchronized push** (dependency-ordered):
1. Compute push plan grouped by dependency level.
2. For each level: push all repos in that level in parallel, wait for completion.
3. After each level: poll NuGet until packages for that level are available, then advance to the next level.
4. After all levels: update commit counts and broadcast.

**Non-synchronized push** (parallel):
All repos pushed in parallel in one pass; no NuGet polling.

**Pre-push hook interaction:**
Git's pre-push hook fires before the actual push data transfer. `PushHookSyncCommand` intentionally sends only `Version` and `Branch` — no commit counts — because they would be stale (push not yet complete). The authoritative post-push counts come from `UpdateCommitCountsAndUpstreamAfterPushAsync` via explicit `GetCommitCounts` commands.

### 7.3 Dependency Update Workflow

```
User clicks Update
→ WorkspaceUpdateHandler.HandleAsync(workspaceId, level?)
→ DependencyUpdateOrchestrator.RunAsync(...)
  ├── WorkspaceGitService.RefreshWorkspaceProjectsAsync
  │     Agent: read all .csproj files, return package references
  ├── WorkspaceProjectRepository.GetUpdatePlanAsync
  │     Returns: repos with stale PackageReference versions
  ├── Per dependency level (if level-ordered) or single pass:
  │   ├── WorkspaceGitService.SyncDependenciesAsync
  │   │     Agent: CsProjFileService.UpdatePackageVersionsAsync
  │   ├── WorkspaceGitService.CommitDependencyUpdatesAsync
  │   │     Agent: git add -A + git commit -m "chore(deps): update..."
  │   └── WorkspaceGitService.RefreshVersionsAsync
  │         Agent: GitVersion only; updates GitVersion in WorkspaceRepositoryLink
  └── WorkspaceSynced broadcast
→ (optional) RunFileVersionUpdateAndGetUpdatedFilesAsync
      Agent: substitute {repoName} tokens in configured files
      → user confirms commit or auto-commit
```

### 7.4 Branch Orchestration

Single-repo and workspace-wide branch operations:

- **Create branch:** `CreateBranch` command; optionally pattern-based across workspace.
- **Checkout:** `CheckoutBranch` command; post-checkout hook fires and sends `SyncCommand`.
- **Set upstream:** `SetUpstreamBranch` after first push of a new branch.
- **Sync to default:** `SyncToDefaultBranch` — checkout default branch, prune feature branch if no drift, pull latest. Used after a PR is merged.
- **Delete branch:** `DeleteBranch` with safety checks.
- **Refresh branches:** `RefreshBranches` command; updates `RepositoryBranch` rows.

### 7.5 Pull Request & CI Status

PR state is fetched via the GitHub REST API (not the gh CLI) from the App's `GitHubPullRequestService`. The App calls `GET /repos/{owner}/{repo}/pulls?state=all&head={owner}:{branch}&per_page=1`.

Mergeability states:

| `MergeableState` | Meaning | Badge |
|-----------------|---------|-------|
| `clean` | No conflicts | Green |
| `dirty` | Merge conflict | Red |
| `unstable` | CI running/failing | Yellow (`#d29922`) |
| `blocked` | Review/protection required | Orange (`#e67700`) |
| `unknown` / null | Not yet computed | Gray |

PR state is persisted in `WorkspaceRepositoryPullRequest` and refreshed whenever a repository row is updated (sync, push, hook).

### 7.6 Connector Health Monitoring

`TokenHealthBackgroundService` runs a periodic loop. `ConnectorHealthService` tests each connector by calling the API with the stored token. Failures set `Connector.IsHealthy = false` and record `LastError`. The `ConnectorHealthException` type allows callers to distinguish health failures from other errors.

---

## 8. Concurrency Model

### 8.1 App-side: MaxParallelOperations

A single `Workspace:MaxParallelOperations` setting (default 16) governs all parallel workspace work in the App:

- `WorkspaceGitService` — all parallel agent command fan-outs.
- `PackageRegistrySyncService` — parallel NuGet package lookups.
- `SyncBackgroundService` — default worker count (overridden by `Sync:MaxConcurrency`).

All parallelism uses `SemaphoreSlim(_maxConcurrent)` + `Task.WhenAll`.

### 8.2 Agent-side: MaxConcurrentCommands

`AgentOptions.MaxConcurrentCommands` (default 8) controls how many workers consume the job queue. Bounded `Channel<JobEnvelope>` with capacity `max(MaxConcurrentCommands × 2, 64)` provides back-pressure.

### 8.3 Sync deduplication

`SyncBackgroundService` deduplicates by `(repositoryId, workspaceId)`: only one sync request per repo is in-flight or queued at a time. Controlled by `Sync:EnableDeduplication` (default `true`).

### 8.4 WorkspaceSynced debounce

`WorkspaceRepositories.razor` uses a 200ms debounce on `WorkspaceSynced` events to avoid cascading reloads when multiple events arrive in a burst (e.g. after a multi-repo push).

### 8.5 Push UI refresh (post-fix)

After the push bug fix in this version:
1. `PushHookSyncCommand` no longer sends stale commit counts via `SyncCommand`.
2. `UpdateCommitCountsAndUpstreamAfterPushAsync` is the sole source of post-push commit count updates.
3. `RefreshFromSync()` is called in the `finally` block of `ExecutePushAsync` in the Blazor renderer context, making it the authoritative UI reload for the push result.

---

## 9. Security Design

### 9.1 Token storage (Level 2 — AES-256-GCM)

Connector tokens (`Connector.UserToken`) are stored encrypted using AES-256-GCM via `AesGcmTokenProtector`, which is backed by ASP.NET Core Data Protection. The key store is in the volume-mounted `/app/db/DataProtection-Keys/` directory so keys survive container restarts.

`ConnectorHelpers.ProtectToken` and `UnprotectToken` are the only call sites. A one-time migration (`MigrateConnectorUserTokenBase64Async`) handles upgrading legacy Base64-encoded values.

### 9.2 Git authentication

All remote git commands on the Agent pass the token via `-c http.extraHeader="Authorization: Basic <base64(token)>"`. Tokens are obtained by the App at command time and passed in the command arguments; they are never persisted on disk by the Agent.

The `IAgentTokenProvider` interface abstracts token acquisition for agent commands that need to contact remotes. For hook-triggered flows where no token is in the command args, the agent calls back to the App via the HTTP connector endpoint.

### 9.3 Agent HTTP listener

The agent HTTP listener binds only to `127.0.0.1` (loopback), so it is not accessible from the network. This limits the hook attack surface to local processes.

### 9.4 Transport security

The App-Agent SignalR connection uses the App's HTTPS endpoint. Data Protection keys are used for ASP.NET Core cookie/session protection.

---

## 10. Architecture Strengths

### Process isolation is well-designed
The App/Agent split is a genuine architectural advantage. The App can be containerized, upgraded, or replaced without disrupting the Agent. The Agent can be updated independently. This also means the App never needs filesystem permissions on the host.

### Command pattern with typed request IDs
The `requestId` + `AgentResponseDelivery` TCS pattern is clean. Each command call is an awaitable task from the App's perspective. The queue on the Agent prevents overload without blocking the App's SignalR receive handler.

### Dependency-level topology
The Kahn topological sort for computing dependency levels is sound. It naturally handles N-deep dependency graphs and detects cycles (sets `DependencyLevel = null`). The same computed levels drive push ordering, sync ordering, UI grouping, and dependency stats.

### Normalized dependency persistence
`ProjectDependency` rows are computed per-repo and merged centrally. The same persistence logic (`PersistRepositoryDependencyLevelAndDependenciesAsync`) handles both full-workspace sync and single-repo sync. File-config dependencies are folded into the same edge set so all consumers automatically see them.

### Token security at rest
AES-256-GCM with Data Protection key management is a proper cryptographic solution. The migration path from Base64 is handled transparently.

### Hook-based passive sync
Git hooks provide real-time state updates without polling. Developers working in their IDE trigger state refreshes automatically. The debounce on `WorkspaceSynced` prevents thrash.

---

## 11. Architecture Weaknesses & Risks

### Single agent connection is a single point of failure
The App tracks one Agent connection ID. If the Agent disconnects mid-operation (network hiccup, process crash), any in-flight `SendCommandAsync` will hang until timeout. There is no reconnect-and-retry for in-progress commands. This is acceptable for a developer tool but is brittle for long operations.

### SQLite for multi-user concurrency
SQLite is write-serialized. For the current single-user use case this is fine. If GrayMoon is ever deployed for multiple simultaneous users (e.g. a team server), write contention during sync operations would become a bottleneck. The EF Core context scoping would also need review.

### Unbounded sync channel in SyncBackgroundService
`SyncBackgroundService` uses an unbounded channel. Git hooks can enqueue faster than the workers consume in large workspaces. Memory growth is bounded by repo count in practice, but there is no back-pressure mechanism.

### `AgentResponseDelivery` is a static dictionary
Static state is global across the process lifetime. If a bug causes a `TCS` to never complete, it leaks indefinitely. There is no timeout-based cleanup for stale entries.

### WorkspaceGitService is a God class
`WorkspaceGitService` handles sync, refresh projects, push, update, create branches, commit sync, and more. It has grown into a large, difficult-to-test orchestration class. Individual concerns should be extracted into focused services (the `WorkspacePushService` / `PushOrchestrator` extraction is a good precedent).

### Root path `null` fallback silently changes behavior
When a workspace has `RootPath = null`, it falls back to the global Settings value. If Settings is changed, all `null`-RootPath workspaces silently "move" to the new path. Workspaces created before the per-workspace `RootPath` column existed are particularly at risk.

### Workspace UpdateAsync does not persist RootPath
`WorkspaceRepository.UpdateAsync` saves only `Name` and repository links. If the user edits a workspace's root path in the UI, the change is not persisted. The edit form binds `editingWorkspaceRootPath` but it never reaches the database.

### BranchEndpoints.cs uses global root path for one call
In the sync-to-default flow in `BranchEndpoints.cs`, one call uses `GetRootPathAsync()` (Settings) instead of `GetRootPathForWorkspaceAsync(workspace)`. This causes the wrong path to be used if the workspace has a custom `RootPath`.

### No push retry / partial push recovery
If the synchronized push fails mid-level (e.g. network error on repo 3 of level 2), there is no recovery mechanism. The user must resolve the failure manually and re-push.

---

## 12. Known Improvements & Optimizations

### High priority

**Fix `UpdateAsync` to persist RootPath**  
`WorkspaceRepository.UpdateAsync` should save `workspace.RootPath` so the edit-workspace UI is functional. This is a one-line fix.

**Fix `BranchEndpoints` global root path**  
Replace `GetRootPathAsync()` with `GetRootPathForWorkspaceAsync(workspace)` in the sync-to-default endpoint.

**Timeout and cleanup for `AgentResponseDelivery`**  
Add a `CancellationToken`-linked cleanup or a background sweep to remove stale `TCS` entries that are never completed. Otherwise a lost response leaks indefinitely.

### Medium priority

**Extract WorkspaceGitService concerns**  
Continue the pattern established with `WorkspacePushService` / `PushOrchestrator`. Extract sync orchestration and branch orchestration into focused services. `WorkspaceGitService` should become a thin coordinator or be renamed to reflect its actual responsibility.

**Bounded sync channel**  
Replace the unbounded channel in `SyncBackgroundService` with a bounded channel (e.g. capacity = worker count × 4) to provide back-pressure. Combined with the existing deduplication, this bounds memory use from hook storms.

**Typed agent command args**  
Currently command args are serialized as `JsonElement` and deserialized per command. Adding typed request/response DTOs (as described in `GrayMoon.Agent-Jobs-Design.md`) would improve type safety, enable compile-time checks, and simplify testing.

**Push retry after failure**  
Track which repos in a push plan were successfully pushed vs. failed. Allow the user to retry only the failed repos without re-pushing the successful ones.

**Per-workspace agent (future)**  
For team deployments, multiple agents per workspace (or per machine) would allow the App to orchestrate work across machines. The current single-agent model is a design constraint, not a fundamental limitation — `AgentConnectionTracker` would need to become a multi-entry registry.

### Performance

**Reduce DB reads on sync**  
`SyncCommandHandler.HandleAsync` reads from the DB on every hook notification. For large workspaces with frequent hooks (active development), this creates read pressure. A short-lived in-memory cache of workspace repo link IDs per workspace would reduce DB round-trips.

**Batch `WorkspaceSynced` after multi-repo operations**  
After a full workspace sync or push, a single `WorkspaceSynced` broadcast is sufficient. Extra broadcasts from intermediate steps trigger extra debounce resets. The fix in 0.1.0-main.177 removes three duplicate broadcasts from the push path; the same review should be applied to sync paths.

**GitHub API pagination**  
`GitHubService` uses `per_page=1` for PR lookups. For organizations with many repos, consider caching PR results per repo with a short TTL to reduce API calls on page load.

**SQLite WAL mode**  
Enable `PRAGMA journal_mode=WAL` on the SQLite connection to allow concurrent reads during writes. This reduces contention between the background sync worker and the UI read paths.

---

## Appendix: Page Inventory

| Route | Page | Purpose |
|-------|------|---------|
| `/` | `Home.razor` | Default workspace landing; redirects to workspace repos |
| `/workspaces` | `Workspaces.razor` | Create, edit, delete workspaces; import from disk |
| `/workspace/{id}` | `WorkspaceRepositories.razor` | Main control panel: sync, push, update, branches, PR status, dependencies |
| `/workspace/{id}/dependencies` | `WorkspaceDependencies.razor` | Dependency graph visualization |
| `/workspace/{id}/projects` | `WorkspaceProjects.razor` | `.csproj` project list |
| `/workspace/{id}/packages` | `WorkspacePackages.razor` | NuGet package list and mismatch view |
| `/workspace/{id}/files` | `WorkspaceFiles.razor` | File configuration and version patterns |
| `/workspace/{id}/actions` | `WorkspaceActions.razor` | GitHub Actions status per repo |
| `/repositories` | `Repositories.razor` | Repository list, import from GitHub |
| `/connectors` | `Connectors.razor` | GitHub/NuGet/Docker connector management |
| `/agent` | `Agent.razor` | Agent status, prerequisites, install/uninstall |
| `/settings` | `Settings.razor` | Global settings (root path, preferences) |

## Appendix: Agent Configuration (`appsettings.json`)

| Key | Default | Meaning |
|-----|---------|---------|
| `Agent:HubUrl` | `http://localhost:8384/hub/agent` | App SignalR endpoint |
| `Agent:ListenPort` | `9191` | Local HTTP port for git hooks |
| `Agent:MaxConcurrentCommands` | `8` | Parallel job worker count |
| `Agent:ReconnectDelaySeconds` | `5` | Reconnect interval on disconnect |

## Appendix: App Configuration (`appsettings.json`)

| Key | Default | Meaning |
|-----|---------|---------|
| `Workspace:MaxParallelOperations` | `16` | Parallel operations for git and package work |
| `Sync:MaxConcurrency` | (none → MaxParallelOperations) | Sync background worker count |
| `Sync:EnableDeduplication` | `true` | De-duplicate in-flight sync requests |
