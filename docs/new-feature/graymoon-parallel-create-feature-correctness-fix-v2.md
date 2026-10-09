# AI Implementation Prompt: Fix Feature Creation Persistence and Dependency Initialization

**Repository:** `Jandini/GrayMoon`  
**Target branch:** `parallel-create-feature`  
**Priority:** High — correctness blocker before merging into `main`  
**Scope:** Feature creation finalization, project/dependency projection seeding, checkout-hook sync suppression/deferral, dependency/version state initialization, Repair/Retry correctness, and regression tests.

## Your role

You are a senior .NET engineer working on GrayMoon. **Review the current code on `parallel-create-feature` before changing anything**, including changes relative to `main`. Fix two reproducible defects *properly*, in a focused, minimal, maintainable manner. Do not paper over symptoms with broad exception suppression, retries without invariants, removing constraints, a full Sync, or a large architecture rewrite. Prioritize correctness, determinism, safety, and performance for real enterprise workspaces with ~39+ repositories and many `.csproj` projects.

**Implement the fixes and tests, not just a plan.** You may use multiple specialized AI agents for independent investigation, tests, and code review, but coordinate ownership and avoid concurrent edits to the same files. Make changes only on `parallel-create-feature`; do not commit or merge to `main`.

## Observed reproduction

1. Prepare a large **.NET dependency Workspace**; Prepare succeeds.
2. From the current Workspace, create Feature `BAM-2874-add-filter-hits`, spanning all repositories (approximately 39).
3. Every repository appears **Ready**, but the Feature immediately reports: `This Feature needs attention. Use Repair to continue.` and `An error occurred while saving the entity changes. See the inner exception for details.`
4. Repair -> Retry appears to recover the Feature.
5. After Feature creation, newly computed GitVersion/version values exist, yet Dependencies stay green and do not indicate the updates they actually require. Manually running **Sync** finally computes and displays the required updates.

**Primary error, October 9, 2026 at 15:27:30 (+05:30):**

```text
Microsoft.Data.Sqlite.SqliteException: SQLite Error 19:
UNIQUE constraint failed:
WorkspaceProjects.WorkspaceFeatureContextId,
WorkspaceProjects.RepositoryId,
WorkspaceProjects.ProjectName

at GrayMoon.App.Services.Features.WorkspaceFeatureOperations.
  SeedInitialFeatureProjectionsAsync(...)
at GrayMoon.App.Services.Features.WorkspaceFeatureOperations.
  CreateFeatureCoreAsync(...)

Feature Create failed after the Feature was saved.
WorkspaceId=14
FeatureName=BAM-2874-add-filter-hits
```

The log also showed Sync processing Feature projection data near creation finalization (around the same second). Investigate this as **strong evidence of overlapping writers, not absolute proof**. Alternative causes include duplicate seed source identities and a seed implementation that fails to remember newly staged inserts.

Review attached application and worker logs **if available in the working environment**; do not fabricate observations if logs are inaccessible.

## Initial code leads (verify against current branch)

- `src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs`
  - `CreateFeatureCoreAsync`
  - `SeedInitialFeatureProjectionsAsync`
  - handling of worktree completion, `Ready`, `NeedsRepair`, and Repair/Retry.
- `src/GrayMoon.App/Repositories/WorkspaceProjectRepository.Merge.cs`
- `src/GrayMoon.App/Data/AppDbContext.Features.cs` and project model/index definition.
- `src/GrayMoon.App/Services/Worker/SyncCommandHandler.cs`
- `src/GrayMoon.App/Services/Workspaces/SyncRecomputeCoalescer.cs` (new on branch)
- `src/GrayMoon.Worker/Services/WorktreeCreateHookDeferral.cs` (new on branch)
- `src/GrayMoon.Worker/Commands/CreateGitWorktreeCommand.cs`
- `src/GrayMoon.Worker/Commands/CheckoutHookSyncCommand.cs`
- `src/GrayMoon.Worker/Hosted/HookListenerHostedService.cs`
- `src/GrayMoon.Worker/Jobs/NotifySyncJob.cs`
- `src/GrayMoon.Worker/Services/GitWorktreeService.cs`
- tests in `src/GrayMoon.App.Tests` and `src/GrayMoon.Worker.Tests`.
- `docs/worktree/GrayMoon-Create-Feature-Flow-And-Performance.md` and associated branch measurement document.
- `docs/worktree/GrayMoon-Worktree-Features-Post-Implementation-UX-Gaps-Analysis-2026-09-21.md` (historical gap; verify whether prior fixes already address its older dependency-level issue).

From `main`, the seeder loads `existingFeatureProjects`, builds `existingByRepoAndName` using `(RepositoryId, ProjectName.Trim().ToLowerInvariant())`, iterates Workspace source projects, and adds missing clones. **It does not add newly staged clones to this dictionary.** Verify whether this still applies on the target branch. Existing lookup and inserts are non-atomic with any concurrent Sync writer. The current seeder also performs `RecomputeDependencyStatsAsync`, catches its exceptions as warnings, and may subsequently mark a Feature Ready. Confirm what that recompute actually covers; *dependency levels/counts* are not necessarily *out-of-date package/version comparison*.

The branch's `parallel-create-feature` changes already introduce checkout-hook deferral and sync-recompute coalescing. **Preserve and validate those optimizations.** Do not regress into hook-triggered fetch/Sync per repository or serialize independent Git worktrees unnecessarily.

## Non-negotiable: preserve delayed GitVersion architecture

**Verified on `parallel-create-feature` (2026-10-09):** `CreateGitWorktreeCommand` uses `WorktreeCreateHookDeferral.BeginCreate`; deferred checkout notification jobs are released by `WorktreeCreateHookDeferral` after active creates finish plus a 500-ms quiet period. `CheckoutHookSyncCommand` marks the jobs as `FreshWorktree`, skips token/network fetch, project scan, commit counts and remote branch list for fresh worktrees, **but still invokes GitVersion** through `stateProbe.CaptureAsync` with `IncludeGitVersion = true` and `GitVersionNonNormalize = true`. App `SyncCommandHandler` applies the fresh version and coalesces recomputation.

**Do not reintroduce GitVersion into the parallel create/worktree loop or call it synchronously per repository as a prerequisite for structural Feature Ready.** The optimization is to **defer** GitVersion until after checkout creation, not to eliminate GitVersion or to use the Workspace's copied GitVersion as the new Feature's final version.

**Critical timing issue to investigate:** the Worker deferral is scoped to each worktree creation and flushes 500 ms after no creates are active; it does **not** explicitly wait for App `SeedInitialFeatureProjectionsAsync` and the Feature `Ready` transition. This may allow deferred Sync to overlap seeding. Introduce a reliable creation/finalization boundary or equally robust single-writer coordination; a longer arbitrary delay is not a fix. Ensure deferred events are not lost on cancellation, failure, restart, or Repair.

**Required lifecycle semantics:** distinguish *structural Feature readiness* (worktrees + valid project/dependency graph + recoverable persistence) from *version-derived status readiness* (Feature-specific GitVersion available and comparisons recalculated). The existing `Ready` enum need not change if a precise context-scoped `pending/refreshing` status is already available. Use the smallest change consistent with existing UI architecture; never show stale green as authoritative. When deferred GitVersion arrives, persist versions and recompute mismatch/out-of-date dependency status using the existing production path, then notify/invalidate the Feature UI. Recompute only after the graph has been finalized; do not initiate an extra GitVersion pass or full Sync just to produce green/red indicators. If a version is unresolved or a deferred job fails, show an explicit unresolved/stale state and provide a recoverable path without misrepresenting structural failure.

## Required investigation before implementation

1. **Trace all writers** of `WorkspaceProjects`, `ProjectDependencies`, dependency status/aggregates, and context GitVersion values during Feature creation: worktree hooks, Worker -> App Sync, seeding, background refresh, Repair, and UI/context switch.
2. Build a precise timeline/state diagram showing when a context becomes visible, when worktree hooks fire, when project/version persistence occurs, and when `Ready` is set.
3. Determine whether the SQLite violation is caused by (a) concurrent Sync and seed, (b) duplicate normalized source keys inside seed, (c) a different writer, or some combination. Use focused diagnostic logging or deterministic tests where runtime evidence is ambiguous.
4. Inspect the **actual database uniqueness semantics** (collation/case sensitivity/normalization). Ensure the application identity key agrees with the unique index and with project merge logic; do not assume that `Trim().ToLowerInvariant()` exactly matches SQLite equality.
5. Trace how Dependencies UI obtains *out-of-date/needs-update* status, including which service computes it on Sync. Distinguish dependency graph/levels from GitVersion-dependent version mismatches; identify the minimal reusable computation to run **upon arrival of deferred GitVersion notifications**, after seeding has completed.
6. Check if Feature GitVersion is copied from Workspace state versus computed for the **new Feature branch**, and the ordering relative to mismatch evaluation. If new branch version values are not yet trustworthy, do not falsely present green, and do not fake synchronization.
7. Compare `main` and `parallel-create-feature`; document which race is already prevented by hook deferral and which correctness gaps remain.

## Required fix A: race-free, idempotent Feature projection seeding

- Preserve the unique constraint on `(WorkspaceFeatureContextId, RepositoryId, ProjectName)` and any foreign keys.
- Make project seeding **idempotent** for repeated create finalization, partial completion, Repair/Retry, and project rows already created by a legitimate Sync.
- Handle duplicate source project identities deterministically: define how duplicates are detected and whether they are truly valid. Never silently choose a random conflicting file/path. If duplicates represent corruption or inconsistent project discovery, emit a useful structured diagnostic and handle safely.
- Ensure newly staged clones are registered in the in-memory map used for subsequent source iterations, avoiding duplicate pending inserts.
- Handle concurrent database writers using an appropriate **single-writer/serialization or short transactional upsert/reconciliation boundary**, consistent with the existing architecture. An application-level gate is only sufficient if **all relevant writers share it**. Consider SQLite-supported conflict handling only if it preserves identity mapping and dependent graph invariants. Avoid a naive `INSERT OR IGNORE` that loses the correct ProjectId mapping or masks data problems.
- Do not reuse tracked EF entities across different DbContexts. Be careful with EF change tracking, tracked vs `AsNoTracking` queries, and identity mappings `source ProjectId -> destination ProjectId`.
- Preserve generated/virtual package semantics: generated package rows remain Workspace-global; never clone them into Feature contexts incorrectly. Keep valid dependency edges to global generated packages and avoid duplicate edges, dangling foreign keys, and orphaned rows.
- Ensure a failure at any seed phase is recoverable without requiring the user to delete/recreate the Feature, and without losing worktrees, source files, commits, or user edits.
- Do not broadly swallow `SqliteException` error 19, suppress all `DbUpdateException`, delete rows blindly, or remove the index.

## Required fix B: correct dependency state without blocking create on GitVersion

- Immediately after structural Feature creation, display correctly seeded graph/levels and show **version-derived dependency comparisons as pending/unresolved** until fresh Feature-specific GitVersion values are available. Never present copied Workspace green status as validated Feature status. After deferred GitVersion notifications complete, dependency indicators must automatically show correct updates **without manual Sync or Fetch**.
- Identify and reuse the existing production calculation behind successful manual Sync wherever possible, rather than implementing a second formula/engine or hard-coding colors.
- Ensure correct ordering: parallel worktrees -> serialized/idempotent project inventory + dependency graph seeding -> structural `Ready` -> deferred GitVersion state persistence -> out-of-date/version-match calculations -> persisted status/UI invalidation. If an already arriving deferred notification overlaps finalization, gate its recomputation safely until the graph is committed. Do not add fresh GitVersion calls to the structural finalization critical path.
- If some independent steps can run concurrently without reading unfinished state, retain parallelism; enforce explicit barriers only where required for correctness.
- Avoid copying **Workspace green/stale computed status** into the Feature as if it were authoritative. Copied graph topology or dependency level may be used as an initial snapshot, but mismatch status must be recomputed for Feature versions.
- Preserve correct behaviors for tag-pinned repositories, generated packages, multiple projects within a repository, project-to-package edges, GitVersion, missing/unmatched dependencies, and repositories where no .NET dependency handling is expected.
- Respect workspace type: Basic workspace should not pay for `.NET` dependency recomputation; .NET dependency workspace should have full correct state.
- Structural persistence/graph failure can require `NeedsRepair`; an unavailable or still-pending deferred GitVersion must instead have an explicit unresolved/pending version status unless existing product rules require otherwise. Never report success/green for unverified comparisons. Do not make expensive asynchronous version detection a hard blocker on worktree creation.
- Make the Feature UI refresh or invalidate the right context-specific caches after finalization; verify the issue is not simply stale component state after correct persistence.

## Required fix C: lifecycle, hook deferral, and sync coalescing

- Review the checkout-hook deferral implemented on `parallel-create-feature` end to end. Worktree add should not cause dozens of competing fetch/GitVersion/project-scan/SQLite writes while creation is underway.
- Make the suppression/deferral specific to **GrayMoon-managed worktree creation**. Do not globally suppress legitimate user checkout hooks, commits, or Sync in unrelated contexts.
- Decide which deferred notifications are **discarded as redundant** versus replayed after finalization; document why. Never deliver stale hooks after new authoritative state has been computed, and never accidentally drop legitimate post-create user changes.
- Ensure cleanup of deferral markers, ambient state, job flags, or scopes in success, failure, cancellation, app/Worker restart, and Repair paths. No permanent suppression after a crash.
- Confirm that `SyncRecomputeCoalescer` cannot race against the initial Feature projection transaction, override newer version/dependency state, or prematurely mark a context in sync. Verify version/generation ordering or equivalent mechanisms as appropriate; keep design simple.
- Ensure only **one authoritative initial reconciliation** for Feature context data; avoid independent duplicate seed+Sync implementations racing to write identical projects.
- A Feature must not become structurally Ready until its required graph/persistence finalization finishes successfully. Feature-specific version-derived indicators may then progress from pending to up-to-date/out-of-date automatically as deferred GitVersion results arrive. Its transitions, persisted state, and UI notification must remain consistent and observable.

## Required fix D: Repair/Retry

- A Feature whose worktrees were created but projection finalization failed must Repair without redundantly recreating successful worktrees or losing valid data.
- Repeated Retry is safe even if interrupted between saving projects, saving dependency edges, recomputing status, and changing lifecycle state.
- Handle cancellation/restart explicitly; reconcile DB intent with actual worktrees instead of guessing.
- Keep existing per-repository status reporting and preserve a specific actionable error. Never display only the generic EF `An error occurred while saving...` when a useful inner exception and operation context can be safely logged or summarized.

## Boundaries: what NOT to do

- **Do not** run full Workspace Sync or Fetch solely to initialize dependencies; reuse the deferred fresh-worktree GitVersion notification pipeline and only necessary in-process/context-scoped calculations after correct graph data exists. **Do not invoke GitVersion a second time in Create Feature or Repair just to force dependency indicators.**
- **Do not** parallelize SQLite project or dependency-edge writes indiscriminately. Independent Git worktree operations can remain parallel, but database finalization must be coordinated.
- **Do not** add global locks covering all workspaces when per-workspace/per-context coordination suffices.
- **Do not** lower concurrency of independent Git operations to hide the race.
- **Do not** remove schema constraints, hide exceptions, add magic delays, introduce unlimited retries, or mark incorrect data as green.
- **Do not** rewrite whole feature orchestrators, rename public APIs, redesign dependency persistence, or broaden to unrelated sync-performance work unless a tightly scoped, demonstrated dependency is unavoidable.
- **Do not** change current user-facing workflows or UI layouts except precise status/error/refresh fixes needed for correctness.
- **Do not** modify `main`.

## Required tests (automated)

Add realistic automated tests (unit/integration as appropriate; favor real SQLite and EF models for uniqueness and transaction behavior). Cover:

1. Creation with 39+ repositories, many projects per repository, multiple dependencies, and a root Workspace repo.
2. Duplicate normalized project names in seed inputs; explicit deterministic handling and no unique-index violation.
3. Existing Feature project rows (as if Sync ran early); seeding reconciles rather than inserting duplicates.
4. Simulated overlapping checkout-hook Sync and seed; prove no concurrent competing project writes and correct eventual state.
5. Sequential and concurrent Retry attempts; idempotent identity mapping and no duplicate projects/edges.
6. Failure injection after project save, after edge save, and during version/dependency recompute; Repair restores a valid state and never loses user Git changes.
7. A Feature where deferred GitVersion differs from the parent Workspace: initially show pending/unresolved comparisons; after deferred notifications arrive, automatically show required updates without manual Sync, and with **no duplicate GitVersion invocation**.
8. Version references already correct => remain green; unmatched deps => still correctly reported; missing projects do not become silent success.
9. Tag-pinned repos and generated/virtual package edges behave as before.
10. Basic workspace vs .NET dependency workspace behavior.
11. Cancellation/Worker restart/crash during checkout-hook deferral does not leak suppression.
12. UI/context cache refresh displays final dependency state and correct Feature lifecycle without switching away/back.
13. Existing performance tests for parallel worktree creation and hook-deferral/coalescing continue to pass. Assert there is **no GitVersion call in worktree-create/finalization**, no extra version-probe round, and no full Sync/fetch per repository on create. Explicitly assert the deferred `FreshWorktree` path skips fetch/projects/counts but still computes GitVersion once where applicable.

Avoid flaky timing-based tests: use controllable barriers, fakes, durable state checks, and real SQLite where appropriate. Make tests deterministic even on Windows/VDI.

## Instrumentation and performance acceptance

- Record stage timings/counts for checkout, seed project reconciliation, edge reconciliation, version/dependency calculation, and Ready transition, using existing Feature operation logging patterns.
- At concurrency boundaries log Feature context ID, workspace ID, repository ID where relevant, operation/generation identity, inserted/updated/existing project counts, duplicate/collision counts, and whether work came from create/Sync/Repair. Avoid personally identifying data, secrets, and noisy per-project success logs.
- Compare before/after numbers from the same environment **if available**. If no comparable benchmark can be run, state that clearly; do not invent an improvement percentage.
- No regressions to the branch's Git worktree parallelism and hook suppression optimization.

## Implementation procedure and deliverables

1. Inspect the full codepaths and changes on current `parallel-create-feature`; **briefly state confirmed root causes versus hypotheses** and identify the exact methods/lines to modify.
2. Design the smallest robust coordination and initialization solution, with explicit ownership of the project seeding writer and lifecycle transition. Explain why it works under concurrent callbacks and restarts.
3. Implement focused changes in logically separated commits or reviewable chunks: (A) projection idempotency/race protection, (B) version/dependency recomputation and UI correctness, (C) hook/lifecycle integration as needed, (D) tests.
4. Update the existing `docs/worktree/` create-feature design/performance documentation with the new finalization contract, barriers, and measured results. Keep a short, living implementation checklist and mark actual completion; do not claim tests pass unless run.
5. Run relevant unit and integration tests, then broader appropriate App/Worker tests; build. Report precise commands and results, failures, and any environmental blockers.
6. Review the diff as if preparing a PR. Specifically look for deadlocks, SQLite write contention, stale status/versions, duplicate edges, detached EF entities, accidental full Sync/fetch, cancellation leaks, broken Repair semantics, and lost parallel performance. Fix findings.
7. Produce a final concise report with: root cause(s) confirmed; changes/file paths; test evidence; perf evidence or limitations; risks; and whether branch is ready for merge.

## Definition of Done

- New Feature creation across a many-repository .NET Workspace finishes successfully with no spurious `NeedsRepair` / SQLite unique failure.
- All worktrees and required structural projections are correctly created; `Ready` never implies version-derived status is already validated while deferred GitVersion is pending.
- Immediately after structural create, dependency graph/levels are correct and unverified version-dependent indicators are pending, not falsely green. When deferred GitVersion arrives, the correct dependency status appears automatically without manual Sync or duplicate GitVersion execution.
- Project and dependency-edge persistence is unique, idempotent, race-free, and Repair-safe.
- Checkout hooks/notifications cannot trigger redundant competing Sync during initial creation, and legit later Sync still works.
- No degradation of unrelated workflows or the existing parallel-create performance work; delayed, once-per-fresh-worktree GitVersion remains off the structural-create critical path.
- Relevant automated tests and build pass, with clear evidence in the final report.

**Start by reading the target branch and tracing the exact Sync -> project persistence and dependency-status paths. Do not implement based only on these hypotheses. Once verified, implement and fully test the smallest correct fix.**
