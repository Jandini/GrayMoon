# Operations that do something other than what was asked

Found 2026-11-21 while fixing Undo Push Commits on a never-pushed branch (it pushed the branch and then reset to the ref the push had created). That one is fixed: `GitService.ResetToRemoteAsync` now resets to the merge base with the no-upstream compare ref and never contacts the remote (`GitServiceUndoUnpushedCommitsTests`).

The pattern looked for: a command that, when its precondition is missing or a step fails, silently runs a different action (push, delete, switch, force) or reports success for a step that did not happen. Scope of the sweep: every Worker command and `GitService` method that pushes, resets, deletes, cleans or force-anything, and the App callers of those commands. Not reviewed in depth: the App orchestrators (Prepare Workspace, Update and Push), Feature removal, and GrayMoon.Desktop.

Status words: CONFIRMED means read in the code path end to end. PLAUSIBLE means it follows from the code but was not reproduced; confirm with a failing test before fixing.

## Open items

### 1. "Pull" also pushes (CONFIRMED)

`CommitSyncRepositoryCommand` pulls and then, unless the request is `pullOnly` (read only repositories), pushes whenever outgoing commits remain (`src/GrayMoon.Worker/Commands/CommitSyncRepositoryCommand.cs`, the `git.PushAsync` call after `IntegrateAsync`).

The same command serves three user actions in `WorkspaceRepositories.CommitSync.cs`: the header **Pull** button (`OnPullClickAsync`), a click on a Commits badge with incoming commits (`OnPullBadgeClickAsync`), and the **Sync Commits** menu. For Sync Commits a push is the meaning of the action. For the two Pull entry points the label says pull only, yet a repository with both incoming and outgoing commits is merged and then pushed, which publishes local commits the user did not ask to publish.

Suggested fix: send `pullOnly`-like behaviour (a new "do not push" flag, without the fast-forward-only and local-commit refusal of the read only path) from the Pull entry points, or rename them. `docs/architecture/01-product-model-and-user-workflows.md` section 11 and `05-user-capability-reference.md` section 12 do not say that pull pushes; fix the document in the same change.

### 2. Sync Commits on a never-pushed branch publishes it without an upstream (PLAUSIBLE)

Same command. On a branch with no upstream, `GetCommitCountsAsync` reports outgoing commits against the divergence base or default branch and no incoming, so `IntegrateAsync` returns success and the command pushes with `setTracking: false`. That creates the branch on the remote but leaves the local branch without an upstream. Expected consequences, not reproduced: the new remote branch appears although the user only asked to sync, and the repository keeps showing "no upstream".

To confirm: a `GitScenarios.ClonedRepository` test with a local-only branch and one commit, run `CommitSyncRepositoryCommand`, assert on `refs/heads/<branch>` in `origin.git` and on `branch.<name>.merge`.

Suggested fix: with no upstream, do not push from commit sync (Push already handles the first push with `-u`), or push with `-u` and say so in the confirmation.

### 3. Return to Default reports success when a branch delete failed (CONFIRMED)

`ReturnToDefaultBranchCommand` logs a warning and carries on when the remote branch delete fails and when the local branch delete fails; the response is `Success = true` with no field for either failure. A user who ticked "delete remote branch" is told the operation worked while the branch is still on the remote. This is the "always report an outcome" rule in `AGENTS.md` ("Failures, cancellation and waiting").

Suggested fix: add `RemoteBranchDeleteError` / `LocalBranchDeleteError` to `ReturnToDefaultBranchResponse` and show them as warnings in the result.

### 4. Create Branch silently switches to an existing branch (CONFIRMED)

`GitService.CreateBranchAsync`: when `git checkout -b <new> --no-track <base>` fails for any reason and a local branch of that name exists, it checks that branch out and returns success. The requested base is ignored and the caller is not told. A caller such as Prepare Workspace then continues (dependency update, commit, push) on a branch that may be old or based on something else.

Suggested fix: return an "already existed" flag in `CreateBranchResponse` so the caller can report it, and take this path only when the failure really is "branch already exists".

### 5. Leftovers of the Undo fix (CONFIRMED, cleanup)

- `IGitService.ResetToRemoteAsync` still has a `bearerToken` parameter it no longer uses, `WorkspaceUndoPushHandler` still decrypts and sends the connector token with every `UndoPush`, and `GitRemoteAuthTests.EveryRemoteGitServiceMethodTakesAnExplicitBearerToken` still lists the method as a remote one. Remove all three together.
- `UndoPushModal.razor` says commits "will be reset back to `origin`", which is not what happens for a never-pushed branch. Its checkbox also lacks `form-check-input-gray`.

### 6. Interpolated git command lines in `GitService` (CONFIRMED, known rule)

`CheckoutBranchAsync`, `CreateBranchAsync` and `DeleteBranchAsync` build `checkout {branch}`, `branch -D {name}`, `push origin --delete {name}` and `update-ref -d {ref}` by interpolation, against rule 2 of "Worker: input, processes and git" in `AGENTS.md`. Branch names reach these from the App. Move them to the `IReadOnlyList<string>` overload of `GitProcessRunner.RunAsync` when one of them is next touched.

## Checked and found in order

- `PushRepositoryCommand` and `SetUpstreamBranchCommand`: push only when the user asked to push or set the upstream.
- `DeleteBranchAsync` (remote): refuses the default branch, uses `--force-with-lease` when a tip is supplied, treats "already deleted" as success.
- `DeleteBranchAsync` (local, not forced): `-D` only after an explicit ancestor check against HEAD or the default branch.
- `UpdateBranchFromDefaultCommand`: fetch and merge, no push.
- Git Changes discard: `git clean` and `git restore` only on validated paths or the whole repository as asked.
- Worktree create and remove: no `--force` unless the request carries the explicit authorization.
