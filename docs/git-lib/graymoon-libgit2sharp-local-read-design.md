# GrayMoon LibGit2Sharp Local Read Design

**Status:** Implemented (sync only). Revised after the pre-implementation code review against `main` at `e8ebf8e` (#394).
**Scope:** Replace the local read-only Git CLI calls on the sync path with one in-process LibGit2Sharp snapshot, while keeping fetch, authentication, repair, hooks, GitVersion and every mutation on the Git CLI.
**Companion plan:** `graymoon-libgit2sharp-local-read-implementation-plan.md` (same folder).

## 1. Goal

GrayMoon already uses LibGit2Sharp for Git ignore evaluation (`IGitIgnoreService`, #393). This phase uses the same pattern for the local repository reads of `SyncRepositoryCommand`.

```text
git fetch                    -> Git CLI (unchanged)

local repository reads       -> LibGit2Sharp, one snapshot per sync
  current branch / HEAD / unborn / detached
  tags (and the checked-out tag)
  local and origin branch lists
  origin/HEAD and the default origin branch
  upstream of the checked-out branch
  ahead/behind (upstream, divergence base, default)

network, repair, hooks,      -> Git CLI (unchanged)
GitVersion and mutations
```

## 2. Baseline (what the review found on `main`)

The Git service split (`docs/git-service/graymoon-git-service-split-design.md`) is already implemented: `IGitRepositoryReader` / `GitCliRepositoryReader` exist, `GitLockIntent` is not on the read contract, `GitWorktreeService` exists, and origin/HEAD read versus repair is already separated (`RefSnapshot.OriginHeadResolved` -> `IGitService.RepairOriginHeadAsync` after GitVersion).

The sync path was already lean. A quiet sync of a repository with an upstream used at most **5** git processes (pinned by `GitServiceFewerProcessesTests`): fetch, the ref listing, the counts against the default, the hooks location and the upstream tracking listing. The realistic target of this phase is **2** (fetch and hooks location): about 3 fewer processes per repository, about 117 per quiet sync of the 39-repository VDI workspace, plus the conditional reads in section 3.

## 3. Sync-time local Git CLI calls

| # | Where | Reader method | Command | When | Now |
|---|---|---|---|---|---|
| 1 | read lane | `GetRefSnapshotAsync` | `for-each-ref --sort=-creatordate ... refs/heads refs/remotes/origin refs/tags` | every sync | snapshot |
| 2 | read lane | `GetCheckedOutTagAsync` | `symbolic-ref -q HEAD`, then `describe --tags --exact-match` | detached or unborn HEAD | snapshot |
| 3 | read lane / after overlap | `GetCurrentBranchNameAsync` | `branch --show-current` | detached/unborn without a version provider, or the version provider gave no branch | snapshot |
| 4 | read lane | `GetCommitCountsVsDefaultAsync` | `rev-list --left-right --count <divRef>...HEAD` | a default or divergence ref exists | snapshot |
| 5 | tail | `ProbeCommitCountsAsync` (`ReadBranchTrackingAsync`) | `for-each-ref --format=%(HEAD)..%(upstream:short)%(upstream:track) refs/heads/<b> [refs/heads/<div>]` | branch is not "-" | snapshot when the branch is the checked-out one |
| 6 | tail | `ProbeCommitCountsAsync` | `rev-list --left-right --count <up>...HEAD` / `rev-list --count <cmp>..HEAD` | branch not HEAD, unparseable track, no or gone upstream | snapshot for the checked-out branch; Git CLI for any other branch |
| 7 | tail | `RefExistsAsync` / `GetDefaultBranchAsync` | `rev-parse --verify --quiet`, `for-each-ref origin/HEAD main master` | odd divergence names, default not known | snapshot |
| 8 | after repair | `GetDefaultBranchOriginRefAsync` + #4 | `for-each-ref` (+ `rev-parse --verify`) + `rev-list` | after a successful repair | a fresh snapshot |
| 9 | fallback lane | single-purpose reads | `tag --sort`, `for-each-ref`, `symbolic-ref`, `rev-parse --verify` | the snapshot cannot read the repository | Git CLI (the sanctioned fallback, section 9) |

Stays on the Git CLI: clone; fetch (`RunRemoteAsync`, authentication); `AddSafeDirectoryAsync` (`rev-parse --is-inside-work-tree` once per process, `config --global`); GitVersion; `RepairOriginHeadAsync` (`ls-remote`, `remote set-head`); `WriteSyncHooksAsync` (`rev-parse --git-common-dir --git-path hooks`, plus `config --get core.hooksPath` only when it points outside); `rev-parse --git-dir` for the divergence base file in unusual layouts. `SetDivergenceBaseBranchAsync` is a file write and stays outside the snapshot.

## 4. Abstraction

A focused, sync-only abstraction next to `IGitRepositoryReader`, following the `IGitIgnoreService` precedent. It supersedes the split design's idea of a whole-reader `LibGit2RepositoryReader`: the CLI reader stays for every other caller and as sync's fallback.

```csharp
public interface ILocalGitSnapshotReader
{
    LocalGitSnapshot Read(string repositoryPath, LocalGitSnapshotRequest request, CancellationToken ct);
}

public sealed record LocalGitSnapshotRequest(string? DivergenceBaseBranch);

public sealed record LocalGitSnapshot(
    RefSnapshot Refs,                           // tags, branch lists, CheckedOutBranch, DefaultOriginRef, OriginHeadResolved
    string? CurrentBranch,                      // attached branch, unborn included; null when detached
    bool IsHeadUnborn,
    string? HeadSha,
    string? CheckedOutTag,                      // detached only, git describe --tags --exact-match rules
    int? DefaultBehind, int? DefaultAhead,      // vs origin/<divergence base>, else the default origin ref
    CommitCountsProbeResult? CurrentBranchCounts, // ProbeCommitCountsAsync semantics for CurrentBranch
    int GraphCalculations);
```

Implementation: `LibGit2SharpLocalGitSnapshotReader` (sealed, stateless singleton). `LocalGitReadException` is the failure type. The API is synchronous; `SyncRepositoryCommand` runs it in `Task.Run` so it still overlaps GitVersion.

## 5. Repository lifetime and threading

```text
one sync read lane -> one Repository -> immutable snapshot -> dispose
```

No caching, pooling or sharing. The result holds plain values only. `ct` is checked between steps; a single libgit2 graph walk cannot be interrupted. Different repositories are read concurrently; disposal is prompt because open pack handles on Windows can block worktree removal and pack deletion.

## 6. Snapshot semantics (parity rules)

The CLI reads are the oracle (`LocalGitSnapshotParityTests`):

- **Current branch:** from the HEAD reference itself, so an unborn branch keeps its name. `Refs.CheckedOutBranch` keeps its old meaning (attached and existing as a ref). In a linked worktree it is that worktree's HEAD.
- **Tags:** `git tag --sort=-creatordate` order: tagger date (annotated) or committer date (lightweight) in whole seconds, newest first, ties in ascending ordinal ref name order. A tag on a tree or blob sorts as date 0.
- **Checked-out tag:** only for a detached HEAD; annotated beats lightweight, the newer annotated tag wins, otherwise the first in ref name order (verified against `git describe --tags --exact-match`).
- **Branch lists:** sorted with the same comparer as before (`OrderBy(b => b)`); origin/HEAD excluded; dangling symbolic refs left out like `for-each-ref` does.
- **Default origin ref / origin/HEAD:** the shared `OriginDefaultRef.Pick` rule (origin/HEAD target when it exists, then `origin/main`, then `origin/master`).
- **Three distinct comparisons** (named explicitly because they differ):
  1. "vs default" counts: HEAD against `origin/<divergence base>`, else the default origin ref (`DefaultBehind`/`DefaultAhead`);
  2. upstream counts: HEAD against the configured upstream's local tracking ref (`refs/remotes/<remote>/<name>`, or a local branch for a `.` remote);
  3. no-upstream (or gone-upstream) outgoing: HEAD against the *local* divergence base branch when it exists, else the default origin ref, with no incoming count.
- **Counts semantics:** `CommitCountsProbeResult` unchanged (`CountsProbed` false when there is nothing to compare with or HEAD is unborn; `UpstreamProbed` true). The divergence base comes from the request and is normalised the way the divergence base file stores and reads it.

### Deliberate differences from the old CLI output (approved)

- **Branch names are the full name** below `refs/heads/` / `refs/remotes/origin/`, never git's disambiguated `%(refname:short)`. Before: a branch sharing its name with a tag was listed as `heads/dup` (selecting it checked out a detached HEAD), and an origin branch whose name collided with a local branch called `origin/<name>` became `remotes/origin/<name>` and silently dropped out of the remote list (so the App pruned it). The CLI reads (`GetRefSnapshotAsync`, `GetLocalBranchesAsync`, `GetRemoteBranchesFromRefsAsync`) were fixed the same way, so the fallback and every other caller agree.
- **Comparison refs are resolved by full ref name**, not git's revision lookup, so a tag named like a divergence base or default branch never stands in for the branch.

## 7. Sync integration (as built)

```text
git fetch (CLI)
  |
+----------------------------------------------------------+
| parallel                                                 |
|  GitVersion (CLI, holds the repository write lock)       |
|  read lane: write divergence base file, then the         |
|             LibGit2Sharp snapshot (no lock)              |
|  project discovery                                       |
+----------------------------------------------------------+
  |
branch = GitVersion branch ?? snapshot.CurrentBranch
  |
origin/HEAD repair if unresolved (CLI mutation), then a fresh snapshot
  |
tail: hooks (CLI) + counts
        counts = snapshot.CurrentBranchCounts when branch == CurrentBranch
               = CLI ProbeCommitCountsAsync for any other branch name
```

The separate `branch --show-current` after the overlap is gone on the snapshot path; GitVersion's branch still wins when it gives one.

## 8. Origin HEAD repair

```text
detect missing/dangling origin/HEAD   -> snapshot (OriginHeadResolved = false)
repair origin/HEAD                    -> existing CLI path (ls-remote + remote set-head, throttled)
re-read                               -> a fresh snapshot (default ref, default counts, CurrentBranchCounts)
```

A repair can change the default, so counts the first snapshot took against the old default are stale; the fresh snapshot replaces them. Repair runs after the snapshot task has finished and disposed its repository, so there is no lock interaction.

## 9. Failure behaviour and the single sanctioned fallback

- The snapshot throws `LocalGitReadException` for any LibGit2Sharp failure (corrupt or unsupported repository, ownership refusal, missing native library, IO).
- `SyncRepositoryCommand` then falls back to the existing Git CLI read lane. This is the one sanctioned fallback: logged at Warning with the exception, shown in the timing line (`lane=...ms [GitCli]`, `snapshot(failed)`), and removable later. It is existing, tested code.
- **No fallback** when the folder has no `.git` of its own: git would walk up and answer for an enclosing repository (for example a broken clone under the Workspace repository root). Sync logs a warning and reports no refs.
- **No snapshot** when `GIT_DIR` is set in the Worker environment (libgit2 does not honour it): the CLI lane is used.

## 10. Locking

The snapshot takes no Worker lock, exactly like the read-intent CLI lane it replaces, so it still overlaps GitVersion. It acquires nothing, so there is no lock inversion. `GitProcessRunner.RepoLocks` is keyed by working-tree path; a main checkout and its Feature worktrees are not serialized against each other. That is unchanged behaviour. Concurrent writers can still be observed mid-change, as with the CLI reads.

## 11. Authentication, safe.directory

The snapshot never takes credentials and never contacts a remote. `AddSafeDirectoryAsync` is unchanged. libgit2 enforces repository ownership like git; a refusal is a `LocalGitReadException` and falls back to the CLI lane (which honours the `safe.directory` entry the Worker writes).

## 12. Logging

- One Debug record per snapshot: repo, elapsedMs, branch, detached, unborn, localBranchCount, remoteBranchCount, tagCount, hasUpstream, originHeadResolved, graphCalculations.
- The existing `SyncRepository timings` line now names the lane source (`[Snapshot]`, `[GitCli]`, `[None]`) and the counts source (`[snapshot]`, `[git]`, `[none]`), with lane steps `divergenceBase=`, `snapshot(graph=N)=`.

## 13. Compatibility notes

- Verified by tests: normal, detached, tag checkouts (ties included), unborn and empty repositories, orphan branches, gone/foreign/local upstreams, dangling and missing origin/HEAD, divergence bases (with and without `origin/`, missing), shallow clones, linked worktrees (branch, detached-on-tag, divergence base), ambiguous names, broken HEAD, bare repository, cancellation, handle release before `git worktree remove`.
- Not verifiable here: repository formats the bundled libgit2 may refuse (reftable, `objectFormat=sha256`, unknown extensions) and ownership behaviour under the Worker service account on enterprise VDI. Both fall back to the CLI lane with a warning, so they show up in logs rather than as wrong data.

## 14. Rollout and future candidates

Done in this phase: abstraction, parity tests, `SyncRepositoryCommand` integration, ambiguous-name fix, fallback, docs.

Still to do: rerun the enterprise VDI benchmark (39 repositories, 16 sync workers, three quiet syncs) and record before/after process counts and wall time.

Future candidates, reusing the same snapshot: the hook-driven sync commands (`CommitSyncRepositoryCommand`, `CheckoutHookSyncCommand`, `PushHookSyncCommand`), `GetCommitCounts`, branch/status refresh, HEAD lookups, optionally the hooks-location read. Git Changes status (`git status --porcelain=v2`) is a separate effort.

## 15. Definition of done

- [x] a local repository snapshot reader exists;
- [x] sync uses one LibGit2Sharp repository open for local-read state (two only after an origin/HEAD repair);
- [x] normal sync no longer launches `for-each-ref`, `rev-list`, `symbolic-ref`, `describe`, `branch --show-current`, `tag` or `rev-parse --verify`;
- [x] fetch, network, authentication and mutations remain on the Git CLI;
- [x] origin/HEAD repair remains separate;
- [x] linked worktrees pass tests;
- [x] CLI parity tests pass;
- [x] existing sync tests pass;
- [ ] the enterprise VDI benchmark is rerun and before/after command counts and wall time are documented.
