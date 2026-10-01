# Plan risk review: can the worktree plan break standard Workspace workflows?

Companion to `06-worktree-release-implementation-plan.md`. It goes through every unit in the plan and asks what could break for someone who uses GrayMoon **without** Features, and what the plan must do to prevent it. Every mitigation listed here has been written into the units in `06` as a **Regression guard** line, so an agent following `06` applies them without reading this file.

Policy (owner): **no implementation may break a feature that already works.** A change in Workspace behaviour is allowed only when the old behaviour was a bug listed in the review, and the change must be stated in the unit and in the Desktop README.

## 1. Standard Workspace workflows that must keep working

These are the flows a Workspace-only user relies on today. They are the scope of the "Workspace smoke test" (section 4) that runs at every gate.

| ID | Workflow | Main code touched by the plan |
|---|---|---|
| W1 | App and Worker start, Worker connects | U0-2 (migrations), F1 (key), F2 (Worker secret) |
| W2 | Connectors work: tokens decrypt, GitHub calls succeed | F1 |
| W3 | Sync (single repo and all) updates versions, branches and ahead/behind | G1 (hooks written during Sync), B4 |
| W4 | Live updates after commit, checkout, pull or push outside GrayMoon | G1 |
| W5 | Branch: switch all, create, switch single, Return to Default | B4 (bulk switch), A2 (external-worktree cleanup in Switch Branch), I2 (occupancy), I3 (service guard) |
| W5b | Edit Workspace (add or remove repositories), Delete Workspace, connector refresh and delete | G3 (unhook after removal, Feature guard on delete) |
| W6 | Git Changes: view, stage, commit, discard | D2 (watcher stop) |
| W7 | Update Dependencies, Push (with levels), Undo Push, Prepare | B3, B4 (push plan) |
| W8 | Pull requests: create, badges, merge | A3, E2 (header button) |
| W9 | Restore packages, Dependencies page, grid dependency tooltips | B1, B2, B3 |
| W10 | Open in... (Cursor, VS Code, Terminal, Claude CLI, Explorer) for Workspace repos | E3 |
| W11 | REST API and automation scripts | F3 |
| W12 | Docker deployment (App in container, Worker on host) | F2, F3, A2 |

## 2. Cross-cutting risks

| ID | Risk | Likelihood / impact | Mitigation (written into `06`) |
|---|---|---|---|
| X-1 | **App and Worker versions differ.** The Desktop installs and updates the Worker separately, and in Docker the App and Worker are updated independently. New Agent commands (A1 `InspectWorktree`) and new request/response fields (D1, G1) reach an old Worker, which throws `Unknown command` (`CommandDispatcher`). | High / High | A3 rule 13: new request fields optional, new response fields nullable, and old meanings unchanged. On `Unknown command`, the App shows "Update the Worker to use this" (reusing `AgentUpdateDesktopNotifier`) and the Workspace flows that don't need the new command keep working. Each Agent unit adds a test that an old-shaped response still deserializes. |
| X-2 | **Merging parallel lanes reintroduces bugs.** Several lanes edit `WorkspaceFeatureOperations.cs` and `Migrations.Features.cs`. A bad merge can silently drop a fix or a guard. | Medium / High | A7: merge one lane at a time, then run the full build and all tests, then the Workspace smoke test, before merging the next lane. Gates run on the merged tree, never on a single lane's checkout. |
| X-3 | **Workspace behaviour has few UI tests.** Most regressions would show up only in the UI. | High / Medium | Before changing shared Workspace code, the unit first adds a **characterization test** that pins today's Workspace result (A4 step 0). The Workspace smoke test (section 4) runs at every gate. |
| X-4 | **Weaker agents "improve" nearby code.** | Medium / Medium | Already in A1 and A3: touch only the listed files and methods, and park everything else in Part F. A5 adds: stop if a change would alter a Workspace (non-Feature) code path that the unit doesn't name. |
| X-5 | **Changes that run at startup can stop GrayMoon from starting** (U0-2 migrations, B2 rebuild, E1 index, F1 re-encryption, F2 secret). A startup failure breaks every workflow at once. | Medium / Critical | Database backup before any change (U0-2). Each startup step is tested against three databases: fresh, the 0.1.0 fixture, and a copy of the owner's real database (GATE-1 and GATE-2). Re-encryption and the index change never fail startup; they log and continue (F1, E1). |

## 3. Risks per unit

Likelihood and impact use High / Medium / Low. "Workspace impact" says which standard workflow from section 1 is exposed.

### Lane 0

| ID | Unit | Risk | Workspace impact | Mitigation |
|---|---|---|---|---|
| R-U02a | U0-2 | Existing databases have been migrated by the old silent steps for months. Some have harmless leftovers (a column added twice and swallowed, a partly created index). The new "fail on error" runner would refuse to start on them. (H/Critical) | W1, so everything | Legacy steps (everything that exists today) become **step 1, "legacy baseline"**, and run in tolerant mode: every statement is check-before-change; an unexpected error is logged at Error and startup **continues**, as today. Only **new** steps (B2, E1 and later) fail hard. Test on all three databases. |
| R-U02b | U0-2 | Copying `graymoon.db` while it is open can produce a corrupt backup (WAL not checkpointed). (M/M) | none directly; a bad backup is discovered only when needed | Use SQLite `VACUUM INTO '<backup path>'`, which writes a consistent copy from the open connection, instead of a file copy. A test opens the backup and runs `PRAGMA integrity_check`. |
| R-U02c | U0-2 | Backups fill the disk for users with large databases. (L/L) | none | Keep the 3 newest backups; create one only when a step is pending. |
| R-U02d | U0-2 | When the App exits on a migration error, Desktop may show a generic or blank error. (M/M) | W1 | GATE-1 step 5 checks the message Desktop shows. If it is unclear, record a Part F item for Desktop (not a blocker). |

### Lane A

| ID | Unit | Risk | Workspace impact | Mitigation |
|---|---|---|---|---|
| R-A1 | A1 | Changing `GitWorktreePorcelainParser` (adding `locked`/`prunable`) changes how existing lines are parsed. The parser feeds branch occupancy badges in the Workspace Switch Branch modal. (L/M) | W5 | Additive fields only. All existing parser and occupancy tests must pass unchanged. Add a test with real porcelain output from a repo with no worktrees. |
| R-A2a | A2 | External-worktree cleanup is used from **Switch Branch in the Workspace** when the target branch is held by a stray worktree. If `InspectWorktree` fails (old Worker, X-1), that cleanup becomes blocked where it used to work. (M/M) | W5 | Unknown state blocks only the **force/discard** path. Non-forced cleanup still runs, because Git itself refuses to remove a dirty worktree without `--force`, so it stays as safe as today. The message says what to do ("Update the Worker" or "Start the Worker"). |
| R-A2b | A2 | The architecture test (no `System.IO` in `Services/Features`) fails on legitimate string-only path code. (L/L) | none | Allow-list pure path helpers (`Path.Combine`, `Path.GetFileName`) and ban only `Directory.*`, `File.*`, `Path.Exists`. |
| R-A3 | A3 | Refreshing PRs before the Remove analysis adds GitHub calls (rate limit) and fails offline. (M/L) | W8 (shared rate limit) | One refresh per analysis, for the Feature's repos only. Offline or failed refresh: PR state Unknown, which **requires acknowledgement** but does not disable Remove (unlike Unknown disk state). |

### Lane B

| ID | Unit | Risk | Workspace impact | Mitigation |
|---|---|---|---|---|
| R-B1 | B1 | Deleting by context id could hit Workspace rows if the id is the special Workspace context or null. (L/Critical) | W9 (dependency graph is the core feature) | Guard: run the deletes only when the context's `Kind` is Feature (1). Never delete rows with null context id. The test asserts Workspace and other Feature rows are untouched, row by row. |
| R-B2a | B2 | **Rebuilding `WorkspaceProjects` and `WorkspaceFileLineStatuses` can lose or renumber rows.** `ProjectDependencies` and other tables reference project ids, so renumbering breaks the Workspace dependency graph. (M/Critical) | W7, W9 | Copy rows **with their ids** (`INSERT INTO new (...) SELECT ... FROM old` including `Id`). Assert non-orphan row counts and a checksum of ids are equal before and after, in the same transaction. Run `PRAGMA foreign_key_check` before commit. Roll back on any mismatch. Backup from U0-2. |
| R-B2b | B2 | The "orphan" definition catches rows with null context id, which may be legacy Workspace rows. (M/High) | W9 | Orphan = context id **not null** and not present in `WorkspaceFeatureContexts`. Null rows are never deleted. |
| R-B2c | B2 | The rebuilt table differs from the EF model (index names, column types), so EF queries fail later. (M/High) | W7, W9 | The schema parity test (fresh vs upgraded) compares `sqlite_master.sql` and the index list. |
| R-B3a | B3 | **How Workspace rows are stored is not uniform.** Some Workspace project rows may have the special context id after the backfill, others null. A filter "special context id only" would hide Workspace projects; "null only" would hide the others. (H/High) | W7, W9 | Step 0 of B3: the agent checks the backfill code (`Migrations.Features.cs`) and the owner's real database (read-only query on a copy) and writes down which form Workspace rows take. The Workspace filter is then `ctx IS NULL OR ctx = specialContextId`. A characterization test on a database **with no Features** must return exactly the same rows before and after the change. |
| R-B3b | B3 | Restore in the Workspace today restores each project once per context, including deleted ones. After B3 it restores each project once. That is a deliberate fix, but a user may notice it got faster or "did less". (L/L) | W9 | Called out in the Desktop README bullet. The characterization test pins the new list. |
| R-B4a | B4 | The bulk Branch modal skips repos whose branch is held by a Feature. If the occupancy lookup fails, it could skip everything or block the switch. (M/M) | W5 | If occupancy cannot be determined, fall back to today's behaviour (attempt the switch; Git refuses per repo) and say so. Skipping happens only on a positive "held by" answer. |
| R-B4b | B4 | The push plan tag exclusion and the notification push list change for Feature contexts, and the Workspace path could be changed by mistake. (M/High) | W7 | Rule 3 of `feature-context-scoping.mdc`: for the special Workspace, the code reads the shared link exactly as today. A characterization test of the Workspace push plan (levels, tag-pinned repos excluded) runs before and after. |

### Lane C

| ID | Unit | Risk | Workspace impact | Mitigation |
|---|---|---|---|---|
| R-C2a | C2 | The reconciler marks healthy Features as "Needs attention" after a transient failure (Worker restarting, slow disk, repo moved), creating false alarms on every reconnect. (H/M) | none for Workspace; trust in Features | Downgrade a repo only when `ListGitWorktrees` **succeeded** and `InspectWorktree` confirms the folder is missing. Never on an error. Never delete anything. Debounce: at most once per 60 s, and skip if a reconcile finished less than 60 s ago. |
| R-C2b | C2 | The reconciler takes locks or floods the Worker at startup, so Workspace Sync waits behind it. (M/M) | W1, W3 | Runs in the background with no structural lock. One git call per repo per Feature, at most 4 at a time. It skips any workspace with an operation running. It only reads and writes Feature rows (Kind 1). |
| R-C3 | C3 | Opening a NeedsRepair Feature (newly selectable) loads pages that call the Worker on missing worktree paths, which may throw and break the browser tab (circuit). (M/M) | the Workspace page shares the component | A NeedsRepair context opens in **read-only** mode: the header shows the "needs attention" banner, and Sync, Push, Update and Git Changes actions are disabled for it. Test: page load for a NeedsRepair context with one missing worktree does not throw. |
| R-C4 | C4 | Roll back deletes a branch that is not the Feature's (wrong name, or a branch the user created by hand with the same name). (L/High) | W5 | Roll back deletes only branches recorded in that Feature's repo rows, with non-force `branch -d`, never the default branch, and never a branch checked out in the primary checkout (Git refuses anyway). |

### Lane D

| ID | Unit | Risk | Workspace impact | Mitigation |
|---|---|---|---|---|
| R-D1a | D1 | **Recursive delete of the wrong folder.** A wrong `worktreePath` (bad DB row, path resolver bug, a Feature named like a parent folder) could make the residue cleanup delete the real Workspace repository. (L/Critical) | All of the user's work | Hard guards in the Agent, all required, each with a test. (1) The path is under `<ManagedFeatureStorageRoot>\<Workspace>\features\<FeatureName>\` after full normalization. (2) It is not equal to, and does not contain, any primary repository path. (3) It is not a registered worktree in `git worktree list`. (4) It contains no `.git` **directory**, since a real repository has one and a worktree only has a `.git` file. (5) It is at least 3 levels below the storage root. If any guard fails, delete nothing and report it. |
| R-D1b | D1 | **Junctions and symlinks** (common in `node_modules` and some build outputs on Windows) point outside the worktree. A recursive delete that follows them deletes the targets. (M/Critical) | All of the user's work | Custom walk: for every entry, if it has the `ReparsePoint` attribute, delete the **link itself** (`Directory.Delete(path, recursive: false)` or `File.Delete`) and never enter it. Test with a junction pointing to a folder outside the worktree; the target must survive. |
| R-D1c | D1 | Allowing create into an existing empty folder also allows create into a folder the user made for something else. (L/L) | none | Only empty folders, and only under the Feature storage root. |
| R-D2 | D2 | Stopping Git Changes monitoring before Remove stops it for the **Workspace** too, or never restarts it if Remove fails. (M/M) | W6 | Stop by context id only. Restart in `finally` when Remove fails or is cancelled. Test both: the Workspace watcher stays running throughout, and a failed Remove restarts the Feature's watcher. |
| R-D4 | D4 | Remote delete removes a shared branch or the default branch. (L/High) | other people's work | Lease (`--force-with-lease` with the Feature's last pushed SHA), never the default branch, opt-in per Remove, list shown before confirming. |

### Lane E

| ID | Unit | Risk | Workspace impact | Mitigation |
|---|---|---|---|---|
| R-E1a | E1 | Existing Features whose names fail the new rules become unusable if the validator runs on load. (M/M) | none for Workspace | The validator runs **only** when creating a new Feature. Existing names are never re-validated. |
| R-E1b | E1 | The validator is reused for Workspace branch creation and blocks names that work today. (L/M) | W5 | E1 does not touch branch creation in the Workspace. Grep confirms the old regex had only the Feature caller. |
| R-E1c | E1 | The `NOCASE` index migration fails on case-only duplicates. (L/High) | W1 | It is not a hard step: if duplicates exist, log a warning and keep the old index (already in E1). |
| R-E2 | E2 | The header rule change alters the Workspace primary button (Branch / Create PR). (M/M) | W5, W8 | The pure rule function has explicit Workspace test cases (creatable PR shows Create PR; otherwise Branch), identical to today. |
| R-E3a | E3 | `code` (VS Code) and `claude` are `.cmd` scripts or need a shell. Switching to `ArgumentList` with `UseShellExecute = false` can stop them from starting. Claude CLI also needs a window that stays open (`cmd /k`). (H/M) | W10 | Keep `cmd.exe /k claude` and `wt.exe`, but never put the path into the command string: set `WorkingDirectory = path` and pass the tool command without the path. Resolve `Code.exe` when available. GATE-4 checks every tool for the Workspace **and** a Feature, including paths with spaces. |

### Lane F

| ID | Unit | Risk | Workspace impact | Mitigation |
|---|---|---|---|---|
| R-F1a | F1 | **All tokens become unreadable** if the new key is lost: Docker volume without the key file, Windows profile reset (DPAPI), restore of an old DB backup without the key. Every connector, Sync, Push and PR breaks. (M/Critical) | W2, so everything remote | (1) The key file lives in the same folder as the database (the Docker volume already persists it). (2) Re-encrypt a token only after a decrypt round-trip with the new key succeeds. (3) The legacy key stays available for decryption permanently. (4) If a token cannot be decrypted, the connector is marked "token needs re-entry" in the existing token health UI (`TokenHealthBackgroundService`); the app does not crash. (5) A configured key (environment or settings) always wins and is never replaced. (6) The backup from U0-2 runs before re-encryption. |
| R-F1b | F1 | DPAPI is unavailable in Docker (Linux). (H/M) | W12 | DPAPI only when `OperatingSystem.IsWindows()`; otherwise a plain key file with mode 600. |
| R-F2a | F2 | **The Worker can no longer connect after upgrade.** Installed Workers have no secret, and without the Worker GrayMoon can do nothing. (H/Critical) | W1, so everything | Transition release: the secret is **enforced only when** `Security:RequireWorkerSecret = true` (default false in v1). In v1 a Worker without the secret is accepted, a warning is logged once, and the UI shows "Reinstall the Worker to finish securing GrayMoon". Desktop's `WorkerInstaller` writes the secret automatically, so Desktop users get it on the next Worker update. Enforcement becomes the default in v1.1. |
| R-F2b | F2 | The secret differs after a database reset or reinstall, so the Worker is rejected. (M/H once enforced) | W1 | The secret is stored next to the database. The Worker install flow always rewrites it. The rejection message tells the user to reinstall the Worker. |
| R-F3a | F3 | **`AllowedHosts` set to loopback breaks Docker users** who open GrayMoon by host name or LAN IP, and the Worker if it connects through such a name. Every request gets HTTP 400. (H/Critical) | W1, W12 | Do **not** change `AllowedHosts` in the shared `appsettings.json`. Desktop (which always uses loopback) sets it in its own App launch configuration. Docker keeps `*` and the docs explain how to restrict it. |
| R-F3b | F3 | Requiring the `X-GrayMoon-Request` header breaks **existing REST API scripts** (W11) and callers inside GrayMoon that were missed. (H/M) | W11 | The header check only applies when an `Origin` header is present (browsers always send it on cross-site POSTs; scripts and the Worker don't). Requests with no `Origin` pass as today. Cross-site browser requests are still blocked, so the protection holds. Grep every caller (`HttpClient`, `fetch(` in `wwwroot`, the Desktop bridge) and test each. |
| R-F3c | F3 | The check accidentally covers Blazor's own `/_blazor` endpoint or the Worker hub. (L/Critical) | W1 | It applies only to `/api/` and `/repos/`. Test that `/_blazor` and `/hub/*` are unaffected. |

Correction to the first version of `06`: F3 said the hook scripts post to `/api/sync`. They don't. They post to the Worker's own listener at `127.0.0.1:<port>/hook/*` (`HookListenerHostedService`). `06` is updated accordingly.

### Lane G

| ID | Unit | Risk | Workspace impact | Mitigation |
|---|---|---|---|---|
| R-G1a | G1 | **GrayMoon fails to recognise its own old hooks**, treats them as user hooks, and stops updating them. Live updates (W4) then break for every existing user, and old hooks keep any outdated attribution. (M/High) | W4 | Recognise GrayMoon hooks by the marker line prefix `# Created by GrayMoon.Agent`, which is identical in 0.1.0 and today (checked with `git show 0.1.0:...`). Test fixtures: the 0.1.0 hook text and the current hook text are both treated as GrayMoon's. |
| R-G1b | G1 | Repositories that have **not** been synced yet and have a user hook stop getting live updates where GrayMoon used to overwrite and win. (M/L) | W4 for those repos only | Intended: GrayMoon no longer destroys user hooks. The warning icon explains it. Repositories already overwritten keep working (R-G1a). |
| R-G1c | G1 | `git rev-parse --git-path hooks` returns a relative path or behaves differently in linked worktrees and old Git versions. (L/M) | W4 | Resolve relative to the repository path. A real-git test runs in a primary checkout and in a linked worktree; both must resolve to the same common hooks folder when `core.hooksPath` is unset. |
| R-G2 | G2 | Unhook deletes a user's hook, or GrayMoon hooks that another Workspace wrote into the same folder later (for example a Workspace deleted and re-created with the same root and name). (L/M) | W4 in the other Workspace | Delete only files with the GrayMoon marker **and** a payload naming the requested `workspaceId`; everything else is skipped and reported. Tests cover both payload styles (0.1.0 and current). |
| R-G3a | G3 | Unhooking is wired into Edit Workspace, Delete Workspace and connector refresh. A slow or offline Worker could make those actions slow or fail. (M/H) | W1, everyday workspace editing | Unhook runs after the database commit, in the background, with a 30 s timeout per repo, and only logs. The user's action never waits for it or fails because of it. Characterization tests pin today's removal results. |
| R-G3b | G3 | Wrong targets: repositories that stay in the Workspace are unhooked, so their live updates stop. (L/H) | W4 | Targets are only the rows being deleted (`toRemove`, the deleted Workspace's links, the dropped connector repositories), collected before the transaction. A test asserts no call for repositories that stay. Sync re-hooks any repository anyway. |
| R-G3c | G3 | The new "Remove the Features first" guard on Workspace delete surprises users who could delete before. (M/L) | Delete Workspace | Intended fix (deleting with Features orphaned worktrees on disk); same rule already applies to editing membership. Clear message plus Desktop README bullet. |
| R-G4 | G4 | Self-heal unhooks a repository that is still in use: a transient DB failure looks like "not found", or the repo was re-added a moment ago. (L/H) | W4 | Unhook only when a fresh query **succeeds** and finds no link; never on query errors. A re-added repo has a link, so it is never touched. G2's `workspaceId` check is the second safety net. At most once per path per hour. |

### Lane I

| ID | Unit | Risk | Workspace impact | Mitigation |
|---|---|---|---|---|
| R-I1 | I1 | The changed analysis misjudges Features that never drifted, or an old Worker without the new fields blocks Remove. (M/M) | none (Remove is Feature-only); A2 external cleanup in W5 | Characterization test: a non-drifted Feature gives the same plan as before. Old Worker: Feature-branch facts are Unknown, force delete is not offered, Remove stays enabled. External-worktree cleanup never sends `featureBranch`. |
| R-I2 | I2 | The occupancy change unblocks a branch for the Workspace view, so the Workspace could try to check out a Feature's branch. (L/M) | W5 | `FeatureOwn` is emitted only when the row's context equals the viewing context, which is never the case for the Workspace. A characterization test pins the Workspace badges. The upgrade-badge occupancy fix (SB-8) is a named bug fix. |
| R-I3a | I3 | The service guard refuses a Workspace checkout, create or Return to Default, for example because the special context is misclassified. (L/High) | W5, W11 | The guard returns "allowed" for Kind 0 before any Feature query. Characterization tests for the three methods on the special Workspace, plus one REST route test. WORKSPACE-SMOKE step 4. |
| R-I3b | I3 | Feature create or repair goes through the guarded methods and is refused. (L/High) | none | The agent confirms with `rg` that create and repair use `CreateGitWorktree`, and stops if not. C4 tests stay green. |
| R-I4 | I4 | The drift badge slows grid refresh with per-row queries, or appears in the Workspace. (L/L) | grid refresh | Feature repo rows are loaded once per selection; there are no Agent calls. The expected branch is null for the Workspace, so no badge appears there. |

### Lane H and R

| ID | Unit | Risk | Workspace impact | Mitigation |
|---|---|---|---|---|
| R-H1 | H1 | Information-level logging per repo floods logs on big Workspaces. (L/L) | none | Information per operation, Debug per repo, Warning per failure. |
| R-R2 | R2 | The kill switch hides the whole context selector, including the Workspace entry. (L/M) | navigation | It hides only the Features section and the create button. Test with Features disabled: the Workspace page works unchanged. |

## 4. Workspace smoke test (run at every gate, after the gate's own steps)

This is in `06` Part D as `WORKSPACE-SMOKE`. Use a Workspace with at least 3 repositories, one at a higher dependency level, and no Features open.

1. Start GrayMoon. The Worker connects within 30 s; no error toasts.
2. Sync all. Versions, branches and ahead/behind fill in.
3. In a terminal, commit in one repo. Within a few seconds the grid shows the new commit (hooks work).
4. Branch, switch all to a new branch name (create). All repos switch. Return to Default brings them back.
5. Git Changes: change a file, stage, commit from GrayMoon.
6. Update Dependencies, then Push with levels. Packages and versions flow to the higher level.
7. Create PR for one repo, see the badge, then close or merge it.
8. Dependencies page and Restore. Each project appears once.
9. Open in... VS Code and Terminal for a Workspace repo.
10. If you use the REST API or scripts, run one of them.

Report PASSED, or the step number and what you saw.

## 5. Summary

- **Highest-impact risks:** D1 deleting the wrong folder (R-D1a, R-D1b), F1/F2/F3 locking the user out of their own tool (R-F1a, R-F2a, R-F3a), and B2/B3 damaging the Workspace dependency graph (R-B2a, R-B3a). All have hard guards and tests in the plan.
- **Most likely regressions:** Worker version skew (X-1), the `.cmd` launchers (R-E3a), and reconciler false alarms (R-C2a).
- **Design changes made to the plan because of this review:**
  - Legacy migrations stay tolerant (R-U02a).
  - The Worker secret is not enforced in v1 (R-F2a).
  - `AllowedHosts` is not changed for Docker, and the header check applies only to requests that carry an `Origin` (R-F3a, R-F3b).
  - NeedsRepair Features open read-only (R-C3).
  - Unknown PR state warns instead of blocking (R-A3).
