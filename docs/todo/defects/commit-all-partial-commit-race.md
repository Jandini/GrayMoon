# Commit All / Commit Staged: hardening that is not built

Status: the data-loss bug is fixed. Phases 0, 1 and 2 and the one-line run summary from Phase 4 are built (shared scoped `DbContext` no longer used by parallel commits; one result per repository; commit message kept when a repository fails; end-of-run summary; targets decided when the commit starts, see `WorkspaceCommitTargetPlanner`). Phase 3 is not built. Automatic retries are deliberately left out so they cannot hide a problem. Written 2026-11-08.

## Background (kept for context)

`CommitWorkspaceCoreAsync` fanned out up to 8 repositories with `Task.WhenAll`, and every branch reached one scoped `AppDbContext`, which is not thread safe. In large runs (18 to 27 repositories) a branch threw "A second operation was started on this context instance" before reaching the Worker, and that repository was silently not committed. Seen on two machines, 7 times in about two weeks. Every gap was in a large run; AI-written messages were not causal. The regression pattern is `BranchRowSaveRaceTests`.

## Still open

### Phase 3: Worker hardening (no log shows these happening; needs a repro first)

| # | Finding | Effect |
|---|---|---|
| C | `git add --all` and `git commit` have no retry. Only remote operations use `GitRetryClassifier`. The in-process lock does not stop an IDE or git extension holding `index.lock`. | Whole repository fails with `StageFailed`. |
| D | `StageAllForCommitAsync` retries 3 times with no delay, and any porcelain worktree entry counts as unsettled (possibly a dirty submodule). | Churning files or a dirty submodule gives `StageFailed`. |
| E | Git subprocess default timeout is 60 s; the App's 240 s command timeout includes queueing behind the repository lock. | Slow hooks or a large `git add` fail. |
| F | `post-commit` hook runs `curl --max-time 2` to the Worker while `git commit` still holds the repository lock. | Possible 2 s stall per commit (unverified). |

Fix sketch: retry `git add` / `git commit` on `index.lock` contention with bounded backoff (`GitRetryClassifier`) inside the existing write lock; delay between `git add --all` verify retries and report dirty submodules as a specific error; a separate timeout tier for `add` / `commit`, starting the App's 240 s clock when the Worker starts executing; check the `post-commit` hook endpoint under the lock.

### Other

- Audit remaining `Task.WhenAll` / `SemaphoreSlim` fan-outs in `GrayMoon.App` (`MultiSelect`, `Discard`, `BulkSectionAction`) for non-thread-safe scoped services, if that was not completed with Phase 0.
- Phase 4 remainder: a correlation id per commit run.
- A Git Changes status scan of one repository timed out after 240 s on 2026-11-07 (not a commit). Possibly worth its own look.

## Order

Phase 3 only after a repro of D and E.
