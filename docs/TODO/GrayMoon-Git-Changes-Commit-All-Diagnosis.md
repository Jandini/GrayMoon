# GrayMoon Git Changes --- `Commit All` committed only one file

## Incident

On 2026-09-19, repository `Jandini/EDX1` produced two consecutive
commits with the same commit message:

-   `b00f1b9631356a5aa6e28e4cb8f85db58c61af4b` at 20:25:17 +05:30 ---
    **1 file**
-   `6f72bcbb620f24c72a04f1648b62f4318e89fbf9` at 20:26:02 +05:30 ---
    **30 files**

The second commit has the first commit as its direct parent. The file in
the first commit,
`tests/Edx1.Format.Mtf.Tests/MtfCompressionCorpusSurveyTests.cs`, also
appears in the second commit, which means it had additional working-tree
changes after the first commit's indexed version.

The reported UI action was **Commit All**, with no intentionally staged
file.

## Diagnosis

The Git implementation itself does not explain a one-file `Commit All`.

`GitCliRepositoryGitChangesService.CommitAsync()` behaves correctly:

``` csharp
if (request.StageAllFirst)
{
    await runner.RunAsync("git", ["add", "--all"], ...);
}

await runner.RunAsync("git", ["commit", "-F", "-"], ...);
```

When `StageAllFirst == true`, GrayMoon runs `git add --all` before
committing. Assuming Git succeeds, that cannot intentionally commit only
one already-staged file while leaving the other ordinary changes
unstaged.

The problem is higher in the Git Changes UI.

Current code derives the operation mode from the **workspace-wide staged
count**:

``` csharp
private int StagedRepositoryCount =>
    _view?.Repositories.Count(r => r.StagedCount > 0) ?? 0;

private string CommitButtonTitle =>
    StagedRepositoryCount > 0 ? "Commit Staged" : "Commit All";
```

and the click handler independently evaluates the same mutable state
again:

``` razor
@onclick="() => CommitWorkspaceAsync(
    StagedRepositoryCount > 0,
    PushAfterCommit)"
```

That boolean becomes `stagedOnly`.

Later:

``` csharp
GitChangesOperations.CommitAsync(
    ...,
    stageAllFirst: !stagedOnly,
    ...);
```

Therefore, **if GrayMoon believes even one repository has any staged
change when the click callback executes, it switches the entire
workspace commit into `Commit Staged` mode and does not run
`git add --all`.**

## Most likely sequence

The observed Git history is consistent with this sequence:

1.  EDX1 had many unstaged changes.
2.  `MtfCompressionCorpusSurveyTests.cs` had an indexed/staged version,
    whether from an earlier action, a transient/stale snapshot, or a
    watcher update not yet reflected in the visible button.
3.  The visible UI still appeared to the user as **Commit All**.
4.  Before or while the click event was handled, `_view` indicated
    `StagedRepositoryCount > 0`.
5.  The click callback evaluated `StagedRepositoryCount > 0` as `true`.
6.  `CommitWorkspaceAsync(stagedOnly: true, ...)` targeted repositories
    with staged changes.
7.  The Agent skipped `git add --all` because `StageAllFirst == false`.
8.  Git committed the one staged file as `b00f1b9`.
9.  The remaining working-tree changes stayed uncommitted.
10. The next commit staged/committed the remaining changes as `6f72bcb`.

The supplied log begins immediately after the first commit. At 20:25:18
GrayMoon is already refreshing repository state (`rev-parse`,
`symbolic-ref`, `status --porcelain=v2`). That timing is compatible with
the first commit completing at 20:25:17 and the UI/status pipeline
discovering what remained afterward.

## Root cause

### Primary design flaw

The button's **displayed command** and **executed command** are not a
captured, explicit operation.

The label is rendered from:

``` csharp
StagedRepositoryCount > 0
```

but the click handler calculates that condition again later.

Because `_view` is asynchronously updated by Git status/watcher
activity, the user can theoretically see **Commit All** while the
callback executes **Commit Staged**.

This is a TOCTOU-style UI race: the state used to describe the action is
not necessarily the state used to execute it.

### Secondary UX/design flaw

GrayMoon automatically changes the primary command from `Commit All` to
`Commit Staged` merely because staged content exists somewhere in the
workspace.

That makes the semantics fragile even without a race. A staged file can
silently change the scope of the commit operation. For a
destructive/state-changing command, the action selected by the user
should be explicit and stable.

## Recommended fix

Do not infer the requested commit operation inside the click callback
from mutable repository state.

Make **Commit All** and **Commit Staged** explicit commands. At minimum,
capture a stable operation value when rendering/choosing the command and
pass that exact value through the entire pipeline.

Preferred behavior:

-   **Commit All** always sends `StageAllFirst = true`.
-   **Commit Staged** always sends `StageAllFirst = false`.
-   Existing staged files may influence which action is offered or
    emphasized, but must not silently change the semantics of an action
    already presented as **Commit All**.
-   The operation should be represented by an enum/value such as
    `WorkspaceCommitMode.All` / `WorkspaceCommitMode.Staged`, rather
    than recomputing a boolean from `_view`.
-   Immediately before execution, a fresh Git status may be obtained for
    validation/display, but it must not mutate the user's selected
    commit mode.
-   Log the selected mode explicitly,
    e.g. `Workspace commit requested: Mode=All, Repository=EDX1, StageAllFirst=True`.

## Regression tests

Add tests covering:

1.  **Commit All with no staged files** → `StageAllFirst=true`.
2.  **Commit All with one staged + many unstaged files** → all changes
    are committed.
3.  **Commit Staged with one staged + many unstaged files** → only
    staged content is committed.
4.  `_view` changes between render and click → the command executed
    remains the command shown/selected by the user.
5.  A staged change in repository A must not unexpectedly alter the
    commit mode for repository B.
6.  Watcher/status refresh during click dispatch must not change
    `Commit All` into `Commit Staged`.
7.  Logging records the requested commit mode and `StageAllFirst` value
    so a future incident is diagnosable directly from logs.

## Confidence

**High confidence in the code-level defect; moderate-to-high confidence
that it caused this incident.**

The Git history proves that the first commit contained only one file and
the second, direct-child commit contained the remaining broad change
set. The Agent implementation proves that a true `Commit All` request
runs `git add --all`. Therefore the first operation almost certainly
reached the Agent with `StageAllFirst=false`, or otherwise was invoked
as staged-only.

The current UI code contains a concrete path for exactly that outcome:
it derives `stagedOnly` from mutable `_view` state at click time rather
than binding execution to the command presented to the user.

The supplied log does not include the actual `CommitGitChanges` request
or `git add` / `git commit` lines, so it cannot by itself prove the
exact value of `StageAllFirst` for the first commit. Adding explicit
commit-mode logging is important.
