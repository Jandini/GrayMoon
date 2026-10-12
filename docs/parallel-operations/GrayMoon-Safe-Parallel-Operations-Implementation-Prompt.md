# AI Implementation Prompt: Safe Parallel Repository Operations in GrayMoon

## Role and objective

You are a senior .NET, Git, concurrency, and distributed-workflow engineer working in the **GrayMoon** repository. Review the **current `main` branch** before editing. Implement **safe, bounded parallel execution** wherever repository operations are truly independent, focusing first on **bulk PR merge → Return to Default** and then diagnosing the apparently sequential **initial Sync after Restore Workspace**.

This is a production correctness and performance task, not a license to rewrite orchestration. **Reuse existing services, Worker commands, DI-scope patterns, concurrency settings, job/progress infrastructure, and tested Git behavior.** Make the smallest well-designed changes, protect data, and retain existing UX semantics. Do not speculate that all sequential code is a bottleneck.

## Verified starting points (revalidate against HEAD)

1. `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.BulkMerge.cs`
   - `RunBulkMergeAndReturnToDefaultAsync` merges via `PullRequestOperations.MergeManyAsync` (bulk merge already has concurrency).
   - `RunBulkReturnToDefaultAsync` currently loops through merged repository rows **sequentially**, invoking `IWorkspaceSyncOperations.ReturnToDefaultAsync` for one repository at a time.
   - Its comment explains the deliberate serialization: each unattended return currently recomputes **workspace-wide statistics and broadcasts**. Running these calls concurrently would race SQLite and recompute.
   - `rowsToReturnOnly` (retry of previously merged rows) are processed independently from newly successful merges. Do not lose their execution when a merge fails.
   - `FinishBulkMergeRun` finalizes interrupted rows and supports retry.
2. `src/GrayMoon.App/Services/Orchestration/WorkspaceSyncHandler.cs`
   - `ReturnToDefaultUnattendedAsync` analyzes eligibility before changing Git state; it already contains a **bounded parallel repository execution path** when passed multiple repository IDs, with distinct scopes and one final `git.RecomputeAndBroadcastWorkspaceSyncedAsync`.
   - However, **its multi-repository return contract aborts on an eligibility blocker without per-repository error attribution**. Simply batching the UI's IDs into that method will break the current partial-success/retry semantics. It is not automatically the right public API.
   - `ExecuteReturnToDefaultAsync` also uses bounded parallel repo operations and separate DI scopes. Its policies differ; do not substitute it without checking semantics.
   - `RefreshGitChangesAfterSyncAsync` currently scans specified repository IDs in a sequential loop. Determine first whether `IGitChangesWorkspaceScanner` and persistence are safe for parallel invocations.
3. `src/GrayMoon.App/Services/Git/WorkspaceGitService.Sync.cs`
   - Full `SyncAsync` **already fans out `SyncRepository` Worker commands** through `SemaphoreSlim(_maxConcurrent)` and `Task.WhenAll(syncTasks)`.
   - It then persists aggregate results and performs further workspace-wide work. Do not add another naive outer concurrent Sync loop.
   - `RecomputeAndBroadcastWorkspaceSyncedAsync` is documented as a **once-per-action** operation, after repository writes.
4. `src/GrayMoon.App/Components/Modals/RestoreWorkspaceModal.razor.cs`
   - Restore calls `RestoreFromRepositoryAsync`, then `SyncNewWorkspaceAsync`, which delegates to the same `IWorkspaceSyncOperations.SyncAsync` used elsewhere.
   - **Therefore, initial Restore Sync is not inherently sequential in the App orchestration.** Find the observed bottleneck in the actual execution path before changing code.
5. `src/GrayMoon.App/Services/Workspaces/WorkspacePullRequestService.cs`
   - `MergePullRequestsAsync` already uses a bounded semaphore and `Task.WhenAll`.
6. `AGENTS.md` and `.cursor/rules/dbcontext-scoped-lifetime.mdc`
   - Explicitly prohibit sharing scoped `AppDbContext` across concurrently running operations; use factory-created contexts or independent DI scopes. This rule is critical because bulk PR merge previously encountered this exact bug.

**Treat these as investigation anchors, not permission to skip examining their current implementations, tests, callers, and dependent services.**

## Required deliverables

1. A concise, code-referenced diagnosis written to `docs/performance/parallel-repository-operations-design.md` (or an existing relevant living document), including actual sequencing points, concurrency limits, shared resources, and evidence.
2. A minimal production implementation of safe concurrent **post-merge Return to Default**, preserving partial success, failure attribution, and retry.
3. An end-to-end investigation of Restore Workspace initial Sync and corresponding **targeted fixes only if a genuine unnecessary serialization point is demonstrated**.
4. Additional safe optimizations only after evidence and correctness review (especially subset Git Changes scans and other sequential multi-repo actions).
5. Unit/integration tests, concurrency tests, cancellation tests, and performance instrumentation/measurement notes.
6. An implementation progress checklist in `docs/performance/parallel-repository-operations-implementation.md`, updated as work lands. Document deliberately unmodified candidates and reasons.

## Phase 0 — Research and safety map (before editing)

Read the relevant production sources and their tests. Trace:

- UI bulk merge → merge service → unattended return → Git/Worker return → persistence → recompute/broadcast → UI refresh.
- Restore modal → restore operations → Sync handler → WorkspaceGitService.Sync → WorkerBridge → worker command router/handler → `SyncRepository` command → Git operations, locks, queues, clone/fetch, watchers → post-sync persistence and changes scan.
- `MaxParallelOperations`, `_maxConcurrent`, limits in Worker Git process and libgit2sharp lanes, mutual-exclusion mechanisms, workspace/feature/repository operation locks, and any shared `DbContext`.
- Shared workspace-root repository: it owns the workspace root while ordinary repositories live underneath. Check path isolation and `.gitignore`/manifest/clone constraints. Do **not** assume the root workspace repository can be modified concurrently with child clones or workspace-wide file operations without proof.
- Dependency-level ordering, special Workspace role repository, tag-pin behavior, .NET dependency/GitVersion recalculation, basic workspaces, feature worktrees, detached/tag/dirty states, and source versus default branch selection.

Produce a table: stage, method/path, independent per-repo vs shared workspace, currently parallel/sequential, reason, proposed action, race risk, expected gain, test. Establish which stage actually dominates on enterprise VDI; measure or instrument if logs are insufficient.

## Phase 1 — Fix post-merge Return to Default safely (highest priority)

### Required behavior

- Keep the existing **MergeManyAsync** behavior intact. Merge results govern which repositories may proceed to return-to-default.
- Run independent eligible per-repository return work concurrently, bounded by the **existing configured concurrency** (not unbounded `Task.WhenAll`, no new hardcoded limit).
- Run any **workspace-wide recomputation, database consolidation, cache invalidation and sync broadcast exactly once for the whole completed batch**, after all attempted repository operations have finished (including failures, subject to the established cancellation contract). Never run workspace recompute concurrently per repo.
- Do not use `ReturnToDefaultAsync([allIds])` as-is if one blocked repo aborts others or errors lose their repository identity. Preserve current **per-repository eligibility, outcomes, and retries**.
- Reuse `WorkspaceSyncHandler` internal return planning, direct per-repo executor, scopes and progress patterns. Prefer extracting a **narrow batch method** that returns per-repo results and coordinates a single recompute, rather than duplicating Git algorithms in the UI. Design it so existing standalone return flows remain unchanged. Avoid unnecessary new generic schedulers/classes.
- Keep analysis/validation **before each repository mutation** and fail that repository safely if its branch, PR, dirty state, upstream or eligibility changed. A per-repo blocker must not accidentally allow force checkout or deletion. Preserve existing unattended options: remote deletion only when allowed/upstream exists, local deletion policy currently used, no unexpected PR closure.
- Distinct Repo IDs should execute **at most once** per batch; prevent duplication between `rowsToReturnOnly` and newly successful merges. Do not re-merge rows requiring only return retry.
- Preserve `Merged`, `Failed`, `ReturningToDefault`, `ReturnedToDefault`, `ReturnFailed`, `Skipped` row semantics, `ErrorMessage`, repository error clearing/setting, overlay and modal close/reopen, abort, and per-row / Retry failed functionality.
- Marshal **all UI state mutations to the Blazor synchronization context** (`SafeInvoke`/`InvokeAsync` as appropriate). No concurrent unsynchronized mutation of UI rows, dictionaries, progress counters or other component state.
- Progress should report the aggregate total and completed/failed counts and identify which repo failed; use thread-safe counters, no confusing per-repo `1 of 1` messages in a batch. Update progress without a flood of expensive UI refreshes.
- Use independent DI scopes for parallel branches. **Never share EF Core `DbContext`** or a non-thread-safe unit of work. If the service itself holds scoped mutable state, split it appropriately.
- Account for the fact that a per-repo operation may already mutate and commit before cancellation. Cancellation should stop scheduling new work, not claim an in-flight Git operation was rolled back. Await/settle safely, re-read authoritative state for final row statuses, avoid marking success as failure or vice versa. Do not re-run successful Git operations on retry.
- Preserve success and failure behavior when merges partially fail; `rowsToReturnOnly` must still be handled when merging another subset throws (as today).
- Recompute/broadcast should still occur when some Git repository returns succeed and some fail. Handle failure of the single recompute explicitly, without misrepresenting successful returns as unperformed.

### Suggested architecture (adapt after code review)

Implement/reuse a thin service-layer batch coordinator that (a) deduplicates IDs, (b) runs **one repository-scoped unattended plan+return** per ID under the existing bounded limit, (c) returns ordered results carrying `RepositoryId`, success/failure, failure stage/reason and whether mutation completed, (d) **performs one workspace-level reconciliation** after the batch, and (e) does one notification/broadcast. UI invokes the batch once for the full set of return-eligible rows and applies results per row. If mixing retry-only and newly merged rows, preserve their semantics while potentially combining them into one deduplicated return batch.

Important: if existing `AnalyzeReturnToDefaultAsync` internally issues a **workspace-wide PR refresh**, concurrent per-repo calls may still race shared mutable caches/database state. Inspect and eliminate/serialize **shared preparatory phases** as necessary, but do not change per-repository safety checks. In particular, do not replace eligibility checks with stale UI snapshots.

### Phase 1 acceptance scenarios

- Four independent merged PRs: returns overlap in time, and recompute occurs only once.
- One blocked/dirty/unsafe repo: others still proceed, one row reports the precise reason, only failed return is retryable.
- One merge fails: no return is attempted for it, but successful merges return.
- Retry has one `ReturnFailed` and one `Failed`: reattempt return-only for former and merge + return for latter, without duplicating either.
- Abort before start / during planning / while operations in flight: no new jobs after cancellation; truthful final states; no orphaned `ReturningToDefault` rows or stuck modal.
- Concurrency respects configured max; same repo never mutated twice simultaneously.
- Workspace-wide recompute/broadcast happens once for batch, after per-repo work; scoped DbContexts never overlap within the same instance.
- Feature vs normal workspace, source branch differing from default, no upstream, tags/detached states, repo with existing worktree, and workspace root repo all retain their current safety policies.

## Phase 2 — Diagnose why Restore Workspace Sync *appears* sequential

**Do not treat the existing App-level `Task.WhenAll` as missing.** Prove the bottleneck below it using timing and trace correlation:

1. Time from restore completion to `SyncNewWorkspaceAsync`, including profile/application, repo links and connector preparation.
2. `WorkspaceGitService.SyncAsync`: initial DB/connector/capability setup, semaphore entry, `SendCommandAsync` invocation, response arrival, and postprocessing.
3. `WorkerBridge` command dispatch: whether requests are truly in flight concurrently, whether pipe/channel/transport serializes command execution, whether outgoing calls await a single-flight lock, and whether worker has global command mutexes.
4. Worker `SyncRepository` handler: per-repo path resolution, clone/fetch, Git locks, shared workspace filesystem and process runner lanes; inspect whether global synchronization is necessary or can safely become **per-repo keyed**.
5. Expensive gitignore, project discovery, dependency analysis, GitVersion, repository status and watcher interactions, especially first clone on VDI.
6. Post-sync `PersistVersionsAsync`, `ApplyDefaultTipVersionsForMergedFeatureReposAsync`, metadata/version checks, manifest drift, special root repo, change scanning. Distinguish harmless post-fanout serialization from the actual critical path.

Add diagnostic logging with correlation `(workspaceId, contextId, repoId, operationId, stage, elapsedMs, activeCount)` where appropriate, **without logging credentials, tokens, Git URL secrets or private content**. Respect existing telemetry/log conventions and keep overhead low. Provide a before/after timing table with at least representative multi-repo and single-repo cases, noting when reliable runtime measurements are not available.

Only optimize a verified serialization point when it is safe. Examples of possible fixes (not assumptions): command dispatch concurrency, repo-keyed mutating-operation exclusion, reducing overly broad Worker locks, overlap of independent clone/fetch phases, batching read-only metadata, and avoiding redundant workspace-wide recomputation. Keep per-repository read/write consistency and platform correctness. Do not bypass an essential Git index/worktree lock, DB transaction, watcher synchronization or dependency gate.

### Restore acceptance scenarios

- Restoring an empty destination with several independent source repositories allows multiple `SyncRepository` operations to make real progress concurrently subject to configured limits.
- Existing Restore preflight, validation, rollback rules, missing-connector/repository reporting and success-with-sync-error messaging stay exactly correct.
- A failed or cancelled initial Sync does **not** undo an otherwise successful Restore, matching existing behavior.
- Parallel cloning does not corrupt the Workspace root repository, its `.gitignore`/`graymoon.json`, or child directories.
- No overlapping mutations for the same physical repository even when UI/workers/refreshes run concurrently.
- A workspace with zero/one repository, basic profile, .NET dependency profile, worktree feature and tagged workspace repository still works.

## Phase 3 — Review adjacent sequential operations, selectively

Inspect these and document conclusions; only implement after safe proof and tests:

- `WorkspaceSyncHandler.RefreshGitChangesAfterSyncAsync`: the per-ID `foreach` and `persistImmediately:true`. Consider bounded parallel scans with independent scopes **only if** scanner state, file watchers and persistence are isolated per repository. Otherwise use a single batch scan, grouped read phase, or retain serial execution.
- `WorkspaceGitService.Sync.cs` result persistence (`PersistVersionsAsync`) and other per-repository update loops: do not parallelize writes via a shared `_dbContext`. Batch set-based updates or fresh contexts only if they preserve ordering and event semantics.
- All-to-default and dependency-level paths: `WorkspaceRepositories.ReturnToDefault.cs`, `ExecuteReturnToDefaultAsync`, `AnalyzeReturnToDefaultAsync` already contain partial parallelism; avoid accidental duplicate work.
- Other multi-repository actions with repeated `await` in loops. Mark each as **safe parallel**, **must serialize**, or **not worth optimizing**, citing concrete code.

Do **not** broaden this into an overhaul of Prepare Workspace, Push Updated, GitVersion algorithms, commit/push semantics, libgit2sharp replacement, or dependency architecture. If a closely related existing scheduler/lane can be reused, prefer it to new infrastructure. Dependency levels remain sequential when dataflow requires it; only independent repos **within the same safe frontier** execute concurrently.

## Engineering rules

- Review `AGENTS.md`, local Cursor/Claude project rules, relevant docs and test standards.
- Code to current project conventions, respecting nullable types, cancellation and structured logs.
- Prefer `MaxParallelOperations` / existing `_maxConcurrent` and existing DI-scope helpers. Do not use `Task.Run` around asynchronous I/O and do not create new global locks or arbitrary semaphore constants.
- Distinguish **per-repo Git mutation locks** from **workspace-wide aggregation locks**. If locking needs to change, document lock scope, ordering and deadlock prevention; maintain same-repo exclusion across all entry points.
- Review `WorkerBridge` and worker concurrency before claiming parallelism: launching multiple tasks is not proof operations run simultaneously.
- Avoid constructing one shared `DbContext` in parallel tasks, sharing unsafe LibGit2Sharp `Repository` instances, or concurrently writing workspace manifest/configuration.
- Maintain deterministic result attribution and output order even when completion order differs.
- No fire-and-forget task branches; await them, capture exceptions, and avoid unobserved failures.
- Minimize changes to public contracts; update tests and callers for any justified API changes.
- Respect cancellation token at admission, between stages, and when invoking cancellable APIs; be careful with destructive Git actions that are already underway.
- Never weaken safety checks for dirty worktrees, divergent commits, protected/default branches, PR states, force/delete policies, tags or worktrees just to increase throughput.
- Preserve default behavior where concurrency is disabled or configured as 1.

## Tests required

Build deterministic concurrency tests (gates/barriers, counters, fakes; avoid sleep-based flaky timing):

1. `max active > 1` for multiple independent repositories, `max active <= configured limit`, and exactly 1 at setting=1.
2. Same repository cannot be mutated twice at the same time, even with duplicates and retries.
3. Per-repo outcomes preserve ID and original input order, with no all-or-nothing failure for a single blocked repo.
4. Workspace reconciliation/broadcast is called once and only after all admitted mutations settle, including partial failure and cancellation conditions.
5. Independent scoped context per parallel branch; tests catch EF Core concurrent use of the same context.
6. Partial merge success, mixed retry batches, exception handling and cancellation at multiple stages.
7. UI status transitions, abort overlay, final summary, error handling, and retry-only behavior.
8. Restore initialization concurrency across independent Worker Sync commands (use a controllable fake Worker transport/handler), and error/cancel/rollback behavior.
9. Root workspace repo / child repo filesystem isolation, dependency-level ordering, and feature context protections.
10. Git Changes scanner behavior if optimized; prove per-repo persistence and watchers do not race.

Run the relevant unit/integration tests, compile affected projects, and report exact commands and results. If an integration test requires unavailable Git credentials, a Worker service or an enterprise VDI, state this explicitly and provide reproducible manual validation steps. Do not claim performance benefits without measurements.

## Work sequence and reporting

1. **Review first.** Read all named files, supporting services, command dispatch and tests; update the diagnosis/design document before editing production code.
2. Implement Phase 1 as one independently testable change, run tests, and check semantic equivalence.
3. Trace Phase 2 through the Worker and add minimal instrumentation. Implement only proven and safe fixes, each with its own tests.
4. Investigate Phase 3, applying only high-value changes with documented evidence; record deferrals.
5. Update the living implementation checklist after every phase.
6. Provide a final report with a file-by-file change list, invariants preserved, tests run, measured concurrency/latency, risks, limitations, and proposed follow-ups.

### Definition of done

The post-merge flow demonstrably executes independent return-to-default operations concurrently **without competing workspace-wide recalculations**, retains exact per-repo success/failure/retry behavior, and never shares non-thread-safe database state. Restore Sync is instrumented and its observed sequential stages are **diagnosed**; any changes made to them are proven safe and measured. All relevant regressions pass. No unrelated architecture churn.

**Start now with the current main branch code review and design notes, then implement and validate. Do not stop after producing a plan.**
