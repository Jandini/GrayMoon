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
