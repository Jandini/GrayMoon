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
