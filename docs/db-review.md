# GrayMoon SQLite performance review

Proposal of **viable, non-breaking** improvements. Nothing here changes user-visible product behavior, schema uniqueness, or Feature/Workspace semantics.

Reviewed from the App process (`GrayMoon.App`). The Worker does not own this SQLite file. Findings come from four parallel reviews: infrastructure/concurrency, Git Changes + hook sync, workspace operation writes, and read/index/query shape.

---

## Goal and constraints

GrayMoon is a single-process Blazor Server app with one SQLite file (`db/graymoon.db`). Parallel **git / GitHub / Worker** work is already the right model. Parallel **DbContext** work is not.

| Constraint | Why it is non-negotiable |
|---|---|
| `AppDbContext` is not thread-safe | Concurrent queries, property mutations, or `SaveChanges` on one instance throw or corrupt the change tracker. |
| SQLite has one writer | WAL allows concurrent **readers** with one writer. Extra writers wait or fail with `SQLITE_BUSY`. |
| `busy_timeout` is per-connection | A PRAGMA on the startup connection does **not** apply to later pooled connections. |
| Factory vs scoped | `IDbContextFactory` = one short-lived context per call. Scoped `AppDbContext` = one instance for the whole Blazor circuit / HTTP request / background scope. |
| Non-breaking | Additive indexes, interceptors, batching `SaveChanges`, routing writes through existing queues. No unique-constraint changes, no “just `Task.WhenAll` the existing writer”. |

**Rule of thumb:** keep Worker/git/HTTP parallel; give each parallel task its **own** factory context **or** collect results and persist on **one** thread; never share one `AppDbContext` across tasks.

---

## Current model (verified)

```
Worker / UI / circuits
        │
        ├─ parallel git / GitHub / GetGitChangeStatus   (good)
        │
        ├─ WorkerSyncNotificationQueue      single reader → new scope → SyncCommandHandler
        ├─ WorkspaceGitChangesWriteQueue    single reader → factory context → snapshot persist
        ├─ SyncRecomputeCoalescer           one recompute per (workspaceId, contextId) burst
        │
        ├─ SyncBackgroundService            N workers (default 16), each new scope  ← contention
        ├─ FeatureRemovalQueue              up to 4 workers, each new scope
        └─ Blazor circuits                  one scoped AppDbContext per circuit
                    │
                    ▼
         SQLite WAL + Cache=Shared
```

Registration (`Program.cs`):

- `AddDbContextFactory<AppDbContext>(UseSqlite)` only (EF Core 10 cannot register factory + `AddDbContext` together).
- Scoped `AppDbContext` is created from the factory.
- `Cache=Shared` is appended if the connection string lacks it.
- Startup applies `PRAGMA journal_mode=WAL`, `busy_timeout=5000`, `synchronous=NORMAL` on **one** connection.
- `journal_mode` and `synchronous` persist in the file. **`busy_timeout` does not.**
- Tests already have `SqliteBusyTimeoutInterceptor`. Production does not register it.

`IWorkspaceOperationRunner` is an in-memory UX lock (overlapping workspace operations / overlay). It does **not** serialize SQLite.

---

## Do not parallelize

These stay serial, or they break the context / the database:

1. The same `AppDbContext` instance across threads — including parallel property updates before one `SaveChanges`.
2. `WorkspaceGitChangesWriteQueue` and `WorkerSyncNotificationQueue` consumers (single reader by design).
3. `WorkspaceStateRecomputeScope.RecomputeAsync` / `CompleteAsync` per repository inside a parallel loop.
4. `WorkspaceRepositoryStateWriter` / `SaveChanges` inside parallel Worker tasks (branch create already forbids this).
5. `TokenHealthBackgroundService` as written (shared context + `Task.WhenAll`).
6. Raising `SyncBackgroundService` / `FeatureRemovalQueue` concurrency without a write budget or interceptor.
7. Capturing circuit-scoped `AppDbContext` in `WorkspaceOperationRunner` / `BackgroundJobService` `Task.Run` closures.
8. Unbounded parallel `GitChangesSnapshotPushHandler.HandleAsync` against the same DB file.

**Safe parallel patterns already in the tree (copy these):**

- Branch create: parallel Worker, then sequential `ApplyAsync` (`WorkspaceGitService.Branches.cs`).
- Bulk branch ops: `CreateAsyncScope()` per repo (`WorkspaceBranchHandler`).
- Push health check: `lock` around scoped `_dbContext` (`WorkspacePushService`).
- Project refresh: parallel Worker, then `MergeWorkspaceProjectsBatchAsync` (one save).
- Git Changes hub path: enqueue, do not persist on the hub slot.

---

## What we gain if we do all of it

If every P0, P1, and P2 item ships, GrayMoon is still a single-process Blazor Server app on one SQLite file. The payoff is fewer failed writes, fewer concurrent writers, fewer fsyncs per workspace operation, and cheaper page loads. Nothing here is a measured latency benchmark; the numbers below are structural counts already visible in the code.

### Writers and locks

- **`SQLITE_BUSY` / timeout-0 writers disappear** once `busy_timeout=5000` is on every pooled connection (P0-1). Startup already sets the PRAGMA on one connection; later factory connections get SQLite’s default timeout of 0, so a second writer fails immediately instead of waiting.
- **Fewer concurrent SQLite writers.** Git Changes snapshot persists all go through `WorkspaceGitChangesWriteQueue` (already single-reader). `SyncBackgroundService` drops from today’s default of **16** independent scoped writers (`Sync:MaxConcurrency` ?? `Workspace:MaxParallelOperations`) to **2–4**. Hook sync stays serial.
- **Fewer fsyncs per workspace operation.** `PersistVersionsAsync` today calls `ApplyAsync` (which saves) once per repo, then a project-deps merge. Push commit counts, `EnsureLocal/RemoteBranchPersistedAsync`, and sequential PR upserts do the same N× `SaveChanges`. After P1-3 through P1-6 those become N mutations + **one** `SaveChanges`.
- **DbContext thread-safety bugs fixed.** `TokenHealthBackgroundService` shares one context across `Task.WhenAll`. `WorkspaceActionRepository.UpsertAsync` uses the circuit-scoped context while grid poll + push CI refresh can hit different repos on one circuit. Both throw or corrupt the change tracker today.
- **FK enforcement / no silent orphans on delete.** Default SQLite does not enforce foreign keys. Enabling them on every connection makes workspace/feature delete cascades real.

### Reads

- **Faster page loads** from query-shape, not from a new engine:
  - Header: `GetHeaderStateAsync` runs ~8–10 round-trips (`CountAsync` plus a series of `AnyAsync` / lowest-level queries on both the special-Workspace and Feature-context paths) → **one** SQL with aggregates.
  - Feature sort/keyset: correlated subqueries in `ApplySort` / `ApplyKeyset` → one join on `WorkspaceRepositoryContextStates` (same as Project).
  - Git Changes reads: `AsNoTracking()` plus a join instead of correlated `EXISTS`.
  - Top bar: `GetHeaderAsync` (workspace row only) instead of `GetByIdAsync` (full graph: repos, connectors, PRs) just to read the name.
  - Additive indexes on `WorkspaceFeatureContexts(WorkspaceId)`, `WorkspaceFileLineStatuses(WorkspaceId, WorkspaceFeatureContextId, RepositoryId)`, and `WorkspaceFeatureRepositories(WorkspaceRepositoryId)`.

### What users notice

- Fewer lock stalls during multi-repo commit, sync, and scan (the paths that currently fan out `HandleAsync` or open 16 sync writers).
- Snappier workspace grid, header, and Git Changes list.
- More reliable background token health and CI Actions refresh (no shared-context exceptions).

### What stays the same

- UI, screens, buttons, and messages.
- Schema uniqueness and Feature name `NOCASE` collation.
- Feature / Workspace semantics.
- Git / GitHub / Worker parallelism (only App DB writes are serialized or batched).

### What we do not gain

- SQLite will still serialize writers. WAL allows concurrent readers with one writer; this work reduces how often writers collide, it does not add write parallelism.
- This is not a move to Postgres (or any other engine).
- Large-repo Git Changes persist is still delete-all + insert-all of change entries. Routing through the queue and collapsing two `SaveChanges` into one (P2-3) makes that slightly cheaper. Dual-write on the special Workspace (~2× I/O) stays until P2-10, and even then the row rewrite remains.
- Filter search stays `LIKE '%term%'` (not sargable). FTS5 is out of scope.
- Hook sync still does 2–3 `SaveChanges` per notification plus a coalesced recompute.

---

## Priority proposals

### P0 — correctness and SQLITE_BUSY (do first)

| ID | Change | Why it is safe |
|---|---|---|
| **P0-1** | Register a production `SqliteBusyTimeoutInterceptor` on `AddDbContextFactory` (same idea as tests: `PRAGMA busy_timeout=5000` on every opened connection). Optionally set `Default Timeout=5` on the connection string. | Per-connection PRAGMA only. No schema, no API change. Unlocks every later write without `SQLITE_BUSY` at timeout 0. |
| **P0-2** | `TokenHealthBackgroundService`: keep HTTP tests parallel; persist on one thread **or** `CreateDbContextAsync` per connector. | Fixes an existing thread-safety bug. Connectors table is small. |
| **P0-3** | `WorkspaceActionRepository.UpsertAsync`: switch to `IDbContextFactory` + `await using` per upsert (mirror `WorkspacePullRequestRepository`). | Grid poll + push CI refresh can hit different repos on one circuit today. Same scoped context would throw. |
| **P0-4** | Enable SQLite foreign keys on every connection (`Foreign Keys=True` and/or `PRAGMA foreign_keys=ON` in the same interceptor). Verify factory-created contexts, not only startup. | Default SQLite does not enforce FKs. Cascades on workspace/feature delete are then real. Behavior stays the same when data is already consistent. |

### P1 — stop extra writers; batch fsyncs

| ID | Change | Why it is safe |
|---|---|---|
| **P1-1** | Route **all** Git Changes snapshot persists through `WorkspaceGitChangesWriteQueue`: UI `PersistMutationResultAsync`, scanner `persistImmediately` / `includeLineStats`. Keep git fan-out (`MaxParallelRepositoryOperations`). Drop the `Task.Delay(150)` race after bulk commit once the queue is the only writer. | Queue already exists for this reason. Latency of one snapshot is small vs WAL lock storms. |
| **P1-2** | Cap `SyncBackgroundService` writers. Default today is `Sync:MaxConcurrency` ?? `Workspace:MaxParallelOperations` = **16** independent scoped writers. Propose config default **2–4** (git stays parallel per worker; App DB does not). | Config-only. Hook sync is already serial; REST/manual sync is the outlier. |
| **P1-3** | `PersistVersionsAsync`: apply all repo snapshots on one context, **one** `SaveChanges`, then one `MergeWorkspaceProjectDependenciesAsync`. | Same rows, fewer fsyncs. Do not call `ApplyAsync` (which saves) inside the loop. |
| **P1-4** | Push follow-up: batch `UpdateCommitCountsAndUpstreamAfterPushAsync` into one persist after `Task.WhenAll` worker counts. Keep the health-check lock. | Same fields, one transaction. |
| **P1-5** | Prepare / branch create: batch `EnsureLocalBranchPersistedAsync` / `EnsureRemoteBranchPersistedAsync` (AddRange + one save). | Same `RepositoryBranches` rows. |
| **P1-6** | PR refresh: after parallel GitHub fetch, one factory context, upsert all, one `SaveChanges`. Keep per-call factory for isolated single-repo upserts. | Same tables. Factory-per-call stays for concurrent features. |

### P2 — query/index and write-shape polish

| ID | Change | Why it is safe |
|---|---|---|
| **P2-1** | Additive indexes (see [Indexes](#indexes)). | New indexes only. No unique-constraint or collation change. |
| **P2-2** | `WorkspaceGitChangesReadService`: `AsNoTracking()`; replace correlated `EXISTS` with join on `WorkspaceRepositories`. | Read-only. Same result set. |
| **P2-3** | `GitChangesSnapshotPushHandler`: one `SaveChanges` before commit when FK order allows; keep the transaction. | Same dual-write, fewer round-trips. |
| **P2-4** | `GetHeaderStateAsync`: one SQL with aggregates instead of ~10 `AnyAsync` round-trips. | Same header flags. |
| **P2-5** | Feature link-list sort/keyset: join `WorkspaceRepositoryContextStates` once (same as `Project`) instead of correlated subqueries in `ApplySort` / `ApplyKeyset`. | Same sort keys. |
| **P2-6** | `WorkspaceTopBarService`: use `GetHeaderAsync` instead of `GetByIdAsync` (full graph) for the name. | Same string. |
| **P2-7** | File-version persist: preload `WorkspaceFileContextStates` for the context (writes already batched). | Fewer reads; same save. |
| **P2-8** | Defer project-dependency recompute to `WorkspaceStateRecomputeScope.CompleteAsync` when the caller already recomputes. | Avoids a second full-graph save on the same burst. |
| **P2-9** | Line-stats refresh: enqueue persists; scan the attributed context when the path is known (not every context). | Same eventual counts. |
| **P2-10** | Dual-write (special Workspace only): feature-flag context-only writes once remaining readers use `GetContextAsync`. Feature contexts already skip legacy tables. | Cutover flag; rollback = write both again. |

---

## Feature-by-feature

### Platform / SQLite host

| Status | Note |
|---|---|
| Sound | WAL, `Cache=Shared`, factory-only DI, `ScopedServiceExecutor` new scope per call |
| Fix | P0-1 interceptor; P0-4 foreign keys |
| Do not | Register `AddDbContext` alongside the factory (EF Core 10) |

### Hook sync (`WorkerSyncNotificationQueue` + `SyncCommandHandler`)

| Status | Note |
|---|---|
| Sound | Serial queue; new scope per notification; `SyncRecomputeCoalescer` |
| Cost | Typical hook: 2–3 `SaveChanges` (state, optional branches/prune, `UpdateContextSyncFlags`). Projects probed: +2. Then coalesced recompute (file versions + dependency stats) |
| Improve | Keep the queue serial. Optionally split “light persist” from “full recompute” so a burst of hooks does not run file-version Worker + delete/insert line statuses on every coalesced batch |
| Do not | Parallelize the queue reader |

### Manual / REST sync (`SyncBackgroundService`)

| Status | Note |
|---|---|
| Risk | Up to 16 scoped writers vs the serial hook queue |
| Improve | P1-2 lower default concurrency; each worker already has its own scope (EF-safe, SQLite-contended) |

### Git Changes

| Status | Note |
|---|---|
| Sound | Hub → write queue; monitoring: workspaces serial, repos parallel, enqueue writes; `HasAnyChangesAsync` is light |
| Risk | Multi-repo commit/stage/unstage and scanner `persistImmediately` / line-stats call `HandleAsync` in parallel (factory-per-call = EF-safe, many writers) |
| Cost | Delete-all + insert-all change entries; two `SaveChanges` per snapshot; special Workspace dual-writes legacy + context (~2× I/O) |
| Improve | P1-1, P2-2, P2-3, P2-9, P2-10 |
| Do not | Share the page’s scoped context across the `SemaphoreSlim` fan-out |

### Update / Update+Push / Push

| Status | Note |
|---|---|
| Sound | Pipelined update+push uses a **separate scope** for the push lane; dependency commits are Worker-only in parallel; recompute once per batch |
| Cost | `PersistVersionsAsync` and post-push commit counts: N× `ApplyAsync` / `SaveChanges` after parallel git |
| Improve | P1-3, P1-4, P2-8 |

### Prepare workspace / branches / checkout

| Status | Note |
|---|---|
| Sound | Parallel Worker, no DbContext in the parallel section; handler uses `CreateAsyncScope`; `RepositoryBranchWriter.PersistAsync` is one save |
| Cost | `EnsureLocal/RemoteBranchPersistedAsync` one save each |
| Improve | P1-5 |

### Features (create, seed, repair, remove, reconcile)

| Status | Note |
|---|---|
| Sound | Removal: up to 4 workers, each `CreateAsyncScope`; projection gate during seed; reconciler: parallel Worker probes, sequential factory writes; folder cleanup is filesystem |
| Optional | Lower `MaxConcurrentRemovals` if P0-1 is not enough under load |
| Do not | Recompute per repo during Feature seed |

### Projects / dependencies

| Status | Note |
|---|---|
| Sound | `MergeWorkspaceProjectsBatchAsync` one save; used after parallel project refresh |
| Cost | Single-repo merge still saves per repo; deps merge + stats recompute each save; `ApplyProjectsAsync` chains both |
| Improve | Callers that loop repos should use the batch API; P2-8 |

### CI Actions

| Status | Note |
|---|---|
| Risk | Scoped context + `UpsertAsync` + `SaveChanges`; coalesce is per **same** `workspaceRepositoryId` only |
| Improve | P0-3; optional batch upsert for “refresh all” |

### Pull requests

| Status | Note |
|---|---|
| Sound | Repository uses factory per upsert (the pattern Actions should copy) |
| Cost | Refresh: parallel GitHub, sequential persist (N saves) |
| Improve | P1-6 |

### File versions / line statuses

| Status | Note |
|---|---|
| Sound | `ExecuteDelete` + `AddRange` + one save; in-flight coalescing per key |
| Cost | Per-file `FirstOrDefaultAsync` on context states |
| Improve | P2-7; P2-1 index on `(WorkspaceId, WorkspaceFeatureContextId, RepositoryId)` |

### Connectors / GitHub catalog

| Status | Note |
|---|---|
| Sound | `MergeRepositoriesAsync` one transaction + `ExecuteUpdateAsync` |
| Risk | Token health: P0-2 |

### Manifest, settings, Open In, selected Feature

| Status | Note |
|---|---|
| Sound | Manifest body is a Worker file write; drift timestamp is one save; settings / selected context / recent tools are one-save, low volume |

### Workspace page lists / top bar / occupancy

| Status | Note |
|---|---|
| Sound | Keyset pagination; link list uses factory + `AsNoTracking`; repo/project lists `AsNoTracking`; capabilities narrow `Select`; feature counts one grouped query |
| Cost | Feature sort/keyset correlated subqueries; header many round-trips; top bar loads full workspace graph; occupancy filters feature repos by `WorkspaceRepositoryId` (wrong index leading column) |
| Improve | P2-1, P2-4, P2-5, P2-6 |

### Filter search

| Status | Note |
|---|---|
| Sound | Expression tree is correct (`AndAlso` / `Or`) |
| Cost | `ToLower().Contains` → `LIKE '%term%'` — not sargable. Accept for current UX. FTS5 only if search becomes a measured problem |

---

## Indexes

Existing coverage is generally good for primary keys and `(context, repo)` lookups. Additive only:

| Index | Helps |
|---|---|
| `WorkspaceFeatureContexts (WorkspaceId)` | `ListForWorkspaceAsync`, `GetFeatureCountsAsync` (filtered unique indexes do not cover “all contexts in workspace”) |
| `WorkspaceFileLineStatuses (WorkspaceId, WorkspaceFeatureContextId, RepositoryId)` | File-status badges / tooltips |
| `WorkspaceFeatureRepositories (WorkspaceRepositoryId)` | Branch occupancy (`WHERE WorkspaceRepositoryId = ?`) |

Optional, only if `EXPLAIN QUERY PLAN` shows benefit:

- Covering index on `WorkspaceGitContextRepositoryStatuses (WorkspaceFeatureContextId)` including count columns for `HasAnyChangesAsync`.

Do **not** change Feature name uniqueness / `NOCASE` collation in this work.

---

## DbContext safety checklist (for any later PR)

Use this when adding parallel work.

```
git / HTTP / Worker   →  parallel, bounded (SemaphoreSlim)
                              │
                         collect results
                              │
                     ┌────────┴────────┐
                     │                 │
              factory context    one thread +
              per task           one scoped context
              (short write)      (batch SaveChanges)
                     │                 │
                     └────────┬────────┘
                              ▼
                         SQLite writer
```

| Situation | Do |
|---|---|
| Parallel Worker, then persist | Collect snapshots; one context; one `SaveChanges` |
| Parallel persist that must overlap | `IDbContextFactory.CreateDbContextAsync()` **inside** each task; still expect SQLite to serialize writers |
| Background / `Task.Run` / operation runner | `CreateAsyncScope()` or factory **inside** the delegate — never capture circuit `AppDbContext` |
| Hub / SignalR inbound | Enqueue; return; persist on the existing single-reader queue |
| Read on a hot circuit | Factory + `AsNoTracking` (link list, capabilities already do this) |
| Same context, many updates | Mutate, then **one** `SaveChanges` — never `SaveChanges` in the `foreach` |

---

## Suggested implementation order

1. **P0-1 + P0-4** interceptor (busy timeout + foreign keys) — one PR, unlocks the rest.
2. **P0-2 + P0-3** (token health + Actions factory) — correctness on existing parallel callers.
3. **P1-1** Git Changes always through the write queue — largest write-storm reduction.
4. **P1-2** lower sync writer default.
5. **P1-3 / P1-4 / P1-5 / P1-6** batch `SaveChanges` on version persist, push counts, branches, PRs.
6. **P2** indexes and read-shape (header, feature sort, git-changes `AsNoTracking`, top bar).
7. Dual-write removal only after remaining special-Workspace readers are on context tables.

Each step is independently shippable. None require a breaking schema or a new scheduler abstraction.

---

## Out of scope (on purpose)

- Moving off SQLite.
- Compiled EF queries (no current hot path proven to need them).
- FTS5 for filter search.
- Changing Feature name collation.
- Parallelizing recompute or the two write queues.
- Desktop JSON prefs (`DesktopUiPreferences`) — not App SQLite.

---

## Sources

- [SQLite infra concurrency](5f84dfa5-635a-409c-af62-a21982bdff06)
- [Git Changes sync writes](0f01ec36-e0df-49cb-9aed-077c5bb6cdd9)
- [Workspace ops DB writes](0f6fedbd-9357-4631-bb71-6380823f3780)
- [Reads indexes query shape](9439df24-3147-4494-b6cb-c0b7ff677d2d)

---

## Bootstrap prompt

Copy-paste the block below to another agent. The spec is this file.

```
Implement every P0, P1, and P2 item in db-review.md (this repo’s SQLite performance review). Treat that file as the spec. Do not invent extra schema, scheduler, or engine changes. Do not implement Out of scope items.

Workspace wrapper: c:\Users\Matt\.graymoon\GrayMoon\features\db-analysis
Work in GrayMoon/ (its own git repo), not the wrapper. App source: GrayMoon/src/GrayMoon.App

IDs to implement (all of them, in this order):

P0 (correctness and SQLITE_BUSY — do first)
- P0-1 Register a production SqliteBusyTimeoutInterceptor on AddDbContextFactory (same idea as tests: PRAGMA busy_timeout=5000 on every opened connection). Optionally set Default Timeout=5 on the connection string.
- P0-2 TokenHealthBackgroundService: keep HTTP tests parallel; persist on one thread or CreateDbContextAsync per connector.
- P0-3 WorkspaceActionRepository.UpsertAsync: switch to IDbContextFactory + await using per upsert (mirror WorkspacePullRequestRepository).
- P0-4 Enable SQLite foreign keys on every connection (Foreign Keys=True and/or PRAGMA foreign_keys=ON in the same interceptor). Verify factory-created contexts, not only startup.

P1 (stop extra writers; batch fsyncs)
- P1-1 Route all Git Changes snapshot persists through WorkspaceGitChangesWriteQueue: UI PersistMutationResultAsync, scanner persistImmediately / includeLineStats. Keep git fan-out (MaxParallelRepositoryOperations). Drop the Task.Delay(150) race after bulk commit once the queue is the only writer.
- P1-2 Cap SyncBackgroundService writers. Default today is Sync:MaxConcurrency ?? Workspace:MaxParallelOperations = 16. Change the config default to 2–4 (git stays parallel per worker; App DB does not).
- P1-3 PersistVersionsAsync: apply all repo snapshots on one context, one SaveChanges, then one MergeWorkspaceProjectDependenciesAsync. Do not call ApplyAsync (which saves) inside the loop.
- P1-4 Push follow-up: batch UpdateCommitCountsAndUpstreamAfterPushAsync into one persist after Task.WhenAll worker counts. Keep the health-check lock.
- P1-5 Prepare / branch create: batch EnsureLocalBranchPersistedAsync / EnsureRemoteBranchPersistedAsync (AddRange + one save).
- P1-6 PR refresh: after parallel GitHub fetch, one factory context, upsert all, one SaveChanges. Keep per-call factory for isolated single-repo upserts.

P2 (query/index and write-shape polish)
- P2-1 Additive indexes (see db-review.md Indexes): WorkspaceFeatureContexts(WorkspaceId), WorkspaceFileLineStatuses(WorkspaceId, WorkspaceFeatureContextId, RepositoryId), WorkspaceFeatureRepositories(WorkspaceRepositoryId). If the repo uses Migrations.RunAllAsync, add a new strict step. Do not change unique constraints or Feature name NOCASE collation.
- P2-2 WorkspaceGitChangesReadService: AsNoTracking(); replace correlated EXISTS with join on WorkspaceRepositories.
- P2-3 GitChangesSnapshotPushHandler: one SaveChanges before commit when FK order allows; keep the transaction.
- P2-4 GetHeaderStateAsync: one SQL with aggregates instead of ~8–10 AnyAsync / CountAsync round-trips.
- P2-5 Feature link-list sort/keyset: join WorkspaceRepositoryContextStates once (same as Project) instead of correlated subqueries in ApplySort / ApplyKeyset.
- P2-6 WorkspaceTopBarService: use GetHeaderAsync instead of GetByIdAsync (full graph) for the name.
- P2-7 File-version persist: preload WorkspaceFileContextStates for the context (writes already batched).
- P2-8 Defer project-dependency recompute to WorkspaceStateRecomputeScope.CompleteAsync when the caller already recomputes.
- P2-9 Line-stats refresh: enqueue persists; scan the attributed context when the path is known (not every context).
- P2-10 Dual-write (special Workspace only): feature-flag context-only writes once remaining readers use GetContextAsync. Feature contexts already skip legacy tables. Cutover flag; rollback = write both again.

Hard DbContext rules (non-negotiable):
- AppDbContext is not thread-safe. Never share one AppDbContext across tasks, including parallel property updates before one SaveChanges.
- Keep Worker / git / HTTP parallel. Give each parallel task its own factory context, or collect results and persist on one thread. Never share one AppDbContext across tasks.
- Keep the two write queues single-reader: WorkspaceGitChangesWriteQueue and WorkerSyncNotificationQueue. Do not parallelize their consumers.
- Do not parallelize WorkspaceStateRecomputeScope.RecomputeAsync / CompleteAsync per repository inside a parallel loop.
- Do not call WorkspaceRepositoryStateWriter / SaveChanges inside parallel Worker tasks.
- Do not raise SyncBackgroundService / FeatureRemovalQueue concurrency without a write budget or interceptor.
- Do not capture circuit-scoped AppDbContext in WorkspaceOperationRunner / BackgroundJobService Task.Run closures.
- Do not unbounded-parallel GitChangesSnapshotPushHandler.HandleAsync against the same DB file.

Key files:
- GrayMoon/src/GrayMoon.App/Program.cs (factory registration, interceptor, connection string)
- GrayMoon/src/GrayMoon.App/Services/Connectors/TokenHealthBackgroundService.cs
- GrayMoon/src/GrayMoon.App/Repositories/WorkspaceActionRepository.cs
- GrayMoon/src/GrayMoon.App/Services/GitChanges/GitChangesSnapshotPushHandler.cs
- GrayMoon/src/GrayMoon.App/Services/GitChanges/WorkspaceGitChangesWriteQueue.cs
- PersistMutationResultAsync / multi-repo fan-out (WorkspaceGitChanges page / MultiRepo)
- GrayMoon/src/GrayMoon.App GitChangesWorkspaceScanner (persistImmediately / includeLineStats)
- GrayMoon/src/GrayMoon.App/Services/Worker/SyncBackgroundService.cs
- PersistVersionsAsync (WorkspaceGitService.Sync.cs)
- WorkspacePushService (UpdateCommitCountsAndUpstreamAfterPushAsync + health-check lock)
- WorkspaceGitService.Branches (EnsureLocalBranchPersistedAsync / EnsureRemoteBranchPersistedAsync)
- WorkspacePullRequestService (refresh upserts)
- AppDbContext*.cs indexes (OnModelCreating) and Migrations.RunAllAsync / StrictSteps if used
- List query services (WorkspaceRepositoryLinkListQueryService GetHeaderStateAsync, ApplySort, ApplyKeyset)
- WorkspaceGitChangesReadService
- WorkspaceTopBarService
- WorkspaceFileVersionService

Tests required:
- Interceptor: busy_timeout and foreign_keys applied on factory-created connections (not only startup).
- Actions factory: UpsertAsync does not share a circuit-scoped context; concurrent upserts on different repos do not throw.
- Queued git-changes persist: PersistMutationResultAsync and scanner persistImmediately / includeLineStats enqueue; they do not call HandleAsync in parallel on the request path. Drop the Task.Delay(150) race only after the queue is the only writer.
- Batched SaveChanges: PersistVersionsAsync, push commit counts, branch persist, PR refresh — N mutations, one save (same rows / same fields).
- New indexes: model + Migrations.RunAllAsync strict step if that runner is how this repo migrates. Additive only.
- Existing tests plus any new ones. Do not skip hooks (--no-verify). Run the existing App test suite and the new tests; fix failures.

Non-breaking:
- Additive indexes only. No unique-constraint changes. No collation changes.
- No user-visible product UI change. Do not update GrayMoon.Desktop/README.md unless product UI actually changes (it should not).
- No “just Task.WhenAll the existing writer”. No AddDbContext alongside the factory (EF Core 10).
- Safe parallel patterns already in the tree: branch create (parallel Worker, sequential ApplyAsync), bulk branch ops (CreateAsyncScope per repo), push health check (lock around scoped context), project refresh (parallel Worker then MergeWorkspaceProjectsBatchAsync), Git Changes hub (enqueue).

Implementation order (each step independently shippable):
1. P0-1 + P0-4 interceptor (busy timeout + foreign keys)
2. P0-2 + P0-3 (token health + Actions factory)
3. P1-1 Git Changes always through the write queue
4. P1-2 lower sync writer default
5. P1-3 / P1-4 / P1-5 / P1-6 batch SaveChanges on version persist, push counts, branches, PRs
6. P2 indexes and read-shape (header, feature sort, git-changes AsNoTracking, top bar, file-version preload, recompute defer, line-stats enqueue)
7. P2-10 dual-write removal only after remaining special-Workspace readers are on context tables

Verify: existing tests + any new ones; do not skip hooks.
```
