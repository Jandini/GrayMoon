# Feature walkthrough: Phase 4 - Merge PRs and Sync to Default (Level 1)

This phase follows [Phase 4 - Create coordinated PRs](phase-4-create-prs.md). PRs for the `tape-density` feature are open (or already merged on GitHub). This step shows the **happy path**: merge Level 1 PRs, then use the **Level 1** header rewind to drop the feature branch and pull latest `main` in dependency order.

We document Level 1 rewind and **Level 2 Only** here. Level 3 stays on `tape-density` until its PRs merge and you rewind that level separately.

See also the general reference: [Sync To Default](../../sync-to-default.md).

## Starting state (full Repositories page)

After PR creation ([phase-4-create-prs.md](phase-4-create-prs.md)), every impacted repo had a PR link on `tape-density`. By this point:

- **Level 1 PRs are merged on GitHub** for the repos that had feature work (`MezzoRecovery`, `MezzoRecovery.TapeDrive`). GrayMoon paints those rows with a purple **merged** badge.
- **Level 2 / 3** still show green open PR badges (`#42`, `#66`, and so on) and remain on `tape-density`.
- **Header Push Updated** (yellow) is expected: Level 1 packages are back on `*-main.*` GitVersion while Level 2 / 3 `.csproj` files still pin `-tape-density` package versions.

In this run, four Level 1 repos (`TapeImage`, `Website`, `DockerBase`, `Solution`) had already been rewound to `main` in an earlier partial sync. Only **two** Level 1 clones still needed cleanup when we opened the dialog.

![Full Repositories page before Level 1 Sync to Default](../../screenshots/workspace2-tape-density-repos-before-level1-sync.png)

What to notice in the screenshot:

| Area | State |
| --- | --- |
| Level 3 | `tape-density`, open PRs, red dependency badges (e.g. `1 of 2`, `2 of 4`) |
| Level 2 | `tape-density`, open PRs, red dependency badges (e.g. `2 of 2`) |
| Level 1 | `MezzoRecovery` / `TapeDrive` on `tape-density` with purple **merged**; other Level 1 rows already on `main` |
| Header | **Push Updated** (yellow) - consumers still pin feature package versions |

## Do not use workspace-wide Sync To Default here

**Branch** caret **Sync To Default** across all 11 repos is for **discard/rollback** when you are abandoning unmerged work. With open PRs still on Level 2 / 3, that path would show red **commits will be lost** warnings and a countdown.

For this walkthrough you only want **per-level cleanup after merge**. Use the **Level 1** header rewind icon (**Sync to default branch...**), not the workspace-wide menu item.

## Level 1 rewind - fetch, then dialog

On the **Level 1** header, click the rewind control (**Sync to default branch...**).

GrayMoon does **not** open the confirm dialog immediately. It first runs a workspace job: **Fetching latest branch state for N repositories...** (refreshes GitHub PR records and `git fetch` per repo). When the fetch finishes, the **Sync to Default** dialog opens scoped to Level 1 only.

![Level 1 Sync to Default dialog (merged PRs, two repos left)](../../screenshots/workspace2-tape-density-level1-sync-to-default-dialog.png)

Dialog details for this **merged-PR** case (reason 2 in [sync-to-default.md](../../sync-to-default.md)):

- **Title:** Sync to Default
- **Lead copy:** *This will sync 2 repositories to their default branch: checkout default, remove the current branch locally, and pull.* (Only repos still on a non-default branch are listed; clones already on `main` are skipped.)
- **No red alert** - merged PRs are treated as landed work, not commits about to be destroyed.
- **Repo list** - `MezzoRecovery` and `MezzoRecovery.TapeDrive`, both leaving `tape-density`. No **N commits will be lost** line.
- **Delete local branches** - default **on**. After checkout of `main`, GrayMoon deletes the local `tape-density` ref.
- **Proceed** stays **blue** and runs **immediately** (no five-second countdown).

Click **Proceed**.

## What runs after Proceed

A second job: overlay **Synchronizing to default branch...**, then per-repo git steps (remote branch delete when upstream existed, fetch --prune, checkout `main`, delete local feature branch, pull). The overlay may be brief for two repos.

For each synced repo GrayMoon recaptures GitVersion, branch lists, and commit counts, then recomputes workspace dependency stats once at the end.

## After Level 1 Sync to Default

![After Level 1 Sync to Default - all Level 1 on main](../../screenshots/workspace2-tape-density-level1-sync-to-default-after.png)

Level 1 after this step:

| Column | Level 1 state |
| --- | --- |
| **Branch** | `main` on every Level 1 row |
| **Version** | `*-main.*` GitVersion strings |
| **PR** | **none** (merged PR is closed; feature branch ref is gone locally) |
| **Deps** | gray `0` (Level 1 repos do not consume workspace packages) |
| **Commits** | **in sync** with origin |

Level 2 / 3 **unchanged** - still on `tape-density` with open PRs. That mixed grid is correct, not a failed job.

### Red dependency badges on Level 2 / 3 (expected)

Level 1 packages now report `*-main.*` again. Level 2 / 3 `.csproj` files on `tape-density` still pin `-tape-density` package versions. GrayMoon compares every workspace package reference to live GitVersion and paints red **`N of M`** badges on consumers (e.g. Tape `2 of 2`, TapeTools `2 of 4`).

Hover a badge and GrayMoon lists each drifted pin as `current -> new`. TapeTools might show only **2 of 4** mismatches because its Level 2 deps still match `-tape-density` on disk - only the Level 1 package pins are stale relative to `main`.

Those badges are the signal that consumers no longer match the package versions GitVersion reports on disk. They are expected immediately after Level 1 rewind. What you do next depends on whether you are **merging level by level** or trying to clear every badge in one shot.

## Push Updated vs Level 2 Only (while merging PRs)

Yellow **Push Updated** is available because unmatched deps exist. The primary button and the split-menu item **Level 2 Only** both run **Update Only** then **Push Only**, but they differ in **scope**:

| Action | Scope | What happens on `tape-density` after Level 1 rewind |
| --- | --- | --- |
| **Push Updated** (primary click) | Every level that still needs work | Rewrites `.csproj` / file tokens on Level 2 **and** Level 3, commits everywhere, then synchronized push walks Level 2 push, NuGet wait, Level 3 push |
| **Level 2 Only** (split menu) | Lowest level needing work only (`maxLevel: 2`) | Rewrites and commits only Level 2 repos (`Tape`, `Mezzo`), synchronized push stops after Level 2 packages publish |

The menu label tracks the lowest red-badge level. After Level 1 is on `main`, that item reads **Level 2 Only**. After Level 2 merges and rewinds, it becomes **Level 3 Only** for the final consumer pass.

See the full menu in [Phase 3 - Push Updated caret](phase-3-commit-push-gha.md#push-updated-caret---full-menu).

### Why full Push Updated is the wrong default during merge

If you click the primary **Push Updated** button while Level 2 and Level 3 PRs are still open:

1. **Level 3 picks up extra commits before its PR merges.** GrayMoon adds a `chore(deps): update package versions` commit on Api, TapeTools, and Agent while their PRs are still in review. Reviewers see a moving target; CI runs again on commits that were not part of the original feature scope.

2. **Level 3 pins Level 2 packages that are not on `main` yet.** A workspace-wide update rewrites Level 3 to whatever GitVersion Level 2 reports **on the feature branch today** (`*-tape-density.*`). After you merge Level 2 to `main` and it publishes new `-main.*` packages, Level 3 pins are stale again - you need **another** update pass.

3. **You pay for NuGet wait and push work you do not need yet.** Synchronized push continues into Level 3 (registry polling, overlay time) even though your immediate goal is only to land Level 2 PRs against packages that already live on default.

4. **It breaks the same rhythm as per-level rewind.** Sync to Default already proceeds level by level so each finished layer drops the feature branch before the next merges. Full **Push Updated** jumps ahead and couples Level 3 to dependency state that will change again when Level 2 merges.

### Why Level 2 Only fits the merge workflow

When your focus is **merge Level 2 PRs next**, open the **Push Updated** caret and choose **Level 2 Only**:

1. **Scope matches merge order.** Only `MezzoRecovery.Tape` and `MezzoRecovery.Mezzo` get `.csproj` rewrites so their Level 1 `<PackageReference>` versions point at `-main.*` packages (the versions Level 1 clones report after rewind). Level 3 repos are not touched.

2. **Level 2 PRs absorb exactly one deps commit.** The open PRs for Tape and Mezzo gain a single alignment commit that explains why they now consume default-branch packages. You merge that PR set, then **Level 2** header rewind - same pattern as Level 1.

3. **Level 3 PRs stay stable.** Api, TapeTools, and Agent keep the commits reviewers already signed off on. Red badges may remain (often partial, e.g. **2 of 4** on TapeTools) until Level 2 is merged and published - that is fine.

4. **The next pass is obvious.** After Level 2 rewind, Level 2 packages report `*-main.*`, Level 3 still pins `-tape-density` Level 2 versions, and the menu offers **Level 3 Only**. One scoped update/push per merge wave instead of rework.

### When primary Push Updated does make sense

Use the primary **Push Updated** button when you want to **clear every unmatched dep and push every outgoing commit in one job** - for example right after feature coding ([Phase 3](phase-3-commit-push-gha.md)) when no PRs exist yet, or when every level is ready to move together.

During **coordinated PR merge**, prefer **Level N Only** so each dependency layer gets one update/push cycle aligned with that layer's merge and rewind.

## Run Level 2 Only

After Level 1 rewind, yellow **Push Updated** is back because Level 2 / 3 still pin `-tape-density` package versions while Level 1 clones report `*-main.*`. The next scoped action is **Level 2 Only** from the split menu - not the primary button.

### Before

Level 1 is on `main`. Level 2 shows red **`2 of 2`** (Tape) and **`1 of 1`** (Mezzo). Level 3 still has open PRs and partial red badges (e.g. TapeTools **`2 of 4`** - only Level 1 pins are stale).

![Before Level 2 Only - Push Updated menu open](../../screenshots/workspace2-tape-density-before-level2-only.png)

Open the **Push Updated** caret. The first item reads **Level 2 Only** because Level 2 is the lowest dependency level with unmatched refs:

![Push Updated menu - Level 2 Only](../../screenshots/workspace2-tape-density-level2-only-menu.png)

Click **Level 2 Only**.

### Update dependencies modal

GrayMoon opens the same **Update dependencies** modal as a full **Push Updated** run, but the job that follows is scoped to Level 2 (`maxLevel: 2`).

![Update dependencies modal (Level 2 Only)](../../screenshots/workspace2-tape-density-level2-only-update-modal.png)

Leave the default message (`chore(deps): update package versions`) and **Include updated dependencies in commit message** on. Click **Proceed**.

### What the job does

One background job runs **Updating Level 2...** then synchronized push **only through Level 2**:

1. **Update phase** - rewrites `.csproj` in `MezzoRecovery.Tape` and `MezzoRecovery.Mezzo` so Level 1 `<PackageReference>` versions point at `-main.*` (matching the Level 1 clones after rewind). Level 3 repos are not edited.
2. **Commit phase** - one deps commit per Level 2 repo with the shared message.
3. **Push phase** - synchronized push publishes Level 2 packages and stops. No Level 3 push, no NuGet wait for Level 3 consumers.

The overlay may finish quickly when only two repos update and push:

![After Level 2 Only job completes](../../screenshots/workspace2-tape-density-level2-only-after.png)

### After Level 2 Only

| Area | What changed | What did not change |
| --- | --- | --- |
| **Level 2** | Dep badges green (`2`, `1`). Tape / Mezzo gained a deps commit and push (`0 \| N` ahead of `main`). Open PR badges `#39` / `#52` still point at the same PRs - now including the alignment commit. | Branch still `tape-density`. |
| **Level 3** | Red badges may **increase** (e.g. TapeTools **`2 of 4`** -> **`4 of 4`**, Api **`1 of 2`** -> **`2 of 2`**) because Level 2 package GitVersion moved on the feature branch while Level 3 `.csproj` files were left untouched. No new commits on Level 3. | PR set unchanged - reviewers still see the original feature commits only. |
| **Level 1** | Still `main`, gray `0`, **none** PR. | Unchanged. |
| **Header** | Still yellow **Push Updated** - Level 3 is now the lowest level needing work. Menu will offer **Level 3 Only** after Level 2 merges and rewinds. | Primary **Push Updated** would still jump into Level 3 - avoid until that wave. |

That split grid is the proof **Level 2 Only** worked: Level 2 is aligned and pushed; Level 3 was deliberately not updated.

### PR checks turn green after GHA completes

Pushing the deps commit re-runs GitHub Actions on the open Level 2 PRs. While workflows are in flight, the check badge beside `#39` / `#52` is **orange** (running). After CI finishes successfully, **Sync** (or a page refresh) picks up the new status and the badge turns **green**.

![Level 2 PR checks green after GHA success](../../screenshots/workspace2-tape-density-level2-pr-checks-green.png)

In this frame:

- **Level 2** - `#39` and `#52` with green check badges (`2` and `1` workflows passed). Ready to merge on GitHub.
- **Level 3** - open PRs unchanged; check badges still red (those PRs were not part of this push).
- **Level 1** - still `main`, no PR.

## Merge in GitHub (GrayMoon does not merge for you)

In this version of GrayMoon, **merging is done by you in GitHub**. GrayMoon tracks PR state, check status, and mergeability on the Repositories grid, but it does not expose a **Merge** button. When checks are green, open each PR on GitHub and merge (or close) there yourself.

### Quick navigation to PRs

Two shortcuts from the Repositories page:

1. **Per-repo PR badge** - click the green `#39` / `#52` link on a row. GrayMoon opens that pull request on GitHub in a new tab.
2. **Level header dropdown** - hover the **`N repositories`** label on a **Level N** header (e.g. **2 repositories** on Level 2). A menu titled **Open in GitHub...** lists **Branches**, **Pull Requests**, **Actions**, and other GitHub tabs. Choose **Pull Requests** to open the PR list for every repo in that level (open PRs resolve to the exact PR URL when GrayMoon knows the number).

![Level 2 header - Open in GitHub dropdown on hover](../../screenshots/workspace2-tape-density-level2-github-dropdown.png)

Use whichever path fits: one PR via the row badge, or the whole level via the header menu before you merge.

### Example: close `MezzoRecovery.Mezzo` when it has no file changes

After **Level 2 Only**, not every Level 2 PR carries feature edits. **`MezzoRecovery.Tape`** (`#39`) picked up the deps alignment commit and still has package work in scope. **`MezzoRecovery.Mezzo`** (`#52`) may have **no changed files** in the PR - only the deps bump GrayMoon committed, or an empty diff if Mezzo had nothing to rewrite.

On the grid, Tape shows a file-count beside the PR badge (e.g. **`4`** next to `#39`). Mezzo shows **`#52`** with a green check but **no file-count** - a signal the PR has little or nothing left to land as product code. In that case you can **close** PR `#52` on GitHub instead of merging it, then continue with Tape and the rest of the level merge plan. You perform that decision in GitHub; GrayMoon will reflect merged/closed state on the next sync.

## Pause point

Level 2 deps are aligned, pushed, and checks are green. Next steps (you merge in GitHub):

1. Open Level 2 PRs (row badge or **2 repositories** header menu -> **Pull Requests**). Merge **`MezzoRecovery.Tape`** (`#39`). Close **`MezzoRecovery.Mezzo`** (`#52`) if it has no file changes.
2. **Level 2** header rewind (**Sync to default branch...**).
3. **Level 3 Only** from the **Push Updated** menu, then merge Level 3 PRs in GitHub and **Level 3** header rewind.

Each level: scoped **Level N Only**, merge (or close) PRs in GitHub, rewind - not one workspace-wide push that runs ahead into the next level.
