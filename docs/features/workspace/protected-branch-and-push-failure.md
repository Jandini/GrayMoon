# Protected branch and push failure

Route: Changes (`/workspaces/{id}/changes`) then Repositories (`/workspaces/{id}`)

This walkthrough shows what happens when you commit on the **default branch** and try to push a **protected** GitHub branch, then how GrayMoon recovers: **Undo Push Commits** (keep changes), a **single-repo** feature branch, push, and create a PR.

Workspace: **MezzoRecovery** (`/workspaces/2`). Only **MezzoRecovery** has the env edit; the other 10 repos stay on `main` the whole time.

| Part | Goal |
| --- | --- |
| **1 - Force path on default** | Commit on `main` despite the warning, **Push**, read GH013 / rule-violation errors in the overlay, see the **Push failed.** toast |
| **2 - Go around with a feature branch** | Undo (keep changes), create `update-env` on MezzoRecovery only, commit, push, create PR |
| **3 - After merge** | GitHub deletes the remote feature branch; purple **merged**; per-repo **Fetch** discovers **Local Only**; yellow **Sync to main** cleans up |

Related: [Changes](changes.md) (default-branch commit warning), [Undo Push Commits](undo-push-commits.md), [Repository branch management](repository-branch-management.md) (per-repo **New Branch** / **Sync to main**), [Sync To Default](sync-to-default.md) (merged-PR cleanup).

---

## Part 1 - Commit on main despite the warning

### Starting state

On Changes, MezzoRecovery is on **main** with one modified file: `docker/.env`.

![Changes: .env staged on main](../screenshots/workspace2-protected-changes-env-staged.png)

Enter commit message **Update env file** and click **Commit Staged** (or **Commit All** if nothing was staged).

![Commit message ready on main](../screenshots/workspace2-protected-changes-commit-ready.png)

### Default Branch Warning - Proceed anyway

Because the target repo is on its default branch, GrayMoon shows **Default Branch Warning** before writing the commit:

- Lists **MezzoRecovery (main)**
- Copy: committing writes directly to the default (protected) branch; proceed with caution
- **Proceed** continues; **Cancel** aborts

![Default Branch Warning for MezzoRecovery on main](../screenshots/workspace2-protected-default-branch-warning.png)

Click **Proceed**. The commit lands locally. Working tree is clean; the notification card shows **commits ready to push** with MezzoRecovery **↑1 ↓0**.

![After commit on main: No changes, card shows ↑1](../screenshots/workspace2-protected-after-commit-on-main.png)

### Repositories grid before push

Back on Repositories (`/workspaces/2`):

- MezzoRecovery: branch **main**, divergence **0 | 1**, yellow **create**, commits **↑1 ↓0**
- Header **Push** is yellow
- Other repos remain **↑0 ↓0** on **main**

![Outgoing commit on main ready to Push](../screenshots/workspace2-protected-repos-outgoing-on-main.png)

### Push - overlay logs show protected-branch rejection

Click header **Push**. The [loading overlay](../shared.md#loading-overlay) streams git output. For this repo GitHub rejects the push:

- `GH013: Repository rule violations found for refs/heads/main`
- `Changes must be made through a pull request.`
- `! [remote rejected] main -> main (push declined due to repository rule violations)`
- `[ERR] git push failed ...`

![Push overlay: GH013 and remote rejected main](../screenshots/workspace2-protected-push-overlay-attempt.png)

![Push overlay: repeated rule violations and ERR line](../screenshots/workspace2-protected-push-after-fail.png)

When the job finishes, an error toast shows **Push failed.** (error toasts dismiss after ~6 s). The outgoing commit remains: **↑1** and yellow **Push** stay - nothing was published to `origin/main`.

GrayMoon does not force-push or bypass branch protection. The recovery path is undo + feature branch (Part 2).

---

## Part 2 - Undo, single-repo feature branch, push, PR

### Undo Push Commits (keep changes)

**Undo Push Commits** resets every workspace repo that has outgoing commits back to `origin/<branch>`. It is workspace-wide by design: if several repos had unpushed commits, all of them would appear in the modal. Here only MezzoRecovery has **↑1**, so the list has one row - that is expected.

Open the yellow **Push** caret -> **Undo Push Commits**.

![Undo Push Commits modal: MezzoRecovery, Keep changes on](../screenshots/workspace2-protected-undo-modal.png)

- Lists **MezzoRecovery (1 commit)**
- **Keep changes** checked (default): mixed reset - commits removed, file edits stay in the working tree
- **Proceed** runs the reset under the overlay; no push to GitHub

After undo: MezzoRecovery is **↑0 ↓0** again, header **Push** is inactive, version drops back (for example `0.1.0-main.54`).

![Grid after undo: no outgoing commits](../screenshots/workspace2-protected-after-undo.png)

The `.env` edit is still on disk - check Changes if you want to confirm before branching.

### Create feature branch on MezzoRecovery only

Do **not** use workspace-wide **New Branch** / **New Feature** (those touch every repo). Open the **per-repo** branch dialog: click the **Branch** cell on the MezzoRecovery row.

![Per-repo Branch dialog - MezzoRecovery Locals](../screenshots/workspace2-protected-branch-dialog-mezzo.png)

**New Branch** tab:

- **Branch name:** `update-env`
- **Based on:** `origin/main` with green **Default**
- **Create**

![New Branch tab filled: update-env from origin/main](../screenshots/workspace2-protected-new-branch-update-env.png)

Only MezzoRecovery switches to **update-env**. Everyone else stays on **main**.

![MezzoRecovery on update-env; others still on main](../screenshots/workspace2-protected-on-update-env.png)

### Commit on the feature branch (no default-branch warning)

On Changes, the same `docker/.env` change is under MezzoRecovery on **update-env**. Commit message **Update env file** -> **Commit All**. There is **no** Default Branch Warning - you are not on the default branch.

![Changes on update-env with .env still modified](../screenshots/workspace2-protected-changes-on-feature.png)

![After commit: card shows MezzoRecovery ↑1](../screenshots/workspace2-protected-feature-committed.png)

### Push succeeds

On Repositories: MezzoRecovery on **update-env**, divergence **0 | 1**, yellow **create**, **↑1**. Header **Push** is yellow.

![Feature branch ready to push](../screenshots/workspace2-protected-feature-ready-push.png)

**Push** publishes `update-env` to origin. Commits badge returns to **↑0 ↓0**; yellow **create** remains (ahead of default, no open PR yet).

![After successful push of update-env](../screenshots/workspace2-protected-feature-after-push.png)

### Create PR - only repos ahead of default

**Create PRs...** (Branch caret) and the yellow **create** badge only target repositories that are ahead of their default branch with no open PR. Repos still on `main` with no outgoing work are skipped. In this run that is a single target: **MezzoRecovery**.

![Branch menu with Create PRs](../screenshots/workspace2-protected-branch-menu-create-prs.png)

Open **Create PRs...** (or click **create** on the row). Modal title: **New Pull Request - MezzoRecovery**. Title can match the commit (**Update env file**).

![New Pull Request modal for MezzoRecovery only](../screenshots/workspace2-protected-pr-modal.png)

Confirm lists only that repo:

![Confirm: Create pull request for MezzoRecovery](../screenshots/workspace2-protected-pr-confirm.png)

After create, the PR badge shows the number (here **#54**) instead of **create**.

![Grid after PR #54 created](../screenshots/workspace2-protected-after-pr-created.png)

Example PR: https://github.com/Jandini/MezzoRecovery/pull/54

---

## Part 3 - After merge: Fetch, then Sync to main

### GitHub deletes the remote branch on merge

**MezzoRecovery** is configured on GitHub to **delete the head branch after the pull request is merged**. That is a repository setting on GitHub (not a GrayMoon setting). When **#54** merges, GitHub removes `origin/update-env` automatically. GrayMoon still has a local `update-env` checkout until you sync.

### Merged badge on the grid

Back on Repositories, MezzoRecovery stays on **update-env** with a purple **merged** PR badge (and divergence still **0 | 1** until you leave the feature branch). Other repos remain on **main**.

![MezzoRecovery on update-env with purple merged badge](../screenshots/workspace2-protected-after-merge-before-refresh.png)

### Branch dialog before Fetch - GrayMoon does not know yet

Click the MezzoRecovery **Branch** cell. The per-repo dialog still reflects the last known refs:

**Locals:** `update-env` (**Current**), `main`. No yellow **Sync to main** yet - the local branch still looks like it has a remote.

![Locals before Fetch: update-env Current](../screenshots/workspace2-protected-branch-dialog-before-fetch.png)

**Remotes:** still lists **origin/update-env** alongside **origin/main**. The UI has not pruned deleted remotes until you fetch.

![Remotes before Fetch: origin/update-env still listed](../screenshots/workspace2-protected-remotes-before-fetch.png)

### Fetch in the dialog

Click **Fetch** in the dialog footer (per-repo fetch - not the workspace header **Fetch**). That runs `git fetch` for MezzoRecovery and refreshes Locals / Remotes / Tags.

After Fetch:

- **Remotes** drops to **origin/main** only (Default). `origin/update-env` is gone.
- **Locals** keeps `update-env` (**Current**) with a yellow **Local Only** badge - local branch with no matching remote ref.
- Footer shows yellow **Sync to main** (shortcut to per-repo Sync To Default).

![Remotes after Fetch: only origin/main](../screenshots/workspace2-protected-remotes-after-fetch.png)

![Locals: Local Only + Sync to main](../screenshots/workspace2-protected-locals-sync-to-main.png)

### Sync to main

Click **Sync to main**. Confirm dialog (single repo, merged path - no red "commits will be lost", **Proceed** stays calm):

- Lead copy: checkout default, remove current branch locally, pull latest
- Row: **MezzoRecovery** / `update-env` with purple **merged**
- **Delete local branches** (default on)

**Proceed** runs the overlay (**Synchronizing to main...**): checkout `main`, delete local `update-env`, pull.

![Overlay: Synchronizing to main](../screenshots/workspace2-protected-sync-overlay.png)

### Done - all repos on main

MezzoRecovery is back on **main** (`0.1.0-main.55`), divergence **0 | 0**, PR **none**, **↑0 ↓0**, **in sync**. The whole workspace is on default again.

![After Sync to main: MezzoRecovery on main](../screenshots/workspace2-protected-after-sync-to-main.png)

Same cleanup can also use workspace **Branch** → **Sync To Default** or Level 1 rewind when many repos need it. This walkthrough used the per-repo button because only MezzoRecovery left the default branch. Full menu / level flows: [sync-to-default.md](sync-to-default.md#reason-2-merged-prs-per-repo-after-github-deletes-the-remote).

---

## What this demonstrates

| Behavior | Takeaway |
| --- | --- |
| Default Branch Warning | GrayMoon warns before committing on default; **Proceed** still allows the local commit |
| Protected push | Overlay shows GH013 / "must be made through a pull request"; toast **Push failed.**; local commit stays |
| Undo Push Commits | Workspace-scoped; resets all repos with outgoing commits (one repo here is fine); **Keep changes** preserves the working tree |
| Single-repo branch | Per-row Branch dialog **New Branch** - not workspace New Feature |
| Create PRs | Only repositories with commits ahead of default (and no open PR) |
| Delete branch on merge | GitHub setting removes `origin/<feature>`; GrayMoon learns via per-repo **Fetch** |
| **Local Only** + **Sync to main** | After Fetch, yellow **Sync to main** is the calm merged-PR cleanup for that one repo |
