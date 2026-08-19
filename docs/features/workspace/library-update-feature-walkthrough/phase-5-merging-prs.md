# Phase 5 - Merge PRs and Sync to Default

This phase follows [Phase 4 - Create PRs](phase-4-create-prs.md). Open PRs remain on Level 2 and Level 3; Level 1 had **no PRs** (MezzoRecovery root and Solution were on `library-update` but not ahead of default enough to need PRs).

The merge workflow proceeds **by dependency level**: clean up finished levels with per-level **Sync to default branch** (rewind icon), merge PRs on GitHub for the next level, then repeat.

See also: [Sync To Default](../sync-to-default.md) (general reference).

## Why Sync to Default matters

**Sync to default branch** checks out `main`, deletes the stale feature branch locally (and on origin when **Delete remote branches** is checked), and pulls latest default. That keeps every workspace clone on a short branch list and prevents accidentally working on yesterday's feature branch after the work is done or abandoned.

Use the **Level N** header rewind (`<<` icon, tooltip **Sync to default branch...**), not the workspace-wide **Branch** caret **Sync To Default**, while Level 2 / 3 still have open PRs. Workspace-wide sync would touch every non-default repo and show red **commits will be lost** warnings for repos still ahead on `library-update`.

## Starting state (after Phase 4)

![Full grid before Level 1 sync](../../screenshots/workspace2-library-update-before-level1-sync.png)

| Area | State |
| --- | --- |
| Level 3 | `library-update`, open PRs **#43**, **#67**, **#38**, green deps |
| Level 2 | `library-update`, open PRs **#40**, **#53**, green deps |
| Level 1 | **MezzoRecovery** / **Solution** on `library-update`, PR **none**; **TapeDrive**, **TapeImage**, **Website**, **DockerBase** frozen on tags |
| Header | **Push** outline (nothing to push) |

Level 1 has **no PRs to merge**. The only branch repos still on `library-update` are MezzoRecovery and Solution (`0 \| 0` vs default - no unique commits, but the stale branch ref still exists locally and on origin). Tag repos are skipped automatically.

## Level 1 - Sync to default (no merge step)

Because Level 1 has no PRs, skip GitHub merge and go straight to rewind.

### Step 1 - Click Level 1 rewind

On the **Level 1** header row, click the rewind control (**Sync to default branch...**, double `<<` arrows).

GrayMoon runs a fetch job first: **Fetching latest branch state for N repositories...** (refreshes PR records and `git fetch`). When fetch completes, the **Sync to Default** dialog opens scoped to Level 1 only.

### Step 2 - Confirm the dialog

![Level 1 Sync to Default dialog - 2 repos, no red alert](../../screenshots/workspace2-library-update-level1-sync-dialog.png)

Dialog details for this run:

- **Title:** Sync to Default
- **Lead copy:** *This will sync 2 repositories to their default branch: checkout default, remove the current branch locally, and pull.*
- **Repo list:** `MezzoRecovery` and `MezzoRecovery.Solution`, both leaving `library-update` with gray **remote** badge (`origin/library-update` will be deleted)
- **No red alert** - neither repo is ahead of default with unmerged commits
- **Delete remote branches** - default **on** (removes `origin/library-update` for these two repos)
- **Delete local branches** - default **on** (removes local `library-update` ref after checkout)
- Tag repos (**TapeDrive**, **TapeImage**, **Website**, **DockerBase**) are **not listed** - already on tags, skipped

Click **Proceed**. Overlay: **Synchronizing to default branch...**, then per-repo git steps (remote delete, fetch --prune, checkout `main`, delete local branch, pull).

### Step 3 - After Level 1 Sync to Default

![After Level 1 sync - MezzoRecovery and Solution on main](../../screenshots/workspace2-library-update-after-level1-sync.png)

Level 1 after this step:

| Column | Level 1 state |
| --- | --- |
| **Branch** | `main` on **MezzoRecovery** and **Solution**; tag icon unchanged on frozen repos |
| **Version** | `*-main.*` GitVersion on branch repos |
| **PR** | blank (no PR was ever opened) |
| **Deps** | gray `0` on Level 1 rows |
| **Commits** | **in sync** with origin |

Level 2 / 3 **unchanged** - still on `library-update` with open PRs **#40**, **#53**, **#43**, **#67**, **#38**.

### Red dependency badges on Level 2 / 3 (expected)

After Level 1 rewind, Level 1 packages report `*-main.*` again. Level 2 / 3 `.csproj` files on `library-update` still pin `-library-update` package versions. GrayMoon paints red **`N of M`** badges on consumers (e.g. Tape **1 of 2**, TapeTools **2 of 4**).

That is expected - not a failed sync. Do **not** click full **Push Updated** while Level 2 / 3 PRs are still open. The next step is merge Level 2 PRs on GitHub, then **Level 2 Only** update and Level 2 rewind (documented in the next section of this phase).

## Next steps (Level 2 and beyond)

| Step | Action |
| --- | --- |
| 1 | Merge Level 2 PRs **#40** (Tape) and **#53** (Mezzo) on GitHub |
| 2 | **Push Updated** caret -> **Level 2 Only** (rewrite Level 2 deps to `-main.*`, push) |
| 3 | Level 2 header **Sync to default branch...** |
| 4 | Merge Level 3 PRs on GitHub |
| 5 | **Level 3 Only** update/push, then Level 3 rewind |

*(Level 2+ sections to be added as the walkthrough continues.)*
