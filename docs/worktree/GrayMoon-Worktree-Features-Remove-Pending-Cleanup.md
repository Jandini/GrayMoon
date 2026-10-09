# Remove Feature: leave leftovers behind, clean them up later

Status: implemented (2026-10-09). Section 4 lists the code.

Replaces `GrayMoon-Worktree-Features-Remove-Blocking-Processes.md` (lock inspection, Kill and remove, Refresh
blockers, Retry). That approach is removed entirely.

---

## 1. Goal

Remove Feature never asks the user about programs that hold Feature files open, and never ends a process. If files
are still in use when a Feature is removed, GrayMoon:

1. finishes the removal anyway (Git has already unregistered the worktrees; the Feature is gone from GrayMoon),
2. leaves a marker file in the leftover Feature folder that says the folder is pending deletion,
3. shows one plain **information** line (not a warning callout) in the Remove dialog,
4. deletes the folder later, silently, in the background. The user is never notified or asked.

Non-goals: finding out which program holds a file; retrying in the dialog; cleaning folders GrayMoon did not create.

---

## 2. What exists today and what goes away

Removal of one worktree (`GitWorktreeService.RemoveWorktreeAsync`) is: `git worktree remove`, then a guarded,
reparse-point-safe residue delete (`RemoveWorktreeResidueAsync`, retries 200..3200 ms per entry), then the empty
Feature root is removed. The Workspace-role root worktree is the Feature root itself (`features\<Feature>`), removed
last, with residue cleanup disabled (`featureRootPath = null`). Git unregisters a worktree even when it cannot delete
its folder, so an "in use" problem almost always shows up as **success with residue**, not as a failure.

Removed by this change (Worker, App, Application, tests, docs):

| Area | Removed |
|---|---|
| Worker commands | `InspectPathLocksCommand`, `TerminateBlockingProcessesCommand` (+ requests/responses, `WorkerHubMethods` constants, `ReadOnlyCommands` entry, `CommandJobFactory` / `CommandDispatcher` / DI registrations) |
| Worker lock scanning | `IFileLockInspector`, `WindowsHandleLockInspector`, `WindowsFileLockInspector`, `UnsupportedFileLockInspector`, `WindowsLockScanner`, `LockScanChildProcess`, `LockScanOutcome`, `FileLockInspectionResult`, `BlockingProcessRules`, `IProcessTerminator`, `ProcessTerminator`, `TerminateProcessOutcome`, `Platform/Windows/WindowsHandleTable`, `WindowsMappedFiles`, `WindowsPathNames`, `WindowsProcessInspector`, `WindowsRestartManager`, `BlockingProcessResponse` |
| Worker CLI / options | `inspect-locks` subcommand (`InspectLocksCommandHandler`, `WorkerCli`, `Program.cs`), `WorkerOptions.LockInspectionTimeoutSeconds` |
| `RemoveGitWorktreeCommand` | `IFileLockInspector` dependency, `AddBlockersAsync`, `BlockingProcesses` / `BlockersMayBeIncomplete` / `BlockersDiagnostic` on the response |
| `WorktreeRemovalFailureClassifier` | `WarrantsLockInspection` (keep `Classify` / `FailureKind` - still useful in the error text) |
| Application contracts | `InspectRemoveFeatureBlockersAsync`, `InspectFeatureBlockersAsync`, `TerminateFeatureBlockersAsync`, `TerminateLeftoverBlockersAsync`, `RetryRemoveFeatureResidueAsync`; `RemoveFeatureBlockingProcess`, `RemoveFeatureRepositoryBlockers`, `RemoveFeatureTerminateResult`, `RemoveFeatureResidueTarget`; `OperationResult.RemoveFeatureBlockers`; `BlockingProcesses` / `BlockersMayBeIncomplete` / `BlockersDiagnostic` / `ResidueTarget` on `RemoveFeatureRepositoryReport` |
| App | the matching code and wire classes in `WorkspaceFeatureOperations`; `BlockingProcessList.razor(.css)`, `KillOutcomeLines.razor`, `RemoveFeatureBlockerText.cs`; the preflight / in-use / Kill / Refresh blockers / Retry states in `RemoveFeatureModal.razor`; blocker bits in `WorkspaceFeatureSelector.razor` |
| Tests | `RemoveFeatureKillTests`, `RemoveFeatureBlockerUiTests`, `TerminateBlockingProcessesCommandTests`, `WindowsHandleLockInspectorTests`, `WindowsFileLockInspectorTests`, `RemoveWorktreeBlockerDiagnosticsTests`; blocker asserts in `RemoveFeatureReportTests` |
| Docs | `docs/worktree/GrayMoon-Worktree-Features-Remove-Blocking-Processes.md`, `docs/feedback/NEW-FEATURE-Feature-Removal-Blocking-Process-Diagnostics.md` (and its line in `docs/feedback/IMPLEMENTATION.md`), the lock/kill sentences in `CLAUDE.md` "Worktree Features", matching passages in `docs/architecture/` and the wiki |

No database schema change: none of the removed data was persisted.

---

## 3. Design

### 3.1 Unit of cleanup: the Feature root folder

Everything a Feature owns on disk lives under `{ManagedFeatureStorageRoot}\<Feature>` (`ManagedFeatureStorageRoot`
already ends in `\<Workspace>\features`). Leftovers are always inside that folder: residue in a Source worktree
(`features\<Feature>\<Repo>`) keeps the root non-empty, and a held root worktree leaves the root itself. So the
pending-deletion unit is always **the whole Feature root folder**. One marker, one decision, one delete.

Features on the legacy drive-root path (`WorkerPath.IsLegacyWindowsDriveRootGraymoonPath`) are never marked or
swept, matching the residue guard. A Feature name may contain `/`, which nests its folder (`features\team\login`); the
unit is still that Feature folder, and grouping folders it leaves empty are removed with it.

### 3.2 The marker file

Name: `GRAYMOON-PENDING-DELETE.md`, written at the top of the Feature root folder. Upper case so it sorts first and
stands out in Explorer, a terminal listing, or an AI agent's file listing.

Content (human and AI readable on top, machine-readable block at the bottom):

```markdown
# This folder is pending deletion

This folder belonged to the GrayMoon Feature "login-fix" in workspace "Shop". The Feature was removed on
2026-10-09 10:12 UTC, but some files were still in use, so the folder could not be deleted completely.

GrayMoon will delete this whole folder automatically later. Do not save work here: nothing in this folder is a Git
worktree any more, and anything left here will be deleted.

If you are an AI agent working in this folder: stop and tell the user this Feature was removed.

<!-- graymoon-pending-delete
version: 1
workspace: Shop
feature: login-fix
folder: C:\Users\me\GrayMoon\Shop\features\login-fix
markedUtc: 2026-10-09T10:12:31Z
-->
```

Copies: when a Source worktree folder (`features\<Feature>\<Repo>`) is also left behind, the same text is written
there too, so an agent whose working directory is the repository folder sees it. Only the root marker is
authoritative; copies are deleted with the tree and never drive a decision.

The marker is written by the Worker only (the App never touches the filesystem). Writing it never fails the
removal: if it cannot be written, the Worker logs a warning and the folder simply stays (same as today).

### 3.3 One Worker primitive: `CleanupFeatureFolder`

Used by Remove, by Create Feature and (per folder) by the background sweep, so exactly one code path deletes a
leftover Feature folder: `FeatureFolderCleaner.CleanupAsync` (`src/GrayMoon.Worker/Services/FeatureFolderCleaner.cs`).

Request (`CleanupFeatureFolderRequest`): `featureStorageRoot`, `featureRootPath`, `workspaceName`, `featureName`,
`retry` (short per-entry retries, used by Remove and Create), `onlyIfMarked` (Create: touch only a folder that already
carries a matching marker).

Behaviour:

1. **Path guards** (`ValidatePaths`, pure): both paths absolute; the storage root is not a drive root and is named
   `features`; the Feature folder is below it (any depth, because a Feature name may contain `/`).
2. Folder missing: return `Removed` (and remove empty grouping parents such as `features\team`).
3. **Tree guards** (`ValidateTree`): neither the storage root nor the Feature folder is a reparse point; no `.git`
   **directory** anywhere in the tree (a real repository); a `.git` **file** only when the `gitdir:` it points to no
   longer exists (the worktree was unregistered). Never enters a reparse point.
4. `onlyIfMarked` and no marker naming this folder: return `NotMarked`, touch nothing.
5. One delete pass with the existing reparse-point-safe walk (`GitWorktreeService.DeleteFolderRecursivelyWithRetryAsync`,
   now with a `retry` flag), markers included, then the folder itself when empty.
6. Gone: return `Removed`, and remove grouping parents it left empty (never the storage root).
7. Still there: write the root marker (keeping the names and time of an existing marker) and a copy in each leftover
   top-level repository folder; return `PendingDeletion` with the remaining file count.

Response: `outcome` (`Removed` | `PendingDeletion` | `Refused` | `NotMarked`), `remainingFileCount`, `message`.

`RemoveGitWorktreeCommand` keeps its residue cleanup unchanged (it still makes the common case finish in one go).

### 3.4 Remove Feature flow (App)

`RemoveFeatureCoreAsync`, after the Source and root worktree loop succeeds (no failed repository):

1. When the Workspace-role root worktree was removed, or any repository report has `ResidueRemaining`, send
   `CleanupFeatureFolder` for `{ManagedFeatureStorageRoot}\{FeatureName}` (`CleanupFeatureFolderForNameAsync`).
   Skipped when the Workspace has no managed storage root or uses the legacy drive-root path.
2. On `PendingDeletion`, set `OperationResult.RemoveFeaturePendingDeletionFolder`. Per-repository `ResidueRemaining`
   stays in the report but no longer drives UI warnings.
3. A Worker failure on this call is logged and ignored: the Feature is removed either way (the folder just has no
   marker; the sweep only deletes marked folders).

Failures that are real refusals (dirty worktree without discard, locked worktree without unlock, Git error with the
worktree still registered) are unchanged: Remove fails, the Feature goes to NeedsRepair, the dialog shows the error.

### 3.5 Background sweep (App triggers, Worker deletes)

The Worker owns the filesystem but not the list of storage roots; the App owns that list. So the App triggers and
the Worker does the work.

Worker command `SweepPendingFeatureFolders` (`SweepPendingFeatureFoldersCommand`):

- Request: `featureStorageRoot`, `workspaceName`, `excludeFeatureNames` (every Feature of that Workspace still in the
  database, any lifecycle state, as typed with `/`).
- `FindMarkedFolders` walks down from the storage root through plain grouping folders only (never into a marked folder,
  a Git checkout or a link, at most 8 levels), collects folders whose marker names that same folder, and skips excluded
  Feature names. Each is cleaned with `CleanupAsync(retry: false, requireMarker: true)`.
- Response: counts (`removed`, `stillPending`, `refused`), for logs only.

App `FeatureFolderCleanupService` (singleton + hosted service, `src/GrayMoon.App/Services/Features/`):

- **Worker connect / first run**: on `WorkerConnectionState.Online`, sweeps every Workspace with a managed storage root.
- **Workspace opened**: `WorkspaceRepositories` calls `RequestSweep(workspaceId)` when it loads a Workspace.
- Throttled per Workspace (`MinInterval`, 15 minutes); a sweep that could not reach the Worker does not count.
- Skips a Workspace while `IWorkspaceOperationLock.IsWorkspaceStructurallyBusy` (Create / Remove Feature running).
- Never toasts, notifies or changes UI state; logs at Information when something was found.

**Create Feature with the same name**: before any worktree work, `CreateFeatureCoreAsync` sends `CleanupFeatureFolder`
with `onlyIfMarked = true`. A marked folder that is still in use fails Create with condition
`FeatureFolderPendingDeletion`: "A previous Feature named '<Name>' is still being cleaned up because some of its files
are in use. Close the programs using <folder> or choose another name." An unmarked folder is never touched.

Why not a Worker-side timer: the Worker would need its own persisted list of storage roots (a second source of truth
next to the App database). The App triggers above cover startup and normal use with no new state.

### 3.6 Remove dialog

`RemoveFeatureModal` is back to the plan / remove / report flow it had before the lock work (no preflight check, no
Kill / Continue, no in-use state, no Refresh blockers, no Retry):

- `RemoveFeaturePendingDeletionFolder` shows one `gm-callout--info` line: "Some files were still in use, so part of
  the Feature folder was left behind. It is marked for deletion and GrayMoon will remove it automatically later."
  with the folder path in muted small text. Alone it reads "Feature removed. ..." with no warning heading.
- Leftover files never add a warning line (`BuildReportWarnings` / `HasReportWarnings`); kept or failed branch lines
  keep their warning treatment. A clean removal still closes the dialog at once.

---

## 4. Implementation

| Area | Files |
|---|---|
| Worker cleanup | `Services/FeatureFolderCleaner.cs`, `Services/PendingDeleteMarker.cs`, `Commands/CleanupFeatureFolderCommand.cs`, `Commands/SweepPendingFeatureFoldersCommand.cs`, matching request/response DTOs, `WorkerHubMethods.CleanupFeatureFolder` / `SweepPendingFeatureFolders`, DI and dispatch wiring |
| Worker removal | Everything in the section 2 table deleted; `RemoveGitWorktreeCommand` reports `failureKind` only; `WorktreeRemovalFailureClassifier.WarrantsLockInspection` removed |
| App | `WorkspaceFeatureOperations` (cleanup after Remove, check before Create), `FeatureFolderCleanupService` (+ `Program.cs`), `WorkspaceRepositories.razor.cs` (open trigger), `RemoveFeatureModal.razor` |
| Application | `OperationResult.RemoveFeaturePendingDeletionFolder`; blocker contracts and DTOs removed |
| Tests | Worker: `FeatureFolderCleanerTests`. App: `FeatureFolderCleanupServiceTests`, pending-deletion cases in `RemoveFeatureReportTests`, `RemoveFeatureModalCheckboxTests` |

---

## 5. Risks and decisions

| Topic | Decision |
|---|---|
| Unattended delete of a user's folder | Only below a managed `...\features` root, only with a marker naming that folder (sweep, Create), never with a `.git` directory or a live `.git` file inside, never a Feature that exists in the database, never through a reparse point. |
| A file stays locked forever | Folder stays marked; every trigger retries cheaply (one no-retry pass). Harmless. |
| User deletes the folder by hand | Next sweep finds nothing. Nothing to do. |
| User deletes the marker by hand | Folder is no longer swept. Their choice; GrayMoon never sweeps unmarked folders. |
| Database lost / reset | Markers still describe themselves; sweep excludes only Features present in the (new) database, so leftovers are still cleaned. |
| Remove slower when files are held | Remove keeps today's per-entry retries in `RemoveGitWorktree` (bounded). Optional follow-up: shorten them, since the sweep now catches whatever is left. |
| Old Worker / new App | Not possible: App and Worker are version-locked. |
| Linux / macOS | Same code; open files rarely block deletes there, so markers will be rare. |
