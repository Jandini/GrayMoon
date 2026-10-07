# GrayMoon LibGit2Sharp Local Read Implementation Plan

**Companion design:** `graymoon-libgit2sharp-local-read-design.md`

## 0. Mandatory pre-implementation code review

Before changing code, inspect the current `main` branch.

The implementing AI must first produce a short review covering:

- current `SyncRepositoryCommand` flow;
- `IGitRepositoryReader` and implementation;
- all sync-time local Git CLI calls;
- repository lock behavior;
- origin HEAD repair flow;
- divergence/upstream/count semantics;
- current LibGit2Sharp ignore implementation and lifetime pattern;
- relevant tests;
- recent changes that conflict with this plan.

**Do not modify production code until that review is complete.**

If current `main` differs materially from this plan, update the plan first.

## 1. Baseline

Record:

- current `main` SHA;
- quiet sync Git process counts;
- current enterprise VDI benchmark;
- exact local Git commands used during sync;
- relevant test-suite state.

## 2. Audit current local-read methods

Map every sync-time reader to its Git command(s).

Expected areas include:

```text
GetRefSnapshotAsync
GetCurrentBranchNameAsync
GetCheckedOutTagAsync
GetTagsAsync
GetLocalBranchesAsync
GetRemoteBranchesFromRefsAsync
GetDefaultBranchOriginRefAsync
GetCommitCountsVsDefaultAsync
ProbeCommitCountsAsync
GetUpstreamRefAsync
```

Search current `main`; do not assume the list is complete.

Deliver a table:

```text
method
command
sync caller
required output
candidate for snapshot
```

## 3. Add snapshot abstraction

Introduce `ILocalGitRepositorySnapshotReader` and a LibGit2Sharp implementation.

Keep it focused and separate from `IGitService`.

## 4. Define request/result model

The request should state which graph relationships are needed.

The result must be immutable/detached from the disposed LibGit2Sharp `Repository`.

Avoid lazy properties that reopen the repository later.

## 5. Implement repository lifetime

Pattern:

```csharp
using var repo = new Repository(repositoryPath);

// read all required state

return immutableSnapshot;
```

No sharing, pooling, or global cache.

## 6. Branch / HEAD state

Tests first:

- attached branch;
- detached HEAD;
- unborn branch;
- empty repository.

Preserve current GrayMoon semantics.

## 7. Tags and checked-out tag

Cover:

- lightweight tags;
- annotated tags;
- multiple tags at HEAD;
- detached commit with no tag.

Document deterministic behavior if multiple tags point to HEAD.

## 8. Local and remote branches

Return names matching current contracts.

Review handling of `origin/HEAD` so it is not accidentally exposed as an ordinary remote branch if existing UI excludes it.

## 9. Default origin branch

Resolve the current equivalent of `refs/remotes/origin/HEAD`.

Return:

```text
DefaultOriginRef
OriginHeadResolved
```

Do not repair here.

## 10. Upstream

Return:

```text
HasUpstream
UpstreamRef
```

Test stale/deleted upstream and detached HEAD behavior.

## 11. Ahead/behind

Use LibGit2Sharp commit graph APIs.

Support:

- current branch vs upstream;
- branch vs divergence/default base;
- default branch counts used by sync.

Add parity tests against current `rev-list --left-right --count` behavior.

## 12. Integrate with SyncRepositoryCommand

Keep this structure:

```text
fetch
  |
parallel:
  GitVersion
  local snapshot
  project discovery
```

Use snapshot values for branch/tag/ref/count/upstream state.

Do not alter GitVersion behavior in this phase.

## 13. Preserve origin HEAD repair

If snapshot reports missing/dangling origin HEAD:

- call the existing repair mutation;
- re-read only the minimum state required afterward.

Do not rewrite remote authentication.

## 14. Remove redundant sync CLI reads

After integration, search the sync path.

Normal successful sync should no longer need the migrated:

```text
for-each-ref
rev-parse
symbolic-ref
rev-list
```

calls.

Document any remaining exceptional calls.

## 15. Locking review

Verify:

- snapshot reads do not overlap unsafe mutations;
- no `Repository` instance escapes an operation;
- no lock inversion is introduced;
- origin-head repair cannot deadlock with snapshot execution.

## 16. Worktree integration tests

Use real linked worktrees.

Verify:

- branch;
- HEAD SHA;
- tags;
- upstream;
- branch lists;
- default branch;
- ahead/behind.

## 17. Failure handling

Cover:

- missing repository;
- repository deleted mid-operation;
- corrupt repository;
- invalid HEAD;
- missing refs;
- empty repository.

Avoid broad silent catches.

## 18. Instrumentation

Add one Debug timing record per snapshot:

```text
repo
elapsedMs
tagCount
localBranchCount
remoteBranchCount
hasUpstream
originHeadResolved
graphCalculations
```

Keep existing `SyncRepository` timing logs.

## 19. Test gate

Run:

- new snapshot parity tests;
- existing Git reader tests;
- sync tests;
- worktree tests;
- full Worker test suite.

Do not simply rewrite expectations to match LibGit2Sharp if GrayMoon behavior changes.

## 20. Performance gate

Re-run the same enterprise VDI AVR workspace where possible:

```text
39 repositories
16 sync workers
```

Collect at least three quiet syncs.

Compare:

- wall clock;
- SyncRepository p50/p90;
- total Git command count;
- for-each-ref count;
- rev-parse count;
- symbolic-ref count;
- rev-list count;
- GitVersion duration;
- fetch duration;
- LibGit2Sharp snapshot duration.

Expected normal target:

```text
for-each-ref -> 0 in snapshot path
rev-list     -> 0 in snapshot path
rev-parse    -> near-zero / exceptional only
symbolic-ref -> exceptional/repair only
```

## 21. Cleanup

After tests and benchmark:

- remove obsolete sync-only readers if unused;
- keep CLI readers still required elsewhere;
- avoid unrelated Git-service refactoring;
- update stale XML comments.

## 22. Living documentation

Update the Git architecture docs to reflect:

```text
LibGit2Sharp:
  ignore evaluation
  local repository snapshot reads

Git CLI:
  network
  authentication
  mutations
  worktree changes
  repair operations
```

Record measured before/after performance.

## Checklist

- [ ] pre-implementation code review complete
- [ ] current main SHA recorded
- [ ] sync local-read call graph documented
- [ ] snapshot abstraction added
- [ ] branch/HEAD parity tests
- [ ] tags parity tests
- [ ] branch-list parity tests
- [ ] origin HEAD tests
- [ ] upstream tests
- [ ] ahead/behind parity tests
- [ ] linked worktree tests
- [ ] `SyncRepositoryCommand` integrated
- [ ] origin HEAD repair preserved
- [ ] redundant CLI reads removed
- [ ] locking review complete
- [ ] failure handling reviewed
- [ ] snapshot timings added
- [ ] Worker tests pass
- [ ] enterprise VDI benchmark rerun
- [ ] living docs updated

## Final acceptance criteria

1. Sync opens one LibGit2Sharp `Repository` for its local snapshot.
2. The snapshot returns all local-read state required by sync.
3. Normal sync no longer shells out for migrated ref/graph reads.
4. Fetch/network/auth flows remain Git CLI.
5. Mutations remain Git CLI.
6. Origin HEAD repair remains separate and correct.
7. Worktrees are covered by real integration tests.
8. Existing public sync behavior remains compatible.
9. The VDI benchmark shows the expected command-count reduction.
10. Architecture docs match the implementation.
