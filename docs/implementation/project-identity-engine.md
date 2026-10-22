# Project Identity Engine - Living Tracker

Design: `docs/todo/graymoon-project-identity/01-engine-design.md` (workspace root of this feature). Plan: `02-implementation-plan.md`.
Branch: `package-collision` (GrayMoon sub-repo), HEAD at audit start `937b5fb6`. Nothing committed or pushed.

## Status

| Phase | State |
|---|---|
| P0 audit and decisions | Audit done, awaiting approval to proceed |
| P1 identity/schema/migration | Not started |
| P2 physical merge | Not started |
| P3 graph identity/ambiguity | Not started |
| P4 transactional reconciliation | Not started |
| P5 diagnostics/perf | Not started |
| P6 integration/docs | Not started |

## P0 findings (deviations from the design baseline `8e284e4`)

1. **A workaround already exists on this branch** (`937b5fb6` "Keep NuGet packages visible when project names collide"): `WorkspaceProjectRepository.SelectProjectsByName` picks one project per name (PackageId first, then Package type, then path order) and is used by both `MergeProjectsForRepository` and `MergeWorkspaceProjectDependenciesAsync`. It is the "packable-first First()" approach the brief forbids as a final answer. Tests: `WorkspaceProjectRepositoryNameCollisionTests`. P2/P3 replace it; those tests need rewriting to the new contract (shell and migrations both persisted).
2. Worker (`CsProjFileParser`) only surfaces `PackageId` when `ProjectType == Package`; `IsPackable=false` yields Library with no PackageId. So `PackageId` on a persisted row already means "effective package producer". No separate packability field is needed (design 4.3).
3. Name-keyed code (all in `WorkspaceProjectRepository.Merge.cs`): unique index `IX_WorkspaceProjects_Context_Repo_Name` (AppDbContext.cs:257, and the legacy guarded SQL in `Migrations.Features.cs:291`, partial `WHERE WorkspaceFeatureContextId IS NOT NULL`), `MergeProjectsForRepository` (existing matched by name), dependent lookup in `MergeWorkspaceProjectDependenciesAsync` (`ToDictionary(ProjectName)`), `IndexPackageTargets` (`TryAdd`, first producer wins silently).
4. Generated rows (`GeneratedPackages.cs`): `IsGenerated`, empty `ProjectFilePath`, special-context owned, keyed in code by `(RepositoryId, PackageId)`; own dedupe logic.
5. Write paths for physical rows: `RefreshWorkspaceProjectsAsync` (batch), `RefreshSingleRepositoryProjectsAsync`, `WorkspaceRepositoryStateWriter.ApplyBatchAsync` (`ProjectsProbed`), plus feature seed (`FeatureProjectionSeedTests`, `WorkspaceFeatureOperations`). Edge writes: `MergeWorkspaceProjectDependenciesAsync`, `SyncGeneratedPackageDependenciesAsync`.
6. Empty-scan gap confirmed: both refresh paths only merge when `ProjectsDetail.Count > 0`, so a successful empty scan leaves stale rows, and dependencies skip null/empty results.
7. Atomicity gap confirmed: project merge, `link.RepositoryType` save, and edge merge are separate `SaveChangesAsync` calls under the projection gate; `MergeWorkspaceProjectDependenciesAsync` also returns early if there are no projects.
8. Schema mechanism: no EF migrations; new step appended to `Migrations.StrictSteps` (next free version is 10; last is 9 "SQLite read indexes"), idempotent, per-step transaction, `VACUUM INTO` backup before pending steps. SQLite partial unique indexes are already in use here, so filtered indexes are viable.
9. Reference facts are not persisted (only edges), so a producer losing its PackageId cannot re-resolve consumers in repos not in the scan. See decision D5.

## Decisions (proposed, need approval)

| # | Decision |
|---|---|
| D1 | Add nullable `ProjectIdentityKey` column (normalized relative path, lower-invariant for ordinal-ignore-case) on `WorkspaceProjects`. Physical unique partial index on `(WorkspaceFeatureContextId, RepositoryId, ProjectIdentityKey) WHERE IsGenerated = 0 AND WorkspaceFeatureContextId IS NOT NULL AND ProjectIdentityKey IS NOT NULL`. |
| D2 | Generated rows: unique partial index on `(WorkspaceFeatureContextId, RepositoryId, lower(PackageId)) WHERE IsGenerated = 1`. Needs the existing dedupe code kept; migration blocks (logs, aborts) if duplicates exist rather than deleting. |
| D3 | One normalizer, replacing `NormalizeProjectPath` and `NormalizeRepoRelativePath`, in `GrayMoon.Application` so Worker-boundary and App share it. |
| D4 | Successful empty scan removes physical rows; failed/missing scan preserves. Requires the refresh paths to distinguish `null` (failure) from empty list. |
| D5 | Ambiguous producers (>1 row with same effective PackageId in context) produce no edge and a structured warning. Partial-refresh invalidation: edges are rebuilt for scanned dependents; edges pointing at a producer row that is removed or loses its PackageId are deleted (FK cascade or explicit) and the affected consumer repos are logged as needing rescan. Persisting reference facts is out of scope unless you want it. |
| D6 | Wrap project merge + RepositoryType + edge merge + level recompute in one `ReconcileProjectSnapshotAsync` transaction in P4. |
| D7 | Rewrite `WorkspaceProjectRepositoryNameCollisionTests` for the new contract instead of keeping the workaround. |

## Baseline

`dotnet test src/GrayMoon.App.Tests --filter FullyQualifiedName~WorkspaceProjectRepository`: Passed 38, Failed 0, Skipped 0 (6 s), at HEAD `937b5fb6`.

## Open questions for the owner

- Approve D1-D7, in particular D5 (no persisted reference facts) and D7.
- Proceed to P1 after this check-in.
