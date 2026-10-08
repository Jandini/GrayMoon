# GrayMoon Sync Critical Path Optimization Prompt
## Post-LibGit2Sharp Enterprise VDI Performance Work

You are working on **GrayMoon**, on the branch that already contains the LibGit2Sharp local snapshot implementation for Sync.

Before making any code changes:

1. Review the current implementation of:
   - `SyncRepositoryCommand`
   - `LibGit2SharpLocalGitSnapshotReader`
   - `GitCliRepositoryReader`
   - `GitService`
   - `GitProcessRunner`
   - Git Changes watchers / refresh coordinator
   - sync hook installation / hook-path resolution
   - GitVersion invocation / version-provider abstraction
   - workspace sync worker scheduling
2. Read the existing design documents under:
   - `docs/git-lib/`
   - `docs/git-service/`
   - relevant Git Changes architecture/docs
3. Read the latest enterprise VDI benchmark report:
   - `graymoon-sync-libgit-performance-analysis-2026-10-08.md`
4. Review the latest enterprise logs that produced that report.
5. **Do not implement immediately.**
   First provide a concise code review and implementation plan, then proceed only after confirming the plan is consistent with the existing architecture.

---

# Objective

The LibGit2Sharp local snapshot optimization is already successful.

The local read lane has been reduced from roughly:

```text
~9.35 seconds median per repository
```

to approximately:

```text
~50-60 ms median per repository
```

on warm enterprise VDI runs.

The local snapshot is therefore **no longer the critical path**.

Do **not** spend time parallelizing the internal LibGit2Sharp snapshot work unless benchmark evidence proves it useful.

The new performance objective is:

> Reduce the remaining expensive Git subprocesses and avoid unnecessary repository/process contention during workspace Sync.

The critical path now consists primarily of:

```text
git fetch
    ↓
+--------------------------------------+
| GitVersion                           |
| LibGit2Sharp local snapshot          |
| project discovery                    |
+--------------------------------------+
    ↓
hook-path / hook setup work
```

At the same time, Git Changes watcher-triggered `git status` processes may run across many repositories and compete with Sync.

---

# Primary priorities

Implement and validate the following areas **in this order**.

---

# Priority 1 — Prevent Git Changes status storms during bulk Sync

## Problem

During workspace Sync, `git fetch` updates repository metadata under `.git`.

The Git Changes watchers detect those changes and schedule:

```text
git status --porcelain=v2 -z --branch --untracked-files=all
```

for many repositories.

On enterprise VDI these status processes can each take several seconds because process creation and filesystem access are heavily inspected by AV/EDR.

This means GrayMoon can be doing, at the same time:

```text
git fetch
dotnet-gitversion
LibGit2Sharp repository reads
git status
hook path resolution
```

across many repositories.

This creates unnecessary contention.

## Desired behavior

During a GrayMoon-managed bulk Sync:

```text
workspace sync starts
    ↓
mark affected repositories as sync-active
    ↓
watcher events still mark repositories dirty
    ↓
do NOT launch repeated status refreshes during sync
    ↓
repository sync completes
    ↓
schedule exactly one authoritative Git Changes refresh
    ↓
clear sync-active state
```

Important:

- Never lose a real external file change.
- Do not disable watchers globally.
- Do not suppress user-triggered manual Git Changes refreshes.
- Do not suppress mutations that require an immediate authoritative snapshot.
- Only coalesce redundant watcher-originated refresh work caused by GrayMoon's own Sync activity.
- The mechanism must work correctly when multiple repositories are syncing concurrently.
- One repository finishing must not release suppression for another repository still syncing.
- Sync failure must still release the suppression state and schedule reconciliation where required.
- Cancellation must not leave repositories permanently suppressed.

## Design preference

Prefer an explicit per-repository bulk-operation/suppression scope over timing hacks.

For example, conceptually:

```csharp
await using var scope = gitChangesRefreshCoordinator.BeginExternalRepositoryMutation(repoPath);

await SyncRepository(...);

scope.RequestRefreshOnDispose();
```

The exact API should match existing GrayMoon architecture.

Avoid:

```text
Task.Delay(...)
ignore watcher events for N seconds
global bool IsSyncing
```

These approaches are brittle.

## Acceptance criteria

- Watcher events during Sync mark the repository dirty but do not trigger repeated `git status`.
- Exactly one post-Sync authoritative refresh is scheduled when required.
- Manual/user-priority refresh still works.
- Stage/unstage/commit behavior is unchanged.
- External file changes occurring during Sync are still reflected afterward.
- Concurrent repositories behave independently.
- Tests cover:
  - successful Sync
  - failed Sync
  - cancelled Sync
  - multiple watcher events during Sync
  - external file modification during Sync
  - two repositories syncing at once
  - manual refresh while Sync is active

## Telemetry

Add low-noise Debug telemetry sufficient to answer:

```text
Repo
suppression entered
watcher refreshes coalesced
manual refresh bypasses
authoritative refresh scheduled
suppression duration
```

Do not log one message per filesystem event.

---

# Priority 2 — Remove the per-Sync hook-location Git subprocess

## Problem

After the LibGit2Sharp work, the normal Sync path still appears to launch one Git subprocess per repository for hook-path resolution, currently around operations conceptually equivalent to:

```text
git rev-parse --git-common-dir --git-path hooks
```

and possibly:

```text
git config --get core.hooksPath
```

On a normal machine this is cheap.

On the enterprise VDI it can cost seconds because launching `git.exe` itself is expensive.

This is now a meaningful part of the post-LibGit2Sharp critical path.

## Required investigation

Determine exactly why Sync resolves/writes hooks every time.

Answer:

1. Are the hook locations stable for the lifetime of a checkout/worktree?
2. Which events can invalidate the result?
3. Is `core.hooksPath` commonly used?
4. Does GrayMoon need to rewrite hooks on every Sync?
5. Is there already a suitable cache/lifecycle boundary?
6. Can hook installation be made idempotent without invoking Git every time?
7. Can hook-path resolution use known repository/worktree filesystem metadata safely?
8. Can a successful previous installation be remembered and revalidated cheaply?

## Desired outcome

The normal quiet Sync should not launch a hook-location Git subprocess for every repository unless something relevant changed.

Possible architecture:

```text
GitHookManager
    Resolve once
    Cache stable location
    Install only when needed
    Invalidate on known topology/config change
```

Do not assume filesystem layout blindly.

Worktrees must remain correct.

Important cases:

- normal repository
- linked Git worktree
- `.git` file indirection
- custom `core.hooksPath`
- relative custom hooks path
- absolute custom hooks path
- repository moved/recreated
- feature/worktree create/remove
- GrayMoon Worker restart
- hooks manually changed externally

It is acceptable to retain Git CLI as the authoritative fallback for unusual layouts.

## Acceptance criteria

For a normal repository after first successful setup:

```text
subsequent quiet Sync
    => no hook-path git.exe
```

unless relevant state changed.

Existing hook behavior and tests must remain intact.

Add process-count tests to prevent regression.

---

# Priority 3 — Investigate GitVersion execution frequency and caching

## Problem

GitVersion is now often the longest task inside the overlap stage.

Typical warm enterprise timing is approximately:

```text
~6 seconds per repository
```

Because the LibGit2Sharp snapshot runs in parallel with GitVersion, making the snapshot faster no longer reduces wall time.

## Important constraint

Do **not** implement a naive cache keyed only by HEAD SHA.

GitVersion output may depend on:

- HEAD commit
- current branch
- tags
- relevant refs
- GitVersion configuration
- version files / configuration files
- repository mode
- possibly fetched remote state depending on project setup

## Required work

First investigate exactly what inputs matter for the way GrayMoon invokes GitVersion:

```text
/nofetch
/verbosity quiet
/nonormalize where applicable
/c <sha> where applicable
```

Then design a safe cache key / invalidation model.

Potential inputs to consider:

```text
HEAD SHA
branch/ref identity
tag/reference snapshot hash
GitVersion.yml / GitVersion.yaml content or timestamp/hash
dotnet tool manifest / GitVersion tool version
non-normalize flag
explicit commit SHA
```

The goal is not a perfect theoretical GitVersion cache.

The goal is:

> Avoid rerunning GitVersion when GrayMoon can prove that the inputs relevant to the version result have not changed.

## Strong preference

Use an explicit value object describing the GitVersion input fingerprint.

Conceptually:

```csharp
GitVersionInputFingerprint
{
    HeadSha,
    Branch,
    RelevantRefsFingerprint,
    ConfigurationFingerprint,
    ProviderIdentity,
    NonNormalize,
    CommitSha
}
```

The exact shape must follow real GitVersion behavior found during review.

## Cache scope

Prefer bounded in-memory caching first.

Do not introduce persistent database caching unless there is a clear need.

The cache must:

- be bounded
- be safe across concurrent sync workers
- avoid duplicate simultaneous GitVersion runs for the same fingerprint where practical
- invalidate naturally when the fingerprint changes
- not reuse failures indefinitely

## Failure semantics

A previous GitVersion failure must not become a permanent cached failure.

At most, consider very short transient failure coalescing if needed.

Do not change existing branch fallback behavior.

## Acceptance criteria

For an unchanged repository:

```text
Sync #1 -> GitVersion executes
Sync #2 -> GitVersion cache hit
Sync #3 -> GitVersion cache hit
```

Changing any semantically relevant input must force recomputation.

Tests must cover:

- HEAD changes
- branch changes
- tag/ref changes
- GitVersion config changes
- tool/provider change if relevant
- nonnormalize difference
- explicit commit SHA difference
- GitVersion failure
- empty/unborn repository
- long path fallback behavior already protected by GrayMoon

Telemetry:

```text
GitVersion cache hit/miss
fingerprint reason / high-level invalidation reason
execution duration
```

Do not log sensitive repository contents.

---

# Priority 4 — Revisit fetch policy

## Problem

Fetch remains several seconds per repository.

Unlike local reads, this is network-bound and native Git should remain the implementation.

Do **not** replace fetch with LibGit2Sharp.

The question is whether GrayMoon is fetching more than required.

## Required review

Inspect:

```text
FetchAsync
FetchMinimalAsync
sync request capabilities
branch/upstream/default branch needs
tag requirements
GitVersion requirements
workspace dependency/version requirements
```

Determine whether every Sync really requires:

```text
git fetch origin --prune --tags
```

for every repository.

Consider whether repository capabilities allow narrower fetch behavior.

Examples to evaluate:

```text
branch only
current upstream + default branch
tags required only for GitVersion
full tags required vs tag refs already present
repositories with no version capability
repositories pinned to tags
workspace repositories
feature worktrees
```

Do not weaken correctness in order to shave network time.

Any minimal-fetch optimization must preserve:

- remote branch state
- default branch state
- upstream counts
- tags required for version calculation
- rename detection
- deleted remote refs
- feature/worktree semantics
- authentication behavior

## Acceptance criteria

If a smaller fetch is safe for a capability class, prove it with tests.

If no safe reduction is found, explicitly document that fetch remains intentionally unchanged.

---

# Do NOT parallelize the LibGit2Sharp snapshot internally

The benchmark shows the snapshot is already effectively off the critical path.

The current model:

```text
one logical read
    ↓
one Repository open
    ↓
one coherent snapshot
    ↓
dispose
```

should remain unless new measurements prove otherwise.

Do not:

- run `CalculateHistoryDivergence` calls concurrently on the same `Repository`
- open multiple repository objects purely to parallelize two tiny graph queries
- introduce a graph-read task fan-out
- create new thread pools for LibGit2Sharp

GrayMoon already processes repositories concurrently at the workspace-worker level.

Keep repository-local snapshot work coherent and bounded.

---

# Required measurement work

Do not claim improvement from microbenchmarks alone.

Use the same enterprise VDI benchmark pattern:

```text
39 repositories
16 sync workers
quiet workspace
```

For each major implementation stage:

1. Restart GrayMoon.
2. Ignore the first Sync after restart / workspace load.
3. Run at least 3 quiet warm Syncs.
4. Record:
   - whole workspace wall time
   - median repository total
   - P90 repository total
   - fetch median / P90
   - GitVersion median / P90
   - snapshot median / P90
   - hook/tail median / P90
   - project scan median / P90
   - Git process count
   - `git status` process count
   - GitVersion process count
   - hook-path process count
   - snapshot fallback count
5. Compare before/after.

Where possible also record:

```text
max simultaneously active git.exe
```

because enterprise AV/EDR cost appears sensitive to process concurrency.

---

# Process-count regression tests

Expand the existing process-count tests.

The optimized quiet Sync should have a documented subprocess budget.

Example target after Priority 1 and Priority 2:

```text
normal unchanged repository:

git fetch           1
GitVersion          maybe 0 on cache hit / 1 on miss
local reads         0
hook path           0 after initialized
commit counts       0
git status          0 during Sync itself
```

Git Changes may perform one post-sync authoritative refresh, but it must be intentionally scheduled rather than caused by a watcher storm.

Pin these expectations in tests where practical.

---

# Architecture requirements

Keep the existing hybrid model:

```text
LibGit2Sharp
    local read-only repository inspection

Git CLI
    fetch
    remote/auth operations
    mutations
    worktree lifecycle
    repair operations
    authoritative compatibility fallbacks
```

Do not expand LibGit2Sharp into fetch/push/merge/worktree mutation as part of this work.

Do not collapse `IGitRepositoryReader`, `ILocalGitSnapshotReader`, `IGitService`, Git Changes services, and worktree services back into one large service.

Preserve the service split.

---

# Implementation units

Implement in small independently testable units.

## Unit 1 — Sync/Git Changes coordination

Owner:

```text
Git Changes refresh scheduling / workspace Sync integration
```

Deliver:

- per-repository Sync suppression/coalescing
- one authoritative refresh after Sync
- tests
- telemetry
- benchmark result

Do not continue until this is correct.

## Unit 2 — Hook resolution/install caching

Owner:

```text
hook-path resolution and hook installation
```

Deliver:

- explicit hook manager/cache boundary
- worktree-safe behavior
- process-count tests
- invalidation tests
- benchmark result

## Unit 3 — GitVersion fingerprint/cache

Owner:

```text
repository version provider
```

Deliver:

- documented relevant inputs
- safe bounded cache
- concurrency deduplication if appropriate
- full invalidation tests
- benchmark result

## Unit 4 — Fetch-policy review

Owner:

```text
Sync remote update logic
```

Deliver either:

- a proven safe smaller-fetch implementation, or
- a documented decision to retain current fetch behavior

Do not force an optimization if correctness cannot be proven.

---

# Living documentation

Create or update a living design / implementation document under:

```text
docs/sync/
```

Suggested filename:

```text
graymoon-sync-critical-path-optimization.md
```

Track:

```text
baseline
hypothesis
implementation unit
code changes
tests
enterprise benchmark
before/after process counts
before/after wall time
accepted/rejected ideas
remaining bottlenecks
```

Update it after every completed unit.

The document should make it possible to understand why an optimization exists six months later.

---

# Review requirements before implementation

Before writing code, report:

1. Exact current code paths responsible for:
   - watcher-triggered status during Sync
   - hook-path Git invocation
   - GitVersion invocation
   - fetch selection
2. Existing synchronization/locking involved.
3. Existing tests that protect each area.
4. Proposed minimal changes.
5. Risks.
6. How each change will be measured.
7. Any part of this prompt that does not match the latest code.

Do not blindly implement this prompt if the branch has evolved.

Treat the current code as authoritative.

---

# Definition of done

This work is done when:

- LibGit2Sharp snapshot internals remain simple and sequential.
- watcher-triggered Git Changes work does not storm `git status` during workspace Sync.
- repositories receive one correct authoritative Git Changes refresh afterward.
- normal initialized Sync no longer resolves hooks through a Git subprocess on every run.
- GitVersion avoids rerunning when its relevant inputs are provably unchanged, if safe caching is confirmed.
- fetch policy is reviewed and either improved safely or explicitly retained.
- no Git behavior regresses.
- Features/worktrees remain correct.
- remote/auth behavior remains correct.
- all tests pass.
- subprocess-count regression tests exist.
- enterprise VDI measurements are recorded for every completed optimization unit.
- the living design document reflects the final implementation and measured results.

The guiding principle is:

> **Do less work and launch fewer processes before adding more parallelism.**
