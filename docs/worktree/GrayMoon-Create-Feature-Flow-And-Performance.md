# Create Feature - how it works and where it can be faster

Scope: `WorkspaceFeatureOperations.CreateFeatureAsync` (App) and the Worker commands it drives, read as of branch `parallel-create-feature`. Motivation: a Workspace with 39 repositories takes noticeably long to create a Feature.

**No timings were measured for this document.** Everything below is derived from reading the code. Items marked *hypothesis* need a measurement (step 0 in the plan) before anyone builds on them.

## 1. Summary

- The "first stage" is **not sequential per repository**. `GetHeadCommits` runs all repositories in parallel inside the Worker, but with a hard-coded cap of 8, as **one** all-or-nothing request that nothing else can overlap with. Each repository costs 4-5 `git` process spawns.
- The only deliberately sequential part is the **Workspace-role root worktree**, which is created alone before any Source repository (design decision D10). If that repository is large, every other repository waits for it.
- The Source worktrees fan out in parallel (App gate 16, Worker pool `ProcessorCount * 2`).
- The most suspicious cost is not in Create itself: every `git worktree add` fires the `post-checkout` hook, which makes the Worker queue a full `CheckoutHookSyncCommand` (minimal network fetch, GitVersion, project scan) **on the same main job queue** as the remaining `CreateGitWorktree` commands (*hypothesis*).

## 2. End-to-end flow

All of this runs inside `operationLock.TryStartStructural(workspaceId, "create-feature", ...)`, so the whole Workspace is locked and the loading overlay shows. Progress is reported only in stage 4.

| # | Stage | Where | Concurrency |
|---|---|---|---|
| 0 | Validate name, base kind, take structural lock | App | - |
| 1 | Pre-flight: duplicate-name query, `EnsureManagedFeatureStorageRootAsync` + `SaveChanges`, `CleanupFeatureFolderForNameAsync(onlyIfMarked: true)` (finishes a leftover pending-delete folder), load repository links, resolve special context id | App (DB, one Worker call possible for cleanup) | sequential |
| 2 | **Head snapshot**: one `GetHeadCommits` Worker command for all repositories | Worker | parallel inside Worker, hard cap 8 |
| 3 | Validate snapshot (every HEAD present, no branch-name collisions), then one DB transaction writing Feature + context + a `Pending` row per repository | App (DB) | sequential, one transaction |
| 4 | **Worktree creation**: root repo first, alone; then all Source repos | App fans out, Worker executes | root: 1; sources: up to `MaxParallelOperations` (16) |
| 5 | Finalize: `SeedInitialFeatureProjectionsAsync` (clone context state, projects, dependency edges), dependency-level recompute, set `Ready`, select the new context | App (DB) | sequential |

### Stage 2 detail - `GetHeadCommitsCommand`

For each repository (semaphore of `DefaultMaxConcurrent = 8`, a constant in the command, not `WorkspaceOptions.MaxParallelOperations`):

1. `git rev-parse HEAD`
2. `git --no-optional-locks branch --show-current`
3. `git --no-optional-locks symbolic-ref -q HEAD` (and `describe --tags --exact-match` when detached)
4. `git for-each-ref ... refs/heads/<name> refs/remotes/*/<name>` (branch-name collision check, skipped when on a tag)

That is about 150-160 process spawns for 39 repositories, 8 at a time. It occupies one slot of the Worker main pool and returns only when the slowest repository finishes; the App gets no progress during it.

### Stage 4 detail - per repository

App side (`createRowAsync`, gated by `SemaphoreSlim(MaxParallel)`):

1. `pathResolver.GetRepositoryPathAsync(special, ...)` - `contextResolver.GetRequiredAsync` plus a fresh `AppDbContext` and 1-2 queries (SQLite).
2. `workerBridge.SendCommandAsync(CreateGitWorktree)` and wait for the response.
3. A fresh `AppDbContext`, load the row, set `Ready` / `NeedsRepair`, `SaveChanges` (SQLite single writer).

Worker side (`CreateGitWorktreeCommand` -> `GitWorktreeService.CreateWorktreeAsync`), on the main pool:

1. `WriteSyncHooksAsync` - hooks-location lookup (cached after first use per repo), up to 4 hook files read/written.
2. `git worktree list --porcelain` (pre-check)
3. `git config --local --get core.longpaths` (Windows)
4. `git worktree add -b <name> <path> <sha>` (or `--detach`) - **the actual checkout**; takes the per-repo write lock
5. `git worktree list --porcelain` (verify)
6. `SetDivergenceBaseBranchAsync` - resolves the git dir (a `git` call) and writes one small file

So about 5 process spawns of overhead around each checkout.

Root first: `rootRow` (Workspace-role repository, whose worktree **is** the Feature folder) is awaited alone; if it fails, the Source fan-out is skipped and the Feature goes to `NeedsRepair`. This is required today because `CreateWorktreeAsync` rejects a non-empty target folder (`PathExists`), and Source worktrees live inside the root folder.

## 3. Parallelism map

| Place | Limit | Source |
|---|---|---|
| Stage 2 inside Worker | 8, constant | `GetHeadCommitsCommand.DefaultMaxConcurrent` |
| Stage 4 App gate | 16 | `WorkspaceOptions.MaxParallelOperations` (appsettings) |
| Worker main pool (all commands **and** hook sync jobs) | `ProcessorCount * 2` | `WorkerOptions.MaxConcurrentCommands` |
| Per-repository git write lock | 1 per repo | `GitProcessRunner.RepoLocks` (irrelevant across different repos) |
| SQLite writes | 1 writer | each row save, hook sync saves, Git Changes write queue |

The effective limit in stage 4 is `min(16, Worker pool free slots)`, and the Worker pool is shared with hook-triggered work.

## 4. Where time can go (ranked by expected payoff)

### 4.1 Hook-triggered sync work competing with Create (*hypothesis, highest payoff*)

`git worktree add` runs `post-checkout` in the new worktree. The hook GrayMoon installs exits early only when `$3 != 1` (a file checkout); a worktree add is a branch checkout, so it POSTs `/hook/checkout`. `HookListenerHostedService` enqueues a `NotifySyncJob` on the **same `jobQueue` as commands**. `CheckoutHookSyncCommand` then does a minimal `git fetch` (network, when a token exists), GitVersion, a project scan, commit counts, and a SignalR `SyncCommand` back to the App.

Consequences, if this is as it reads:

- 39 of these run during Create, on the pool that stage 4 needs.
- A hook job is enqueued while its own `worktree add` is still running, so it lands **ahead** of the next `CreateGitWorktree` command the App releases. Create work queues behind network fetches and GitVersion runs.
- The App then processes 39 `SyncCommand`s (SQLite writes) while Create is also writing rows.
- The result is largely redundant: `SeedInitialFeatureProjectionsAsync` already fills the Feature context from the Workspace snapshot, and its comment shows the early syncs are tolerated, not wanted ("Worktree create can trigger SyncCommand attribution ... before seed runs").

Options:

1. Suppress sync hooks for the duration of the create. Needs a way to reach the hook: an environment variable the hook script checks (`[ -n "$GRAYMOON_SKIP_SYNC" ] && exit 0`; hook scripts are rewritten idempotently on create, so existing installs pick it up) or `-c core.hooksPath=<empty dir>` on the `worktree add` command line. Check what `GitProcessRunner.RunAsync` can pass.
2. Or let hooks fire but drop `Checkout` notify jobs for repository paths under a Feature that is still in `Creating`.
3. Run one real sync pass after `Ready` if the seeded state needs verifying (that is what a user-visible refresh would do anyway).

Risk to check: whether anything relies on the early checkout sync to set fields that the seed does not (`GitVersion`, `Projects` are copied from the special context, so likely not).

### 4.2 Root-first barrier (*hypothesis, depends on repo size*)

Total time is at least `time(root checkout) + time(slowest source checkout, in the queue)`. If the Workspace-role repository is a small manifest repo this is negligible. If it is a large one, it is a pure serial prefix.

Option: create the root with `git worktree add --no-checkout`, which creates the folder and the `.git` file immediately, start the Source fan-out, and run `git checkout`/`git reset --hard` for the root in parallel with it. Needs: relaxing the "empty folder" guard, confirming nested worktree folders do not confuse the root's index/status, and a clear failure story (root failed -> NeedsRepair, as today). Medium effort, medium risk; only worth it if measurement shows the root is expensive.

### 4.3 Stage 2 cost and shape

- Raise or remove the hard cap of 8 (use the same `MaxParallelOperations`, or a Worker option). `git rev-parse`/`for-each-ref` are cheap, I/O-light reads.
- Cut spawns per repository from 4-5 to 1-2: one `git for-each-ref` already knows the branch collisions; HEAD sha plus symbolic-ref can come from one `git rev-parse HEAD --symbolic-full-name HEAD`-style call, or from reading `.git/HEAD` and packed refs directly (`GitCliRepositoryReader` is behind `IGitRepositoryReader`, so the change is contained). On Windows, process spawn plus antivirus scanning is commonly the dominant cost of small git calls.
- Overlap it with stage 1: the pre-flight DB work and `CleanupFeatureFolderForNameAsync` do not depend on the snapshot, so start `GetHeadCommits` first and `await` it after the pre-flight.
- Report progress during stage 2 (per repository) so the overlay is not silent while it runs.

### 4.4 Per-worktree overhead

- Drop the verify `git worktree list` after a successful `git worktree add` (exit code 0 is already authoritative; keep it only on the failure path).
- Cache "core.longpaths already true" per repository path for the Worker lifetime, or fold it into the first `worktree list` pass; today it is a spawn per repository per create.
- Resolve all repository paths once, before the fan-out, instead of three DB round-trips per row inside it; batch the row state writes (or write them as each completes but through a single serialized writer rather than a new `AppDbContext` per row).

Each is small, but they multiply by 39 and each spawn is expensive on Windows.

### 4.5 Concurrency tuning and environment

- `git worktree add` is checkout I/O. On Windows, Defender real-time scanning of the Feature storage root is often the single largest factor; excluding the storage root is a user-side mitigation worth documenting. More parallelism than the disk and scanner can serve will not help.
- 16 App-side slots against a Worker pool of `ProcessorCount * 2` is fine on 8+ cores, but on small machines the Worker pool is the limit, and (see 4.1) it is shared.
- Consider ordering the fan-out largest-first (by repository size or last observed create time) so the long pole starts early instead of being queued last.

### 4.6 Finalize (stage 5)

`SeedInitialFeatureProjectionsAsync` and the dependency-level recompute run strictly after the last worktree, single-threaded, on SQLite. Unknown cost; for 39 repositories with many projects the clone-and-edge copy could be seconds. Measure first; overlapping it with stage 4 is possible in principle but entangles with hook-written rows and failure handling, so it is the last thing to try.

## 5. Plan

0. **Measure first.** Add `Stopwatch` timings and log per stage (`GetHeadCommits`, pre-flight, transaction, root create, source fan-out, seed, recompute) and per repository (queue wait, Worker execution) next to the existing `FeatureOperationFinished` log. Run once on the 39-repository Workspace. Everything else is prioritised by that output.
1. Suppress or defer the checkout-hook sync during Create (4.1) - biggest suspected win, small change.
2. Stage 2: remove the cap, cut spawns, overlap with pre-flight, add progress (4.3).
3. Per-worktree overhead trims (4.4).
4. Root `--no-checkout` overlap (4.2) only if the root is a measurable share.
5. Largest-first ordering and Defender guidance (4.5).

## 6. Things not to break

- The root worktree must exist (or be reserved) before Source worktrees are created inside it (D10), and a root failure must still skip the fan-out.
- The intent transaction (Feature + context + `Pending` rows) before any Worker work, and the `NeedsRepair` handling for any failure or Abort after it.
- Idempotent retry: `CreateWorktreeAsync` treats an existing matching worktree as success, so Retry from the repair dialog must keep working after any change to ordering.
- Do not call `pathResolver` inside the intent transaction (documented SQLite deadlock in the code).
- Tag-pinned repositories use `--detach` with no Feature branch.
