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
