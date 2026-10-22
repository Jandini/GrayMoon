# Project Identity Engine - Living Tracker

Design: `docs/todo/graymoon-project-identity/01-engine-design.md` (workspace root of this feature). Plan: `02-implementation-plan.md`.
Branch: `package-collision` (GrayMoon sub-repo), HEAD at audit start `937b5fb6`. Nothing committed or pushed.

## Status

| Phase | State |
|---|---|
| P0 audit and decisions | Done, approved |
| P1 identity/schema/migration | Done, gate passed (1596 App tests green) |
| P2 physical merge | Done, gate passed (1607 App, 817 Worker tests green) |
| P3 graph identity/ambiguity | Done, gate passed (1610 App tests green) |
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

## P1 - identity primitive and schema (done, gate passed)

Approved by the owner ("ok, let's start"): D1-D7 as proposed, with the D1/D2 refinement below. P2 not started.

**Refinements to the decisions**
- D1/D2 merged into one column and one index: `ProjectIdentityKey` (physical: case-folded normalized path; generated: case-folded `PackageId`) with a single unique partial index `IX_WorkspaceProjects_Context_Repo_Identity` on `(WorkspaceFeatureContextId, RepositoryId, IsGenerated, ProjectIdentityKey) WHERE ProjectIdentityKey IS NOT NULL AND WorkspaceFeatureContextId IS NOT NULL`. `IsGenerated` in the key keeps a generated package id from colliding with a physical path.
- Correction to P0 finding 8: the next free strict step was 11, not 10 (10 is the pinned AI provider column).
- Added finding: the Feature projection seed (`WorkspaceFeatureOperations`, around line 2480) is also name-keyed (`featureProjectsByKey` by `(RepositoryId, name)`) and logs "Collisions". It is a P2 site, not only the repository merge.

**Changed**
- `GrayMoon.Application/Models/ProjectIdentity.cs` (new): the one normalizer (`NormalizePath`, `PhysicalKey`, `GeneratedKey`). Rejects empty, rooted, drive and repo-escaping paths.
- `WorkspaceProject.ProjectIdentityKey` + `RefreshIdentityKey()`; declared last so a fresh database and an upgraded one have the same column order.
- `AppDbContext`: unique name index replaced by the unique identity index plus a non-unique name index with the old name.
- `Migrations.ProjectIdentity.cs` (new) + `StrictSteps` entry 11: adds the column, backfills from each row's own path or package id, swaps the indexes. Rows with a duplicate or underivable key keep a NULL key (ignored by the unique index), are logged, and are never deleted. ProjectIds and edges are untouched.
- Keys are stamped at the three existing row writers (repository merge add/update, generated package add, Feature seed add/update). Matching is still by name there until P2.
- `docs/architecture/03-data-model-and-persistence.md`: step 11 and the new uniqueness.
- Test fixture only: `WorkspaceFeatureContextOrphanCleanupTests` rebuilds a pre-step-11 table shape, so it now skips index definitions that mention the new column.

**Validated**
- `dotnet test src/GrayMoon.App.Tests`: Passed 1596, Failed 0 (includes the 38 baseline repository tests and the existing 0.1.0 fixture schema-parity test).
- New `ProjectIdentityTests`: path normalization (mixed separators, `.`, `..`, rooted, drive, escaping, case), distinct paths with equal names coexist, canonical duplicate (case/separator variant) rejected, same path in another repo/context allowed, generated rows with empty paths unique by package id and not colliding with a physical key, step 11 backfill keeps ProjectIds and a dependency edge and is idempotent, duplicate/underivable rows are kept with NULL keys.

**Compatibility**
- Existing DBs get the backup the runner already takes (`VACUUM INTO`) before step 11. Rollback: restore that backup file; the column is additive, and the previous build ignores it, but the old unique name index is dropped so an older build would not re-impose it.
- Note for P2: until the merge stops matching by name, behaviour is unchanged for users (the P0 workaround still decides which same-name project is stored).

**Risks / next**
- Legacy rows with NULL context are not keyed (the unique index requires a context). Real DBs backfill contexts in the legacy baseline, so this should only affect pathological rows.
- Next gated phase: P2 (physical project merge by path). Waiting for a go-ahead.

## P2 - physical project merge by path (done, gate passed)

**Changed**
- `WorkspaceProjectRepository.Merge.cs`: `MergeProjectsForRepository` matches incoming projects to rows by `ProjectIdentityKey` (path), never by name. Same-named projects at different paths all persist; a moved project is a removal plus an addition. A legacy row with no usable path is adopted by an unclaimed same-name incoming project so its ProjectId survives. Duplicate incoming paths collapse (identical) or keep the first and log a warning (conflicting). Projects with unusable paths are skipped and logged. A successful empty list removes the physical rows of that repository and context; generated rows are untouched.
- Dependency merge: dependents resolve by path (same-named consumers keep their own PackageReferences). Targets resolve through `PackageTargetIndex` by effective PackageId only (D8: physical beats generated; more than one candidate is ambiguous, no edge, structured warning naming the producers). Project name is no longer a fallback. `UpdateProjectDependencyVersionsAsync` uses the same index.
- D5: edges whose referenced project is no longer a unique producer are deleted during a partial refresh and logged as needing a rescan of the consumers.
- Empty-scan fix: `GetAuthoritativeProjectsDetail` keeps "no project list" (null, not scanned, nothing pruned) distinct from "scanned, none found" (empty, authoritative). `WorkspaceGitService.Projects.cs` now merges empty lists and only updates `RepositoryType` when scanned. The Worker's `RefreshRepositoryProjectsCommand` now returns null (not scanned) when the repository folder is missing, so a missing clone cannot prune data.
- `WorkspaceFeatureOperations` seed keys projects by `(RepositoryId, path key)` instead of name.
- Tests: `WorkspaceProjectRepositoryNameCollisionTests` rewritten for path identity (reported case, order independence, same-name coexistence, duplicate PackageId, stale-edge invalidation, rename, move, case/separator, conflicts, idempotence, empty scan, context isolation). Two `FeatureProjectionSeedTests` updated: identity is the path, not the name.

**Validation:** `dotnet test` App 1607 passed / 0 failed; Worker 817 passed (one timing test, `RepositoryAccessTests.Mutating_holder_that_finishes_inside_the_grace_period_is_never_cancelled`, failed once under load and passed on rerun; unrelated).

**Risks / next**
- `WorkspaceRepositoryStateWriter` batch path already passes empty lists through the same merge; the edge merge there is still not run (P4 covers all entry points).
- `UpdateProjectDependencyVersionsAsync` and `SyncGeneratedPackageDependenciesAsync` still use their own path normalizers (P3).
- Next gated phase: P3. Waiting for a go-ahead.

## P3 - graph identity and ambiguity (done, gate passed)

**Audit result:** dependent lookup, target resolution, ambiguity handling and stale-edge invalidation landed with P2 (`MergeWorkspaceProjectDependenciesAsync`, `PackageTargetIndex`). No edge is created by project name anywhere.

**Changed in P3**
- `UpdateProjectDependencyVersionsAsync` and `SyncGeneratedPackageDependenciesAsync` now use `ProjectIdentity.PhysicalKey` (the former local `NormalizeRepoRelativePath` is removed); the version update ignores generated rows as dependents and skips unusable paths.
- Tests (`ProjectIdentityGraphTests`): version update touches only the consumer at that path (differently formatted), an ambiguous package changes nothing, generated edges find the consumer whatever the path formatting. Other P3 scenarios (reported collision, same-named consumers, duplicate PackageId, nonpackable, PackageId change, producer-only refresh) are covered by `WorkspaceProjectRepositoryNameCollisionTests`.

**Validation:** App tests 1610 passed / 0 failed.

**Risks / next**
- Dependency levels are recomputed from the full graph after each merge (unchanged); no incremental maintenance was added.
- Next gated phase: P4 (transactional `ReconcileProjectSnapshotAsync`, all entry points). Waiting for a go-ahead.
