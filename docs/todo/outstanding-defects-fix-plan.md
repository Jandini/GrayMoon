# Outstanding defects: classification and fix plan

Written 2026-11-11 on branch `outstanding-bugs`. Covers every note in `docs/todo/defects/` (11 notes, 30 separate items). Each note was read against the current code; "Checked" says what was actually opened. Nothing was run or reproduced, so items the notes mark "to confirm first" still need their failing test before the fix.

## Classes

- **Must** - loses or corrupts data, or leaves a secret exposed. Fix before the next release.
- **Should** - a real defect with a wrong or missing result, or a broken rule, but no data loss. Fix in this round.
- **Optional** - hardening with no observed failure, a missing feature, or a test-only gap. Safe to defer.
- **Close** - already fixed in the code or needs no change; only the note is out of date.

## Summary

| # | Item | Note | Class | Size |
|---|---|---|---|---|
| 1a | Repository refresh deletes Workspace-linked repositories (empty answer, inactive connector, no longer returned) | connector-refresh-empty-answer | Done 2026-11-11 | S-M |
| 1b | Removing a connector cascades to its repositories and Workspace links | connector-refresh-empty-answer (widened by owner decision) | Must | M-L |
| 2 | Feature sync deletes workspace-global generated package rows | generated-package-context-write | Must | M |
| 3 | Version from one context written onto every consumer edge | generated-package-context-write | Must | M |
| 4 | Signing secrets still at repository level | release-signing-secrets-and-workflow-run | Must (owner action) | S |
| 5 | All-empty refresh never purges deleted repositories | connector-refresh-empty-answer | Closed with 1a (an all-empty answer now changes nothing and is reported) | XS |
| 6 | Worker drops a command result while not connected, with no log | worker-response-dropped-while-reconnecting | Should | S |
| 7 | Registry lookup error read as "package does not exist" | silent-catch-blocks | Should | S |
| 8 | Failed main-path lookup becomes `null` in the Feature reconciler | silent-catch-blocks | Should | S |
| 9 | Root-path refresh on Worker connect fails silently | silent-catch-blocks | Should | XS |
| 10 | `synchronous=NORMAL` only on the startup connection | sqlite-synchronous-per-connection | Should | XS |
| 11 | Commit targets frozen before the AI step and the default-branch dialog | commit-all-partial-commit-race (Phase 2) | Should | M |
| 12 | Browser tab rewrites the Desktop window title and tray label | push-package-wait-and-circuit-crash (3) | Should | S |
| 13 | Hub first start has no retry; no tests for the helper | push-package-wait-and-circuit-crash (1, 2) | Should | M |
| 14 | Four review fixes never proven against the old code | review-fix-test-gaps (row 4) | Should | S |
| 15 | Missing test: backup written but fails the integrity check | review-fix-test-gaps (row 1) | Should | S |
| 16 | No startup probe for a damaged Worker install | worker-damaged-install-probe | Optional (cause already fixed) | M |
| 17 | Post-commit status refresh failure not logged | silent-catch-blocks | Optional | XS |
| 18 | Missing tests: serial cancel dispatch, context flip, hub attach | review-fix-test-gaps (rows 2, 3, 5) | Optional | M-L |
| 19 | Convention scans narrower than the rules | convention-test-coverage | Optional (do with 7-9) | M |
| 20 | Worker commit hardening C, D, E, F | commit-all-partial-commit-race (Phase 3) | Optional (needs repro) | L |
| 21 | Correlation id per commit run; fan-out audit; 240 s status scan timeout | commit-all-partial-commit-race (Other) | Optional | S-M |
| 22 | "Continue waiting" on a failed push level | moved to `ideas/push-continue-waiting.md` | Not a defect | - |
| 23 | Warn when commit targets differ from what the AI described | commit-all-partial-commit-race (Phase 2.2) | Optional (falls out of 11) | S |
| 24 | Push step re-reads the context (finding G) | commit-all-partial-commit-race | Close | - |
| 25 | "Nothing to commit" treated as a failure | commit-all-partial-commit-race (Phase 2.1, second half) | Close | - |
| 26 | Toast is only a hint | push-package-wait-and-circuit-crash (4) | Close | - |
| 27 | "Clear read-only" catch before folder delete | silent-catch-blocks | Close (leave as is) | - |

## Must fix

### 1a and 1b. Connector state must never remove a Workspace's repositories

Checked: `GitHubRepositoryService.RefreshRepositoriesAsync` and Phase E of `GitHubRepositoryRepository.MergeRepositoriesAsync` (`Repositories/RepositoryRepository.cs`).

The cause is as the note says. A connector lands in `failedConnectorIds` only when `fetch.ErrorMessage` is set. A 200 with an empty list is "answered", and Phase E deletes every unmatched row whose connector is not in `failed`. With two connectors, one empty answer plus one normal answer runs the merge and removes the empty connector's repositories with their links, branches and pull request rows. Only repositories used by a Feature survive (`KeptForFeatures`).

Also seen while reading, not in the note: Phase E filters with a negative list (`!failed.Contains(r.ConnectorId)`). An **inactive** connector is never fetched and never in `failed`, so its stored repositories are delete candidates whenever another connector's merge runs. This may be intended (deactivate means remove) or the same bug in a second form. Confirm with a test and decide before fixing.

A third path does the same thing on purpose: `ConnectorRepository.DeleteAsync` removes the connector, and the `Repositories.ConnectorId` foreign key (`OnDelete(DeleteBehavior.Cascade)`, `AppDbContext.cs` line 127-130) cascades to its repositories and their Workspace links. It refuses only when a Feature uses one of them.

Owner decision (2026-11-11): connector state must never wipe a Workspace. A removed, deactivated, unusable or empty-answering connector leaves the Workspace's repositories in place, and the Workspace says that the repository has no valid connector. This replaces the note's "confirm the removal" idea; there is no purge of linked repositories at all.

Rule after the fix: **a repository linked to any Workspace is deleted only by removing it from that Workspace.** Connector state can delete only unlinked catalog rows (the Repositories page list), and only for a connector that answered with a non-empty list.

Plan, in two changes.

Change 1a - the refresh stops deleting linked repositories (done 2026-11-11; the merge derives the answered connectors from the fetched set instead of taking a list, and the unused `DeleteOrphanedAsync` was removed):
1. Failing tests first (`GitHubFanOutTests` style), each seeding links, branches and pull request rows: (a) connector A returns zero, connector B returns its list; (b) connector A is inactive, B returns its list; (c) A returns a list that no longer contains a linked repository. Assert in all three that A's linked rows survive.
2. Phase E of `MergeRepositoriesAsync`: generalize the existing "kept because a Feature uses it" branch to "kept because a Workspace links it". The `KeptForFeatures` result becomes "kept, no longer returned by the provider" and names the Workspaces.
3. Change the merge to a positive list: pass the connector ids that answered with at least one repository and delete unlinked rows only for those. A zero answer for a connector with stored rows is reported as "Connector X returned no repositories; its repositories were left unchanged". This matches the rule "delete only for sources that answered".
4. Mark a kept repository so the UI can say why: new nullable column `Repositories.MissingFromProviderSinceUtc` (set when kept, cleared when a later fetch returns the repository). Needs the entity change, an idempotent `Migrate*Async` and a new `StrictSteps` entry.

Change 1b - removing a connector is a soft delete, never a cascade (medium, one added column):
1. Failing test: delete a connector whose repositories are linked to a Workspace and used by a Feature; assert the links, context rows and Feature rows survive and the delete succeeds.
2. Soft delete (owner decision 2026-11-11): the connector row stays as a tombstone. New nullable column `Connectors.RemovedAtUtc` (an added nullable column, so no table rebuild and no foreign key change). `DeleteAsync` then: deletes the connector's unlinked repositories; if no linked repository is left, deletes the connector row as today; otherwise sets `RemovedAtUtc`, clears `UserToken`, sets `IsActive = false` and keeps the row. It loses its "remove the Features first" refusal. The stored token is always cleared, so a removed connector can never authenticate anything.
3. A removed connector is invisible everywhere a connector can be chosen or used: `ConnectorRepository.GetAllAsync` and the other list reads filter it out (one place, with an explicit `IncludeRemoved` read for the few callers that need it), the Connectors page does not list it, the refresh never fetches it, the name-uniqueness check ignores it so the same name can be added again, and health checks skip it.
4. Reassignment to a good connector: Phase B of the merge already matches an existing row by clone URL and sets its `ConnectorId` to the connector that returned it, so adding or repairing a connector that can see the repository moves it over with its links, branches and Feature rows intact. Tests: (a) remove connector A, add connector B that returns the same clone URL, refresh; assert the repository now belongs to B and every link survived; (b) after the last repository has moved, the tombstone row of A is deleted (clean-up at the end of the merge). The refresh must treat a removed connector like a silent one, so 1a's positive list keeps its linked repositories.
5. Every reader that turns `Repository.Connector` into a token must treat a removed connector as "no valid connector" (one helper, for example `Connector.IsUsable`: not removed, active, healthy, token present). Local work (status, diff, stage, commit, branch, Feature worktrees) needs no connector and keeps working. Anything that needs a token (fetch, pull, push, pull requests, Actions, `GET /repos/{id}/connector` for the Worker) refuses for that repository with one message: "Repository X has no valid connector. Add or repair a connector that can access it." Per repository, never for the whole Workspace.
6. UI: the Repositories grid shows a "No connector" state for a repository whose connector was removed and a "Not found on provider" state for a kept one (tooltip names the connector and since when). The Connectors page delete confirm says how many Workspace repositories will be detached.
7. An unhealthy connector (bad token, lost SSO grant) already keeps its rows through `failedConnectorIds`; show the same per-repository state from `Connector.IsHealthy` instead of only failing at operation time. See also `docs/todo/ideas/connector-token-failure-handling.md`.
8. Update architecture `01`, `03` and `05`, and the Desktop README bullet.

Size the reader audit in step 5 before starting 1b; it decides whether 1b is one change or two.

### 2 and 3. Feature sync writes workspace-global generated package rows

Checked: `WorkspaceFileVersionService.SyncGeneratedPackageDependenciesAsync` (lines 787-895) and `WorkspaceProjectRepository.SyncGeneratedPackageDependenciesAsync`.

Confirmed. The service overlays the calling context's missing-file flags, filters `.csproj` configs on `IsMissingOnDisk != true`, and when none remain calls the repository method with an empty list. That method removes every generated row not in the desired set, so a Feature that does not have the version file on disk deletes the Workspace's generated packages and their edges. The next check from the special Workspace recreates them, so the visible effect is flapping dependency levels, unmatched counts and push order between contexts. The edge-version write (item 3) is in the same method: the version resolved for the calling context is written to every matching consumer edge, including real projects in other contexts.

Plan:
1. Failing tests: (a) special Workspace has a generated package; run the sync for a Feature context whose version file is flagged missing; assert the generated row and its edges are still there. (b) two contexts resolve different versions; assert each context's consumer edge keeps its own version.
2. Row lifecycle (create and delete of generated `WorkspaceProject` rows) is decided only by the special Workspace context. A Feature-context call never deletes generated rows and never passes an empty desired set. Simplest form: in the service, when `contextId` is not the special context, skip row creation and removal and only update edges for consumer projects that belong to that context.
3. Edge versions: in the repository method, restrict `realProjects` (the consumer side) to the calling context's projects, so a context only writes its own edges. This needs the context id as a parameter; the method takes only `workspaceId` today.
4. A failed Worker call already returns `false` without writing; keep that.
5. Update `docs/architecture/03-data-model-and-persistence.md` and `05-user-capability-reference.md` (they say generated packages are global but do not record the write path).

Read `AGENTS.md` "Feature-context scoping" before starting.

### 4. Desktop signing secrets

Checked: `GrayMoon.Desktop/.github/workflows/release.yml`. All `ESIGNER_*` uses (lines 371-374 and 496-499) are in the one `build-and-release` job, which carries the conditional `environment:` (line 219). The workflow text is right; the secrets are still readable at repository level by a `workflow_dispatch` from any branch.

No code change. Owner steps, exactly as in the note: add the secrets to the `production` environment, delete them at repository level, dispatch from a feature branch (expect an unsigned build), push or re-run a stable tag (expect the approval prompt and a signed build). If the empty `environment` value is rejected on the branch run, split signing into its own job. Record both run links.

## Should fix

### 5. All-empty refresh never purges

Same code as item 1: the merge is guarded by `allFetched.Count > 0`. After change 1a nothing is left to do here: a connector that answers with zero repositories is never on the purge list, so an all-empty refresh correctly purges nothing, and linked repositories that are gone on the provider are shown as "Not found on provider" the next time the connector returns a non-empty list. Close this item with 1a and add one test for the all-empty case.

### 6. Worker result dropped while not connected

Checked: `SignalRConnectionHostedService.SendCommandResponseAsync` (line 173), `WorkerHub.OnDisconnectedAsync`, `WorkerResponseDelivery.FailForConnection` / `FailAll`.

The note is partly out of date. The App already fails the requests of a dropped connection at once ("Worker disconnected before responding."), so the 240 s wait happens only until the App notices the disconnect, not for the whole timeout. A reconnected Worker has a new connection id and the old waiter is gone, so a resend queue would deliver to nobody. That selects the note's second option.

What is still wrong: the Worker discards the result with no log line, and the App reports a failure for work that was done (a commit that succeeded shows as failed).

Plan:
1. Test in the style of `CommandRequestIntakeTests`: produce a response while the connection is not `Connected`; assert a warning with request id and command.
2. Log at warning in `SendCommandResponseAsync` when the state is not `Connected`, and when `InvokeAsync` throws.
3. Make the App-side reason say the outcome is unknown: "Worker disconnected before responding; the command may have completed." No retry.
4. Confirm by test that the App fails the request on disconnect (`ServiceLifetimeAndConnectionPlumbingTests` already covers `FailForConnection`), then rewrite the note's symptom in architecture `04`.

### 7, 8, 9. Silent catches that change a result

Checked: all five places in the note are as described.

- **7, `PackageRegistrySyncService` line 152**: a thrown `PackageExistsAsync` becomes "does not exist". The package keeps `MatchedConnectorId = null`, and the two-lane push pre-check then says a package has no registry mapping. Fix: catch per connector, log at warning with the exception, and do not write a `null` mapping for a package where any lookup failed (leave it untouched so the next run retries). Test: one registry throws, one answers "no"; assert no write for that package and a warning.
- **8, `WorkspaceFeatureReconciler.SafeMainPathAsync` line 467**: the `null` goes straight into a worktree probe, and a `Missing` probe marks the Feature repository `NeedsRepair`. Check what the probe returns for a `null` main path; a failed lookup must mean "unknown, skip this pass", never `Missing`. Log at warning. Test: path resolver throws; assert the row's state is unchanged.
- **9, `WorkerHub` line 34**: log at warning with the exception. No behavior change.

### 10. `synchronous=NORMAL` on every connection

Checked: `Program.cs` line 137 is the only place; `SqliteConnectionPragmasInterceptor` sets `busy_timeout` and `foreign_keys` only, and its comment wrongly says `synchronous` persists.

This is performance, not correctness (`FULL` is the safer setting; each commit syncs the WAL). Fix: add `PRAGMA synchronous=NORMAL;` to both `Apply` and `ApplyAsync`, correct the comment, extend `Factory_connections_apply_busy_timeout_and_foreign_keys` in `SqlitePerformanceTests` to read `PRAGMA synchronous` (expect 1) on a factory connection, and fix the "persisted in the file" sentence in architecture `03`. Keep the startup pragma or remove it; it becomes redundant.

### 11. Commit targets frozen too early

Checked: `WorkspaceGitChanges.MultiRepo.cs`, `CommitWorkspaceAsync` and `CommitWorkspaceCoreAsync`.

`targets` is built from `_view` before the default-branch dialog and before the AI step (up to 600 s) and is used unchanged. For Commit All, a repository that became dirty in that window is not committed and nothing says so. The other half of the note is already fixed: `NothingStaged` is now reported as skipped.

Plan:
1. Rebuild the target list inside the job, right before the fan-out, from a fresh read of the persisted projection (`WorkspaceGitChangesReadService`) for the captured `committedContextId`. Load into a local; do not read `_view` from the job thread.
2. Commit All: do not prefilter on counts; send every non-tag-pinned repository of the context and let "nothing to commit" be a skip. Commit Staged: keep the staged filter, on the fresh counts.
3. Default-branch warning: if the fresh list adds a repository on its default branch that the user did not confirm, leave it out and report it as skipped with that reason. Do not commit to a default branch without the confirmation.
4. Item 23 falls out: when the fresh list differs from the set the AI described, add one terminal line naming the added and removed repositories.
5. Test with a fake read service whose answer changes between click and job start.

### 12. Browser tab writes the Desktop title

Checked: `WorkspaceTopBarService.PushToDesktopAsync` sends `WorkspaceChanged` to all Desktop clients from any circuit; `Worker.razor` line 283 probes `graymoonDesktopBridge.isAvailable` while the rest of the App uses the `GrayMoonDesktop` user-agent token (`WorkspaceOpenInRecentTools.DesktopUserAgentToken`).

Fix: gate `PushToDesktopAsync` (and the tracker write) on the same Desktop-client flag Open in uses; move `Worker.razor` to that flag. The bare `catch` there can stay but should log at debug. Extend `WorkspaceTopBarServiceTests`: a non-Desktop circuit never pushes and never changes the tracker.

### 13. Hub start hardening and tests

Checked: `WorkspaceSyncHubConnectionHelper.TryStartAsync` is one `StartAsync` in a try/catch with a debug log; no test references the helper except a source-text assertion in `ModalAndSharedUiGuardTests`.

A failed first start leaves the page without live updates until reload, and `WithAutomaticReconnect` does not cover it. Fix: bounded retry with back-off inside `TryStartAsync`, a cancellation token the page cancels in `Dispose`, a debug log with page, host, elapsed time and outcome (`started`, `cancelled by dispose`, `failed`). Inject `TimeProvider` for the back-off so tests use `ManualClock`. Tests: one per outcome, plus "cancelled start does not throw out of a render callback". Update architecture `04`.

### 14 and 15. Unproven review fixes

No production change expected.

- **14**: for the request host check, refused-start toast and Worker start without the App, revert each fix locally once and run its test. A test that still passes is rewritten. This is an hour of work and is the only way to know those tests mean anything.
- **15**: add the `MigrationsRunnerTests` case: backup file is written but `PRAGMA integrity_check` fails; assert no step runs and the message names the backup path. Needs a seam to corrupt or fake the backup check.

### 16. Damaged Worker install probe (reclassified: Optional, deferred)

Checked: no probe exists in App or Worker source.

Background: this happened once, on 2026-10-17. Two Worker installs ran at the same time (the automatic self-update after a branch switch, and a click on Install Worker eight seconds later). One deleted files the other had just extracted, so the Worker started without `git2-5853918.dll` and every repository call failed with "The type initializer for 'LibGit2Sharp.Core.NativeMethods' threw an exception" until a clean reinstall. No data was lost. The cause was fixed in the same change (#462): the install script now holds a machine-wide mutex, uses a unique zip name per run, and verifies every archive entry before installing. The probe was that investigation's optional follow-up, for a damaged install from any other cause (the investigation document was removed in #463; it is in git history at `e4385f4a:docs/worker-install/`).

Reclassified to Optional on 2026-11-11: the only known cause is closed and it has not been seen since. Do not build it now; keep the note. If it is built later, the sketch is: at Worker start, before connecting, touch the types whose initializers failed in the original investigation (LibGit2Sharp native load is the likely one; confirm from `docs/worker-install/` if that document still exists) inside one try/catch. On failure, report one state through `GetHostInfo` and the connection tracker so the App shows a single "Worker install is damaged, reinstall" message and refuses commands like `VersionMismatch` does. Do not add a per-feature capability flag. Test with an injected failing probe. Decide the reporting channel before building; this is the only item here that needs a small design.

## Optional

- **17**: `StageAndCommitCommand` line 58, log at debug or warning. One line; do it with 9.
- **18**: the three remaining test gaps. The context-flip test needs the tooltip load extracted from the page or a component harness; the hub-attach test needs the same. Worth doing when those pages are next refactored, not on their own.
- **19**: widen the convention scans together with the fixes they guard: the best-effort scan to `Hubs`, `Services/Connectors`, `Services/Features` and the Worker with items 7-9; the link-setter scan (`SyncStatus`, `OutgoingCommits`, `IncomingCommits`, `GitVersion`) on its own. Each scan starts with an explicit exception list and one deliberately wrong sample string.
- **20**: Phase 3 Worker hardening. The note itself says no log shows C-F happening. Keep the note; build a repro for D and E first, as it says.
- **21**: correlation id per commit run (small, useful for the next incident); audit of `MultiSelect`, `Discard`, `BulkSectionAction` fan-outs for shared scoped services (read-only audit, half a day); the 240 s status scan timeout needs its own investigation.
- **22**: "Continue waiting" is a feature, not a defect. Moved to `docs/todo/ideas/push-continue-waiting.md` on 2026-11-11.

## Close (note clean-up only)

- **24**: finding G is fixed. The push step uses the captured `committedContextId` (`WorkspaceGitChanges.MultiRepo.cs` line 362). Remove the row.
- **25**: `NothingStaged` is already a skip (line 252-264). Reword Phase 2 to the stale-target half only.
- **26**: the note says no change is needed. Remove the item.
- **27**: the `GitService` read-only catch stays; the following delete reports its own failure. Remove the row.

## Order of work

Each phase is one reviewable change. Stop for a check-in between phases; nothing is committed without an explicit request.

| Phase | Items | Why this order |
|---|---|---|
| A1 | 1a, 5 | Data loss on refresh; small and self-contained |
| A2 | 1b | Connector removal becomes a soft delete; one added column and a reader audit, own change |
| B | 2, 3 | Cross-context corruption; one area, one change |
| C | 4 | Owner action, independent, can happen any time (do it first if convenient) |
| D | 6, 7, 8, 9, 17, and the scan widening from 19 | All "report an outcome" fixes, same rule, same tests style |
| E | 10 | One line plus a test and a doc sentence |
| F | 11, 23 | Git Changes page; read `AGENTS.md` "Blazor page jobs" first |
| G | 12, 13 | Hub and Desktop bridge |
| H | 14, 15 | Tests only |
| Later | 16, 18, 20, 21 | Deferred |

Every fix ships with a regression test that fails on the old code. Items 1a and 6 are marked "to confirm first" in their notes: write the failing test before touching the code, and if it cannot be made to fail, stop and re-read. After each behavior change, update the architecture document named in the item and add the bullet to `GrayMoon.Desktop/README.md`. When a note is fully shipped, delete it and update `docs/todo/README.md`; the "Close" items above can be removed from their notes in phase A.

## Open questions for the owner

Answered 2026-11-11: connector state never wipes a Workspace (item 1 rewritten as 1a and 1b); "Continue waiting" moved to ideas; the damaged-install probe is deferred.

Also answered 2026-11-11: item 1b uses a soft delete (the connector row stays as a hidden tombstone while a Workspace repository still points at it), on the condition that a Workspace's repositories can be reassigned to a good connector, which the refresh does by clone URL. Unlinked catalog rows of a removed connector are deleted with it; they are only the pick list for adding repositories and come back on the next fetch.

Nothing is open.
