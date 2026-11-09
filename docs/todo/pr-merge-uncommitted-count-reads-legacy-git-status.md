# Merge PR "uncommitted changes" warning reads a legacy table that is no longer refreshed

Status: not built. Written 2026-11-09 after checking what still reads the legacy Git Changes tables, while verifying the 0.1.0 to 0.2.0 upgrade. Found by reading the code; not yet reproduced in the running App.

## Symptom (expected from the code)

In the Merge pull request dialog and in bulk merge on Workspace Repositories, the "N uncommitted changes" warning and the HasUncommitted flag can be wrong:

- A repository that was on the Workspace before the upgrade from 0.1.0 shows the uncommitted count it had at upgrade time, forever.
- A repository added after the upgrade has no legacy status row, so it shows 0 uncommitted changes even when files are modified.

The warning is the user's last prompt before merging with local work that is not committed, so a false 0 matters.

## Root cause

- `WorkspacePullRequestService.ComputeLocalGitState` (`GrayMoon.App/Services/Workspaces/WorkspacePullRequestService.cs`) computes the count as `link.GitStatus.StagedCount + link.GitStatus.ChangedCount`.
- `link.GitStatus` is the legacy `WorkspaceGitRepositoryStatus` entity (table `WorkspaceGitRepositoryStatus`), loaded by `GetLinkWithConnectorAndGitStatusAsync` with `.Include(wr => wr.GitStatus)`. Three callers use it: the merge review details (around line 377), the local-state check (around line 402) and the merge review (around line 431). The results reach `WorkspaceRepositories.Merge.cs`, `WorkspaceRepositories.BulkMerge.cs` and `MergePullRequestModal.razor`.
- `GitChangesSnapshotPushHandler` writes that table only when `GitChangesOptions.WriteLegacyGitChangesTables` is true and the context is the special Workspace. The option defaults to `false` (the "cutover default", also in the 0.2.0 tag). The Changes page already reads only the context tables, so nothing keeps the legacy row current.
- Nothing else writes the legacy status or entry tables. The only other uses are the one-time upgrade copy (`Migrations.Features.cs`), row cleanup (`WorkspaceRepositoryLinkCleanup`) and the model/EF configuration. Tests: `WorkspaceRepositoryReplaceTests` sets `GitStatus.StagedCount`; `WorkspaceRepositoryLinkListQueryServiceTests` and `UpgradeFrom010Tests` touch the entities.

## Fix plan

1. Read the counts from `WorkspaceGitContextRepositoryStatuses` (staged + changed) for the Workspace's special context, matched on `WorkspaceRepositoryId`, instead of `link.GitStatus`. Resolve the special context with the existing context resolver (`GetOrCreateSpecialWorkspaceContextIdAsync` / `WorkspaceFeatureContextResolver`); do not add a second lookup path.
2. Keep behaviour the same when there is no context status row (count 0), exactly as `link.GitStatus?.` does today.
3. Remove `GitStatus` from the merge queries (`.Include(wr => wr.GitStatus)` and `GetLinkWithConnectorAndGitStatusAsync`). Leave the `GitStatus` navigation, `GitChangeEntries` and the legacy tables in the model and database: the upgrade copy and cleanup still need them. Do not drop any table in this change.
4. Decide, and write down in the change, what a Feature context should show. The merge dialog is about the Workspace checkout, so the special Workspace context is the expected answer; confirm that against how `WorkspaceRepositories` chooses its context before coding.
5. Leave `WriteLegacyGitChangesTables` off. Do not turn it back on as the fix: that doubles the writes and keeps a second source of truth.

## Tests (write first, they must fail before the fix)

- Merge review reports the staged + changed count from the context status row when the legacy `WorkspaceGitRepositoryStatus` row is missing (new repository after upgrade).
- Same, when the legacy row exists with a different, stale count: the context value wins.
- A repository with no context status row reports 0 and no warning.
- Update `WorkspaceRepositoryReplaceTests` only if it still depends on the merge path; it tests replacing a link, not the warning.
- Run `UpgradeFrom010Tests` and `SpecialWorkspaceContextBackfillRepairTests` to confirm the legacy tables are still readable for the upgrade copy.

## Docs and release notes

- `docs/architecture/03-data-model-and-persistence.md` and `04-runtime-communication-and-concurrency.md` still say the special Workspace dual-writes the legacy tables. Correct them: legacy writes are off by default and only the upgrade copy and cleanup use those tables.
- Add one bullet at the top of `## Recent GrayMoon changes` in `GrayMoon.Desktop/README.md`: the Merge dialog and bulk merge show the current uncommitted count (see `.cursor/rules/update-desktop-readme.mdc` for the style).

## Prompt for an AI agent

> Work in `GrayMoon/` only. Read this file, `docs/architecture/07-git-changes-projection-and-tree.md` and `GrayMoon.App/Services/Workspaces/WorkspacePullRequestService.cs`.
> Fix the Merge PR uncommitted-changes warning so it reads the current Git Changes counts from `WorkspaceGitContextRepositoryStatuses` for the special Workspace context, not from the legacy `WorkspaceGitRepositoryStatus` table that is no longer refreshed.
> Write the failing tests first (see Tests above), then change `ComputeLocalGitState` and the three callers, and remove the `GitStatus` include from the merge queries. Do not drop or migrate any table, do not change `WriteLegacyGitChangesTables`, and do not touch the upgrade copy in `Migrations.Features.cs`.
> Then update the two architecture docs and add the Desktop README bullet. Run the App tests with `--filter "Category!=Long"`. Do not commit or push.
