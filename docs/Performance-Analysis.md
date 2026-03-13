## GrayMoon Performance Analysis

This document provides a high-level performance analysis of GrayMoon (app + agent), identifies the primary performance-sensitive areas, and outlines concrete improvement opportunities and observability gaps.

---

## 1. System overview – what matters for performance

- **App (`GrayMoon.App`)**: ASP.NET Core 8 + Razor Components front-end, SQLite + EF Core persistence, background workers (sync, token health), and HTTP/SignalR endpoints.
- **Agent (`GrayMoon.Agent`)**: Long-running host process that performs git and filesystem operations and some package-registry work, with background workers and a bounded job queue.
- **Shared contracts (`GrayMoon.Abstractions`)**: Command/response DTOs between app and agent.

Performance is driven primarily by:

- **Parallel workspace operations** (sync, refresh projects, dependency sync, push, branch ops) coordinated by `WorkspaceGitService` and `SyncBackgroundService`, executed on the app + agent.
- **Database access** via `AppDbContext` on SQLite, especially on large workspaces and during bulk updates.
- **External calls** to GitHub and package registries via `GitHubService`, `NuGetService`, and agent-side utilities.
- **UI rendering and SignalR-driven updates** in `WorkspaceRepositories` and related components.

See `Parallelism-Analysis.md` for detailed concurrency mechanics; this document focuses on functional hotspots, risks, and improvements.

---

## 2. Key performance-sensitive flows

### 2.1 Workspace sync and refresh

- **Main classes**:
  - `WorkspaceGitService` – orchestrates sync, refresh projects, dependency sync, push, and branch creation across many repositories.
  - `SyncBackgroundService` – reads sync requests from an unbounded channel and runs `SyncSingleRepositoryAsync` with a configurable worker count.
  - `WorkspaceService` – resolves and manages workspace root paths, and forwards workspace-level commands to the agent.
- **Characteristics**:
  - Heavy use of **parallelism** (SemaphoreSlim + `Task.WhenAll`) to fan out per-repository work.
  - Mix of **agent commands**, **EF Core persistence**, and **external HTTP calls** (for package registries) inside these flows.
  - Sync requests are deduplicated in-flight, but the underlying channel is unbounded.

### 2.2 Git and dependency operations

- **Main classes**:
  - `WorkspaceGitService` – `SyncAsync`, `RefreshWorkspaceProjectsAsync`, `SyncDependenciesAsync`, `RunUpdateAsync`, `RunPushAsync`, `CreateBranchesAsync`.
  - Agent-side commands for `SyncRepository`, `RefreshRepositoryProjects`, `SyncRepositoryDependencies`, `PushRepository`, etc.
- **Characteristics**:
  - Uses a single **`MaxParallelOperations`** setting for workspace operations (see `WorkspaceOptions`).
  - For pushes with dependency synchronization, performs **polling loops** that:
    - Build dependency lists per level.
    - Repeatedly call `NuGetService.PackageVersionExistsAsync` (bounded by `_maxConcurrent`) until packages appear or a per-dependency timeout elapses.
  - Git operations themselves are I/O bound and offloaded to the agent, but **coordination and persistence** are app-bound.

### 2.3 Data access and workspace loading

- **Main classes**:
  - `AppDbContext` – SQLite-backed EF Core context with multiple indexed entities: connectors, repositories, workspaces, workspace repositories, branches, projects, dependencies, files, settings.
  - `WorkspaceRepository` – reads and writes workspace graphs, including navigation properties and related entities.
  - `WorkspaceProjectRepository` and related repositories – manage projects, dependency levels, and associated data.
- **Characteristics**:
  - Many read paths (especially in UI) load **entire workspace graphs** with eager includes for repositories, connectors, pull requests, and branches.
  - Writes often occur in **per-repository loops**, sometimes with batched pre-loading and then per-repository updates.

### 2.4 UI rendering and SignalR updates

- **Main components**:
  - `WorkspaceRepositories` page (`.razor` + `.razor.cs`) – primary UI for viewing and managing workspace repositories, dependencies, and operations.
  - SignalR hubs for workspace sync and agent events.
- **Characteristics**:
  - Uses a **debounced** handler for `WorkspaceSynced` and `RepositoryError` events, but still reloads workspace data from a new `DbContext` scope on each (expensive for large workspaces).
  - Client-side filtering, grouping, and overlay state are maintained in memory over a list of `workspaceRepositories` entries – trivial with small counts, but can become noticeable for very large workspaces.

### 2.5 Agent job processing

- **Main components**:
  - `JobBackgroundService` – agent-side background service with `MaxConcurrentCommands` workers consuming a bounded queue.
  - `HookListenerHostedService` – receives local hook HTTP requests and enqueues jobs.
- **Characteristics**:
  - Uses a bounded channel to maintain backpressure on agent work.
  - Tracks job latencies with `Stopwatch` and logs them, which provides a useful **performance signal** for command-level timing.

---

## 3. Identified performance risks and trade-offs

### 3.1 Concurrency stacking and configuration complexity

- **Observation**:
  - Independent concurrency controls exist at multiple layers:
    - `WorkspaceOptions.MaxParallelOperations` for workspace operations and some package-registry checks.
    - `Sync:MaxConcurrency` for sync worker count (falls back to `MaxParallelOperations`).
    - `AgentOptions.MaxConcurrentCommands` for agent job workers.
  - These can stack in ways that are unintuitive when all set to high values (e.g., many sync workers fanning out to many agent commands, each with internal parallelism).
- **Risk**:
  - On large workspaces or many simultaneous workspaces, misconfiguration can lead to **spiky CPU and disk utilization** and more contention on SQLite.
- **Mitigation ideas**:
  - Document recommended ranges for `MaxParallelOperations`, `Sync:MaxConcurrency`, and agent `MaxConcurrentCommands` based on core count and typical workspace size.
  - Enforce upper bounds or warn when configurations are extreme (e.g., > 4 × logical processors).

### 3.2 Unbounded sync channel with limited deduplication

- **Observation**:
  - `SyncBackgroundService` uses `Channel.CreateUnbounded` and a deduplication dictionary keyed by `(RepositoryId, WorkspaceId)`.
  - Deduplication prevents duplicate items, but **does not bound the queue** when requests involve many unique pairs.
- **Risk**:
  - Under bursty or adversarial load, the sync queue can grow large, increasing memory usage and latency before processing.
- **Mitigation ideas**:
  - Add configuration for a **soft or hard queue size cap** and return 429 / a structured error when exceeded.
  - Expose queue depth metrics (already partially present) and track over time, optionally emitting alerts when depth crosses thresholds.

### 3.3 EF Core eager loading and large workspaces

- **Observation**:
  - Core UI operations (e.g., `WorkspaceRepositories` reloads) use repository methods that **fully load workspace graphs** via `Include` / `ThenInclude`.
  - This is repeated on:
    - Page initialization.
    - SignalR `WorkspaceSynced` and `RepositoryError` events (after debounce).
    - Certain modal close or action-completion flows that call `ReloadWorkspaceDataAsync`.
- **Risk**:
  - For workspaces with many repositories, branches, and pull requests:
    - Eager loads can produce **large object graphs**, increasing memory usage and GC pressure.
    - Repeated loads for minor updates can cause **visible latency** in the UI and increased DB contention.
- **Mitigation ideas**:
  - Introduce **projection-based, read-model queries** for common UI views (e.g., workspace repository summary without full navigation graphs).
  - Consider **incremental updates** where only affected repositories/rows are reloaded after operations, rather than full workspace reloads.
  - Add basic **paging or lazy loading** for very large workspaces in the grid.

### 3.4 Polling loops for dependency availability

- **Observation**:
  - `WorkspaceGitService` push flows that wait for package versions to appear in registries:
    - Repeatedly call `NuGetService.PackageVersionExistsAsync` for many dependencies in parallel.
    - Sleep for a fixed interval (1 second) between checks, until all packages appear or timeouts are reached.
- **Risk**:
  - For large projects or slow registries:
    - Can generate substantial **HTTP traffic** and logs over extended periods.
    - Could delay pushes or block other operations that depend on the same workflows.
- **Mitigation ideas**:
  - Make polling intervals and the **maximum concurrency** for these checks separately configurable from `MaxParallelOperations`.
  - Cap the number of dependencies checked in one wave and/or aggregate by package ID.
  - Consider registry-specific optimizations (e.g., smarter caching of package indices).

### 3.5 UI-level inefficiencies on large datasets

- **Observation**:
  - `WorkspaceRepositories` keeps the entire set of workspace repositories in memory and performs filtering/grouping client-side.
  - Each refresh rebinds the full list and triggers component rerender.
- **Risk**:
  - With hundreds or thousands of repositories, client-side filtering and repeated full rebinds can:
    - Increase **render times**.
    - Make **interaction laggy** when filters or modes change quickly.
- **Mitigation ideas**:
  - Introduce server-side projections with paging or partial loading for large workspaces.
  - Use **virtualized lists/grids** when repository counts exceed a threshold.
  - Cache and reuse immutable portions of the view model where possible.

---

## 4. Strengths and existing mitigations

- **Clear concurrency model**:
  - A single `MaxParallelOperations` setting drives most workspace parallelism, with documented override points (`Sync:MaxConcurrency`, agent command concurrency).
  - `Parallelism-Analysis.md` already documents this, reducing configuration surprises.
- **Backpressure on the agent**:
  - The agent uses a bounded job queue and `MaxConcurrentCommands` workers, preventing unbounded job explosion.
- **Structured logging and timing**:
  - Extensive use of structured Serilog logging on both app and agent.
  - `JobBackgroundService` measures job durations; many orchestrators log detailed step-level messages.
- **Resilient external calls**:
  - `GitHubService` uses Polly with retries, exponential backoff, and rate-limit handling, smoothing out transient API issues rather than failing fast or hammering endpoints.
- **Targeted caching**:
  - `WorkspaceService` caches workspace root paths with explicit refresh/clear operations to reduce repeated DB reads and agent communications.

These building blocks make it easier to **observe** and **tune** performance, rather than requiring large architectural changes.

---

## 5. Recommended improvements and profiling plan

### 5.1 Short-term, low-risk improvements

- **Document recommended configuration values**:
  - Add a short guide (or extend this doc) describing:
    - Typical `MaxParallelOperations` values for small/medium/large instances.
    - How `Sync:MaxConcurrency` interacts with `MaxParallelOperations`.
    - How to size agent `MaxConcurrentCommands` based on CPU and expected workload.
- **Guard the sync queue**:
  - Add a soft cap or telemetry-based warning for `SyncBackgroundService` queue depth.
  - Consider returning explicit “queue full” responses or backoff hints when depth is above a configurable threshold.
- **Optimize common workspace read paths**:
  - Introduce a lightweight DTO-based query for the main workspace grid, avoiding unnecessary navigation graphs when not needed.
  - Use `AsNoTracking` for read-only queries that feed the UI where change tracking is unnecessary.

### 5.2 Medium-term improvements

- **Incremental UI updates instead of full reloads**:
  - On `WorkspaceSynced` events, reload only the **affected repositories** or levels rather than the entire workspace.
  - Reuse existing in-memory lists and update changed items by key where feasible.
- **Smarter dependency-availability checks**:
  - Separate the concurrency and polling configuration for dependency checks from global `MaxParallelOperations`.
  - Cache recent package existence checks (e.g., within a short time window) to avoid repeatedly hitting registries for the same versions.
- **Better scaling for large workspaces**:
  - Introduce paging/virtualization in the `WorkspaceRepositories` grid.
  - Provide a collapsed/summary view for large workspaces that shows high-level status and allows drill-down by level or filter.

### 5.3 Profiling and observability

- **Add performance-centric metrics** (e.g., via logs, Application Insights, or Prometheus/Grafana):
  - Sync queue depth distribution over time.
  - Average/percentile durations for:
    - Workspace syncs (`SyncSingleRepositoryAsync` and `SyncAsync`).
    - Refresh projects and dependency sync.
    - Push operations, including time spent in dependency-wait loops.
  - EF Core query counts and durations for key endpoints/pages.
- **Targeted profiling sessions**:
  - Use realistic large workspaces and run:
    - Mass sync, refresh, and push operations with different `MaxParallelOperations` settings.
    - Heavy GitHub and registry operations (e.g., many repositories, many dependencies).
  - Capture traces to identify:
    - Hot queries.
    - Slow external calls.
    - GC pressure from large object graphs (especially workspace reload paths).

---

## 6. Summary

GrayMoon’s architecture already separates UI, coordination, and heavy work (via the agent), with explicit concurrency controls and good logging. The main performance risks center around **stacked concurrency**, **unbounded sync queues**, **eager loading of large workspace graphs**, and **polling-based dependency checks**. With modest changes focused on configuration guidance, bounded queues, lighter read models, and improved observability, the system should scale more predictably as workspace size and operation frequency grow.

