# Create Feature - performance measurements (2026-10-09)

Companion to `GrayMoon-Create-Feature-Flow-And-Performance.md`. That document was derived from reading the code; this one is derived from the timing telemetry added on branch `parallel-create-feature`, read from `C:\ProgramData\GrayMoon\logs` (`graymoon-app-20261009.log`, `graymoon-worker-20261009.log`).

## 1. Setup

- Two runs on the same Workspace: `feature-1` (13:23:23) and `feature-2` (13:24:29).
- **27 repositories**, all Source role. There is no Workspace-role root repository in this Workspace (`RootWorktree Detail=NoRoot`), so the root-first barrier did not apply. Other Workspaces may differ.
- Worker pools at startup: main 16 workers, read 8, diff 4. App `MaxParallelOperations` = 16.
- Fast developer machine. An enterprise VDI with antivirus and security agents will be slower; see section 6 for what scales how.
- Two samples only. The two runs agree closely, but treat the numbers as indicative.

## 2. Where the time goes

Stage timings from the App log (`Feature Create timing`):

| Stage | feature-1 | feature-2 | Share |
|---|---|---|---|
| PreFlight | 120 ms | 6 ms | ~0% |
| HeadSnapshot (`GetHeadCommits`) | 4275 ms | 2803 ms | 8-11% |
| IntentTransaction | 289 ms | 15 ms | ~0% |
| RootWorktree | 2 ms (none) | 0 ms (none) | 0% |
| **SourceWorktrees** | **34168 ms** | **33734 ms** | **~92%** |
| SeedProjections | 459 ms | 238 ms | ~1% |
| **Total** | **39343 ms** | **36814 ms** | |

The per-repository fan-out is the whole story. Stage 2 (head snapshot) is a distant second.

## 3. Main finding: Create waits behind hook-triggered sync jobs

This confirms hypothesis 4.1 of the first document.

Each `git worktree add` fires `post-checkout`, the hook POSTs to the Worker, and the Worker queues a `Checkout` `NotifySync` job on the **same main queue (16 workers)** as `CreateGitWorktree`. Those jobs are slow and hold their worker slot the whole time.

feature-2, Worker log:

- 13:24:38.0 - 14 `Checkout` hook jobs enqueued within 4 ms, as the first creates finish their checkout.
- `NotifySync ... hook=Checkout` durations for the 27 jobs in this window: **average 15.1 s, maximum 23.8 s** each.
- The first 15 `CreateGitWorktree` commands ran immediately: `queued 0ms`, 6.2-8.0 s each (one 12.2 s).
- The remaining 11 commands were sent by the App only as slots freed, then waited in the Worker queue **14.3-15.7 s each** (`queued 14281ms` ... `queued 15745ms`) while the sync jobs occupied the workers. Their own execution was 8.7-11.5 s.
- Those 11 finished at 13:25:04-05, about 25 s after the first wave finished.

feature-1 shows the same pattern: queue waits sorted across the 27 commands are `0 x8, 3-195 ms x7, then 3543, 6928, 6979, 14345, 14938, 15004, 15250, 16170, 16265, 16872, 17141 ms`. Eleven commands, 40% of the repositories, sat 3.5-17 s in the queue.

On the App side this shows as `GateWaitMs` of 6-8 s for the same 11 repositories (waiting for the first wave to return) and `WorkerRoundTripMs` of 24-27 s against a Worker execution time of 8-11 s.

Rough effect: the first wave took about 8 s. The second wave should take about the same, but took about 25 s. **About 17 s of the 34 s fan-out (about half of the whole Create) is queue wait behind sync jobs.** This is an estimate from the logs, not a with-and-without comparison.

Side effects seen in the same logs:

- The sync jobs are still running after the App reports Create finished: the last `Checkout` sync of feature-2 completed at 13:25:16, 10 s after "Feature Create finished" at 13:25:06. The Workspace is busy in the background after the overlay is gone.
- The extra CPU, process and disk load slows the Create work itself (section 4).
- Sync writes to SQLite coincide with the Create row writes: `DbMs` is normally 1-3 ms but reached 32-214 ms for the repositories that finished last.

What each sync does for ~15 s was not broken down here. The code (`CheckoutHookSyncCommand`) does a minimal network fetch (if a token exists), a GitVersion run, a project scan and commit counts, then a SignalR sync back to the App. The seed step already fills the new Feature context from the Workspace, so the early syncs are largely redundant.

## 4. Per-worktree cost on the Worker

Averages over the 27 repositories (from `Git worktree created ...` and `CreateGitWorktree timing`):

| Step | feature-1 | feature-2 |
|---|---|---|
| Hook install (`WriteSyncHooksAsync`) | - | 509 ms |
| First `git worktree list` | 759 ms | 703 ms |
| `core.longpaths` get/set | 1741 ms | 838 ms |
| **`git worktree add` (the checkout)** | **4408 ms** (max 6682) | **5023 ms** (max 7468) |
| Verify `git worktree list` | 719 ms | 902 ms |
| Divergence file | - | 55 ms |
| Worker command total | - | 8035 ms |

- The checkout itself is about 5 s, about 60-65% of the command. About 2.5-3 s per repository (hooks, two `worktree list`, the long-paths check) is overhead around it, mostly short git process launches.
- These small commands are slow for what they are: a `git worktree list` or `git config --get` should take tens of milliseconds, but takes 300-1500 ms, and `core.longpaths` reached 4.7 s for a few repositories. That is contention (16 checkouts plus 14 sync jobs running fetch and GitVersion at once), not the commands. The `longpaths` value halved in the second run, when most repositories already had the setting.
- The overhead steps are cheap to remove and are repeated for each of the 27 repositories (the second `worktree list` and the long-paths check are pure repetition after the first Create).

## 5. Head snapshot

`GetHeadCommits timing`: 27 repositories, cap of 8, **4243 ms** (slowest repository 1710 ms) and **2800 ms** (slowest 876 ms). Each repository costs 4-5 git launches; at 8 concurrent that is 3-4 rounds. It is 8-11% of the total and cannot overlap with anything. The first run being slower than the second is consistent with cold caches.

## 6. What the numbers mean for slower machines (VDI)

- **Sync jobs hold queue slots for their full duration.** Their duration is dominated by network fetch, GitVersion and many process launches, which AV and VDI latency stretch. If a sync takes 3x longer, the second wave of creates waits about 3x longer. The harm from section 3 grows faster than linearly with machine slowness.
- **Process-launch overhead scales with the number of launches.** Per repository this is roughly 4-5 (snapshot) plus about 5 (worktree overhead: list, long-paths, add, list, divergence/hooks lookup) plus the sync work. Cutting launches matters more on a slow machine than here.
- **The checkout (`worktree add`) scales with disk and AV scanning.** It is about 5 s here; it will be larger there and cannot be removed, only parallelised (it already is, up to the pools).
- The Worker pool (16 here, `ProcessorCount * 2`) may be smaller on a VDI with few vCPUs, making queue starvation worse.

## 7. Conclusions and recommended order

1. **Stop hook-triggered sync from competing with Create** (largest win, estimated at about 15-17 s of 37 s here, more on slower machines). Options, which can be combined:
   - Suppress the `post-checkout` sync for worktrees of a Feature that is being created (hook script checks an environment variable set by the Worker for the `worktree add` call, or the Worker drops `Checkout` notify jobs for repository paths under a `Creating` Feature).
   - Give worktree create/remove commands their own lane, like the read and diff pools, so notify jobs can never starve them. This is a safe guard even if hooks stay on.
   - Run the Feature sync once after `Ready` instead of 27 times during Create, if the seeded state needs an authoritative refresh.
   - Needs a check that nothing relies on the early `Checkout` sync for fields the seed does not set. `GitVersion` and `Projects` are copied from the special context by the seed, so this is expected to be fine.
2. **Remove repeated git launches around each worktree** (about 2-3 s per repository on this machine, more on a slow one): drop the verify `git worktree list` on success, skip or cache the `core.longpaths` check, take the pre-check from one list, do the hook installation once per repository per Worker run.
3. **Cheaper head snapshot** (2.8-4.3 s here): remove the hard cap of 8, cut the 4-5 launches per repository to 1-2, and overlap the pre-flight work with it; show progress while it runs.
4. **Not worth doing for this Workspace:** the root `--no-checkout` overlap (no root repository here). Revisit only for a Workspace with a large Workspace-role repository.
5. Add a post-change measurement: re-run the same two Creates and compare `queued` values and the `SourceWorktrees` stage. The expected shape after fix 1 is `queued` near 0 for all 27 commands and a stage time close to two checkout waves (about 16-18 s here).

## 8. Telemetry reference

To repeat the measurement (log level Information):

- App: `Feature Create timing` (per stage), `Feature Create repository timing` (`GateWaitMs`, `PathMs`, `WorkerRoundTripMs`, `DbMs`, `TotalMs`), `Feature Create finished`.
- Worker: `GetHeadCommits timing`, `CreateGitWorktree timing`, `Git worktree created ... PreListMs LongPathsMs AddMs VerifyListMs`, `ResponseCommand ... completed (Command) in Nms (queued Nms)`, `NotifySync completed ... hook=... in Nms (queued Nms)`.
