# Read-only (reference) repositories - test log

Test log required by the [implementation plan](read-only-repositories-implementation-plan.md). One entry per phase, newest last. Deleted when the feature ships.

All commands run from the application repository root, `C:\Workspace\GrayMoon\GrayMoon` (branch `readonly-repos`).

## Environment

| Item | Value |
|---|---|
| Operating system | Windows 11 Pro 10.0.26300 |
| .NET SDK | 10.0.401 |
| Git | 2.56.0.windows.4 |
| Base commit | `1515d32b` |

## Commands

```
dotnet build GrayMoon.slnx
dotnet test src/GrayMoon.Common.Tests/GrayMoon.Common.Tests.csproj
dotnet test src/GrayMoon.Worker.Tests/GrayMoon.Worker.Tests.csproj
dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj
dotnet test src/GrayMoon.Worker.Tests/GrayMoon.Worker.Tests.csproj --filter "Category=Long"
dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj --filter "Category=Long"
```

## Phase 0A - 2026-11-12

### Baseline, untouched tree at `1515d32b`

| Step | Result |
|---|---|
| Build | succeeded, 0 warnings, 0 errors |
| Common tests | 393 passed, 0 failed, 0 skipped (14 s) |
| Worker tests | 1007 passed, 0 failed, 1 skipped (5 m 33 s) |
| App tests | 2055 passed, 0 failed, 0 skipped (31 s) |
| `Category=Long` | not run separately on the untouched tree; the full runs above do not exclude the category |

Skipped test (skipped in the repository, not by this work): `GitVersionParityTests.Default_tip_version_matches_a_fresh_checkout_when_the_repo_is_on_a_Feature_branch_and_origin_moved_on`.

### After the 0A changes (test files and documents only, uncommitted)

| Step | Result | Change |
|---|---|---|
| Build | succeeded, 0 warnings, 0 errors | - |
| Common tests | 393 passed, 0 failed, 0 skipped (13 s) | none |
| Worker tests | 1015 passed, 0 failed, 1 skipped (3 m 26 s) | +8 (`InspectWorktreeMainCheckoutTests`) |
| App tests | 2134 passed, 0 failed, 0 skipped (31 s) | +79 (`Policy/Characterization`) |
| Worker `Category=Long` | 2 passed, 0 failed (25 s) | - |
| App `Category=Long` | 2 passed, 0 failed (9 s) | - |

The same test is skipped as in the baseline. No existing test was changed, and no production file was changed.

Focused runs:

```
dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj --filter "FullyQualifiedName~Policy.Characterization"
dotnet test src/GrayMoon.Worker.Tests/GrayMoon.Worker.Tests.csproj --filter "FullyQualifiedName~InspectWorktreeMainCheckoutTests"
```

Results: 79 passed; 8 passed.

### Not run

- The manual smoke list (owner, at the gates).
- No test was run on a second machine or operating system.

## F22 fix - 2026-11-12

Base: commit `ece83ab7` (0A committed by the owner). Production files changed: `WorkspaceBranchOperations.cs`, `WorkspaceGitService.Branches.cs`, `WorkspaceManifestService.cs`.

| Step | Result | Change |
|---|---|---|
| Build | succeeded, 0 warnings, 0 errors | - |
| App tests | 2145 passed, 0 failed, 0 skipped (42 s) | +11: 8 in `TagPinCharacterizationTests`, 3 in `WorkspaceManifestTagPinConcurrencyTests` |

Three tests named `..._does_not_clear_the_pin_today` were rewritten to assert that the pin is cleared; that is the intended behavior change. No other existing test changed.

Check that the new tests detect the defects:

- The three rewritten tests asserted the opposite (no pin change) against the unfixed code in 0A and passed there.
- With the gate in `SetRepositoryTagPinsAsync` removed, `Two_pin_changes_for_one_Workspace_do_not_overwrite_each_other` fails (expected 1 read, actual 2). The gate was restored.

Focused run:

```
dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj --filter "FullyQualifiedName~TagPin"
```

Result: 16 passed.

Not run for this fix: Worker and Common tests (no file of theirs changed; they run again at the end of 0B), and a manual check against a real Workspace repository.

## Phase 0B - 2026-11-12

Base: commit `92363c3c` (F22 fix committed by the owner). Not committed.

| Step | Result | Change from the F22 run |
|---|---|---|
| Build (`dotnet build GrayMoon.slnx`) | succeeded, 0 warnings, 0 errors | - |
| Common tests | 393 passed, 0 failed, 0 skipped (13 s) | - |
| Worker tests | 1015 passed, 0 failed, 1 skipped (4 m 3 s) | same skipped test as the baseline |
| App tests | 2748 passed, 0 failed, 0 skipped (32 s) | +603, all new policy tests (most are theory cases) |
| Worker `Category=Long` | 2 passed, 0 failed (25 s) | - |
| App `Category=Long` | 2 passed, 0 failed (10 s) | - |

New test files: `Policy/RepositoryOperationPolicyMatrixTests.cs`, `Policy/WorkspaceRepositoryOperationPolicyServiceTests.cs`, `Policy/RepositoryOperationPolicyExpressionsTests.cs`, `Policy/RepositorySkipReportingTests.cs`, `Conventions/RepositoryPolicyConventionTests.cs`.

The 0A characterization tests (`Policy/Characterization`, 87 tests) are unchanged and pass.

Existing tests changed, none in what they assert:

- `FeatureContextBranchRefreshScopingTests.cs`: four calls to the removed `GetTagPinnedRepositoryIdsAsync` now call `GetSkippedRepositoryIdsAsync` for Restore Packages; the expected values are the same.
- `BranchRowSaveRaceTests.cs`, `SqlitePerformanceTests.cs`: one more constructor argument (the policy) where `WorkspacePushService` is built by hand.
- `SyncStateTestContext.cs`, `WorkspaceB4ContextLeakTests.cs`, `WorkspaceProjectRepositoryContextScopingTests.cs`, `WorkspaceProjectRepositoryGeneratedPackageTests.cs`: the policy service is registered; `WorkspaceProjectRepositoryContextScopingTests.cs` also registers a context factory for it.

Focused runs:

```
dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj --filter "FullyQualifiedName~GrayMoon.App.Tests.Policy|FullyQualifiedName~RepositoryPolicyConventionTests"
dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj --filter "FullyQualifiedName~Policy.Characterization"
```

Results: 690 passed; 87 passed.

### Two existing tests that fail together

During 0B the full App run failed twice with one test each, and passed on the next run:

- `AiAndGitChangesSweepTests.Cancel_racing_the_end_of_a_run_never_throws`
- `BackgroundJobRefusedStartTests.Workspace_job_refused_by_the_lock_reports_not_started`

Each passes alone. Run together with a filter they fail in most runs:

```
dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj --filter "FullyQualifiedName~AiAndGitChangesSweepTests|FullyQualifiedName~BackgroundJobRefusedStartTests"
```

The same command fails in the same way on an export of commit `ece83ab7` (built with `-p:DisableGitVersionTask=true`, 3 of 3 runs), so the cause is older than 0B and is not in a file 0B changed. The cause was not investigated.

### Not run

- No check that each new test detects a defect by breaking the code on purpose, except what the tests show by themselves: the convention test failed with the full list of readers before the allow-list was filled in, and the service tests failed where a test host had no policy registered.
- The manual smoke list (owner, at the gates).
- No test was run on a second machine or operating system.

## 0B follow-ups - 2026-11-12

Base: commit `459c120c`. Not committed. Plan: `read-only-repositories-0b-follow-ups.md`, steps 1 and 2 of "Resulting order".

### Mutation checks

Each change was applied alone, the whole App suite was run, and the file was restored. "Caught" means at least one test failed that does not fail without the change.

| # | Change | Result | Tests that failed |
|---|---|---|---|
| 1 | Rule table: Push allowed on a tag | caught, 34 failed | matrix, counter and push characterization, push strategy, service and skip tests |
| 2 | Rule table: Sync not allowed on a tag | caught, 8 failed | `Every_operation_answers_as_it_did_before_the_policy` only. No service asks the policy for Sync. |
| 5a | Loader: a Feature without a state row reads the link | caught, 3 failed | service tests, Feature restore characterization |
| 5b | Loader: a Feature never reads its state rows | caught, 6 failed | service tests, restore characterization, `FeatureContextBranchRefreshScopingTests` |
| 7a | `DependencyUpdateOrchestrator`: source changed to `Context` | **not caught** | none |
| 7b | `WorkspaceGitService.Projects.cs`: source changed to `Context` | caught, 2 failed | Feature update plan characterization |
| 7c | `WorkspaceGitService.Commit.cs`: source changed to `Context` | caught, 1 failed | Feature commit characterization |
| 8 | Push plan: source changed to `Context` | **not caught** | none |
| 9 | Query form: a tag is "not null" only | caught, 40 failed | `RepositoryOperationPolicyExpressionsTests` |
| 10 | Header query: one `ExpandRepositoryPolicy()` removed (each of the four) | caught, 6 to 13 failed | counter characterization, header state tests |
| 12 | `[JsonIgnore]` removed from `OperationResult.Skipped` | caught, 1 failed | `Skips_are_not_part_of_the_REST_response` |

Closing 7a and 8:

- The source of the update path and of the push plan is now one constant each (`RepositorySubjectSources.UpdatePath`, `.PushPlan`), used by every call site.
- New `Policy/RepositoryPolicySubjectSourceTests.cs`, 3 tests: the Workspace push plan reads a state row before the link (both directions), and a Feature update run names a repository the Workspace has on a tag.
- Run again against the constants: `UpdatePath` changed to `Context` fails 4 tests; `PushPlan` changed to `Context` fails 1 test.

Not covered: a call site that stops using the constant and names another source itself. A full dependency-aware update run of a Feature is not tested end to end; the test host has no Worker answer for `RefreshRepositoryProjects`.

### The two tests that failed together

Command, 10 or 20 runs each:

```
dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj --no-build --filter "FullyQualifiedName~AiAndGitChangesSweepTests|FullyQualifiedName~BackgroundJobRefusedStartTests"
```

| Tree | `Workspace_job_refused_by_the_lock_reports_not_started` | `Cancel_racing_the_end_of_a_run_never_throws` |
|---|---|---|
| Both fixes | 0 of 20 failed | 0 of 20 failed |
| Lock fix undone | 10 of 10 failed | 0 of 10 failed |
| Test change undone | 0 of 10 failed | 9 of 10 failed |

Causes:

- Lock: `WorkspaceOperationRunner.RunAsync` marked the operation completed (which raises `Changed`) and released the lock afterwards. The test waits for "completed" and starts the next job at once. Fixed in the product: the lock is released first.
- AI run: the test expected the run to finish inline on the completing thread. Under the test runner the run was started with a synchronization context, so it finished later. Fixed in the test: the run is started from a pool thread.

### Suites after the changes

| Step | Result |
|---|---|
| Build (`dotnet build GrayMoon.slnx`) | succeeded, 0 warnings, 0 errors |
| App tests, 5 runs | 2751 passed, 0 failed in each (+3: `RepositoryPolicySubjectSourceTests`) |
| App `Category=Long` | 2 passed |
| Common tests | 393 passed |
| Worker tests | 1015 passed, 1 skipped (same test as the baseline) |
| Worker `Category=Long` | 2 passed |

### Not run

- Mutations 3, 4, 6, 11, 13, 14, 15 (planned for later; 14 needs a new test first).
- The manual smoke list.

## Phase 0C and 0D - 2026-11-12

Base: commit `459c120c` plus the uncommitted follow-up changes above. Not committed.

### 0C gate

Automated part: the "Suites after the changes" table above (build, Common, Worker, App five times, both `Long` runs), all passed. Compared with the 0A baseline: Common and Worker totals are the same and the same Worker test is skipped; App grew only by tests added in F22, 0B and the follow-ups.

Manual smoke list M1-M8: **not run** (needs a running App and Worker and a person).

### 0D

| Step | Result | Change |
|---|---|---|
| Build (App tests project) | succeeded, 0 warnings, 0 errors | - |
| App tests | 2750 passed, 0 failed, 0 skipped (20 s) | -1: `The_legacy_source_judges_a_Feature_by_the_Workspace_link` deleted with the source it tested |

Tests changed on purpose (each asserted the old behavior and passed against it before the change):

- `ServiceGapCharacterizationTests`: `Pull_of_a_tag_pinned_repository_is_refused_and_a_level_pull_leaves_it_out`, `Update_branch_from_default_of_a_tag_pinned_repository_is_refused`, `Single_return_to_default_of_a_tag_pinned_repository_is_refused` now assert no Worker call and the tag message. The other tests of the class are renamed from `..._has_no_service_side_tag_check_today` to `..._is_allowed_on_a_tag`; what they assert is unchanged.
- `UpdateCharacterizationTests`: the three Feature tests are reversed (`Feature_update_plan_keeps_a_repository_only_the_Workspace_has_on_a_tag`, `Feature_update_plan_leaves_out_a_repository_the_Feature_has_on_a_tag_while_the_Workspace_is_on_a_branch`, `Feature_commit_keeps_a_repository_only_the_Workspace_has_on_a_tag_and_drops_one_the_Feature_has_on_a_tag`).
- `RepositoryPolicySubjectSourceTests`: the Feature update run now names what the Feature has on a tag and not what the Workspace has.

Not run for 0D: Common and Worker tests (no file of theirs changed), the manual smoke list.

## Phase 1, first group (WP1, WP4-WP7) - 2026-11-12

Base: commit `d0f00d30` plus uncommitted changes (69 changed or new files). Windows 11, git 2.56.0.windows.4, .NET SDK 10.0.401. Not committed.

Commands, from the repository root:

```powershell
dotnet build GrayMoon.slnx
dotnet test src/GrayMoon.Common.Tests/GrayMoon.Common.Tests.csproj
dotnet test src/GrayMoon.Worker.Tests/GrayMoon.Worker.Tests.csproj
dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj
dotnet test src/GrayMoon.Worker.Tests/GrayMoon.Worker.Tests.csproj --filter "Category=Long"
dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj --filter "Category=Long"
```

| Step | Result |
|---|---|
| Build | succeeded, 0 warnings, 0 errors |
| Common tests | 393 passed, 0 failed |
| Worker tests | 1023 passed, 0 failed, 1 skipped (`GitVersionParityTests.Default_tip_version_matches_a_fresh_checkout_when_the_repo_is_on_a_Feature_branch_and_origin_moved_on`, the same test as the baseline) |
| App tests | 4308 passed, 0 failed, 0 skipped |
| Worker `Category=Long` | 2 passed |
| App `Category=Long` | 2 passed |

Each suite was run once, not five times.

New tests:

- `GrayMoon.App.Tests/Policy/ReferenceRepositoryEnforcementTests` (16): push, pull-only flag per repository, Git Changes commit refused before the Worker (Workspace and Feature snapshot), stage and unstage still sent, the Workspace-role repository exempt, bulk create branch, Workspace-wide checkout, pull request create and merge, Feature checkout guard, state write of a snapshot, snapshot branch expectations, Remove Feature plan, commit target planner.
- `GrayMoon.App.Tests/Policy/RepositorySkipReportingTests` (+3): the skip summary sentence and how `ShowRepoErrors` reports it.
- `GrayMoon.Worker.Tests/CommitSyncPullOnlyTests` (8): fast-forward pull, local commit refused and never pushed, the same commit pushed without `pullOnly`, compatible and conflicting local changes, checkout `commit` and the local-changes refusal. New scenario `GitScenarios.CloneBehindRemote`.

Test changed on purpose: `RepositorySkipReportingTests.Skips_are_not_part_of_the_REST_response` became `Skips_are_part_of_the_REST_response_with_the_reason_by_name`. It asserted the 0B behavior (skips hidden from REST) and passed against it before the change.

### Not run

- The manual smoke list.
- Mutation checks.
- No test was written for: the read-only handling of Prepare Workspace as a whole, the dependency update and version-file skip, the Actions page run and rerun guards, the Git Changes page logic (bulk section actions, Discard All, the `ro` badge), the AI commit message scope, Create Feature with a read-only Source, Remove Feature on a snapshot end to end, and the REST endpoint bodies. These paths are covered by the build only.

## Phase 1, first group: leftovers - 2026-11-12

Base: commit `d0f00d30` plus uncommitted changes. Same machine and commands as the section above. Not committed.

| Step | Result |
|---|---|
| Build | succeeded, 0 warnings, 0 errors |
| Common tests | 393 passed, 0 failed |
| Worker tests | 1027 passed, 0 failed, 1 skipped (the same baseline test) |
| App tests | 4314 passed, 0 failed, 0 skipped |
| Worker `Category=Long` | 2 passed |
| App `Category=Long` | 2 passed |

Each suite was run once.

New tests:

- `ReferenceRepositoryEnforcementTests` (+3, 19 in all): Return to Default of one read-only repository is a `CheckoutBranch` of the default branch with no `ReturnToDefaultBranch` and no `DeleteBranch` call, even when deletion was asked for; bulk Return to Default names it in `Skipped`; projects are still scanned while the update plan and the dependency sync leave it out.
- `PackageWaitTimeoutTests` (+3): the timeout message names one or several read-only producers and `Report` passes them on.
- `InspectWorktreeMainCheckoutTests` (+4, real git): `headOnRemote` is null with an upstream, true for a branch without upstream on a pushed commit, false with a local commit, and tells the two detached cases apart.

### Not run

- The manual smoke list and mutation checks.
- No test for: the Repositories page code that routes a read-only row to the plain checkout (page logic), `PackageProducerDiagnostic.IsReference` being set from the link and the `ReadOnly` status, `ReturnToDefaultRepositoryPlan.IsReference` from the analysis, and the pipelined run's `Skipped`.

## Phase 1, second group (WP2-WP3) and the activation rule - 2026-11-12

Base: commit `d0f00d30` plus uncommitted changes. Same machine and commands as the first group. Not committed.

| Step | Result |
|---|---|
| Build | succeeded, 0 warnings, 0 errors |
| Common tests | 446 passed, 0 failed |
| Worker tests | 1034 passed, 0 failed, 1 skipped (the same baseline test) |
| App tests | 4364 passed, 0 failed, 0 skipped |
| Worker `Category=Long` | 2 passed |
| App `Category=Long` | 2 passed |

Each suite was run once after the last code change.

Failures on the way, both fixed before the run above:

- `WorkspaceManifestFileTests.Parse_rejects_schema_version_2` failed as expected once version 2 became readable. It is now `Parse_rejects_schema_version_3`, with `Parse_accepts_schema_version_2` beside it.
- The two new `AddRepositoriesAsync_gives_a_new_repository_the_read_only_default...` cases failed twice because of the test's own setup of the shared circuit context. The second attempt led to one product change: the default is read as a value from the database.

New tests:

- Common `WorkspaceManifestReferenceTests` (29 cases): the note's sample round trips as version 2 byte for byte; property order; default true with one writable Source; no read-only data stays version 1; `"yes"`, `1` and `""` are rejected with the property name, on the repository and on the workspace; `codegraph` keeps its message; `reference` or `branch` in a version-1 file is refused; the revision rules; `ReadPinnedRevision`.
- Common `WorkspaceDefinitionRevisionPinTests` (23 cases).
- App `WorkspaceManifestReferenceTests` (14): what is written from the database; branch and commit kept for a read-only repository and dropped for a writable one; a tag pin survives turning read-only off; recording, repeating, replacing a tag pin and clearing a revision; a writable entry is refused without a write; drift names a flag difference, reports a default difference, ignores branch and commit.
- App `ReferenceRestoreTests` (8): the mixed sample restores each state and leaves the file alone; workspace value only; default false with one read-only Source; version 1; read-only data under version 1 creates nothing; the import guard keeps a folder with unpushed commits writable, does not inspect writable repositories, and lets uncommitted changes pass.
- App `WorkspaceRepositoryReplaceTests` (+5): a new Source takes the default through the editor and through import while the existing one keeps the opposite state; saving the same membership changes nothing.
- App `ReferenceActivationTests` (23): the rule and the checker (first reported in the previous check-in message, recorded here).
- Worker `SyncRepositoryTagPinTests` (+6, real git): a fresh clone of a read-only repository is on its branch at the recorded commit, tracks `origin`, and is one commit behind with nothing outgoing; the workspace value applies; a branch the remote no longer has gets no upstream; an existing checkout is not moved; a missing commit and a branch on a writable entry fail the sync and remove the clone.
- Worker `InspectWorktreeMainCheckoutTests` (+1): a main checkout folder that does not exist reports `Repository not found.`.

### Not run

- The manual smoke list and mutation checks.
- No test for: the Workspaces page calling the import guard and showing the warning (page code); `GitService.CheckoutBranchAtCommitAsync` failing at the set-upstream step.
- Restore was tested against the fake Worker in the App and against real git in the Worker separately; no test runs a restore end to end through both.

## Phase 1, third group (WP8-WP9) - 2026-11-12

Base: commit `4b6590bf` plus uncommitted changes. Same machine and commands as before. Not committed.

| Step | Result |
|---|---|
| Build | succeeded, 0 warnings, 0 errors |
| Common tests | 446 passed, 0 failed |
| Worker tests | 1034 passed, 0 failed, 1 skipped (the same baseline test) |
| App tests | 4390 passed, 0 failed, 0 skipped |
| Worker `Category=Long` | 2 passed |
| App `Category=Long` | 2 passed |

Each suite was run once after the last code change.

Failure on the way, fixed before the run above:

- `State_reports_none_mixed_and_all_and_leaves_the_Workspace_repository_out` gave two links the Workspace role, which the database refuses. The no-Sources case now asks for a Workspace without links.

New tests:

- App `WorkspaceReferenceOperationsTests` (23): the state; one blocked repository of two refuses the whole change, nothing is saved, written or broadcast; a successful change writes the definition and then records the checked HEAD; the default with and without the batch; making writable runs no check; a default-only change touches no link; one repository alone and the Workspace repository refused; no Worker; a definition that cannot be written; the change refused while a context operation holds the lock; a context operation refused while the change runs (signals, no timing); recording after a pull and after a manual switch; nothing for a writable repository, a failed pull, a Feature, or a Workspace without a Workspace repository; a failed write fails neither the pull nor the switch; a hook report and a Sync with a new head write nothing.
- App `CounterCharacterizationTests` (+3): header counters for a read-only repository in both projections of the special Workspace, and the notification with its panel sets - incoming only.

### Not run

- The manual smoke list and mutation checks. Nothing in this group was looked at in a browser.
- No test for: the Razor changes (section, badges, dialog lines, the Workspaces and Repositories pages passing the new parameters); the header counters of a read-only snapshot in a Feature; two pulls recording at the same moment (the queue and the single write were not exercised in parallel).

## Phase 2 - 2026-11-12

Base: commit `fb8df758` (the third group of Phase 1) plus the uncommitted Phase 2 changes. Same machine and commands as before. Not committed.

| Step | Result |
|---|---|
| Build | succeeded, 0 warnings, 0 errors |
| Common tests | 446 passed, 0 failed |
| Worker tests | 1034 passed, 0 failed, 1 skipped (the same baseline test) |
| App tests | 4413 passed, 0 failed, 0 skipped (4390 before) |
| Worker `Category=Long` | 2 passed |
| App `Category=Long` | 2 passed |

Each suite was run once after the last code change. No test failed on the way.

New tests (23):

- App `ReferencePerRepositoryTests` (20 cases): `ro:yes` and `ro:no` split 30 repositories 7 / 23 and are not case sensitive; another value matches nothing; an unknown field matches everything; `ro:` combined with a text term; none / some / all for 0, 1, 19 and 20 of 20; the Workspaces list leaves the Workspace repository out; the drift text with names, the default, and more than 5 names; the Restore preview text for six combinations; where the per-repository control is offered.
- App `WorkspaceReferenceOperationsTests` (+2): one of twenty repositories made read-only changes no sibling and not the default, and only that one is checked; with all twenty read-only and the default on, one made writable changes no sibling and not the default; setting a repository to the state it has changes nothing and checks nothing.
- App `ReferenceRestoreTests` (+1): a definition with default true and one writable repository is previewed as "Some repositories, and new repositories", restored with that repository writable and the default on, and listed as "Some".
- No assertion of an older test was changed.

### Not run

- The manual smoke list M1-M13 and mutation checks. Nothing in Phase 2 was looked at in a browser.
- No test for: the Razor markup (checkbox, column, banner text, preview row); the checkbox being recreated after a refusal; the second drift comparison of the page (Worker read, no-Worker path); `ro:` in a Feature view.

## Badge tooltips (Appendix B, P2) - 2026-11-12

Base: commit `fb8df758` plus the uncommitted Phase 2 changes. Change: tooltip text of the `archived` and `ro` badges, one new file with the texts. Not committed.

| Step | Result |
|---|---|
| Build | succeeded, 0 warnings, 0 errors |
| App tests | 4413 passed, 0 failed, 0 skipped |

### Not run

- Common tests, Worker tests and both `Category=Long` runs were **not run again** after this change. Their last run is the Phase 2 run above. The change touches only the App project (Razor markup and one class of constants), but the full command list must run on the final tree for the reference-mode gate.
- No test reads the tooltips. Not looked at in a browser.

## Hint for archived repositories (Appendix B, P3) - 2026-11-12

Base: commit `8e3b30f1` (Phase 2 and the badge tooltips) plus this change. Not committed. This is a full run, so it also covers the tooltip change above.

| Step | Result |
|---|---|
| Build | succeeded, 0 warnings, 0 errors |
| Common tests | 446 passed, 0 failed |
| Worker tests | 1034 passed, 0 failed, 1 skipped (the same baseline test) |
| App tests | 4415 passed, 0 failed, 0 skipped (4413 before) |
| Worker `Category=Long` | 2 passed |
| App `Category=Long` | 2 passed |

Each suite was run once after the last code change.

Failure on the way, fixed before the run above:

- Build error RZ1010 in `WorkspaceReferenceSection.razor`: `@{ ... }` inside a code block. Changed to a plain statement.

New tests:

- App `WorkspaceReferenceOperationsTests` (+2): the state marks an archived repository and suggests read-only for it, not for the other one; after it is made read-only (the usual check ran once) it is no longer suggested. An archived repository with unpushed commits is refused, stays writable and stays suggested.

### Not run

- The manual smoke list M1-M13. Nothing was looked at in a browser.
- No test for the two callouts as markup, or for the button in Edit Workspace.

## Archived push rule, un-archive check, `readonly` badge (Appendix B.9) - 2026-11-12

Base: commit `8e3b30f1` plus the uncommitted hint change (B.8) and this change. Not committed.

| Step | Result |
|---|---|
| Build | succeeded, 0 warnings, 0 errors |
| Common tests | 446 passed, 0 failed |
| Worker tests | 1034 passed, 0 failed, 1 skipped (the same baseline test) |
| App tests | 4641 passed, 0 failed, 0 skipped (4415 before) |
| Worker `Category=Long` | 2 passed |
| App `Category=Long` | 2 passed |

Each suite was run once after the last code change.

Failure on the way, fixed before the run above:

- Build errors CS0103 in the new test file (missing usings for `WorkspaceCapabilities` and `WorkspaceType`).

New tests, App `ArchivedRepositoryPolicyTests` (226 cases):

- Policy: push of an archived repository is skipped in a bulk action and refused for one repository, with the archived reason and message. Only push reads the archived state. The rule holds for the Workspace repository; a tag is reported before archived.
- Query form: for every operation, tag, role and read-only combination of an archived repository, the expanded expression and the method agree with the policy.
- Header: with and without the dependency graph, push is recommended before the mark and not after; incoming commits stay.
- Service: a bulk push partition leaves the archived repository out and the summary names it ("1 archived repository skipped: ..."); pull skips nothing; the single evaluation is refused.
- Link and list item carry the archived state into the policy.
- Check: a repository the provider reports as not archived is unmarked and is eligible for push again, the other one stays marked. No answer or a failed request keeps the mark. With no marked repository the provider is not asked, and the check never sets the mark.

Changed tests: `RepositoryOperationPolicyExpressionsTests` passes the new sixth argument (4 call sites).

### Not run

- The manual smoke list M1-M13. Nothing was looked at in a browser.
- No request to GitHub: the real probe is not tested.
- No test for the page trigger of the check, the tooltip, the toast or the badge markup.

## Wording "read only" and the Edit Workspace fix (Appendix B.10) - 2026-11-12

Base: commit `ee992ac0`. Not committed.

| Step | Result |
|---|---|
| Build | succeeded, 0 warnings, 0 errors |
| Common tests | 446 passed, 0 failed |
| Worker tests | 1034 passed, 0 failed, 1 skipped (the same baseline test) |
| App tests | 4641 passed, 0 failed, 0 skipped |
| Worker `Category=Long` | 2 passed |
| App `Category=Long` | 2 passed |

Failures on the way:

- Reported by the owner: Edit Workspace did not open (`ToastService` not registered for `WorkspaceReferenceSection`). Fixed by injecting `IToastService`.
- Build error CS1705 after that fix: the App had restored EF Core 10.0.15 and the App test project 10.0.14 (floating `10.0.*`). `dotnet restore GrayMoon.slnx --force-evaluate` fixed it. No project file changed.

Changed tests: the expected texts in 9 test files now say "read only" (no new tests).

### Not run

- The manual smoke list M1-M13. Nothing was looked at in a browser, also not the fixed Edit Workspace dialog.

## Overlay, parallel activation check, checkbox removed (Appendix B.11) - 2026-11-12

Base: commit `ee992ac0` plus the uncommitted wording change (B.10). Not committed.

| Step | Result |
|---|---|
| Build | succeeded, 0 warnings, 0 errors |
| Common tests | 446 passed, 0 failed |
| App tests | 4642 passed, 0 failed, 0 skipped (4641 before) |
| App `Category=Long` | 2 passed |
| Worker tests, Worker `Category=Long` | not run again: no Worker file changed since the run of B.10 |

New test: App `ReferenceActivationTests` (+1): two repositories are inspected at the same time (each answer waits until both requests are open), the findings are in name order, and progress is reported as 0 of 2 up to 2 of 2.

### Not run

- The manual smoke list M1-M13. Nothing was looked at in a browser, also not the overlay in Edit Workspace.
- No measurement of the check on a real Workspace.

## Switch Branch dialog: button in place of the checkbox (Appendix B.12) - 2026-11-12

Base: commit `ee992ac0` plus the uncommitted changes of B.10 and B.11. Not committed. One Razor file changed.

| Step | Result |
|---|---|
| Build | succeeded, 0 warnings, 0 errors |
| App tests | 4642 passed, 0 failed, 0 skipped |
| Common, Worker, both `Category=Long` | not run again: only `SwitchBranchModal.razor` changed |

No new test. Nothing was looked at in a browser.

## Progress texts, no overlay for the default switch (Appendix B.13) - 2026-11-12

Base: commit `ee992ac0` plus the uncommitted changes of B.10 to B.12. Not committed.

| Step | Result |
|---|---|
| Build | succeeded, 0 warnings, 0 errors |
| App tests | 4642 passed, 0 failed, 0 skipped |
| Common, Worker, both `Category=Long` | not run again: three App files changed, texts and one overlay condition |

No new test; the parallel check test reads the texts through `ProgressMessage`. Nothing was looked at in a browser.

## Overlay terminal for the check (Appendix B.15) - 2026-11-12

One Razor line changed. Build: 0 warnings, 0 errors. App tests: 4642 passed. Other suites not run again. No new test; nothing was looked at in a browser.

## Lock / Unlock button and title badge (Appendix B.16) - 2026-11-12

One Razor file changed. Build: 0 warnings, 0 errors. App tests: 4642 passed. Other suites not run again. No new test; nothing was looked at in a browser.

### B.17 Padlock in place of the `readonly` badge (2026-11-12)

- `dotnet build GrayMoon.slnx`: 0 warnings, 0 errors.
- `dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj --no-build`: 4642 passed.
- Common, Worker and the Long runs: not run (only Razor markup in the App changed).
- Browser: not run. M1-M13: not run.

### B.18 Keyhole, white hover underline (2026-11-12)

- `dotnet build GrayMoon.slnx`: 0 warnings, 0 errors.
- `dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj --no-build`: 4642 passed.
- Common, Worker and the Long runs: not run (only Razor markup and CSS in the App changed).
- Browser: not run. M1-M13: not run.

### B.19 Padlock in Git Changes, hover color in the other grids (2026-11-12)

- `dotnet build GrayMoon.slnx`: 0 warnings, 0 errors.
- `dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj --no-build`: 4642 passed.
- Common, Worker and the Long runs: not run (only Razor markup and CSS in the App changed).
- Browser: not run. M1-M13: not run.

### B.20 Sync on a tag asks the policy (2026-11-12)

- `dotnet build GrayMoon.slnx`: 0 warnings, 0 errors.
- `dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj --no-build`: 4814 passed (4642 before; the matrix tests grew with the two new operations).
- `dotnet test src/GrayMoon.Common.Tests/GrayMoon.Common.Tests.csproj --no-build`: 446 passed.
- Worker and the Long runs: not run (no Worker change).
- Browser: not run. M1-M13: not run.

## Reference-mode gate (plan section 10) - 2026-11-12

Tree: `0a9fe3d4` plus uncommitted changes (the documentation of section 11 and one new test). Windows 11, from the repository root.

### 1. Full command list

| Command | Result |
|---|---|
| `dotnet build GrayMoon.slnx` | 0 warnings, 0 errors |
| `dotnet test src/GrayMoon.Common.Tests/GrayMoon.Common.Tests.csproj --no-build` | 446 passed |
| `dotnet test src/GrayMoon.Worker.Tests/GrayMoon.Worker.Tests.csproj --no-build` | 1034 passed, 1 skipped (the same skip as the baseline) |
| `dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj --no-build` | 4814 passed |
| `dotnet test src/GrayMoon.Worker.Tests/GrayMoon.Worker.Tests.csproj --no-build --filter "Category=Long"` | 2 passed |
| `dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj --no-build --filter "Category=Long"` | 2 passed |

After the run one test was added (`Create_holds_a_read_only_repository_as_a_detached_snapshot_of_its_commit`). Build again: 0 warnings, 0 errors. App: 4815 passed. App `Long`: 2 passed. Common and Worker were not run again (no change there).

### 2. Characterization tests of 0A

All in `Policy/Characterization` pass in the App run above. The ones changed on purpose are listed under "0D" and "B.20" (the convention test lost its two OPEN entries).

### 3. Mapping of the required tests (idea note, "Constraints")

| Required | Test | Covered |
|---|---|---|
| Local edits: warning | `ReferenceActivationTests.Uncommitted_changes_are_a_warning_and_never_hide_a_block`; `ReferenceRestoreTests.An_imported_folder_with_only_uncommitted_changes_stays_read_only` | yes |
| Local edits: manual stage and unstage | `ReferenceRepositoryEnforcementTests.Staging_and_unstaging_in_a_read_only_repository_stay_possible` | yes |
| Local edits: manual Undo (discard) | `RepositoryOperationPolicyMatrixTests.A_reference_repository_answers_as_the_design_says` (the rule only) | in part: discard has no service, the page calls the Worker client; no test drives the page |
| Unpushed commits: activation refused | `ReferenceActivationTests.Unpushed_commits_block_and_are_counted`, `Unpushed_commits_in_the_Workspace_checkout_block_and_name_the_repository`; `WorkspaceReferenceOperationsTests.One_blocked_repository_refuses_the_whole_change_and_nothing_is_written` | yes |
| Absent upstream: activation refused | `ReferenceActivationTests.A_branch_without_upstream_passes_only_when_its_commit_is_on_the_remote`, `A_branch_whose_upstream_state_is_unknown_blocks`, `An_unknown_ahead_count_blocks` | yes, with the decided difference: no upstream passes when the commit is on the remote |
| Fully pushed branch ahead of default: allowed, no extra warning | `ReferenceActivationTests.A_clean_branch_level_with_its_upstream_passes_without_a_message` | in part: the rule does not read the default branch, so no test sets "ahead of default" |
| Switch of a read only repository to another branch: allowed, stays read only, no commit or push | `WorkspaceReferenceOperationsTests.A_manual_switch_of_a_read_only_repository_records_the_branch_and_commit`; Worker `CommitSyncPullOnlyTests.A_checkout_reports_the_commit_it_arrived_at` | yes |
| Feature worktree with unpublished commits: activation refused | `ReferenceActivationTests.Unpushed_commits_in_a_Feature_worktree_block_although_the_Workspace_checkout_is_clean` | yes |
| Fixed Feature snapshots and provenance | `CreateFeatureParentBranchTests.Create_holds_a_read_only_repository_as_a_detached_snapshot_of_its_commit` (new); `ReferenceRepositoryEnforcementTests.A_Feature_refuses_every_checkout_in_a_read_only_snapshot`, `A_state_write_keeps_a_read_only_snapshot_without_a_branch_and_in_sync`, `A_snapshot_has_no_expected_branch_and_is_never_off_its_Feature_branch`, `Removing_a_Feature_treats_an_unchanged_snapshot_as_nothing_to_lose` | yes; "a pull in the Workspace does not move the snapshot" has no test of its own |
| Safe checkout conflicts | Worker `CommitSyncPullOnlyTests.A_checkout_blocked_by_local_changes_says_so_and_changes_nothing`, `Local_changes_to_an_incoming_file_block_the_pull_and_stay_as_they_are`, `Local_changes_that_do_not_touch_the_incoming_files_do_not_block_the_pull` | yes |
| Service-side write rejection | `ReferenceRepositoryEnforcementTests` (push, Git Changes commit, branch create, Workspace checkout, pull requests, Return to Default, workspace commit); `RepositorySkipReportingTests` | yes |
| Complete graph with read only producers | `ReferenceRepositoryEnforcementTests.The_projects_of_a_read_only_repository_are_still_scanned_but_it_is_never_an_update_target` | yes |
| Graph: missing registry packages | `DotNet_synchronized_push_is_refused_when_a_required_package_has_no_registry`, `Push_lane_reports_a_level_error_and_stops_when_a_required_package_has_no_registry` (not specific to read only) | in part: no test has a read only producer whose package is missing from the registry |
| Mode toggle against a running operation | `WorkspaceReferenceOperationsTests.The_change_is_refused_while_an_operation_holds_the_lock_of_a_context` | yes |
| Operation against a running mode toggle | `OperationLockCharacterizationTests.Rest_write_is_refused_with_409_while_a_workspace_operation_runs` (any structural operation) | in part: no test starts the mode change itself and then an operation |
| Default true with one writable Source survives serialization and Restore | Common `WorkspaceManifestReferenceTests.A_default_of_true_with_one_writable_repository_round_trips`; `ReferenceRestoreTests.A_default_of_true_with_one_writable_repository_is_previewed_and_restored_as_mixed` | yes |
| Changing only the default changes no Source | `WorkspaceRepositoryReplaceTests.Changing_only_the_read_only_default_changes_no_repository`; `WorkspaceReferenceOperationsTests.A_default_only_change_touches_no_repository_and_asks_the_Worker_nothing` | yes |
| Toggling one Source changes no sibling | `WorkspaceReferenceOperationsTests.Toggling_one_of_twenty_repositories_changes_no_sibling_and_not_the_default` | yes |
| GrayMoon never commits or pushes a read only Source | Worker `CommitSyncPullOnlyTests.A_local_commit_alone_is_never_pushed`, `A_branch_with_a_local_commit_is_refused_and_neither_side_moves`; `ReferenceRepositoryEnforcementTests.A_push_leaves_the_read_only_repository_out_and_pushes_the_writable_one`, `A_Git_Changes_commit_in_a_read_only_repository_is_refused_before_the_Worker` | yes |

### 4. Manual smoke M1-M13

Not run. It is for the owner to run.

### 5. Result

**The gate is not passed.** Open: the manual smoke (item 4), and the four rows marked "in part" above (discard through the page, "ahead of default", a read only producer with a package missing from the registry, an operation started while a mode change runs). The automated suites all pass.

### 6. The four rows marked "in part": closed (2026-11-12, later the same day)

This replaces the "in part" answers of the table in item 3.

| Required | Test | Covered |
|---|---|---|
| Local edits: manual Undo (discard) | `WorkspaceCommitTargetPlannerTests.Undo_across_all_repositories_leaves_a_read_only_repository_out_and_names_it`, `Undo_across_only_read_only_repositories_has_no_target`, `Undo_on_one_named_read_only_repository_is_allowed_by_the_policy_and_Undo_across_all_is_not` | yes, for the decision. The decision moved from the Changes page into `WorkspaceDiscardTargetPlanner.PlanAll`; the page calls it. The page itself (dialog, Worker call) is still not driven by a test |
| Fully pushed branch ahead of default: allowed, no extra warning | `ReferenceActivationTests.A_fully_pushed_branch_ahead_of_the_default_branch_passes_without_a_warning` | yes |
| Graph: missing registry packages, read only producer | `WorkspacePushStrategyTests.DotNet_synchronized_push_never_pushes_a_read_only_producer_whose_package_has_no_registry` | yes |
| Operation against a running mode toggle | `WorkspaceReferenceOperationsTests.No_operation_can_start_in_a_context_while_the_change_runs` | yes. The test was already there; the mapping missed it |

Commands, after these changes:

- `dotnet build GrayMoon.slnx`: 0 warnings, 0 errors.
- `dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj --no-build`: 4820 passed.
- `dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj --no-build --filter "Category=Long"`: 2 passed.
- Common and Worker: not run again (no change there since the full run above).

**Result now:** every required test has a test name. The gate is still **not passed** for one reason: the manual smoke M1-M13 is not run.
