# GrayMoon LibGit2Sharp Local Read Implementation Plan

**Companion design:** `graymoon-libgit2sharp-local-read-design.md` (same folder)
**Status:** Implemented on branch `sync-git-lib`, except the enterprise VDI benchmark (step 8).

## 0. Pre-implementation review (done)

Reviewed `main` at `e8ebf8e` (#394) before any code change: `SyncRepositoryCommand`, `IGitRepositoryReader` / `GitCliRepositoryReader`, `GitProcessRunner` locking, origin/HEAD repair, worktree handling (`GitDirectoryLocator`, linked worktrees), and `LibGit2SharpGitIgnoreService`. Findings that changed this plan:

- The Git service split is already done; the new snapshot is a separate sync-only abstraction, not a replacement for `IGitRepositoryReader`.
- A quiet sync already used at most 5 git processes; the target is 2 (fetch + hooks location), not "near zero rev-parse" (hooks keep one `rev-parse`).
- `GetUpstreamRefAsync` is not on the sync path; `describe` and `branch --show-current` are.
- Upstream counts depend on GitVersion's branch: the snapshot counts the checked-out branch, and the tail uses them only when the branch names match.
- A repair can make no-upstream counts stale: take a fresh snapshot after a successful repair.
- Decisions approved: fix ambiguous short branch names (CLI reads too); keep the CLI read lane as the single explicit, logged fallback.

## 1. Baseline

- `main` SHA: `e8ebf8e`.
- Quiet sync with an upstream: at most 5 git processes (`fetch`, `for-each-ref` listing, `rev-list` vs default, `rev-parse --git-common-dir --git-path hooks`, `for-each-ref` tracking), plus `symbolic-ref`/`describe`/`branch --show-current` on detached or unborn HEADs, and a gone-upstream `rev-list`.
- Tests before the change: Worker 478 passed / 1 skipped.
- VDI benchmark baseline: the 82.1 s clean sync recorded in the gitignore design (after #393 the `check-ignore` part is gone); a fresh quiet-sync baseline should be taken from `main` when the benchmark is rerun.

## 2. Sync-time call audit

See design section 3 (method, command, caller, condition, outcome for every call).

## 3. Abstraction and model (done)

- `src/GrayMoon.Worker/Abstractions/ILocalGitSnapshotReader.cs`: `ILocalGitSnapshotReader`, `LocalGitSnapshotRequest`, `LocalGitReadException`.
- `src/GrayMoon.Worker/Models/LocalGitSnapshot.cs`: immutable result; embeds `RefSnapshot`.
- `src/GrayMoon.Worker/Services/LibGit2SharpLocalGitSnapshotReader.cs`: one `using var repository = new Repository(path)` per call; registered as a singleton in `RunCommandHandler`.

## 4. Parity tests (done)

`src/GrayMoon.Worker.Tests/LocalGitSnapshotParityTests.cs` (36 tests) compares every snapshot field with the CLI read answering the same question: attached/detached/unborn/empty/orphan; tag order with ties and fixed dates; describe priority; tag of a tag and tag on a tree; awkward branch lists; ambiguous names; dangling, missing and non-main origin/HEAD; ahead/behind/diverged; gone, foreign-remote and local upstreams; no default; divergence base with and without `origin/` and missing; shallow clone; tag named like the divergence base; linked worktrees (with upstream, with divergence base, detached on a tag); not a repository, broken HEAD, bare, cancellation, handle release before `git worktree remove`.

The parity tests caught one real defect during implementation: LibGit2Sharp's `Branch.UpstreamBranchCanonicalName` is the upstream's name on the remote (`refs/heads/main`), not the local tracking ref; the reader uses `TrackedBranch` instead.

## 5. Ambiguous-name fix (done)

`GitCliRepositoryReader.GetRefSnapshotAsync` uses full ref names (`%(refname:strip=2)` / the name below `refs/remotes/origin/`), `GetLocalBranchesAsync` uses `%(refname:strip=2)`, `GetRemoteBranchesFromRefsAsync` uses `%(refname:strip=3)`. The snapshot does the same.

## 6. Sync integration (done)

`SyncRepositoryCommand`:

- read lane: write the divergence base file, then `Task.Run(snapshotReader.Read)`; no `.git` of its own -> no refs, no fallback; `GIT_DIR` set -> CLI lane; `LocalGitReadException` -> Warning + CLI lane (unchanged code);
- the branch fallback after GitVersion runs only when the lane did not read the branch (CLI lane);
- counts: `snapshot.CurrentBranchCounts` when the chosen branch is the checked-out one, else the CLI `ProbeCommitCountsAsync`;
- repair: CLI mutation, then a fresh snapshot (or the CLI re-read when the lane was the CLI or the fresh snapshot fails, in which case the counts go back to the CLI probe);
- timing line names the lane and counts sources; one Debug record per snapshot.

Sync-level tests: `SyncRepositoryLocalSnapshotTests` (fallback gives the same response, no-own-`.git` guard, non-checked-out version-provider branch counted by git, tag checkout without a read process); `GitServiceFewerProcessesTests` budgets tightened to 2 processes with no local-read commands, plus an ahead/behind sync.

## 7. Test gate (done)

- Worker: 519 passed, 1 skipped (the pre-existing skip).
- Common: 261 passed. App: 1130 passed.
- Solution build: 0 warnings.

## 8. Performance gate (open)

Rerun the enterprise VDI AVR workspace (39 repositories, 16 sync workers, at least three quiet syncs) on `main` and on this branch. Compare wall clock, `SyncRepository` p50/p90, total git process count, per-command counts (`for-each-ref`, `rev-list`, `symbolic-ref`, `rev-parse`, `describe`), GitVersion and fetch duration, and the `Local snapshot` elapsed time. Expected per quiet repository: `for-each-ref`, `rev-list`, `symbolic-ref`, `describe` = 0; `rev-parse` = 1 (hooks); total = 2 plus GitVersion when versioning is on. Check the logs for `falling back to git CLI reads` warnings (ownership or unsupported repository formats).

## 9. Living documentation (done)

- This plan and the design describe the as-built state.
- `docs/architecture/04-runtime-communication-and-concurrency.md` section 16 records the LibGit2Sharp / Git CLI split.
- `docs/git-service/graymoon-git-service-split-design.md` notes that its "future `LibGit2RepositoryReader`" follow-up was superseded.

## Checklist

- [x] pre-implementation code review complete
- [x] current main SHA recorded
- [x] sync local-read call graph documented
- [x] snapshot abstraction added
- [x] branch/HEAD parity tests
- [x] tags parity tests
- [x] branch-list parity tests (ambiguous names fixed)
- [x] origin HEAD tests
- [x] upstream tests
- [x] ahead/behind parity tests
- [x] linked worktree tests
- [x] `SyncRepositoryCommand` integrated
- [x] origin HEAD repair preserved (fresh snapshot after repair)
- [x] redundant CLI reads removed from the normal sync path (kept as the fallback lane)
- [x] locking review complete (no lock taken, no inversion, repair after dispose)
- [x] failure handling reviewed (typed exception, single logged fallback, no-own-`.git` guard)
- [x] snapshot timings added
- [x] Worker tests pass
- [ ] enterprise VDI benchmark rerun
- [x] living docs updated

## Final acceptance criteria

1. Sync opens one LibGit2Sharp `Repository` for its local snapshot (one more only after an origin/HEAD repair). Done.
2. The snapshot returns all local-read state required by sync. Done.
3. Normal sync no longer shells out for migrated ref/graph reads. Done (pinned by process-budget tests).
4. Fetch/network/auth flows remain Git CLI. Done.
5. Mutations remain Git CLI. Done.
6. Origin HEAD repair remains separate and correct. Done.
7. Worktrees are covered by real integration tests. Done.
8. Existing public sync behaviour remains compatible, except the approved branch-name fix. Done.
9. The VDI benchmark shows the expected command-count reduction. Open.
10. Architecture docs match the implementation. Done.
