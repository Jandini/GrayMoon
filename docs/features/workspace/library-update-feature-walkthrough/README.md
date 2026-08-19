# Feature walkthrough: `library-update` (dependency-only, frozen repos)

This walkthrough demonstrates a **dependency-only** coordinated update across the **MezzoRecovery** workspace when some Level 1 repositories stay **frozen on release tags**. It complements the full [tape-density walkthrough](../tape-density-feature-walkthrough/README.md) (feature code + deps + PR merge).

## What this walkthrough shows

1. **New Feature** creates `library-update` only on repos **not** pinned to a tag (**Skip repos on tags**).
2. **Push Updated** rewrites `.csproj` package pins, commits, and synchronized-pushes by dependency level.
3. **Partial push** when NuGet wait times out - Level 3 repos may stay unpushed (yellow cloud-up, no upstream).
4. **GitHub Actions** on GrayMoon **Actions** explains why packages never arrived (failed Level 2 builds).
5. **TapeImage tag upgrade** recovery - Fetch, checkout **0.2.0**, **Push Updated** again until all levels push and builds go green.
6. **Create PRs...** (one Branch menu call) - five PRs for Level 2 / 3 branch repos.
7. **Merge + Sync to Default** level by level - Level 1 rewind (no PRs), Level 2 merge + rewind, **Level 3 Only**, Level 3 merge + final rewind. All 11 repos end on `main` or frozen tags.

## Workspace

| Field | Value |
| --- | --- |
| Workspace | **MezzoRecovery** |
| Id | **2** |
| Route | `/workspaces/2` |

## Starting state

- Level 1: **TapeDrive** `0.2.0`, **TapeImage** `0.1.0`, **Website** / **DockerBase** `1.0.0` on tags (frozen).
- Level 2 / 3 on **main** with red **`N of M`** dependency badges (consumers pin old package versions while upstream tags moved).
- Header yellow **Push Updated**.

![Starting grid before library-update](../screenshots/workspace2-library-update-before.png)

## Walkthrough phases

| Phase | Doc | Status |
| --- | --- | --- |
| 1 - New Feature (branch only) | [phase-1-new-feature-branch.md](phase-1-new-feature-branch.md) | Complete |
| 2 - Push Updated | [phase-2-push-updated.md](phase-2-push-updated.md) | Complete (partial push) |
| 3 - Push failure, NuGet wait, GitHub Actions | [phase-3-push-failure-and-gha.md](phase-3-push-failure-and-gha.md) | Complete (diagnosis) |
| 3 recovery - TapeImage tag upgrade | [phase-3-recovery-tapeimage-upgrade.md](phase-3-recovery-tapeimage-upgrade.md) | Complete |
| 4 - Create PRs (one Branch menu call) | [phase-4-create-prs.md](phase-4-create-prs.md) | Complete |
| 5 - Merge + Sync to Default | [phase-5-merging-prs.md](phase-5-merging-prs.md) | Complete |

## Frozen repos vs branch repos

| Repo | After New Feature | After Push Updated + recovery |
| --- | --- | --- |
| TapeDrive, Website, DockerBase | Stay on tag (frozen) | Unchanged |
| TapeImage | Stay on tag **0.1.0** | Upgraded to tag **0.2.0** during recovery |
| MezzoRecovery, Solution, Tape, Mezzo, Api, TapeTools, Agent | `library-update` branch | Deps commit + push; PRs **#40**, **#53**, **#43**, **#38**, **#67** |

Tagged repos keep blank divergence / PR / commits metrics ([frozen grid](../repository-branch-management.md#frozen-on-the-repositories-grid)).

## Related docs

- [New Feature](../new-feature.md) - **Skip repos on tags**
- [Repositories - Push Updated](../repositories.md#update-vs-push-updated)
- [Actions](../actions.md) - workflow status and logs
- [Tag upgrade](../tag-upgrade.md) - upgrade badge and tag checkout flow
- [Sync To Default](../sync-to-default.md) - per-level rewind and stale branch cleanup

