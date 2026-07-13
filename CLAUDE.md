# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Coding conventions

- **Line endings:** Always use **Windows (CRLF)** in generated or edited code. Do not use LF-only.
- **Hyphens only:** Never use em dash (U+2014) or en dash (U+2013) anywhere - user-visible strings, UI markup, comments, XML docs, logs, CSS, or Markdown. ASCII hyphen-minus (`-`) only.
- **Primary constructors:** Prefer C# 12 primary constructors (`public sealed class MyService(ILogger<MyService> logger)`) for classes that only capture constructor parameters. Keep an explicit constructor only when the body has non-trivial side effects.
- **`sealed` by default:** All service, handler, command, and repository classes are `sealed`. Apply `sealed` to every new concrete class unless inheritance is explicitly required.
- **Modal state as sealed records:** UI modal state objects in page code-behind files (e.g. `WorkspaceRepositories.razor.cs`) use `private sealed record ModalState` with `init`-only properties and record-level defaults. Use a `sealed class` with mutable properties only when the modal needs to mutate state mid-display (e.g. a progress counter while fetching).
- **Agent DTOs as sealed classes:** Request and response types under `src/GrayMoon.Agent/Jobs/Requests/` and `src/GrayMoon.Agent/Jobs/Response/` are `sealed class` (not records) with `[JsonPropertyName("camelCase")]` on every property. Nullable fields default to `null`; required string fields use `string?` with a null-guard in the handler.

## Task completion

When you complete a task, write a single sentence summarizing what was done. Keep it to one sentence - never write a multi-line summary.

## Commands

```powershell
# Build the solution
dotnet build GrayMoon.slnx

# Run tests (xUnit) - three test projects: GrayMoon.Common.Tests, GrayMoon.Agent.Tests, GrayMoon.App.Tests
dotnet test src/GrayMoon.Common.Tests/GrayMoon.Common.Tests.csproj
dotnet test src/GrayMoon.Agent.Tests/GrayMoon.Agent.Tests.csproj
dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj

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

- **GrayMoon.App** (`src/GrayMoon.App`) — ASP.NET Core **.NET 10** + Blazor Server, runs in Docker. Exposes the web UI and two SignalR hubs (`/hub/agent` for App↔Agent commands, `/hubs/workspace-sync` for browser broadcast). Never touches the local filesystem or runs git directly. SQLite database via EF Core at `/app/db` (volume-mounted). Uses `EnsureCreated()` — no incremental migrations. **New DB columns must be nullable or have a default value.**
- **GrayMoon.Agent** (`src/GrayMoon.Agent`) — .NET **10** console app / dotnet tool (`graymoon-agent` command) that runs on the developer's host machine. Packaged as a single binary; supports Windows Service (`Microsoft.Extensions.Hosting.WindowsServices`) and Linux Systemd (`Microsoft.Extensions.Hosting.Systemd`) hosting. CLI entry via `AgentCli.Build()` with subcommands: `run` (default), `install`, `uninstall`, `start`, `stop`. Connects to the App via SignalR, executes all git and filesystem operations, and exposes a local HTTP listener on `127.0.0.1:9191` for git hook callbacks. Job queue: bounded channel with up to 16 concurrent workers. Three hosted services manage the Agent lifecycle: `SignalRConnectionHostedService` (maintains hub connection), `HookListenerHostedService` (HTTP listener for git hook POSTs), and `JobBackgroundService` (dequeues and executes jobs).
- **GrayMoon.Abstractions** (`src/GrayMoon.Abstractions`) — Shared interfaces and DTOs used by both App and Agent. `Agent/AgentHubMethods.cs` contains all SignalR hub method name constants (`RequestCommand`, `ResponseCommand`, `SyncCommand`, `CommandOutput`, `ReportSemVer`, `ReportQueueStatus`).
- **GrayMoon.Common** (`src/GrayMoon.Common`) — Shared utilities including `CommandLineService` (process execution wrapper) and `Search/FilterSearchExpression` (boolean expression parser used by every UI grid).

### App → Agent command flow

The App sends commands to the Agent over SignalR using a `requestId` (GUID). `AgentBridge` calls `AgentResponseDelivery.WaitAsync(requestId)` (a `TaskCompletionSource`) then fires `RequestCommand` to the Agent. The Agent enqueues a `JobEnvelope` into a bounded `Channel<JobEnvelope>`, runs it in one of up to 16 concurrent workers, and calls back `ResponseCommand` with the same `requestId`. `AgentResponseDelivery.Complete` resolves the TCS, unblocking the App's awaiter. Intermediate output streams via `CommandOutput` events for the loading overlay terminal.

### Agent → App sync (hook-driven)

Git hooks (`post-commit`, `post-checkout`, `post-merge`, `pre-push`) POST JSON to the Agent's local HTTP listener (`/hook/notify` or `/hook/push`). The Agent processes these as `NotifyJobs` and pushes partial state updates back to the App via `SyncCommand` on the SignalR connection. `SyncCommandHandler` persists only non-null fields (partial update semantics) then broadcasts `WorkspaceSynced` to all browser clients.

### Agent command structure

The Agent uses `System.CommandLine`. Each agent operation implements `ICommandHandler<TRequest, TResponse>` in `src/GrayMoon.Agent/Commands/`. To add a new command: (1) define request/response DTOs, (2) create the handler class, (3) register it via `AddSingleton<ICommandHandler<TRequest, TResponse>, YourCommand>()` in `RunCommandHandler.cs`, (4) add it to the dispatcher dictionary in `CommandDispatcher.cs`, (5) add the hub method constant to `AgentHubMethods.cs`, (6) call via `AgentBridge.SendCommandAsync` on the App side.

### App layer structure

The App is organized into:
- `Components/Pages/` — Blazor Server pages and interactive components
- `Components/Shared/` and `Components/Modals/` — reusable UI components
- `Services/` — 60+ domain services (GitHub API, workspace operations, push orchestration, dependency update, etc.)
- `Data/` — EF Core `DbContext`, entity models, and repository classes
- `Api/Endpoints/` — REST API endpoints grouped by domain (About, Agent, Branch, Connector, Settings, Sync, Workspace)

### Orchestrators

Three orchestrators coordinate multi-step workflows across repositories:
- `DependencyUpdateOrchestrator` — drives level-by-level package version updates: updates `.csproj` files, runs `dotnet restore --force --no-cache`, commits each level, then moves to the next.
- `PushOrchestrator` — synchronized push: pushes level-by-level, waits for NuGet availability between levels so downstream consumers get the correct package version before their push starts.
- `NewFeatureOrchestrator` — automates branch creation, optional dependency update, commit, and push across selected repositories in one workflow.

### Handler services

`Workspace*Handler` services (`WorkspaceBranchHandler`, `WorkspacePushHandler`, `WorkspaceUpdateHandler`, `WorkspaceUndoPushHandler`, `WorkspaceCommitSyncHandler`, `WorkspaceSyncHandler`) are the page-facing layer above orchestrators and core services. They are stateless and accept UI callbacks (`Action<string> setProgress`, `Action<string> showToast`, `CancellationToken`) so Blazor components can delegate complex operations without owning workflow logic. When adding a new user-initiated multi-step operation, add it to the appropriate existing handler or create a new one rather than putting the logic in the component or a new orchestrator.

`WorkspacePendingActionsService` computes per-workspace notification state (unmatched dependencies, outgoing commits, incoming default-branch commits) by subscribing to workspace sync hub events. It powers `WorkspaceActionNotificationPanel`, the floating notification cards visible from any page.

### GHA live feed services

`GhaWorkflowLiveFeedService` polls `GetWorkflowRunJobsAsync` (jobs + steps) on adaptive intervals (2 s active, 3 s waiting, 15 s idle). It tracks step-status transitions and feeds the left pane of `GhaWorkflowLiveTerminal`. Rate limit state (`_rateLimitedUntil`) is shared across all terminals in the same Blazor circuit so one 429 pauses all.

`GhaStepLogFeedService` polls `GetJobLogsAsync` (the full job log text) and supports two modes: incremental group-parsed step output (`PollStepLogsAsync`) and a raw tail (`PollStepLogTailAsync`) that strips timestamps, filters blank lines, and returns the last N lines of the full log without parsing group markers.

### Filter search expression (GrayMoon.Common)

`FilterSearchExpression` parses boolean queries (`and`, `or`, parentheses, field prefixes like `repo:`, `type:`). All workspace grid and catalog pages use `FilterSearchInput` + a per-page `ISearchMatcher` implementation. `and` binds tighter than `or`; spaces imply `and`. Parse errors fall back to implicit-and word matching so users always see results.

### Dependency levels

Workspace repositories are topologically sorted into dependency levels (Kahn's algorithm). Level 1 = no dependencies; higher levels depend on lower ones. This drives: grid grouping, push ordering (synchronized push waits for NuGet availability level-by-level), and the dependency update flow. Dependency edges come from three sources, all merged before the sort runs: (1) csproj `<PackageReference>` entries, (2) file-config token names (`{repositoryname}` in `WorkspaceFileVersionConfig` patterns), and (3) user-declared `WorkspaceRepositoryCustomDependencies` edges.

### Token encryption

Connector tokens are AES-256-GCM encrypted at rest via `AesGcmTokenProtector` (backed by ASP.NET Core Data Protection). Keys live in `/app/db/DataProtection-Keys/`. All git remote operations pass the token at runtime via `-c http.extraHeader="Authorization: Basic ..."` — tokens are never written to disk by the Agent.

### File versioning

`WorkspaceFiles` (`/workspaces/{id}/files`) lets users pin arbitrary files from workspace repos. Each file can have a `WorkspaceFileVersionConfig` with a multi-line version pattern (`KEY={repositoryname}` per line) that maps file content tokens to workspace repo names.

- **`WorkspaceFileVersionService`** - two main operations: `UpdateAllVersionsAsync` (calls `UpdateFileVersions` agent command to do in-place token substitution) and `CheckAndPersistFileVersionStatusAsync` (calls `CheckFileVersions` agent command, persists results to `WorkspaceFileLineStatuses`, updates `OutOfDateFileRepos`/`TotalFileConfigRepos` counters on `WorkspaceRepositories`). Concurrent callers for the same workspace coalesce onto one in-flight check unless `forceFresh: true`.
- **`WorkspaceFileLineStatuses`** - one row per out-of-date token per file. Stores `TokenName`, `CurrentValue`, `ExpectedValue`. Cleared and rebuilt on every check.
- **`WorkspaceFile.IsMissingOnDisk`** - nullable bool set by `CheckFileVersions` when the file path no longer exists. Missing files are excluded from badge computation and version update.
- Agent commands `CheckFileVersions` and `UpdateFileVersions` live in `src/GrayMoon.Agent/Commands/`.
- File-config token names also contribute dependency edges to the topological sort (alongside csproj project deps). `ImplicitReferencedRepoIdsBySource` separates `FromProject` vs `FromFile` for badge tooltip clarity.

### Git Changes (in-app diff viewer)

`WorkspaceGitChanges` (`/workspaces/{id}/changes`) shows a combined, multi-repo file tree of staged/unstaged changes across every repository in a workspace, with a Monaco diff viewer, and lets users stage/unstage/commit without leaving the app.

- **Change detection is watcher-driven, not polled from the browser.** On the Agent, `GitRepositoryWatcherManager` lease-manages one `FileSystemWatcher` per repo (`GitRepositoryWatcher`) once the App has asked about that repo via the `GetGitChangeStatus` command. Watcher events are invalidation-only signals into `GitStatusRefreshCoordinator`, which debounces/coalesces them (state machine: `Clean`/`Dirty`/`Refreshing`/`RefreshingAndDirty`) behind a shared semaphore (`GitChangesOptions.MaxParallelRepositoryOperations`, default 16) so at most one scan plus one coalesced follow-up runs per repo at a time. Each scan shells out to `git status --porcelain=v2 -z --branch --untracked-files=all`, parsed by the process-free `GitPorcelainV2Parser` (Common). `GitChangesSnapshotCache` versions each result; `GitChangesSnapshotPublisher` pushes new snapshots to the App over the existing Agent-App SignalR connection.
- **The browser never receives snapshot payloads directly.** The App's `GitChangesSnapshotPushHandler` (run off `WorkspaceGitChangesWriteQueue`, a `Channel`-backed `BackgroundService` that serializes all Git-Changes SQLite writes through `IDbContextFactory`) persists the snapshot to `WorkspaceGitRepositoryStatus`/`WorkspaceGitChangeEntry` tables, then broadcasts a lightweight `GitChangesUpdated` signal over `WorkspaceSyncHub`. The page's own hub connection (`WorkspaceGitChanges.Realtime.cs`) reacts by re-reading the persisted projection via `WorkspaceGitChangesReadService` - opening the page never triggers a fresh Agent scan.
- **Stage/unstage/commit** flow through `IGitChangesAgentClient` to Agent commands `StageGitChangesCommand`/`UnstageGitChangesCommand`/`CommitGitChangesCommand`, which validate every path with `GitRepositoryPathValidator` (rejects absolute paths and `.`/`..` traversal) before calling `git add`/`git restore --staged`/`git commit -F -` via `GitCliRepositoryGitChangesService`. Large path sets are passed via `GitPathspecStdinWriter` (`--pathspec-from-file=-` on git >= 2.25, else batched args) to avoid Windows command-line length limits. Every mutation's resulting snapshot flows through the same write queue as watcher-driven updates - there is no separate optimistic UI state.
- **Monaco** is vendored under `src/GrayMoon.App/wwwroot/monaco/` and loaded globally via `App.razor`. `GitDiffViewer.razor`/`.razor.js` is the one component in the app using per-instance `IJSObjectReference` module interop (rather than global `window.*` scripts) so each diff editor instance can be created, updated, and disposed independently; `MonacoLanguageMapper` (Common) maps file extensions to Monaco language ids.
- **Monaco CSS must survive Blazor enhanced navigation.** Monaco injects its own `<link>`/`<style>` tags into `<head>` at runtime (base `editor.main.css`, theme colors, per-language syntax token colors) - none of that is ever part of any server response. Blazor's enhanced navigation reconciles `<head>` against the server-rendered markup on every SPA navigation and deletes any old-side node with no counterpart in the new response; `data-permanent` does **not** save these, since (per `blazor.web.js`'s own diff algorithm) it only prevents mismatched *pairing* between two candidate nodes - it does nothing for a node that has no candidate at all. Losing that CSS is what makes Monaco appear to "break" after navigating away from and back to a page hosting it: syntax highlighting disappears, scrollbar/layout rules break, and Monaco's hidden input-capture `textarea.inputarea` (normally invisible via `resize:none;color:transparent;z-index:-10`) shows up as a tiny raw resizable box. The fix in `GitDiffViewer.razor.js` (`trackHeadInjections`/`restoreHeadInjectionsIfMissing`) uses a `MutationObserver` to record every `<style>`/`<link>` Monaco adds to `<head>`, then re-appends any that are no longer `.isConnected` at the top of `init()` on every mount - Monaco itself never redoes this once `window.monaco` exists, since `ensureMonacoLoaded()` short-circuits past the AMD `require` that injected the CSS the first time.

### Custom dependencies

`WorkspaceRepositoryCustomDependencies` lets users declare explicit ordering edges between workspace repos. These user-declared edges are merged with csproj-derived and file-config-derived edges before Kahn's algorithm runs. Managed via `WorkspaceRepositoryCustomDependencyRepository` and the `CustomDependenciesModal`.

### Database schema

Schema is owned by EF Core but applied via `EnsureCreated()`. Core tables: `Connectors`, `Repositories`, `Workspaces`, `WorkspaceRepositories` (join with live state), `WorkspaceRepositoryPullRequest` (1:1 with link), `WorkspaceRepositoryActions` (CI status per link), `WorkspaceProject`, `ProjectDependency`, `WorkspaceFile`, `WorkspaceFileVersionConfig`, `WorkspaceFileLineStatuses`, `WorkspaceRepositoryCustomDependencies`, `RepositoryBranch`, `Settings`.

After `EnsureCreated()`, startup calls `Migrations.RunAllAsync(dbContext)` (`src/GrayMoon.App/Migrations.cs`). Each migration is an idempotent `ALTER TABLE` guarded by `pragma_table_info()`. To add a new column: add a `public static async Task MigrateXxxAsync(AppDbContext dbContext)` method to `Migrations.cs` (check `pragma_table_info('TableName') WHERE name='ColumnName'` before executing the `ALTER TABLE`), then call it at the end of `RunAllAsync`. No EF Core migration assembly is used.

### Background job system

Long-running App-side operations (restore, update, push orchestration) run as background jobs so they survive page navigation within the same browser tab.

- **`BackgroundJobService`** (Blazor-circuit-scoped) — keyed by URL absolute path (`/workspace/123/repositories`). `StartJob` is idempotent: if a job for that key is already `Running` it returns the existing handle. Disposing the service (tab closed) cancels all jobs.
- **`BackgroundJobHandle`** — state machine (`Running` → `Completed` / `Faulted` / `Aborted`). Exposes `ReportProgress(string)` for mid-job status updates, `Abort()` for user cancellation, and a `JobTerminalBuffer Terminal` that holds up to 800 log lines.
- **`TerminalSinkContext`** — `AsyncLocal`-based ambient context. `AgentBridge.SendCommandAsync` checks `TerminalSinkContext.Current` and routes all `CommandOutput` streaming lines to the active job's terminal automatically, without threading the buffer through every service call. Wrap a job's `Task.Run` body with `using var _ = TerminalSinkContext.Use(handle.Terminal)`.
- **`BackgroundJobOverlay`** layout component — reads `IBackgroundJobService.GetJob(currentPath)` on every navigation and renders `LoadingOverlay` when the job is `Running`. It also reads `AgentQueueStateService.GetTotalPendingCount()` to show pending agent tasks in the spinner.
- **`AgentQueueStateService`** — receives `ReportQueueStatus` SignalR events from the Agent (total + per-workspace pending counts). Use `HasWorkspaceJobsPending(workspaceId)` to disable workspace actions while the agent queue is busy.

**A running job must never touch page-instance-owned disposables.** `Dispose()` on a page component (e.g. `WorkspaceRepositories.razor.cs`) runs the moment Blazor tears down that component on route navigation - which happens even though the page's `JobService`-tracked background job (circuit-scoped, deliberately meant to survive navigation) keeps running in its own `Task.Run`. If that still-running job body calls back into a method that touches a disposable owned by the now-disposed page (e.g. `_reloadGate`/`_queryLoader` `SemaphoreSlim`/loader in `WorkspaceRepositories.Loading.cs`), it throws `ObjectDisposedException`, which the job's own `catch (Exception) { throw; }` propagates up to `BackgroundJobService.StartJob`'s `catch (Exception ex) { handle.MarkFaulted(ex); }` - killing the *entire remaining job* (e.g. an Update-then-Push job never reaches the push phase), not just the UI refresh that triggered it. Any method a job body calls after its actual git/agent work completes (grid-refresh helpers, `ReloadWorkspaceDataFromFreshScopeAsync`, etc.) must guard on `_disposed` at entry and swallow `ObjectDisposedException`/`InvalidOperationException` around the call, exactly like `ReloadWorkspaceDataAfterCancelAsync` already does.
- The Git Changes page (`WorkspaceGitChanges`) follows the same `JobService`/`BackgroundJobOverlay` pattern via its own `PageJobKey` (`WorkspaceGitChanges.JobHelper.cs`) - including its initial persisted-state load, which runs as a job (`StartInitialLoadJob`) purely so the standard `LoadingOverlay` covers first open instead of a bespoke inline "Loading..." message. `BackgroundJobOverlay` is keyed per exact URL path, so a job started on Repositories is only visible while you are back on the Repositories URL, not while sitting on Changes (or vice versa) - that is intentional, not a gap to fix reflexively.
- **Do not disable Blazor enhanced navigation (`data-enhance-nav="false"`) to work around a page-specific bug.** It forces a full browser page reload for that link, which tears down the entire SignalR circuit - including every circuit-scoped `BackgroundJobService` job, regardless of which page started it. `NavMenu.razor`'s Changes link briefly carried this (originally added to mask a Monaco CSS issue - see the Monaco bullet above) and it silently killed in-flight Repositories jobs the moment you navigated to Changes. Fix the underlying problem under normal SPA navigation instead.

### Toast service

`IToastService` (singleton). Call `Show(message)` / `ShowError(message)` from any service or component. The `Toast` component auto-dismisses after 2.5 s (normal) or 6 s (error).

### REST API registration

`ApiEndpointRegistration.MapApiEndpoints()` is called in `Program.cs`. Each feature adds a static extension method (e.g., `MapWorkspaceEndpoints()`). Add new API groups there rather than scattering `app.Map*` calls.

### CSS conventions for the loading overlay terminal

Global terminal styles live in `wwwroot/css/loading-overlay.css`. The embedded GHA terminal in `WorkspaceActions` has a split-pane layout (`actions-gha-terminal__pane--left` / `--right`) in `GhaWorkflowLiveTerminal.razor.css`. Line color variants: `--out` (green/yellow by scheme), `--err` (red), `--cmd` (gray), `--raw` (white). Scheme toggling uses `.loading-overlay-terminal--scheme-yellow`. High-specificity overrides needed to beat the running-row `td { color }` inheritance use `!important` in `loading-overlay.css`.
