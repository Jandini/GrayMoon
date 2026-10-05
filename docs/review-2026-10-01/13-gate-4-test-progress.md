# GATE-4 test progress

Owner test script for GATE-4 (source: `06-worktree-release-implementation-plan.md`, section "GATE-4 Names, header, security, hooks, Desktop").

- **Build under test:** `worktree-release-implementation` (commit `48bc7e6` or later)
- **Tester:** Matt
- **Started:** 2026-10-04
- **Overall result:** IN PROGRESS. Agent-checked parts are done (see notes); the owner-only steps are still open.

How to use: tick a box when the **Expected** result happened. If it did not, leave it unticked, set the step status to FAIL and write what you saw under **Notes**. When every step is PASS (or SKIPPED with a reason), reply PASSED in chat.

Status values: `TODO`, `PASS`, `FAIL`, `SKIPPED`.

## Summary

| Step | Area | Status | Notes |
|---|---|---|---|
| 1 | Feature names | PASS | Duplicate name is refused when Create is pressed, not while typing (owner accepted 2026-10-04: live check not needed) |
| 2 | Header button | PASS | Fresh Feature reads "Feature"; after a real merged PR it reads "Remove" (Workspace DeepSpace, 2026-10-04) |
| 3 | Open in... | TODO | |
| 4 | Security (browser) | PASS | Agent-checked; REST script line is yours |
| 5 | Worker reinstall and secret | TODO | Before-reinstall part done; reinstall and 401 checks are yours |
| 6 | Hooks (G1) | PASS | Agent-checked on Workspace DeepSpace (a Deep test repo): all 10 lines OK |
| 7 | Branch rules in a Feature | PASS | All lines checked, including tag-pinned and the Workspace Locals Feature badge |
| 7b | Long paths (Windows) | PASS | Deep-path repo (over 260 characters): Create and Remove OK, no "Filename too long"; GitVersion fails on such paths (fixed in code, see Issues) |
| 8 | Workspace edits with a Feature | TODO | Save, rename lock, delete dialog OK; connector delete and the last two lines are yours |
| 9 | Linux Worker | TODO | |
| 10 | WORKSPACE-SMOKE | TODO | |

---

## 1. Feature names

Status: PASS (agent-checked 2026-10-04 on Workspace Test, build .134; the duplicate-name line was accepted by the owner as refused on Create, not while typing)

In the New Feature dialog, type each name below.

- [x] `CON`: shows a reason, Create disabled ("Feature name cannot use the reserved Windows name 'CON'.")
- [x] `a&b`: shows a reason, Create disabled ("Feature name cannot contain '&'.")
- [x] `my feature`: shows a reason, Create disabled ("Feature name cannot contain spaces.")
- [x] `Foo` (when `foo` already exists): refused with "A Feature named 'Foo' already exists." when Create is pressed; nothing is created. Accepted by the owner: a live message while typing is not needed
- [x] `feature/login`: works (valid, Create enabled; not created)

Notes: Tried `Gate4-B` and `gate4-b` while `gate4-b` existed. While typing there is no message and Create stays ENABLED. Pressing Create is refused with "A Feature named 'Gate4-B' already exists." inside the dialog and nothing is created, so the case-insensitive rule works but the script's "Create is disabled" is not what happens. In my screenshot a few seconds later the message was gone and the field was empty. DECISION (owner, 2026-10-04): a live duplicate check while typing is not needed. The behaviour is accepted as it is, and the script wording for this line now reads "refused when Create is pressed". Nothing is planned for v1.1 either.

## 2. Header button

Status: PASS (agent-checked 2026-10-04: Workspace Test for the fresh Feature, Workspace DeepSpace with a real GitHub PR for the merged half, build .134)

- [x] Fresh Feature: header button reads "Feature", not "Remove" (gate4-b, top right button reads Feature with a dropdown)
- [x] After its PRs are merged: header button reads "Remove" (Feature gate4-br on DeepSpace: header "Create PR" before the PR, "Feature" while PR #1 was open, "Remove" right after the in-app Squash and merge)

Notes: Flow used: commit and push in the Feature folder, Sync, Create PR from the row badge (draft off), merge with the in-app merge chip, then Remove Feature. Remove dialog said "pull requests are merged, safe to remove", both "Delete local Feature branches" and "Delete remote Feature branches" were ticked; afterwards the worktree folder, the local branch and the GitHub branch were all gone and `main` on GitHub held the squash commit. Side finding: the merge dialog said "Local branch is not in sync, 1 uncommitted change" although `git status` was clean (stale Git Changes snapshot after a terminal commit; informational only, "Merge anyway" was needed). See Issues found.

## 3. Open in...

Status: TODO

Create Feature `gate4-x` in a Workspace whose path has a space. For each tool, open once for a Workspace repo and once for the Feature. Expected: opens in the right folder.

| Tool | Workspace repo | Feature |
|---|---|---|
| Claude CLI (window stays open) | [ ] | [ ] |
| Terminal | [ ] | [ ] |
| VS Code | [ ] | [ ] |
| Cursor | [ ] | [ ] |
| Visual Studio | [ ] | [ ] |
| Explorer | [ ] | [ ] |

Notes:

## 4. Security

Status: PASS (checked by the agent on 2026-10-04, build 0.1.1-worktree-release-implementation.134)

In a normal browser tab (not GrayMoon), open the console.

- [x] Run `fetch('http://localhost:8384/api/workspaces/1/sync', {method:'POST', mode:'no-cors'})`. Expected: nothing happens in GrayMoon, no sync starts
- [x] Run `new WebSocket('ws://localhost:8384/hub/agent')`. Expected: the connection fails (403)
- [x] The real Worker stays connected and GrayMoon shows no Worker change
- [ ] REST API script (only if you use them): works as before (not run by the agent; a script without an Origin header would start a real sync, so this one is yours)

Notes: Run from a tab on https://example.com. The fetch came back opaque (always does with no-cors), so it proves nothing by itself; the App log (C:\ProgramData\GrayMoon\Logs\graymoon-app-20261004.log) shows no sync for Workspace 1 afterwards, only your own sync of the Test Workspace (id 5) at 21:44:26. The WebSocket failed. With curl and `Origin: https://evil.example` the server answered 403 for the hub upgrade, for POST /api/workspaces/1/sync, and for /repos/1/connector; `Origin: null` on the sync POST also gave 403. No new Worker connect lines in the log after 21:42:05 and the Worker process kept running. The UI part of "no Worker change" is a log check, not a look at the screen.

## 5. Worker reinstall and secret

Status: TODO

Before reinstalling:

- [x] Worker still connects, with a "Reinstall the Worker" notice (log shows the warning "A Worker connected without a worker secret. Reinstall the Worker..." at 21:42:05; the on-screen notice is for you to eyeball)
- [x] Sync works (Test Workspace sync completed at 21:44:39)

Reinstall the Worker from GrayMoon:

- [ ] Worker connects, the notice is gone
- [ ] Sync works

Secret checks:

- [x] `http://localhost:8384/api/worker/install`: the script text contains no secret (200; only variable names and comments mention a secret, it reads it from `%ProgramData%\GrayMoon\worker.secret` at run time)
- [ ] `http://localhost:8384/repos/1/connector` in the browser: no token shown (403)
- [ ] `curl http://localhost:8384/repos/1/connector` from a terminal: 401

Notes: The last two secret checks are not meaningful until the Worker has been reinstalled. Right now the App has not seen a Worker with a secret, so a request with no secret and no Origin header is let through by design (upgrade window). Example: curl /repos/1/connector with no secret gave 404 (repo 1 does not exist; the request reached the endpoint) instead of 401. Do them after the reinstall. With a browser-style `Origin` header the same URL already gives 403.

## 6. Hooks (G1)

Status: PASS (agent-checked 2026-10-04 on Workspace DeepSpace, build .134)

The cleanup steps moved to GATE-H in `12-hook-cleanup-plan.md`.

Repo with `core.hooksPath`:

- [x] In a test repo run `git config core.hooksPath .husky`, then Sync
- [x] `.husky` contents are unchanged
- [x] No GrayMoon hooks written in `.husky`
- [x] One Warning in the Worker log

Repo with your own hook:

- [x] Add your own `.git/hooks/pre-push` (for example `echo "user hook"`), then Sync
- [x] Your hook is now `.git/hooks/pre-push.replaced-by-graymoon` with its content intact
- [x] GrayMoon's own `pre-push` is in its place
- [x] Worker log has a Warning naming the rename

Repo where GrayMoon hooks already exist:

- [x] Commit from a terminal: the grid updates within seconds (2.4 s, also on the over-260-character path)
- [x] Sync twice: the hook files are not rewritten the second time (modified time unchanged)

Notes: Done on the DeepSpace test repo (Deep). Round-2 finding while doing the terminal commit on the deep path: GitVersion crashed (LibGit2Sharp "path too long") and the hook flow dropped the repo's branch. Fixed in code (the branch now falls back to `git branch --show-current`, a failed GitVersion is reported as unresolved) with tests; the installed build .134 still has the old behaviour, so see the retest section.

## 7. Branch rules in a Feature

Status: PASS (agent-checked 2026-10-04 on Workspace Test, and the tag-pinned and Workspace Locals lines on Workspace DeepSpace, build .134)

Create Feature `gate4-b` and open a repo's branch dialog.

- [x] No New Branch tab (tabs are Locals, Remotes, Tags)
- [x] No Return to Default (no such button on the Feature page or in the dialog)
- [x] Other branches cannot be checked out, and the tooltip explains why (Check out disabled; "A Feature keeps every repository on its Feature branch 'gate4-b'. Use the Workspace to check out another branch.")
- [x] Tags cannot be checked out unless the repo is pinned to a tag ("Tags can be checked out only in repositories pinned to a tag. Use the Workspace to check out a tag.")
- [x] Delete a throwaway branch from inside the Feature: the confirmation says branches are shared by the Workspace and all Features (read on 'develop', then Cancelled: "Branches are shared by the Workspace and every Feature of this repository. Deleting it removes it everywhere.")
- [x] In a terminal, in that repo's Feature folder, run `git switch -c side`: within seconds the grid shows "off feature branch" (Janda.Backup.Sequential, seen within about 10 seconds)
- [x] Click it and press "Return to Feature branch": the repo is back on `gate4-b` and the badge is gone (the temporary branch 'side' was deleted afterwards)
- [x] Tag-pinned repo (if you have one): in the Feature, open its upgrade badge and check out a newer tag. It works and the repo stays on the tag (gate4-tag pinned to v1.0.0, checked out v1.1.0, repo stayed detached on the tag)
- [x] In the Workspace, open a repo's upgrade badge, then the Locals tab: branches held by a Feature show the Feature badge; everything else as before (the Feature's branch appears in the Locals list only after a Sync, see Issues)

Notes: The branch dialog opens by clicking the branch name in the Branch column.

## 7b. Long paths (Windows)

Status: PASS (agent-checked 2026-10-04 on Workspace Test and on Workspace DeepSpace with a repo whose paths are over 260 characters, build .134)

In a test repo commit a file nested deeper than 260 characters (or use a repo with a deep `node_modules`).

- [x] Create a Feature: succeeds (gate4-b on 6 repos, 3.2 s; this is a normal repo, not a deep one)
- [x] No "Filename too long" in the Feature status panel (deep-path Feature gate4-deep on DeepSpace)
- [x] Remove the Feature: succeeds (gate4-deep: Remove finished, Outcome=Succeeded, 3.8 s, folder gone)
- [x] `git config --local core.longpaths` in the primary checkout prints `true` (all 6 repos under C:\Workspace\Test)

Notes: Done on the DeepSpace test repo (Deep) with a committed directory tree deeper than 260 characters. core.longpaths=true was set automatically on the primary checkout. The automated test GitServiceLongPathsTests also covers create and remove with a 260+ character path and passes.

## 8. Workspace edits while a Feature exists

Status: partly done; the connector delete and the last two lines are left to you (agent-checked 2026-10-04 on Workspace Test, build .134)

Keep `gate4-b` from step 7.

- [x] Edit the Workspace and press Save without changing anything: saves, no error (log: "User edited workspace Test")
- [x] Try to rename it: the name field is read-only with the reason, nothing changes (name and path are locked; "Rename and root changes are not possible while Features exist. Remove Features first.")
- [ ] Try to delete the connector of its repositories: refused with "Remove the Features that use this connector's repositories first.", nothing deleted
- [x] Open Remove Workspace on it: the dialog says "Remove the Features first, then delete the Workspace." and Remove is disabled (opened and cancelled)
- [ ] Remove `gate4-b`, then rename the Workspace: works as before
- [ ] Remove Workspace on a test Workspace with no Features: deleted as before

Notes: After Save an "Import Repositories" prompt appeared ("Found 6 repositories in this workspace that match your existing repositories by URL"). It comes from WorkspaceImportModal, an old feature (PR 26), and I answered No. Not connector delete, not rename after Remove, not delete of a Feature-less Workspace: those can change real data, so they are yours. The Feature gate4-b is still there on purpose, for your steps 7 and 8.

## 9. Linux Worker

Status: TODO (skip if you have none; DEC-7 and E4)

- [ ] Connect a Linux Worker, create Feature `gate4-l`
- [ ] Remove it: works like on Windows
- [ ] The Feature paths the Worker reports use `/` separators

Notes:

## 10. WORKSPACE-SMOKE

Status: TODO

- [ ] Run WORKSPACE-SMOKE: passes

Notes:

---

## Merge readiness (updated 2026-10-04, evening)

Short answer: **not yet**. The code is ready; the release steps are not done.

Technical state, checked by the agent:

| Check | Result |
|---|---|
| Build, both repos | Clean, 0 warnings |
| Tests, GrayMoon repo | Common 234 passed; App 812 passed; Agent 279 passed, 1 skipped on purpose (the GitVersion Feature-branch mismatch, v1.1). Run 2026-10-05 |
| Tests, GrayMoon.Desktop repo | 199 passed |
| Merge into main | Clean in both repos: main has not moved (GrayMoon is 134 commits ahead, Desktop is 54 ahead), no conflicts |
| Part F items marked "release blocker" | None |
| Uncommitted work | The empty-repo and hook-flow fixes with their tests, this file, the `00-README.md` index line, the `06` changelog, and the Desktop README |

What still stands between this branch and the merge (from the tracker in `06`):

- [ ] GATE-4: finish the owner steps in this file (3 Open in..., 5 reinstall and the two checks after it, 8 connector delete and the last two lines, 9 Linux, 10 WORKSPACE-SMOKE) and the retest of the empty-repo fix on a new build. Steps 1, 2, 6, 7 and 7b are closed (agent-checked live on Workspace DeepSpace)
- [ ] R1: user guide, troubleshooting (with the downgrade note), operations, changelog, "superseded" banners on the old design docs (starts after GATE-4)
- [ ] R4: full regression run, checklist walk, then Publish-GrayMoonBundle.ps1 (final task, runs once)
- [ ] GATE-5: release candidate sign-off, including WORKSPACE-SMOKE on the release build

Known and accepted, not blocking: GitVersion default-tip mismatch on a Feature branch (v1.1, Part F). A duplicate Feature name is refused when Create is pressed, not while typing: the owner accepted this on 2026-10-04, so it is not a defect and not a v1.1 item.
---

## Retest: empty repository and the red Sync button (found 2026-10-04, fixed in code, needs a new build)

Found on Workspace `DeepSpace` (an empty GitHub repo added to a Workspace). Two defects: (a) GitVersion failed with `No commits found on the current branch` and the repo lost its branch; (b) after a README commit that was not pushed, the Sync button stayed red because the empty remote has no default branch (status stayed NeedsSync). Fix: a GitVersion failure leaves the version unresolved (red `unresolved` in the version column) and does not affect the repo or Workspace; a remote with no branches counts as in sync. Tests added in the Agent and App suites (all green).

Needs a build that contains the fix, then:

- [ ] New empty GitHub repo, added to a Workspace, Sync: sync completes, branch is shown, version column shows red `unresolved`, Sync button is blue
- [ ] Hover `unresolved`: tooltip explains GitVersion could not compute a version; clicking it copies nothing
- [ ] Commit a README locally (do not push), Sync: version appears (for example `0.1.0+0...`), Sync button stays blue
- [ ] Push the commit, Sync: still blue, default branch shows `main`
- [ ] Worker log: the GitVersion failure is a Warning, not an Error

Notes: (your results here) Agent check on the old build .134, Workspace DeepSpace (Deep): the Sync button went red to blue as soon as `main` was pushed, which confirms the root cause (an empty remote has no default branch). Bullets 1 to 4 will only be fully visible on a build with the fix.

---

## Optional: H1 logging check

Not part of the plan's gate. While doing steps 7b and 8, watch the App log.

- [x] `Feature Create started` and `finished ... Outcome=Succeeded`, with `DurationMs` (21:49: started, six per-repo Debug lines, finished DurationMs=3242)
- [x] `Feature Remove started` and `finished ... Outcome=Succeeded` (DeepSpace: gate4-deep 3.8 s, gate4-br 5.4 s; Create of gate4-br 0.8 s)

Notes:

## Issues found

| # | Step | What I saw | Severity (blocker / v1.1) |
|---|---|---|---|
| A | 7b / empty repo | Red Sync button after a successful sync of an empty remote; GitVersion error wiped the branch | Fixed in code, needs retest on a new build |
| B | 7b / 6 | GitVersion crashes on paths over 260 characters (LibGit2Sharp), including in the commit-hook flow, which lost the branch | Fixed in code (branch fallback, red `unresolved`), needs retest on a new build. The GitVersion tool itself ignores `core.longpaths`: v1.1 |
| C | 6 | Hook notification carries no remote branch list, so an empty-remote repo flipped back to NeedsSync after a terminal commit | Fixed in code, with a test |
| D | 7 | A new Feature's branch shows in the Workspace Locals list only after a Sync | v1.1 |
| E | 2 | Merge dialog showed "1 uncommitted change" for a clean Feature worktree (stale Git Changes snapshot after a terminal commit; informational only, needs "Merge anyway") | v1.1 |
| F | log | Once, a unique-constraint error inserting the Git Changes snapshot (`WorkspaceGitContextRepositoryStatuses`), caught and not user visible: two writers race in `GitChangesSnapshotPushHandler` (probably also the cause of E) | v1.1, non-blocking |
| G | 2 | After Remove with "Delete remote Feature branches", the Workspace repo still lists `origin/gate4-br` until a fetch with prune | v1.1 |
| H | branch dialog | Owner report 2026-10-05: created a branch in all repositories, switched one repository back to main, then deleting the new branch from the switch-branch dialog said "Cannot delete the current branch" although the repository was on main. Cause: the delete check read the branch from a link tracked by the page's long-lived database context, which still held the old branch (the sync had saved main through another context). Worker was never called. | Fixed in code (fresh read), regression tests added; needs a check on a new build |

## Final result

- [ ] All steps PASS or SKIPPED with a reason
- [ ] Reply PASSED in chat (the agent then sets GATE-4 to PASSED in `06` and starts R1)
