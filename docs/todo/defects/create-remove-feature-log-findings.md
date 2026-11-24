# Create Feature and Remove Feature: findings from the logs of 2026-11-24

Status: partly built. Seven defects were found in the logs of one manual test (Create Feature, a deploy, Remove Feature twice; Workspace AVR, 40 repositories, build with #553). Five are fixed, two are open. Written 2026-11-24.

Logs: `GrayMoon.Logs/20261125/` in the workspace root (three cumulative snapshots of `graymoon-app-20261124.log` and `graymoon-worker-20261124.log`; line numbers below are the same in every snapshot that has the line).

## Built

| # | Defect | Fix | Test (fails on the old code) |
|---|---|---|---|
| 1 | Remove Feature failed on the first attempt with "changed since the check, run Remove again." after removing 39 of 40 worktrees (App log 14184, 23:13:13). The second attempt showed "Has uncommitted changes" for the Workspace repository. `codegraph uninit` deletes the whole `.codegraph` folder, including a committed `.codegraph/.gitignore`. | The Worker puts the tracked files of `.codegraph` back after the index is removed (`CodeGraphCli.RestoreTrackedIndexFilesAsync`, called by `UninitCodeGraphCommand`). When that fails the outcome is still `Removed`, with a message the App logs as a warning. | `CodeGraphUninitTrackedFilesTests` |
| 2 | A failed removal was reported only by a toast; the lasting report ("Removal incomplete" in the context selector, with the error as tooltip) was cut off behind a long Feature name. | Fixed by 1 (the failure no longer happens) and 3 (the label is visible). The state after a failure was already right: `NeedsRepair` with `LastError`. | see 1 and 3 |
| 3 | In the context selector the state label ("Removing...", "Removal incomplete") was inline text behind the name in one ellipsis box, so a long name cut it off first. | The name and the label are separate elements; the name shrinks, the label keeps its width (`WorkspaceFeatureSelector.razor`, `.razor.css`). Both lists: Features, and the Features of another Workspace. | `FeatureSelectorStateLabelTests` |
| 4 | The sync queue's reader waited inside one notification of a Feature that was still being created (136 s, App log 2246 to 3852). 21 notifications of the Workspace context and 39 of the Feature waited behind it: 61 "Slow sync notification" warnings. | The reader no longer waits. The notifications of a Feature that is not ready are set aside and applied in order when it has finished (04, "Hook sync batches"). The time set aside does not count for the slow warning. | `WorkerSyncNotificationQueueBatchTests` (6 new), `FeatureProjectionSeedTests.A_sync_held_for_a_feature_in_create_does_not_hold_up_the_workspace_in_the_sync_queue` |
| 5 | After the failed removal the periodic audit asked the Worker about the 39 worktrees that were already deleted: 39 "Repository not found" warnings per audit (App log 14290 to 14452). | `GitChangesMonitorTargets` leaves out Feature repositories in state `Removed`. | `GitChangesMonitorTargetsTests` |

Logging added for the open points: the Worker names the first files of a dirty worktree in `InspectWorktree` ("Worktree ... has uncommitted changes ... First entries: ..."); the Repositories page logs each instance it creates and disposes and each grid load with its trigger ("Repositories grid load: instance=..., trigger=..."); the sync handler logs each broadcast ("SyncCommand broadcast for N notification(s)").

### What is confirmed and what is not for defect 1

- Confirmed with CodeGraph 1.6.2 in a scratch repository: with `.codegraph/.gitignore` committed, `codegraph uninit -f` leaves `D .codegraph/.gitignore`.
- Confirmed in the logs: the root worktree was clean at the check (23:09:43, Worker log 15446) and dirty at the re-check (23:13:11, Worker log 22181), and `UninitCodeGraph` was the only command that ran on the root folder in between.
- Not confirmed: that the AVR Workspace repository has `.codegraph/.gitignore` committed. To check: `git ls-files .codegraph` in that repository. If it prints nothing, defect 1 had another cause there and the new Worker log line will name the file at the next occurrence.

## Open

### A. Create Feature waits for the hook burst on the Worker (should)

Symptom: the `EffectiveVersions` stage of Create Feature took 82.4 s (App log 3838); the measurement on Edx1 (27 repositories) gave 7.2 s for the same work. Create took 228 s in total.

Cause: the stage sends 29 `GetGitVersionAtBranchPoint` commands. They go to the Worker's main pool (16 workers), which at that moment runs the 40 checkout-hook syncs of the new worktrees, each a GitVersion run of 17 to 27 s (Worker log 4026, 7793, 9192). The commands were queued for up to 66.9 s before they started (Worker log 8201 to 9814, for example line 9349: "in 10048ms (queued 66892ms)"). The user waits for the stage; nobody waits for the hook syncs.

Code: `WorkspaceFeatureOperations.CreateFeatureCoreAsync` (stage `EffectiveVersions`), `EffectiveVersionResolver.ProbeAsync`, the Worker's `MainJobBackgroundService` and `WorkerCommandPoolRouter`, `HookSyncDispatcher`.

Options, not decided:

1. Hook syncs get a pool of their own on the Worker, so a command the App sent never queues behind them. The total GitVersion work stays the same (69 runs), so the gain depends on how many runs the machine carries at once.
2. The Worker's main pool takes commands before hook syncs (two queues, one set of workers). Same effect on the stage as 1, no extra processes at once; the hook syncs finish later.
3. Leave it. The stage is correct, only slow, and only for a Workspace this size.

Recommendation: 2, after a measurement. Measure first: Create Feature on AVR twice in a row a few minutes after the App started, and read `Feature Create timing ... Stage=EffectiveVersions` and the `queued` times of `GetGitVersionAtBranchPoint` in the Worker log.

Also seen, not attributed: `HeadSnapshot` took 63.1 s (Worker log 2671). Create started 35 s after the App, while the startup Feature Reconcile was still running (76.3 s). The same measurement shows whether it repeats.

### B. The Repositories grid loads twice after Create Feature and after leaving a removed Feature (should)

Symptom: the grid blinks twice.

Evidence: a second "Workspace-sync hub start for WorkspaceRepositories" 575 ms after the Create operation ended (App log 7310, 7314) and 284 ms after "Feature removal queued" (App log 12106, 12146). The hub is created once per page instance (`WorkspaceRepositories.Realtime.cs`), so a second instance of the page was created each time. The first instance had already loaded the new context (`OnSelectedContextChangedAsync`), the second loads it again.

Cause: not known. The design expects the instance to survive a navigation that only changes `?context=` (`PageQueryAttribute`, `OnParametersSetAsync`). Both flows call `SelectedContextIdChanged` and then `NavigationManager.NavigateTo(..., replace: true)` (`WorkspaceContextBar.SelectContextAsync`, `WorkspaceRepositories.OnFeatureRemovalConfirmedAsync`). Whether the selector's own switch does the same is not in the logs.

Fix plan: confirm first. Run Create Feature once with the new page logging and read the "Repositories page instance ... created / disposed" and "Repositories grid load" lines. They say whether the instance is replaced and which trigger loads the grid. Then either keep the instance (the cause of the replacement) or drop the load before the navigation; not both, and not before the cause is known.

### C. A Feature index without the Workspace switch makes the Feature look changed (to validate)

Seen in the scratch repository, not in the logs: when `.codegraph` is neither committed nor ignored (New Feature with the CodeGraph box ticked in a Workspace whose definition has no `"codegraph": true`), `codegraph init` leaves `?? .codegraph/` and `FeatureCodeGraphService.InitializeAsync` writes an untracked `codegraph.json`. The Remove Feature check then shows "Has uncommitted changes" for the Workspace repository although the user changed nothing. To validate: create such a Feature and read the new "Worktree ... has uncommitted changes" line of the Worker.

## Order

B first (one run with the new logging decides it), then A (needs the owner's decision between the options), then C.
