# Create Feature and Remove Feature: follow-up run of 2026-11-25

Status: partly built. This is the reading of the second manual test, done after the five fixes of `create-remove-feature-log-findings.md`, and what was built from it on 2026-11-25. Not built: the unordered query warning, the removal time, and C. The fixes were checked in a second run the same day; "Round 2" at the end has the results and what is open. Written 2026-11-25.

Logs: `GrayMoon.Logs/20261125-follow-up/` in the workspace root (`graymoon-app-20261125.log`, `graymoon-worker-20261125.log`, and the tester's notes in `README.md`). Line numbers below are from these two files. Workspace AVR, 40 repositories, App 0.2.1-main.46.

## What was run

| Time | Step | Result |
|---|---|---|
| 01:52:19 | Create Feature `BAM-9999-moon-test`, overlay left open until the CodeGraph build ended | Succeeded in 226.6 s, then 5 min 2 s of CodeGraph watch |
| 02:04:33 | Remove Feature `BAM-9999-moon-test` | Succeeded at the first attempt in 205.0 s |
| 02:08:54 | Switch to two other Features in the selector | - |
| 02:11:22 | Remove Feature `BAM-2500-new-features` (an older Feature) | Succeeded at the first attempt in 197.3 s |
| 02:15:45 | Create Feature `BAM-8888-flicker-test`, Background clicked at 02:20:30 | Succeeded in 239.5 s |

## The five fixes in this run

| # | Fix | Seen in this run |
|---|---|---|
| 1 | Tracked `.codegraph` files are put back after `codegraph uninit` | Both removals succeeded at the first attempt; no "changed since the check", no "has uncommitted changes" line. Both uninits answered `Removed` without a message (App log 9980, 14920). The log does not say whether a file was put back, so this run does not prove the restore ran; it only shows the failure did not come back. |
| 2, 3 | State label visible behind a long Feature name | Tester: "Removing... is nicely visible in context". |
| 4 | Sync queue no longer waits for a Feature in create | "Applying 40 sync notification(s) held for the Feature" after both creates (App log 4638, 22702). No "Slow sync notification" warning in the whole log. |
| 5 | Audit leaves out removed Feature repositories | No "Repository not found" warning in the whole log. |

Still not confirmed from the first document: that the AVR Workspace repository has `.codegraph/.gitignore` committed (`git ls-files .codegraph` there).

## B. The grid loads two or three times: cause found

The page instance is replaced on every change of `?context=`. All seven context changes of this run show the same pair of loads, the first on the old instance and the second on a new one:

| App log | Flow | Loads |
|---|---|---|
| 7623 to 7631 | Create Feature ends (overlay open) | `refresh` of context 1, `context selected` 44 on instance c9f394c2, instance 500f95ad created, `page opened` 44 |
| 23125 to 23130 | Create Feature, Background clicked | same three loads, within 650 ms |
| 9876 to 9918 | Remove Feature confirmed | `context selected` 1 on the old instance, new instance, `page opened` 1 |
| 14669 to 14673 | Remove Feature confirmed (second) | same |
| 13717 to 13728 | Selector switch, twice | `context selected` on the old instance, new instance, `page opened` |

So the question the first document left open is answered: the selector's own switch does the same. It is not specific to Create or Remove.

Cause, from the code: `ContextQuery` is a `[Parameter, PageQuery("context")]`. `PageQueryRouteData` hands it to the page as a direct parameter of the interactive root component (`Routes.razor`). A change of `?context=` therefore changes the root component's parameters, and the page is rendered without `@key`. The logs show that Blazor then disposes the instance and creates a new one instead of setting the parameters on the old one. The branch in `WorkspaceRepositories.OnParametersSetAsync` that was written for a surviving instance (`contextChanged && workspace != null && hasLoadedOnce` -> `SyncContextFromQueryAsync`) never ran in this log.

Confirmed in the framework (ASP.NET Core 10.0.15, `RemoteRenderer.WebRootComponentManager.WebRootComponent.UpdateAsync`, decompiled): a root component with a key gets the new parameters; one without a key is removed and added again when its parameters changed.

The same replacement must happen on the other pages with `[PageQuery("context")]` (Changes, Projects, Actions, Files, Packages, Dependencies); this run only has the Repositories page.

Two more loads that are not needed:

- After Create, the job's own end-of-operation refresh loads context 1 (the Workspace) a few milliseconds before the page switches to the new Feature (App log 7623, 23125). Its result is thrown away.
- After the Workspace refresh that follows a removal, the grid loads twice within about 210 ms: once from the job (with `op=`) and once without `op=`, the hub's "synced" broadcast after its 200 ms debounce (App log 10862 and 10866, 15401 and 15402). The first is the refresh that waited for the end of the job (`_pendingRefreshAfterJob` in `OnJobServiceChanged`).

### Built for B

- `PageRouteView` (used by `Routes.razor`) renders a page marked `[KeepsInstanceOnQueryChange]` with a key made of its type and its route values, without the query-bound ones. A change of `?context=` now updates the instance; another Workspace still creates a new one. Only the Repositories page is marked: it is the one page whose `OnParametersSetAsync` was written for a surviving instance. The other six pages keep today's behaviour (replaced, two loads) until each handles a changed context in `OnParametersSet`; Changes and Projects compare the query with the context id, which differs for the Workspace context (no `?context=`), so they need that fixed first.
- The load before the navigation stays the one load: the parameter update that follows finds the context already bound and returns. `OnFeatureRemovalConfirmedAsync` now selects first and navigates second, the order the selector already had.
- The refresh that waited for the end of a job goes through the 200 ms debounce and is cancelled by a context switch (`CancelPendingGridRefresh`): no load of the Workspace context just before Create switches to the new Feature, and one load instead of two after "Refreshing workspace state..." (the second one was the hub's "synced" event, 200 ms after the job's own).
- A page job that refreshes at its end clears the waiting refresh first (`StartPageJob`), as Update and Push already did.

Tests: `PageRouteViewTests` (the key, and that the rendered page frame carries it). The page behaviour itself has no automated test; the next run confirms it from the log.

## A. Create Feature waits for the hook burst: measured twice, worse than before

| Stage | First document | Create 1 | Create 2 |
|---|---|---|---|
| HeadSnapshot | 63.1 s | 60.6 s | 36.4 s |
| RootWorktree | - | 4.7 s | 5.4 s |
| SourceWorktrees (39, 16 at once) | - | 73.0 s | 84.8 s |
| EffectiveVersions | 82.4 s | 87.2 s | 112.2 s |
| Total | 228 s | 226.6 s | 239.5 s |

EffectiveVersions, Worker side (29 `GetGitVersionAtBranchPoint` each time):

| | Run time, median / max | Queued, median / max |
|---|---|---|
| Create 1 | 11.2 s / 18.9 s | 43.5 s / 65.4 s |
| Create 2 | 14.4 s / 17.6 s | 68.2 s / 88.2 s |

The cause named in the first document repeats: the commands wait behind the checkout-hook syncs in the Worker's main pool. Create 2 started 30 minutes after the App, so the startup Feature Reconcile is not the reason. Without the queue the stage would be about 15 to 20 s.

Built: option 2 of the first document. The main queue (`TrackedJobQueue`) holds commands and hook syncs apart and its workers take a command first. A hook sync that is running is not interrupted, so a command waits for at most one running job. The hook syncs finish later than before. Test: `TrackedJobQueueTests`.

### New: HeadSnapshot is slow on its own

`GetHeadCommits` is one command, never queued, 8 repositories at once: 60.5 s and 36.4 s for 40 repositories, slowest single repository 4.9 s (Worker log, "GetHeadCommits timing", 02:16:21). In Create 2 nothing else ran on the Worker at that time (the second removal had ended a minute before, no GitVersion activity). So it is not load from other work, and it is not explained by the logs. The command runs four or more git processes per repository (HEAD, branch, `describe --tags --exact-match`, `for-each-ref` for the collision check), about 200 for 40 repositories, 8 repositories at once. That gives 1.5 to 2.5 s per git process on the test machine, which points at the machine (process start), not at GrayMoon's queueing. Built: the "GetHeadCommits timing" line now also has `SumHeadMs`, `SumBranchMs`, `SumTagMs` and `SumCollisionMs`; divided by the repository count they give the time of one step. Not built: fewer processes per repository; decide after the numbers.

### New: nothing is shown during "Verifying published packages..."

Tester's note: for 87 to 112 s the overlay shows the label and nothing else. The resolver already reported "Checked N of M repositories..." after each answer, but no answer came for the first 45 to 70 s because of the queue (A). Built and taken out again: a "Checked 0 of M repositories..." report before the first Worker call; the tester does not want a count of 0. The label counts from the first answer, and with A the first answers come after one GitVersion run. Test: `EffectiveVersionStartCheckTests.Bulk_push_plan_counts_the_GitVersion_runs_and_writes_every_decision_to_the_job_terminal`.

## Remove Feature takes about 200 s in the background

Nobody waits for it at the screen, so this is lower priority. What the Worker log shows for the first removal:

- At the moment the removal is queued, leaving the Feature also starts a refresh of the Workspace: 40 `SyncRepository` (median 21 s each, with GitVersion) from 02:04:34 to 02:05:47.
- The removal's 40 `InspectWorktree` re-checks run in the read pool at the same time: median 28 s each, queued up to 58.9 s; the first one returns 46 s after the start.
- `RemoveGitWorktree`: median 10.8 s, max 23.2 s. `DeleteBranch` (30): median 13.5 s, max 20.1 s. 12 s to delete a local branch is not explained by the logs.
- `GetGitChangeStatus` of the read pool queued up to 60 s during the second removal, so the Changes data of other contexts was late for that time.

Options: (a) leave it; (b) start the Workspace refresh after the removal's re-check instead of with it; (c) find out why single git commands (`DeleteBranch`, `GetHeadCommits`) take seconds on this machine, which would help every stage above. Not built. (c) comes first: the new `GetHeadCommits` numbers of the next run are that measurement.

## Log noise found

| What | Where | State |
|---|---|---|
| A handled race is logged as two errors with stack traces: "UNIQUE constraint failed: RepositoryBranches..." followed by "Branch row was already stored by a parallel writer; kept the existing row." Twice during Create 2. | App log 19200 to 19244, 19645 to 19686 | Built. `AppDbContext` drops the added branch rows that are already stored right before the save, so the insert is not sent. The insert can still fail when another writer commits between that read and the save; the existing handling stays for it. Test: `RepositoryBranchWriterRaceTests.A_branch_stored_before_the_save_is_dropped_without_a_failed_insert`. |
| 40 warnings "Git Changes scan failed ... Worker version does not match" in 75 ms while the Worker was updating. | App log 653 to 731 | Built. The scan asks the Worker nothing while `GetUnavailableReason()` has a reason, and logs one line. Test: `BatchLookupTests.Scanner_asks_the_Worker_nothing_while_it_cannot_take_commands`. |
| The uninit outcome does not say whether tracked files were put back. | App log 9980, 14920 | Built. The Worker logs "put back N tracked file(s)" or "Git tracks no file of .codegraph". |
| EF warning "'First'/'FirstOrDefault' operator without 'OrderBy'" right after a grid load. | App log 734, 7625 | Open. Not found by reading the queries of the grid load; the warning does not name the query. Next step: make this warning throw in the App tests and read which test fails. |

Not a problem: "Too many changed paths" for `sightline-archive` while its worktree was still checking out (Worker log 32515), and the "Project name collision" warnings, which describe the repositories' content.

## Not covered by this run

- C of the first document (Feature index without the Workspace switch): no run with that setup.
- A failed removal: none happened, so the lasting report of a failure (fix 2) was not exercised.

## Next test round

Same steps as this run (Create Feature, switch contexts in the selector, Remove Feature, Create Feature with Background), on a build with these changes; the App and the Worker both changed. What to read:

| Point | Expected |
|---|---|
| B | One "Repositories grid load" per context change, `trigger=context selected`, and no "Repositories page instance ... created" after it. No `trigger=refresh` of context 1 just before the switch after Create. One `trigger=refresh` after "Refreshing workspace state...". |
| A | `Stage=EffectiveVersions` well under the 87 s of this run; `queued` of `GetGitVersionAtBranchPoint` in the Worker log at most about one GitVersion run (20 s). |
| Label | "Checked 1 of N repositories..." after the first answer; no "Checked 0". |
| HeadSnapshot | The `Sum...Ms` values of "GetHeadCommits timing". |
| Fix 1 | "CodeGraph uninit in ...: put back N tracked file(s)" or "Git tracks no file" in the Worker log at Remove Feature. |
| Noise | No "UNIQUE constraint failed: RepositoryBranches" error; no burst of "Git Changes scan failed" during a Worker update. |
| C | Once: New Feature with the CodeGraph box ticked in a Workspace whose definition has no `"codegraph": true`, then open Remove Feature and read the Worker line "Worktree ... has uncommitted changes". |

Also: `git ls-files .codegraph` in the AVR Workspace repository.

Watch on the Repositories page, because its instance now survives a context switch: the grid, the header buttons and the live updates must belong to the selected context after each switch, and browser Back and Forward between two contexts must still load the right one.

## Round 2 (2026-11-25, 12:29 to 13:12, build 0.2.1-optimization-followup.48)

Steps: Remove BAM-8888, Create BAM-7777 while that removal was still running, two aborted creates, Create BAM-1111, Create BAM-2222 aborted late, Remove BAM-2222. All logs are from an enterprise VDI.

| Point | Result |
|---|---|
| B | Confirmed. One page instance (41e06f11) from 12:29 to 12:57 through a removal, a Create and the context changes; every change is one `trigger=context selected` load. No load of context 1 before the switch after Create. One `trigger=refresh` after the Workspace refresh (12:49:41). |
| A | Better, not at the target. `Stage=EffectiveVersions` 58.5 s and 66.7 s (was 87 to 112 s). `GetGitVersionAtBranchPoint`: 58 runs, queued 11.8 s on average and 28.8 s at most, running 16.2 s on average. The requests now wait for each other, not for hook syncs. |
| HeadSnapshot | 129 s, 87 s, 51 s. Per git process (sum divided by 40): 4.2 s, 2.3 s, 1.4 s for `rev-parse HEAD`, and the same for the other three steps. The 129 s run shared the Worker with the removal of BAM-8888 (39 `worktree remove`, 80 change scans); the 51 s run had the Worker to itself. So one git process costs 1.4 s on this machine with nothing else running; 160 of them, 8 at once, are the 51 s. |
| Fix 1 | Confirmed: "CodeGraph uninit in ...BAM-8888-flicker-test: put back 1 tracked file(s) of .codegraph". |
| Noise | Confirmed: no "UNIQUE constraint failed", no error in either log, no "Git Changes scan failed". |
| Removal | 252 s for BAM-8888 (a Create ran beside it), 93 s for BAM-2222 alone. |

New in this round:

- After a Create that was waited for, the grid loads once for the new context and then 5 more times in 18 s (12:55:40 to 12:55:57, `trigger=refresh`). Each is a batch of hook syncs of the new worktrees being applied and broadcast; batches 2 to 6 s apart are not merged by the 200 ms debounce. This is the flicker still seen after Create.
- A `trigger=refresh` load 240 ms after a `context selected` load (13:06:24, 13:06:40): a hub event that arrived after the switch started its own debounce.
- Applying a batch of hook syncs took 8.1 to 8.4 s twice ("CheckAndPersist completed ... in 8103ms"; 22 "Slow sync notification" warnings at 13:03:37).
- Two "ensure watchers timed out" warnings at 12:51:21, while the removal and HeadSnapshot ran together.
- The EF warning about 'First' without 'OrderBy' is still logged after a grid load (12:29:54, 12:48:27).
- A Create aborted after its worktrees were made ends as `NeedsRepair` (BAM-2222); removing it worked.

Tester's notes of this round: no "Checked 0 of N" (done, see above); "Checked 1 of 40" without the word "repositories" in the Remove Feature check (done: `WorkspaceFeatureOperations` and the Repositories page); unticked checkboxes are white where they should be gray, seen at "Create CodeGraph index" and in one modal (open).

### Built after round 2

| Point | Change | Test |
|---|---|---|
| Flicker after Create | A grid refresh dropped the loaded rows first and read the visible ones after; a render in between showed the grid without rows. `ApplyWorkspaceReloadSnapshotAsync` now reads the visible rows first and swaps them in one step. The refreshes stay (each batch of hook syncs brings new versions), they no longer blank the rows. That the blanking is the flicker the tester saw is a reading of the code, not something seen in the log. | None: the page has no component test. Round 3 checks it. |
| HeadSnapshot | `GetHeadCommits` starts one git process for HEAD per repository (`GetHeadStateAsync`: commit and branch from one `rev-parse HEAD --symbolic-full-name HEAD`; `describe` only when detached) and one for the collision check: 2 per repository on a branch, was 4. The log line now has `SumHeadMs` and `SumCollisionMs` only. | `GetHeadCommitsCommandTests` |
| 8 s apply of a sync batch | The time was one Worker call: `ResolveGeneratedPackageReferences` ran 420 ms and waited 7.5 s in the main pool for a GitVersion run of the same hook syncs to end. It and `CheckFileVersions` only read files and now run in the read pool. | `WorkerCommandPoolRouterTests` |
| Unordered query warning | Found by making the warning throw in the App tests: the four header aggregates of `WorkspaceRepositoryLinkListQueryService.GetHeaderStateAsync` took the one row of a constant group with `FirstOrDefaultAsync`. They use `SingleOrDefaultAsync`. | `WorkspaceRepositoryLinkHeaderStateProfileTests.Header_state_reads_its_one_aggregate_row_without_an_unordered_first` |
| Checkboxes | The unticked checkboxes and radios that were Bootstrap white got `form-check-input-gray`: Create Feature (CodeGraph index), Return to default options, Version files commit, Branch (skip tags), Push with dependencies, Update dependencies, and the two terminal colour radios in Settings. Switches and the grids' own checkbox classes already had a gray. | None (markup class). |

Not changed: a hub event that arrives while a context switch is loading still causes one more load about 240 ms later. It may carry data newer than the load, so it is not dropped; with the row swap it does not blank the grid.

## Round 3: what to read

Same steps. The App and the Worker both changed.

| Point | Expected |
|---|---|
| Flicker | After a Create that was waited for, the rows stay on screen while the versions arrive. The log still shows several `trigger=refresh` loads. |
| HeadSnapshot | About half of round 2 for the same load: near 25 s with the Worker to itself (was 51 s). `SumHeadMs` divided by the repository count is the cost of one git process. |
| Sync apply | No "CheckAndPersist completed ... in" above about 1 s caused by a queued `ResolveGeneratedPackageReferences`; its `queued` value in the Worker log stays small. Fewer "Slow sync notification" warnings. |
| Warning | No "'First'/'FirstOrDefault' operator without 'OrderBy'" after a grid load. |
| Label | No "Checked 0 of N"; "Checked N of M repositories" in the Remove Feature check. |
| Checkboxes | Unticked boxes are gray in the dialogs listed above. |

Still open: Remove Feature time (93 s alone), `EffectiveVersions` (58 to 67 s, the GitVersion runs themselves), C, and the six other context pages.
