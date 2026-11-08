# Commit All / Commit Staged: repositories silently not committed

Status: Phase 0 and Phase 1 built (in testing), plus the one-line run summary from Phase 4. Phases 2 and 3 not built. Automatic retries are deliberately left out for now so they cannot hide a problem. Written 2026-11-08 after reading the whole path (Changes page -> App -> SignalR -> Worker `git add` / `git commit`) and one local log.

## Symptom

With many repositories in a Workspace, Commit All (also seen around Commit Staged, and when the commit message is written by AI first) sometimes leaves one or more repositories uncommitted. Running the commit again for those repositories works.

## Confirmed root cause: one `DbContext` used by parallel commits

1. `CommitWorkspaceCoreAsync` (`WorkspaceGitChanges.MultiRepo.cs`) fans out up to `MaxParallelRepositoryOperations` (8) repositories with `Task.WhenAll`.
2. Every branch calls the page's single scoped `IWorkspaceGitChangesOperations.CommitAsync`.
3. `WorkspaceGitChangesOperations.WithResolvedRepo` calls `workspaceRepository.GetByIdAsync(workspaceId)` for every call. `WorkspaceRepository` is scoped and owns one `AppDbContext`, which is not thread safe. The query also loads the whole workspace graph (repositories, connector, pull request) once per repository.
4. Two branches reaching the query together throw `InvalidOperationException: A second operation was started on this context instance...`. The page reports "Unexpected error - see logs." and that repository never reaches the Worker.

Stage All / Unstage All (`BulkSectionActionAsync`) uses the same call, and its `catch` only logs. A repository that fails to stage is left unstaged and Commit Staged then skips it (`StagedCount == 0`). `MultiSelect` and `Discard` use the same fan-out pattern.

### Evidence (local log `desktop-20261024.log`, 00:46:52)

- `Edx1.Mapi` and `Edx1.IO` logged "Workspace commit requested" in the same millisecond.
- Next, EF "second operation" errors in `WithResolvedRepo` / `WorkspaceRepository.GetByIdAsync`.
- "Workspace commit failed" for `Edx1.IO` and `Edx1.MimeType`, no `CommitGitChanges` request sent for them.
- At 00:47:50 the same two repositories were committed again by hand.

### Second environment confirms it (Edx1 workspace, 27 repositories)

Logs read 2026-10-25 to 2026-11-08 (App, Worker, Desktop). 133 commit runs, 334 "Workspace commit requested", 330 "Sent RequestCommand: CommitGitChanges". The gap of 4 equals the 4 dropped repositories, and the Worker logged "Git commit starting" == "completed" on every day with 0 "Git commit failed". Same three frames every time (`CommitWorkspaceCoreAsync` -> `WithResolvedRepo` -> `WorkspaceRepository.GetByIdAsync`). No other caller, no `Bulk section` hit.

| Time | Op | Requested / sent | Dropped | Hand retry |
|---|---|---|---|---|
| 2026-10-25 (rotated log) | - | - | Edx1.FileSystem.Windows, Edx1.File.Video, Edx1.Format | - |
| 2026-11-06 19:14:20 | b4e7314d | 26 / 25 | Edx1.File.Email | 27 s later |
| 2026-11-07 08:30:19 | c7ea36be | 27 / 25 | Edx1.File.Email, Edx1.FileSystem | 13 min later |
| 2026-11-08 20:47:57 | 6c4b4c5e | 18 / 17 | Edx1.FileSystem.Windows | 41 s later |

What the data says:

- **Every gap is in a large run (18 to 27 repositories).** The 130 runs with no gap had 1 to 7 repositories. The chance of two parallel branches hitting `GetByIdAsync` together grows with fan-out size, so this bug mostly hits whole-workspace commits.
- **Drops happen in the first fan-out burst.** The failure is logged 5 to 120 ms after "Workspace commit requested", before the Worker is called.
- **The user sees it only at the end of the job.** The warning line is logged 12 to 15 s later. The repository is simply missing from the "Committed in N" count.
- **The three failing runs all used an AI-written message, but AI does not look causal.** The AI step ended 3 to 4 s before the fan-out started, and the single-repository retries also used AI and succeeded. The earlier inference that the AI step makes the burst worse is weakened; large fan-out size is the better explanation. The AI link is not needed to reproduce the bug.
- **Nothing else explains a missed repository** in these logs: 0 hits for `index.lock`, "Unable to create", `StageFailed`, `NothingStaged`, `CommitFailed`, "Commit All staging still dirty", Worker timeout on `CommitGitChanges`, dirty submodule. Findings C, D and E below are therefore not observed in practice so far (they stay as hardening, lower priority).
- Environment: Windows 10.0.26300, git 2.55.0, GrayMoon App 0.1.1-main.117 and 0.2.1-main.4.
- Side note: a Git Changes status scan of `Edx1.Lab` timed out after 240 s on 2026-11-07 (not a commit). Possibly worth its own look.

Reproduction target for the regression test: a fan-out of at least 18 repositories with `MaxParallelRepositoryOperations = 8`.

## Other findings (lower confidence)

| # | Finding | Effect | Confidence |
|---|---|---|---|
| A | Targets and mode are frozen before the AI step (up to 600 s) and before the default-branch modal; counts come from `_view` and are not re-checked. | A repository that became dirty is skipped. A repository that was cleaned fails with `NothingStaged`. | Confirmed by reading |
| B | Failures are only a toast; the message is cleared if any repository succeeded. | The message needed to retry is lost; with AI it must be regenerated. | Confirmed |
| C | `git add --all` and `git commit` have no retry. Only remote operations use `GitRetryClassifier`. The in-process lock does not stop an IDE or git extension holding `index.lock`. | Whole repository fails with `StageFailed`. | Code-confirmed, no log hit |
| D | `StageAllForCommitAsync` retries 3 times with no delay, and any porcelain worktree entry counts as unsettled (possibly a dirty submodule). | Churning files or a dirty submodule gives `StageFailed`. | Needs repro |
| E | Git subprocess default timeout is 60 s; the App's 240 s command timeout includes queueing behind the repository lock. | Slow hooks or a large `git add` fail. | Needs repro |
| F | `post-commit` hook runs `curl --max-time 2` to the Worker while `git commit` still holds the repository lock. | Possible 2 s stall per commit. | Unverified |
| G | The push step calls `RequireSelectedContextId()` again instead of reusing the captured context. | Mismatch only if the context changes mid-job. | Hygiene |

## Fix plan

### Phase 0: stop the data loss
1. Resolve the repository once, outside the fan-out (the page already has root, workspace name, repository name, workspace repository name from `ResolveRepositoryAsync`) and pass those values to the commit and stage calls.
2. If a lookup must remain, use `IDbContextFactory` with a short-lived context and a minimal query; never the scoped `WorkspaceRepository` from parallel code.
3. Audit every `Task.WhenAll` / `SemaphoreSlim` fan-out in `GrayMoon.App` for non-thread-safe scoped services (`MultiSelect`, `Discard`, `BulkSectionAction`).
4. Regression test: N parallel `CommitAsync` calls through `WorkspaceGitChangesOperations` with a real `AppDbContext` and a fake worker client (fails today). Pattern: `BranchRowSaveRaceTests`.

### Phase 1: failures cannot be missed
1. One result per repository: Committed / Skipped (no changes) / Failed(reason), with a summary. Stage All / Unstage All report failures the same way.
2. Keep the commit message unless every repository succeeded; add "Retry failed".
3. After the job, refresh status and show "N repositories still have changes" if any target is still dirty.

### Phase 2: stale targets
1. Re-evaluate targets when the commit actually starts (after AI and after the modal). For Commit All, do not prefilter on cached counts; "nothing to commit" is a skip.
2. Warn if the targets differ from what the AI described.

### Phase 3: Worker hardening
1. Retry `git add` / `git commit` on `index.lock` contention with bounded backoff (`GitRetryClassifier`), inside the existing write lock.
2. Delay between `git add --all` verify retries; report dirty submodules and similar as a specific error.
3. Own timeout tier for `add` / `commit`; start the App's 240 s clock when the Worker starts executing.
4. Check the `post-commit` hook endpoint behaviour under the lock (F).

### Phase 4: observability
Correlation id per commit run and one end-of-run log line: `requested=N committed=N skipped=N failed=N (names, reasons)`.

## Order
Phase 0 steps 1 and 4 first, then Phase 1, then Phases 2 and 3 after a repro of D and E.

## Priority after the second environment

Phase 0 and Phase 1 first: the bug is confirmed on two machines, with 7 occurrences in about two weeks. Phase 2 next: the stale-target problem is real but unobserved. Phase 3 (`index.lock` retry, timeouts) is hardening; no log shows it happening. Phase 4 is needed regardless, because the only trace of a drop today is an error buried in the log and a warning toast.

## Prompt for analysing logs in another environment

```text
I am investigating a bug in GrayMoon where "Commit All" or "Commit Staged" across many repositories sometimes leaves one or more repositories uncommitted. A known cause is an EF Core DbContext shared by parallel commits in the App. I need to know whether it, or something else, happened in THIS environment. Read-only investigation: do not change, move or delete anything, and do not run git commit/push.

1. Find the logs. GrayMoon Desktop logs are normally in %LOCALAPPDATA%\GrayMoon\Logs (desktop-YYYYMMDD.log, App output is prefixed "[GrayMoon.App stdout]"). Also look for App and Worker logs elsewhere (for example under ~/.graymoon or the Worker's log folder). List every log file with size and date range. If the Worker's own log is not present, say so.

2. Search all log files (use rg, case-insensitive) for these signatures and report counts per file and per day:
   - "A second operation was started on this context"
   - "Workspace commit failed"
   - "Workspace commit requested"  (count per run)
   - "Sent RequestCommand: .*CommitGitChanges"
   - "Bulk section"  (stage/unstage failures)
   - "Git commit starting|Git commit failed|Git commit completed"
   - "Commit All staging still dirty|Commit All staging settled"
   - "StageFailed|NothingStaged|CommitFailed"
   - "index.lock|Unable to create"
   - "Worker command CommitGitChanges .* timed out|Worker command timed out"
   - "rejected stale or duplicate context snapshot"

3. For each commit run (group "Workspace commit requested" lines that are within a few seconds of each other), build a table: time, mode (All/Staged), repositories requested, how many got "Sent RequestCommand: CommitGitChanges", how many "Workspace commit failed", and which repositories failed. Flag every run where requested > sent, and every repository that was committed again by hand within 5 minutes of a failure.

4. For every "second operation" hit, quote the exact lines around it (repository names, timestamps, stack frames mentioning WithResolvedRepo, WorkspaceRepository.GetByIdAsync, CommitWorkspaceCoreAsync, BulkSectionAction or any other caller). If a different caller appears, tell me which one, because it means another fan-out has the same bug.

5. Check whether the failed runs used AI-generated messages: look near each run for AI generation lines (for example "AI generation", "Commit message by", "Writing the commit message"). Report, per failed run, whether AI was involved and how long the AI step took.

6. Report anything else that explains a repository not being committed: index.lock errors, timeouts, hook failures, "Nothing staged", dirty-submodule or "still had unstaged or untracked changes" messages.

7. Output: (a) a short verdict: same root cause / different cause / not reproduced in these logs; (b) the run table; (c) the raw evidence lines (trimmed to 300 characters each); (d) the environment details: GrayMoon version (App, Worker, Desktop), OS, git version (git --version), number of repositories in the affected Workspace, and whether an IDE (Cursor, VS Code, Visual Studio) was open on those repositories. Do not paste secrets or tokens; redact them.
```
