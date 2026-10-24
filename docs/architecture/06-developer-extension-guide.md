# 06 - Developer Extension Guide

Rules for changing GrayMoon without breaking its invariants. Every rule names the code that enforces it or the reference implementation to copy. Coding conventions (CRLF, ASCII hyphen, `sealed`, primary constructors, Worker DTO shape) live in `GrayMoon/CLAUDE.md`; DbContext and Feature-context rules also live in `GrayMoon/AGENTS.md` and `.cursor/rules/feature-context-scoping.mdc`. This document does not replace them.

## 1. Start by locating the ownership boundary

Before changing behavior, classify the feature.

### Local Git/filesystem behavior

Belongs in `GrayMoon.Worker`.

Examples:

```text
git command
filesystem read/write
project discovery
restore
Git Changes diff/status
hook processing
AI CLI execution and Git evidence collection
```

### Multi-repository orchestration

Belongs in `GrayMoon.App`.

Examples:

```text
dependency-ordered update
synchronized push
batch branch creation
batch PR flow
batch recomputation
```

### Reusable user/automation operation

Expose it through `GrayMoon.Application` where appropriate.

### Persisted read-heavy page

Prefer a dedicated query service.

---

## 2. Do not let pages become the domain layer

A page may own:

```text
modal visibility
button state
dialog wording
toast presentation
navigation
local selection
```

A page should not be the only place that knows:

```text
whether an operation is safe
how a multi-step operation works
how destructive eligibility is calculated
which repositories must be changed
```

Return to Default and Remove Feature (`AnalyzeRemoveFeatureAsync` -> plan -> dialog -> `RemoveFeatureAsync`) are the models to follow:

```text
headless analysis
structured plan
UI confirmation
headless execution
```

---

## 3. Preserve App-Worker separation

Never add code to GrayMoon.App that shells out to local Git, runs a CLI, or touches the developer's disk just because it is easier.

If App needs local state:

```text
define/reuse Worker command
send concrete request
Worker executes
return typed result
persist in App
```

This keeps Docker/web deployment valid: in Docker the App cannot see the developer's paths, so even `Directory.Exists` / `File.Exists` / `Path.Exists` answers must come from the Worker (for worktrees: `InspectWorktree`).

Enforcement: `GrayMoon.App.Tests/FeaturesDiskAccessArchitectureTests` scans every `src/GrayMoon.App/Services/Features/*.cs` file (top directory only) and fails on `Directory.`, `File.` or `Path.Exists(`. `WorkspaceContextPathResolver.cs` is allow-listed because it only builds path strings. The test covers only that folder; elsewhere in the App the rule is enforced by review. The App's own files (database, `graymoon.key`, Worker secret, backups) are the only legitimate App disk access.

---

## 4. Use application contracts for reusable mutations

If a workflow is needed by:

```text
Blazor
REST
future automation
```

it should not have three implementations.

Preferred:

```text
IWorkspaceSomethingOperations
        |
        +- page calls
        +- endpoint calls
```

The endpoint should be thin. Pages and endpoints must not resolve `WorkspaceGitService`, orchestrators or `IGitChangesWorkerClient` directly (see `GrayMoon/CLAUDE.md` "Application facades and workspace lock" for the allowed leftovers).

REST endpoints resolve the special Workspace context (`GetOrCreateSpecialWorkspaceContextIdAsync`); no endpoint maps `IWorkspaceFeatureOperations`. A new endpoint that should act on a Feature needs an explicit context parameter, not an implicit one.

---

## 5. Use query services for large pages

Do not load the full Workspace aggregate with every navigation property for grids that may contain many rows.

Follow existing query patterns:

```text
CountAsync
GetIndexAsync
GetPageAsync / GetByIdsAsync
GetSnapshotAsync
GetHeaderStateAsync
```

Design indexes together with the query (indexes for existing databases are a strict migration step, section 20). Avoid N+1: `IWorkspaceBranchOperations.GetBranchesForRepositoriesAsync` is the reference bulk read (one fresh DbContext, a fixed number of queries whatever the repository count, guarded by a query-count test).

---

## 6. Persist expensive observations

If obtaining state requires:

```text
Git
GitVersion
GitHub API
package registry
filesystem scan
project parse
```

consider whether the UI should render a persisted projection first.

GrayMoon favors fast persisted reads plus background refresh. Keep network calls off the page-open path.

---

## 7. Define state authority

For every new persisted field answer:

```text
Who is authoritative?
When is it refreshed?
When is it invalid?
Can another operation know only part of it?
What is the correct uniqueness key (does it need the context id)?
Is it per machine (DB only) or shared (graymoon.json)?
```

Do not store values that cannot be reliably refreshed or invalidated.

---

## 8. Respect partial repository snapshots

`WorkspaceRepositoryStateWriter` intentionally writes only probed groups.

When adding new repository state:

- add a clear snapshot group;
- mark it probed only when the operation truly obtained authoritative data; a step skipped by profile or failure reports `*Probed = false`, never `true` with null;
- test that unrelated partial operations preserve existing values.

Hook paths send partial snapshots too (`RepositoryStateSnapshot.IsBranchRefsOnly` from the `reference-transaction` hook carries branch lists only). A new hook-driven field must be null-when-unknown so `SyncCommandHandler`'s "persist only non-null fields" semantics hold.

---

## 9. Batch recomputation

When one user action updates several repositories:

Bad:

```text
repo A write
recompute whole Workspace
repo B write
recompute whole Workspace concurrently
repo C write
recompute whole Workspace concurrently
```

Good:

```text
repo A write
repo B write
repo C write
all complete
recompute once
broadcast once
```

Use the existing recompute-scope pattern: `WorkspaceStateRecomputeScope.RecomputeDependencyStatsAsync` is the only sanctioned recompute entry, and hook bursts go through `SyncRecomputeCoalescer` (keyed `"{workspaceId}:{contextId}"`, at most one run per key at a time, every caller's write included in a run that started after it). `ReturnToDefaultUnattendedBatchAsync` is the reference bulk flow (parallel per-repo work, one recompute and broadcast at the end, only if something changed).

---

## 9A. Parallelizing multi-repository work

Guiding principle: do less work and launch fewer processes before adding parallelism.

Recipe:

1. Fan out only Worker calls, HTTP calls, or work that has its own DI scope or `IDbContextFactory` context.
2. Bound with `SemaphoreSlim(Math.Max(1, WorkspaceOptions.MaxParallelOperations))` (default 16), or `GitChangesOptions.MaxParallelRepositoryOperations` (default 8) for Git Changes. No hard-coded constants.
3. Collect results per item and merge them in input order.
4. Use per-item try/catch so one repository's failure is attributed to it; let `OperationCanceledException` through.
5. Persist once at the end through one context (or each item through its own factory context).
6. Test with `FakeWorkerBridge` + `ConcurrencyProbe` (`WorkspaceGitServiceParallelismTests`), including a limit of 1.

Must stay sequential:

| Work | Why |
|---|---|
| Dependency-level loops (`DependencyUpdateOrchestrator`, `WorkspacePushService`) | Level N+1 consumes level N's versions/packages. Overlap is only the two-lane Update and Push (`UpdateAndPushOrchestrator`). |
| Writes through the shared scoped `AppDbContext` | Not thread-safe (section 20E). |
| `WorkspaceFeatureReconciler` apply | Single-flight by design. |
| Git Changes monitoring and line-stats sweeps | Each scan is already bounded. |
| Single-reader channel queues (`FeatureRemovalQueue` dispatcher, `WorkspaceGitChangesWriteQueue`, `WorkerSyncNotificationQueue`, `SyncBackgroundService`) | Ordering and SQLite writer serialization. |
| Worker `DotnetRestoreCommand` | Shared `obj/` and NuGet cache. |
| The sync snapshot itself (`LibGit2SharpLocalGitSnapshotReader`) | Do not parallelize it internally. |

Process counts are a contract: refactoring Git code must not add `git` launches (`GitServiceFewerProcessesTests`, `GitHooksLocationCacheTests`).

---

## 10. Dependency graph changes

If a feature affects dependency semantics, inspect all dependency sources:

```text
physical project PackageReferences
configured file tokens
generated packages
custom dependencies
the Workspace-role repository (always top level, edges into it dropped)
```

Also inspect all consumers:

```text
Repositories grouping
Update
Push Updated
synchronized push
package wait
Dependencies page
badges
notifications
restore planning
```

Changing only the graph page is not enough. Dependency state exists only when the profile `UsesDependencyGraph` (section 20C), and Feature edges into a repository whose context `GitVersionPending` is true are pending, never matched or mismatched.

---

## 11. Branch/tag changes

Remember that GrayMoon persists both:

```text
ref inventory
current checkout state
```

Keep synthetic detached-HEAD descriptions out of persisted real branches.

Rules:

- Branch row changes go through `RepositoryBranchWriter` (`PersistAsync` for a full refresh; `AddLocalBranchesAsync`, `AddRemoteBranchAsync`, `RemoveBranchAsync`, `PruneRemoteBranchesAsync` for single changes). Remote rows are `origin/<name>`, normalized only there (`ToRemoteBranchName`). These methods never touch tags. Remove a row only after git confirmed the delete. Test: `FeatureBranchPersistenceTests`.
- Any new branch or tag mutation that can run in a Feature context must call `IFeatureBranchGuard.CheckAsync` (rules and messages in `FeatureBranchPolicy`). A Feature keeps every repository on its Feature branch (or detached at its pinned tag); the guard is enforced service-side for dialog, REST and bulk paths, so a new entry point is not protected by the dialog alone.
- **Tag-pinned / tag-checked-out repositories are never part of mutating operations.** Filter them out of candidate id sets (`!wr.IsOnTag` / empty `CheckedOutTag`) before branch create/checkout, Push, Sync Commits, Update Dependencies, Return to Default, Prepare Workspace, and bulk follow-ups such as Merge PRs... → Return to default → All repositories. Service paths must leave them untouched (skip, do not fail or mutate) when an id slips through - Return to Default classifies `IsOnTag` as excluded, not blocking (`ClassifyRepository`, `ReturnToDefaultUnattendedBatchAsync`, `ExecuteReturnToDefaultAsync`). Switching onto a tag (which records the pin) and leaving a tag through GrayMoon are the intentional exceptions.
- Never drop, cap or delay tags to speed up a sync.

When branch state changes, consider:

```text
PR invalidation
Actions branch cache
GitVersion
commit counts
default divergence
upstream
tags
dependency/version state
hooks
tag pins in graymoon.json (special Workspace, Source repos)
branch occupancy (Feature / Workspace / external worktree)
```

---

## 12. Git Changes changes

Do not bypass the snapshot pipeline.

A new mutation should converge to:

```text
Worker mutation
authoritative status snapshot
versioned cache
App write queue
SQLite
browser invalidation
```

Preserve:

```text
path validation (GitRepositoryPathValidator)
large pathset handling (GitPathspecStdinWriter)
watcher debounce and sync suppression (IGitChangesRefreshSuppressor)
separate read/diff Worker pools
```

Ignore rules have a single predicate: `IGitIgnoreSession.IsExcluded(path, GitPathKind)` (LibGit2Sharp; tracked paths are never excluded). No `git check-ignore`, no fail-open, no swallow-to-empty, no CLI fallback; a discovery failure means "not probed", never `[]`. Staging never uses `git add -f`.

---

## 13. SignalR changes

Use browser hub events as invalidation signals.

Avoid sending giant domain objects to every circuit.

Handlers should:

```text
filter identity (workspace and context id)
debounce where needed
reload persisted state
```

Context-aware events (`ContextRepositorySynced`, `ContextSynced`, `ContextGitChangesUpdated`) carry the context id; legacy events are for the special Workspace. If a new event can fire very frequently, plan backpressure/debounce.

---

## 14. Background operation changes

Every long-running mutation must define:

```text
operation lock scope (structural Workspace, one Feature context, or none)
progress
cancellation
page disposal behavior
error aggregation
final persistence
final broadcast
```

Lock scopes (`IWorkspaceOperationLock`, implemented by `WorkspaceOperationRunner`):

- `TryStartStructural` locks the whole Workspace and is refused while any context mutation runs. Current structural kinds: `create-feature`, `repair-feature`, `rollback-feature`, `remove-external-worktree`, `refresh-workspace` (`WorkspaceCheckoutRefresh`), `enable-workspace-repository`, plus legacy `TryStart` and REST `WorkspaceCommandHttp.RunExclusiveAsync`.
- `TryStartContext` locks one context (job key `WorkspaceJobKeys.ContextOverlayKey`) and is refused while the Workspace is structurally busy.
- Remove Feature takes no lock: it runs in `FeatureRemovalQueue` (at most `IFeatureRemovalQueue.MaxConcurrentRemovals` = 4 at once) so Sync and every other operation keep working. Code that must not run during a removal cannot rely on `IsWorkspaceStructurallyBusy`.

Do not depend on page-owned scoped services after the component is gone unless the work creates its own scope. A job body that calls back into the page after its Worker work must guard on `_disposed` (see `GrayMoon/CLAUDE.md` "Background job system").

---

## 15. Worker command changes

Worker DTO conventions:

```text
typed request/response classes
explicit JSON property names ([JsonPropertyName("camelCase")])
null validation in handlers
sealed classes
```

Command handlers should remain narrow.

Adding a Worker command touches (the `.claude/skills/add-worker-command` skill walks through the same steps):

```text
GrayMoon.Worker/Jobs/Requests + Jobs/Response      sealed DTOs; derive from WorkspaceCommandRequest when the command works on Workspace paths
GrayMoon.Worker/Commands                           ICommandHandler<TRequest, TResponse>
Cli/Handlers/RunCommandHandler.cs                  AddSingleton<ICommandHandler<...>, ...>()
Services/CommandDispatcher.cs                      constructor parameter + _executors entry
Services/CommandJobFactory.cs                      DeserializeRequest case (without it the payload is not deserialized)
GrayMoon.Abstractions/Worker/WorkerHubMethods.cs   name constant (preferred; older commands use literals)
Hosted/SignalRConnectionHostedService.cs           pool selection, see below
App call site                                      IWorkerBridge.SendCommandAsync(name, args, ct), deserialize WorkerCommandResponse.Data
GrayMoon.Worker.Tests                              handler test
```

Pool selection is by command name:

| Pool | Commands | Option (default) |
|---|---|---|
| Read (`ReadOnlyCommands`) | `GetGitChangeStatus`, `ListGitWorktrees`, `InspectWorktree` | `MaxConcurrentReadCommands` (8) |
| Diff (`DiffCommands`) | `GetGitFileDiff` | `MaxConcurrentDiffCommands` (4) |
| Main | everything else, including worktree create/remove, CodeGraph and the AI commands | `MaxConcurrentCommands` (`ProcessorCount * 2`) |

Add a command to a fast pool only when it is a quick read that must not queue behind long writes.

Rules:

- **Path contract.** A command that resolves repositories under a Workspace root must take `WorkspaceRoot` and `WorkspaceRepositoryName` (from `WorkspaceCommandRequest`) and resolve paths with `WorkerRepositoryPaths.Resolve(workspacePath, repositoryName, workspaceRepositoryName)`, so the Workspace-role repository maps to the root itself. App side, build arguments from `WorkerWorkspaceArgs(WorkspaceRoot, WorkspaceFolderName, WorkspaceRepositoryName)`. `WorkerSendSiteGuardTests` fails any App anonymous payload that has `workspaceRoot` and `repositoryName(s)` but no `workspaceRepositoryName`.
- **Capabilities.** A command that does profile-dependent enrichment reads `request.EffectiveCapabilities` (null = legacy full enrichment). If its request carries `WorkspaceId` + `Capabilities`, add it to the explicit list in `CommandDispatcher.WarmWorkspaceCapabilities` so hook syncs can reuse the cached profile.
- **Version lock.** App and Worker ship together (`WorkerVersionPolicy`; on `VersionMismatch` `WorkerBridge` refuses everything but `SelfUpdate` and `GetHostInfo`). Do not add per-feature Worker capability flags or old-Worker fallbacks. Never change the `SelfUpdate` payload (`installUrl`). An operation that must not start without a usable Worker checks `IWorkerBridge.GetUnavailableReason()` before it mutates.
- **Processes.** Launch processes only through `ICommandLineService` or `IBoundedProcessRunner` (both wrapped by the repository-access broker, section 15A); never `Process.Start` ad hoc. Use `IBoundedProcessRunner` (argument list, output caps, no argument logging) for anything that can produce unbounded output or carries user content.
- **Remote auth.** Remote Git operations get credentials from the existing remote-auth path (`GitRemoteAuth`); see section 16.
- **File writes into a repository** use `WriteRepositoryFile` semantics: temp file + move, reject absolute, `..` and `.git` paths, optional `onlyIfChanged`.

---

## 15A. Repository access broker (Worker)

`IRepositoryAccess` (`GrayMoon.Worker/Abstractions/IRepositoryAccess.cs`, singleton) is the single Worker owner of "who may touch this folder now". Removal (`GitWorktreeService.RemoveWorktreeAsync`, `FeatureFolderCleaner`) takes an exclusive claim; everything else takes a shared lease. The claim releases watchers, cancels `ReadOnly` holders at once, gives `Mutating` holders 5 s, waits up to 15 s, then kills only processes the Worker itself started.

Rules for new Worker code:

- A process launch through `ICommandLineService` / `IBoundedProcessRunner` is leased automatically (`RepositoryAccessCommandLineService`, `RepositoryAccessBoundedProcessRunner`). The access kind comes from `GitAccessClassifier`: known read-only git subcommands are `ReadOnly`, anything unknown is `Mutating`. Add a new read-only git subcommand to the classifier only if it truly never writes.
- A new LibGit2Sharp `Repository` open, file walk or other direct folder use must take `TryAcquireShared(path, kind)` for exactly its lifetime and dispose it. A null lease means the path is being removed: throw `PathUnderRemovalException` (an `OperationCanceledException`). Honour `lease.Yield`: stop and dispose when it fires.
- A long-lived handle holder (like `GitRepositoryWatcher`) must register through `RegisterReleasable` and implement `IReleasable.Release()` idempotently, never restarting itself; ancestor watchers register `IRepositoryAccessObserver`.
- App side, `ErrorCode = "PathUnderRemoval"` from `GetGitChangeStatus` is an expected outcome, not an error. `IWorkspaceGitChangesMonitoringPause` is only an optimization; the Worker is authoritative.
- A `RepositoryAccessException` means the Worker could not release its own handles. Treat it as a defect, not an outcome to handle.

Git configuration rules: `core.longpaths=true` only on Windows, only in the repository's common local config (`RepositoryConfigurationInitializer`); `git config --global --add safe.directory` only after `CheckRepoSafeAsync` reports dubious ownership; no other global or system config writes.

---

## 15B. Adding an AI CLI provider

AI generation runs on the Worker: `CollectGitEvidence` -> `AiPromptBuilder` (Common) -> `GenerateAiText` (one CLI process per click, run by `AiCliRunner` through `IBoundedProcessRunner`). A provider is an `IAiCliAdapter` (`GrayMoon.Worker/Services/Ai/AiCliAdapters.cs`).

To add one:

1. `GrayMoon.Common/Ai/AiProviderConfiguration.cs`: add the id to `AiProviderIds` (constant, `All`, `DisplayName`) and, if needed, a `DefaultModel` entry. Models are validated by `AiProviderConfiguration.IsValidModel`.
2. Implement a `sealed` `IAiCliAdapter`: `ProviderId`, `ExecutableNames` (preferred first), `HasNoTools` (true only if the CLI can run with no tools at all; shown to the user), `Prepare` (argument list for the per-run empty work folder) and `Interpret` (parse the answer, classify failures into `AiGenerationErrorKind`).
3. Register it in `Cli/Handlers/RunCommandHandler.cs` (`AddSingleton<IAiCliAdapter, ...>`); `AiCliRunner` discovers adapters by `ProviderId`.
4. App: add the icon mapping in `WorkspaceAiGenerationOperations.IconFor` and the SVG under `wwwroot/icons/`.
5. Tests in `GrayMoon.Worker.Tests/AiCliRunnerTests.cs`.
6. Optional host prerequisite (as Claude, id `claudeCli`, or Codex, id `codexCli`): a `HostPrerequisiteIds` entry that is never required, listed on the Worker Host tab, and installed by Desktop from the same allowlist.

Adapter rules:

- No argument may span lines (Windows `.cmd` shims); put prompts in files in the work folder or on stdin.
- Disable tools, MCP servers, hooks, sessions and user config wherever the CLI allows; if a no-tools option is rejected, fail as `UnsupportedCli` and never retry with tools.
- Never pass credentials; the CLI must already be signed in for the Worker's user.
- Never log arguments or prompts (`BoundedProcessRunner` logs only the executable, timing and sizes).
- The answer is strict JSON parsed by `AiGeneratedTextParser`; one provider call per click, never partial results.

---

## 16. Remote credentials

Never write connector tokens into:

```text
git config
repository files
graymoon.json
logs
terminal output
```

Remote Git authentication is supplied dynamically by `GitRemoteAuth` (environment-based `GIT_CONFIG_*` on newer git, `-c http.extraHeader` otherwise).

Logging must redact secrets (including GitHub SSO `authorization_request` URLs).

---

## 17. Retry behavior

Do not blindly retry deterministic Git failures such as:

```text
protected branch rejection
invalid ref
known auth failure
GitHub SSO authorization required
non-fast-forward requiring user action
```

Retry only when the failure class is plausibly transient and the existing product behavior expects it (`GitRetryClassifier`).

---

## 18. UI consistency

GrayMoon has a strong visual language.

When adding UI:

- reuse existing buttons;
- reuse existing modal layout;
- reuse callouts;
- reuse disabled/busy styling;
- reuse keyboard behavior;
- avoid one-off icons or decorative patterns;
- prefer shared CSS/classes over page-specific hacks;
- buttons never word-wrap (`text-nowrap`).

---

## 19. Search/filter consistency

Use the shared filter-expression implementation.

Do not create page-specific mini query languages unless the page truly needs an additional field predicate.

---

## 20. Database change checklist

`Program.cs` runs `EnsureCreated()` (builds a brand-new database from the model; never alters an existing one) and then `Migrations.RunAllAsync`. `RunAllAsync` is versioned by `PRAGMA user_version`:

- version 1 is the frozen legacy baseline (all pre-versioning patches, tolerant mode: an error is logged and startup continues);
- versions 2 and later are `Migrations.StrictSteps`, an ordered list of `(Version, Name, Action)`; each pending step runs in its own transaction together with the `user_version` bump, and a failure rolls back and throws `DatabaseMigrationException`, which stops startup;
- before any pending step, a `VACUUM INTO` backup `graymoon.db.bak-yyyyMMdd-HHmmss` is written next to the database (3 newest kept).

A brand-new database starts at `user_version` 0, so every step also runs on a schema `EnsureCreated()` just built: steps must be no-ops when the change is already there.

For a schema change:

1. update the entity and its `modelBuilder.Entity<T>` block (`Data/AppDbContext.cs`, Feature tables `Data/AppDbContext.Features.cs`) - this is what fresh databases get;
2. write an idempotent `MigrateXxxAsync(AppDbContext)` in `src/GrayMoon.App/Migrations.cs` (Feature context tables: `Migrations.Features.cs`), guarded by `pragma_table_info` / `sqlite_master` checks (helpers such as `AddNullableTextColumnIfMissingAsync`), with no try/catch inside so a failure reaches `RunStrictStepAsync` and rolls back;
3. append it to `StrictSteps` with the next free version number; never edit, reorder or remove a shipped step, and never add to the legacy baseline;
4. ensure a new column is nullable or has a default; backfill existing rows only on the branch that just created the column (as `MigrateWorkspaceProfileColumnsAsync` does);
5. add indexes in the same step (or a new step);
6. test the migration from the existing shape (`MigrationsTests`, `MigrationsRunnerTests`, golden upgrade `UpgradeFrom010Tests` with `Fixtures/graymoon-0.1.0.db`);
7. test a fresh database;
8. ensure no duplicate source of truth remains;
9. if the data is observed from a checkout, key it by `WorkspaceFeatureContextId` and backfill the special Workspace context for existing rows;
10. decide whether the data is per machine (DB only) or belongs in `graymoon.json` (section 20D).

---

## 20A. Feature context scoping

A Workspace repository can be checked out in several contexts at once (the special Workspace plus each Feature). The rule (`AGENTS.md`, `.cursor/rules/feature-context-scoping.mdc`):

1. any query or write of projects, dependency stats, GitVersion, PR, Actions, or Git Changes data takes a `WorkspaceFeatureContextId`, not just a `workspaceId`;
2. for a Feature, read and write the context tables (`WorkspaceRepositoryContextState` and siblings); never fall back to the shared link row when the context row is missing - the answer is unknown;
3. mirror writes onto the shared `WorkspaceRepositoryLink` / legacy tables only for the special Workspace context;
4. include the context id in any lookup key that is not globally unique across contexts;
5. resolve repository paths through `IWorkspaceContextPathResolver`, and attribute Worker notifications through `IWorkspaceHookContextAttributor`; never derive a context from the UI selection inside an operation;
6. structural changes (membership, Feature create/repair/rollback, external worktree removal, Workspace refresh) use `TryStartStructural`; per-context mutations use `TryStartContext` with a context job key (`WorkspaceJobKeys.ContextOverlayKey`); Remove Feature is lockless and queued (section 14);
7. Feature-scoped writers of `WorkspaceProjects` / `ProjectDependencies` take `FeatureFinalizationCoordinator.AcquireProjectionAsync(contextId)`, so the unique (context, repository, name) index is never raced.

When adding a context-aware overload, update every call site that already has a context in scope.

Reference test: `WorkspaceRepositoryLinkListQueryServiceTests.Feature_context_sort_keyset_and_level_grouping_use_context_state_not_shared_link`.

---

## 20B. Worktree safety rules

- HEAD is per worktree; `refs/heads/*`, remote refs and hooks are shared through the common git dir. Never copy `.git`, edit `.git/worktrees/*`, move a worktree with a filesystem move, or bypass `git worktree`.
- Never `git worktree add --force`. `git worktree remove --force` only when the user authorized discarding changes; unlock only when authorized. No global `git worktree prune`.
- Never auto-adopt external worktrees, and never default an ambiguous hook path to the Workspace (`WorkspaceHookContextAttributor` skips unknown paths).
- Persist intent before the Git mutation (Create: Feature `Creating` + context + `Pending` rows in one transaction); on failure persist the lifecycle and per-repository error and allow retry (Repair). Never "catch, delete rows, hope". Git is authoritative for worktree existence, SQLite for ownership and intent.
- Do not call `IWorkspaceContextPathResolver` (or anything that opens a second `AppDbContext`) inside the intent transaction: SQLite deadlock.
- Worktree create is idempotent per repository (an existing matching worktree is success); keep that when changing retry or ordering. The Workspace-role (root) worktree is created first and removed last.
- Deletes below the Feature storage root go through the Worker's guarded cleaners (`FeatureFolderCleaner`, residue removal): never a folder holding a `.git` directory, a registered worktree, an existing Feature, or a path outside a managed `...\features` root. Reparse points are deleted, never followed.
- Repair, reconcile and rollback never delete worktrees the user still needs; the reconciler only changes lifecycle state.

---

## 20C. Workspace profile capabilities

A Workspace has three persisted settings (`Type`, `VersioningMode`, `CiProvider`); behavior is derived from them by `WorkspaceCapabilities` (`GrayMoon.Application/Workspaces/WorkspaceCapabilities.cs`), never persisted.

- Ask a capability (`UsesRepositoryVersioning`, `UsesGitVersion`, `DiscoversDotNetProjects`, `UsesDependencyGraph`, `UsesNuGetPackages`, `UsesDependencyAwareUpdate`, `UsesDependencyAwarePush`, `UsesPackageRestore`, `UsesGeneratedPackagesFromVersionFiles`, `UsesCiIntegration`, `UsesGitHubActions`); never test `workspace.Type ==` in feature code. If type checks start accumulating, add a capability or a strategy seam (as `WorkspacePushStrategySelector`, `IWorkspaceCiProviderResolver`, Worker `IRepositoryVersionProvider`).
- Resolve with `IWorkspaceCapabilitiesResolver` by `workspaceId` only. Features inherit the Workspace profile; do not add a context overload.
- Gate producers, not readers: decide at orchestration and UI boundaries (and in `WorkspaceRepositoryStateWriter` / `WorkspaceStateRecomputeScope`), never inside low-level Git code or `WorkspaceProjectRepository`.
- Send the Worker subset with `ToRepositoryOperationCapabilities()` on `WorkspaceCommandRequest.Capabilities` (section 15).
- A new Workspace page: add it to `WorkspacePage` and `WorkspacePageAccess.IsAvailable`, and render `WorkspacePageGate` for direct navigation; the left navigation uses the same policy.
- A new host tool requirement goes through `HostPrerequisiteRequirements.For(capabilities)`.
- Tests that need .NET behavior call `SyncStateTestContext.UseDotNetDependencyProfileAsync()`; the harness default is Basic.

---

## 20D. graymoon.json ownership

`graymoon.json` (Workspace definition in the Workspace-role repository) is parsed and serialized only by `IWorkspaceManifestFile` (`GrayMoon.Common/Manifest`), written only by `WorkspaceManifestService` through the Worker, and authoritative only in the special Workspace context.

| Section | Owner | Meaning for new code |
|---|---|---|
| `workspace.profile`, `connectors`, `repositories`, `versionFiles` | DB | Rebuilt from the database by `BuildFromDatabaseAsync` on every authoritative write. Drift compares repositories, connectors and profile only. The Workspace name is the local folder and is never written; a leftover `workspace.name` is ignored and omitted on the next rewrite. |
| `codegraph`, `ai`, repository `tag`/`commit` pins | File | No DB copy; `WriteAuthoritativeManifestAsync` copies them from the existing file; drift ignores them. |

Rules:

- A new DB-owned section must be produced by `BuildFromDatabaseAsync`, imported on Restore / Sync, and decide whether it takes part in drift.
- A new file-owned section must be copied forward in `WriteAuthoritativeManifestAsync`, or the next write deletes it.
- Never write anything local: the Workspace name, database ids, paths, credentials, the pinned AI provider (`Workspaces.PinnedAiProviderId` is per machine).
- Keep the schema at `version` 1 for optional additive sections; a reader rejects a newer version. Strict parse errors are user-visible, so keep new sections lenient where safe (unknown AI providers are kept and reported, not rejected).
- GrayMoon never commits the definition; written files appear in Git Changes.

---

## 20E. DbContext rules

From `GrayMoon/AGENTS.md` (the authoritative text):

1. Never inject `AppDbContext` into a Blazor page/component; inject `IDbContextFactory<AppDbContext>` and create a short-lived context per operation (reference: `WorkspaceGitChangesReadService`, `WorkspaceGitChangesWriteQueue`).
2. Any method that calls `SaveChangesAsync`, or can run concurrently (`Task.WhenAll`, a semaphore fan-out), creates its own factory context.
3. Work that can outlive the page (`Task.Run`, polling, `BackgroundJobService` / `WorkspaceOperationRunner` closures) resolves its own DI scope; never capture the circuit `AppDbContext`.
4. A plain scoped context used only for one-at-a-time reads in a never-fanned-out service is fine.

Also: batch mutations and call `SaveChanges` once (never in a loop); keep `WorkspaceGitChangesWriteQueue` and `WorkerSyncNotificationQueue` single-reader. Per-connection `busy_timeout` and `foreign_keys` come from `SqliteConnectionPragmasInterceptor`; do not add per-call pragmas.

---

## 21. Test expectations

Every architectural change should include tests at the narrowest appropriate layer.

Examples:

```text
repository writer tests
query-service tests
dependency graph tests
Worker command tests
operation-runner / lock tests
hook sync tests
Git Changes activation/snapshot tests
migration tests (incl. UpgradeFrom010Tests)
Feature-context tests (context row deliberately different from the link)
parallelism tests (ConcurrencyProbe, limit 1)
process-count tests (GitServiceFewerProcessesTests)
architecture tests (FeaturesDiskAccessArchitectureTests, WorkerSendSiteGuardTests)
```

Before changing shared Workspace code, add a characterization test that pins the special Workspace behavior first. Old-Worker compatibility tests are not needed (version lock).

After broad Workspace changes, run all three test projects. Manual smoke list for wide changes: App starts and the Worker connects within 30 s; Sync all; a terminal commit updates the grid (hooks); switch all to a new branch and Return to Default; Git Changes stage and commit; Update Dependencies + Push with levels; create and close a PR; Dependencies page and Restore list each project once; Create and Remove a Feature; Open in VS Code / Terminal; one REST script.

---

## 22. Code conventions

Repository coding conventions are defined in `GrayMoon/CLAUDE.md` and `GrayMoon/AGENTS.md`.

Important examples include:

```text
CRLF line endings
ASCII hyphen only
sealed concrete services/handlers by default
C# primary constructors where appropriate
sealed record modal state
typed sealed Worker DTO classes
```

Architecture docs do not replace those coding rules.

---

## 23. Documentation rule

`GrayMoon/docs/architecture/` is the current-state reference. Update it in the same change as the behavior it describes; a change that alters current behavior without updating these documents is incomplete.

Proposals and design notes for work not yet built live in `GrayMoon/docs/todo/`. When the work ships, its behavior is folded into these documents and the proposal is removed or updated. Design tags that remain in code comments (for example root-first worktree creation or the intent transaction) refer to concepts described here, not to separate documents.

The architecture folder should answer "how GrayMoon works now".

---

## 24. Known gaps and open review findings

Verified against the code; each item is still true. Fix the code and remove the item in the same change.

Security (open review findings):

- **Self-update URL trust.** The App builds `installUrl` from `NavigationManager.BaseUri` (`Worker.razor`) and from the request scheme and Host (`DesktopNotificationHub.UpgradeWorker`). The Worker's `SelfUpdateCommand` rejects non-http(s) URLs and shell-special characters, but checks neither host nor path before running `irm '<url>' | iex`.
- **Origin check.** `RequestSecurityMiddleware` accepts an Origin whose host equals the request Host (passes under DNS rebinding), accepts any loopback host on any port (and `0.0.0.0`), compares hosts only, and trusts `X-Forwarded-Host` without a proxy allowlist.
- **Hook listener.** `127.0.0.1:9191/hook/*` has no secret, no Origin rejection, no Content-Type check and no body cap (`ReadToEndAsync`); only an in-flight cap of 64 exists.
- **Worker secret.** `Security:RequireWorkerSecret` defaults to false; a missing secret is accepted (with a warning) until some Worker has presented one.
- **Data layer.** `Cache=Shared` is forced on the connection string; schema uses `EnsureCreated()` (synchronous call) plus hand-written migrations, not EF Core migrations.

Behavior:

- **GitVersion at default tip.** `GetGitVersionAtDefaultTip` relies on GitVersion `/c <sha>`, which GitVersion ignores for a local repository, so merged Feature repositories get the Feature branch version and `ApplyDefaultTipVersionsForMergedFeatureReposAsync` has no useful effect (`GitVersionParityTests`, skipped).
- **REST and Features.** REST acts only on the special Workspace; there are no Feature endpoints and no endpoint uses `IWorkspaceFeatureOperations`.
- **Hooks are never removed** when a repository leaves a Workspace or the Workspace is deleted; renamed foreign hooks (`<hook>.replaced-by-graymoon`) stop running.
- **Large tag lists.** The branch inventory's unique index `(WorkspaceRepositoryId, BranchName, IsRemote)` has no `IsTag`, so a tag and a local branch with the same name collide; `SwitchBranchModal` renders every tag without virtualization.
- **`dotnet restore` failures are reported as success.** `DotnetRestoreCommand` logs a non-zero exit as a warning and returns `Success = true`.
- **DependencyStats Feature fallback.** `RecomputeAndPersistRepositoryDependencyStatsAsync` falls back to the shared link `GitVersion` for a Feature repository with no context-state row (`GetValueOrDefault(..., x.GitVersion)`), contrary to rule 20A.2.
- **Reconciler vs background removal.** `WorkspaceFeatureReconciler` marks `Removing` Features `NeedsRepair` ("Interrupted while removing") unless the Workspace is structurally busy, but queued removals take no structural lock, so a Worker reconnect during a removal can flag it (seen in code, not reproduced).
- **Bulk branch dialog.** `BranchModal` shows no branch occupancy; `WorkspaceBranchHandler.CheckoutBranchForWorkspaceAsync` skips held repositories but fails open (`OccupancyCheckDegraded`) when occupancy cannot be read.
- **Git lock scope.** `GitProcessRunner` serializes git per working-tree path, so a Workspace checkout and its Feature worktrees do not share a lock; ref updates in the shared common git dir rely on git's own `*.lock` files.
- **Copy absolute path** in Git Changes (`WorkspaceGitChanges.CopyPath.cs`) always joins with `\`, wrong for a Linux Worker.
- **Git Changes large inventories.** The Changes page loads every persisted change path for the context into the Blazor circuit and renders a non-virtualized, default-expanded flattened tree; Worker snapshots and SQLite replaces are also full-list. UX degrades around thousands of paths. Current path: `07-git-changes-projection-and-tree.md`. Design: `docs/todo/git-changes-large-changeset-scale.md`.
- **Dual-write.** The special Workspace still writes both the legacy link/status tables and the context tables.
- **Profile cache.** Saving a Workspace profile does not invalidate the Worker's capability cache; hook syncs may use the old profile until the next App command re-warms it.
- **Create Feature throughput.** `GetHeadCommits` uses a hard-coded parallelism of 8; worktree create/remove share the main Worker pool with hook notify jobs.
