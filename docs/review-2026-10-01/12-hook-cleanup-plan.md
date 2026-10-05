# Hook cleanup plan (G2, G3, G4) - separate from worktrees v1

Moved out of `06-worktree-release-implementation-plan.md` on 2026-10-04 by the owner: removing GrayMoon's Git hooks when a repository leaves a Workspace is general Workspace housekeeping, not part of the worktree feature. **Start this plan only after the worktree plan (06) is finished, GATE-5 included.** It is not a dependency of any worktree gate. (The Workspace-delete guard that used to be part of G3 was pulled back into the worktree plan as B7 and is done; this plan only unhooks.)

The unit ids (G2, G3, G4) are kept so the references in `07-appendix-why-lane-g-hooks.md` (section 6) and `08-plan-regression-risk-review.md` (R-G2, R-G3a to R-G3c, R-G4) stay valid.

> **AI agent: this document is your brief for these three units.** Read Part A of `06-worktree-release-implementation-plan.md` first. Its rules (A2 start-of-session routine, A3 rules, A4 how to implement a unit including "pin the Workspace first", A5 when to stop, A6 final message, A7 working in parallel) and its build and test commands apply here unchanged. Use **this** file's tracker and changelog instead of 06's Part C and Part E. The owner does manual testing at the gate below. You never skip it.

---

## 1. What is already in place (from the worktree plan)

- **G1 is DONE** (in 06): `GitService.WriteSyncHooksAsync` resolves the hooks folder with `git rev-parse --git-path hooks`; GrayMoon recognises its own hook by the first comment line after the shebang starting with `# Created by GrayMoon.Agent` (same in 0.1.0 and today); a foreign hook is renamed to `<hook>.replaced-by-graymoon` (UTC-timestamped name if taken) and a Worker Warning is logged; `post-update` is no longer written; a `core.hooksPath` outside the common Git dir writes and renames nothing. There is no `hookStatus` response field and no warning icon.
- **B5 and B6 are DONE** (in 06): connector refresh and delete keep a Feature's repositories, and Edit Workspace is transactional with rename and root change blocked while Features exist. G3 edits the same repository files, so it must start from a tree that contains them.
- Background and reasoning: `07-appendix-why-lane-g-hooks.md` section 6. Risks: `08-plan-regression-risk-review.md` section "Lane G".

## 2. Decisions (owner answers; agents read, never decide)

| ID | Question | Default | Blocks | Status | Answer |
|---|---|---|---|---|---|
| HK-1 | ~~Ship without the Workspace-delete guard until this plan runs?~~ | n/a | none | SUPERSEDED | Owner reversed this on 2026-10-04: the guard stayed in the worktree plan as unit B7 (DONE). A Workspace with Features cannot be deleted in the worktree release. G3 here is unhooking only. |
| HK-2 | When unhooking, what happens to `<hook>.replaced-by-graymoon` files that G1 made from a user's own hooks? | Leave them untouched and log one Warning per repository naming them, so the user can rename them back. No restore, no chaining. | G2 | OPEN | |
| HK-3 | Is G4 (self-heal from stale pings) in this plan, or dropped? | In, as the last unit. It is the only cleanup for hooks left behind by removals that happened before G3 existed (those paths are no longer in the database). | G4 | OPEN | |

## 3. Tracker

Status values: `TODO`, `IN PROGRESS`, `BLOCKED`, `DONE`, and for the gate `WAITING`, `READY FOR USER TEST`, `PASSED`, `FAILED`.

| Order | Unit | Title | Depends on | Size | Status | Owner | Notes |
|---|---|---|---|---|---|---|---|
| 1 | G2 | Worker `UnhookRepository` command | worktree plan GATE-5, HK-2 | S | TODO | | |
| 2 | G3 | Unhook when repositories leave a Workspace | G2 | M | TODO | | Shares `WorkspaceRepository.cs`, `RepositoryRepository.cs`, `ConnectorRepository.cs` with the already-done B5/B6 |
| 3 | G4 | Self-heal stale hooks from pings | G2, G3, HK-3 | S | TODO | | |
| 4 | GATE-H | Hook cleanup (owner) | G3, G4 | - | WAITING | | |

Hot files (see 06 A7): `App/Repositories/WorkspaceRepository.cs`, `RepositoryRepository.cs`, `ConnectorRepository.cs`. One agent at a time on this plan.

---

## 4. Units

Template and path conventions are the same as in 06 Part D. Paths are relative to the folder containing `GrayMoon` and `GrayMoon.Desktop`. `App` = `GrayMoon/src/GrayMoon.App`, `Agent` = `GrayMoon/src/GrayMoon.Agent`, `Common` = `GrayMoon/src/GrayMoon.Common`.

### G2 Worker `UnhookRepository` command

- **Goal:** The Worker can safely remove GrayMoon's hooks from one repository folder.
- **Ref:** `07-appendix-why-lane-g-hooks.md` section 6. Requires HK-2.
- **Read first:** `07` section 6; `GrayMoon/.claude/skills/add-agent-command/SKILL.md`; G1's hooks-folder resolution and marker recognition in `GitService.cs` (`WriteSyncHooksAsync`, the hooks-location helper, the marker check).
- **Touches:** new DTOs, `Agent/Commands/UnhookRepositoryCommand.cs`, a new `GitService.RemoveSyncHooksAsync`, registrations per the skill, constant in `AgentHubMethods.cs`, tests.
- **Steps:**
  1. Request: `repositoryPath`, `workspaceId`. Response: `outcome` (`Removed`, `NothingToRemove`, `PathMissing`, `NotAGitRepository`, `HooksPathCustom`, `Error`), `removedHooks` (names), `skippedHooks` (names with a reason), `message`.
  2. Resolve the hooks folder with G1's helper (`git rev-parse --git-path hooks`). If the folder or repository does not exist, return `PathMissing` or `NotAGitRepository`. Never throw.
  3. For each of `post-commit`, `post-checkout`, `post-merge`, `post-update`, `pre-push`: delete the file **only if** it carries the GrayMoon marker (`# Created by GrayMoon.Agent`) **and** its payload names the requested `workspaceId`. Parse the id with a regex that accepts both payload styles, the current escaped `\"workspaceId\":N` and the 0.1.0 single-quoted `"workspaceId":N`, for example `workspaceId\\?"\s*:\s*(\d+)`. Otherwise skip it and give the reason (`NotGrayMoon`, `OtherWorkspace`). `post-update` is still removed if an older GrayMoon wrote it, even though G1 no longer writes it.
  4. Per HK-2 (default): never delete, rename or restore `<hook>.replaced-by-graymoon` files. If any exist next to a hook GrayMoon removed, list them in `message` and log one Warning.
  5. Read-only pool is wrong for this (it deletes files); register it in the main pool.
- **Tests to add (real git):** GrayMoon hooks for workspace 3 are removed when asked for 3; kept when asked for 4 (`OtherWorkspace`); a user hook is kept (`NotGrayMoon`); the 0.1.0 hook text is recognised and removed (copy it from `git show 0.1.0:src/GrayMoon.Agent/Services/GitService.cs`); missing folder -> `PathMissing`; plain folder -> `NotAGitRepository`; `core.hooksPath` set -> `HooksPathCustom` and nothing deleted; in a linked worktree, the primary checkout's common hooks are the ones removed; a `pre-push.replaced-by-graymoon` file survives and is named in the result.
- **Done when:** tests pass; a test proves an old-shape request (missing optional fields) is handled and an old App never sends this command.
- **Regression guard (R-G2):** deletes only files that pass both checks in step 3; never deletes the hooks folder itself or any other file. An old App never sends this command, so nothing changes for it.

### G3 Unhook when repositories leave a Workspace

- **Goal:** Removing one or more repositories, deleting a Workspace, or a connector change that drops repositories unhooks those folders. Failures only log Warnings and never fail the user's action.
- **Ref:** `07` section 6. The Workspace-delete Feature guard is not part of this unit; it is B7 in the worktree plan and is already in `WorkspaceRepository.DeleteAsync`.
- **Read first:** `07` section 6.1 (the four removal paths); `App/Repositories/WorkspaceRepository.cs` `ReplaceRepositoriesAsync` and `DeleteAsync`; `App/Components/Pages/Workspaces.razor` `DeleteWorkspaceAsync`; `App/Repositories/RepositoryRepository.cs` (merge, `toDeleteIds`); `App/Repositories/ConnectorRepository.cs` `DeleteAsync`; `App/Services/Workspaces/WorkspaceService.cs` `GetWorkspacePath`; `App/Services/Features/WorkspaceContextPathResolver.cs` `GetRepositoryPathAsync`; `GrayMoon/.cursor/rules/ui-application-facades.mdc`.
- **Touches:** new `App/Services/Hooks/WorkspaceHookCleanupService.cs` (+ interface), the four removal paths, a Workspace facade method for delete, `Workspaces.razor`, tests, Desktop README bullet.
- **Steps:**
  1. `WorkspaceHookCleanupService.UnhookAsync(IReadOnlyList<(int WorkspaceId, int RepositoryId, string RepositoryPath)> targets, CancellationToken)`: if the Worker is not connected, log one Warning listing the targets and return. Otherwise send `UnhookRepository` per target, at most 4 at a time, with a 30 s timeout each. Log the outcome per repository: Information for `Removed` and `NothingToRemove`, Warning for everything else, including `Unknown command` from an old Worker. Never throw.
  2. **Collect targets before deleting rows.** In each removal path, build the repository paths of the primary checkouts (workspace root + repository folder name, the same way `WorkspaceContextPathResolver` does for the special Workspace) for the rows about to be removed, before the transaction. After the transaction commits, run `UnhookAsync` in the background through `IServiceScopeFactory` (rule: `dbcontext-scoped-lifetime.mdc`). The user's action returns without waiting.
  3. Removing repositories (`ReplaceRepositoriesAsync`): targets = the `toRemove` links. Repositories that stay or are added are not touched.
  4. Deleting a Workspace: move the delete behind a facade method (for example `IWorkspaceManagementOperations.DeleteWorkspaceAsync`, or whatever the existing workspace facade is called; find it with `rg -n "interface IWorkspace\w*Operations" GrayMoon/src`). The page calls the facade, not `WorkspaceRepository`. The Feature guard (B7) is already there and runs first: a Workspace with Features never reaches the unhook step. Targets = every linked repository.
  5. Connector refresh dropping repositories, and connector delete: targets = every link of those repositories in every Workspace. (B5 already keeps a Feature's repositories on refresh and refuses connector delete while Features use them, so only genuinely dropped repositories reach this step.)
  6. Re-adding a repository needs nothing new: it starts as `NeedsSync`, and Sync writes the hooks. Add a test that proves it: remove a repo (unhooked), add it back, Sync -> hooks present.
- **Tests to add (App.Tests, fake Agent bridge):** remove 2 of 3 repos -> 2 unhook calls with the right paths and workspace id, none for the third; delete Workspace -> one call per repo; Worker not connected -> the removal still succeeds and one Warning is logged; Agent returns `PathMissing` or throws -> removal still succeeds, Warning logged; Workspace with a Feature -> delete refused by the existing B7 guard, so no unhook call is made; connector delete -> unhook for its repos in every Workspace.
- **Done when:** tests pass; `Workspaces.razor` no longer injects `WorkspaceRepository` for delete.
- **Regression guard (R-G3a, R-G3b, R-G3c):** this unit edits standard Workspace flows (Edit Workspace, Delete Workspace, connector refresh), so do A4 step 0 first: characterization tests that today's removal results (rows deleted, rows kept) are unchanged. Unhooking runs only after the database commit and never changes the result, timing or error messages of the user's action. Repositories that stay in the Workspace are never unhooked. There is no intended behaviour change for the user; unhooking is invisible. No Desktop README bullet needed unless the owner wants one.

### G4 Self-heal stale hooks from pings

- **Goal:** Stale hooks that G3 could not remove (Worker offline, left by an older GrayMoon, or left by removals made before G3 existed) remove themselves the next time they fire.
- **Ref:** `07` section 6.2. Requires HK-3.
- **Read first:** `App/Services/Agent/SyncCommandHandler.cs` `HandleAsync` (the `wr == null` branch that logs "not found"); `GrayMoon.Abstractions/Notifications/RepositorySyncNotification.cs` (`RepositoryPath`); G3's `WorkspaceHookCleanupService`.
- **Touches:** `SyncCommandHandler.cs`, `WorkspaceHookCleanupService.cs`, tests.
- **Steps:**
  1. In the "not found" branch, if `RepositoryPath` is present, check (with a fresh query) whether a link for `(WorkspaceId, RepositoryId)` exists. The workspace may be deleted or the repository unlinked.
  2. If it definitely does not exist and the query succeeded, call `UnhookAsync` for `(WorkspaceId, RepositoryId, RepositoryPath)` in the background. Rate-limit per normalized path: at most once per hour (in-memory dictionary of path to last attempt time).
  3. Log at Information "Unhooking stale GrayMoon hooks in {Path} (workspace {WorkspaceId} no longer tracks repository {RepositoryId})". Downgrade the existing "not found" Warning to Debug when an unhook was started, so logs stop repeating.
  4. If the query fails, do nothing (no unhook).
- **Tests to add:** link missing -> one unhook with the reported path; second ping within the hour -> no second unhook; link exists -> no unhook, normal processing; DB query throws -> no unhook; `RepositoryPath` null -> no unhook.
- **Done when:** tests pass.
- **Regression guard (R-G4):** unhook only when the link is confirmed missing; a repository that is linked, including one re-added a moment ago, is never unhooked. G2's `workspaceId` check is the second safety net: hooks rewritten by another Workspace are left alone.

---

## 5. GATE-H Hook cleanup (owner)

Taken from the hook steps that used to be in GATE-4 step 6 of the worktree plan. Run `WORKSPACE-SMOKE` from 06 Part D as the last step.

```
GATE-H
1. Everyday hooks still work: in a repo where GrayMoon hooks already exist, commit from a terminal. Expected: the grid updates within seconds.
2. Unhook on edit: Edit Workspace, untick 2 repos, save. Expected: their .git\hooks no longer contain GrayMoon hooks (your own hook files, and any *.replaced-by-graymoon files, untouched); the remaining repos still have theirs.
3. Commit in one of the removed repos. Expected: no new "not found" warning in the App log, no fetch or GitVersion activity in the Worker log.
4. Re-add: tick them again, Sync. Expected: hooks are back and a terminal commit updates the grid.
5. Worker offline: stop the Worker, untick a repo, save, start the Worker. Expected: the removal succeeded with a warning in the log. Commit in that repo: the first ping removes the hooks (Information log line); later commits are quiet.
6. Delete a test Workspace that has a Feature. Expected: refused with "Remove the Features first" (B7, already shipped). Remove the Feature, delete the Workspace. Expected: its repos are unhooked; a repo folder you deleted by hand beforehand only produces a warning.
7. Connector: delete a test connector whose repos are in a Workspace without Features. Expected: its repos are unhooked in every Workspace that had them.
8. Run WORKSPACE-SMOKE.
Reply with PASSED, or the failing step and what you saw.
```

## 6. Changelog of this plan

Agents append one line per session: `YYYY-MM-DD <unit> <status> <agent> - <one sentence>`.

- 2026-10-04 owner reversed part of the move: the Workspace-delete guard stayed in the worktree plan as B7 (DONE); HK-1 superseded; G3 here is unhooking only.
- 2026-10-04 plan created: G2, G3, G4 moved out of `06` by the owner. Added HK-1 (consequence of moving the Workspace-delete guard), HK-2 (what to do with `*.replaced-by-graymoon` files) and HK-3 (G4 in or out); GATE-H takes the hook steps of the old GATE-4 step 6.

## 7. Discovered issues

Agents add items here instead of fixing them. Format: `- [<unit>] <file:symbol> - <problem> - <suggested severity: release blocker | v1.1 | later>`.

- [06 G1] Hook chaining for repos with their own hooks or `core.hooksPath` (instead of renaming the user's hook) - later.
- [06 G1] GrayMoon's own commits and pulls run with `skipHooks: true` (`GitService.GetHooksConfigPrefix`), so they bypass the team's `pre-commit` hooks - owner decision.
