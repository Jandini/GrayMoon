# The Worktree Features v1 plan in plain words

This is a short, non-technical companion to [06-worktree-release-implementation-plan.md](06-worktree-release-implementation-plan.md). It is written for you, the owner and user of GrayMoon, not for an AI agent. For each step it explains what will change and what you will notice when using GrayMoon.

A few words used below:
- **Workspace**: your normal set of repositories, as you use GrayMoon today.
- **Feature**: a separate copy of all Workspace repositories (Git worktrees) on its own branch, so you can work on something without touching the Workspace.
- **Worker**: the small background program on your machine that runs Git for GrayMoon.
- **Gate**: a point where the work stops and you test GrayMoon yourself, following a short checklist. Work continues only after you reply PASSED.

At every gate you also run the **Workspace smoke test**: ten quick checks that everything you use today still works (sync, live updates after a terminal commit, branch switching, Git Changes, push with levels, PRs, Dependencies page, Open in..., REST scripts).

---

## Before the work starts: your decisions

The plan has eight questions only you can answer, for example: is Docker supported, should Remove also delete remote branches, are Linux Workers supported for Features. Each one has a sensible default. You can answer "go with default" for all of them.

---

## Step 0: Foundation

**U0-1 Clean start.** Fixes one build warning, deletes some leftover test log files and makes one unreliable test reliable. *You will notice nothing.*

**U0-2 Safe database upgrades.** Before GrayMoon changes its database on startup, it now makes a backup copy (`graymoon.db.bak-<date>`, the 3 newest are kept). If a new upgrade step fails, GrayMoon stops with a clear message and tells you where the backup is, instead of silently continuing with a half-upgraded database. *You will notice backup files next to your database. Normally nothing else.*

**U0-3 Upgrade test from 0.1.0.** An automatic test proves that a database from the released 0.1.0 version upgrades correctly. *You will notice nothing.*

**GATE-1: you test.** Back up your real database, start the new build, and check that all your Workspaces and Features are still there and a backup file was created.

---

## Step A: GrayMoon asks the Worker about your folders

**A1 New "inspect" command for the Worker.** The Worker learns to report, for each Feature folder: does it exist, are there uncommitted changes, unpushed commits, is it locked. *You will notice nothing yet.*

**A2 Remove Feature tells the truth.** Today, when GrayMoon runs in Docker, Remove Feature cannot see your folders and thinks they are all missing. That can delete work you never saw. Now it asks the Worker. If the Worker cannot be reached, Remove says "Could not check this repository" and is disabled. *You will notice the Remove dialog shows real uncommitted changes, also in Docker.*

**A3 Fresh commit and PR facts in Remove.** Remove checks pull requests and commit counts live, instead of using older cached values. "We don't know" is never treated as "safe". A repo with pushed commits but no PR is no longer called "completed". *You will notice more accurate warnings. If GitHub cannot be reached, you get a checkbox to confirm instead of a blocked button.*

---

## Step B: No leftover or mixed-up data

**B1 Remove cleans up its own data.** Removing a Feature also deletes its projects and dependency data from the database. *You will notice nothing directly. The database stops growing with leftovers.*

**B2 One-time cleanup of old leftovers.** On upgrade, GrayMoon deletes data left behind by Features you removed earlier, and makes sure it cannot pile up again. *You will notice nothing, except Restore and the Dependencies page may get shorter.*

**B3 Each view shows only its own projects.** Restore, the Dependencies page and grid tooltips show only the projects of the Workspace or Feature you are looking at. *You will notice that duplicate projects from old Features disappear from the Workspace.*

**B4 Close the remaining mix-ups.** Push plan, notifications, file lists and packages use the selected Feature's data. When you switch all repos to a branch that a Feature is using, GrayMoon skips those repos and tells you why, instead of failing halfway. *You will notice clearer bulk branch switches.*

**B5 Connector refresh cannot break a Feature.** If your Git provider stops returning a repository that a Feature uses, GrayMoon keeps it and shows a notice. Deleting a connector is refused while Features use its repositories. *You will notice a message on the Connectors page in that rare case.*

**B6 Editing a Workspace with Features.** Today, renaming a Workspace that has Features shows an error but still saves the rename, which breaks the Features. Now saving is all or nothing. Rename and root folder change are blocked (fields read-only, with the reason) while Features exist. *You will notice read-only name and root fields when the Workspace has Features.*

---

## Step C: Recovering from failures

**C1 Create is all or nothing.** If Create Feature fails before it starts making folders, nothing half-made is saved. *You will notice no "ghost" Features.*

**C2 Stuck Features are found automatically.** If GrayMoon crashed or was closed while creating or removing a Feature, it notices on the next start (and when the Worker reconnects) and marks that Feature "Needs attention". It also notices a Feature folder that went missing. It never deletes anything by itself. *You will notice that stuck Features show up instead of silently blocking the name.*

**C3 The selector shows every Feature.** Features that are being set up, removed or need attention appear with friendly labels: "Setting up...", "Removing...", "Needs attention", "Removal incomplete". Hover to see the error. A Feature that needs attention opens read-only, with a banner. *You will notice these labels in the Feature selector.*

**C4 Retry and Roll back.** Behind the scenes: a Feature that needs attention can retry the repos that failed, or roll back everything it created. A Feature you were removing can only be finished, never re-created. *You will notice it through C5.*

**C5 Status and repair panel.** A new panel lists each repo of a broken Feature with its state and error, plus the buttons Retry, Roll back and Remove (or only Continue removal when a removal was interrupted). If Create fails, this panel opens instead of a vague error message. It is also in the Feature menu as "Status and repair". *You will notice a clear way out of every failed Feature.*

**GATE-3: you test.** Kill GrayMoon during create, then repair. Force a create to fail, then retry and roll back. Kill GrayMoon during remove, then continue the removal.

---

## Step D: Remove does what it says

**D1 Leftover files are cleaned up.** After Git removes a Feature folder, the Worker deletes leftover files (like build output), retries when Windows has a file locked, and deletes the empty Feature folder. Strict safety checks make sure it only ever deletes inside the Features folder. Creating a Feature into an empty leftover folder now works. *You will notice no more leftover `features\<name>` folders.*

**D2 A removal report.** After Remove you see exactly what happened per repo: worktree removed, local branch deleted or kept (and why), leftover files still on disk (with an "Open folder" button). Progress is saved per repo, so a crash midway can be continued. *You will notice a report like "Removed 6 worktrees, 6 local branches" plus any warnings.*

**D3 Only the checkboxes that matter.** The Remove dialog asks about uncommitted changes only if there are some, and about unmerged branches only if there are some, and names the repos. Remove is enabled once you tick every checkbox shown. *You will notice a simpler dialog. A clean Feature shows no checkboxes at all.*

**D5 Locked worktrees.** If a Feature folder is locked in Git, the dialog shows this with the reason and offers "Unlock and remove". *You will notice a checkbox instead of a raw Git error.*

**D4 Delete remote branches too (optional).** Only if you decide yes for v1. An opt-in checkbox deletes the remote branches, but only if nobody else pushed to them since. *Default: not in v1. Remote branches stay, and the dialog says so.*

**I1 Remove judges the right branch** (from step I, done here). If you switched a Feature repo to another branch in a terminal, Remove still checks the Feature branch it is about to delete, and tells you the other branch is kept. *You will notice text like "This repository is on side, not on its Feature branch. side is kept."*

**GATE-2: you test.** Remove Features with uncommitted changes, unpushed commits, a locked file, a locked worktree and a repo on another branch, and check the dialog and report each time. Optionally repeat in Docker.

---

## Step E: Names and small UX fixes

**E1 Feature name check.** Names that Git, Windows or a terminal cannot handle (like `CON`, `a&b`, `my feature`) are refused while you type, with the reason. `Foo` and `foo` count as the same name. Existing Features keep working whatever their name. *You will notice a message under the name field and a disabled Create button.*

**E2 Header button.** The main toolbar button keeps showing the next action. It shows "Remove" only when all PRs of the Feature are merged or closed. A brand-new Feature shows "Feature", not "Remove". *You will notice the button no longer suggests removing a Feature you just made.*

**E3 "Open in..." with any folder path.** Opening a repo in Claude CLI, Terminal, VS Code, Cursor, Visual Studio or Explorer works even when the path has spaces or characters like `&` or `;`. *You will notice fewer "Open in..." failures.*

**E4 Linux Worker.** With a Linux Worker, Create Feature is refused with "Features need a Windows Worker in this version." Normal Workspace use on Linux is unchanged. *You will notice a disabled "+" in the selector on a Linux Worker.*

---

## Step F: Local security (still no login)

GrayMoon stays a tool without accounts or passwords. These steps stop other programs and web pages on your computer from reading your tokens or triggering actions.

**F1 Your own encryption key.** Your Git provider tokens get encrypted with a key unique to your install (stored next to the database), instead of a key built into the source code. Existing tokens are converted automatically on startup. *You will notice a `graymoon.key` file. If a token cannot be read, the connector asks you to enter it again.*

**F2 The Worker proves who it is.** GrayMoon and the Worker share a secret, so only the real Worker can connect and fetch tokens. Desktop sets it up for you. A script install asks for a short pairing code shown on the Worker page. An already installed Worker keeps working, with a notice "Reinstall the Worker to finish securing GrayMoon". *You will notice that notice once, and a pairing code for script installs.*

**F3 Web pages cannot control GrayMoon.** A web page open in your browser can no longer start a sync or push, or pretend to be the Worker. Your own scripts, `curl` and the GrayMoon window are not affected. *You will notice nothing in normal use.*

---

## Step G: Git hooks (live updates after terminal commits)

GrayMoon puts small Git hooks in your repos so the grid updates when you commit from a terminal. Background in [07-appendix-why-lane-g-hooks.md](07-appendix-why-lane-g-hooks.md).

**G1 Your own hooks are set aside, not deleted.** If a repo already has a hook of its own (not GrayMoon's), GrayMoon renames it to `<hook>.replaced-by-graymoon` and writes its own, and logs a warning in the Worker log. Nothing is shown in the app. A repo using `core.hooksPath` (husky, lefthook) is left alone. GrayMoon also stops rewriting its hook files on every Sync. *Caution: a renamed hook of yours no longer runs until you rename it back. Chaining your hook with GrayMoon's is a later idea.*

**G2, G3, G4 (removing hooks when a repo leaves a Workspace, and cleaning up old ones) moved out of this plan.** They are general Workspace housekeeping, not worktree work, and live in [12-hook-cleanup-plan.md](12-hook-cleanup-plan.md), to be done after the worktree release. *Until then, repos you remove keep their old GrayMoon hooks.*

**B7 You cannot delete a Workspace that still has Features.** Remove Workspace says "Remove the Features first, then delete the Workspace." and the Remove button stays disabled until the Features are gone. This part was pulled back from the hook plan into the worktree release. *You will notice it only if you try to delete a Workspace that has Features.*

---

## Step I: Branch rules inside a Feature

Background in [09-switch-branch-in-feature-analysis.md](09-switch-branch-in-feature-analysis.md). The rule: every repo in a Feature stays on the Feature's branch (or on its tag, for tag-pinned repos). The Workspace's branch dialog does not change.

**I2 "Return to Feature branch".** In a Feature, the branch dialog marks the Feature's own branch "This Feature" and lets you check it out. If a repo is on another branch, you see a warning and a "Return to Feature branch" button. A small bug fix: the upgrade badge path now also shows which branches Features are using. *You will notice an easy way back.*

**I3 The dialog only allows safe actions in a Feature.** In a Feature there is no New Branch tab and no Return to Default. You cannot check out other branches (the tooltip explains why), and tags only for tag-pinned repos. Deleting a branch warns that branches are shared with the Workspace and all Features. *You will notice a simpler branch dialog inside Features.*

**I4 "Off Feature branch" badge.** If a Feature repo ends up on another branch (for example after a terminal checkout), the grid shows "Off Feature branch". Click it to go back. This never marks the Feature as broken. *You will notice the badge within seconds of a terminal switch.*

---

## Step H: Logging

**H1 Better logs.** Every create, remove, repair, rollback and automatic check writes a clear log line with the Feature name, duration and result, plus one line per failed repo. *You will notice nothing, but problems are easier to explain from the log.*

**GATE-4: you test.** Feature names, the header button, Open in... with a path that has a space, the security checks in a browser console, Worker reinstall, hooks with husky and your own hook, branch rules and the drift badge, editing a Workspace that has a Feature, and a Linux Worker if you have one.

---

## Step R: Release

**R1 Documentation.** A user guide for Features, a troubleshooting page (including a manual cleanup recipe and how to go back to the previous version), an operations page for Docker and settings, and a changelog. The old design docs get a "superseded" banner. Worktrees are marked "Preview" until the final gate passes.

**R2 Off switch.** Dropped (owner, 2026-10-04). Features are part of GrayMoon, so there is no setting to hide them.

**R3 Version check.** An automatic test checks that GrayMoon calculates the same versions as GitVersion on a real checkout. If it does not, you are told, and it becomes a new task.

**R4 Final check.** All tests run in both repos, and every item on the release checklist is ticked or moved to v1.1 on purpose.

**GATE-5: you sign off.** Fresh install on a clean profile, upgrade over a real 0.1.0 install, repeat the most important Remove and recovery tests, read the user guide as a new user, and run the Workspace smoke test on real data. PASSED means v1 is ready.

---

## What does not change

- No login, no accounts.
- The Workspace works as it does today. The intended visible differences are: no duplicate projects from old Features, read-only rename while Features exist, a Feature badge on one more branch dialog path, and a refusal to delete a Workspace or connector that Features still use.
- Moved to after v1: choosing which repos go into a Feature, adopting an existing branch as a Feature, chaining your hooks with GrayMoon's, and very long Windows paths.
