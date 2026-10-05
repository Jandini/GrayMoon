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
| 2 | Header button | TODO | Fresh Feature label OK; merged half needs a real PR |
| 3 | Open in... | TODO | |
| 4 | Security (browser) | PASS | Agent-checked; REST script line is yours |
| 5 | Worker reinstall and secret | TODO | Before-reinstall part done; reinstall and 401 checks are yours |
| 6 | Hooks (G1) | TODO | |
| 7 | Branch rules in a Feature | TODO | Dialog rules and off-branch badge OK; tag-pinned and Workspace Locals badge are yours |
| 7b | Long paths (Windows) | TODO | core.longpaths=true and Create OK; deep-path repo is yours |
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

Status: half done (the merged half needs a real merged PR) (agent-checked 2026-10-04 on Workspace Test, build .134)

- [x] Fresh Feature: header button reads "Feature", not "Remove" (gate4-b, top right button reads Feature with a dropdown)
- [ ] After its PRs are merged: header button reads "Remove"

Notes:

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

Status: TODO

The cleanup steps moved to GATE-H in `12-hook-cleanup-plan.md`.

Repo with `core.hooksPath`:

- [ ] In a test repo run `git config core.hooksPath .husky`, then Sync
- [ ] `.husky` contents are unchanged
- [ ] No GrayMoon hooks written in `.husky`
- [ ] One Warning in the Worker log

Repo with your own hook:

- [ ] Add your own `.git/hooks/pre-push` (for example `echo "user hook"`), then Sync
- [ ] Your hook is now `.git/hooks/pre-push.replaced-by-graymoon` with its content intact
- [ ] GrayMoon's own `pre-push` is in its place
- [ ] Worker log has a Warning naming the rename

Repo where GrayMoon hooks already exist:

- [ ] Commit from a terminal: the grid updates within seconds
- [ ] Sync twice: the hook files are not rewritten the second time (modified time unchanged)

Notes:

## 7. Branch rules in a Feature

Status: mostly PASS; tag-pinned and the Workspace Locals badge are left (agent-checked 2026-10-04 on Workspace Test, build .134)

Create Feature `gate4-b` and open a repo's branch dialog.

- [x] No New Branch tab (tabs are Locals, Remotes, Tags)
- [x] No Return to Default (no such button on the Feature page or in the dialog)
- [x] Other branches cannot be checked out, and the tooltip explains why (Check out disabled; "A Feature keeps every repository on its Feature branch 'gate4-b'. Use the Workspace to check out another branch.")
- [x] Tags cannot be checked out unless the repo is pinned to a tag ("Tags can be checked out only in repositories pinned to a tag. Use the Workspace to check out a tag.")
- [x] Delete a throwaway branch from inside the Feature: the confirmation says branches are shared by the Workspace and all Features (read on 'develop', then Cancelled: "Branches are shared by the Workspace and every Feature of this repository. Deleting it removes it everywhere.")
- [x] In a terminal, in that repo's Feature folder, run `git switch -c side`: within seconds the grid shows "off feature branch" (Janda.Backup.Sequential, seen within about 10 seconds)
- [x] Click it and press "Return to Feature branch": the repo is back on `gate4-b` and the badge is gone (the temporary branch 'side' was deleted afterwards)
- [ ] Tag-pinned repo (if you have one): in the Feature, open its upgrade badge and check out a newer tag. It works and the repo stays on the tag
- [ ] In the Workspace, open a repo's upgrade badge, then the Locals tab: branches held by a Feature show the Feature badge; everything else as before

Notes: The branch dialog opens by clicking the branch name in the Branch column. Left for you: the tag-pinned repo, and the Workspace-side upgrade badge and Locals tab showing the Feature badge.

## 7b. Long paths (Windows)

Status: partly done (agent-checked 2026-10-04 on Workspace Test, build .134)

In a test repo commit a file nested deeper than 260 characters (or use a repo with a deep `node_modules`).

- [x] Create a Feature: succeeds (gate4-b on 6 repos, 3.2 s; this is a normal repo, not a deep one)
- [ ] No "Filename too long" in the Feature status panel
- [ ] Remove the Feature: succeeds
- [x] `git config --local core.longpaths` in the primary checkout prints `true` (all 6 repos under C:\Workspace\Test)

Notes: Still yours: a repo with a file path over 260 characters, and the Remove. The automated test GitServiceLongPathsTests covers create and remove with a 260+ character path and passes.

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
| Tests, GrayMoon repo | Common 234 passed; App 801 passed; Agent 274 passed, 1 skipped on purpose (the GitVersion Feature-branch mismatch, v1.1) |
| Tests, GrayMoon.Desktop repo | 199 passed |
| Merge into main | Clean in both repos: main has not moved (GrayMoon is 134 commits ahead, Desktop is 54 ahead), no conflicts |
| Part F items marked "release blocker" | None |
| Uncommitted work | Only this file and the `00-README.md` index line |

What still stands between this branch and the merge (from the tracker in `06`):

- [ ] GATE-4: finish the owner steps in this file (3, 5 reinstall and the two checks after it, 6, 9, the leftovers in 2, 7, 7b, 8). Step 1 is closed (duplicate name refused on Create is accepted)
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

Notes: (your results here)

---

## Optional: H1 logging check

Not part of the plan's gate. While doing steps 7b and 8, watch the App log.

- [x] `Feature Create started` and `finished ... Outcome=Succeeded`, with `DurationMs` (21:49: started, six per-repo Debug lines, finished DurationMs=3242)
- [ ] `Feature Remove started` and `finished ... Outcome=Succeeded` (not run: gate4-b is still there)

Notes:

## Issues found

| # | Step | What I saw | Severity (blocker / v1.1) |
|---|---|---|---|
| | | | |

## Final result

- [ ] All steps PASS or SKIPPED with a reason
- [ ] Reply PASSED in chat (the agent then sets GATE-4 to PASSED in `06` and starts R1)
