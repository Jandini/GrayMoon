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

Order of work: 1 -> 2 -> 3. Each item is committed and pushed to `feedback` when done; the user tests each Desktop Release build.

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

Status: **implemented and committed** (2026-10-08)

### Analysis

- `NewPullRequestModal.LoadOneRepoBranchesAsync` calls `BranchOperations.RefreshBranchesAsync` (Worker fetch incl. tags, persist, `WorkspaceSynced`) and then `GetBranchesAsync` per repository, bounded by `MaxParallelOperations`.
- `LoadReviewersAsync` issues 2 GitHub calls per target repository (users + teams), all on modal open.
- `CheckUnpushedCommitsAsync` already runs independently and does not gate branch selectors.

### What was done

- `IWorkspaceBranchOperations.GetBranchesForRepositoriesAsync`: one fresh DbContext and a fixed number of queries (links, branch rows, Feature context info, context states, Feature repositories) regardless of repository count. No Worker, no remote access, no persistence, no `WorkspaceSynced`. The single-repository `GetBranchesAsync` now shares the same snapshot builder, so both reads mean the same thing.
- `NewPullRequestModal` first paint uses the bulk read. Selection rules are unchanged (Feature parent -> default -> another remote branch, never the head branch), now in `NewPullRequestTargetBranch.Resolve`.
- Freshness is off the critical path:
  - Repositories with no persisted remote branches (never synced) are refreshed in the background; they show the default branch (or a spinner when nothing is known) meanwhile.
  - A new **Refresh** link in the branch panel fetches every target from the remote on demand and keeps the user's selection when it is still valid.
- Reviewers: loaded only when the Review tab is first opened (they are optional). `IPullRequestService.GetReviewerCandidatesAsync` resolves all repositories in one query, asks each distinct GitHub repository once, and caches answers for 5 minutes (failures are not cached).
- Unpushed check unchanged (already a single bulk DB read that never gated the selectors).
- Timings logged: cached first paint (count, ms, how many need a background refresh), background / explicit refresh (count, ms, failures), reviewers (repositories, distinct GitHub repositories, cache hits, ms).

### Tests added

- `NewPullRequestBranchLoadingTests`: selection rules (parent, default, head never base, no valid base, no persisted remotes, keep / drop selection after refresh); bulk read correctness; bulk equals single-repository read; repositories outside the workspace skipped; 100 repositories with no Worker call, no broadcast and the same SQL query count as 3 repositories (N+1 guard via an EF command interceptor).

### Notes

- The explicit Refresh still uses the existing `RefreshBranches` path (it emits `WorkspaceSynced` per repository). That only runs when the user asks for it or for never-synced repositories, never for the whole workspace on open.
- Full run: Common 261, Worker 561 (+1 pre-existing skip), App 1164 - all passed; build 0 warnings.

### Manual test checklist

1. Open New Pull Request on a 20+ repository workspace: target selectors appear immediately (no "Loading..." wave).
2. Branch tab -> Refresh: spinner on the link, selectors stay usable, a manually chosen target is kept.
3. Review tab: reviewers load on first open; close and reopen the dialog within 5 minutes - reviewers appear without GitHub calls (see "cache hits" in the App log).

---

## 3. BUG - Git Changes directory rendering

Status: **implemented and committed** (2026-10-08)

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

### What was done

- `GitChangesTreeBuilder` decides node kind from the entry itself: a path ending in `/` (`IsDirectoryPath`) becomes an explicit directory row (`GitChangesTreeRow.IsDirectoryEntry`, Folder kind, no children, `FilePath` = the entry path). It is never inferred from "has children".
- If the same name already exists as a parent folder of other entries, the folder row wins; a directory entry never becomes a file row.
- `GitChangesTree`: directory rows show a folder icon, an "untracked folder" hint with a tooltip (usually a nested Git repository; add it to `.gitignore` to hide it), no status letter, no diff on click and no Stage / Undo actions (staging would embed the nested repository, Undo would delete it). Copy path still copies `Name/`.
- Ignored child repositories were already absent (Git does not report them); nothing reintroduces them.
- No schema or Worker change: the trailing slash is preserved end to end, so the fix lives at the tree / model layer.

### Tests added

- `GitChangesDirectoryEntryTests`: nested file creates parent folders; `GrayMoon.Release/` and `GrayMoon.wiki/` render as directories, not files; a parent folder is not replaced by a same-named entry; tracked files beside repository directories (folders first, then files); nested directory entry; copy-path round trip; trailing-slash detection; integration test with a real temporary Workspace repository (tracked root file, tracked nested `.claude` file, ignored child repo, non-ignored child repo) run through real `git status` and `GitPorcelainV2Parser`.

### Notes

- Unchanged (out of scope): folder-level Stage on a parent folder passes the folder as a pathspec, so it would still stage a nested repository inside it as Git does on the command line.
- Full run: Common 261, Worker 561 (+1 pre-existing skip), App 1175 - all passed; build 0 warnings.

### Manual test checklist

1. Remove `GrayMoon.Release/` from the Workspace `.gitignore`: Changes shows `GrayMoon.Release` as a folder with "untracked folder", no actions, nothing opens on click.
2. Put the ignore line back: the entry disappears.
3. Normal file changes in the Workspace and in regular repositories behave exactly as before.
