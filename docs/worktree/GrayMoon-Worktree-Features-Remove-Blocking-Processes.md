# Remove Feature: listing the processes that lock a worktree folder

Status: describes the current behaviour (part 1) and proposes a pre-removal "Kill or Continue" step (part 2).

Today GrayMoon names the programs that keep a Feature worktree in use only **after** Remove Feature has run: either
Remove fails ("Feature is still in use") or it succeeds and the "Feature removed with warnings" report lists leftover
files with the programs holding them. Part 1 explains how that list is produced. Part 2 proposes a robust lookup that
runs **before** removal and cannot hang, followed by a choice: **Kill** the listed programs and remove, or **Continue**
without them, with the dialog explaining that Continue may leave files behind.

---

## Part 1 - How listing the locked folders works today

### 1.1 Overview

```
RemoveFeatureModal    WorkspaceFeatureOperations (App)                       Worker
------------------    --------------------------------                       ------
Remove Feature   ---> RemoveFeatureCoreAsync            --RemoveGitWorktree--> RemoveGitWorktreeCommand
                                                                                 |- git worktree remove + residue cleanup
                                                                                 '- on failure / leftovers: IFileLockInspector
   <--- OperationResult.RemoveFeatureBlockers (failed) / RemoveFeatureReport[].BlockingProcesses (leftovers)

Refresh blockers ---> InspectRemoveFeatureBlockersAsync --InspectPathLocks---> InspectPathLocksCommand
                                                                                 '- IFileLockInspector (read-only)
Retry            ---> RetryRemoveFeatureResidueAsync    --RemoveGitWorktree--> residue cleanup again (+ inspector)
```

The lookup itself lives only in the Worker (it needs local process access). The App never inspects processes; it only
forwards the Worker's result to the dialog. Nothing in this chain ever closes, kills or signals a process.

### 1.2 The inspector (Worker)

`IFileLockInspector` (`src/GrayMoon.Worker/Abstractions/IFileLockInspector.cs`) has one method,
`InspectAsync(path)`, returning a `FileLockInspectionResult` (`src/GrayMoon.Worker/Models/FileLockInspectionResult.cs`):

| Field | Meaning |
|---|---|
| `Processes` | One `BlockingProcessInfo` per PID: id, friendly name, executable path, service name, `Kind`, `Reason`. |
| `MayBeIncomplete` | The list may miss blockers (too many files, unreadable process, unsupported OS, Restart Manager error). |
| `Diagnostic` | Short user-safe text explaining why the list may be incomplete. |

`RunCommandHandler` registers `WindowsFileLockInspector` on Windows and `UnsupportedFileLockInspector` elsewhere
(`src/GrayMoon.Worker/Cli/Handlers/RunCommandHandler.cs:119`). The unsupported one always returns an empty,
`MayBeIncomplete` result with "only supported on Windows".

`WindowsFileLockInspector.Inspect` (`src/GrayMoon.Worker/Services/WindowsFileLockInspector.cs:30`) works in two layers:

1. **Open files - Windows Restart Manager.**
   - `CollectFiles` walks the folder (never entering junctions/symlinks) and collects up to
     `MaxFilesToRegister = 4000` files. More than that sets `MayBeIncomplete` ("Only the first 4000 files were checked.").
   - `WindowsRestartManager.GetProcessesUsingFiles` (`src/GrayMoon.Worker/Platform/Windows/WindowsRestartManager.cs:88`)
     starts a private RM session, registers the files in batches of 500 (`RmRegisterResources`), calls `RmGetList`
     (retrying on `ERROR_MORE_DATA`) and ends the session. Restart Manager only works on files, not folders.
   - The RM application type maps to `Kind`: Application, Service, Explorer, Console, Critical or Unknown. Each hit gets
     `Reason = OpenFile`; the executable path comes from `QueryFullProcessImageNameW`.
2. **Current directory scan.** A shell or AI coding tool sitting *in* the folder keeps the folder itself in use without
   holding any file open, which Restart Manager cannot see. `FindProcessesWithWorkingDirectoryUnder` (line 123)
   enumerates every process, reads its current directory from the PEB
   (`WindowsProcessInspector.TryGetCurrentDirectory`, `src/GrayMoon.Worker/Platform/Windows/WindowsProcessInspector.cs:85`,
   handles both x64 and WOW64 layouts) and reports those whose directory is the folder or below it (`IsSameOrUnder`,
   case-insensitive). These get `Reason = WorkingDirectory`. A 32-bit Worker cannot read 64-bit PEBs, so this layer is
   skipped and the result is marked incomplete.

Both layers skip the Worker's own process (and PIDs 0 and 4), dedupe by PID, and sort by name. Processes that cannot be
opened (other user, elevated, protected) are silently skipped. An empty, complete list still does not prove nothing
holds the folder (a virus scanner may already have let go, for example).

### 1.3 When the Worker runs the inspector

The interface contract today says *"Only meant for the failure path; callers must not run it before every delete."*
It is invoked from exactly two commands:

**`RemoveGitWorktreeCommand`** (`src/GrayMoon.Worker/Commands/RemoveGitWorktreeCommand.cs:15`) - after
`GitWorktreeService.RemoveWorktreeAsync` (git `worktree remove` plus the guarded residue cleanup, which retries each
delete with back-off 200/400/800/1600/3200 ms). It inspects only when:

- removal failed and `WorktreeRemovalFailureClassifier.WarrantsLockInspection` says the failure is `PathInUse` or
  `AccessDenied`, or
- removal succeeded (Git unregistered the worktree) but `ResidueRemaining` is true - on Windows that is how a file or
  folder held open elsewhere usually shows up.

`AddBlockersAsync` copies the result into `RemoveGitWorktreeResponse.BlockingProcesses / BlockersMayBeIncomplete /
BlockersDiagnostic`. An inspection failure never replaces the removal outcome; it only sets "may be incomplete".

**`InspectPathLocksCommand`** (`src/GrayMoon.Worker/Commands/InspectPathLocksCommand.cs:15`) - read-only, for
"Refresh blockers". Takes up to 100 absolute paths, returns one result per path in request order; a missing path comes
back `Exists = false` with no processes. It is listed in `ReadOnlyCommands`
(`src/GrayMoon.Worker/Hosted/SignalRConnectionHostedService.cs:41`), so it does not queue behind mutating commands.

### 1.4 App side

`WorkspaceFeatureOperations` (`src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs`) reads the Worker JSON
through private wire classes (`RemoveGitWorktreeResult`, `BlockingProcessWire`, `InspectPathLocksWorker*`) and maps it
with `ToBlockingProcesses` (line 1185) into `RemoveFeatureBlockingProcess` (`src/GrayMoon.Application/OperationModels.cs`).
`Kind`/`Reason` travel as the Worker's enum names (strings).

Three entry points produce blocker lists:

| Entry point | When | Result shape |
|---|---|---|
| `RemoveFeatureCoreAsync` - failed `RemoveGitWorktree` (line ~820) | A worktree could not be removed and the Worker sent blockers | `blockersByWrId` -> `OperationResult.RemoveFeatureBlockers` (Remove failed) |
| `RemoveFeatureCoreAsync` - successful remove with residue (line ~1009) | Worktree unregistered, files left behind | `RemoveFeatureRepositoryReport.BlockingProcesses` plus a `ResidueTarget` for Retry |
| `InspectRemoveFeatureBlockersAsync` (line 1193) | "Refresh blockers" | Sends all target paths in one `InspectPathLocks` call; on Worker failure marks every target `MayBeIncomplete` with "Make sure the Worker is running and up to date." |
| `RetryRemoveFeatureResidueAsync` (line 1231) | "Retry" after leftovers | Re-runs `RemoveGitWorktree` (guarded residue cleanup only) and refreshes residue + blocker fields |

`ResidueTarget` is only set for Source worktrees when both Feature storage paths resolved; the Workspace-role root
worktree is never cleaned by GrayMoon, so it is never retried.

### 1.5 Dialog

`RemoveFeatureModal.razor` (`src/GrayMoon.App/Components/Features/`) has two post-remove states:

- **Remove failed, folder in use** (`HasInUseBlockers`: `_report is null && _blockers` non-empty). Shows
  `RemoveFeatureBlockerText.InUseTitle/InUseBody`, one `BlockingProcessList` per repository, "Refresh blockers", and
  the primary button turns into "Retry" (runs Remove again).
- **Removed with warnings** (`_report` set). `BuildReportWarnings` adds a leftover-files line per repository with
  `ResidueRemaining`, and that line carries its `BlockingProcessList`. When any entry has a `ResidueTarget`,
  "Refresh blockers" (`LeftoverBlockerTargets` -> `InspectRemoveFeatureBlockersAsync` -> `ApplyRefreshedBlockers`;
  a folder that has disappeared drops out of the warnings) and "Retry" (`RetryRemoveFeatureResidueAsync`) are offered.

`BlockingProcessList.razor` renders `RemoveFeatureBlockerText.Ordered(...)`: one row per PID,
`WorkingDirectory` holders first (closing a shell or AI tool usually closes its children), then by name. Each row shows
`Title` ("Claude (PID 18472)"), a plain-language `Reason` and the executable path. An empty list shows
`NoBlockerFound`; `MayBeIncomplete` adds "This list may be incomplete." plus the diagnostic.

### 1.6 Why the user only finds out afterwards

During planning (`AnalyzeRemoveFeatureAsync`) the dialog learns disk state, dirty/staged files, branches, locks and PRs
for each worktree (`InspectWorktree`), but nothing about processes. So the typical sequence is: user leaves a terminal
or an AI tool inside the Feature, clicks Remove, Git unregisters the worktree, residue cleanup fails on the held folder,
and only then does the report say which program was responsible. By then the Feature context is gone and the user is
left with Retry on leftover files (and no Retry at all for the root worktree).

---

## Part 2 - Kill or Continue before the Feature is removed

### 2.1 What the user gets

When the user clicks **Remove Feature**, GrayMoon first checks which programs are using the Feature's folders. If none
are found, removal starts immediately as today. If some are found, the dialog stops and shows them:

```
+--------------------------------------------------------------------------------------+
| Remove Feature - login-redesign                                                    X |
|--------------------------------------------------------------------------------------|
| [!] Programs are using this Feature                                                  |
|     These programs have files or folders open inside the Feature. GrayMoon cannot    |
|     delete what they hold.                                                           |
|                                                                                      |
|   WebApi                                                                             |
|   [x] claude (PID 18472)       Its current folder is inside this Feature             |
|       C:\Users\...\claude.exe                                                        |
|   [x] VBCSCompiler (PID 9120)  Has files open in this Feature                        |
|   Frontend                                                                           |
|   [x] node (PID 7716)          Has files open in this Feature                        |
|   [ ] Explorer (PID 4410)      A File Explorer window shows this Feature             |
|       Cannot be killed by GrayMoon. Close the window yourself.                       |
|                                                                                      |
|   Kill ends the selected programs immediately. Unsaved work in them is lost.         |
|   Continue removes the Feature without closing them. Files they hold will be left    |
|   on disk; you can delete them later from the removal report.                        |
|                                                                                      |
|                              [Refresh]  [Cancel]  [Continue]  [Kill and remove]      |
+--------------------------------------------------------------------------------------+
```

- **Kill and remove** - ends the selected programs, checks again, then removes the Feature. If something is still
  holding a folder afterwards (a program that could not be killed, or a new one), the dialog shows the list again
  with the same choices instead of starting the removal.
- **Continue** - removes the Feature now. Anything held stays on disk and appears in the existing "Feature removed with
  warnings" report (part 1.5), with Refresh blockers / Retry, and Kill there too (2.7).
- **Cancel** - closes the step; nothing changed.
- **Refresh** - reruns the check (for when the user closed programs themselves).

Neither the check nor a failed Kill ever blocks the user: **Continue and Cancel are enabled at all times**, including
while the check is still running.

### 2.2 Robust lookup that cannot hang

The current file walk + Restart Manager (part 1.2) is too slow and too incomplete to put in front of every Remove: it
costs time per file, stops at 4000 files and cannot see folder handles. Replace it with three passes whose cost does
not depend on how many files the worktrees contain, and run all worktree folders of the Feature in one call.

| Pass | What it finds | API |
|---|---|---|
| 1. Handle table | Open files **and folders** (editors, build servers, Explorer windows, `FileSystemWatcher`s, shells' current folder handle) | `NtQuerySystemInformation(SystemExtendedHandleInformation)`, then per candidate handle `DuplicateHandle` + `GetFinalPathNameByHandleW` |
| 2. Loaded images | Programs and DLLs running from inside the Feature (a test host from `bin\Debug`), which may hold no file handle | `EnumProcessModulesEx` + `GetModuleFileNameExW` per process, plus the process image path |
| 3. Current folder | Labels a match as `WorkingDirectory` (shell, AI tool) for the friendlier reason text and ordering | existing `WindowsProcessInspector.TryGetCurrentDirectory` |

Each hit is matched by path prefix against all requested roots, and attributed to the **most specific** root that
contains it, so a process in a Source worktree is not reported again under the Workspace-role root that contains it.

**Why it cannot hang - four layers of protection:**

1. **Filter before naming.** The known hang is asking for the name of a synchronous named pipe handle. Only handles of
   the `File` object type are considered (type index learned once by opening a file in the Worker and finding it in the
   table), and each duplicated handle must pass `GetFileType == FILE_TYPE_DISK` before its name is queried. Pipes,
   sockets and devices are never named.
2. **Per-call watchdog.** Name queries run on one dedicated background thread fed from a queue. If a single query does
   not return within 200 ms, that handle is skipped, the thread is abandoned (marked background so it never keeps the
   process alive) and a fresh thread continues. The result is marked `MayBeIncomplete`.
3. **Overall deadline.** The whole inspection has a budget (5 s by default, `WorkerOptions.LockInspectionTimeout`).
   When it expires, whatever was found so far is returned with `MayBeIncomplete = true` and a diagnostic
   ("The check took too long and may have missed some programs.").
4. **Isolated process.** The native work runs in a short-lived child process, `graymoon-worker inspect-locks --json`,
   with the roots on stdin. The Worker kills the child if it exceeds the deadline plus a grace period. A stuck kernel
   call can therefore never wedge the Worker, its SignalR connection or its job pools. (Layers 1-3 keep this from
   happening in practice; layer 4 is the guarantee.)

On the App side, the call uses its own `CancellationToken` with a timeout slightly above the Worker deadline. A timeout
or any Worker error turns into "could not check" in the dialog, never an exception, and never a disabled Continue.

Restart Manager stays as a fallback when pass 1 is not available (handle table query fails), capped as today.

Limits that remain and are shown as "This list may be incomplete." when they apply: processes the Worker cannot open
(elevated or another user's, unless the Worker runs elevated), a 32-bit Worker, the deadline being hit. Antivirus
scanners and the Windows Search indexer usually let go within seconds; the existing residue cleanup retry (200 ms to
3.2 s back-off) already covers them.

### 2.3 Kill: rules that keep it safe

Today GrayMoon never closes a process, and that is written into `IFileLockInspector`, `CLAUDE.md` and the architecture
docs. Kill changes that rule, so it gets strict limits:

- **User-confirmed only.** A process is killed only when the user clicked Kill with it selected in this dialog.
  Nothing is killed automatically, on a timer, or by an API caller without that selection.
- **Worker re-verifies, never trusts the App.** The App sends the Feature paths (resolved from its own database, like
  `ResidueTarget`, never from browser input) and the selected `(ProcessId, StartTime)` pairs. The Worker runs the
  inspection again and kills only processes that (a) are still found holding one of those paths **now** and (b) still
  have the same start time. That rules out PID reuse and turns a compromised or buggy App into, at worst, killing
  programs that really are inside the Feature folder.
- **Never killable** (shown without a checkbox, with a reason):
  - the Worker itself, its parent and its children, the GrayMoon App and GrayMoon Desktop processes;
  - `Critical` kind, PID 0/4, processes in session 0, Windows services (`Service` kind);
  - Explorer (`Explorer` kind) - killing it restarts the whole taskbar and desktop. The text asks the user to close the
    window instead. (Later: close only the Explorer windows showing the Feature through `IShellWindows`.)
  - anything the Worker cannot open with `PROCESS_TERMINATE` (reported as "Access denied - close it yourself").
- **Default selection**: every killable process is pre-selected; the user can untick any.
- **How**: `TerminateProcess` (via `Process.Kill()`, not the whole tree; children that also hold files are listed and
  selected separately), then wait up to 3 s for exit.
- **Logged** at Information in the Worker log: PID, name, executable path, Feature path. Never persisted in the App
  database and never sent to a connector.

The per-process result comes back to the dialog: `Killed`, `AlreadyExited`, `NotHoldingAnymore`, `StartTimeChanged`,
`Protected`, `AccessDenied`, `Failed`. Anything other than killed or gone keeps the process in the list, with that reason.

### 2.4 End-to-end flow

```
Dialog open        -> AnalyzeRemoveFeatureAsync (unchanged)
                   -> start prefetch: InspectRemoveFeatureBlockersAsync(all existing worktree paths)   (background)

Remove clicked     -> prefetch result younger than ~5 s? use it : run inspection again (bounded, 2.2)
                      no blockers                    -> RemoveFeatureAsync (unchanged)
                      blockers / could not check     -> "Programs are using this Feature" step

Kill and remove    -> TerminateRemoveFeatureBlockersAsync(featureContextId, selected (pid, startTime))
                      Worker: re-inspect, intersect, refuse protected, kill, wait, re-inspect
                      remaining blockers             -> show the step again with per-process results
                      none                           -> RemoveFeatureAsync

Continue           -> RemoveFeatureAsync; leftovers go to the existing warnings report
```

When the check itself failed ("could not check"), the step says so in one line and offers only Continue and Cancel;
there is nothing to kill.

### 2.5 Wire and contract changes

**Worker**

| Item | Change |
|---|---|
| `IFileLockInspector` | Add `InspectAsync(IReadOnlyList<string> roots, TimeSpan deadline, CancellationToken)` returning one result per root. Update the summary: read-only, called before Remove, on Refresh and on the failure path. |
| `WindowsHandleLockInspector` (new) | Passes 1-3 from 2.2 inside the `inspect-locks` child process; `WindowsFileLockInspector` (Restart Manager) becomes the fallback. |
| `BlockingProcessInfo` | Add `StartTime` (UTC, from `GetProcessTimes`), `CanTerminate`, `ProtectedReason`. |
| `InspectPathLocksCommand` | Uses the multi-root overload (one pass for all roots). Stays read-only. |
| `TerminateBlockingProcessesCommand` (new) | Request: `paths`, `processes[{processId, startTime}]`. Re-inspects, applies 2.3, kills, re-inspects. Response: per-process outcome + fresh `InspectPathLocksResult` list. Mutating, so main pool (not `ReadOnlyCommands`). Add the name to `WorkerHubMethods`. |
| `inspect-locks` CLI subcommand (new) | Hidden `WorkerCli` subcommand: roots on stdin, JSON on stdout, exits on its own deadline. |

**App / Application**

| Item | Change |
|---|---|
| `RemoveFeatureBlockingProcess` | Add `StartTime`, `CanTerminate`, `ProtectedReason`. |
| `IWorkspaceFeatureOperations` | Add `TerminateRemoveFeatureBlockersAsync(WorkspaceFeatureContextId, IReadOnlyList<(int ProcessId, DateTime StartTime)>, CancellationToken)`. Resolves the worktree paths from the database itself. |
| `WorkspaceFeatureOperations` | Implement it; `InspectRemoveFeatureBlockersAsync` gets an App-side timeout. Kill runs under the same structural lock Remove takes, so no other Feature operation runs while processes are being ended. |
| `RemoveFeatureModal.razor` | New step state (`_preflightBlockers`, selection set, `_killing`), prefetch on open, check on Remove click, the buttons from 2.1, late results ignored once removal started or the dialog closed. |
| `BlockingProcessList.razor` | Optional checkbox per row (`Selectable`, `Selected`, `SelectedChanged`), disabled with `ProtectedReason` text when `CanTerminate` is false. |
| `RemoveFeatureBlockerText` | New text (ASCII only): `PreflightTitle`, `PreflightBody`, `KillExplanation` ("Kill ends the selected programs immediately. Unsaved work in them is lost."), `ContinueExplanation` ("Continue removes the Feature without closing them. Files they hold will be left on disk; you can delete them later from the removal report."), `CheckFailed`, and one line per kill outcome. |

**Docs to update when this lands**: `CLAUDE.md` (Worktree Features paragraph: "nothing ever closes a process"),
`docs/architecture/05` section 32, and the `IFileLockInspector` summary.

### 2.6 Things to get right

- **Kill is not a guarantee.** Child processes can start, a killed IDE can be restarted by a launcher, a scanner can
  grab a file. That is why the Worker re-inspects after killing and the dialog loops back instead of assuming success,
  and why the post-remove leftovers report stays.
- **Killing AI tools and shells loses their session.** The `WorkingDirectory` reason is shown first and the Kill
  explanation is always visible, not hidden behind a tooltip.
- **Shared build servers** (`VBCSCompiler`, MSBuild nodes, `dotnet build-server`) often hold files from several
  repositories; killing them is harmless (they restart on the next build) but affects other Features' builds in flight.
  The reason line can say so for these known names.
- **Stale prefetch.** Only reuse the open-time result for a few seconds; the Remove click otherwise pays one bounded
  check (typically well under a second with the handle table).
- **Version lock.** App and Worker are version-locked, so no capability flag is needed; a mismatched Worker refuses the
  commands and the dialog falls back to "could not check" + Continue.

### 2.7 Kill in the leftovers report too

The "Feature removed with warnings" state already lists the programs holding leftover folders. Add the same checkboxes
and a **Kill and retry** button there, calling `TerminateBlockingProcesses` with the leftover `ResidueTarget` paths and
then `RetryRemoveFeatureResidueAsync`. The Feature context no longer exists at that point, so this overload takes the
report's residue targets instead of a context id; the Worker still re-verifies as in 2.3.

### 2.8 Tests

Worker (`GrayMoon.Worker.Tests`, Windows-only where native):

- A helper process holding a file open, one sitting in the folder as its current directory, one with a folder handle
  open, one running an executable from the folder: each is found by the handle/image passes and attributed to the most
  specific root.
- A helper holding a synchronous named pipe open: inspection completes within the deadline (pipe never named).
- A fake name query that never returns: the watchdog skips it and the result is `MayBeIncomplete`.
- Deadline exceeded in the child: the Worker kills it and returns partial results.
- Terminate: kills a matching helper; refuses a PID whose start time differs; refuses a PID not holding the paths;
  refuses protected kinds and the Worker itself; reports `AlreadyExited`.

App (`GrayMoon.App.Tests`):

- Modal logic: no blockers -> remove starts directly; blockers -> step shown; Continue calls Remove with the plan;
  Kill with remaining blockers loops back; check failure shows Continue/Cancel only; late inspection results ignored.
- `BlockingProcessList` selection: protected rows not selectable, default selection = all killable.
- `RemoveFeatureBlockerText` new strings are ASCII-only (extend `Ui_text_uses_only_ascii_hyphens`).
- `TerminateRemoveFeatureBlockersAsync` sends database-resolved paths only.

### 2.9 Suggested delivery order

1. Multi-root handle/image inspector with watchdog, deadline and child process; `InspectPathLocks` switches to it.
2. Dialog check on Remove click with **Continue / Cancel** only (useful on its own: the user sees what will block).
3. `TerminateBlockingProcesses` + **Kill and remove**.
4. Kill in the leftovers report (2.7).
