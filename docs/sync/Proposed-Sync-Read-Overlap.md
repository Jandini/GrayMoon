# Proposed: overlap read-only work inside one repository sync

**Status:** proposal only. No code change is described as done.  
**Date:** 2026-10-06

Workspace sync already runs repositories side by side, up to `Workspace:MaxParallelOperations` (16). Each repository is one `SyncRepository` command. Inside that command the steps run one after another, and the per-repository git write lock keeps even the steps that are started together from overlapping.

The long part of one repository is `git fetch origin --prune --tags`, then GitVersion (`/nofetch`). The branch list, tag list, commit counts, and `.csproj` scan wait behind those two even though they do not need to.

## Why

A sync feels slow because each repository pays for its fetch and its version calculation, and then pays again for a queue of reads that could have been in flight already.

Those later reads do not update refs or the index:

- checked-out tag (`symbolic-ref`, `describe --tags --exact-match`)
- tag list (`git tag`)
- local and remote branches (`for-each-ref`)
- the two ahead/behind counts (`rev-list --left-right --count`)
- `.csproj` discovery, which never touches `.git`

`SyncRepositoryCommand` already starts the two count calls together, and the two branch-list calls together. Both pairs still take the per-repository write lock (`GitLockIntent.Write` is the default), so a semaphore of 1 runs them one at a time.

Hiding that read work behind GitVersion shortens the repository without a second writer on the same repository. The tagged fetch and GitVersion stay as long as they are today.

## How

Change only `SyncRepositoryCommand`. Leave the shared git helpers on the write lock so checkout, commit, refresh, and hook paths keep their current exclusion.

### Order that stays

1. Clone, when the directory is missing, still finishes before anything else.
2. `git fetch origin --prune --tags` still finishes before GitVersion and before any ref read. GitVersion stays `/nofetch`, so it must see the fetch.
3. The branch name is still resolved after GitVersion. GitVersion’s branch wins when it returns one; otherwise the name comes from `git branch --show-current`.
4. Commit counts still wait for that winning name, and still run only when the checkout is a branch. A tag checkout still clears the branch and skips counts.
5. Hook install still waits until the checkout is a real branch or a tag.
6. A failed fetch still returns immediately, with no version and with projects left unset.
7. Project discovery still runs only when the workspace profile asks for it. A failed or skipped discovery still leaves projects unset, so the app does not treat “nobody looked” as “this repository has no projects.”

### What overlaps

After the directory exists, start the `.csproj` scan when the profile discovers projects. Await it only when building the success response. On a failed fetch, ignore that scan and keep projects unset.

After fetch succeeds, run GitVersion at the same time as the tag check, the tag list, and the local and remote branch lists.

Those git reads use `GitLockIntent.Read` and `--no-optional-locks` on this path only. That is the same pattern status and diff already use: the read does not take `index.lock`, so it can run while GitVersion holds the write lock. GitVersion’s own process stays on the write lock, because it runs git internally.

Once the winning branch name is known, run the two `rev-list` counts together, also as reads. Hook install can run in that same window. It writes `.git/hooks`, which is neither the index nor the refs.

### What stays on the write lock

Fetch, clone, checkout, merge, commit, and push stay one writer per repository. Reads on the sync path wait until that fetch has released the lock. A read started early would see refs from before the fetch, or refs mid-update.

## Why this does not change existing behavior

The response shape stays the same. Success still carries version, branch, tag, tags, both branch lists, both count pairs, upstream, and projects under the same rules. Failure on fetch still carries the fetch error and leaves the optional groups absent.

Cross-repository fan-out stays capped by `Workspace:MaxParallelOperations`. This proposal does not raise that cap and does not turn the batch into an unbounded `Task.WhenAll`.

Shared helpers such as `GetTagsAsync`, `GetLocalBranchesAsync`, `ProbeCommitCountsAsync`, and `GetCommitCountsVsDefaultAsync` stay on the write lock for every other caller. Switching their default intent would let a sync read pass a checkout or a commit on those other paths. The read intent belongs on the calls `SyncRepositoryCommand` makes after fetch.

Persistence stays a sequential pass over one `DbContext` after the worker results return. That ordering is for EF, and this proposal does not change it.

The per-repository lock stays the boundary between writers. Overlapping reads with each other, and with GitVersion after fetch, does not add a second writer on the same repository.
