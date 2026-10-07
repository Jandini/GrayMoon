# Feedback Implementation - Live Document

Started: 2026-10-08
Source: [README.md](README.md)

This document tracks the analysis, plan and progress for the three feedback items. It is updated as work happens.

## Effort ranking (largest first)

| Rank | Item | Effort | Why |
|---:|---|---|---|
| 1 | NEW FEATURE - Feature removal blocker diagnostics | Large | New Worker abstraction + Windows Restart Manager P/Invoke + process working-directory scan, new Worker command, failure classification, App flow changes, Application DTOs, Remove Feature modal UX (Retry / Refresh blockers / Cancel), Windows integration tests |
| 2 | IMPROVEMENT - New Pull Request initialization performance | Medium | Bulk persisted-branch query, modal rewiring off `RefreshBranches`, reviewer de-duplication by owner + short cache, optional background freshness, tests |
| 3 | BUG - Git Changes directory rendering | Small | Root cause found; fix in tree builder + regression tests |

Order of work: 1 -> (commit/test by user) -> 2 -> 3.

---

## 1. NEW FEATURE - Feature removal blocker diagnostics

Status: **implemented - awaiting user commit / manual test** (2026-10-08)

### Analysis

- Remove Feature: `WorkspaceFeatureOperations.RemoveFeatureCoreAsync` (App) sends `RemoveGitWorktree` per repository.
- Worker: `RemoveGitWorktreeCommand` -> `GitWorktreeService.RemoveWorktreeAsync`.
- `git worktree remove` on Windows deletes the work tree first and **unregisters the worktree even when the delete fails**
  (git continues to `delete_git_dir` after `delete_git_work_tree` fails). So the dominant "in use" outcome today is:
  - Worker returns `Success = true` with `ResidueRemaining = true` and the message
    "Some files could not be deleted. They may still be open in another program." (or "The worktree folder could not be removed." when
    only directories remain - the typical result of a process whose current directory is inside the worktree, such as a shell or Claude).
  - The Feature is then fully removed from the database and the modal shows "Feature removed with warnings" with leftover-file lines.
- The less common path is a real failure while the worktree is still registered (`GitFailed`), where the Feature stays and goes to `NeedsRepair`.
- Neither path tells the user what is holding the folder.

### Design

Worker (owns all process inspection):

- `IFileLockInspector` (platform neutral) -> `FileLockInspectionResult` (processes, `MayBeIncomplete`, diagnostic) of `BlockingProcessInfo`.
- `WindowsFileLockInspector`:
  - Layer 1: Restart Manager (`RmStartSession` / `RmRegisterResources` / `RmGetList` / `RmEndSession`) over the files still under the path (bounded file count, batched registration).
  - Layer 2: process current-working-directory scan (PEB `ProcessParameters.CurrentDirectory`), because a shell or AI tool sitting in the
    folder keeps the directory itself in use but holds no file open, so Restart Manager cannot see it.
- `UnsupportedFileLockInspector` for non-Windows (empty result, `MayBeIncomplete = true`).
- `WorktreeRemovalFailureClassifier`: GitRefusal / UncommittedChanges / WorktreeLocked / PathInUse / AccessDenied / Unknown. Inspection only for PathInUse and AccessDenied, and for residue left after removal.
- `RemoveGitWorktreeCommand` enriches its response (`failureKind`, `blockingProcesses`, `blockersMayBeIncomplete`) only on the failure / residue path; a clean removal does no inspection. Inspection failure never hides the original removal result.
- New command `InspectPathLocks` (read pool) for "Refresh blockers".

App / Application:

- `RemoveFeatureBlockingProcess` DTO; `RemoveFeatureRepositoryReport` gains blockers + residue retry target.
- Failed remove: `OperationResult.RemoveFeatureBlockers` carries per-repository blockers.
- `IWorkspaceFeatureOperations.InspectRemoveFeatureBlockersAsync` (refresh) and `RetryRemoveFeatureResidueAsync` (retry leftover cleanup for an already-removed Feature, re-using the Worker's guarded residue cleanup).

UX (Remove Feature modal):

- "Feature is still in use" callout with the process list (name, PID, executable path, service name, reason).
- Actions: Retry, Refresh blockers, Cancel/Done. No kill action.

### Progress

- [x] Worker models + abstraction (`Models/FileLockInspectionResult.cs`, `Abstractions/IFileLockInspector.cs`)
- [x] Windows Restart Manager inspector (`Platform/Windows/WindowsRestartManager.cs`, `Services/WindowsFileLockInspector.cs`)
- [x] Working-directory scan (`Platform/Windows/WindowsProcessInspector.cs`, x64 and WOW64 PEB layouts)
- [x] `UnsupportedFileLockInspector` for non-Windows
- [x] Failure classifier (`Services/WorktreeRemovalFailureClassifier.cs`)
- [x] RemoveGitWorktree enrichment (`failureKind`, `blockingProcesses`, `blockersMayBeIncomplete`, `blockersDiagnostic`)
- [x] `InspectPathLocks` command (read pool) + DI / dispatcher / job factory / hub constant
- [x] Application DTOs (`RemoveFeatureBlockingProcess`, `RemoveFeatureRepositoryBlockers`, `RemoveFeatureResidueTarget`, report + `OperationResult` fields)
- [x] App flow: failed remove returns blockers; leftover report carries blockers + retry target; `InspectRemoveFeatureBlockersAsync`; `RetryRemoveFeatureResidueAsync`
- [x] Modal UX: "Feature is still in use" callout, per-repository process list (`BlockingProcessList.razor`), Retry / Refresh blockers / Cancel; leftover report gets Refresh blockers / Retry / Done
- [x] Tests
- [x] Build (0 warnings, 0 errors) + full test run: Common 261 passed, Worker 561 passed (1 pre-existing skip), App 1153 passed

### Tests added

| Project | File | Covers |
|---|---|---|
| Worker | `WorktreeRemovalFailureClassifierTests` | Classification of real Git messages; only PathInUse / AccessDenied warrant inspection |
| Worker | `RemoveWorktreeBlockerDiagnosticsTests` | Real git: clean removal and logical refusals (locked, dirty) never inspect; open file -> residue -> inspection + blockers returned; inspector exception keeps the removal outcome; `InspectPathLocks` validation and ordering |
| Worker | `WindowsFileLockInspectorTests` | Windows integration: helper PowerShell holding a file open is found via Restart Manager and disappears after exit; helper `cmd` with current directory inside the folder is found via the working-directory scan and disappears after exit; own process never reported; caps |
| App | `RemoveFeatureReportTests` (+8) | In-use failure returns blockers and keeps original error + NeedsRepair; no blockers for unrelated failure; clean remove never calls `InspectPathLocks`; leftover report carries blockers + retry target; retry clears residue / keeps it while in use; refresh maps results and handles an old Worker |
| App | `RemoveFeatureBlockerUiTests` | Display text, ordering / dedupe, empty-list fallback, ASCII hyphens, report warning lines, refresh merge |

### Notes / decisions

- Inspection happens inside the Worker's `RemoveGitWorktree` response (no extra round trip, data is fresh at failure time); `InspectPathLocks` exists only for "Refresh blockers".
- Restart Manager only accepts files, so the inspector registers the files still under the folder (max 4000, batches of 500). After a failed delete, the remaining files are mostly exactly the locked ones, so this is cheap.
- Layer 2 (current-directory scan) was implemented in the first version because the most common blocker named in the feedback (Claude / a terminal sitting in the Feature) holds the folder without any open file, which Restart Manager cannot see.
- The worktree is usually already unregistered when files are left behind, so the Feature is removed and the leftovers are reported. "Retry" for leftovers re-sends `RemoveGitWorktree`; the Worker sees the worktree is gone and runs the same guarded residue cleanup. The Workspace-role root worktree never gets a retry target (GrayMoon never deletes its leftovers itself).
- No kill / close action, no persistence, nothing logged beyond counts and the path at Information level.
- Backward compatible: an old App ignores the new response fields; a new App with an old Worker gets no blockers (plain error as before) and "Refresh blockers" reports that the lookup is unavailable.

### Manual test checklist

1. Create a Feature, open a terminal (or Claude) with its current folder inside one repository worktree, Remove Feature -> report lists the shell / Claude with "Its current folder is inside this Feature".
2. Close it, select Retry -> leftovers deleted, dialog closes when nothing else needs acknowledging.
3. Open a file from the worktree in an editor that locks it, Remove Feature -> process listed as "Has files open in this Feature".
4. Refresh blockers after closing a program -> it disappears from the list.
5. Clean Feature -> removal unchanged, no blocker UI.

---

## 2. IMPROVEMENT - New Pull Request initialization performance

Status: not started (analysis done)

### Analysis

- `NewPullRequestModal.LoadOneRepoBranchesAsync` calls `BranchOperations.RefreshBranchesAsync` (Worker fetch incl. tags, persist, `WorkspaceSynced`) and then `GetBranchesAsync` per repository, bounded by `MaxParallelOperations`.
- `LoadReviewersAsync` issues 2 GitHub calls per target repository (users + teams), all on modal open.
- `CheckUnpushedCommitsAsync` already runs independently and does not gate branch selectors.

### Plan

- Bulk `GetBranchesForRepositoriesAsync` (one DbContext, bulk queries, no Worker, no persistence, no broadcast).
- Modal uses the bulk read for first paint; optional explicit "Refresh" in Branch tab using refresh only for selected repos.
- Reviewers: group by GitHub owner, one users + teams request per owner, short memory cache; lazy load when Reviewers is opened.
- Timings logged.
- Tests: projection, parent/default/head rules, no Worker calls, bounded queries for 100 repos.

---

## 3. BUG - Git Changes directory rendering

Status: not started (root cause found)

### Root cause

`git status --porcelain=v2 --untracked-files=all` does not descend into a nested Git repository that is not ignored; it reports it as one
untracked entry **with a trailing slash**, e.g. `? GrayMoon.Release/`. `GitChangesTreeBuilder.AppendLevel` splits the path on `/` with
`RemoveEmptyEntries`, so `GrayMoon.Release/` becomes a single segment and is rendered as a File row (diff / stage / discard actions offered).

Reproduced:

```text
? .claude/x/s.json
? Child/          <- nested repository, not ignored
? root.txt
```

In the current workspace these folders are now listed in the root `.gitignore`, so they only appear when the ignore entry is missing.

### Plan

- Model: carry an explicit "is directory" flag for entries whose path ends with `/` (nested repository / directory entry).
- Tree builder: render such entries as Folder-kind nodes (repository-directory style) and never with file actions; never let a file row replace a folder row of the same name.
- Tests: tree-building cases from the feedback + integration test with a temporary workspace repo containing a tracked root file, nested tracked file, ignored child repo and a non-ignored child repo.
