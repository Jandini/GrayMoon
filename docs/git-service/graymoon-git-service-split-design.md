# GrayMoon Git Service Split Design
## Pre-LibGit2Sharp Refactor for `sync-is-slow`

### Purpose

This document defines the refactor that should be completed on the current `sync-is-slow` branch before introducing LibGit2Sharp.

The goal is not to redesign GrayMoon's Git architecture from scratch. The goal is to preserve the performance work already completed, reduce the responsibility and size of `IGitService` / `GitService`, and create a clean read-side seam that LibGit2Sharp can later implement without disturbing Git mutations, worktree lifecycle, or the existing Git Changes feature.

At the time of review, `GitService.cs` is roughly 3,100 lines and exposes more than 50 public operations. The issue is no longer just file size. The service currently combines several distinct responsibilities:

- read-only local repository inspection
- Git mutations and network operations
- worktree lifecycle and Feature-specific policy
- GitVersion invocation
- filesystem helpers
- hook installation
- remote/default-branch repair behavior

The refactor should split these responsibilities by behavior, not by Git noun or verb.

---

# Design principles

1. **Do not throw away the optimization work already completed on `sync-is-slow`.**
   `RefSnapshot`, commit-count probes, reduced subprocess counts, timing instrumentation, awkward-repository tests, and origin/default-branch logic remain valuable.

2. **Do not introduce LibGit2Sharp in this refactor.**
   This change should be behavior-preserving and should leave the branch in a clean state where LibGit2Sharp can be added afterward as a separate implementation step.

3. **Do not split Git into many tiny interfaces.**
   Avoid abstractions such as `IGitBranchService`, `IGitTagService`, `IGitRemoteService`, `IGitCommitService`, etc. That would reduce file size while increasing architectural fragmentation.

4. **Separate reads from operations.**
   Local repository inspection should not expose CLI implementation details such as `GitLockIntent`.

5. **Keep GrayMoon domain models as the contract.**
   Future LibGit2Sharp types must never leak into command handlers, orchestration code, or application models.

6. **Preserve existing behavior and tests first.**
   The first refactor should move code without changing observable behavior.

---

# Target architecture

The intended pre-LibGit2Sharp architecture is:

```text
                     GrayMoon Worker
                           │
             ┌─────────────┼──────────────┐
             │             │              │
             ▼             ▼              ▼
 IGitRepositoryReader   IGitService   IGitWorktreeService
             │             │              │
             │             │              │
       Git CLI reader   GitService   GitWorktreeService
             │             │              │
             └─────────────┴──────────────┘
                           │
                   GitProcessRunner
                           │
                        git.exe
```

After this refactor, a later LibGit2Sharp change should require only replacing the implementation behind `IGitRepositoryReader`:

```text
IGitRepositoryReader
        │
        ├── GitCliRepositoryReader   (initial implementation / fallback)
        │
        └── LibGit2RepositoryReader  (future)
```

---

# 1. Introduce `IGitRepositoryReader`

This is the most important extraction.

`IGitRepositoryReader` owns local, read-only repository inspection. These are the operations that are candidates for future in-process implementation with LibGit2Sharp.

Suggested initial contract:

```csharp
public interface IGitRepositoryReader
{
    Task<string?> GetCurrentBranchNameAsync(
        string repoPath,
        CancellationToken ct);

    Task<string?> GetHeadCommitAsync(
        string repoPath,
        CancellationToken ct);

    Task<IReadOnlyList<string>> FindBranchCollisionsAsync(
        string repoPath,
        string branchName,
        CancellationToken ct);

    Task<string?> RevParseAsync(
        string repoPath,
        string rev,
        CancellationToken ct);

    Task<string?> GetRemoteOriginUrlAsync(
        string repoPath,
        CancellationToken ct);

    Task<CommitCountsProbeResult> ProbeCommitCountsAsync(
        string repoPath,
        string branchName,
        string? defaultBranchOriginRef,
        CancellationToken ct,
        bool skipUpstreamCheck = false);

    Task<(int? Outgoing, int? Incoming, bool HasUpstream)> GetCommitCountsAsync(
        string repoPath,
        string branchName,
        string? defaultBranchOriginRef,
        CancellationToken ct,
        bool skipUpstreamCheck = false);

    Task<(int? DefaultBehind, int? DefaultAhead, string? DefaultBranchName)> GetCommitCountsVsDefaultAsync(
        string repoPath,
        string? defaultBranchOriginRef,
        CancellationToken ct);

    Task<IReadOnlyList<string>> GetLocalBranchesAsync(
        string repoPath,
        CancellationToken ct);

    Task<RefSnapshot?> GetRefSnapshotAsync(
        string repoPath,
        CancellationToken ct);

    Task<IReadOnlyList<string>> GetRemoteBranchesFromRefsAsync(
        string repoPath,
        CancellationToken ct);

    Task<string?> GetDefaultBranchNameAsync(
        string repoPath,
        CancellationToken ct);

    Task<IReadOnlyList<string>> GetTagsAsync(
        string repoPath,
        CancellationToken ct);

    Task<string?> GetCheckedOutTagAsync(
        string repoPath,
        CancellationToken ct);

    Task<string?> GetDefaultBranchOriginRefAsync(
        string repoPath,
        CancellationToken ct);

    Task<string?> GetDivergenceBaseBranchAsync(
        string repoPath,
        CancellationToken ct);
}
```

The exact method list can be adjusted while implementing, but the ownership rule should remain:

> If the operation only answers a question about local repository state and does not intentionally mutate repository state or contact the network, it belongs in `IGitRepositoryReader`.

---

# 2. Remove `GitLockIntent` from the read contract

The current `IGitService` exposes `GitLockIntent` on methods such as:

```csharp
GetCurrentBranchNameAsync(...)
GetRefSnapshotAsync(...)
GetTagsAsync(...)
ProbeCommitCountsAsync(...)
GetCommitCountsVsDefaultAsync(...)
```

This is an implementation leak.

`GitLockIntent` exists because the current backend shells out to Git and needs to decide whether to use `--no-optional-locks` and the read/write runner lane.

That concern must stay inside the CLI implementation.

The new public read contract should look like:

```csharp
await repositoryReader.GetRefSnapshotAsync(repoPath, ct);
```

The CLI implementation may internally do:

```csharp
RunGitWithIntentAsync(..., GitLockIntent.Read)
```

A future LibGit2Sharp implementation should not know that `GitLockIntent` exists.

This is one of the main reasons to perform the split before adding the library.

---

# 3. Create `GitCliRepositoryReader`

The initial implementation of `IGitRepositoryReader` should be `GitCliRepositoryReader`.

This class should receive:

```csharp
GitProcessRunner
ILogger<GitCliRepositoryReader>
```

and should initially contain the existing optimized code moved out of `GitService`.

This means:

- no behavior change
- no command-shape change
- no new library
- no performance regression
- existing process-reduction work remains intact

The current `sync-is-slow` optimizations should move with the implementation:

- combined `RefSnapshot`
- reduced `for-each-ref` calls
- commit-count probing
- local ref based remote/default resolution
- read-lane usage
- `--no-optional-locks`
- fallback behavior for awkward repositories

Do not re-expand consolidated queries into smaller methods merely because a new class is being created.

`RefSnapshot` in particular should remain coarse-grained.

---

# 4. Keep `IGitService` for Git operations

After the split, `IGitService` should represent intentional Git operations, especially those where native Git remains the preferred implementation.

It should retain operations such as:

```csharp
CloneAsync
CloneIntoAsync

FetchAsync
FetchMinimalAsync
FetchTagsAsync
PullAsync
PushAsync

CheckoutBranchAsync
CreateBranchAsync
DeleteBranchAsync
CheckoutTagAsync
CheckoutTrackingAsync
SetUnbornHeadAsync

MergeFromRemoteAsync
AbortMergeAsync

StageAndCommitAsync
ResetToRemoteAsync

InitAsync
AddRemoteAsync

GetRemoteBranchesAsync
GetRemoteDefaultBranchAsync
RepairOriginHeadAsync

SetDivergenceBaseBranchAsync

AddSafeDirectoryAsync
WriteSyncHooksAsync
```

These operations may:

- mutate repository state
- contact a remote
- rely on Git credential helpers
- rely on hooks
- rely on native Git semantics
- require retry/progress/streaming behavior

They should continue to use `GitProcessRunner`.

The purpose of this refactor is not to eliminate `GitService`; it is to make `GitService` mean something coherent.

---

# 5. Extract `IGitWorktreeService`

Worktree management is a distinct responsibility and already contains substantial GrayMoon Feature policy.

Create:

```csharp
public interface IGitWorktreeService
{
    Task<(bool Success,
          IReadOnlyList<GitWorktreeInfo> Worktrees,
          string? ErrorCode,
          string? ErrorMessage)> ListWorktreesAsync(
        string mainRepositoryPath,
        CancellationToken ct);

    Task<(bool Success,
          GitWorktreeInfo? Worktree,
          bool AlreadyExisted,
          string? ErrorCode,
          string? ErrorMessage)> CreateWorktreeAsync(
        string mainRepositoryPath,
        string worktreePath,
        string? branchName,
        string baseCommitSha,
        CancellationToken ct);

    Task<(bool Success,
          bool AlreadyRemoved,
          string? ErrorCode,
          string? ErrorMessage,
          WorktreeResidueResult Residue)> RemoveWorktreeAsync(
        string mainRepositoryPath,
        string worktreePath,
        bool force,
        CancellationToken ct,
        string? featureRootPath = null,
        string? featureStorageRoot = null,
        bool unlock = false);

    Task<WorktreeInspectionResult> InspectWorktreeAsync(
        string mainRepositoryPath,
        string worktreePath,
        string? defaultBranch,
        string? featureBranch,
        CancellationToken ct);
}
```

Implementation:

```text
GitWorktreeService
```

The implementation can still use:

```text
GitProcessRunner
IGitRepositoryReader
IGitService
```

where appropriate.

For example, `InspectWorktreeAsync` may need local repository facts. Those should increasingly come from `IGitRepositoryReader` rather than reimplementing Git reads inside the worktree service.

Do not migrate worktree add/remove behavior to LibGit2Sharp as part of the later reader work. Native Git should remain authoritative for Feature worktree lifecycle for now.

---

# 6. Filesystem helpers must leave `IGitService`

The following methods are not Git operations:

```csharp
GetWorkspacePath
CreateDirectory
DirectoryExists
GetDirectories
```

They should not move into a new `IGitFileSystemService`.

That would only preserve the original design problem under another name.

Instead:

- `GetWorkspacePath` should move to the workspace/path-owning component
- `Directory.CreateDirectory` should be used directly where appropriate, or through an existing filesystem abstraction if GrayMoon already has one
- `Directory.Exists` should likewise stay in the owning service
- `Directory.GetDirectories` should be owned by whichever repository-discovery/workspace component performs the traversal

The important rule is:

> `IGitService` should not be used as a general-purpose dependency simply because the caller happens to be performing a Git-related workflow.

---

# 7. Keep `IRepositoryGitChangesService` separate

Do not merge the existing Git Changes abstraction into `IGitRepositoryReader` during this refactor.

Current `IRepositoryGitChangesService` owns a feature-level contract:

```csharp
GetStatusAsync
GetDiffAsync
StageAsync
UnstageAsync
DiscardAsync
CommitAsync
```

This combines both reads and mutations around the Git Changes feature.

It is already a coherent boundary and is independently tested.

Therefore, for the pre-LibGit2Sharp refactor:

```text
IRepositoryGitChangesService
    remains unchanged
```

Later, when LibGit2Sharp is introduced, GrayMoon can separately decide whether:

- `GetStatusAsync`
- `GetDiffAsync`

should internally reuse `IGitRepositoryReader`, or

- a LibGit2-backed Git Changes implementation should be introduced

That should not be mixed into this refactor.

---

# 8. GitVersion

`GetVersionAsync` and its associated logic are another distinct responsibility:

- tool discovery
- dotnet tool restore
- GitVersion invocation
- `/nonormalize`
- optional commit selection
- JSON parsing
- GitVersion-specific error handling

This is conceptually better represented by something such as:

```text
IGitVersionService
```

However, this extraction is not required for the first split.

Recommended approach:

### Required now

Extract:

```text
IGitRepositoryReader
IGitWorktreeService
filesystem helpers
```

### Optional follow-up

Move GitVersion into:

```text
IGitVersionService
GitVersionService
```

Only do this in the same change if it is low-risk and does not broaden the refactor significantly.

The primary objective is to create the read-side LibGit2 seam, not to achieve a perfectly decomposed Git subsystem in one pass.

---

# 9. `OriginDefaultRef` and repair behavior

The current branch has improved default-origin handling.

Preserve the distinction between:

### Read-side facts

Examples:

```text
What does origin/HEAD currently point to?
Does the ref exist?
Is it missing?
Is it dangling?
What default ref can be inferred locally?
```

These belong in `IGitRepositoryReader`.

### Repair / side effects

Examples:

```text
contact remote
run ls-remote
determine remote HEAD
set origin/HEAD
throttle repair attempts
```

These remain in `IGitService`.

The future flow should look like:

```text
IGitRepositoryReader
        │
        ▼
RefSnapshot
OriginHeadResolved = false
        │
        ▼
sync orchestration decides repair is appropriate
        │
        ▼
IGitService.RepairOriginHeadAsync(...)
```

Do not hide network activity inside the repository reader.

---

# 10. `RefSnapshot` remains a first-class contract

Do not replace `RefSnapshot` with many fine-grained calls after extraction.

The work already done on `sync-is-slow` demonstrates why it is valuable.

Today:

```text
GetRefSnapshotAsync
    → one consolidated Git ref query
```

Later:

```text
GetRefSnapshotAsync
    → one LibGit2Sharp Repository open
    → enumerate refs/tags/branches/head in-process
```

The orchestration remains unchanged.

This is exactly the kind of backend-independent, coarse-grained contract GrayMoon should preserve.

If more related facts naturally become available later, consider evolving the snapshot rather than making Sync perform many independent repository queries.

---

# 11. Dependency injection

The expected DI registrations after the refactor should be conceptually:

```csharp
services.AddScoped<IGitService, GitService>();
services.AddScoped<IGitRepositoryReader, GitCliRepositoryReader>();
services.AddScoped<IGitWorktreeService, GitWorktreeService>();
```

Adjust lifetime to GrayMoon's established service conventions.

The important point is that callers should depend on the smallest coherent capability they need.

Examples:

```text
SyncRepositoryCommand
    IGitService
    IGitRepositoryReader

Feature/worktree commands
    IGitWorktreeService
    IGitRepositoryReader where needed

repository discovery
    IGitRepositoryReader

Git Changes
    IRepositoryGitChangesService
```

Avoid a compatibility façade that simply injects all three and recreates a giant `IGitService`.

---

# 12. Migration strategy

Perform the split in small, testable units.

## Unit A - introduce reader contract

Create:

```text
IGitRepositoryReader
GitCliRepositoryReader
```

Move read-only operations from `GitService` without altering their implementation.

Update callers.

Run the existing test suite.

No LibGit2Sharp.

## Unit B - remove `GitLockIntent` from callers

Move read/write runner selection entirely into `GitCliRepositoryReader`.

Application/orchestration code should no longer pass `GitLockIntent` for repository reads.

Verify process-count tests still pass.

## Unit C - extract worktree service

Create:

```text
IGitWorktreeService
GitWorktreeService
```

Move:

```text
ListWorktreesAsync
CreateWorktreeAsync
RemoveWorktreeAsync
InspectWorktreeAsync
```

Preserve all existing behavior, safety guards, residue cleanup, branch checks, and Feature semantics.

## Unit D - remove filesystem helpers

Find every caller of:

```text
GetWorkspacePath
CreateDirectory
DirectoryExists
GetDirectories
```

Move each responsibility to the owning component.

Do not create a replacement Git filesystem interface unless one already exists and is appropriate.

## Unit E - cleanup `IGitService`

Remove moved members.

Review remaining methods as a group.

The resulting interface should mostly represent:

```text
remote/network operations
mutations
checkout/branch changes
merge/reset/commit
Git hooks
safe.directory
origin HEAD repair
```

## Optional Unit F - GitVersion extraction

Only if the change remains small and safe.

---

# 13. Tests

The refactor must not reduce coverage.

Existing tests that should be preserved and redirected to the new implementation include:

- `GitServiceFewerProcessesTests`
- commit-count probe tests
- long-path tests
- stage/commit tests
- delete-branch tests
- sync-hook tests
- awkward repository/ref behavior
- detached HEAD behavior
- unborn branch behavior
- origin/default branch behavior

Where tests exercise moved read functionality, rename or relocate them only when it improves clarity.

Example:

```text
GitServiceFewerProcessesTests
```

may become:

```text
GitCliRepositoryReaderFewerProcessesTests
```

if most of its assertions now apply to the reader.

The important performance contract must remain tested:

> extracting the reader must not silently reintroduce extra `git.exe` calls.

---

# 14. Non-goals

This refactor must NOT:

- add LibGit2Sharp
- rewrite Git Changes
- change fetch/push/clone semantics
- change authentication behavior
- change Feature/worktree behavior
- change hook behavior
- replace native Git worktree operations
- redesign GrayMoon's versioning system
- introduce new repository caching/lifetime complexity
- introduce a generic repository object abstraction
- expose LibGit2Sharp concepts
- redesign every Git-related service at once

---

# 15. Definition of done

The refactor is complete when:

1. `IGitRepositoryReader` exists and owns local read-only repository inspection.
2. `GitCliRepositoryReader` contains the current optimized CLI implementation.
3. `GitLockIntent` no longer leaks through the read-side public contract.
4. `IGitService` is materially smaller and focused on operational/mutating Git behavior.
5. `IGitWorktreeService` owns worktree lifecycle and inspection.
6. Filesystem helper methods no longer live on `IGitService`.
7. `IRepositoryGitChangesService` remains behaviorally unchanged.
8. Existing sync behavior is unchanged.
9. Existing process-count optimizations remain intact.
10. All existing tests pass.
11. No LibGit2Sharp dependency has been added yet.
12. The resulting architecture allows a future `LibGit2RepositoryReader` to replace `GitCliRepositoryReader` without changing Sync orchestration.

---

# Expected follow-up after this refactor

> **Superseded (2026-10):** this split is implemented. The LibGit2Sharp follow-up was built as a separate, sync-only `ILocalGitSnapshotReader` (one in-process snapshot per sync) rather than a whole-reader `LibGit2RepositoryReader`; `GitCliRepositoryReader` stays for every other caller and as sync's explicit fallback. See `docs/git-lib/graymoon-libgit2sharp-local-read-design.md`.

Only after this split is merged and verified should the LibGit2Sharp work begin.

The first LibGit2Sharp implementation should target:

```text
GetRefSnapshotAsync
GetCurrentBranchNameAsync
GetHeadCommitAsync
GetDefaultBranchOriginRefAsync
commit-count/ref graph reads
```

The CLI reader should initially remain available as a reference/fallback implementation.

The existing `sync-is-slow` timing instrumentation and process-count tests should then be used to compare:

```text
GitCliRepositoryReader
vs
LibGit2RepositoryReader
```

before expanding LibGit2Sharp usage into Git Changes status/diff reads.
