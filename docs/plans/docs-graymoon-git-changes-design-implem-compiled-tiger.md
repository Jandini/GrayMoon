# Git Changes Feature — Staged Implementation Plan

## Context

`docs/GrayMoon-Git-Changes-Design-Implementation-Prompt-v6.md` specifies a new workspace "Git Changes" page (VS-Git-Changes-window-inspired): a two-panel UI showing staged/unstaged changes across every repository in a workspace, with Monaco-based diffs, stage/unstage at file/folder/repo/section granularity, and multi-repo commit. It explicitly frames itself as directions, not rigid requirements, and instructs reuse of GrayMoon's existing patterns over new parallel abstractions.

The full spec (~2500 lines) is large: it asks for Agent-side `FileSystemWatcher` monitoring, a SQLite-backed read-model projection, a 16-way bounded parallel scheduler, a vendored Monaco diff editor, and multi-repo atomic-per-repo commits. Three parallel codebase explorations (Agent command/process infra, App SignalR/DB/job infra, UI/JS/test infra) and a design pass grounded every recommendation below against actual source (not just the spec's suggestions) — verified in particular: the `Migrations.cs` `CREATE TABLE IF NOT EXISTS`-equivalent idiom (real, used 7+ times already), and cascade-delete-on-`WorkspaceRepositoryId` (real, used by `WorkspaceRepositoryPullRequest`/`WorkspaceRepositoryAction`).

User decisions locked in for this plan:
- **Full spec, staged**: implement the complete first-release scope, but structured as independently buildable/testable/committable stages, not one big-bang change.
- **Monaco: vendor locally** into `wwwroot/lib/monaco`, no CDN, no npm build pipeline (none exists in this repo).

This plan resolves every open design question the spec left ambiguous by mapping it onto an existing GrayMoon pattern wherever one exists, and calls out explicitly where it deliberately diverges from the spec's suggested abstractions (mainly: no generic `IGitRepositoryWorkScheduler`, no Subscribe/Unsubscribe RPC pair, no temp-file commit messages, C# Monaco highlighting is a leave-it, may-degrade-to-plaintext acceptable gap).

---

## Key architectural decisions

**Where pure logic lives:** `GrayMoon.Common` gets the porcelain v2 parser, git change models, path validator, and Monaco language mapper — pure functions, no IO — mirroring how `FilterSearchExpression` already lives there and is tested in `GrayMoon.Common.Tests`. `GrayMoon.Agent` gets only the process-invocation shell (`GitCliRepositoryGitChangesService`) that calls into `Common`. This means the highest-value tests (porcelain fixtures: spaces, unicode, renames, conflicts, unborn branch, detached HEAD) need zero new test-project setup.

**New `GrayMoon.Agent.Tests` project:** does not exist today (Agent has zero tests). Still needed for true integration tests that spawn real `git` processes (stage/unstage/commit against a temp repo) — these can't live in `Common.Tests` without an inappropriate project reference. Added in Stage 2, with a `TempGitRepositoryFixture` that sets local (not global) `user.name`/`user.email` so tests never depend on developer git config.

**Process safety:** `CommandLineService`/`GitProcessRunner` currently build `ProcessStartInfo.Arguments` as an interpolated **string** (confirmed in `GitService.cs`, e.g. hand-rolled quoting for `git add --`). This is the exact anti-pattern the spec warns about. Rather than refactor all 76 existing `GitService` methods (large, risky, out of scope), add a **new `ArgumentList`-based overload** to `ICommandLineService`/`CommandLineService` and `GitProcessRunner`, with an explicit UTF-8 `StandardInputEncoding` for NUL-delimited pathspec stdin. Every new Git Changes git invocation uses only this overload with `--` before paths. Existing call sites are untouched (flagged as a deliberate, scoped fix, not a repo-wide convention change).

**Per-repo mutation serialization:** already exists. `GitProcessRunner.RepoLocks` (a `ConcurrentDictionary<string, SemaphoreSlim>` keyed by normalized repo path) already serializes every `git`/`gitversion` invocation per repository, process-wide. Git Changes reuses this directly instead of inventing a second lock — this alone satisfies most of the spec's "per-repository operation coordinator" requirement.

**Concurrency/scheduling — deliberate downscope:** the spec's `IGitRepositoryWorkScheduler`/`GitRepositoryWorkItem` generic abstraction with 8-tier priority and fairness guarantees is **not** built as a reusable component. GrayMoon's existing convention (confirmed in `DependencyUpdateOrchestrator`, `PushOrchestrator`) is a hand-rolled `SemaphoreSlim` + `.Select(...)` + `Task.WhenAll` per use site, gated by `IOptions<WorkspaceOptions>.MaxParallelOperations` (already defaults to 16). Git Changes follows this same idiom:
- Agent: two bounded `SemaphoreSlim`-gated loops (status scans capped at 16, mutations capped at 4) inside a `GitStatusRefreshCoordinator`, plus a per-repo `Clean/Dirty/Refreshing/RefreshingAndDirty/Disposed` debounce state machine (genuinely new — no watcher precedent exists at all today).
- App: same `SemaphoreSlim`+`Select`+`WhenAll` idiom for multi-repo commit/stage/unstage fan-out, reusing `WorkspaceOptions.MaxParallelOperations`.
- Priority is reduced to two tiers (explicit user action vs. background watcher/reconciliation) via separate semaphores, not a full priority queue — no priority-queue precedent exists anywhere in GrayMoon's job infra (`TrackedJobQueue` is a plain bounded channel).

**No Subscribe/Unsubscribe RPCs:** folded into `GetGitChangeStatus(ForceRefresh: bool)` plus an always-on watcher-lease policy (not gated by whether the page is open, per the spec's own requirement). The App receives unsolicited snapshot-push events from the Agent (new `AgentHubMethods.GitChangesSnapshotUpdated` constant), mirroring the existing `SyncCommand` push pattern exactly — persist via a single-writer queue, then broadcast over `/hubs/workspace-sync`, same shape as `SyncCommandHandler.cs`.

**SQLite schema** (verified against `Migrations.cs`'s real `sqlite_master`-check-then-`CREATE TABLE` idiom, and `AppDbContext.cs`'s real cascade-delete-on-`WorkspaceRepositoryId` pattern used by `WorkspaceRepositoryPullRequest`/`WorkspaceRepositoryAction`):
```
WorkspaceGitRepositoryStatus  (1:1 on WorkspaceRepositoryId, cascade delete — matches existing PR/Action tables)
  WorkspaceRepositoryId (PK/FK), SnapshotVersion, BranchName, HeadCommit,
  IsDetachedHead, IsUnbornBranch, IsMerging, IsRebasing, IsCherryPicking,
  StagedCount, ChangedCount, ConflictCount, AgentScannedAt, PersistedAt,
  LastErrorCode, LastErrorMessage

WorkspaceGitChangeEntry  (many per WorkspaceRepositoryId, delete+reinsert per snapshot)
  WorkspaceGitChangeEntryId (PK), WorkspaceRepositoryId (FK, indexed),
  Path, OriginalPath, IndexChange, WorktreeChange, IsTracked, IsConflicted, IsSubmodule
```
Keying on `WorkspaceRepositoryId` (not separate `WorkspaceId`+`RepositoryId`) gets workspace/repository-removal cleanup for free via the existing cascade chain. New tables are added via a `MigrateWorkspaceGitChangesAsync` method in `Migrations.cs` using the exact `SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='X'` → `CREATE TABLE` idiom already used 7+ times there (confirmed real, not assumed).

**Single SQLite writer:** one `Channel<GitChangeSnapshotWriteRequest>` consumed by one background loop (same shape as the existing `AgentSyncNotificationQueue` hosted service), using `IDbContextFactory<AppDbContext>` per write (already registered in `Program.cs`) — never a shared scoped context across concurrent Agent updates. Rejects snapshot versions ≤ the currently persisted version.

**Monaco vendoring:** download `monaco-editor`'s `min/vs` AMD-loader build into `wwwroot/lib/monaco/vs/**`. Add a global `<script src="lib/monaco/vs/loader.js">` tag to `App.razor` (matches the existing `cytoscape.min.js`-style always-loaded-but-tiny convention) — the multi-MB `editor.main.js`/language bundles load lazily via AMD `require([...])` only when the diff viewer first initializes. **Interop convention: introduce a co-located `GitDiffViewer.razor.js` + `IJSObjectReference` module** as a scoped exception for Monaco only (first use of this pattern in the repo — everything else is global `window.*` scripts like `resizable-columns.js`). Justified by Monaco's per-instance lifecycle/disposal needs; the new splitter JS (`git-changes-splitter.js`) still follows the existing global-script/localStorage convention, no exception needed there. C# has no first-class Monaco grammar in the basic-languages bundle — verify at implementation time; falls back to `plaintext` if genuinely unavailable, documented as a known limitation rather than silently promised.

**Commit message delivery:** keep the existing convention already proven in `GitService.StageAndCommitAsync` — pipe the message via stdin to `git commit -F -`, not the spec's temp-file approach. Safer (nothing to leak/clean up on crash) and already battle-tested in this codebase.

**Agent command contracts** (follow existing `ICommandHandler<TRequest,TResponse>` / `Jobs/Requests` / `Jobs/Response` / `CommandDispatcher` dictionary pattern, sealed classes with `[JsonPropertyName]` camelCase per CLAUDE.md):

| Command | Purpose |
|---|---|
| `GetGitChangeStatus` | status/snapshot fetch, `ForceRefresh` flag replaces Subscribe/Refresh split |
| `GetGitFileDiff` | lazy diff load, `Comparison: Staged\|Unstaged`, `ExpectedSnapshotVersion` |
| `StageGitChanges` | `Scope: ExplicitPaths\|Folder\|Repository\|Section`, `Paths`, `ExpectedSnapshotVersion` |
| `UnstageGitChanges` | same shape as Stage |
| `CommitGitChanges` | `CommitMessage`, `StageAllFirst` (Commit All vs Commit Staged), `ExpectedSnapshotVersion` |

New `AgentHubMethods` constant: `GitChangesSnapshotUpdated` (Agent → App push).

---

## Stage breakdown

Each stage: `dotnet build` green, tests green, independently committable. Later stages depend only on earlier merged stages.

**Stage 1 — Shared contracts & pure logic (`GrayMoon.Common`)**
`Git/GitChangeModels.cs`, `GitPorcelainV2Parser.cs`, `GitRepositoryPathValidator.cs`, `MonacoLanguageMapper.cs`, `GitChangesOptions.cs`. Tests in `GrayMoon.Common.Tests`: porcelain v2 fixtures (spaces, unicode, renames, untracked, deleted, conflicts, mixed staged+unstaged, detached HEAD, unborn branch), path traversal rejection, language mapping. No other project touched.

**Stage 2 — Agent core: safe process execution + CLI git-changes service**
`ArgumentList` overloads on `ICommandLineService`/`CommandLineService` and `GitProcessRunner` (reusing `RepoLocks`); `IRepositoryGitChangesService`/`GitCliRepositoryGitChangesService` (status/diff/stage/unstage/commit against real git); `GitPathspecStdinWriter` (NUL-delimited UTF-8 stdin, `--pathspec-from-file` capability probe + bounded-batch fallback). New `src/GrayMoon.Agent.Tests` project + `TempGitRepositoryFixture`. Not wired into the dispatcher yet — library-level increment only.

**Stage 3 — Agent monitoring (watchers, debounce, command wiring)**
`GitRepositoryWatcher`/`GitRepositoryWatcherManager` (lease-based, policy-driven not page-gated), `GitStatusRefreshCoordinator` (debounce state machine), `GitChangesSnapshotCache`. The 5 command request/response DTOs + handler classes, registered in `RunCommandHandler.cs` and `CommandDispatcher.cs`. `AgentHubMethods.GitChangesSnapshotUpdated` added. Manual verification: point the standalone Agent at a real repo, confirm one authoritative status scan per debounce window, not one per filesystem event.

**Stage 4 — Application integration (persistence + SignalR fan-out, no UI)**
`WorkspaceGitRepositoryStatus`/`WorkspaceGitChangeEntry` entities + `AppDbContext` registration + `Migrations.cs` method. `IWorkspaceGitChangesReadService`, `WorkspaceGitChangesWriteQueue` (single writer), `GitChangesSnapshotPushHandler` (mirrors `SyncCommandHandler`), `GitChangesAgentClient` (wraps `IAgentBridge.SendCommandAsync`). Tests: snapshot-version-ordering rejection, coalescing, independent-DbContext-per-write. Manual: start App against an existing populated DB, confirm new tables appear via `Migrations.cs` without touching existing data.

**Stage 5 — Front end (page, tree, filter, commit panel; Monaco stubbed)**
Nav entry in `NavMenu.razor` (`workspaces/{id}/changes`, placed after Repositories). `WorkspaceGitChanges.razor` + partials mirroring `WorkspaceRepositories.razor.cs`'s partial-class split. `GitChangesTree`/`RepositoryNode`/`FolderNode`/`FileNode`, `GitCommitPanel`, `GitChangesFilter`, `GitRepositoryStateBanner`. `GitChangesTreeBuilder` (pure, testable — section-first Staged/Changed hierarchy). `WorkspaceGitChangeSearchMatcher` (static class following the existing `*SearchMatcher` convention, `repo:`/`status:`/`staged:`/`ext:` fields). New `git-changes-splitter.js` (vanilla JS, modeled on `resizable-columns.js`). Diff panel shows a placeholder — no Monaco yet. Manual verification: real workspace with staged-only/unstaged-only/mixed repos, confirm section-first tree correctness, filter debounce+ancestor-preservation, commit button state/wording, and zero Agent command sent on page load/reload (verify via Agent log).

**Stage 6 — Monaco diff integration**
Vendor `wwwroot/lib/monaco/vs/**`, loader `<script>` tag in `App.razor`. `GitDiffViewer.razor`/`.razor.cs`/`.razor.js` (new `IJSObjectReference` pattern, scoped to this component), `GitDiffPanel`/`GitDiffToolbar`. Wire `GetGitFileDiff`. Handle new/deleted/renamed/binary/oversized states without crashing. Manual verification only (no JS test harness in this repo): side-by-side `vs-dark` diff renders correctly, scroll sync works, prev/next navigation works, no console errors or leaks across repeated selection/navigation-away.

**Stage 7 — Hardening**
No new structural code. Walk the spec's full acceptance-criteria and parallel-processing-acceptance-criteria lists item by item, pass/fail. Finish true multi-repo `Commit Staged`/`Commit All` fan-out if simplified to single-repo during Stage 5/6. Fix whatever the walkthrough surfaces.

---

## Deliberate deviations from the spec (call these out explicitly when reporting completion)

1. No generic `IGitRepositoryWorkScheduler` — reuses `GitProcessRunner.RepoLocks` + existing `SemaphoreSlim`+`WhenAll` idiom instead.
2. No Subscribe/Unsubscribe RPC pair — folded into `GetGitChangeStatus(ForceRefresh)` + always-on watcher leases + unsolicited push events.
3. Commit message via `git commit -F -` (existing convention), not a temp message file.
4. `.razor.js`/`IJSObjectReference` interop introduced only for Monaco — first and only use of this pattern in the repo.
5. `GrayMoon.Agent.Tests` is wholly new infrastructure (Agent has zero tests today).
6. Old string-based `Arguments` convention left untouched on all existing `GitService` methods; only new Git Changes code uses the new `ArgumentList` overload — two coexisting conventions going forward, flagged in code comments.
7. 8-tier priority queue and full metrics subsystem from the spec reduced to 2-tier semaphore gating + structured logging (Serilog, already the codebase's actual instrumentation mechanism) — no metrics abstraction exists in GrayMoon today.
8. C# Monaco syntax highlighting may fall back to `plaintext` if the vendored basic-languages bundle lacks a C# grammar — verify at Stage 6, don't promise it's delivered without checking.

---

## Critical files

- `src/GrayMoon.Agent/Services/GitProcessRunner.cs` — extend with `ArgumentList` overload, reuse `RepoLocks`.
- `src/GrayMoon.Common/CommandLineService.cs` + `ICommandLineService.cs` — add safe `ArgumentList`/UTF-8-stdin overload.
- `src/GrayMoon.App/Migrations.cs` — add new-table migration using the verified `sqlite_master`-check idiom.
- `src/GrayMoon.App/Data/AppDbContext.cs` — add the two new `DbSet`s + fluent config with cascade delete on `WorkspaceRepositoryId`.
- `src/GrayMoon.Agent/Services/CommandDispatcher.cs` + `src/GrayMoon.Agent/Cli/Handlers/RunCommandHandler.cs` — register the 5 new command handlers.
- `src/GrayMoon.App/Services/SyncCommandHandler.cs` — reference pattern for the new push-handler (persist-then-broadcast).
- `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.razor.cs` + partials — reference pattern for the new page's structure.
- `src/GrayMoon.Abstractions/Agent/AgentHubMethods.cs` — add `GitChangesSnapshotUpdated`.

## Verification

Per-stage `dotnet build`/`dotnet test` gates are listed inline above. In addition:
- Stage 2/3: real temp-repo integration tests must set local (not global) `user.name`/`user.email`.
- Stage 4: manually confirm new tables appear on an *existing* populated `db/graymoon.db` without touching existing rows.
- Stage 5: confirm via Agent logs that opening/reloading the page issues zero Agent status commands (SQLite-only read).
- Stage 6: manual-only (no JS test harness exists); check console for errors across repeated file selection and navigation away.
- Stage 7: full `dotnet build` + `dotnet test` across all projects must stay green; walk the spec's acceptance-criteria checklist explicitly.
