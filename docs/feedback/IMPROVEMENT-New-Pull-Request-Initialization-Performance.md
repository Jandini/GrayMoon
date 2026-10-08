# IMPROVEMENT - Make New Pull Request Initialization Fast for Large Workspaces

## Classification

**IMPROVEMENT**

## User observation

Opening **New Pull Request** for a workspace with approximately 24 repositories takes far too long while target branches are loading.

The delay is especially painful in enterprise VDI environments.

## Current behavior

The modal currently initializes three independent pieces of work when opened:

```csharp
LoadTargetBranchesAsync();
LoadReviewersAsync();
CheckUnpushedCommitsAsync();
```

The target-branch path is the largest problem.

For every target repository, `LoadOneRepoBranchesAsync()` currently performs:

```text
RefreshBranchesAsync
    ↓
Worker RefreshBranches command
    ↓
obtain repository token
    ↓
git fetch origin, including tags
    ↓
capture repository state / branch lists
    ↓
return full branch data
    ↓
persist branch/tag state
    ↓
broadcast WorkspaceSynced
    ↓
GetBranchesAsync
    ↓
read branch rows back from SQLite
    ↓
build candidate target list
```

This runs once per repository with bounded concurrency.

For 24 repositories and a concurrency of 4, this becomes roughly six waves of remote Git fetches.

## Why the current design is expensive

The UI needs a small answer:

> Which remote branches can be used as Pull Request base branches?

The implementation currently performs a much larger operation:

> Refresh the repository from origin, refresh tags and branch state, persist it, broadcast workspace change, and then query the same state back from the database.

The expensive work includes:

- process startup / Worker command transport
- Git credential/token resolution
- remote network round-trips
- `git fetch`
- tag fetching
- branch/state probing
- database writes
- repeated database reads
- workspace-wide notifications
- modal re-rendering

This cost scales with repository count.

## Additional concurrent pressure

At the same time, reviewer loading can start up to two repository-scoped GitHub lookups per target repository:

- users
- teams

The unpushed-state check also starts concurrently.

These are not necessarily bugs individually, but together they make modal-open a burst of network, Git, and database work.

## Design goal

Opening the modal should be fast enough that branch selection feels immediate even with 20-100 repositories.

The initial branch list should come from already-known GrayMoon state.

Remote freshness should be separated from first paint.

## Recommended design

### Phase 1 - Bulk cached read on modal open

Replace the per-repository refresh/read sequence with one bulk read from persisted branch state.

Conceptually:

```text
Open New PR
   ↓
collect target repository IDs
   ↓
one bulk query for persisted remote branches/defaults
   ↓
group by RepositoryId
   ↓
populate all target selectors
```

Add an operation such as:

```csharp
Task<IReadOnlyDictionary<int, WorkspaceBranchesSnapshot>>
    GetBranchesForRepositoriesAsync(
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        IReadOnlyCollection<int> repositoryIds,
        CancellationToken cancellationToken);
```

The implementation should:

- create one fresh DbContext
- resolve all requested WorkspaceRepository rows in one query
- load all matching RepositoryBranches in one query
- load Feature context state in bulk when needed
- project results by repository ID
- avoid N calls to `TryResolveLinkedRepoAsync`
- avoid N DbContexts
- avoid remote access
- avoid persistence
- avoid `WorkspaceSynced`

### Phase 2 - Resolve initial PR base locally

Use the existing target-branch rules:

1. Feature parent branch when still available
2. repository default branch
3. another valid remote branch

This is pure local logic and should remain cheap.

### Phase 3 - Optional freshness

If branch data needs freshness guarantees, refresh after the modal is already usable.

Possible approaches:

#### Option A - Staleness-based background refresh

If branch state is older than a configured threshold:

```text
show cached branches immediately
    ↓
refresh stale repositories in background
    ↓
update selector only if branch list changed
```

#### Option B - Explicit Refresh control

Provide a lightweight refresh action in the Branch tab.

This gives the user control and keeps opening deterministic.

#### Option C - Lightweight remote branch refresh

Introduce a purpose-built Worker operation that refreshes only what this UI needs.

It should avoid:

- tags
- GitVersion
- projects
- commit counts
- unnecessary state probes
- per-repository WorkspaceSynced broadcasts

For example, the Worker could perform a branch-only fetch/read path and return remote-tracking refs.

## Do not use full RefreshBranches for modal initialization

`RefreshBranches` is appropriate when GrayMoon deliberately wants to refresh branch state.

It is too heavy for a dropdown population path because it:

- fetches remote data
- fetches tags
- captures broader repository state
- persists branch data
- updates upstream-related state
- emits workspace notifications

The modal should not need mutation-style refresh semantics just to render.

## Notification improvement

Today a repository branch refresh can emit:

```csharp
WorkspaceSynced
```

for every repository.

For a 24-repository modal-open operation, that can produce repeated workspace-wide notifications.

Even if the current event consumers debounce or avoid full reloads, this is avoidable churn.

If a future bulk refresh is required:

- perform the work as one batch
- persist in bulk
- emit one workspace notification at the end

## Reviewer loading

Reviewer loading should be considered in the same implementation effort because it contributes to modal-open pressure.

Recommended approach:

- do not block modal validity on reviewers
- lazy-load reviewers only when the Review tab is opened, or
- fetch reviewer options once per GitHub owner/connector where possible
- cache results for a short period
- avoid duplicate repository requests when many repositories share the same organization

This is secondary to target branches but belongs in the same performance pass.

## Unpushed-state check

Keep the current functional behavior, but verify whether it can be answered from already-known repository state before performing any expensive probes.

The modal should not wait for the unpushed check to make branch selectors usable.

## Expected performance impact

With persisted branch state already available:

### Current path

```text
N repositories
× network fetch
× Git process
× state probe
× persistence
× query-back
```

### Proposed path

```text
1-2 SQLite queries
+ in-memory grouping
+ optional background refresh
```

For a 24-repository workspace, target-branch rendering should move from seconds/tens of seconds to effectively immediate local UI latency.

## Instrumentation

Add timings for:

- modal open → cached branches rendered
- bulk branch query
- reviewer load
- unpushed check
- optional background branch refresh
- total repositories
- cache hit/stale counts

Do not rely only on subjective measurements.

## Tests

### Unit tests

- bulk branch projection returns correct branches per repository
- Feature parent branch preferred when present
- default branch used when parent is absent
- head branch cannot be selected as base
- repository with no valid base is reported correctly

### Integration tests

Use 24+ repositories with persisted branch rows and verify:

- modal branch state is populated without Worker calls
- no remote fetch is executed during first paint
- no WorkspaceSynced event is emitted during cached load
- query count remains bounded and does not scale linearly with repository count

### Performance test

Create a synthetic 100-repository workspace and assert that cached target-branch initialization stays within a small local threshold.

The exact threshold should be determined from CI/VDI reality rather than hard-coded prematurely, but the test should detect accidental N+1 regressions.

## Acceptance criteria

- Opening New Pull Request does not fetch every repository before branch selectors become usable.
- Target branch data is loaded in bulk from persisted state.
- Initial modal branch rendering does not scale linearly with remote latency.
- Optional freshness happens outside the critical UI-open path.
- Reviewer loading does not unnecessarily multiply per-repository requests.
- WorkspaceSynced is not emitted once per repository merely to populate the modal.
- Existing target-branch selection semantics remain unchanged.
