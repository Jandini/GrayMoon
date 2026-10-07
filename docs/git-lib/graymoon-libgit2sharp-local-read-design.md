# GrayMoon LibGit2Sharp Local Read Design

**Status:** Proposed  
**Target branch:** `main`  
**Scope:** Replace local read-only Git CLI calls in hot paths with LibGit2Sharp while keeping network and mutating operations on Git CLI.

## 1. Goal

GrayMoon has already introduced LibGit2Sharp successfully for Git ignore evaluation.

The next optimization should use the same pattern for local repository reads that currently spawn many `git.exe` processes.

The primary target is the workspace sync read lane.

Target split:

```text
git fetch                    -> keep Git CLI

local repository reads       -> LibGit2Sharp
  branch
  HEAD
  tags
  refs
  remote refs
  origin/HEAD
  upstream
  commit graph
  ahead/behind

network and mutations        -> keep Git CLI
```

## 2. Architectural principle

Do not translate individual CLI methods one-for-one.

The wrong approach is multiple small methods that each open a LibGit2Sharp `Repository`.

The correct unit of work is:

> one repository open per logical local-read operation.

For sync this means a single local repository snapshot.

## 3. Proposed abstraction

Introduce a focused abstraction, for example:

```csharp
public interface ILocalGitRepositorySnapshotReader
{
    LocalGitRepositorySnapshot Read(
        string repositoryPath,
        LocalGitRepositorySnapshotRequest request);
}
```

Possible result model:

```csharp
public sealed record LocalGitRepositorySnapshot(
    string? CurrentBranch,
    bool IsDetachedHead,
    string? CurrentTag,
    string? HeadSha,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> LocalBranches,
    IReadOnlyList<string> RemoteBranches,
    string? DefaultOriginRef,
    string? UpstreamRef,
    int? Ahead,
    int? Behind,
    int? DefaultAhead,
    int? DefaultBehind,
    bool HasUpstream,
    bool OriginHeadResolved);
```

Exact names can follow existing GrayMoon conventions.

## 4. Primary integration target

The first consumer should be `SyncRepositoryCommand`.

Current flow:

```text
fetch
  |
GitVersion + local read lane + project discovery
  |
branch fallback / origin-head repair
  |
commit counts / hooks
```

Replace the local read lane with:

```text
fetch
  |
open Repository once
  |
read local snapshot
  |
dispose
```

The snapshot should provide enough information to eliminate normal sync usage of:

```text
git for-each-ref
git rev-parse
git symbolic-ref
git rev-list
```

where practical.

## 5. Keep fetch on Git CLI

`git fetch` stays unchanged.

Reasons:

- GrayMoon remote authentication was recently hardened and centralized;
- Git CLI provides mature credential-manager behavior;
- enterprise proxy/certificate compatibility matters;
- fetch is not the dominant local-process problem;
- changing fetch would mix network/auth risk into a local-read optimization.

## 6. Keep mutations on Git CLI

Do not migrate in this phase:

```text
clone
fetch
pull
push
merge
checkout
switch
branch creation/deletion
tag mutation
reset
restore
add
commit
worktree creation/removal
remote configuration
```

## 7. Repository lifetime

Follow the pattern already established by `IGitIgnoreService`:

```text
one logical read operation
  -> one Repository
  -> build result
  -> dispose
```

Do not cache, pool, or share `Repository` instances across threads.

## 8. Thread safety

Treat LibGit2Sharp `Repository` as operation-scoped and single-threaded.

Different repositories may be read concurrently.

The same repository must continue to respect GrayMoon's existing repository-lock model.

## 9. Snapshot contents

The snapshot should cover:

- current branch;
- detached/unborn state;
- HEAD SHA;
- local branches;
- remote branches;
- tags;
- checked-out tag when HEAD is detached;
- `origin/HEAD` / default origin branch;
- upstream branch;
- ahead/behind vs upstream;
- ahead/behind vs divergence/default base;
- whether `origin/HEAD` is resolved.

Preserve existing GrayMoon response semantics and ordering expectations.

## 10. Origin HEAD repair

Repairing `origin/HEAD` mutates repository metadata.

Therefore:

```text
detect broken origin/HEAD       -> LibGit2Sharp snapshot
repair origin/HEAD              -> existing Git CLI path
re-read required state          -> LibGit2Sharp or current narrow read
```

Do not move repair logic into the snapshot abstraction.

## 11. Sync integration

Target flow:

```text
git fetch
  |
+---------------------------------------+
| parallel work                         |
|                                       |
| GitVersion                            |
| LibGit2Sharp Local Snapshot           |
| project discovery                     |
+---------------------------------------+
  |
origin HEAD repair if needed
  |
tail work
```

## 12. Avoid duplicate commit-graph work

The new snapshot should calculate graph relationships once where possible and return all values required downstream.

If two calculations differ semantically, make the distinction explicit in the request/result model.

## 13. Failure behavior

Introduce a focused failure model, for example `LocalGitReadException`.

Expected cases:

- repository removed during sync;
- corrupt repository;
- invalid worktree;
- unborn branch;
- missing commit;
- dangling refs.

Do not silently fall back to Git CLI for every failure.

If a temporary fallback is required, it must be explicit, logged, measurable, and removable.

## 14. Worktree support

GrayMoon uses linked worktrees heavily.

Open repositories from the worktree path and rely on LibGit2Sharp repository discovery.

Do not assume `repoPath/.git` is always a directory.

Tests must cover normal and linked worktrees.

## 15. Safe.directory

Do not remove existing `AddSafeDirectoryAsync` behavior in this phase.

Verify LibGit2Sharp behavior under:

- normal user ownership;
- Worker service account;
- enterprise VDI paths;
- repositories created by another identity where relevant.

## 16. Authentication

The local snapshot must not participate in authentication.

No credentials or tokens should be passed to LibGit2Sharp in this phase.

## 17. Logging

Add Debug timing for:

```text
repo
elapsedMs
branch
isDetached
localBranchCount
remoteBranchCount
tagCount
hasUpstream
originHeadResolved
graphCalculations
```

## 18. Test strategy

Parity-test LibGit2Sharp results against current CLI behavior using temporary repositories.

Required cases:

- normal branch;
- detached HEAD;
- tag checkout;
- unborn branch;
- upstream configured / missing;
- ahead / behind / diverged;
- renamed default branch;
- missing/dangling `origin/HEAD`;
- local and remote branches;
- lightweight and annotated tags;
- linked worktree;
- repository with no commits.

Existing GrayMoon sync behavior remains authoritative.

## 19. Rollout

Implement in stages:

1. add snapshot abstraction;
2. parity-test independently;
3. integrate only into `SyncRepositoryCommand`;
4. benchmark;
5. then consider reuse in other hot read paths.

Do not simultaneously migrate Git Changes status.

## 20. Future candidates

After this phase:

```text
GetGitChangeStatus
branch/status refresh
HEAD lookup
commit-count queries outside sync
```

These should reuse the local-read architecture.

## 21. Definition of done

This phase is complete when:

- a local repository snapshot reader exists;
- sync uses one LibGit2Sharp repository open for local-read state;
- normal sync no longer launches the targeted local read Git processes;
- fetch/network/mutations remain on Git CLI;
- origin-head repair remains separate;
- linked worktrees pass tests;
- CLI parity tests pass;
- existing sync tests pass;
- the enterprise VDI benchmark is rerun;
- before/after command counts and wall time are documented.
