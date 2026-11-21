# Operations that do something other than what was asked

**Priority: HIGH. Investigate and validate before the next release.** Written 2026-11-21.

Status: the bug that started this note is fixed (see "The bug this came from"). The six items under "Open items" came out of a code sweep for the same pattern. None of them is fixed, and none was reproduced in the running app: four were read end to end in the code, one is only plausible, one is cleanup. Each needs the validation named in its section before any fix.

Why high: items 1 and 2 publish local commits to the remote without the user asking for a push, the same harm as the original bug. A push to a public repository cannot be taken back once someone has fetched it.

## The bug this came from

**Undo Push Commits on a never-pushed branch pushed the branch instead of undoing the commits.**

Seen 2026-11-21 in the Feature `unhook-workspace`, repository `GrayMoon` (a public repository). Evidence: `graymoon-worker-20261121.log`, Worker request `9da36c8a17334b6e948398f3a69eb43d`, and the reflogs of the Feature worktree.

| Time (+05:30) | What happened |
|---|---|
| 13:51:08 | AI commit `ac31b787` "Document workspace unhooking and entry point changes", 3 files under `docs/todo/`. The branch had no upstream (`hasUpstream=false`, 1 outgoing). |
| 13:51:16 | `UndoPush` with "Keep changes". The Worker probes `origin/unhook-workspace`: not found. |
| 13:51:16 | Log: `Remote ref origin/unhook-workspace not found ... pushing branch upstream first`, then `git push -u origin unhook-workspace`. |
| 13:51:19 | GitHub creates the branch. The remote-tracking ref reflog says `update by push`. |
| 13:51:19 | `git reset --mixed origin/unhook-workspace`. The ref now equals HEAD, so the reset changes nothing. |

Result: the commit stayed, no file came back to Changes, the outgoing count went to 0, and two design documents were published on a public branch. No local data was lost. The same undo worked in the special Workspace only because that branch was already on the remote; every never-pushed branch took the broken path, and a new Feature branch is always never-pushed.

Cause: `GitService.ResetToRemoteAsync` pushed the branch when `origin/<branch>` was missing and then reset to the ref the push had just created.

Fix (uncommitted at the time of writing): with no `origin/<branch>`, reset to `git merge-base <compare ref> HEAD`, where the compare ref is the one the outgoing count uses (`IGitRepositoryReader.GetNoUpstreamCompareRefAsync`: the Feature divergence base as a local branch, otherwise the default origin branch). Fail without changing anything when there is no compare ref or no common commit. The command no longer contacts the remote. Regression tests: `GitServiceUndoUnpushedCommitsTests` (4 tests; 3 fail on the old code). Behavior is described in `docs/architecture/01-product-model-and-user-workflows.md` (Undo Push) and `05-user-capability-reference.md` (Undo Push).

Still to do for the bug itself:

- Manual check in the app with a rebuilt Worker: new Feature, commit, Undo Push Commits with and without "Keep changes".
- Commit `ac31b787` is still on `origin/unhook-workspace` of the public repository. Owner decision: leave it, or reset locally and delete or force-push the remote branch.

## The pattern

A command that, when its precondition is missing or a step fails, silently runs a different action (push, delete, switch, force) or reports success for a step that did not happen.

Scope of the sweep: every Worker command and `GitService` method that pushes, resets, deletes, cleans or forces, and the App callers of those commands.

**Not reviewed, still to investigate:** the App orchestrators (Prepare Workspace, Update and Push, Dependency Update), Feature create / repair / roll back / remove, the notification panel actions, and GrayMoon.Desktop.

Status words: CONFIRMED means read in the code path end to end, not run. PLAUSIBLE means it follows from the code but the outcome was not observed. Both need a failing test or a manual reproduction before a fix (workspace rule: items a review marks PLAUSIBLE are confirmed first).

## Open items

| # | Item | Status | Class |
|---|---|---|---|
| 1 | "Pull" also pushes | CONFIRMED by reading | Should |
| 2 | Sync Commits on a never-pushed branch publishes it without an upstream | PLAUSIBLE | Should |
| 3 | Return to Default reports success when a branch delete failed | CONFIRMED by reading | Should |
| 4 | Create Branch silently switches to an existing branch | CONFIRMED by reading | Should |
| 5 | Leftovers of the Undo fix | CONFIRMED | Optional |
| 6 | Interpolated git command lines in `GitService` | CONFIRMED | Optional |

### 1. "Pull" also pushes (CONFIRMED by reading, Should)

`CommitSyncRepositoryCommand` pulls and then, unless the request is `pullOnly` (read only repositories), pushes whenever outgoing commits remain (`src/GrayMoon.Worker/Commands/CommitSyncRepositoryCommand.cs`, the `git.PushAsync` call after `IntegrateAsync`).

The same command serves three user actions in `WorkspaceRepositories.CommitSync.cs`: the header **Pull** button (`OnPullClickAsync`), a click on a Commits badge with incoming commits (`OnPullBadgeClickAsync`), and the **Sync Commits** menu. For Sync Commits a push is the meaning of the action. For the two Pull entry points the label says pull only, yet a repository with both incoming and outgoing commits is merged and then pushed, which publishes local commits the user did not ask to publish. The notification panel's Pull action was not traced.

Validate: a `GitScenarios.CloneBehindRemote` test with one extra local commit, run the command as the Pull button sends it, assert the local commit is not on `origin.git`. Then the same by hand in the app, and trace the notification panel's Pull.

Suggested fix: a "do not push" flag sent from the Pull entry points (without the fast-forward-only and local-commit refusal of the read only path), or rename the entry points. `01-product-model-and-user-workflows.md` section 11 and `05-user-capability-reference.md` section 12 do not say that pull pushes; fix the documents in the same change.

### 2. Sync Commits on a never-pushed branch publishes it without an upstream (PLAUSIBLE, Should)

Same command. On a branch with no upstream, `GetCommitCountsAsync` reports outgoing commits against the divergence base or default branch and no incoming, so `IntegrateAsync` returns success and the command pushes with `setTracking: false`. That would create the branch on the remote and leave the local branch without an upstream. Expected consequences, not observed: the new remote branch appears although the user only asked to sync, and the repository keeps showing "no upstream". This is the closest relative of the original bug: same precondition (no `origin/<branch>`), same harm.

Validate: a `GitScenarios.ClonedRepository` test with a local-only branch and one commit, run `CommitSyncRepositoryCommand`, assert on `refs/heads/<branch>` in `origin.git` and on `branch.<name>.merge`. Check whether the UI offers Sync Commits for such a repository at all (`GroupHasSyncCommits` excludes the no-upstream badge from the highlight, but the menu item may still run).

Suggested fix: with no upstream, do not push from commit sync (Push already does the first push with `-u`), or push with `-u` and say so in the confirmation.

### 3. Return to Default reports success when a branch delete failed (CONFIRMED by reading, Should)

`ReturnToDefaultBranchCommand` logs a warning and carries on when the remote branch delete fails and when the local branch delete fails; the response is `Success = true` with no field for either failure. A user who ticked "delete remote branch" is told the operation worked while the branch is still on the remote. This breaks the "always report an outcome" rule in `AGENTS.md` ("Failures, cancellation and waiting").

Validate: a test with a stub `IGitService` whose `DeleteBranchAsync` fails, asserting the response carries the failure.

Suggested fix: add `RemoteBranchDeleteError` / `LocalBranchDeleteError` to `ReturnToDefaultBranchResponse` and show them as warnings in the result.

### 4. Create Branch silently switches to an existing branch (CONFIRMED by reading, Should)

`GitService.CreateBranchAsync`: when `git checkout -b <new> --no-track <base>` fails for any reason and a local branch of that name exists, it checks that branch out and returns success. The requested base is ignored and the caller is not told. A caller such as Prepare Workspace then continues (dependency update, commit, push) on a branch that may be old or based on something else.

Validate: a test that creates `feature/x` at an old commit, calls `CreateBranchAsync("feature/x", "main")` and asserts what is reported. Then read what Prepare Workspace does with that result.

Suggested fix: return an "already existed" flag in `CreateBranchResponse` so the caller can report it, and take this path only when the failure really is "branch already exists".

### 5. Leftovers of the Undo fix (CONFIRMED, Optional)

- `IGitService.ResetToRemoteAsync` still has a `bearerToken` parameter it no longer uses, `WorkspaceUndoPushHandler` still decrypts and sends the connector token with every `UndoPush`, and `GitRemoteAuthTests.EveryRemoteGitServiceMethodTakesAnExplicitBearerToken` still lists the method as a remote one. Remove all three together.
- `UndoPushModal.razor` says commits "will be reset back to `origin`", which is not what happens for a never-pushed branch. Its checkbox also lacks `form-check-input-gray`.

### 6. Interpolated git command lines in `GitService` (CONFIRMED, Optional)

`CheckoutBranchAsync`, `CreateBranchAsync` and `DeleteBranchAsync` build `checkout {branch}`, `branch -D {name}`, `push origin --delete {name}` and `update-ref -d {ref}` by interpolation, against rule 2 of "Worker: input, processes and git" in `AGENTS.md`. Branch names reach these from the App. Move them to the `IReadOnlyList<string>` overload of `GitProcessRunner.RunAsync` when one of them is next touched.

## Checked and found in order

- `PushRepositoryCommand` and `SetUpstreamBranchCommand`: push only when the user asked to push or set the upstream.
- `DeleteBranchAsync` (remote): refuses the default branch, uses `--force-with-lease` when a tip is supplied, treats "already deleted" as success.
- `DeleteBranchAsync` (local, not forced): `-D` only after an explicit ancestor check against HEAD or the default branch.
- `UpdateBranchFromDefaultCommand`: fetch and merge, no push.
- Git Changes discard: `git clean` and `git restore` only on validated paths or the whole repository as asked.
- Worktree create and remove: no `--force` unless the request carries the explicit authorization.

## Order

1. Validate items 1 and 2 (two Worker tests and a manual check). Fix whichever reproduces, with the test as its regression test.
2. Sweep the areas listed under "Not reviewed" for the same pattern and add what is found here.
3. Items 3 and 4.
4. Items 5 and 6 with the next change in those files.
