# Worktree Features v1 - Implementation Plan (live document)

> **AI agent: this document is your complete brief. Read all of Part A before you touch any file. Then follow it exactly.**
> The owner (the human who gave you this file) does manual testing at the gates marked `USER TEST GATE`. You never skip a gate.

Created 2026-10-01 from the review in this folder. Baseline: GrayMoon `a06fe33`, GrayMoon.Desktop `8c4fb90`, both on branch `opus-review`; 788 tests passing.

---

# PART A - How you (the AI agent) must work

## A1. Your role

You implement **one unit at a time** from Part D. A unit is small and testable on purpose. You are done with a unit when its "Done when" list is fully true and you have updated the tracker (Part C). You do not start a second unit in the same session unless the owner tells you to.

You are not a reviewer here. Do not redesign, do not "also fix" nearby things, do not refactor code that the unit does not name. If you see a problem outside the unit, write it in the **Discovered issues** list (Part F) and move on.

## A2. Start-of-session routine (do this every time, in order)

1. Read Part A (this part) fully.
2. Read Part B (decisions). If a decision your unit depends on is still `OPEN`, **stop and ask the owner** that question. Do not guess.
3. Read Part C (tracker). Pick your unit:
   - If the owner named a unit, take that one.
   - Otherwise take the **first** unit, top to bottom, whose status is `TODO` and whose every `Depends on` unit is `DONE`. If the lane column says a lane is claimed by another agent, skip units in that lane.
   - If nothing qualifies, report "No unit is ready" with the reason (waiting on a gate, a decision, or a dependency) and stop.
4. Set the unit's status to `IN PROGRESS`, put your agent/model name and the date in the `Owner` column, and save this file.
5. Read the unit's section in Part D completely, then read every file listed under "Read first". The `Ref` codes (F-1, U-15, S1...) point to the review docs in this folder:
   - `02-worktree-code-and-approach-review.md` (F-n findings)
   - `03-worktree-ux-review.md` (U-n findings)
   - `04-worktree-v1-release-roadmap.md` (P0/P1/P2 items)
   - `05-general-code-and-ux-review.md` (A-n, S-n, R-n, D-n findings)
   - `07-appendix-why-lane-g-hooks.md` (background for G1)
   - `08-plan-regression-risk-review.md` (R-..., X-... risk IDs behind each Regression guard)
   - `09-switch-branch-in-feature-analysis.md` (SB-n findings, background for lane I)
   Read only the referenced section, not the whole doc.

## A3. Rules that always apply

These come from the repo's own rules in `GrayMoon/.cursor/rules/`. Breaking them fails the unit.

1. **Never commit, push, stage, or create branches.** At the end, give the owner a one-line commit message in a fenced code block (imperative, e.g. `Fix ...`, `Add ...`). If you changed files in GrayMoon.Desktop too, give a second message labelled `GrayMoon.Desktop`.
2. **Windows.** The shell is PowerShell. Chain with `;`, never `&&`. Quote paths. Every file you create or edit uses **CRLF** line endings.
3. **No em dashes or en dashes** in code, comments or docs. Use a plain hyphen `-`.
4. **App never touches the developer's disk.** `GrayMoon.App` may not call `System.IO.Directory`, `System.IO.File` or `Path.Exists` on repository or worktree paths. Disk facts come from the Agent (`GrayMoon.Agent`) through `IAgentBridge.SendCommandAsync`. The App can run in Docker where those paths do not exist.
5. **Feature-context scoping.** Read `GrayMoon/.cursor/rules/feature-context-scoping.mdc` before touching any query on `WorkspaceProject`, dependency, version, PR, Actions or Git Changes data. Every such query takes the context id. Never fall back to the shared Workspace row for a Feature.
6. **DbContext lifetime.** Read `GrayMoon/.cursor/rules/dbcontext-scoped-lifetime.mdc`. Anything that saves or runs in parallel uses `IDbContextFactory<AppDbContext>` and a short-lived context.
7. **UI calls facades.** Pages and modals call `IWorkspace*Operations`, never `WorkspaceGitService` or Agent clients directly (`GrayMoon/.cursor/rules/ui-application-facades.mdc`).
8. **New Agent command** = follow `GrayMoon/.claude/skills/add-agent-command/SKILL.md` step by step. Missing step 5 (deserialization) silently breaks the command.
9. **User-visible change** = add or edit one bullet in `GrayMoon.Desktop/README.md` under "Recent GrayMoon changes" (`GrayMoon/.cursor/rules/desktop-readme-on-feature-change.mdc`).
10. **Primary constructors** for new C# classes, matching the surrounding code (`GrayMoon/.cursor/rules/primary-constructors.mdc`).
11. **Comments**: only for constraints the code cannot show. No "changed X because review said so" comments.
12. **Line numbers in this plan drift.** Always locate code by the **symbol name** given (method, class, file). Use search (`rg -n "SymbolName" GrayMoon/src`). If a symbol named here does not exist, stop and ask.
13. **Worker compatibility.** The App and the Worker can be different versions (Desktop updates the Worker separately; in Docker they are separate installs). New request fields must be optional, and new response fields nullable with "unknown" meaning. Never change the meaning of an existing field. When the Worker answers `Unknown command`, the App shows "Update the Worker to use this" and every flow that does not need the new command keeps working. Each unit that changes an Agent request or response adds a test that the **old** response shape still deserializes.
14. **Do not break the standard Workspace.** Most users never open a Feature. Any change to code that also runs for the special Workspace context must leave the Workspace result identical, unless the unit names the change as a bug fix. Every unit has a **Regression guard** line: it is part of "Done when". The risk behind each guard is explained in `08-plan-regression-risk-review.md` (risk IDs `R-...`, `X-...`). You do not need to read that file unless a guard is unclear.

## A4. How to implement a unit

0. **Pin the Workspace first.** If the unit edits code that also runs for the special Workspace (its Regression guard says so), first add a characterization test that records today's Workspace result for that code (seed a database with **no** Features, call the method, assert the exact output). Run it before your change and keep it green after.
1. **Reproduce first.** Write the test(s) listed under "Tests to add" before the fix. Run them and confirm they fail for the reason described. If a test you expected to fail passes, stop and report it: the finding may already be fixed.
2. Make the smallest change that makes the tests pass and satisfies "Done when".
3. Build and run tests (commands below). The **whole** affected test project must be green, not only your new tests.
4. Do the "Self-check" list in the unit.
5. Update Part C (status, notes) and Part E (changelog) in this file.

Do not publish a bundle after a plan unit (this section, A4). For a Part D unit, `Publish-GrayMoonBundle.ps1` runs exactly once, as the last step of R4, after the entire plan's tests and release checklist are green (see R4 step 4) - not after any individual unit or gate. Ad-hoc fixes the owner asks for while testing a build are different: see A4a.

## A4a. Ad-hoc fixes the owner asks for while testing (not a plan unit)

While the owner is testing a build, they will sometimes ask for something that is not a unit in Part D at all: a bug they hit, a UX tweak, "make this button yellow", and similar small iterations. These are not plan units and do not go through the Part D "Done when"/Regression guard machinery, but they still need the normal engineering care:

1. Make the change. Build and run the affected tests; keep the whole affected test project green (same bar as A4 step 3).
2. Unlike a plan unit, the owner needs an actual build to test the result right away. Once the change builds and tests are green, run `GrayMoon.Desktop\build\Publish-GrayMoonBundle.ps1` so a fresh bundle exists under `artifacts\bundle\` and tell the owner it is ready to test.
3. Add one line to the changelog (Part E) describing what changed and why, the same way a unit would.

Run the publish script after **every** such ad-hoc iteration, however small, so the owner is always testing current code. This is the only place outside R4 (A4) where the script runs.

### Commands (run from the folder that contains `GrayMoon` and `GrayMoon.Desktop`)

```powershell
# Build everything (expect 0 errors; warnings must not increase)
dotnet build GrayMoon\GrayMoon.slnx

# Tests, one project at a time (Agent tests take about 2.5 minutes)
dotnet test GrayMoon\src\GrayMoon.Common.Tests\GrayMoon.Common.Tests.csproj
dotnet test GrayMoon\src\GrayMoon.App.Tests\GrayMoon.App.Tests.csproj
dotnet test GrayMoon\src\GrayMoon.Agent.Tests\GrayMoon.Agent.Tests.csproj

# Only your tests while iterating
dotnet test GrayMoon\src\GrayMoon.App.Tests\GrayMoon.App.Tests.csproj --filter "FullyQualifiedName~YourTestClass"

# Desktop (only for units that touch GrayMoon.Desktop)
dotnet build GrayMoon.Desktop\GrayMoon.Desktop.slnx
dotnet test GrayMoon.Desktop\src\GrayMoon.Desktop.Tests\GrayMoon.Desktop.Tests.csproj
```

Known flaky test: a timing-sensitive PowerShell pipe test in Agent.Tests (fixed by unit U0-1). If only that test fails, rerun it alone once; if it passes alone, note it and continue.

## A5. When to STOP and talk to the owner

Stop immediately, set the unit to `BLOCKED` with a one-line reason in Notes, and ask, when:

- A decision in Part B that your unit needs is `OPEN`.
- The fix needs files outside the unit's "Touches" list in a way that changes behaviour (small compile fixes in callers are fine).
- A test unrelated to your change fails twice.
- The code differs from what this plan describes (symbol missing, behaviour already different).
- You would need to delete user data, change the database schema beyond what the unit says, or change a public REST route.
- Your change would alter behaviour for the standard Workspace (no Feature selected) in a way the unit does not name.
- You reach a `USER TEST GATE` row in the tracker.

When a unit is done and the **next** tracker row is a `USER TEST GATE`, set the gate to `READY FOR USER TEST`, print the gate's test script from Part D verbatim (and the `WORKSPACE-SMOKE` script when the gate's last step refers to it), and stop. Do not continue until the owner writes the result. The owner marks the gate `PASSED` or writes what failed; failures become new units (e.g. `C2-fix1`) that you add to the tracker directly after the failed unit.

## A6. Your final message for each session

Use exactly this shape, **including the commit message**, every session in which you changed any file, even when the unit is BLOCKED:

````
Unit: <id> <title> - <DONE | BLOCKED | READY FOR USER TEST>
What changed: <one or two sentences>
Tests: <new tests added>; <project>: <passed>/<total>
Files: <list>
Next: <next unit id, or the gate the owner must run, or the question you need answered>

Owner checks before the next unit:
1. <action in GrayMoon or a terminal> - Expected: <what the owner should see>
2. ...
Workspace check: <one quick check that the standard Workspace still works where this unit touched shared code, or "not needed (Feature-only change)">

Commit message (GrayMoon):
```
<Imperative summary line, at most 72 characters, ending with the unit id, e.g. "Block branch checkout off the Feature branch (I3)">

<Optional body: 1 to 4 short lines saying what changed for the user and why. No file lists, no test counts.>
```

Commit message (GrayMoon.Desktop):
```
<Same rules; only when files in GrayMoon.Desktop changed, otherwise leave this block out>
```
````

Owner check rules:
- **List 2 to 5 manual checks** that prove this unit works on the owner's machine, each with a concrete expected result. Use real names, for example "Create Feature `check-i2`", not "create a Feature". The checks must take under 10 minutes in total.
- **Cover the unit's behaviour, not its tests.** Do not ask the owner to run `dotnet test`; you already did.
- **Add the Workspace check line.** When the unit's Regression guard names Workspace code, give one quick check from WORKSPACE-SMOKE that exercises it (for example "Workspace: switch one repo to a branch and back"). Otherwise write `not needed (Feature-only change)`.
- **Say what is needed first:** a running Worker, a Worker reinstall (Agent changes), an App restart (migrations), or a rebuild of Desktop.
- **When the next tracker row is a gate,** keep these checks short and point to the gate script you print below them (A5).
- **When the unit is BLOCKED or nothing changed,** write `Owner checks: none`.
- **The owner replies OK, or with the failing check.** A failing check means the unit goes back to `IN PROGRESS` in the next session; do not start the next unit.

Commit message rules:
- **One message per repository you changed.** The owner commits each repository separately.
- **The summary line starts with an imperative verb** (`Add`, `Fix`, `Block`, `Remove`...). It describes the behaviour, not the files, and ends with the unit id in parentheses.
- **The message covers only this session's changes.** That includes the edits to this plan file (tracker, changelog), so do not mention them separately.
- **Format:** plain ASCII hyphens, with no em or en dashes and no emojis.
- **If nothing changed** (for example, you only answered a question or stopped at a decision), write `Commit message: none (no files changed)`.
- **You never run `git commit` yourself** (rule 1).

## A7. Working in parallel (several agents)

- Lanes (Part C) are designed so agents in different lanes touch mostly different files. **One agent per lane at a time.**
- **Each parallel agent works in its own checkout** (for example a GrayMoon Feature worktree created by the owner). Two agents must never edit the same working folder at the same time.
- Hot file: `GrayMoon/src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs` is touched by lanes A, B, C, D and H. Keep your edits to the methods your unit names. Do not reformat, reorder or rename anything else in that file, so the owner can merge lanes cleanly.
- Hot files outside Features: `App/Repositories/WorkspaceRepository.cs` (B6), `RepositoryRepository.cs` and `ConnectorRepository.cs` (B5). (G3 used to share them; it moved to `12-hook-cleanup-plan.md`.)
- Lane I also touches shared code: I1 edits `AnalyzeRemoveFeatureAsync` in that hot file and the A1 `InspectWorktree` files, so it starts only after lanes A and D are merged. `WorkspaceBranchOccupancyService.cs` is edited by I2 only; B4 only calls it.
- Shared migration file: `GrayMoon/src/GrayMoon.App/Migrations.Features.cs`. Only units marked "Migration" edit it, and each adds a **new, separate step method**. Never edit another unit's step.
- **Merging lanes (owner, or an agent the owner asks):** merge one lane at a time into the main checkout. After each merge run the build, all test projects and `WORKSPACE-SMOKE` (Part D) before merging the next lane. Gates are always run on the merged tree, never on a single lane's checkout.

---

# PART B - Decisions (owner answers; agents read, never decide)

Write the answer in the `Answer` column and change `OPEN` to `DECIDED`. The `Default` is what the plan assumes if the owner says "go with default".

| ID | Question | Default | Blocks units | Status | Answer |
|---|---|---|---|---|---|
| DEC-1 | Is the Docker deployment (App in a container, Worker on the host) supported for worktrees v1? | Yes. The README documents it as the main install, so Agent-side disk checks are required. If No, A1/A2 are still done because they are correct for both setups, but severity drops and the Docker test in Gate 2 is skipped. | A2, Gate 2 | DECIDED | Yes. Desktop is an addon-wrapper around the webapp; Docker (App in container, Worker on host) is a supported deployment for worktrees v1. |
| DEC-2 | Should v1 offer "also delete remote branches" in Remove Feature? | **Owner UX (2026-10-02):** Yes for v1. Replace the static "Local branches are deleted. Remote branches are kept." line with checkboxes (local + remote), both ticked by default when applicable; hide/disable the remote checkbox with accurate copy when no remote/upstream Feature branches exist. Keep D4's safe lease-based delete. (Previous default was No / v1.1.) | D4, D6 | DECIDED | Yes for v1 â€” checkboxes, default-on when remotes exist; accurate messaging when they do not (owner, 2026-10-02). |
| DEC-3 | Git hooks when a repo uses `core.hooksPath` (husky, lefthook) or already has its own hook: chain them, or detect and warn? | Detect and warn in v1 (G1). Chaining in v1.1. | G1 | DECIDED | Rename an existing non-GrayMoon hook to `<hook>.replaced-by-graymoon` and warn in logs only; no UX; no chaining in v1 (owner, 2026-10-04). Note: the renamed hook no longer runs; chaining remains a possible v1.1 item. |
| DEC-4 | Local security without user login: App and Worker share a generated secret that the Worker must present to `/hub/agent` and `/repos/{id}/connector`. OK for v1? | Yes. No user login anywhere; the secret is created on first start and handed to the Worker by the existing install flow. | F2 | DECIDED | Yes (default) - owner, 2026-10-03. |
| DEC-5 | Every Workspace repo always gets a worktree (no repo picker) in v1? | Yes. Repo picker is v1.1. | none (scope) | OPEN | |
| DEC-6 | Hide Features behind a setting (kill switch) for v1? | Yes, a setting `Features:Enabled` (default true) that hides create and the selector's Features section. Existing worktrees stay usable from plain Git. | R2 | DECIDED | **No** - owner (2026-10-04): Features are a core part of GrayMoon, not an optional feature. No feature flag, no `Features:Enabled` setting. R2 is dropped. |
| DEC-7 | Are Features supported with a Linux Worker in v1? Feature paths were always built Windows-shaped (`CombineWindows` in `WorkspaceContextPathResolver` and two near-duplicates), even though the Agent/Worker already ships, installs (systemd), and is distributed for Linux today â€” only the Feature path-building added for this release assumed Windows. | Yes. No refusal, no "is this Linux" branching in business logic: path join style (Windows `\` vs POSIX `/`) is inferred from the shape of whatever path the Agent already handed back (user profile, configured storage root, persisted worktree path) via a shared `AgentPath` helper (E4). A POSIX-shaped Worker path flows through POSIX-shaped automatically; existing Windows-shaped data and behavior is byte-for-byte unchanged. | E4 | DECIDED | Yes â€” owner (2026-10-03): Features work with any Worker OS the same way; fix the path-building to be shape-aware instead of blocking Linux. |
| DEC-8 | Release checklist items in `04` that no unit covers: bUnit component tests, counters, diagnostics export, startup foreign-key schema check, downgrade path, ops doc, paths over 260 characters (`core.longpaths`; **pulled into v1 as E6 by the owner, 2026-10-04, DONE**). Add units, or move them to v1.1? | Move to v1.1, except: `core.longpaths` (E6, done), the ops doc goes into R1, the downgrade note goes into R1 troubleshooting ("restore the backup with the previous build"), and pure-helper unit tests replace bUnit. R4 ticks these as "moved per DEC-8". | R1, R4 | OPEN | |

---

# PART C - Tracker (the live part; update it every session)

Status values: `TODO`, `IN PROGRESS`, `BLOCKED`, `DONE`, and for gates `WAITING`, `READY FOR USER TEST`, `PASSED`, `FAILED`.

Lanes: **0** foundation, **A** Agent truth, **B** data integrity, **C** lifecycle and recovery, **D** honest removal, **E** names and UX, **F** local security, **G** hooks, **I** branch rules inside a Feature, **H** logging, **R** release.

| Order | Unit | Title | Lane | Depends on | Size | Status | Owner | Notes |
|---|---|---|---|---|---|---|---|---|
| 1 | U0-1 | Baseline and housekeeping | 0 | - | S | DONE | Claude Sonnet 5 (2026-10-01) | Baseline: build 1 warning (CS8619), Common.Tests 179/179, App.Tests 444/444, Agent.Tests 165/165. After fix: build 0 warnings; Common.Tests 179/179, App.Tests 444/444, Agent.Tests 165/165 x3 runs. Flaky pipe test was actually in Common.Tests/CommandLineServiceTests.cs (T1), not Agent.Tests; ran that test class 3x too, all green. |
| 2 | U0-2 | Safe migration runner | 0 | U0-1 | M | DONE | Claude Sonnet 5 (2026-10-01) | Migration. `PRAGMA user_version` gates a tolerant legacy-baseline step (version 1, all prior migration methods) and a `StrictSteps` list for future versioned, transactional steps (currently empty; B2 and E1 append to it). Backup via `VACUUM INTO` runs once before the first pending step (legacy or strict), keeps 3 newest. App.Tests 447/447, Common.Tests 179/179. |
| 3 | U0-3 | Golden 0.1.0 upgrade test | 0 | U0-2 | S | DONE | Claude Sonnet 5 (2026-10-01) | Fixture produced from tag 0.1.0 in a temporary worktree ($env:TEMP\gm-010, removed after use): 1 connector (dummy token), 1 workspace, 3 repositories linked, 2 projects, 1 dependency. Fixture size 200 KB. New test `UpgradeFrom010Tests` runs `Migrations.RunAllAsync` against a copy of the fixture, queries every `AppDbContext` DbSet, and asserts the seeded rows survive plus the backfilled special Workspace context exists. App.Tests 448/448 (447 before plus this one). Build 0 warnings. |
| 4 | GATE-1 | Upgrade your real database | gate | U0-3 | - | PASSED | Owner (2026-10-01) | Owner upgraded real database from this branch; no issues seen. |
| 5 | A1 | Agent `InspectWorktree` command | A | GATE-1 | M | DONE | Claude Sonnet 5 (2026-10-01) | New read-only Agent command reports isRegistered, exists, isLocked/lockReason, headSha, branch, dirty state (staged/unstaged/untracked/conflict counts), hasUpstream, aheadOfUpstream, behindUpstream, aheadOfDefault for one worktree. Added `GitWorktreePorcelainParser` `locked` parsing alongside existing `prunable`. Common.Tests 181/181 (179 baseline + 2 new parser tests). Agent.Tests 172/172 (165 baseline + 7 new InspectWorktreeCommandTests). Build 0 warnings. Not yet called from the App (A2 scope). |
| 6 | A2 | Remove analysis uses the Agent, not App disk | A | A1, DEC-1 | M | DONE | Claude Sonnet 5 (2026-10-01) | Remove Feature analysis and external-worktree cleanup now call Agent `InspectWorktree` instead of `Directory.Exists`; new `WorktreeStatusUnknown` distinguishes Unknown from Missing and blocks Remove / force-discard until the Worker confirms. Architecture test bans disk APIs in `App/Services/Features`. App.Tests 456/456 (448 + 8 new). Common.Tests 181/181 unaffected. Build 0 warnings. |
| 7 | A3 | Live outgoing commits and PR facts in remove analysis | A | A2 | S | DONE | Claude Sonnet 5 (2026-10-01) | OutgoingCommits/HasUpstream/new AheadOfDefault now read live from Agent InspectWorktree (not cached DB state); one PR refresh per analysis via WorkspacePullRequestService.RefreshContextPullRequestsAsync (now returns per-repo outcome); IsAutomaticallySafe treats null counts and failed PR refresh as not safe; Completed requires AheadOfDefault==0 or merged PR; new headline for pushed-commits-no-PR and a PullRequestStatusUnknown callout in RemoveFeatureModal. App.Tests 460/460 (456 + 5 new, 1 removed). Build 0 warnings. GATE-2 still waits on B2, B3, D3, D5, I1. |
| 8 | B1 | Remove deletes context-scoped project data | B | GATE-1 | S | DONE | Claude Sonnet 5 (2026-10-01) | `RemoveFeatureCoreAsync` now runs `DeleteContextScopedProjectDataAsync` (ExecuteDeleteAsync, no cascade dependency) for ProjectDependencies/WorkspaceProjects/WorkspaceFileLineStatuses of the removed Feature context only, guarded by `context.Kind == Feature`. New `RemoveFeatureProjectDataTests.cs`: 2 new tests (cross-context isolation; same again with `PRAGMA foreign_keys = OFF` to prove no cascade dependency on upgraded DBs). App.Tests 462/462 (460 + 2 new). Build 0 warnings. |
| 9 | B2 | Orphan cleanup and foreign keys on upgraded databases | B | B1 | M | DONE | Claude Sonnet 5 (2026-10-01) | Migration. Owner decided the FK-scope question: WorkspaceProjects gets the FK/cascade rebuild (already matches a fresh database); WorkspaceFileLineStatuses gets orphan cleanup only, no FK (a fresh database has no FK there either, so this is true schema parity). New strict step (version 2) in Migrations.Features.cs deletes orphan ProjectDependencies, WorkspaceProjects and WorkspaceFileLineStatuses rows (context id set but missing from WorkspaceFeatureContexts; null context id rows never touched), then rebuilds WorkspaceProjects with the WorkspaceFeatureContextId foreign key matching a fresh EnsureCreated() database. Discovered during implementation: SQLite fires ON DELETE CASCADE for every row of a table when that table itself is dropped while a child's foreign key still targets it, even with PRAGMA foreign_keys=OFF attempted (a no-op once a transaction is open, which the existing StrictSteps runner always has). Dropping WorkspaceProjects directly would have silently wiped every ProjectDependencies row; fixed by retargeting ProjectDependencies at the new table first (its own temporary rebuild) before dropping the old WorkspaceProjects table, letting SQLite's automatic foreign-key-text rewrite on RENAME restore the correct reference. New test file WorkspaceFeatureContextOrphanCleanupTests.cs (6 tests): orphan cleanup with null/valid/orphan context ids, FK rebuild preserves row ids and indexes, WorkspaceFileLineStatuses keeps no foreign key, idempotency, schema parity (fresh vs upgraded 0.1.0 fixture), and the Workspace dependency graph (projects, edges) unchanged on the 0.1.0 fixture. One pre-existing test (MigrationsRunnerTests) updated to compute the expected post-migration version dynamically instead of hardcoding LegacyBaselineVersion, since StrictSteps now has an entry. App.Tests 468/468 (462 baseline + 6 new). Build 0 warnings. |
| 10 | B3 | Scope project queries by context | B | B1 | M | DONE | Claude Sonnet 5 (2026-10-01) | Step 0 answer (checked on a copy of the owner's real database): all 2380 `WorkspaceProjects` rows and all 126 `WorkspaceFileLineStatuses` rows already have `WorkspaceFeatureContextId` set (0 null), because every Workspace in that database has been resynced since the Feature upgrade; implemented the `IS NULL OR == special` filter anyway (defensive, matches the migration's nullable column with no backfill). Added new `WorkspaceFeatureContextId?`-scoped overloads alongside the existing unscoped methods (never changed their signatures, so every other, unnamed caller of `GetByWorkspaceIdAsync`/`GetDependencyEdgesAsync` keeps its exact legacy behavior) for `GetByWorkspaceIdAsync`, `GetDependencyEdgesAsync`, `GetPackageDependencyLinesByRepoAsync`, `GetPackageDependencyLinesForRepoAsync`, `GetMismatchedDependencyLinesForRepoAsync`, `GetDependencyGraphAsync`, `GetRepositoryDependencyGraphAsync`. `GetMismatchedDependencyLinesForRepoAsync`'s new overload compares against a Feature's own `WorkspaceRepositoryContextState.GitVersion` (null, never the shared link, when that Feature has no context-state row yet). Updated the three named callers to pass their already-available context id: `WorkspaceGitService.Restore.cs` (both Restore methods), `WorkspaceDependencies.razor`, `WorkspaceRepositories.Loading.cs` grid tooltips. New test file `WorkspaceProjectRepositoryContextScopingTests.cs` (15 tests): 8 scoping tests seeding a Workspace context and a Feature context with deliberately different projects/dependencies/versions (plus one proving the legacy-null-row fallback only applies to the special Workspace, never a Feature) and 7 "pin the Workspace first" characterization tests (one per method) on a database with no Features, each asserting the new context-scoped call returns exactly the same rows as the old unscoped call, kept green after the change. `GrayMoon.App.Tests` 483/483 (468 baseline + 15 new), run twice for determinism. Build 0 warnings. Added a Desktop README bullet (Restore no longer repeats a Feature's projects into the Workspace). Note: the `rg -n "GetByWorkspaceIdAsync\("` Done-when check also matches same-named methods on unrelated repository classes (`WorkspacePullRequestRepository`, `WorkspaceFileRepository`, `WorkspaceActionRepository`, `WorkspaceFileVersionConfigRepository`) and two call sites inside `WorkspaceProjectRepository.DependencyGraph.cs` that belong to unnamed methods (`LoadWorkspaceRepoDependencyGraphAsync`, used only by the out-of-scope `GetImplicitReferencedRepoIdsAsync`/`GetCircularCustomDependencyRepoIdsAsync`); none of those are `WorkspaceProjectRepository.GetByWorkspaceIdAsync`'s own named callers, so they were left on the legacy overload per A7 scope discipline. Filed one Discovered issue (Part F) about an existing, out-of-scope fallback-to-shared-version bug in `DependencyStats.cs`. |
| 11 | B4 | Close remaining context leaks | B | B3 | M | DONE | Claude Sonnet 5 (2026-10-01) | Four remaining reads scoped by context. (1) `WorkspaceProjectRepository.Push.cs` `GetPushPlanPayloadAsync`'s tag exclusion now reads `CheckedOutTag` from the calling context's own state (new private `GetContextCheckedOutTagByRepoAsync`), not the shared link. (2) `WorkspaceActionNotificationPanel.razor`'s `repoIdsThatNeedPush` now comes from a new `WorkspaceRepository.GetRepositoryIdsThatNeedPushForNotificationAsync(workspaceId, contextId, repoIds, maxLevel, includeNeverPushedUpstream)` that reads context state, used at both call sites (push-badge click and auto-push), keeping their two different predicates (outgoing-only vs outgoing-or-never-pushed-upstream) intact. (3a) `IWorkspaceFileOperations.ListAsync` takes a `WorkspaceFeatureContextId` and overlays `IsMissingOnDisk` from `WorkspaceFileVersionService.GetMissingFlagsByFileIdAsync`, not the shared `WorkspaceFile` row; `WorkspaceEndpoints.GetWorkspaceFiles` resolves and passes the context id same as `SearchWorkspaceFiles` already did. (3b) new `WorkspaceProjectRepository.GetPackagesByWorkspaceIdAsync(workspaceId, contextId)` overload (real packages scoped, `IsGenerated` rows always included) replaces `WorkspacePackages.razor`'s fetch-all-then-filter-in-memory, which had been dropping workspace-global generated packages whenever a Feature was selected; `SyncGeneratedPackageDependenciesAsync`'s cross-context edge fan-out was left unchanged, confirmed intentional by its own existing test. (4) `WorkspaceRepositories.Branches.cs`'s `CheckoutCommonBranchAcrossWorkspaceAsync` now passes a `workspaceRepositoryId`-by-`repositoryId` map to a rewritten `WorkspaceBranchHandler.CheckoutBranchForWorkspaceAsync`, which calls `IWorkspaceBranchOccupancyService.GetBadgesForRepositoryAsync` per repo before checkout; a repo whose badge disallows checkout is skipped and reported (never attempted), and a repo whose occupancy answer could not be determined is checked out exactly as before with a new `WorkspaceBranchBulkResult.OccupancyCheckDegraded` flag (default `false`, so `WorkspaceBranchBulkResult.Empty` is unaffected) surfaced as a "Could not check worktrees" toast; `BranchModal.razor` and `WorkspaceBranchOccupancyService.cs` themselves were not touched (UI-only and I2-owned respectively). New `WorkspaceB4ContextLeakTests.cs` (10 tests: context-isolation and "pin the Workspace first" characterization pairs for items 1 to 3) and `WorkspaceBranchHandlerOccupancyTests.cs` (3 tests: occupied repo skipped and reported, degraded flag when the occupancy check throws, normal checkout when no badge blocks it) for item 4. `GrayMoon.App.Tests` 496/496 (483 baseline + 13 new), build 0 errors. Appended " Done (B4)." to the P1-7 Fix cell in `04-worktree-v1-release-roadmap.md`. |
| 11a | B5 | Connector refresh and delete never drop a Feature's repository | B | GATE-1 | M | DONE | Claude Sonnet 5 (2026-10-01) | Refresh keeps a Feature-used repository (and reports it); connector delete refuses when any repository is used by a Feature. App.Tests 500/500 (496 baseline + 4 new). |
| 11b | B6 | Edit Workspace: no partial save; rename and root change blocked while Features exist | B | GATE-1 | S | DONE | Claude Sonnet 5 (2026-10-01) | `UpdateAsync` now computes name/root-path change before any write and refuses with "Rename and root changes are not possible while Features exist. Remove Features first." before touching the database when the Workspace has Features; the rename/root save and the membership change now run in one transaction (`ReplaceRepositoriesCoreAsync`, no transaction of its own) so a membership failure rolls back the rename. `ReplaceRepositoriesCoreAsync` now throws the membership message only when the requested set actually differs from the current set (an unchanged set, including after invalid-id filtering, is a no-op and never blocked by Features). Edit Workspace dialog shows the name field read-only with the refusal reason as a hint once the Workspace has any Feature (new `WorkspaceModal.NameDisabledHint` parameter; `Workspaces.razor` resolves "has Features" via the existing `IWorkspaceFeatureOperations.ListFeaturesAsync` facade, no new repository method). Deleted the unused `WorkspaceFeatureOperations.EnsureNoFeaturesBeforeMembershipChangeAsync` (never called). New tests in `WorkspaceRepositoryReplaceTests.cs` (5): a Workspace-without-Features characterization test (rename, root change, and membership change together, A4 step 0) plus the 4 Workspace-with-Features cases from the unit text (unchanged save succeeds; rename refused and nothing saved; root change refused and nothing saved; membership change refused and name unchanged). Confirmed all 3 new "with Features" partial-save tests fail against the original code for the described reason (rename/root committed before the unconditional membership throw) and pass after the fix; `GrayMoon.App.Tests` 505/505 (500 baseline + 5 new), run twice. Build 0 warnings. Added a Desktop README bullet. |
| 11c | B7 | Workspace delete refused while Features exist | B | GATE-1 | S | DONE | Claude Sonnet 5.5 (2026-10-04) | Owner reversed the G3 move for this one guard (G2, G3 unhooking and G4 stay in `12-hook-cleanup-plan.md`). `WorkspaceRepository.DeleteAsync` now throws `InvalidOperationException("Remove the Features first, then delete the Workspace.")` (const `WorkspaceDeleteBlockedByFeaturesMessage`) when any `WorkspaceFeatures` row exists for the Workspace (any lifecycle state), before anything is deleted; a missing Workspace is still a silent no-op. `Workspaces.razor`: `ShowDeleteModal` asks the existing `IWorkspaceFeatureOperations.ListFeaturesAsync` facade and shows the message in an alert inside the Remove Workspace dialog with Remove disabled; `DeleteWorkspaceAsync` also catches the exception (a Feature created after the dialog opened) and shows the same message instead of the generic failure text. New tests in `WorkspaceRepositoryReplaceTests.cs` (4): Workspace without Features deletes exactly as before, links included (A4 step 0 characterization); with a Feature it is refused and nothing is deleted; Features of another Workspace do not block; succeeds once the Feature is gone. The two refusal tests failed before the change. App.Tests 796/796. Build 0 warnings. Desktop README bullet added. |
| 12 | C1 | Create Feature intent in one transaction | C | GATE-1 | S | DONE | Grok (2026-10-02) | Validate every repo HEAD (including blank SHA) before any Feature write. Feature + context + Pending rows commit in one DB transaction; failure rolls back all three. C1-fix: do not call `pathResolver` inside that transaction (second AppDbContext + Git Changes writers caused SQLite Error 6 table locked); build Pending `WorktreePath` from `ManagedFeatureStorageRoot` + Feature name + repo name instead. `CreateFeatureIntentAtomicityTests` (3). `SyncStateTestContext.CreateAsync` accepts optional `configureServices` / `configureDb`. |
| 13 | C2 | Reconcile stuck Features at startup and on Worker reconnect | C | C1, A1 | M | DONE | Grok (2026-10-02) | `WorkspaceFeatureReconciler` (singleton + hosted) runs on Worker Online: stuck Creating/Removing -> NeedsRepair; Ready missing worktree -> NeedsRepair; Pending/NeedsRepair registered -> Ready; untracked under Feature root -> NeedsRepair; 60s throttle + SemaphoreSlim(1); skips structurally busy workspaces. `WorkspaceFeatureReconcilerTests` (18). |
| 14 | C3 | Selector shows every state with friendly labels | C | C2 | S | DONE | Grok (2026-10-02) | All Feature states listed; friendly labels via `FeatureSelectorPresentation`; NeedsRepair selectable + read-only (Sync/Push/Update disabled, banner). `FeatureSelectorPresentationTests` (9). |
| 15 | C4 | Repair (retry, continue remove) and Roll back service operations | C | C2, D2 | M | DONE | Grok (2026-10-02) | `RepairFeatureAsync` / `RollbackFeatureAsync` / `IsRemoveIncompleteAsync` on facade; refuse remove-incomplete; repair seeds once when all Ready; rollback force=false and never deletes default/primary branch. `WorkspaceFeatureRepairTests` (7). |
| 16 | C5 | Feature status panel | C | C3, C4 | M | DONE | Grok (2026-10-02); UX fixes Grok (2026-10-03) | `FeatureStatusPanel.razor`; Create failure opens panel; Feature menu Status and repair; Continue removal for remove-incomplete. Panel reads rows via GetFeatureStatusAsync (facade, no direct DbContext). 2026-10-03: replaced the bordered "Show details" `alert-warning` banner with a dark amber status-callout and header `Repair` button (visible, replaces Sync); panel drops z-index below `BackgroundJobOverlay` while Retry/Roll back runs so the overlay covers it instead of it sticking out; `modal-dialog-scrollable` caps dialog height; identical per-repo messages (e.g. "Worker not connected" on every repo) collapse to one line instead of repeating. |
| 17 | D1 | Agent removes worktree residue and reports what is left | D | GATE-1 | M | DONE | Claude Sonnet 5 (2026-10-01) | After `git worktree remove`, `GitService.RemoveWorktreeAsync` now also runs a custom residue walk (clears read-only attributes, deletes reparse points as the link itself without entering them, retries each entry up to 5 times at 200/400/800/1600/3200 ms) when every safety guard passes, and reports `residueRemaining`/`residueFileCount`/`residueSampleFiles`/`residueMessage` either way. Discovered empirically: `git worktree remove` (with or without `--force`) unregisters the worktree from `git worktree list` even when it cannot finish deleting the directory itself (for example a file open elsewhere, which fails with a non-zero exit code on Windows); the method now re-checks the worktree list after the git call regardless of exit code and only reports `GitFailed`/`VerifyFailed` when the path is still registered, so a locked file produces an honest residue report instead of swallowing the whole remove as failed. New request fields `featureRootPath`/`featureStorageRoot` (both optional) gate all deletion; without `featureStorageRoot` nothing is deleted, matching an old App's behaviour exactly. All 5 safety guards from the unit text are implemented as a pure `internal static GitService.ValidateResidueRemovalGuards` method (depth-under-storage-root checked before the Feature-root relationship check, so a path that is both outside the root and too shallow is reported as too shallow, isolating that guard's own test). `CreateWorktreeAsync` now allows creating into an existing, empty folder (still refuses a non-empty one or a file at that path). New `WorktreeResidueResult` record in `Agent/Models/`. New test file `RemoveWorktreeResidueTests.cs` (15 tests): one per safety guard (6, including a well-formed-path sanity check), leftover files removed when a worktree was already unregistered outside GrayMoon, a locked file produces `residueRemaining` with that file sampled, the Feature root folder is removed only once both of its repos are gone, create into an empty vs non-empty existing folder, a junction inside the worktree is removed without entering it and its external target survives, and 3 Worker-compatibility tests (old-shape request JSON still deserializes and skips deletion; old-shape response JSON still deserializes with safe defaults). `GrayMoon.Agent.Tests` 187/187 (172 baseline + 15 new), the new file re-run 3 times for determinism given its retry-timing logic. `GrayMoon.App.Tests` 505/505 unaffected (no App files changed; the new request/response fields are additive and the App's existing anonymous-object calls to `RemoveGitWorktree` are unaffected). Build 0 warnings. Fixed 2 fake `IGitService` test doubles in `Agent.Tests` (`ReturnToDefaultBranchCommandTests.cs`, `DeleteBranchCommandTests.cs`) for the new interface signature. CI fix (2026-10-01): 2 tests OS-guarded/adjusted for Linux runners (junction creation and open-file-lock semantics are Windows-only; see commit). |
| 18 | D2 | App removal report and no swallowed branch failures | D | D1 | M | DONE | Claude Sonnet 5 (2026-10-01) | Added `Removing`/`Removed` to `WorkspaceFeatureRepositoryState`; `RemoveFeatureCoreAsync` now sets every live row to `Removing` up front, marks each repo `Removed` with its own short-lived context right after its worktree is unregistered (crash-safe), categorizes the branch delete into `NotApplicable`/`Deleted`/`KeptUnmerged`/`Failed` (never silently logged only), and returns a per-repo `RemoveFeatureRepositoryReport` on `OperationResult`. A failed repo's row stays `Removing` with its error in `LastError` (feature-level state still becomes `NeedsRepair`). Added `IWorkspaceGitChangesMonitoringPause` (new file) to stop the Git Changes sweep for the Feature's context id only during Remove, restored in a `finally`; the Workspace's own monitoring is untouched. `RemoveFeatureModal.razor` shows the report (headline + warnings) with a Done button instead of closing immediately. `GrayMoon.App.Tests` 511/511 (505 baseline + 6 new in `RemoveFeatureReportTests.cs`; 1 pre-existing test updated for the new row-state behaviour). Build 0 warnings. Desktop README bullet added. |
| 19 | D3 | Remove dialog: checkboxes that match the situation | D | D2, A2 | S | DONE | Claude Sonnet 5 (2026-10-01) | `RemoveFeatureModal.razor` now shows "Permanently discard uncommitted changes in N repositor(y/ies)" (with the repo list) only when at least one repository is actually dirty, and "Delete local branches that have commits not in the default branch (N)" only when at least one repository has `aheadOfDefault > 0` and no merged PR; Remove is enabled only when every checkbox the dialog actually shows is ticked, and stays disabled whenever any repository is Unknown (A2). Added "Local branches are deleted. Remote branches are kept." under the repository list (fixes U-19). **Superseded for branch-cleanup copy by DEC-2 / D4 / D6 (2026-10-02):** that static line is replaced by local/remote delete checkboxes. New pure `internal static` helpers on the component (`IsRepositoryDirty`, `HasUnmergedBranchAheadOfDefault`, `CanRemove`) extract the enable rule per the unit's own Tests-to-add instruction, following the existing `MergePullRequestModal.BuildChecksActionsUrl` pattern for testing razor logic from App.Tests. New `RemoveFeatureModalCheckboxTests.cs` (20 tests: dirty detection, ahead-of-default-without-merged-PR detection, and an 11-case `CanRemove` combination theory covering U-17 directly, including Unknown disk state always blocking regardless of checkbox state). `GrayMoon.App.Tests` 531/531 (511 baseline + 20 new). `GrayMoon.Common.Tests` 181/181 unaffected. `GrayMoon.Agent.Tests` 187/187 unaffected (no Agent code touched). Build 0 warnings. |
| 19b | D5 | Locked worktrees in Remove | D | D3 | S | DONE | Claude Sonnet 5 (2026-10-01) | `GitWorktreeInfo.IsLocked`/`LockReason` (A1) now flow all the way to the Remove dialog: `RemoveFeatureRepositoryPlan` gets `IsLocked`/`LockReason` from `InspectWorktree`, `IsAutomaticallySafe` treats a locked repo as not safe, and `RemoveFeatureModal.razor` shows "Unlock and remove N locked worktrees" (listing each repo's reason) only when at least one repo is locked, gated by the D3 `CanRemove` helper (now also takes `showUnlockCheckbox`/`allowUnlock`). `RemoveFeatureOptions.AllowUnlockWorktrees` flows through `RemoveFeatureCoreAsync` to a new optional `unlock` field on `RemoveGitWorktreeRequest`; the Agent's `GitService.RemoveWorktreeAsync` runs `git worktree unlock` before `git worktree remove` only when `unlock = true`. Old App/Worker compatibility verified by test (missing `unlock`/`isLocked` deserializes to false/not-locked). External-worktree cleanup in the Workspace never sends `unlock` (R-D5, unchanged code path). Common.Tests 181/181, App.Tests 540/540 (9 new), Agent.Tests 189/189 (2 new). Build 0 warnings. GATE-2 now waits on A3 (done), B2, B3, I1. |
| 19a | I1 | Remove analysis judges the Feature branch, not the current checkout | I | A3, D3 | M | DONE | Claude Sonnet 5 (2026-10-01) | Data-loss fix (09 SB-2). `InspectWorktree` gets an optional `featureBranch` request field and 5 nullable response fields (`featureBranchExists`/`featureBranchSha`/`featureBranchAheadOfDefault`/`featureBranchHasUpstream`/`featureBranchAheadOfUpstream`), computed via `git rev-parse`/`rev-list`/`for-each-ref` against `refs/heads/<featureBranch>` without checking anything out; null when `featureBranch` is not sent. `AnalyzeRemoveFeatureAsync` sends `featureBranch = info.FeatureName` for non-tag-pinned rows only (tag-pinned rows unchanged); each plan row gets `FeatureBranchName`/`CheckedOutBranch`/`IsOffFeatureBranch` plus the 4 Feature-branch facts, and computed `EffectiveAheadOfDefault`/`EffectiveOutgoingCommits` (fall back to the old checked-out-branch fields when `FeatureBranchName` is null, so every hand-built test plan and tag-pinned repo keeps its exact old meaning). `IsAutomaticallySafe`, `IsPrMergedOrNeverCreated` and the D3 unmerged-branch checkbox now read the Effective* fields instead of the checked-out branch's. `RemoveFeatureModal.razor` explains drift per repository ("This repository is on `<X>`... `<X>` is kept; `<name>` is deleted." / "The Feature branch is already gone; nothing to delete."), the force checkbox lists each affected Feature branch with its unmerged commit count, and the D2 report gets a new `KeptBranchName` shown as a warning line. An old Worker (Feature-branch fields null) makes the repo not automatically safe and does not offer the force checkbox for it, so a non-force delete is refused by Git and D2 reports it kept, exactly as the unit requires. `GrayMoon.Common.Tests` 181/181 unaffected. `GrayMoon.App.Tests` 545/545 (540 baseline + 5 new in `RemoveFeatureWorkspaceRefreshTests.cs` covering drift+unmerged, drift+merged, old-Worker-unknown, and the A4-step-0 not-drifted characterization test, plus 1 new in `RemoveFeatureReportTests.cs` for `KeptBranchName`; 3 existing `CleanInspectWorktree()`-based test files updated to default to "no drift" so pre-existing assertions keep their exact old meaning). `GrayMoon.Agent.Tests` 194/194 (189 baseline + 5 new in `InspectWorktreeCommandTests.cs`, including 2 Worker-compatibility old-shape-JSON tests; 2 existing fake `IGitService` test doubles updated for the new interface signature). Build 0 warnings. `ExternalWorktreeCleanupTests.cs`/`WorkspaceExternalWorktreeOperations.cs` left untouched per the regression guard (never sends `featureBranch`). Desktop README bullet added. GATE-2 now has every dependency DONE. |
| 20 | GATE-2 | Remove and analysis | gate | A3, B2, B3, D3, D5, I1 | - | PASSED | Owner (2026-10-02) | Owner confirmed Remove Feature state is good; proceed to lane C. |
| 21 | GATE-3 | Failure and recovery | gate | C5 | - | PASSED | Owner (2026-10-03) | Owner confirmed Feature recovery (lane C) is good; proceed to lane E. |
| 22 | E1 | Feature name validation | E | GATE-1 | M | DONE | Claude Sonnet 5 (2026-10-03) | New `FeatureNameValidator` (Git/Windows/shell rules); duplicate check case-insensitive; `COLLATE NOCASE` index via `.UseCollation` + strict migration step 3 (skips with a warning on an existing case-only duplicate); modal validates on every keystroke. Common.Tests 234/234, App.Tests 613/613, Agent.Tests 248/248 (git-parity theory). Build 0 warnings. |
| 23 | E2 | Header primary button rule | E | - | S | DONE | Claude Sonnet 5 (2026-10-03) | New `WorkspaceRepositoryHeaderStateDto.AllFeaturePrsCompleted` (query service, Feature path only: at least one PR, none open, no repo with commits outside a PR via `!HasCreatablePr`); `WorkspaceRepositoriesHeader.DeterminePrimaryAction` (internal static, `HeaderPrimaryAction` enum) replaces the old `ShowRemoveFeaturePrimary` rule (`!HasCreatablePr && !HasOpenPr`, which showed Remove on a fresh Feature with zero commits and no PR). `WorkspaceRepositories.State.cs` adds `allFeaturePrsCompleted`; `WorkspaceRepositories.razor` passes it through. App.Tests 626/626 (613 baseline + 13 new: 8 in `WorkspaceRepositoriesHeaderPrimaryActionTests.cs` covering the unit's 4 Feature cases plus 3 Workspace regression cases per R-E2 plus the creatable-wins-over-remove case; 5 in `WorkspaceRepositoryLinkListQueryServiceTests.cs` for the DTO aggregation). Common.Tests 234/234 unaffected. Build 0 warnings. Desktop README bullet added. |
| 24 | E3 | Desktop "Open in..." launch quoting | E | - | S | DONE | Claude Sonnet 5 (2026-10-03) | `WebMessageBridgeService`: Cursor/Code/Visual Studio launch via `CreateOpenInExecutableStartInfo` (`UseShellExecute = false`, path as a single `ArgumentList` item, never a command string), falling back to `CreateOpenInShellFallbackStartInfo` (`cmd.exe /c <tool> .`, `WorkingDirectory = path`) only if the direct launch throws (covers VS Code's `code.cmd` shim). Claude CLI and Open in Terminal now use `CreateWindowsTerminalStartInfo` (`wt.exe -d .` + optional command) and `CreateCmdKeepOpenStartInfo` (`cmd.exe /c start "" cmd.exe /k "cd /d . [&& command]"`) fallback; both set `WorkingDirectory = path` and pass the literal `.` as the folder argument, so the real path is never re-parsed by cmd/wt (no `%` expansion, no `;`/`&`/`(`/`'` breakout). New `OpenInToolStartInfoTests` (36 cases: 6 tricky paths - spaces, `&`, `;`, `(`, `'`, `%` - x 6 test methods covering `CreateOpenInExecutableStartInfo`, `CreateOpenInShellFallbackStartInfo`, and `CreateWindowsTerminalStartInfo`/`CreateCmdKeepOpenStartInfo` with and without a trailing command). `GrayMoon.Desktop.Tests` 185/185 (149 baseline + 36 new). Build 0 warnings. Desktop README bullet added. |
| 24a | E4 | OS-aware Feature paths (no refusal) | E | DEC-7 | S | DONE | Claude Sonnet 5 (2026-10-03) | Replaced the planned "refuse on non-Windows" with the owner's actual intent (DEC-7 revised 2026-10-03): new internal `AgentPath` (`GrayMoon.App/Services/Features/AgentPath.cs`) infers Windows `\` vs POSIX `/` join style from the shape of the path text itself (POSIX starts with `/`) â€” not an OS flag â€” then `Combine`/`Normalize`/`GetFileName`/`GetDirectoryName`/`IsLegacyWindowsDriveRootGraymoonPath` replace 4 near-duplicate Windows-only implementations: `WorkspaceContextPathResolver` (`CombineWindows`, `GetWindowsFileName`, `GetWindowsDirectoryName`, `IsWindowsDriveRootGraymoonPath`), `WorkspaceFeatureOperations` (`CombineWindowsPath`, `IsWindowsDriveRootGraymoonPath`), `WorkspaceFeatureReconciler` (`CombineWindows`, plus `NormalizePathKey`/`IsPathUnder` generalized to either style), and `WorkspaceService.TryGetAgentDefaultFeatureStorageRootAsync`/`NormalizeWindowsRoot` (now derives `{profile}/.graymoon` or `{profile}\.graymoon` purely from the Agent's own `UserProfilePath` shape â€” no new `GetHostInfo` field needed). Also generalized `WorkspaceHookContextAttributor.NormalizePath` and `WorkspaceBranchOccupancyService`'s two inline path-key normalizations the same way. No `osPlatform` field was added to `GetHostInfo`: it was unnecessary once path style is inferred from the path text, which keeps the fix a generic path-handling correctness change rather than Linux-specific branching. New `AgentPathTests.cs` (31 tests: `IsPosix`, `Normalize`, `Combine` incl. mixed separators/empty segments, `GetFileName`, `GetDirectoryName`, legacy-drive-root detection) plus 3 new tests in `FeatureContextIsolationTests.cs` proving a POSIX-configured storage root, a POSIX persisted worktree path, and a POSIX Agent `UserProfilePath` all stay `/`-shaped end to end. Every pre-existing Windows-shaped test (the original regression guard) is unchanged and still green. `GrayMoon.App.Tests` 660/660 (626 baseline + 34 new). Build 0 warnings. Filed one Discovered issue (Part F) for an out-of-scope, pre-existing Windows-only hardcode in `WorkspaceGitChanges.CopyPath.cs` ("Copy absolute path"), not touched here. |
| 25 | F1 | Per-install token encryption key | F | GATE-1 | M | DONE | Claude Sonnet 5 (2026-10-03) | `TokenEncryptionKeyProvider`: a configured `TokenKey` always wins (unchanged behavior, now tagged key id `configured` instead of reusing `default`); otherwise a random 32-byte key is generated once and kept in `graymoon.key` next to the database (new shared `DatabasePathResolver`, same path logic `Program.cs` already used for the DB and Data Protection folders), DPAPI-protected (CurrentUser) on Windows, raw bytes with Unix mode 600 elsewhere. The old hard-coded key (`CreateDefaultKeyString`, renamed `DeriveLegacyKey`) is kept forever under a new `LegacyKeyId = "default"` constant for decrypt-only; `GetKeyById` now actually branches on key id (current/legacy/unknown-falls-back-to-current) instead of always returning the single key. New `ITokenProtector.TryGetKeyId` (peeks the `v2:KEYID:...` prefix without decrypting, null for legacy plain/Base64) backs a new static `TokenReencryptionService.ReencryptLegacyTokensAsync`, called once at startup right after `Migrations.RunAllAsync` (after the U0-2 backup, before any hosted service): decrypts every connector token still on the legacy key id and re-persists it under the current key only after the round trip succeeds, logging a count; never throws (try/catch around the whole pass). `TokenHealthBackgroundService` now catches `UnprotectToken` failures specifically and sets `LastError = "Token needs to be re-entered."` instead of a raw exception message. Added `System.Security.Cryptography.ProtectedData` package reference (needed for DPAPI on net10.0). Fixed the inaccurate `CLAUDE.md` statement that tokens are "backed by ASP.NET Core Data Protection" (unrelated; DP only protects antiforgery/circuit state). New `TokenEncryptionKeyProviderTests.cs` (6, including a Unix-only file-mode test that is a no-op pass on Windows) and `TokenReencryptionServiceTests.cs` (4). `GrayMoon.App.Tests` 670/670 (660 baseline + 10 new). `GrayMoon.Common.Tests` 234/234 unaffected. Build 0 warnings. Desktop README bullet added. |
| 26 | F2 | Worker secret for hub and token endpoint | F | F1, DEC-4 | M | DONE | Claude Sonnet 5.5 (2026-10-03) | Worker secret (`graymoon-worker.secret`) required on `/hub/agent` and `/repos/{id}/connector` once any Worker has presented it (or `Security:RequireWorkerSecret`); wrong secret always 401; connector/pair with an Origin header 403. One-time pairing code (`POST /api/worker/pair`) for manual installs; Desktop copies the secret itself; install script never contains it. App.Tests 774/774 (37 new), Agent.Tests 255/255 (6 new), Desktop.Tests 199/199 (14 new), Common.Tests 234/234. Build 0 warnings. GATE-4 owner check: reinstall the Worker and confirm it still connects. |
| 27 | F3 | Block cross-site and rebinding requests (REST and hubs) | F | - | S | DONE | Claude Sonnet 5 (2026-10-03) | New `RequestSecurityMiddleware`: non-GET `/api/`/`/repos/` requests with an `Origin` header need a same-host (or loopback, or `Security:AllowedOrigins`) Origin plus `X-GrayMoon-Request: 1`; no-`Origin` requests (scripts, Worker) pass unchanged. `/hub/agent` rejects any `Origin`; `/hubs/workspace-sync`, `/hubs/desktop`, `/_blazor` require a same-host/loopback/allowed Origin when one is present. Desktop sets `AllowedHosts` to loopback only for its own App process; shared `appsettings.json` keeps `"*"`. No first-party JS posts to `/api/` so no header needed adding. App.Tests 707/707 (670 + 37 new, pure static methods + full `InvokeAsync`). Common.Tests 234/234, Agent.Tests 249/249, Desktop.Tests 185/185 unaffected. Build 0 warnings. Documented in `05-user-capability-reference.md` section 33. Desktop README bullet added. |
| 28 | G1 | Detect hook conflicts and warn | G | DEC-3 | M | DONE | Claude Sonnet 5.5, 2026-10-04 | Owner variant of DEC-3: `GitService.WriteSyncHooksAsync` resolves the folder with `git rev-parse --git-path hooks`; a missing hook is written, a GrayMoon-marked hook (first comment line after the shebang starts with `# Created by GrayMoon.Agent`) is rewritten only when its content ignoring the marker line changed, and a non-GrayMoon hook is renamed to `<hook>.replaced-by-graymoon` (UTC-timestamped name if taken; never overwritten or deleted) before GrayMoon's hook is written, with a Warning log. A failed rename leaves the original and skips that hook; hook writing never throws. A `core.hooksPath` outside the common Git dir writes and renames nothing, one Warning per Sync; inside it, treated as the normal folder. `post-update` is no longer written (existing files untouched). Deviations from the written steps: no UX, no `hookStatus` field (no DTO change, so no compat test), rename instead of leave-in-place. Tests: `GitServiceSyncHooksTests` (13, real git, includes a 0.1.0-text hook and a linked worktree), `Agent.Tests` 269/269 (256 baseline + 13 new). |
| 28a | G2 | Worker `UnhookRepository` command | G | G1 | S | MOVED | Owner (2026-10-04) | Not worktree work. Moved to `12-hook-cleanup-plan.md`; do not implement here. |
| 28b | G3 | Unhook when repositories leave a Workspace; block Workspace delete while Features exist | G | G2, B5, B6 | M | MOVED | Owner (2026-10-04) | Unhooking moved to `12-hook-cleanup-plan.md`. The Workspace-delete guard stayed in this plan as B7 (owner, 2026-10-04). |
| 28c | G4 | Self-heal stale hooks from pings | G | G2 | S | MOVED | Owner (2026-10-04) | Moved to `12-hook-cleanup-plan.md`. |
| 28d | I2 | Own Feature branch is checkable; "Return to Feature branch" | I | GATE-1 | S | DONE | Claude Sonnet 5 (2026-10-04) | New `FeatureBranchPolicy` (`ExpectedBranch`, `IsOffFeatureBranch`, pure). `WorkspaceBranchOccupancyService.GetBadgesForRepositoryAsync`'s second loop now emits `BranchOccupancyKind.FeatureOwn` (`AllowCheckout = true`, `RequiresFeatureCleanup = false`, `ContextId = null`) when a Feature row's own context equals the viewing context, instead of the blocked `Feature` badge; other Features' rows and the first loop (worktree-list matches) are unchanged. `SwitchBranchModal.razor` gets a new `FeatureBranchName` parameter: the `FeatureOwn` badge reads "This Feature" and never offers a delete button (ordinary or cleanup-routed); when `IsFeatureContext` and `FeatureBranchPolicy.IsOffFeatureBranch(FeatureBranchName, CurrentBranch)`, a warning callout plus a "Return to Feature branch" button (calls `OnCheckoutBranch(RepositoryId, FeatureBranchName, false)`) renders above the tabs. `WorkspaceRepositories.razor.cs` stores `_selectedFeatureName` from the resolved context (set in `ApplySelectedContext`) and computes `FeatureBranchName` once per modal open from that name plus the dialog's own repository's `CheckedOutTag` (the context-scoped pinned-tag field already on `WorkspaceRepositoryLink`) via `FeatureBranchPolicy.ExpectedBranch`; never recomputed per grid row. SB-8 fixed: `ShowSwitchBranchModalOnTagsTab` now sets `WorkspaceRepositoryId` and `CurrentBranch` the same way `ShowSwitchBranchModal` does (previously left at 0/null, which could checkout the wrong repository). New `WorkspaceBranchOccupancyServiceFeatureOwnTests.cs` (2: viewing Feature A shows A's own undrifted-but-unlisted branch as `FeatureOwn`/checkable while Feature B's stays `Feature`/blocked; viewing the special Workspace keeps both Features' branches `Feature`/blocked - this second test doubles as the A4 step 0 Workspace characterization, since the changed code path only branches for the viewing context's own rows) and `FeatureBranchPolicyTests.cs` (7 theory cases). `GrayMoon.App.Tests` 716/716 (707 baseline + 9 new). `GrayMoon.Common.Tests` 234/234 unaffected. Build 0 warnings. Desktop README bullet added. |
| 28e | I3 | Feature-aware Switch Branch dialog and service rules | I | I2 | M | DONE | Claude Sonnet 5.5 (2026-10-03) | New `IFeatureBranchGuard` / `FeatureBranchGuard` (DI-registered; special Workspace and unknown context return allowed after one context lookup, no Feature-repository query) calls `FeatureBranchPolicy.Evaluate(FeatureBranchAction, expectedBranch, pinnedTag, target, isTag)`. Guard is called at the start of `WorkspaceBranchOperations.CheckoutAsync` (after the `origin/` strip), `CreateBranchAsync` and `ReturnToDefaultAsync`, returning `BranchHttpOutcome.BadRequest(message)` with zero Agent calls on refusal; `WorkspaceBranchHandler.CheckoutBranchForWorkspaceAsync` refuses the whole bulk call in a Feature with the create-branch message. A successful tag checkout in a Feature moves a pinned repo's `WorkspaceFeatureRepository.PinnedTag` to the new tag (never pins an unpinned repo). `SwitchBranchModal` (Feature only): New Branch tab hidden (`newbranch` initial tab falls back to `local`), checkout disabled except the Feature branch (tags only when pinned) with the policy message as tooltip, delete confirmation gets the shared-branch notice, new `PinnedTag` parameter fed by the page. Pre-checked: Feature create/repair use `CreateGitWorktree`, not these three methods. App.Tests 737/737 (+ new `FeatureBranchGuardTests` 10, policy table tests, bulk-refusal handler test). Build 0 warnings. A flaky `FeatureNameValidationTests.Case_only_duplicate_name_is_refused` (WorkspaceBusy) failed once in a full run and passed on rerun alone and in the next full run. |
| 28f | I4 | Show drift in the grid | I | I2, C2 | S | DONE | Claude Sonnet 5.5 (2026-10-03) | Feature rows that are not on their Feature branch (other branch, or detached HEAD with a recorded state) show an "Off Feature branch" warning badge next to the branch; tooltip names where the repository is; click opens Switch Branch with I2's "Return to Feature branch". Pure `FeatureBranchPolicy.GetOffFeatureBranch(featureName, pinnedTag, currentBranch, hasRecordedState)`; blank branch before first sync is unknown and not flagged; Workspace (null name) and tag-pinned repos never flagged. The real pinned tag now comes from the Feature repository row (`WorkspaceRepositoryLink.FeaturePinnedTag`, NotMapped, filled by the existing Feature join in `WorkspaceRepositoryLinkListQueryService`), so a terminal checkout of a tag in an unpinned repo is flagged and still gets the Return button; the Switch Branch dialog's pinned tag uses the same field. No new Agent calls. C2 reconciler already compares path only and already has `Ready_repo_registered_at_path_but_on_another_branch_stays_Ready`, so no reconciler change. App.Tests 785/785; Build 0 warnings. |
| 29 | H1 | Structured logging for Feature operations | H | C4, D2 | S | TODO | | |
| 30 | D4 | Safe remote branch deletion + local/remote cleanup checkboxes | D | D2, F2, DEC-2 | M | DONE | Grok (2026-10-02) | DEC-2 = Yes. Static "Remote branches are kept." removed. Local/remote checkboxes default on when applicable; remote hidden with never-pushed note. Lease delete (fetch + force-with-lease + bearerToken); report remote outcomes. |
| 30a | D6 | Remove Feature dialog UX polish | D | D3, D5, I1 | S | DONE | Grok (2026-10-02) | Repo status summary, list height cap, neutral finished copy, green/red Remove CTA, "Checking feature status..." / "Checked x of y", repository wording. Post-create sync NOTE left documentation-only. |
| 30b | E5 | Create Feature: structured branch-already-exists dialog | E | E1 | S | DONE | Grok (2026-10-02) | BranchExists returns structured collisions + short Error; CreateFeatureModal shows X of Y summary, scrollable list, guidance. |
| 30c | E6 | `core.longpaths` for Windows Feature worktrees | E | - | S | DONE | Claude Sonnet 5.5 (2026-10-04) | Owner pulled this out of DEC-8's v1.1 list. New `GitService.EnsureLongPathsAsync`: on Windows only, if `git config --local --get core.longpaths` is not `true`, runs `git config --local core.longpaths true` (repository config, shared by every linked worktree; not global, not per-worktree, so no `extensions.worktreeConfig`). Written at most once; never throws (a failure is a Worker Warning and Create/Remove carry on). Called at the start of `CreateWorktreeAsync` (just before `git worktree add`) and `RemoveWorktreeAsync` (just before `git worktree remove`, which also heals Features made by an older build). The `--local` check is deliberate: a global `true` does not skip the write, so behaviour is the same on every machine. Other OSes are untouched. No App, protocol or Desktop change. New `GitServiceLongPathsTests` (4, real git): create sets the local value (Windows) or leaves it unset (other OS); an existing local `true` is not duplicated; remove sets it for a Feature created without it; Windows-only: a worktree with a file path over 260 characters (fixture committed with `hash-object` + `update-index --cacheinfo` so building it needs no long-path support) is created, the file exists, and `RemoveGitWorktree` with residue cleanup removes it. |
| 31 | GATE-4 | Names, header, security, hooks, Desktop | gate | B5, B6, B7, E1, E2, E3, E4, E5, E6, F3, F2, G1, I3, I4, D6 | - | WAITING | | |
| 32 | R1 | Docs: user guide, troubleshooting, changelog, mark old designs superseded | R | GATE-4 | M | TODO | | |
| 33 | R2 | Features kill switch | R | DEC-6 | S | DROPPED | Owner (2026-10-04) | DEC-6 = No. Features are part of GrayMoon, so there is no feature flag. Do not implement. Skip when walking dependencies and in R4. |
| 34 | R3 | GitVersion parity check | R | GATE-1 | S | TODO | | |
| 35 | R4 | Release regression suite and checklist | R | all above | M | TODO | | |
| 36 | GATE-5 | Release candidate sign-off | gate | R4 | - | WAITING | | |

**What can run in parallel after GATE-1:** lanes A, B, C, D, E, F, I each have one agent (lane G is finished with G1; G2 to G4 moved to `12-hook-cleanup-plan.md`). Lane H waits for C4 and D2. In lane I, I2 can start right after GATE-1 and I3 follows it; I1 waits for A3 and D3, and I4 waits for C2. C4 waits for D2 (row states during Remove). E2, E3 and F3 have no dependencies and can be done at any time, even before GATE-1. **D6** (Remove dialog UX) can start after D3/D5/I1 (GATE-2 deps); coordinate with **D4** so the static remote-kept line is removed once. **E5** (branch-exists dialog) can follow E1 or run beside it if `CreateFeatureModal` touch conflicts are managed.

---

# PART D - Units

Template used by every unit:
- **Goal** - one sentence.
- **Ref** - finding codes in the review docs.
- **Read first** - files to read before editing.
- **Touches** - files you are expected to edit or create.
- **Steps** - what to do.
- **Tests to add** - write first, must fail before the fix.
- **Done when** - every item must be true.
- **Self-check** - verify before marking DONE.
- **Out of scope** - do not do these.

Paths are relative to the folder containing `GrayMoon` and `GrayMoon.Desktop`. `App` = `GrayMoon/src/GrayMoon.App`, `Agent` = `GrayMoon/src/GrayMoon.Agent`, `Common` = `GrayMoon/src/GrayMoon.Common`, `FeatureOps` = `App/Services/Features/WorkspaceFeatureOperations.cs`.

---

## Lane 0 - Foundation

### U0-1 Baseline and housekeeping

- **Goal:** Start from a clean, warning-free, non-flaky baseline.
- **Ref:** 04 P1-10, 05 R7, T1, T2.
- **Read first:** `App/Services/Git/WorkspaceGitService.Context.cs` (the CS8619 warning near line 38), `GrayMoon/.gitignore`.
- **Touches:** `WorkspaceGitService.Context.cs`, `GrayMoon/.gitignore`, delete `GrayMoon/test-agent.txt`, `GrayMoon/test-app.txt`, `GrayMoon/test-common.txt`; the flaky pipe test in `Agent.Tests` (find it with `rg -n "Pipe" GrayMoon/src/GrayMoon.Agent.Tests`).
- **Steps:**
  1. Run the build and all three test projects. Record the counts in Notes.
  2. Fix CS8619 by making the dictionary type match (`IReadOnlyDictionary<int, string?>`), not by suppressing it.
  3. Delete the three `test-*.txt` files and add `test-*.txt` to `.gitignore`.
  4. Make the flaky pipe test deterministic: replace fixed sleeps or short timeouts with waiting on the actual condition (with a generous upper timeout such as 30 s).
- **Tests to add:** none.
- **Done when:** build has 0 warnings; all tests pass three runs in a row for Agent.Tests; the files are gone.
- **Regression guard:** do not weaken any test assertion to make it pass; fix the timing, not the expectation. The CS8619 fix must not change runtime behaviour (type annotation only).
- **Self-check:** `git -C GrayMoon status` shows only the intended changes.
- **Out of scope:** turning on `TreatWarningsAsErrors` (owner decides later).

### U0-2 Safe migration runner

- **Goal:** A failed migration is logged, stops startup with a clear message, and never leaves a half-applied schema; the database is backed up first.
- **Ref:** F-10, 05 R1, A3.
- **Read first:** `App/Migrations.cs`, `App/Migrations.Features.cs`, `App/Program.cs` around `Migrations.RunAllAsync`, `GrayMoon/src/GrayMoon.App.Tests/MigrationsTests.cs`, `WorkspaceFeatureContextMigrationTests.cs`.
- **Touches:** `Migrations.cs`, `Migrations.Features.cs`, `Program.cs` (startup call only), new tests.
- **Steps:**
  1. Introduce a schema version using SQLite `PRAGMA user_version`. Version 0 = before this change. Each migration step gets a number; a step runs only when `user_version` is below its number and sets it after success.
  2. Before running any pending step, write a backup with SQLite `VACUUM INTO '<folder>\graymoon.db.bak-<yyyyMMdd-HHmmss>'` from the open connection (a plain file copy of an open WAL database can be corrupt). Keep the 3 newest backups, delete older ones.
  3. **All migration code that exists today becomes step 1, "legacy baseline", and runs in tolerant mode.** Every statement in it is made check-before-change (query `pragma_table_info` / `sqlite_master` instead of catching "already exists"). An unexpected error in step 1 is logged at Error with the statement, and startup **continues** as it does today. Real user databases have months of silently half-applied history, and refusing to start on them would break everything.
  4. **New steps (step 2 and later: B2, E1 and any future one) run in strict mode:** one transaction each (`BeginTransactionAsync`). On exception: roll back, log at Error with the step number and exception, and throw a dedicated `DatabaseMigrationException` whose message tells the user the backup path.
  5. Replace every empty `catch { }` in `Migrations*.cs` with the explicit checks from step 3 plus logging.
  6. In `Program.cs`, catch `DatabaseMigrationException` at startup, log it, and exit with a non-zero code. Desktop shows its existing startup error for a failed App start.
  7. Remove the N+1 `AnyAsync` loop in the Feature backfill (`BackfillSpecialWorkspaceContextsAsync` or similar): load existing ids once into a `HashSet`.
  8. Update the comment in `Migrations.cs` that says "Pre-release ... no shipped version". 0.1.0 has shipped.
- **Tests to add (App.Tests):**
  - A strict step that throws leaves `user_version` and the schema unchanged and throws `DatabaseMigrationException`.
  - The legacy baseline on a database where its columns already exist completes without error (tolerant mode).
  - Running migrations twice is a no-op the second time.
  - A backup is created before a pending step and not created when nothing is pending; the backup passes `PRAGMA integrity_check`.
- **Regression guard (R-U02a, R-U02b, X-5):** fresh database, the 0.1.0 fixture (after U0-3) and an existing current-schema database all start without error. The legacy baseline never stops startup.
- **Done when:** no empty catch remains in `Migrations*.cs` (`rg -n "catch\s*\{\s*\}" GrayMoon/src/GrayMoon.App/Migrations*.cs` returns nothing); new tests pass; all App.Tests pass.
- **Self-check:** start the app once on a copy of a database (owner verifies in GATE-1).
- **Out of scope:** moving to EF Core migrations.

### U0-3 Golden 0.1.0 upgrade test

- **Goal:** Prove that a real 0.1.0 database upgrades to the current schema.
- **Ref:** 05 R2.
- **Read first:** `WorkspaceFeatureContextMigrationTests.cs` (it builds the current schema first, which is the gap).
- **Touches:** new fixture `GrayMoon/src/GrayMoon.App.Tests/Fixtures/graymoon-0.1.0.db`, the test project file (copy fixture to output), new test class `UpgradeFrom010Tests.cs`.
- **Steps:**
  1. Produce the fixture from the shipped tag `0.1.0`. Create a temporary checkout outside the repo (`git -C GrayMoon worktree add $env:TEMP\gm-010 0.1.0`), run a tiny console or test there that calls `EnsureCreated` on a new SQLite file and inserts a minimal realistic data set: 1 connector (dummy token), 1 workspace, 3 repositories linked, 2 projects with 1 dependency. Copy the file into `Fixtures`, then `git -C GrayMoon worktree remove $env:TEMP\gm-010`.
  2. If you cannot build the 0.1.0 tag, stop and ask the owner for a copy of a 0.1.0 database instead.
  3. Test: copy the fixture to a temp file, run `Migrations.RunAllAsync`, then query every `DbSet` on `AppDbContext` (one `Take(1).ToListAsync()` each) and assert the seeded rows survived and the special Workspace context exists.
- **Done when:** the test passes; the fixture is small (under 1 MB).
- **Regression guard:** test-only unit; no production code changes. The temporary 0.1.0 checkout lives under `$env:TEMP` and is removed afterwards.
- **Out of scope:** testing older versions than 0.1.0.

### GATE-1 Upgrade your real database (owner)

Print this for the owner and stop:

```
GATE-1 - Database upgrade
1. Close GrayMoon (Desktop and any running App).
2. Copy %LOCALAPPDATA%\GrayMoon\Data\graymoon.db (and -wal, -shm if present) somewhere safe.
3. Start GrayMoon built from this branch.
Expected:
 a. The app starts normally. Workspaces, repositories and Features you had are still there.
 b. A file graymoon.db.bak-<timestamp> exists next to graymoon.db.
 c. The log has no migration errors.
4. Restart GrayMoon. Expected: no new backup is created (nothing pending).
5. Check the log once: it shows the legacy baseline step completed and the schema version. (Strict-step failure is covered by automated tests; you will see a real one at GATE-2 when B2 runs.)
6. Run WORKSPACE-SMOKE (end of Part D).
Reply with PASSED, or with what you saw.
```

---

## Lane A - The Agent is the source of truth for disk facts

### A1 Agent `InspectWorktree` command

- **Goal:** One Agent command that reports everything removal needs to know about a worktree, checked live on the developer's machine.
- **Ref:** F-1, F-9, 04 P0-1.
- **Read first:** `GrayMoon/.claude/skills/add-agent-command/SKILL.md`; `Agent/Services/GitService.cs` methods `ListWorktreesAsync`, `RemoveWorktreeAsync`; `Common/Git/GitWorktreePorcelainParser.cs`; `GrayMoon/src/GrayMoon.Agent.Tests/GitWorktreeCommandTests.cs` (pattern for real-git tests).
- **Touches:** new request/response DTOs in `Agent/Jobs/Requests/` and `Agent/Jobs/Response/`, new `Agent/Commands/InspectWorktreeCommand.cs`, a new `GitService.InspectWorktreeAsync`, registrations per the skill, constant in `GrayMoon/src/GrayMoon.Abstractions/Agent/AgentHubMethods.cs`, `GitWorktreePorcelainParser` (add `locked` and `prunable` parsing), tests.
- **Steps:**
  1. Request: `mainRepositoryPath`, `worktreePath`, `defaultBranch` (for example `main`), optional `expectedBranch`.
  2. Response fields: `isRegistered` (in `git worktree list --porcelain`), `exists` (folder exists), `isLocked`, `lockReason`, `headSha`, `branch` (null when detached), `isDirty` (any change from `git status --porcelain=v1`), `stagedCount`, `unstagedCount`, `untrackedCount`, `conflictCount`, `hasUpstream`, `aheadOfUpstream`, `behindUpstream` (null when no upstream), `aheadOfDefault` (commits on HEAD not on `origin/<defaultBranch>`, null when that ref is missing), `error` (null on success).
  3. When the folder does not exist, return `exists = false` and skip status probes (no exception).
  4. Register it as a read-only command (`ReadOnlyCommands` in `Agent/Hosted/SignalRConnectionHostedService.cs`).
- **Tests to add (Agent.Tests, real git, follow `GitWorktreeCommandTests`):**
  - Clean worktree: registered, exists, not dirty, counts 0.
  - Untracked file, staged file, unstaged change: each count is right and `isDirty` is true.
  - Missing folder: `exists = false`, no exception.
  - Locked worktree (`git worktree lock --reason x`): `isLocked = true`, `lockReason = "x"`.
  - Two local commits with no upstream: `hasUpstream = false`, `aheadOfDefault = 2`.
  - Parser tests in Common.Tests for `locked` and `prunable` lines.
- **Done when:** all tests pass; the command is reachable by name from the App (`IAgentBridge.SendCommandAsync(AgentHubMethods.InspectWorktree, ...)`).
- **Regression guard (R-A1, X-1):** parser changes are additive; every existing `GitWorktreePorcelainParserTests` and `GitWorktreeOccupancyTests` test passes unchanged; add a parser test with porcelain output from a repo that has no linked worktrees.
- **Out of scope:** using it in the App (A2).

### A2 Remove analysis uses the Agent, not App disk

- **Goal:** Remove Feature and external-worktree cleanup decide "missing / dirty / safe" from `InspectWorktree`, so they tell the truth when the App runs in Docker.
- **Ref:** F-1, U-15, 04 P0-1.
- **Read first:** `FeatureOps` methods `AnalyzeRemoveFeatureAsync`, `ProbeFeatureWorktreeLiveStatusAsync`, `GrokemoveWarning`, `IsAutomaticallySafe`, `Classify`; `App/Services/Features/WorkspaceExternalWorktreeOperations.cs` (the plan builder with `Directory.Exists` and hard-coded `dirty = false`); `GrayMoon/src/GrayMoon.App.Tests/RemoveFeatureWorkspaceRefreshTests.cs` (how the fake Agent bridge is set up).
- **Touches:** `FeatureOps`, `WorkspaceExternalWorktreeOperations.cs`, an App-side response record for `InspectWorktree`, tests, a new architecture test.
- **Steps:**
  1. Replace every `Directory.Exists(...)` on worktree paths in both files with the `InspectWorktree` result. Call it per repo in parallel (same bounded fan-out style already used in the file).
  2. If the Agent is not connected or `InspectWorktree` returns an error for a repo, the repo's state is **Unknown**, not Missing. Unknown makes the whole plan not automatically safe, and the "Discard uncommitted changes" option must **not** be offered for Unknown repos; the dialog shows "Could not check this repository. Make sure the Worker is running, then try again." Remove is disabled while any repo is Unknown.
  3. In external-worktree cleanup, use the real `isDirty` instead of `false`.
  4. Add an architecture test (App.Tests) that scans the source files under `App/Services/Features/` and fails if they contain `Directory.`, `File.` or `Path.Exists(` from `System.IO`. Allow-list `WorkspaceContextPathResolver.cs` only for pure string path building (no disk access).
- **Tests to add (App.Tests):**
  - **Docker topology:** worktree paths do **not** exist on the test machine (use a path under a non-existent drive folder such as `Z:/nope/...`); fake Agent says exists and dirty. Expected: plan not safe, repo shown as dirty, not "missing".
  - Fake Agent unreachable: repo Unknown, Remove not allowed, discard not offered.
  - Fake Agent says missing: repo shown as missing (existing behaviour kept).
  - External worktree dirty per Agent: plan reports dirty.
  - Update existing `RemoveFeatureWorkspaceRefreshTests` so they no longer depend on directories created on the App host.
- **Done when:** all tests pass; `rg -n "Directory\.Exists|File\.Exists" GrayMoon/src/GrayMoon.App/Services/Features` returns nothing.
- **Regression guard (R-A2a, R-A2b, X-1):** external-worktree cleanup is reached from **Switch Branch in the Workspace**. When `InspectWorktree` is unavailable (old Worker returns `Unknown command`, or Worker offline), only the force/discard path is blocked; non-forced cleanup still runs as today (Git refuses to remove a dirty worktree without `--force`). Test: old Worker -> non-forced external cleanup still works, message says "Update the Worker". The architecture test bans only `Directory.*`, `File.*` and `Path.Exists`; pure string helpers (`Path.Combine`, `Path.GetFileName`) are allowed.
- **Out of scope:** changing what Remove deletes (lane D).

### A3 Live outgoing commits and PR facts in remove analysis

- **Goal:** "Safe to remove" is based on live commit counts and fresh PR state, and "unknown" is never treated as zero.
- **Ref:** F-9, U-18, 04 P1-2.
- **Read first:** `FeatureOps` `AnalyzeRemoveFeatureAsync` (the part reading `state?.OutgoingCommits`, `pr?.MergedAt`), `IsAutomaticallySafe`; `App/Components/Features/RemoveFeatureModal.razor` (headline texts).
- **Touches:** `FeatureOps`, `RemoveFeatureModal.razor`, tests.
- **Steps:**
  1. Use `aheadOfUpstream`, `hasUpstream` and `aheadOfDefault` from `InspectWorktree`.
  2. Before classifying, refresh PR state once for the Feature's repos through the existing PR refresh service (find it with `rg -n "Refresh.*PullRequest" GrayMoon/src/GrayMoon.App/Services`). If refresh fails (offline, rate limit), PR state is Unknown: not automatically safe, the dialog says "Could not check pull requests", and Remove **requires an acknowledgement checkbox but is not disabled**. Only Unknown **disk** state (A2) disables Remove.
  3. `IsAutomaticallySafe` treats null counts as not safe.
  4. Rule for "Completed": every repo either has no commits beyond default (`aheadOfDefault == 0`) or its PR is merged. A repo with pushed commits but no PR is **not** completed; headline: "Some repositories have commits that are not in a pull request yet."
- **Tests to add:** pushed commits, no PR -> not safe; null ahead -> not safe; merged PR -> safe; PR refresh fails -> not safe.
- **Done when:** tests pass; the headline text no longer says "or never opened" for repos that have commits.
- **Regression guard (R-A3):** one PR refresh per analysis, limited to the Feature's repos, so the shared GitHub rate limit used by Workspace PR badges is not drained.

---

## Lane B - Data integrity across contexts

### B1 Remove deletes context-scoped project data

- **Goal:** Removing a Feature deletes its projects, project dependencies and file line statuses in the same transaction.
- **Ref:** F-2, U-13, 04 P0-2 (part 1).
- **Read first:** `FeatureOps` `RemoveFeatureCoreAsync`; `App/Data/AppDbContext.cs` (look for `WorkspaceProject`, `ProjectDependency`, `WorkspaceFileLineStatus` configuration); `feature-context-scoping.mdc`.
- **Touches:** `FeatureOps` (`RemoveFeatureCoreAsync` only), tests.
- **Steps:** inside the existing remove transaction, before deleting the context row, delete with `ExecuteDeleteAsync`: `ProjectDependencies` whose project belongs to the context, then `WorkspaceProjects` with that `WorkspaceFeatureContextId`, then `WorkspaceFileLineStatuses` with that context id. Never touch rows with a null context id or another context id.
- **Tests to add:** seed Workspace context and two Feature contexts each with projects, a dependency and line statuses; remove one Feature; assert its rows are gone and the other two contexts' rows are untouched. Run the test on a database **without** the FK (simulate an upgraded DB by creating the tables with raw SQL, or reuse the 0.1.0 fixture from U0-3 after migration).
- **Done when:** test passes; all App.Tests pass.
- **Regression guard (R-B1):** run the deletes only when the context's `Kind` is Feature (1); return early for the special Workspace context. Never delete rows whose context id is null. The test asserts every Workspace row is untouched, row by row.

### B2 Orphan cleanup and foreign keys on upgraded databases (Migration)

- **Goal:** Upgraded databases get the same foreign keys as fresh ones, and existing orphan rows are deleted.
- **Ref:** F-2, 04 P0-2 (part 2).
- **Read first:** `Migrations.Features.cs` (where `WorkspaceFeatureContextId` is added with `ALTER TABLE`), `AppDbContext.cs` FK configuration for those columns, the U0-2 step mechanism.
- **Touches:** new migration step in `Migrations.Features.cs` (new method), tests.
- **Steps:**
  1. New strict step: delete orphan `ProjectDependencies` (whose project's context id points to a missing context), then orphan `WorkspaceProjects`, then orphan `WorkspaceFileLineStatuses`. **Orphan = context id is NOT NULL and not present in `WorkspaceFeatureContexts`.** Rows with a null context id are never deleted. Log how many rows were removed per table at Information.
  2. Rebuild `WorkspaceProjects` and `WorkspaceFileLineStatuses` with `FOREIGN KEY (WorkspaceFeatureContextId) REFERENCES WorkspaceFeatureContexts(WorkspaceFeatureContextId) ON DELETE CASCADE`. SQLite cannot add a constraint, so: `PRAGMA foreign_keys=OFF`; create `<Table>_new` with the exact column list and constraints EF produces for a fresh database (generate the reference by calling `EnsureCreated` on an empty database in a test and reading `sqlite_master.sql`); copy rows; drop old; rename; recreate indexes; `PRAGMA foreign_key_check`; `PRAGMA foreign_keys=ON`. The step runs only if the FK is missing (`pragma_foreign_key_list`).
- **Tests to add:**
  - **Schema parity:** fresh database (`EnsureCreated`) and upgraded 0.1.0 fixture (after all migrations) have identical `sqlite_master.sql` for these two tables and identical `pragma_foreign_key_list`.
  - Orphans seeded on the upgraded DB are gone after the step; non-orphans keep their ids.
- **Done when:** tests pass; GATE-2 includes checking the owner's real DB.
- **Regression guard (R-B2a, R-B2b, R-B2c):** copy rows **with their `Id`** (`INSERT INTO <Table>_new (Id, ...) SELECT Id, ... FROM <Table>`), because `ProjectDependencies` and other tables reference project ids. Inside the same transaction, before commit, assert that the non-orphan row count and the sum of ids are equal in old and new tables, and that `PRAGMA foreign_key_check` returns nothing; otherwise roll back. Test on the 0.1.0 fixture with a Workspace dependency graph: after the step, the Workspace Dependencies data (projects, edges, levels) is identical to before.

### B3 Scope project queries by context

- **Goal:** Restore, the Dependencies page and grid tooltips read only the current context's projects.
- **Ref:** F-2 query list, U-13.
- **Read first:** `feature-context-scoping.mdc` (all of it); `App/Repositories/WorkspaceProjectRepository.cs` (`GetByWorkspaceIdAsync`), `WorkspaceProjectRepository.DependencyGraph.cs`, `WorkspaceProjectRepository.DependencyLines.cs` (`GetDependencyEdgesAsync`, `GetPackageDependencyLinesByRepoAsync`, `GetPackageDependencyLinesForRepoAsync`, `GetMismatchedDependencyLinesForRepoAsync`); callers in `WorkspaceGitService.Restore.cs`, `WorkspaceDependencies.razor`, `WorkspaceRepositories.Loading.cs`. Locate files with `rg -l "class WorkspaceProjectRepository"`.
- **Touches:** those repository files and their callers.
- **Steps:**
  0. **Find out how Workspace rows are stored before changing any filter.** Read the backfill in `Migrations.Features.cs` and the projection seeding in `FeatureOps` `SeedInitialFeatureProjectionsAsync`, and ask the owner to run (or allow you to run, on a **copy** of the database) `SELECT WorkspaceFeatureContextId IS NULL AS IsNull, COUNT(*) FROM WorkspaceProjects GROUP BY 1`. Write the answer in Notes. Expect Workspace rows to have the special context id, null, or a mix.
  1. Add a `WorkspaceFeatureContextId? contextId` parameter to each listed method, following rule 1 of `feature-context-scoping.mdc`. For the special Workspace (contextId null or the special context id), the filter is `WorkspaceFeatureContextId IS NULL OR WorkspaceFeatureContextId = <specialContextId>`, so no Workspace row is hidden whichever form it has. For a Feature, the filter is `WorkspaceFeatureContextId = <featureContextId>`.
  2. Update every call site (rule 5): grep each method name and pass the context id the caller already has (`_selectedContextId` or similar). Restore runs for the context being restored.
  3. `GetMismatchedDependencyLinesForRepoAsync` must compare against the context state's version for a Feature, not the shared link's `GitVersion`.
- **Tests to add:** for each method, seed two contexts with different projects and versions; assert only the requested context's rows come back (pattern: `WorkspaceRepositoryLinkListQueryServiceTests.Feature_context_sort_keyset_and_level_grouping_use_context_state_not_shared_link`).
- **Done when:** tests pass; `rg -n "GetByWorkspaceIdAsync\(" GrayMoon/src/GrayMoon.App` shows every call passing a context.
- **Regression guard (R-B3a, R-B3b):** this unit changes code every Workspace user runs (Restore, Dependencies page, tooltips). Do A4 step 0 for **each** method: a characterization test on a database with **no** Features, recording the exact rows returned today, kept green after the change. The only intended Workspace change is that Restore no longer repeats projects from deleted Features; add a Desktop README bullet for it.

### B4 Close remaining context leaks

- **Goal:** No remaining read uses the shared Workspace row while a Feature is selected.
- **Ref:** F-8, U-11, 04 P1-7.
- **Read first:** `docs/worktree/GrayMoon-Worktree-Features-Dependency-And-Wiring-Gaps-2026-09-21.md` section "remaining"; `GetPushPlanPayloadAsync` (tag exclusion via `CheckedOutTag`); the notification panel code computing `repoIdsThatNeedPush`; `IWorkspaceFileOperations.ListAsync`; generated packages code (`rg -n "GeneratedPackage" GrayMoon/src/GrayMoon.App`); `App/Components/Modals/BranchModal.razor`; `App/Services/Features/WorkspaceBranchOccupancyService.cs`.
- **Touches:** the files above.
- **Steps:**
  1. Push plan tag exclusion: read `CheckedOutTag` from the context state for a Feature.
  2. Notification push list: compute per selected context.
  3. `ListAsync` and generated packages: take the context id and filter.
  4. Bulk Branch modal: before switching, call `WorkspaceBranchOccupancyService` for the target branch; show which repos are skipped because the branch is held by a Feature or another worktree, and skip them instead of failing mid-run.
- **Tests to add:** one context-isolation test per item 1-3 (seed differing Workspace and Feature rows, assert the Feature value is used); a service-level test for item 4 that occupied repos are skipped and reported.
- **Done when:** tests pass; append " Done (B4)." to the Fix cell of the P1-7 row in `04-worktree-v1-release-roadmap.md` (that table has no Notes column).
- **Regression guard (R-B4a, R-B4b):** characterization tests (A4 step 0) for the Workspace push plan (levels, tag-pinned repos excluded) and the Workspace notification push list, before the change. For the special Workspace these read the shared link exactly as today. In the bulk Branch modal, skip a repo only on a positive "held by a Feature / worktree" answer; if occupancy cannot be determined, attempt the switch as today and show "Could not check worktrees".

### B5 Connector refresh and delete never drop a Feature's repository

- **Goal:** A connector refresh or connector delete can no longer delete a `WorkspaceRepositoryLink` that a Feature uses, so no worktree or Feature branch is left on disk without a database row.
- **Ref:** found in the 2026-10-01 follow-up review (not in 02 to 05). `WorkspaceFeatureRepositories.WorkspaceRepositoryId` cascades from `WorkspaceRepositories` (`AppDbContext.Features.cs`, and the `FK_WorkspaceFeatureRepositories_Links` constraint in `Migrations.Features.cs`), so deleting the link silently deletes the Feature's repo row.
- **Read first:** `App/Repositories/RepositoryRepository.cs` `MergeRepositoriesAsync` (PHASE E, `toDeleteIds`); `App/Repositories/ConnectorRepository.cs` `DeleteAsync`; `App/Repositories/WorkspaceRepositoryLinkCleanup.cs`; `WorkspaceRepository.ReplaceRepositoriesAsync` (the existing "Features exist" guard and its message); `App/Components/Pages/Connectors.razor` (how refresh and delete errors are shown).
- **Touches:** `RepositoryRepository.cs` (PHASE E only), `ConnectorRepository.cs` (`DeleteAsync` only), the connector refresh result type if it needs a new list, `Connectors.razor` (message only), tests, Desktop README bullet.
- **Steps:**
  1. **Refresh:** before PHASE E deletes anything, split `toDeleteIds` into repositories that have **no** `WorkspaceFeatureRepositories` row through any of their links (delete exactly as today) and repositories that have one (keep). For a kept repository, delete nothing, log a Warning "Repository {RepositoryId} is no longer returned by the provider but is used by Feature(s) {FeatureNames}; kept until those Features are removed", and add it to a new `KeptForFeatures` list on the merge result.
  2. The connector page shows a one-line notice when `KeptForFeatures` is not empty: "N repositories are no longer returned by the provider but are kept because Features use them. Remove those Features to finish."
  3. **Delete:** `DeleteAsync` refuses with "Remove the Features that use this connector's repositories first." when any repository of the connector has a `WorkspaceFeatureRepositories` row. Nothing is deleted.
  4. Rename detection is unchanged (it updates the repository row in place, so links and Feature rows survive).
- **Tests to add (App.Tests):** refresh drops 2 repositories, one used by a Feature: the unused one is deleted as today, the used one and its link and Feature row stay, `KeptForFeatures` lists it; connector delete with a Feature repo is refused and deletes nothing; connector delete without Features deletes as today.
- **Done when:** tests pass; no path in `RepositoryRepository` or `ConnectorRepository` can delete a link that has a Feature row (`rg -n "WorkspaceRepositories\s*\.Where" GrayMoon/src/GrayMoon.App/Repositories` reviewed for every delete).
- **Regression guard (R-B5):** do A4 step 0 first: characterization tests for a refresh and a connector delete on a database with **no** Features, asserting the same rows are deleted and kept as today. The new checks run one extra query and change nothing when no Feature exists.
- **Out of scope:** the connector delete impact summary and type-to-confirm (05 U1, Part F).

### B6 Edit Workspace: no partial save; rename and root change blocked while Features exist

- **Goal:** Editing a Workspace that has Features either saves completely or not at all, and never moves the primary checkout under existing Feature worktrees.
- **Ref:** found in the 2026-10-01 follow-up review. `WorkspaceRepository.UpdateAsync` saves `Name` and `RootPath` with `SaveChangesAsync`, then calls `ReplaceRepositoriesAsync`, which throws "Cannot change Workspace repository membership while Features exist" whenever **any** Feature exists, even when membership did not change. The user sees an error, but the rename is already committed. The primary checkout path comes from the Workspace name and root (`WorkspaceService.GetWorkspacePath`), and every Feature worktree's Git link points into that checkout's `.git\worktrees`. 01 section 5 item 4 (unused `EnsureNoFeaturesBeforeMembershipChangeAsync`).
- **Read first:** `WorkspaceRepository.UpdateAsync`, `ReplaceRepositoriesAsync`, `AddRepositoriesAsync` (the guard near "Cannot add Workspace repositories"); `FeatureOps` `EnsureNoFeaturesBeforeMembershipChangeAsync`; `App/Components/Pages/Workspaces.razor` (edit modal save handler and error display).
- **Touches:** `WorkspaceRepository.cs` (`UpdateAsync`, `ReplaceRepositoriesAsync` only), `Workspaces.razor` (message and disabled fields only), `FeatureOps` (delete the unused `EnsureNoFeaturesBeforeMembershipChangeAsync`), tests, Desktop README bullet.
- **Steps:**
  1. In `UpdateAsync`, before any write: if the Workspace has Features and the name or root path changes, throw "Rename and root changes are not possible while Features exist. Remove Features first."
  2. `ReplaceRepositoriesAsync` throws the membership message only when the requested repository set differs from the current set; an unchanged set is a no-op.
  3. Run the name/root save and the membership change in one transaction, so a membership failure rolls back the rename.
  4. Edit modal: when the Workspace has Features, show the name and root fields read-only with the reason as a hint.
  5. Delete `EnsureNoFeaturesBeforeMembershipChangeAsync` (never called; the real guard lives in `WorkspaceRepository`).
- **Tests to add:** Workspace with a Feature: save with unchanged name, root and members succeeds; rename is refused and nothing is saved; membership change is refused and the name is unchanged. Workspace without Features: rename plus membership change works as today.
- **Done when:** tests pass.
- **Regression guard (R-B6):** A4 step 0 first: characterization tests for Edit Workspace on a Workspace **without** Features (rename, root change, add, remove repos), kept green. Only Workspaces with Features change behaviour, and only from "partial save plus error" to "clean refusal" or "clean success".
- **Out of scope:** moving the primary checkout and running `git worktree repair` (v1.1).

---

### B7 Workspace delete refused while Features exist

- **Goal:** Deleting a Workspace that still has Features is refused, so no Feature is orphaned with worktrees and branches left on disk.
- **Ref:** Part of the old G3 (see `12-hook-cleanup-plan.md`); `08` R-G3c. Matches the guards B6 and `ReplaceRepositoriesAsync` already apply.
- **Touches:** `App/Repositories/WorkspaceRepository.cs` `DeleteAsync`, `App/Components/Pages/Workspaces.razor` (`ShowDeleteModal`, `DeleteWorkspaceAsync`, delete dialog), `App.Tests/WorkspaceRepositoryReplaceTests.cs`, Desktop README bullet.
- **Steps:** guard in `DeleteAsync` before any delete (any `WorkspaceFeatures` row for the Workspace, any lifecycle state); the page checks `IWorkspaceFeatureOperations.ListFeaturesAsync` when the dialog opens, shows the message and disables Remove; the page also handles the exception for a Feature created after the dialog opened.
- **Tests to add:** Workspace without Features deletes as today (write first, keep green); Workspace with a Feature is refused with the exact message and nothing is deleted; Features of another Workspace do not block; delete succeeds once the Feature is gone.
- **Done when:** tests pass and the Remove Workspace dialog shows the reason.
- **Regression guard (R-G3c):** Workspaces without Features delete exactly as before. The only behaviour change is the refusal. No unhooking is part of this unit (that is `12-hook-cleanup-plan.md`).

## Lane C - Lifecycle and recovery

### C1 Create Feature intent in one transaction

- **Goal:** The Feature, its context and its per-repo Pending rows are saved together or not at all.
- **Ref:** F-3, 04 P0-3 (part 1).
- **Read first:** `FeatureOps` `CreateFeatureCoreAsync` (three separate `SaveChanges` and the early return with `HeadCommitsIncomplete`).
- **Touches:** `FeatureOps` (`CreateFeatureCoreAsync` only), tests.
- **Steps:**
  1. Gather head commits and collisions **before** writing anything. If incomplete, return the error without writing rows.
  2. Write Feature (`Creating`), context and all `Pending` repo rows in one transaction.
  3. The worktree creation that follows stays as it is (it updates rows per repo).
- **Tests to add:** head commits incomplete -> no Feature row exists; exception thrown while saving repo rows -> no Feature or context row exists.
- **Done when:** tests pass.
- **Regression guard:** only `CreateFeatureCoreAsync` changes; the Workspace has no create path here. Existing `CreateFeatureParentBranchTests` stay green unchanged.

### C2 Reconcile stuck Features at startup and on Worker reconnect

- **Goal:** A Feature left in `Creating` or `Removing` by a crash or restart becomes `NeedsRepair` with a reason, and database rows are checked against `git worktree list`.
- **Ref:** F-3, U-2, 04 P0-3 (part 2), design `docs/worktree/...Detailed-Implementation-Design-v3.md` section 28.
- **Read first:** `App/Hubs/AgentHub.cs` `OnConnectedAsync` and the connection tracker it calls; `App/Program.cs` hosted services list; `WorkspaceOperationRunner` (to know if an operation is running); `Models/Features/WorkspaceFeatureLifecycleState.cs`, `WorkspaceFeatureRepositoryState.cs`.
- **Touches:** new `App/Services/Features/WorkspaceFeatureReconciler.cs` (+ interface), registration in `Program.cs`, a hook from the Agent-connected event, tests.
- **Steps:**
  1. `ReconcileAsync(CancellationToken)`: for each Feature in `Creating` or `Removing` that has no running structural operation in `WorkspaceOperationRunner`, set `NeedsRepair` and `LastError = "Interrupted while creating"` or `"Interrupted while removing"`.
  2. For each Feature, call `ListGitWorktrees` once per main repository; a repo row in `Ready` whose worktree is not registered **and** whose folder `InspectWorktree` (A1) reports as missing becomes `NeedsRepair` with `LastError = "Worktree is missing"`. If either call fails or the Worker lacks `InspectWorktree`, change nothing for that repo. A repo row in `Pending` or `NeedsRepair` whose worktree **is** registered at the expected path becomes `Ready`. Compare the path only, never the branch: a repo moved off its Feature branch is drift (shown by I4), not breakage. Rows in `Removing` or `Removed` (D2) are skipped: a missing worktree there is expected.
  2b. **Untracked worktrees.** From the same `ListGitWorktrees` results, a registered worktree whose path is under the Workspace's `ManagedFeatureStorageRoot` but matches no `WorkspaceFeatureRepositories` row is untracked (older connector refreshes could delete the row, see B5). If its path is under an existing Feature's root folder, set that Feature to `NeedsRepair` with `LastError = "Worktree <path> has no record in GrayMoon"`; otherwise log one Warning with the path. Never delete or change the worktree.
  3. Run it once at startup when the Worker first connects, and again on every Worker reconnect. Never run two reconciles at once (use a `SemaphoreSlim(1)`); skip workspaces that have a structural operation running.
  4. Use `IDbContextFactory`.
- **Tests to add:** stuck `Creating` becomes `NeedsRepair`; a running operation is left alone; Ready repo with unregistered worktree becomes NeedsRepair; `Removed` row with unregistered worktree stays `Removed`; untracked worktree under a Feature root sets that Feature to NeedsRepair and is not touched; untracked worktree elsewhere under the storage root only logs; Ready repo registered at its path but on another branch stays Ready; Pending repo with registered worktree becomes Ready; Worker not connected -> no changes, no exception; `ListGitWorktrees` fails -> no changes; two reconnects within 60 s -> one reconcile.
- **Done when:** tests pass.
- **Regression guard (R-C2a, R-C2b, R-C2c):** untracked-worktree paths are compared after normalizing slashes and trailing separators, case-insensitively on Windows. The reconciler only reads and writes Feature rows (context `Kind` 1) and never deletes anything. It takes no structural lock, runs at most 4 Worker calls at a time, skips workspaces with a running operation, and runs at most once per 60 s, so Workspace Sync at startup is not delayed. A test asserts the special Workspace context's rows are byte-identical after a reconcile. C2 depends on A1 for `InspectWorktree`.

### C3 Selector shows every state with friendly labels

- **Goal:** Every Feature is visible in the selector; broken ones can be opened.
- **Ref:** U-2, U-8, U-9, 04 P0-3 (part 3).
- **Read first:** `App/Components/Features/WorkspaceFeatureSelector.razor` (`CanSelect`, the state filter that lists only Ready and NeedsRepair).
- **Touches:** `WorkspaceFeatureSelector.razor` (+ its code-behind if present).
- **Steps:**
  1. List all states. Labels: `Creating` -> "Setting up...", `Removing` -> "Removing...", `NeedsRepair` -> "Needs attention", or "Removal incomplete" when any repo row is `Removing` or `Removed` (D2), `Ready` -> no label.
  2. Tooltip on the label shows the Feature's `LastError` when present.
  3. `NeedsRepair` rows are selectable; `Creating` and `Removing` rows are not selectable but stay visible.
  4. Keep the existing "âˆ’" remove action; hide it for `Creating`/`Removing`.
- **Tests to add:** if the project has no bUnit, add a unit test for the pure label/selectable mapping (extract it into a small static helper).
- **Done when:** test passes; no raw enum names appear in markup (`rg -n "NeedsRepair\)" App/Components/Features` returns nothing).
- **Regression guard (R-C3):** a NeedsRepair Feature opens **read-only**: show the "This Feature needs attention" banner and disable Sync, Push, Update, Prepare and Git Changes actions for that context (the header and toolbar already receive the context; add an `IsReadOnlyContext` flag). Test or manual check: opening a NeedsRepair Feature with one missing worktree does not throw and the browser tab stays usable; switching back to Workspace works.

### C4 Repair (retry) and Roll back service operations

- **Goal:** A user can retry the failed repos of a Feature, roll back everything that was created, or finish a removal that failed or was interrupted. A Feature the user was removing is never re-created.
- **Ref:** F-4, U-1, 04 P0-4. Follow-up review: today a partly failed Remove leaves the already-removed repos' rows `Ready` (only failing rows become `NeedsRepair`), so a create-style retry plus C2 would re-create those worktrees at `BaseCommitSha` after their branch was deleted. D2 adds the `Removing`/`Removed` row states that this unit relies on.
- **Read first:** `FeatureOps` `CreateFeatureCoreAsync` (per-repo create loop and `NeedsRepair` handling), `SeedInitialFeatureProjectionsAsync` (runs once for the whole Feature, only when every repo succeeded, and upserts rows Sync already wrote), `RemoveFeatureCoreAsync` (with D2's row states); Agent `GitService.CreateWorktreeAsync` (idempotent when the path already holds the branch); the facade interface that exposes Create/Remove Feature to the UI (`rg -n "CreateFeatureAsync" GrayMoon/src/GrayMoon.Application GrayMoon/src/GrayMoon.App`).
- **Touches:** `FeatureOps` (new methods), the facade interface and implementation, tests.
- **Steps:**
  0. **Remove-incomplete check.** A Feature is "remove incomplete" when any of its repo rows is `Removing` or `Removed`. `RepairFeatureAsync` and `RollbackFeatureAsync` refuse such a Feature with "This Feature was being removed. Use Continue removal." Never call `CreateGitWorktree` for it.
  1. `RepairFeatureAsync(contextId)`: under the structural lock, for each repo in `Pending` or `NeedsRepair`, call `CreateGitWorktree` with the stored `BaseCommitSha`, `ParentBranchName` and pinned tag. Mark each repo Ready or NeedsRepair with the Agent's message in `LastError`. When every repo is Ready (including when there was nothing to retry because C2 already marked them Ready), run `SeedInitialFeatureProjectionsAsync` once for the whole Feature (it upserts, so it is safe after Sync wrote rows) and then set the Feature Ready. A NeedsRepair Feature has never been seeded, so seeding per repo is wrong.
  2. `RollbackFeatureAsync(contextId)`: same as Remove with `force = false` for worktrees and normal (non-force) branch delete, but only for repos whose worktree is registered; then delete the Feature's rows. If any repo has uncommitted changes (per `InspectWorktree`), refuse and return the list.
  3. **Continue removal:** for a remove-incomplete Feature there is no new service method. The panel (C5) opens the Remove dialog; `AnalyzeRemoveFeatureAsync` and `RemoveFeatureCoreAsync` skip `Removed` rows (D2), so the analysis and the consent checkboxes cover only what is still on disk.
  4. Repair and Rollback return a per-repo result list (repo name, outcome, message).
  5. Expose both on the facade, plus `IsRemoveIncomplete(contextId)` for the UI.
- **Tests to add:** two repos fail then succeed on repair -> Feature Ready and projections seeded; all repos already Ready but Feature NeedsRepair -> repair seeds once and sets Ready with zero Agent calls; one still fails -> NeedsRepair with that repo's message; rollback removes created worktrees and rows; rollback refuses when a repo is dirty; **remove incomplete** (one row `Removed`, one `Removing` with an error) -> repair and rollback refuse and the fake Agent records zero `CreateGitWorktree` calls.
- **Done when:** tests pass.
- **Regression guard (R-C4, R-C4b):** "remove incomplete" comes only from the `Removing`/`Removed` row states that Remove writes, never from `LastError` text; create never writes those states. Roll back deletes only the branch names stored in that Feature's repo rows, with non-force delete, and never the repo's default branch or the branch checked out in the primary checkout (check both before calling the Agent; test both).

### C5 Feature status panel

- **Goal:** For a Feature that needs attention, the user sees each repo's state and error and can Retry, Roll back or Remove.
- **Ref:** U-1, U-8, U-9, 03 section 6 item 2.
- **Read first:** `App/Components/Features/CreateFeatureModal.razor` (how failure is shown today), `WorkspaceRepositoriesHeader.razor` (Feature menu), C4 facade methods.
- **Touches:** new `App/Components/Features/FeatureStatusPanel.razor`, `CreateFeatureModal.razor`, the Feature menu in `WorkspaceRepositoriesHeader.razor`, `GrayMoon.Desktop/README.md` (one bullet).
- **Steps:**
  1. Panel lists repos with state and `LastError`, and buttons Retry, Roll back, Remove. Buttons call the facade and show the per-repo result list afterwards. For a remove-incomplete Feature (C4 `IsRemoveIncomplete`), show only **Continue removal** (opens the Remove dialog) and list `Removed` repos as "already removed". When every repo shares the exact same outcome/message (e.g. all repos fail with "Worker not connected" because the Worker is simply offline), collapse that into one line instead of repeating it once per repo - same for the persisted rows' `LastError`. The dialog uses Bootstrap's `modal-dialog-scrollable` so the repo list scrolls and the dialog itself never grows taller than the viewport. While Retry/Roll back is running, the dialog's own z-index drops below the page's single `BackgroundJobOverlay` (`.loading-overlay--page`, same pattern as `SwitchBranchModal`/`MergePullRequestModal`) - which already shows "Repairing feature..."/"Rolling back feature..." - so that overlay covers the dialog instead of it sticking out above it. The dialog deliberately does **not** also render its own `LoadingOverlay`: that produced two visibly stacked overlays for the same operation instead of one.
  2. When create ends with failures, the Create modal closes and opens the panel instead of saying "One or more worktrees failed to create."
  3. When a `NeedsRepair` Feature is selected: (a) show a status line styled like the repo-row error callout (dark strip with an inset amber accent bar, not a bordered Bootstrap `alert-warning` box) reading "This Feature needs attention. Use Repair to continue." - no button on the line itself; (b) the header's primary Sync button is replaced by a `btn-warning` **Repair** button (visible, not just a disabled Sync) that opens the panel directly.
  4. ~~Feature menu gets "Status and repair".~~ Removed again: the Feature menu never offers Status and repair (and therefore never Roll back) for a healthy (`Ready`) Feature - the dialog is reachable only via the header's `Repair` button, which itself only renders while `IsReadOnlyContext` (`NeedsRepair`), or automatically after a Create failure classified `NeedsRepair`. The Feature menu keeps only "Remove Feature".
  5. The dialog title follows the `.modal-title--compound` convention shared with `BranchModal`/`SwitchBranchModal`/`MergePullRequestModal`: "Feature Status" plus the Feature name in small gray text (`.modal-title__secondary`), not "`{name}` - status and repair".
  6. State labels never use parentheses or a dash: the Feature selector shows "`<name>` Needs attention" with a small CSS margin (`ms-1` on the label `<span>`, not a bare leading space character, which read as "too close" since adjacent inline text/elements don't reliably keep a visible gap) - same for every `FriendlyLabel`/`FeatureTitle` usage in `WorkspaceFeatureSelector.razor`.
  7. All three Feature dialogs (`CreateFeatureModal`, `RemoveFeatureModal`, `FeatureStatusPanel`) support Esc to close/cancel (ignored while busy/analyzing) and Ctrl/Cmd+Enter to run the dialog's primary action, via the existing shared `data-default-action` + `modal-default-action.js` convention (no per-dialog JS).
  8. `FeatureStatusPanel`'s error text uses the shared `.gm-callout`/`.gm-callout--error` convention (dark strip, inset accent bar, no border/border-radius) already used by `CreateFeatureModal`/`RemoveFeatureModal`, not the old bordered Bootstrap `alert alert-danger` box.
  9. A cancelled Repair/Roll back (pressing the overlay's Abort button) shows "Repair was cancelled."/"Roll back was cancelled." instead of the raw, confusing .NET `OperationCanceledException` message ("A task was canceled.").
  10. `RollbackFeatureAsync`'s per-repository dirty check (an Agent round trip per repository, sequential) now runs as the first step **inside** the structural lock instead of before it, so the page's single `BackgroundJobOverlay` ("Rolling back feature...") is already showing by the time that check starts, instead of the button appearing to do nothing for however long the check takes. `RepairFeatureAsync` had no such pre-check and was already instant. No second/duplicate overlay is introduced - this is a pure timing fix inside the existing single-overlay plumbing.
- **Tests to add:** none required beyond C4 (UI). Build must pass.
- **Done when:** builds; owner verifies at GATE-3.
- **Regression guard:** the Repair button and the status line render only for Feature contexts; the Workspace header is unchanged (WORKSPACE-SMOKE at GATE-3).

### GATE-3 Failure and recovery (owner)

```
GATE-3 - Failure and recovery
Setup: a test Workspace with at least 3 repositories.
1. Stuck create: start "New Feature" named gate3-a, and while it runs close GrayMoon (kill the App process).
   Start again. Expected: gate3-a is visible as "Needs attention" with "Interrupted while creating".
2. Repair: open gate3-a > Status and repair > Retry. Expected: all repos Ready, Feature works normally.
3. Partial failure: in one repo, create a folder at the path the Feature will use
   (%USERPROFILE%\.graymoon\<Workspace>\features\gate3-b\<Repo>) and put a file in it.
   Create Feature gate3-b. Expected: the status panel opens and shows that repo with a clear message; other repos Ready.
4. Delete that folder, press Retry. Expected: Feature Ready.
5. Roll back: create gate3-c with the same trick, then press Roll back. Expected: worktrees and branches for gate3-c are gone (git worktree list, git branch), Feature disappears.
6. Restart GrayMoon twice in a row with healthy Features. Expected: no healthy Feature turns into "Needs attention"; the Workspace loads and syncs without waiting.
7. Interrupted remove: create gate3-d, start Remove, and kill the App process after the first repo is reported removed.
   Start again. Expected: gate3-d shows "Removal incomplete"; Status and repair offers only Continue removal; the repo already removed is listed as already removed.
   Press Continue removal and finish. Expected: gate3-d is gone; no worktree for gate3-d was re-created at any point (git worktree list).
8. Run WORKSPACE-SMOKE.
Reply with PASSED, or with what failed (step number and what you saw).
```

---

## Lane D - Honest removal

### D1 Agent removes worktree residue and reports what is left

- **Goal:** After `git worktree remove`, leftover files are deleted with retries, empty parents are removed, and the Agent reports anything it could not delete. Creating into an empty existing folder is allowed.
- **Ref:** F-5, U-16, 04 P0-5.
- **Read first:** Agent `GitService.RemoveWorktreeAsync`, `CreateWorktreeAsync` (the check that rejects an existing directory even when empty); the remove command's response DTO.
- **Touches:** `GitService.cs` (those two methods + a private helper), the remove response DTO, tests.
- **Steps:**
  1. After a successful `worktree remove` (or when the worktree is already unregistered), if the folder still exists and passes **every** safety guard below, delete it with a custom walk (not `Directory.Delete(path, true)`): clear read-only attributes; for any entry with the `ReparsePoint` attribute (junction or symlink), delete the link itself without entering it; on `IOException`/`UnauthorizedAccessException` retry up to 5 times with 200, 400, 800, 1600, 3200 ms waits.
  2. Response gets `residueRemaining` (bool), `residueFileCount`, `residueSampleFiles` (up to 5 relative paths), `residueMessage`.
  3. A new optional request field `featureRootPath`: after removing, delete that folder if it is empty (and only if empty).
  4. `CreateWorktreeAsync`: an existing **empty** folder is allowed; a non-empty folder still returns `PathExists`.
  5. **Safety guards, all required before deleting anything** (request gets a new optional field `featureStorageRoot`, the `<ManagedFeatureStorageRoot>\<Workspace>\features` folder; without it, D1 deletes nothing and only reports residue):
     1. After `Path.GetFullPath` normalization, `worktreePath` is strictly under `featureStorageRoot\<FeatureName>\`, and `featureRootPath` equals `featureStorageRoot\<FeatureName>`.
     2. The path is not equal to, and does not contain, `mainRepositoryPath` (the primary checkout).
     3. The path is not a registered worktree in `git worktree list` at the moment of deletion.
     4. The path does not contain a `.git` **directory** at its top level (a real repository has one; a linked worktree has only a `.git` file).
     5. The path is at least 2 levels below `featureStorageRoot` (`features\<FeatureName>\<Repo>`). `featureStorageRoot` is the Workspace's persisted `ManagedFeatureStorageRoot`, which already ends in `<Workspace>\features`, so this is 4 levels below the user-level storage root (for example `%USERPROFILE%\.graymoon`). Worktrees created under the legacy drive-root path (`C:\.graymoon\...`) fail guard 1 and are only reported, never deleted.
     If any guard fails: delete nothing, set `residueRemaining = true` and `residueMessage` to the guard that failed, and log a Warning.
- **Tests to add (real git, Windows-aware):** leftover untracked build output is removed; a file held open (`FileStream` with `FileShare.None`) produces `residueRemaining = true` with that file listed; empty feature root removed, non-empty kept; create into empty folder succeeds; **one test per safety guard** (path outside root, path equal to the primary checkout, registered worktree, folder with a `.git` directory, too shallow) proving nothing is deleted; **junction test**: a junction inside the worktree pointing to a folder outside it; after cleanup the target folder and its files still exist.
- **Done when:** tests pass.
- **Regression guard (R-D1a, R-D1b, R-D1c, X-1):** this is the only unit that recursively deletes folders, so the guards and their tests are mandatory, not optional. An old App that does not send `featureStorageRoot` gets today's behaviour (no residue deletion). Create into an existing folder is allowed only when the folder is empty and under `featureStorageRoot`.

### D2 App removal report and no swallowed branch failures

- **Goal:** After Remove, the user sees exactly what was removed and what was not, per repo; branch delete failures are reported, not hidden.
- **Ref:** F-5, F-6, U-16, 04 P0-5.
- **Read first:** `FeatureOps` `RemoveFeatureCoreAsync` (local/remote `DeleteBranch` failures only logged); the Git Changes file watcher service for a context (`rg -n "class .*GitChanges.*Monitor|FileSystemWatcher" GrayMoon/src`); `RemoveFeatureModal.razor`.
- **Touches:** `FeatureOps` (`RemoveFeatureCoreAsync`, and the row filter at the start of `AnalyzeRemoveFeatureAsync`), the result type it returns, `Models/Features/WorkspaceFeatureRepositoryState.cs` (two new values), `RemoveFeatureModal.razor`, `GrayMoon.Desktop/README.md` bullet.
- **Steps:**
  0. **Row states during Remove.** Add `Removing = 3` and `Removed = 4` to `WorkspaceFeatureRepositoryState` (stored as int; no schema change). When Remove starts, set every row that is not already `Removed` to `Removing` in the same save that sets the Feature to `Removing`. After a repo's worktree is unregistered, set that row to `Removed` and save it at once with a short-lived context from the factory, so a crash keeps the progress. A failed repo stays `Removing` with its error in `LastError`. `AnalyzeRemoveFeatureAsync` and `RemoveFeatureCoreAsync` skip `Removed` rows. C4 and C3 use these states to recognise "remove incomplete".
  1. Before removing, stop Git Changes monitoring for the context (call the existing stop/unregister method; ask the Agent to stop watching those paths if the watcher lives in the Agent).
  2. Pass `featureRootPath` and `featureStorageRoot` to the Agent remove (D1), computed with `WorkspaceContextPathResolver`.
  3. Collect a per-repo report: worktree removed (yes/no), local branch deleted (yes / kept because it has unmerged commits / failed + message), leftover files (count + sample).
  4. Database rows are deleted only when every worktree is unregistered. Leftover files or kept branches do not block row deletion, but they are listed.
  5. The modal shows the report after Remove: "Removed 6 worktrees, 6 local branches." plus a warning list for anything left, with "Open folder" (Desktop) for leftovers.
- **Tests to add:** branch delete fails -> report says kept with message, Feature still removed; Agent reports residue -> report lists it; one of three repos fails -> the two removed rows are `Removed`, the failed row is `Removing` with its error, Feature is `NeedsRepair`; a second Remove skips the `Removed` rows (zero Agent calls for them); every code path that switches on `WorkspaceFeatureRepositoryState` handles the new values (`rg -n "WorkspaceFeatureRepositoryState\." GrayMoon/src` reviewed).
- **Done when:** tests pass; no `DeleteBranch` failure path only logs.
- **Regression guard (R-D2, R-D2b):** occupancy, path resolution and the selector never treat a `Removed` row as a live worktree (test). Stop Git Changes monitoring **by Feature context id only**; the Workspace's monitoring must keep running. Restart the Feature's monitoring in a `finally` when Remove fails or is cancelled. Tests: Workspace monitoring stays active during a Feature remove; a failed remove restarts the Feature's monitoring.

### D3 Remove dialog: checkboxes that match the situation

- **Goal:** The dialog only asks about risks that exist, and Remove is enabled only when every risk shown is acknowledged.
- **Ref:** U-17, U-19, 04 P1-4.
- **Read first:** `RemoveFeatureModal.razor` (checkboxes and the enable condition).
- **Touches:** `RemoveFeatureModal.razor`.
- **Steps:**
  1. Show "Permanently discard uncommitted changes in N repositories" only when at least one repo is dirty; list those repos.
  2. Show "Delete local branches that have commits not in the default branch (N)" only when at least one repo has `aheadOfDefault > 0` and no merged PR.
  3. Remove is enabled when every **shown** checkbox is ticked, and disabled while any repo is Unknown (A2).
  4. ~~Text under the list: "Local branches are deleted. Remote branches are kept."~~ **Superseded (DEC-2 / D4 / D6, 2026-10-02):** that static line is misleading when the Feature was never pushed (no remotes exist). Replace with the local/remote cleanup checkboxes in D4; D6 covers the rest of the dialog polish.
- **Tests to add:** extract the enable rule to a small pure helper and unit-test the combinations.
- **Done when:** tests pass.
- **Regression guard:** the Remove dialog is Feature-only; external-worktree cleanup in the Workspace Switch Branch modal keeps its current checkboxes and text.

### D5 Locked worktrees in Remove

- **Goal:** A locked worktree (`git worktree lock`) is explained in the Remove dialog instead of failing with a raw Git error, and is unlocked only with consent.
- **Ref:** F-12, 04 P2-3 (lock part only; `core.longpaths` was pulled into v1 as E6). A1 already reports `isLocked` and `lockReason`, but no other unit uses them.
- **Read first:** A1 response DTO; `FeatureOps` `AnalyzeRemoveFeatureAsync`, `RemoveFeatureCoreAsync`; Agent `GitService.RemoveWorktreeAsync`; the D3 enable-rule helper; `RemoveFeatureModal.razor`.
- **Touches:** the plan types (one field), `FeatureOps` (those two methods), the `RemoveGitWorktree` request (new optional `unlock` field) and `GitService.RemoveWorktreeAsync`, the D3 helper, `RemoveFeatureModal.razor`, tests.
- **Steps:**
  1. Plan field `IsLocked` and `LockReason` per repo from `InspectWorktree`.
  2. Dialog: when any repo is locked, show the checkbox "Unlock and remove N locked worktrees" with each repo's reason. The D3 rule treats it like the other shown checkboxes.
  3. Agent: when `unlock = true`, run `git worktree unlock <path>` before `git worktree remove`. Without it, behaviour is unchanged. An old App never sends `unlock`.
  4. Old Worker (no `isLocked`): nothing changes; Git's error is shown in the D2 report as today.
- **Tests to add:** Agent (real git): locked worktree with `unlock = true` is removed; without it, removal fails and the worktree is still locked. App: locked repo makes the checkbox appear and gates Remove; old Worker produces no checkbox.
- **Done when:** tests pass.
- **Regression guard (R-D5):** `unlock` is optional and defaults to false; external-worktree cleanup in the Workspace never sends it.

### D4 Safe remote branch deletion + local/remote cleanup checkboxes (DEC-2 = Yes)

- **Goal:** The Remove dialog lets the user choose to delete local and/or remote Feature branches, with accurate defaults and messaging; remote deletes never delete someone else's work.
- **Ref:** F-6, U-19, 04 P1-3, 05 R3; owner UX 2026-10-02 (DEC-2).
- **Read first:** `GitService.DeleteBranchAsync`, the comment near it saying the remote call needs `bearerToken`; `GitService.FindBranchCollisionsAsync`; how other commands pass `bearerToken` (`rg -n "bearerToken" GrayMoon/src/GrayMoon.App`); `RemoveFeatureOptions.DeleteRemoteBranches` (already on the options type; `RemoveFeatureCoreAsync` already has a gated path); `RemoveFeatureRepositoryPlan.HasUpstream` / `FeatureBranchHasUpstream` (A3/I1); `RemoveFeatureModal.razor` (current static "Local branches are deleted. Remote branches are kept." under the repository list).
- **Touches:** `RemoveFeatureModal.razor`, `FeatureOps` `RemoveFeatureCoreAsync` (wire options from the new checkboxes; keep lease-based remote delete), Agent delete-branch path if still incomplete, tests.
- **Steps:**
  1. **Remove the static line** "Local branches are deleted. Remote branches are kept." (D3 U-19). It is wrong when the Feature was never pushed â€” there are no remote branches to "keep".
  2. **Two checkboxes** (wording to refine in implementation; suggested):
     - "Delete local Feature branches" â€” shown whenever local Feature branches exist to delete; **checked by default**.
     - "Delete remote Feature branches" â€” shown when at least one repository has a remote/upstream for the Feature branch (`FeatureBranchHasUpstream` / live `HasUpstream` for the Feature branch); **checked by default when shown**.
  3. **When no remote/upstream Feature branches exist:** do **not** show the remote checkbox as an enabled "keep remotes" claim. Either hide it, or show a short explanatory line such as "No remote Feature branches to delete (this Feature was never pushed)." â€” never "Remote branches are kept."
  4. **When remotes exist:** default intent is cleanup â€” both local and remote checked. User may untick remote to keep remotes while still removing local worktrees/branches.
  5. Wire `DeleteRemoteBranches` from the remote checkbox into `RemoveFeatureAsync` / `RemoveFeatureCoreAsync`.
  6. **Safe remote delete:** fetch the remote first; delete with a lease (`git push --force-with-lease=refs/heads/<b>:<expectedSha> origin :<b>` where expectedSha is the Feature's local branch tip); pass the connector token; report each result in the D2 report.
  7. D3's discard / force-unmerged / unlock checkboxes stay as today (risk acknowledgements). The new local/remote cleanup checkboxes are separate intent controls; confirm enable-rule interaction in tests (Remove still requires every **risk** checkbox that is shown).
- **Tests to add:** remote tip equals local -> deleted; remote moved -> refused and reported; no-upstream Feature -> remote checkbox hidden/disabled and options.DeleteRemoteBranches false; both checkboxes default true when remotes exist; unticked remote keeps remotes in the report.
- **Done when:** tests pass; static "Remote branches are kept." line gone.
- **Regression guard (R-D4):** never delete the repo's default branch or a branch name not stored in the Feature's repo rows; the branch list / affected repos are visible before confirming; lease refuse path is reported, not silent. (Previous plan said "off by default"; owner 2026-10-02 wants **on by default when remotes exist**.)

### D6 Remove Feature dialog UX polish

- **Goal:** Make the Remove Feature confirmation dialog scannable and honest: summary when every repository is clean, capped list height, neutral safety copy, clear loading progress, and a Remove CTA color that matches risk. Also **document** (plan only) why sync badges go red after Create Feature â€” do **not** change that product behavior in this unit.
- **Ref:** owner UX 2026-10-02 (Remove Feature flow follow-up). Builds on D3/D5/I1.
- **Read first:** `RemoveFeatureModal.razor` (`StatusHeadline`, `CalloutClass`, `DescribeRepository`, repository list markup, footer Remove button); `WorkspaceRepositories.razor.cs` `BeginRemoveFeatureAnalysis` ("Checking Feature removal..."); `FeatureOps` `AnalyzeRemoveFeatureAsync` probe loop (no per-repo progress today); Merge PR primary button styling (`MergePullRequestModal.razor.css` `.merge-pr-btn-github`) as the green reference.
- **Touches:** `RemoveFeatureModal.razor` (+ small CSS if needed), `WorkspaceRepositories.razor.cs` (analysis job label/progress), optionally `AnalyzeRemoveFeatureAsync` progress reporting into the page job; tests for summary helpers / CTA class selection. **Does not** change Create Feature sync, Worker queue UI, or grid sync badges (explanation only â€” see note below).
- **Steps:**

  **1. Repository status summary (collapse the noisy list)**
  - Today every repository gets a line like "Up to date with nothing pending", repeating N times.
  - When **all** repositories share the same clean/nothing-pending status: show one summary, e.g. "All N repositories are up to date with nothing pending." Do **not** enumerate each repository.
  - When states are mixed, or any repository needs detail (dirty, unmerged commits, locked, off Feature branch, missing folder, Unknown, PR state worth listing, warnings): list only the repositories that differ / require attention (or list all if that is clearer for mixed cases â€” prefer listing only non-clean rows when the rest are uniformly clean).
  - Keep risk checkbox sub-lists (dirty / unmerged / locked) as today.

  **1a. Cap repository list height**
  - The scrollable repository list must **not** stretch the dialog to the full available page height.
  - Cap the list viewport at ~**70% of the dialog content area** height; overflow scrolls inside that region. Dialog chrome (header, safety message, checkboxes, footer) stays visible without the list eating the viewport.

  **2. Safety / status explanation copy + styling**
  - Current Completed headline: "This Feature is finished (pull request merged, or never opened), so it's safe to remove." in `gm-callout--success` (green).
  - Use **normal body text** for this explanation (not a green success callout). Keep warning/info callouts for Abandoned / Active / NeedsRepair / Unknown as appropriate.
  - Avoid brackets / parentheticals; be brief and specific.
  - **Suggested copy (pick one in implementation; refine if PR-merged vs never-opened should differ):**
    - Uniform finished: "This feature is finished and safe to remove."
    - If distinguishing is useful without parentheticals:
      - Merged: "This feature's pull requests are merged. It is safe to remove."
      - Never opened / no PR work: "This feature has no open pull request work. It is safe to remove."
  - Update Abandoned / Active lines only if needed for the same no-brackets rule; do not rewrite risk semantics.

  **3. Local / remote cleanup checkboxes**
  - Implemented in **D4** (do not duplicate). D6 assumes D4's checkboxes (or lands after / with D4). Coordinate so the static line is removed once.

  **4. Remove Feature primary button color (design intent â€” implement in this unit)**
  - **Green** (same family as Merge PR's primary / `.merge-pr-btn-github`) when removal is **safe**: classification Completed / finished Feature, nothing pending that makes the drop destructive (aligned with today's "safe to remove" cases â€” no unmerged Feature commits, no upstream Feature branches with commits the user is abandoning, automatically-safe path).
  - **Red** (`btn-danger` or equivalent) when the user is **dropping** a Feature that still has commits or upstream branches with commits (Active / Abandoned with work, unmerged ahead, outgoing, etc.) â€” destructive / unsafe drop.
  - Exact predicate: extract a small pure helper (e.g. `RemoveCtaIsDestructive(plan)`) and unit-test it; prefer matching the same facts the headline already uses so color and copy never disagree.

  **5. Loading / status copy**
  - Change page job start label from `"Checking Feature removal..."` to **"Checking feature status..."**.
  - While analysis probes repositories, progress to **"Checked x of y"** (wire progress from the existing parallel InspectWorktree probe loop in `AnalyzeRemoveFeatureAsync` into the page `BackgroundJob` / overlay message).
  - Anywhere this flow's loading overlay says "repo", prefer full words **"repository"** / **"repositories"** (also audit create/remove structural overlay strings in the same pass if they say "repos").

  **6. Explanation only â€” post-create red sync (do not implement product changes here)**
  - See **NOTE: Post-create sync indicators** immediately below. Capture design/intent only so the owner understands the behavior; any fix is a separate unit if desired later.

- **Tests to add:** summary helper (all-clean -> one line, no per-repo list; mixed -> only non-clean listed); CTA color helper (Completed/safe -> green, Active with commits -> red); loading label constants or progress formatting if extracted.
- **Done when:** tests pass; dialog matches the steps above; NOTE section remains documentation-only for sync-red.
- **Regression guard:** external-worktree cleanup / Switch Branch cleanup UI unchanged; D3 risk checkbox enable rule unchanged; D4 remote-delete safety unchanged.

### NOTE: Post-create sync indicators (explanation only â€” no implementation in D6)

Owner observation: after Create Feature (and sometimes other Feature operations), the header does not show "Worker is completing x tasks...". Instead the grid sync badges all appear **red** and gradually update.

**What actually runs after new Feature creation**

1. `CreateFeatureAsync` runs under the page **structural overlay** (`WorkspaceJobKeys.RepositoriesOverlayKey`), with progress like "Created feature in N of M repos" / "Seeding Feature projections..." â€” that is `BackgroundJobOverlay` / locked-operation progress, **not** the header Worker-task line.
2. Per repository, the App calls Agent `CreateGitWorktree` (parallel, gated). On success, rows become `Ready`; then `SeedInitialFeatureProjectionsAsync` copies Workspace link fields into `WorkspaceRepositoryContextState` for the new Feature context (including `SyncStatus` from the link â€” historically seeding from lagging special-context state left new Features all-red; seed now prefers the link).
3. The new Feature context is created with `IsInSync = false`, then selected (`SetSelectedAsync` + page `OnFeatureCreatedAsync` â†’ `OnSelectedContextChangedAsync` â†’ grid reload).
4. Creating/checking out worktrees fires **git hooks** on the Worker. Those enqueue **`SyncCommand`** notifications (`AgentSyncNotificationQueue` â†’ `SyncCommandHandler`), which attribute the path to the Feature context, `WorkspaceRepositoryStateWriter.ApplyAsync` with `SyncStatusWrite.Derive`, recompute deps, and broadcast `ContextRepositorySynced` / `ContextSynced`. Those updates arrive **asynchronously, one repository at a time**, after the create overlay has already closed.

**Why sync/status shows red then gradually updates**

- `RepoSyncStatusBadge` paints **NeedsSync**, **Error**, and **VersionMismatch** as red (`#dc3545`); only **InSync** is the calm blue. Missing status in the page dictionary also defaults to **NeedsSync** (`GetRepoSyncStatus`).
- Feature grids read context-state `SyncStatus`. Until a good derive/seed value is present, or while a hook sync is still writing, badges read as needing sync (red).
- As each `SyncCommand` (or a later user Sync) finishes for a repository, that badge flips toward InSync â€” hence the slow left-to-right / one-by-one clearing the owner sees.
- This is **expected current behavior**, not a failed create. Create already completed under its own overlay.

**Relation to "Worker is completing x tasks..."**

- That header line is bound to `AgentTasksPendingCount` â†’ `AgentQueueStateService.GetPendingCountForWorkspace` â€” the Agent command/queue pending count for the Workspace.
- Create Feature's long work is the App's structural job + Agent `CreateGitWorktree` commands inside that job. When the job finishes, the overlay goes away and the Feature grid loads; pending hook `SyncCommand` processing may or may not still show a non-zero Agent task count (often it does not, or only briefly), so the owner often **never** sees the Worker progress line for the gradual badge updates.
- **Non-relation:** red sync badges after create are **not** driven by the Worker progress UI. They are persisted/derived `RepoSyncStatus` on Feature context rows, refreshed as hook syncs land. Fixing the "surprise red" (if desired later) would be a separate UX/product unit (e.g. seed InSync more aggressively, keep overlay until initial hook syncs settle, or show a Feature-scoped "updating status..." affordance) â€” **out of scope for D6**.

### GATE-2 Remove and analysis (owner)

```
GATE-2 - Remove Feature tells the truth
Desktop setup (always):
1. Create Feature gate2-a. In one repo's worktree, edit a tracked file and add a new file.
   Open Remove. Expected: that repo shows uncommitted changes; the discard checkbox appears and lists it; Remove is disabled until ticked.
2. Commit in another repo's worktree (no push). Expected: that repo says it has commits not in the default branch; the branch checkbox appears.
3. Tick both, Remove. Expected: report shows worktrees and branches removed; folder %USERPROFILE%\.graymoon\<Workspace>\features\gate2-a is gone.
4. Locked files: create gate2-b, open a file from one worktree in a program that locks it (or run dotnet build there and keep the process alive). Remove.
   Expected: report lists leftover files for that repo; after closing the program, the folder can be deleted; creating gate2-b again works.
5. Clean Feature: create gate2-c, change nothing, Remove. Expected: no checkboxes, safe headline, clean report.
5b. Locked worktree: create gate2-e, run git worktree lock --reason "testing" on one of its repo folders. Open Remove.
   Expected: that repo shows it is locked with reason "testing"; the unlock checkbox appears and Remove is disabled until it is ticked. Tick it and Remove. Expected: gate2-e is fully removed.
6. Off the Feature branch: create gate2-d. In one repo's Feature folder commit once (no push), then run git switch -c side in a terminal.
   Open Remove. Expected: that repo says it is on "side" and "side" is kept; the unmerged-branch checkbox names gate2-d with 1 commit.
   Tick it and Remove. Expected: the report says gate2-d deleted and side kept; git branch in the Workspace folder still lists side.
7. Database: after these steps, the Dependencies page and Restore in the Workspace show only Workspace projects (no duplicates).
Docker setup (skip if DEC-1 = No):
8. Run the App in Docker with the Worker on the host. Repeat step 1. Expected: identical result to Desktop; no repo shows "Folder is already missing".
9. Stop the Worker and open Remove. Expected: "Could not check this repository", Remove disabled, no discard option.
Workspace regression:
10. In the Workspace, open Switch Branch for a repo whose target branch is held by a stray worktree (create one with git worktree add outside GrayMoon). Expected: cleanup offered and works as before.
11. Run WORKSPACE-SMOKE.
Reply with PASSED, or with the failing step and what you saw.
```

**After D4 / D6 ship (not required to pass GATE-2 as currently READY FOR USER TEST):** re-check a clean Feature Remove â€” expect summary line instead of NÃ— "Up to dateâ€¦", neutral (non-green) finished copy, local/remote cleanup checkboxes per DEC-2 (remote hidden or explained when never pushed), green Remove CTA when safe / red when destructive, and analysis overlay "Checking feature statusâ€¦" â†’ "Checked x of y".

---

## Lane E - Names and UX

### E1 Feature name validation (Migration for the index)

- **Goal:** Names that Git, Windows or a shell cannot handle are rejected while typing, with the reason; `Foo` and `foo` count as the same name.
- **Ref:** F-7, U-3, 04 P1-1.
- **Read first:** `FeatureOps` (the name regex near the top and the duplicate check in `CreateFeatureCoreAsync`); `CreateFeatureModal.razor`; `AppDbContext.cs` unique index `(WorkspaceId, Name)`.
- **Touches:** new `Common/Features/FeatureNameValidator.cs`, `FeatureOps`, `CreateFeatureModal.razor`, new migration step (index with `COLLATE NOCASE`), `AppDbContext.cs` (collation on `Name`), tests in Common.Tests and App.Tests.
- **Steps:**
  1. `FeatureNameValidator.Validate(string name)` returns null when valid, or a user-facing reason. Rules, implemented in code (no Agent call): Git `check-ref-format` rules for a branch (no space, `~ ^ : ? * [ \`, no `..`, no `@{`, not `@`, no leading `-` or `.`, no segment starting with `.`, no segment ending with `.lock`, no trailing `/` or `.`, no `//`, no control characters); Windows segment rules (no `< > " | ?  *`, no reserved names `CON PRN AUX NUL COM1-9 LPT1-9` with or without extension, no trailing space or dot in a segment); shell safety (no `& ; % ^ ! $` and backtick); max length 100.
  2. Replace the regex in `FeatureOps` with the validator.
  3. Duplicate check uses case-insensitive comparison. Migration step recreates the unique index with `COLLATE NOCASE`. If existing data already has case-only duplicates, the step logs a warning and keeps the old index (do not fail startup).
  4. Modal validates on every input change and shows the reason under the field; Create is disabled while invalid.
- **Tests to add:** a table-driven test with at least 30 names (valid: `feature/login`, `fix-123`, `user@host`; invalid: each rule above) in Common.Tests; case-only duplicate rejected in App.Tests; **Git parity** in Agent.Tests: for every name in the same table that the validator accepts, real `git check-ref-format --branch <name>` also accepts it (the validator may be stricter than Git, never looser).
- **Done when:** tests pass.
- **Regression guard (R-E1a, R-E1b, R-E1c):** the validator runs only when **creating** a Feature. Existing Feature names are never re-validated on load, select or remove (test: a Feature stored with a name the new rules reject still loads and can be removed). Do not use the validator for Workspace branch creation or switching; confirm with `rg` that the old regex had no other caller. The `NOCASE` index step logs and keeps the old index when case-only duplicates exist; it never fails startup.

### E2 Header primary button rule

- **Goal:** The header's primary button always shows the next useful action, and offers "Remove" only when the Feature's work is finished.
- **Ref:** U-12, 04 P1-5. Owner intent: keep the next-action pattern.
- **Read first:** `App/Components/Shared/WorkspaceRepositoriesHeader.razor` (`ShowRemoveFeaturePrimary`, `HasCreatablePr`, `HasOpenPr`, `HandleBranchPrimaryClick`); the page that passes those parameters (`rg -n "HasOpenPr=" GrayMoon/src/GrayMoon.App`).
- **Touches:** `WorkspaceRepositoriesHeader.razor`, the parent page that passes parameters, tests for a pure helper.
- **Steps:**
  1. Add parameter `bool AllFeaturePrsCompleted`: true when the Feature has at least one PR and every PR is merged or closed, and no repo has commits outside a PR. Compute it in the parent from data already loaded for the PR badges.
  2. Rule: Create PR (if creatable) > Remove (if `AllFeaturePrsCompleted`) > Feature (menu). A fresh Feature with no commits shows "Feature".
  3. Extract the rule into a static method returning an enum and unit-test it.
- **Tests to add:** fresh Feature -> Feature; creatable -> Create PR; open PR -> Feature; all merged -> Remove.
- **Done when:** tests pass; Desktop README bullet updated.
- **Regression guard (R-E2):** the rule function also gets Workspace cases, which must match today exactly: Workspace with a creatable PR -> Create PR; otherwise -> Branch. Write those two tests first (A4 step 0).

### E3 Desktop "Open in..." launch quoting (Desktop repo)

- **Goal:** Opening a Feature or repo in Claude CLI, Terminal, VS Code etc. works for any valid folder path.
- **Ref:** F-11, U-10, 04 P1-8, 05 D2.
- **Read first:** `GrayMoon.Desktop/src/GrayMoon.Desktop/Services/WebMessageBridgeService.cs` (the `cmd.exe /c start ... cd /d` and `wt.exe -d` lines).
- **Touches:** `WebMessageBridgeService.cs`, Desktop tests.
- **Steps:**
  1. Extract the `ProcessStartInfo` creation for each tool into a testable method that returns the `ProcessStartInfo` without starting it.
  2. **The folder path never goes into a command string.** Set `WorkingDirectory = path` on the process.
  3. Tools that are real executables (Cursor, `Code.exe` when it can be resolved, Visual Studio, Explorer): `UseShellExecute = false`, path passed as one `ArgumentList` item.
  4. Tools that need a shell or a window that stays open (Claude CLI, `code.cmd` fallback): keep `cmd.exe /k claude` (or `/c code .`), with `WorkingDirectory = path` and the literal `.` as the folder argument, so cmd never parses the path.
  5. `wt.exe`: `ArgumentList` items `-d`, `.` and the command, with `WorkingDirectory = path`; never pass a raw path that may contain `;`.
- **Tests to add (Desktop.Tests):** for paths with spaces, `&`, `;`, `(`, `'`: no `Arguments` string contains the path, `WorkingDirectory` equals the path, and `ArgumentList` items are either the raw path (executables) or `.` (shell tools).
- **Done when:** Desktop tests pass; GATE-4 manual check.
- **Regression guard (R-E3a):** Open in... is used for Workspace repos too. GATE-4 step 3 checks every tool for a **Workspace** repo and a Feature, on a path with a space. If a tool cannot start without putting the path in a string, keep today's launch for that tool, quote correctly, and add a Part F note.

### E4 OS-aware Feature paths (no refusal)

- **Goal:** Features work the same way regardless of the connected Worker's OS. Feature path-building (storage root, per-Feature folder, per-repository worktree path) uses whichever separator style the Agent's own paths already use (Windows `\` or POSIX `/`), instead of unconditionally assuming Windows. No "refuse on Linux" behavior; no explicit "which OS is the Worker" branching in business logic â€” the shape of the path text itself (a POSIX path starts with `/`) is the only signal needed.
- **Ref:** 01 section 5 item 6 and R9. DEC-7 (revised 2026-10-03): Linux Worker support for Features is in scope for v1, not deferred or refused â€” the Agent/Worker already ships, installs as a systemd service, and is distributed for Linux (`Dockerfile` publishes `linux-x64` alongside `win-x64`); only the Feature path-building added for this release assumed Windows.
- **Read first:** `WorkspaceContextPathResolver` (`CombineWindows`/`GetWindowsFileName`/`GetWindowsDirectoryName`/`IsWindowsDriveRootGraymoonPath`); `WorkspaceFeatureOperations` (`CombineWindowsPath`/`IsWindowsDriveRootGraymoonPath`, used in `CreateFeatureAsync` and `EnsureManagedFeatureStorageRootAsync`); `WorkspaceFeatureReconciler` (`CombineWindows`, `NormalizePathKey`, `IsPathUnder`); `WorkspaceService` (`NormalizeWindowsRoot`, `TryGetAgentDefaultFeatureStorageRootAsync`); `WorkspaceHookContextAttributor.NormalizePath`; `WorkspaceBranchOccupancyService`'s two inline `Replace('/', '\\').TrimEnd('\\')` path-key normalizations.
- **Touches:** new `GrayMoon.App/Services/Features/AgentPath.cs` (shared helper); the 4 files above that duplicate Windows-only path joining/inspection, replaced to call it; tests. No `GetHostInfo`/Agent protocol change (not needed - see Steps).
- **Steps:**
  1. Add `internal static class AgentPath` with `IsPosix(path)` (true when `path` starts with `/`), `Normalize(path)`, `Combine(params parts)`, `GetFileName(path)`, `GetDirectoryName(path)`, and `IsLegacyWindowsDriveRootGraymoonPath(path)` (the existing `X:\.graymoon` legacy-bug check, unchanged, which naturally never matches a POSIX path). Each picks `\` or `/` from the first non-empty input's own shape; never from a cached or passed-in "OS" value.
  2. Replace every one of the Windows-only private helpers listed in Read first with calls to `AgentPath`, deleting the old private methods. Do not change any method's public signature or the one Windows-only legacy-bug check's semantics.
  3. `WorkspaceService.TryGetAgentDefaultFeatureStorageRootAsync` derives the default root from the Agent's own `UserProfilePath` (already POSIX-shaped on Linux/macOS, e.g. `/home/dev`, or Windows-shaped, e.g. `C:\Users\dev`) via `AgentPath.Combine(profile, ".graymoon")` â€” the existing field is enough; no new `osPlatform` field on `GetHostInfo`.
  4. Leave `FeatureNameValidator` (E1), the residue-removal reparse-point walk (D1), and hook-file POSIX permission handling (`GitService.WriteHookFile`) untouched â€” they already work for either OS (E1 enforces a safe superset of rules; the other two already branch correctly where it matters).
- **Tests to add:** pure `AgentPath` tests covering both styles, mixed separators, trailing separators, and the legacy-drive-root check; a POSIX-configured Feature storage root still resolves to a `/`-joined context root; a persisted POSIX worktree path round-trips unchanged; `TryGetAgentDefaultFeatureStorageRootAsync` with a POSIX `UserProfilePath` returns a `/`-joined default. Every existing Windows-shaped test must stay green unchanged (A4 step 0 â€” these are the regression guard).
- **Done when:** tests pass.
- **Regression guard (R-E4):** every pre-existing Windows-shaped path test (persisted `ManagedFeatureStorageRoot`, configured setting, legacy drive-root relocation, worktree path round-trip, `IsPathUnder`/`PathsEqualNormalized`) produces byte-for-byte the same result as before. `AgentPath` is pure string manipulation (never `System.IO.Path`, matching the existing "do not use host Path.\*" rule) so the App's own OS (e.g. running in a Linux Docker container) never affects the outcome either.

### E5 Create Feature: structured branch-already-exists dialog

- **Goal:** When the Feature name collides with an existing branch in some repositories, show a proper structured dialog â€” not one continuous error string.
- **Ref:** owner UX 2026-10-02; `CreateFeatureCoreAsync` `BranchExists` / `FailCreate("BranchExists", ...)`.
- **Read first:** `FeatureOps` `CreateFeatureCoreAsync` (branch collision block building the long message from `snapshot.BranchCollisions`); `CreateFeatureModal.razor` (error callout showing `_error` as one string); `CreateFeatureResult` (`Condition`, `Error`).
- **Touches:** `CreateFeatureResult` (structured collision payload, keep `Error` as short summary for logs/toasts), `FeatureOps` `CreateFeatureCoreAsync` collision return, `CreateFeatureModal.razor` (layout), tests.
- **Steps:**
  1. On `BranchExists`, stop returning only:
     `Branch 'X' already exists in 10 of 11 repositories: RepoA (origin/X); RepoB (...); .... Choose a different Feature name or delete those branches first.`
  2. Return a structured result, e.g. `Condition = "BranchExists"`, short `Error` summary, plus `BranchCollisions: IReadOnlyList<{ RepositoryName, Refs }>` (or reuse the existing collision dictionary shape).
  3. **Dialog layout** (same modal or a dedicated error state inside `CreateFeatureModal`):
     - **Title / short summary:** e.g. "Branch already exists in X of Y repositories"
     - **Scrollable list** of affected repositories and their remote/local refs (cap list height similarly to D6 ~70% of content area if the list is long)
     - **Short guidance** under the list: "Choose a different Feature name, or delete those branches first."
  4. Name field stays editable so the user can try a different name without dismissing and reopening.
  5. Do not dump the full semicolon-joined string into a single callout.
- **Tests to add:** BranchExists result carries structured collisions (count + names); modal/helper formats summary "X of Y" without requiring the concatenated Error string; non-BranchExists errors still show as a simple callout.
- **Done when:** tests pass; manual create with a colliding name shows the structured layout.
- **Regression guard:** other create failures (`DuplicateName`, `NoRepositories`, `NeedsRepair`) keep their current simple error presentation; E1 validation-under-field unchanged.

---

## Lane F - Local security (no user login)

GrayMoon is a local developer tool with no user accounts, and that stays. These units stop **other programs and web pages** on the same machine from reading tokens or triggering actions.

### F1 Per-install token encryption key

- **Goal:** Tokens are encrypted with a key unique to this install, not one derived from a constant in the source.
- **Ref:** 05 S2.
- **Read first:** `App/Services/Security/TokenEncryptionKeyProvider.cs` (`CreateDefaultKeyString`), `AesGcmTokenProtector.cs`, `ITokenEncryptionKeyProvider.cs`, `Program.cs` registration; `GrayMoon/CLAUDE.md` section about token storage.
- **Touches:** those files, a startup re-encryption step, tests, `GrayMoon/CLAUDE.md` (fix the statement about Data Protection).
- **Steps:**
  1. If no key is configured, generate 32 random bytes on first start and store them in a key file next to the database (`graymoon.key`). On Windows, protect the file content with DPAPI (`ProtectedData`, CurrentUser) when running on Windows; on Linux (Docker), store as is with file mode 600.
  2. Keep the old default key as a **read-only legacy key id**: tokens with the legacy key id are decrypted with it and re-encrypted with the new key on startup (one pass, logged count). After that, the legacy key is used only to decrypt.
  3. Never log keys or tokens.
- **Tests to add:** new install generates and reuses the same key file; a token encrypted with the legacy key is readable and re-encrypted; tampered ciphertext fails.
- **Done when:** tests pass; `CreateDefaultKeyString` is used only for legacy decryption.
- **Regression guard (R-F1a, R-F1b, X-5):** losing the key would break every connector, so:
  1. A key configured in settings or the environment always wins and is never replaced.
  2. The key file lives in the same folder as the database (the Docker volume already persists it).
  3. Re-encrypt a token only after decrypting it with the new key succeeds (round trip); otherwise leave it under the legacy key.
  4. The legacy key stays available for decryption permanently.
  5. Re-encryption runs after the U0-2 backup and **never fails startup**: on error, log and continue.
  6. A token that cannot be decrypted marks its connector "Token needs to be re-entered" in the existing token health UI (`TokenHealthBackgroundService`) instead of throwing.
  7. DPAPI only when `OperatingSystem.IsWindows()`.

  Tests: configured key untouched; undecryptable token produces the health status and no exception; Sync with a connector works after re-encryption (existing connector tests stay green).

### F2 Worker secret for hub and token endpoint

- **Goal:** Only the real Worker can connect to `/hub/agent` and call `/repos/{id}/connector`.
- **Ref:** 05 S1, S4. Requires DEC-4.
- **Read first:** `App/Api/Endpoints/ConnectorEndpoints.cs`; `App/Hubs/AgentHub.cs`; `Agent/Services/AgentTokenProvider.cs` (calls `/repos/{repositoryId}/connector`); how the Worker gets the App URL during install (`App/Api/Endpoints/AgentEndpoints.cs` `InstallAgent`, Agent install handler).
- **Touches:** those files, Agent configuration, `App/Resources/install-worker.ps1` (pairing prompt), a new `POST /api/worker/pair` endpoint, the Worker page (pairing code), `GrayMoon.Desktop/src/GrayMoon.Desktop/Services/WorkerInstaller.cs`, tests.
- **Steps:**
  1. App generates a random Worker secret on first start (stored like F1's key).
  2. **Delivering the secret.** The install script comes from the unauthenticated `GET /api/worker/install` (`AgentEndpoints.InstallAgent`, placeholder substitution), so the secret must **never** be written into that script: anyone who can fetch the script would get it. Instead:
     - Desktop: `WorkerInstaller` reads the secret file next to the database and writes it into the Worker config directly.
     - Script install (Docker or manual): the Worker page shows a one-time pairing code (valid 10 minutes, single use). The script prompts for it and exchanges it at `POST /api/worker/pair` (no `Origin` allowed, F3) for the secret. A wrong or expired code returns 401.
  3. Worker sends `X-GrayMoon-Worker-Secret` on the hub connection and on the connector request.
  4. A **wrong** secret is always rejected (401), using a constant-time comparison (`CryptographicOperations.FixedTimeEquals`).
  5. A **missing** secret is handled by setting `Security:RequireWorkerSecret` (default **false** in v1): when false, the request is accepted, a Warning is logged once per Worker connection, and the App shows "Reinstall the Worker to finish securing GrayMoon" in its Worker status UI. When true, it gets 401. v1.1 flips the default.
  5b. **Sticky enforcement.** The first time a Worker presents the correct secret, persist the setting `Security:WorkerSecretSeen = true`. From then on a missing secret is rejected (401) on `/hub/agent` and `/repos/{id}/connector` even when `RequireWorkerSecret` is false. Reinstalling the Worker always sends the secret, so this never locks out a real Worker; without it, the connector endpoint would keep returning tokens to any local process for the whole of v1. If the secret file is missing at startup, generate a new secret **and reset `WorkerSecretSeen` to false** in the same step, so the next Worker connects with the warning instead of being locked out (test it).
  5c. `/repos/{id}/connector` rejects any request that carries an `Origin` header (403), regardless of the secret. The Worker never sends one.
  6. Desktop: `GrayMoon.Desktop/src/GrayMoon.Desktop/Services/WorkerInstaller.cs` writes the secret automatically on install and update.
- **Tests to add:** connector endpoint: wrong secret -> 401; right secret -> 200; missing secret with `RequireWorkerSecret=false` -> 200 plus warning; missing with `true` -> 401; missing after `WorkerSecretSeen` -> 401; any request with an `Origin` header -> 403. Same cases for the hub connection. The install script returned by `GET /api/worker/install` does not contain the secret; pairing code: valid once, expired after 10 minutes, wrong code 401.
- **Done when:** tests pass; owner verifies Worker still connects after reinstall (GATE-4).
- **Regression guard (R-F2a, R-F2b, R-F2c, X-1):** without the Worker GrayMoon does nothing, so v1 must accept already-installed Workers that have no secret (step 5) until a Worker has proved it has the secret (step 5b). The secret is stored next to the database and the install flow always rewrites it, so a reinstall always fixes a mismatch. If pairing fails in the script, the script stops with "Open GrayMoon > Worker and copy a new pairing code"; it never installs a Worker without a secret once `WorkerSecretSeen` is true.

### F3 Block cross-site and rebinding requests

- **Goal:** A web page open in the browser cannot trigger push, sync or other actions on GrayMoon, and cannot connect to its SignalR hubs.
- **Ref:** 05 S3, S4. Follow-up review: browsers do not apply CORS to WebSockets and nothing in the App checks `Origin`, so a web page can open `ws://127.0.0.1:8384/hub/agent` (with `skipNegotiation`), become "the Worker" in `AgentHub.OnConnectedAsync`, receive commands that carry `bearerToken`, and send fake results. With F2 not enforced in v1, the hub check here is the only protection.
- **Read first:** `App/appsettings.json` (`AllowedHosts: "*"`), `App/Api/Endpoints/*.cs` (all `MapPost`), `Program.cs` middleware order.
- **Touches:** `Program.cs`, new small middleware, tests, Desktop App launch configuration (where Desktop passes settings to the App process), `docs/architecture/05-user-capability-reference.md`.
- **Steps:**
  1. Middleware for every non-GET request under `/api/` and `/repos/`:
     - If the request has an `Origin` header (browsers always send it on cross-site POSTs) and the Origin's host is not the request's own host or a loopback name, return 403.
     - If the request has an `Origin` header, also require header `X-GrayMoon-Request: 1`; a cross-site page cannot add a custom header without a CORS preflight, which GrayMoon does not allow.
     - Requests **without** an `Origin` header (scripts, `curl`, the Worker, Desktop's own HTTP client) pass exactly as today.
  1b. Hub rule, for every request (negotiate and WebSocket upgrade) under `/hub/agent`, `/hubs/workspace-sync`, `/hubs/desktop` and `/_blazor`:
     - `/hub/agent`: any request with an `Origin` header is rejected with 403. The Worker's .NET SignalR client sends none; a browser always does.
     - `/hubs/workspace-sync`, `/hubs/desktop`, `/_blazor`: when an `Origin` header is present, its host must be the request's own host or a loopback name, otherwise 403. No `Origin` passes (the App's own server-side hub clients, 05 A4).
     - "Own host" for both rules also accepts `X-Forwarded-Host` when forwarded headers are enabled, and any host listed in a new optional setting `Security:AllowedOrigins`, so Docker users behind a reverse proxy can allow their public name.
  2. Update GrayMoon's own browser-side callers to send the header: `rg -n "fetch\(" GrayMoon/src/GrayMoon.App/wwwroot` and any JS interop that posts to `/api/`.
  3. `AllowedHosts`: do **not** change the shared `appsettings.json` (it stays `*` for Docker). Desktop sets `AllowedHosts=localhost;127.0.0.1;[::1]` for the App process it starts, because Desktop always uses loopback. Docs explain how Docker users restrict it.
  4. Document both rules in the REST API section of `docs/architecture/05-user-capability-reference.md`.
- **Tests to add:** cross-site Origin POST -> 403; same-origin POST with header -> passes; same-origin POST without header -> 403; POST with no Origin (script) -> passes; GET unaffected; `/hub/agent` negotiate and WebSocket with any `Origin` -> 403 and without `Origin` -> connects; `/hubs/workspace-sync` and `/_blazor` with a foreign `Origin` -> 403, with the page's own `Origin` -> connects, with no `Origin` -> connects.
- **Done when:** tests pass.
- **Regression guard (R-F3a, R-F3b, R-F3c, R-F3d):** test the hub rule with the Desktop WebView origin, `localhost` and `127.0.0.1`, and a forwarded host, so the UI never loses `/_blazor`. existing REST API scripts and the Worker send no `Origin` header and must keep working unchanged (test). Docker users who open GrayMoon by host name or LAN IP must not get HTTP 400, so `AllowedHosts` changes only for Desktop. The REST rule applies only to `/api/` and `/repos/`. The hub rule never blocks a request without `Origin`, so the Worker and the App's own server-side hub clients keep connecting; the Desktop WebView and a normal browser tab on GrayMoon send their own origin and pass (test both, R-F3d). Note: GrayMoon's Git hooks post to the **Worker's** listener (`127.0.0.1:<port>/hook/*`, `HookListenerHostedService`), not to the App, so they are not affected by this unit.

---

## Lane G - Hooks

### G1 Detect hook conflicts and warn

- **Goal:** When GrayMoon cannot install its sync hooks safely, the user is told, and existing user hooks are never overwritten.
- **Ref:** 05 A1, R4; full background and reasoning in `07-appendix-why-lane-g-hooks.md`. Requires DEC-3.
- **Read first:** `07-appendix-why-lane-g-hooks.md` sections 1, 2 and 4; Agent `GitService.WriteSyncHooksAsync`, `ResolveGitHooksDirectoryAsync`, `WriteHookFile`; `SyncRepositoryCommand` (calls it on every Sync); `CreateGitWorktreeCommand`; `Hosted/HookListenerHostedService.cs`.
- **Touches:** `GitService.cs` hook writing, the Sync response (new nullable `hookStatus` field), the App's repo grid row (warning icon), tests.
- **Owner decision 2026-10-04 (DEC-3):** step 3 (leave an existing hook byte-for-byte and report `ExistingHook`) and step 6 (App warning icon, `hookStatus`) are replaced by: rename the existing non-GrayMoon hook to `<hook>.replaced-by-graymoon` (timestamped name if that exists; never overwrite or delete) and write GrayMoon's hook, logging a Worker Warning only. A failed rename leaves the original untouched and skips that hook. A `core.hooksPath` outside the Git directory is still left alone (one Warning per Sync). No UX, no response field, no chaining.
- **Steps (default: detect and warn):**
  1. Resolve the hooks folder with `git rev-parse --git-path hooks` (honours `core.hooksPath`), relative to the repo path. If `core.hooksPath` is set (`git config --get core.hooksPath` returns a value), write nothing and return `hookStatus = HooksPathCustom` with the value.
  2. **GrayMoon's marker is the existing first comment line prefix `# Created by GrayMoon.Agent`** (identical in 0.1.0 and today). Do not invent a new marker: existing installs must keep being recognised.
  3. For each hook GrayMoon writes: if the file is missing or starts (after the shebang line) with the marker, write it; otherwise leave it byte-for-byte and add its name to `hookStatus = ExistingHook` with the list of names.
  4. Rewrite a GrayMoon hook only when its content, ignoring the marker line, differs from the new content (stops changing files on every Sync).
  5. Stop writing `post-update` (it never runs in a developer checkout). Leave an existing GrayMoon `post-update` in place.
  6. The App shows a small warning icon on affected repos with the tooltip "GrayMoon cannot install its Git hooks here, so changes made outside GrayMoon appear after the next Sync." plus the hook names or `core.hooksPath` value.
- **Tests to add (real git):** custom `core.hooksPath` -> nothing written, status returned; foreign `pre-push` kept byte-for-byte and reported; a hook file with the **0.1.0** GrayMoon text (copy it from `git show 0.1.0:src/GrayMoon.Agent/Services/GitService.cs`) is recognised and updated; current GrayMoon hook with unchanged content is not rewritten (file timestamp unchanged); in a linked worktree, the resolved folder equals the primary checkout's common hooks folder.
- **Done when:** tests pass.
- **Regression guard (R-G1a, R-G1b, R-G1c, X-1):** live updates (WORKSPACE-SMOKE step 3) must keep working in every repo where they work today, which means every repo GrayMoon already wrote hooks into is recognised by the existing marker. An older App that does not read `hookStatus` keeps working (nullable field).

### G2, G3, G4 - moved out of this plan

> Moved on 2026-10-04 (owner): unhooking repositories that leave a Workspace is general Workspace housekeeping, not worktree work. The full unit text (G2 Worker `UnhookRepository` command, G3 unhook on removal, G4 self-heal from pings) now lives in `12-hook-cleanup-plan.md` and runs after this plan. Do not implement them from here. G1 above stays in this plan and is DONE.

---

## Lane I - Branch rules inside a Feature

Background: `09-switch-branch-in-feature-analysis.md` (findings SB-1 to SB-8, chosen option B).

**The rule this lane enforces:**
- A Feature keeps every repository that is not tag-pinned on the branch named after the Feature.
- A tag-pinned repository (`WorkspaceFeatureRepository.PinnedTag != null`) stays detached at a tag.

**What the lane changes:**
- The per-repository Switch Branch dialog and the branch services follow the rule.
- The user always has a way back when a repository drifts off its branch, whether through the dialog or a terminal.
- Remove judges the branch it actually deletes.

The Workspace's dialog and services stay unchanged.

### I1 Remove analysis judges the Feature branch, not the current checkout

- **Goal:** Remove Feature's analysis, checkboxes and force delete describe the branch Remove actually deletes (the Feature branch), also when the worktree is on another branch or detached.
- **Ref:** 09 SB-2, F-9.
- **Read first:**
  - `FeatureOps` `AnalyzeRemoveFeatureAsync`: the line `BranchName = state?.BranchName ?? ...` and the `OutgoingCommits` and `HasUpstream` lines.
  - `FeatureOps` `RemoveFeatureCoreAsync`: `var branchName = row.PinnedTag == null ? info.FeatureName : null;` and `force = options.AllowForceDeleteLocalBranches`.
  - The A1 `InspectWorktree` request and response DTOs, and `GitService.InspectWorktreeAsync`.
  - `RemoveFeatureModal.razor` and the D3 enable-rule helper.
  - The plan types (`rg -n "class RemoveFeatureRepositoryPlan|class RemoveFeaturePlan" GrayMoon/src`).
- **Touches:**
  - The `InspectWorktree` DTOs and `GitService.InspectWorktreeAsync` (additive only).
  - `FeatureOps` `AnalyzeRemoveFeatureAsync` only.
  - The plan types.
  - `RemoveFeatureModal.razor`, and the "kept" lines of the D2 report.
  - Tests, and a Desktop README bullet.
- **Steps:**
  1. **Agent:** `InspectWorktree` gets an optional request field `featureBranch`. When it is set, the response adds these nullable fields, computed from refs without checking anything out:
     - `featureBranchExists`
     - `featureBranchSha`
     - `featureBranchAheadOfDefault`: commits on `refs/heads/<featureBranch>` that are not on `origin/<defaultBranch>`; null when that ref is missing.
     - `featureBranchHasUpstream`
     - `featureBranchAheadOfUpstream`

     When `featureBranch` is not set, these fields are null and nothing else changes.
  2. **Plan fields:** in `AnalyzeRemoveFeatureAsync`, send `featureBranch = info.FeatureName` for rows that are not tag-pinned. Each repository's plan gets:
     - `FeatureBranchName`
     - `CheckedOutBranch`: the live `branch` from `InspectWorktree`; null when detached.
     - `IsOffFeatureBranch`: true for a non-pinned repo whose `CheckedOutBranch` differs from `FeatureBranchName` (ordinal comparison, null counts as different).
  3. **Classification:** "automatically safe", the "Completed" rule and the D3 unmerged-branch checkbox use the Feature-branch fields (`featureBranchAheadOfDefault`, `featureBranchHasUpstream`, `featureBranchAheadOfUpstream`), not the checked-out branch. Checks for uncommitted changes stay on the worktree, as today.
  4. **Drift text:** when `IsOffFeatureBranch` is true, the dialog says for that repository: "This repository is on `<X>` (or a detached commit), not on its Feature branch `<name>`. `<X>` is kept; `<name>` is deleted." The D2 report lists `<X>` as kept. When `featureBranchExists` is false: "The Feature branch is already gone; nothing to delete."
  5. **Force checkbox:** the label of the "delete even if not merged" checkbox lists the Feature branches it applies to, with their unmerged commit counts.
  6. **Old Worker** (Feature-branch fields null): the facts are Unknown. The repo is not automatically safe, and the force-delete option is **not** offered for it. The delete is then non-force, so Git refuses an unmerged branch and D2 reports it as kept. Remove stays enabled, as with unknown PR state in A3.
- **Tests to add:**
  - **Agent.Tests (real git):**
    - The Feature branch has 2 commits not on default while the worktree is on `side`: `featureBranchAheadOfDefault = 2` and `branch = side`.
    - The Feature branch is missing: `featureBranchExists = false`.
    - No `featureBranch` in the request: all new fields are null.
    - The old response shape still deserializes.
  - **App.Tests:**
    - Drift plus an unmerged Feature branch: not safe, the checkbox lists the Feature branch with count 2, and the plan names `side` as kept.
    - Drift with a merged Feature branch: safe.
    - Old Worker: force not offered.
    - A repo that has not drifted produces the same plan as before (characterization test first, A4 step 0).
- **Done when:** tests pass. No path deletes a branch that the dialog did not describe.
- **Regression guard (R-I1):**
  - Remove is Feature-only.
  - A2's external-worktree cleanup (Workspace Switch Branch) never sends `featureBranch` and is unchanged.
  - The Agent fields are additive, per rule 13.
  - Tag-pinned rows are unchanged (no branch is deleted for them).
- **Out of scope:** deleting `<X>`, and changing what Remove deletes.

### I2 Own Feature branch is checkable; "Return to Feature branch"

- **Goal:** In a Feature, the user can always put a repository back on its Feature branch from the dialog.
- **Ref:** 09 SB-1, SB-8.
- **Read first:**
  - `App/Services/Features/WorkspaceBranchOccupancyService.cs`: `GetBadgesForRepositoryAsync` (its second loop, commented "Feature branches owned in DB but not currently listed"), `BranchOccupancyKind` and `BranchOccupancyBadge`.
  - `App/Components/Modals/SwitchBranchModal.razor`: `IsCheckoutDisabled`, `LoadOccupancyAsync`, `RequestFeatureCleanup` and the `[Parameter]` list.
  - `App/Components/Pages/WorkspaceRepositories.Branches.cs`: `ShowSwitchBranchModal` and `ShowSwitchBranchModalOnTagsTab`.
  - The existing occupancy tests (`rg -l "WorkspaceBranchOccupancyService|GitWorktreeOccupancy" GrayMoon/src/*.Tests`).
- **Touches:**
  - The occupancy service, `SwitchBranchModal.razor`, `WorkspaceRepositories.Branches.cs`, and the page markup that renders `SwitchBranchModal` (to pass the new parameter).
  - New `App/Services/Features/FeatureBranchPolicy.cs`.
  - Tests, and a Desktop README bullet.
- **Steps:**
  1. **Policy helper:** new static helper `FeatureBranchPolicy`, pure with no I/O:
     - `ExpectedBranch(string featureName, string? pinnedTag)` returns `featureName` when `pinnedTag` is null, otherwise null.
     - `IsOffFeatureBranch(string? expectedBranch, string? currentBranch)` returns true when `expectedBranch` is not null and `currentBranch` differs (ordinal), including a null `currentBranch` (detached).
  2. **Occupancy:** add `BranchOccupancyKind.FeatureOwn`. In the second loop, when `row.WorkspaceFeatureContextId` equals `viewingContextId`, emit `FeatureOwn` with `AllowCheckout = true`, `RequiresFeatureCleanup = false` and `ContextId = null`. Rows of other Features and the first loop stay unchanged.
  3. **Badge:** the modal gets a new parameter `FeatureBranchName` (string?, the expected branch from step 1; null in the Workspace and for pinned repos). The `FeatureOwn` badge reads "This Feature". Delete is not offered on the `FeatureOwn` row, because deleting the Feature branch is Remove's job.
  4. **Warning and button:** when `IsFeatureContext` is true and `FeatureBranchPolicy.IsOffFeatureBranch(FeatureBranchName, CurrentBranch)` is true, the modal shows the warning "This repository is not on its Feature branch `<name>`." It also shows a "Return to Feature branch" button that calls the existing `OnCheckoutBranch(repositoryId, FeatureBranchName, false)`. If Git refuses (for example because of uncommitted changes), show Git's message the way checkout errors are shown today.
  5. **Page:**
     - Pass `FeatureBranchName`, computed from the selected Feature's name and the repo's `PinnedTag`. Find where the page holds them with `rg -n "PinnedTag" GrayMoon/src/GrayMoon.App/Components/Pages`, and load them once per Feature selection, not per row.
     - Fix SB-8: `ShowSwitchBranchModalOnTagsTab` sets `WorkspaceRepositoryId` (and `CurrentBranch`) the same way `ShowSwitchBranchModal` does.
- **Tests to add:**
  - Viewing Feature A, with A's branch not in the worktree list: `FeatureOwn`, `AllowCheckout = true`.
  - Feature B's branch while viewing A: `Feature` and blocked, as today.
  - Viewing the Workspace: both A's and B's branches are `Feature` and blocked, as today.
  - Table tests for `FeatureBranchPolicy`.
- **Done when:** tests pass, and the Desktop README bullet is added.
- **Regression guard (R-I2):**
  - Do A4 step 0 first: a characterization test of `GetBadgesForRepositoryAsync` for the special Workspace context with two Features, whose output stays identical after the change.
  - The Workspace dialog always gets `FeatureBranchName = null`, so the warning and the button never render there.
  - The SB-8 fix is a named bug fix that also affects the Workspace: badges now appear on the upgrade-badge path, and Git refused those checkouts anyway.
- **Out of scope:** blocking other checkouts (I3), and the grid badge (I4).

### I3 Feature-aware Switch Branch dialog and service rules

- **Goal:** In a Feature, the dialog and the branch services allow only actions that keep the rule true:
  - view, filter and fetch;
  - delete, with a warning;
  - return to the Feature branch;
  - change the tag of a tag-pinned repository.
- **Ref:** 09 SB-3, SB-4, SB-5, SB-7 and section 7; F-16; 04 P2-10.
- **Read first:**
  - `SwitchBranchModal.razor`: the tabs, `IsCheckoutDisabled`, the `RequestDeleteBranch` confirmation text, the New Branch tab (including its "branch exists, check out instead" path) and the Return to Default button.
  - `App/Services/Application/WorkspaceBranchOperations.cs`: `CheckoutAsync`, `CreateBranchAsync` and `ReturnToDefaultAsync`.
  - `App/Services/Orchestration/WorkspaceBranchHandler.cs`: `CheckoutBranchForWorkspaceAsync`.
  - `BranchHttpOutcome`.
  - How a context id resolves to a Feature (`rg -n "Kind" GrayMoon/src/GrayMoon.App/Services/Features/*Context*`).
  - `FeatureBranchPolicy` (from I2).
- **Touches:**
  - `SwitchBranchModal.razor`.
  - `WorkspaceBranchOperations.cs` (the start of the three methods named above only), and `WorkspaceBranchHandler.CheckoutBranchForWorkspaceAsync` (its start only).
  - New `App/Services/Features/FeatureBranchGuard.cs` (with an interface and DI registration), and `FeatureBranchPolicy`.
  - Tests, and a Desktop README bullet.
- **Steps:**
  1. **Policy:** add `FeatureBranchPolicy.Evaluate(FeatureBranchAction action, string? expectedBranch, string? pinnedTag, string? target, bool isTag)`. It returns null when the action is allowed, or the user message. Rules for a Feature context:
     - Check out a branch: allowed only when `target` equals the expected branch. `CheckoutAsync` already strips `origin/`, so call the guard after that line.
     - Check out a tag: allowed only when `pinnedTag` is not null. Message: "Tags can be checked out only in repositories pinned to a tag. Use the Workspace to check out a tag."
     - Create a branch: never allowed. Message: "A Feature keeps every repository on its Feature branch. To work on another branch, create another Feature or use the Workspace."
     - Return to Default: never allowed. Message: "Return to Default is a Workspace action."
     - Fetch, delete, set upstream and Update Branch from Default: always allowed, and not routed through the policy.
  2. **Guard:** `FeatureBranchGuard.CheckAsync(contextId, repositoryId, action, target, isTag, cancellationToken)`.
     - It loads the context with `IDbContextFactory`.
     - For the special Workspace (Kind 0), or a context that is not found, it returns "allowed" **without any further query**.
     - For a Feature, it loads the Feature name and the repo row's `PinnedTag`, then calls `Evaluate`.
  3. **Wiring:**
     - Before wiring, confirm with `rg` that Feature create (`CreateFeatureCoreAsync`) and repair (C4) do not call these three methods; they use `CreateGitWorktree`. If they do, stop and ask.
     - Call the guard at the start of `CheckoutAsync`, `CreateBranchAsync` and `ReturnToDefaultAsync`.
     - On refusal, return `BranchHttpOutcome.BadRequest(message)`. If an existing conflict-style outcome fits better, reuse it; do not add a new status.
     - In `CheckoutBranchForWorkspaceAsync` (bulk), refuse the whole call for a Feature context with the create-branch message. The header already hides bulk actions in Features, so this is defence only.
  4. **Pinned tag upgrade:** when `CheckoutAsync` succeeds with `isTag` in a Feature for a pinned repo, set that repo's `WorkspaceFeatureRepository.PinnedTag` to the new tag.
  5. **Dialog**, only when `IsFeatureContext` is true:
     - Hide the New Branch tab. If `InitialTab` is `newbranch`, fall back to `local`.
     - Locals and Remotes: checkout is disabled for every branch except the Feature branch, which I2 keeps checkable. The tooltip is the message from step 1. The modal calls the same `FeatureBranchPolicy.Evaluate`, so the UI and the service give the same answer. Add a `PinnedTag` parameter for that.
     - Tags: checkout is enabled only when `PinnedTag` is not null; otherwise it is disabled, with the tag message as tooltip.
     - The delete confirmation gets the line "Branches are shared by the Workspace and every Feature of this repository. Deleting it removes it everywhere."
     - Return to Default stays hidden.
- **Tests to add:**
  - **`Evaluate` table tests:**
    - Checking out the expected branch is allowed; any other branch is refused.
    - A tag is allowed for a pinned repo and refused otherwise.
    - Create and Return to Default are refused.
  - **Guard:**
    - Special context: allowed, with no Feature query (count queries or use a context that has no Feature tables seeded).
    - Feature context: refused for a disallowed action.
  - **Service:**
    - `CheckoutAsync` in a Feature to another branch returns the refusal, and the fake Agent bridge records **zero** calls.
    - `CreateBranchAsync` and `ReturnToDefaultAsync` in a Feature behave the same way.
    - A pinned tag upgrade updates `PinnedTag`.
- **Done when:** tests pass. `rg -n "FeatureBranchGuard" GrayMoon/src/GrayMoon.App/Services/Application/WorkspaceBranchOperations.cs` shows three calls. The Desktop README bullet is added.
- **Regression guard (R-I3a, R-I3b):**
  - Do A4 step 0 first for `CheckoutAsync`, `CreateBranchAsync` and `ReturnToDefaultAsync` on the special Workspace: one test each, asserting the Agent is called with the same arguments as today.
  - For the special Workspace, the guard returns before any Feature query.
  - Every dialog change sits inside an `IsFeatureContext` branch.
  - REST routes call these methods with the Workspace context (05 R5), so they keep working. Add one test that the checkout route still succeeds for a Workspace repo.
  - WORKSPACE-SMOKE step 4 at the gate.
- **Out of scope:** multi-branch Features (09 option C), and changes to Update Branch from Default.

### I4 Show drift in the grid

- **Goal:** When a Feature repository is not on its Feature branch, for any reason including a terminal checkout, the grid says so and offers the way back. Drift never makes a Feature "Needs attention".
- **Ref:** 09 SB-6.
- **Read first:**
  - The grid branch cell (`rg -n "OnBranchClick" GrayMoon/src/GrayMoon.App/Components`).
  - Where the page gets each row's `BranchName` for a Feature context.
  - `FeatureBranchPolicy` (I2).
  - `WorkspaceFeatureReconciler` (C2).
- **Touches:** the grid branch cell component, the page code that builds rows, the reconciler tests (and its branch comparison, only if there is one), and a Desktop README bullet.
- **Steps:**
  1. **Compute drift:** for a Feature context, compute per repository `expected = FeatureBranchPolicy.ExpectedBranch(featureName, pinnedTag)` and `IsOffFeatureBranch(expected, branchName)`. Use data the page already loads, plus the Feature's repo rows (with `PinnedTag`) loaded once per Feature selection. No new Agent calls. Put the mapping in a small pure method (for example `ComputeOffFeatureBranchRepos`) so it can be unit-tested.
  2. **Badge:** a drifted row shows a warning badge "off feature branch" (lower case, like other badges) next to the branch, with the tooltip "This repository is on `<X>`, not on `<name>`. Click to return." Clicking opens the Switch Branch dialog, where I2's "Return to Feature branch" button is shown.
  3. **Reconciler (C2):** add a test that a Ready repo registered at its path but on another branch stays Ready. If C2's code compares the branch, change it to compare the path only (C2 step 2).
- **Tests to add:** mapping tests for drifted, not drifted, tag-pinned, and the Workspace context (expected is null, so nothing is flagged), plus the reconciler test.
- **Done when:** tests pass; the owner checks it at GATE-4 step 7.
- **Regression guard (R-I4):**
  - The badge renders only for Feature contexts, because the expected branch is null for the Workspace.
  - No extra Agent calls per refresh.
  - The reconciler never changes state because of the branch.
- **Out of scope:** returning automatically on Sync (09 question 2: warning only).

## Lane H - Logging

### H1 Structured logging for Feature operations

- **Goal:** Support can reconstruct what happened to a Feature from the log alone.
- **Ref:** F-15, 04 P1-9.
- **Read first:** `FeatureOps` logging calls; `WorkspaceFeatureReconciler` (C2).
- **Touches:** `FeatureOps`, `WorkspaceFeatureReconciler.cs`.
- **Steps:** Information log at start and end of Create, Remove, Repair, Rollback, Reconcile with `{WorkspaceId} {FeatureId} {ContextId} {FeatureName} {DurationMs} {Outcome}`; Warning per failed repo with `{Repository}` and the Git error text; use `LoggerMessage` source-generated methods if the project already uses them, otherwise `ILogger` with message templates.
- **Tests to add:** none (log content), but build and all tests must pass.
- **Done when:** every per-repo failure path logs once.
- **Regression guard (R-H1):** Information per operation, Debug per repo, Warning per failure, so large Workspaces don't flood the log. Never log tokens.

### GATE-4 Names, header, security, hooks, Desktop (owner)

```
GATE-4
1. New Feature dialog: type "CON", "a&b", "my feature", "Foo" (when "foo" exists). Expected: each shows a reason and Create is disabled. "feature/login" works.
2. Fresh Feature: header button reads "Feature", not "Remove". After its PRs are merged: it reads "Remove".
3. Open in...: create Feature "gate4-x" in a Workspace whose path has a space; Open in Claude CLI, Terminal, VS Code, Cursor, Visual Studio, Explorer, once for a Workspace repo and once for the Feature. Expected: all open in the right folder and Claude CLI's window stays open.
4. Security: in a normal browser tab (not GrayMoon), open the browser console and run
   fetch('http://localhost:8384/api/workspaces/1/sync', {method:'POST', mode:'no-cors'})
   Expected: nothing happens in GrayMoon (no sync starts).
   In the same console run
   new WebSocket('ws://localhost:8384/hub/agent')
   Expected: the connection fails (403); the real Worker stays connected and GrayMoon shows no Worker change.
   If you use REST API scripts, run one. Expected: it works as before.
5. Worker, before reinstalling: it still connects (with a "Reinstall the Worker" notice); Sync works.
   Reinstall the Worker from GrayMoon. Expected: it connects, the notice is gone, Sync works.
   Open http://localhost:8384/api/worker/install in the browser. Expected: the script text contains no secret.
   Then open http://localhost:8384/repos/1/connector in the browser. Expected: no token shown (403, the browser sends an Origin or the secret is missing after the Worker proved it has one).
   From a terminal run curl http://localhost:8384/repos/1/connector. Expected: 401 (no secret).
6. Hooks (G1; the cleanup steps moved to GATE-H in 12-hook-cleanup-plan.md): in a test repo run git config core.hooksPath .husky, Sync. Expected: .husky contents unchanged, no GrayMoon hooks written there, one Warning in the Worker log, no warning icon (there is none).
   In another repo add your own .git/hooks/pre-push (e.g. echo "user hook"), Sync. Expected: your hook is now .git/hooks/pre-push.replaced-by-graymoon with its content intact, GrayMoon's own pre-push is in its place, and the Worker log has a Warning naming the rename.
   In a repo where GrayMoon hooks already exist, commit from a terminal. Expected: grid updates within seconds (hooks still work). Sync twice; Expected: the hook files are not rewritten the second time (modified time unchanged).
7. Branch rules in a Feature: create Feature gate4-b and open a repo's branch dialog.
   Expected: no New Branch tab, no Return to Default; other branches cannot be checked out and the tooltip explains why; tags cannot be checked out unless the repo is pinned to a tag.
   Delete a throwaway branch from inside the Feature. Expected: the confirmation says branches are shared by the Workspace and all Features.
   In a terminal, in that repo's Feature folder, run git switch -c side. Expected: within seconds the grid shows "off feature branch".
   Click it and press "Return to Feature branch". Expected: the repo is back on gate4-b and the badge is gone.
   Tag-pinned repo (if you have one): in the Feature, open its upgrade badge and check out a newer tag. Expected: works; the repo stays on the tag.
   In the Workspace, open a repo's upgrade badge, then the Locals tab. Expected: branches held by a Feature now show the Feature badge (missing on this path before); everything else as before.
7b. Long paths (Windows): in a test repo commit a file nested deeper than 260 characters (or use a repo with a deep node_modules), create a Feature, then Remove it. Expected: Create and Remove both succeed, no "Filename too long" in the Feature status panel, and `git config --local core.longpaths` in the primary checkout prints `true`.
8. Workspace edits while a Feature exists (keep gate4-b):
   Edit the Workspace and press Save without changing anything. Expected: saves, no error.
   Try to rename it. Expected: the name field is read-only with the reason; nothing changes.
   Try to delete the connector of its repositories. Expected: refused with "Remove the Features that use this connector's repositories first."; nothing is deleted.
   Open Remove Workspace on it. Expected: the dialog says "Remove the Features first, then delete the Workspace." and Remove is disabled.
   Remove gate4-b, then rename the Workspace. Expected: works as before. Then Remove Workspace on a test Workspace with no Features. Expected: it is deleted as before.
9. Linux Worker (skip if you have none; DEC-7 and E4): connect a Linux Worker, create Feature "gate4-l", then Remove it. Expected: it works like on Windows, and the Feature paths the Worker reports use "/" separators.
10. Run WORKSPACE-SMOKE.
Reply with PASSED, or the failing step and what you saw.
```

---

## Lane R - Release

### R1 Docs

- **Goal:** Users and support have what they need for v1.
- **Ref:** 01 sections 4.2 and 4.3; 04 checklist "Docs".
- **Touches:** new `GrayMoon/docs/user-guide/features.md`, new `GrayMoon/docs/user-guide/troubleshooting.md`, new `GrayMoon/docs/user-guide/operations.md`, new `GrayMoon/CHANGELOG.md`, a superseded banner at the top of every file in `GrayMoon/docs/worktree/`, `GrayMoon/README.md`.
- **Steps:**
  1. `features.md`: what a Feature is; where it lives on disk; what Create copies and what it does not (uncommitted changes); switching; Open in...; what Remove deletes (worktrees, local branches, and remote branches when the user leaves that checkbox ticked â€” D4) and what it keeps when remote delete is unticked or no remotes exist; Needs attention and repair.
  2. `troubleshooting.md`: Feature stuck or needs attention; "Removal incomplete"; leftover folders; untracked worktrees reported by the reconciler; locked worktrees; branch already exists; Worker not connected or pairing failed; hooks warning; "Filename too long" (GrayMoon sets `core.longpaths=true` in the repository's config on Windows; if it still happens, check `git config --local core.longpaths` and the shortness of the Feature storage root); manual cleanup recipe (`git worktree list`, `git worktree unlock <path>`, `git worktree remove --force <path>`, `git worktree prune`, `git branch -D <name>`, delete `features\<name>`); where logs and DB backups are; **downgrade**: close GrayMoon, restore the newest `graymoon.db.bak-<timestamp>` (and the key file from F1 if it was created after the backup), then start the previous build.
  2b. `GrayMoon/docs/user-guide/operations.md` (DEC-8): the Feature storage root setting, what changes when the App runs in Docker (Agent-side checks, `AllowedHosts`, pairing the Worker), that Features work with a Linux (or Windows) Worker the same way â€” paths just follow whichever shape that Worker already uses (DEC-7/E4), the security settings (`Security:RequireWorkerSecret`), and the log fields from H1.
  2c. `features.md` also gets a short "REST API and Features" paragraph: the API works on the Workspace only and cannot create, list, repair, remove or run operations inside a Feature. Use the text in `11-post-release-plans.md` section 1.1, after checking its three "verify first" items against the code so the claim is true.
  3. `CHANGELOG.md`: a v1 entry summarizing user-visible changes from all units (use the Desktop README bullets).
  4. Banner for `docs/worktree/*`: `> Superseded design history. The current behaviour is documented in docs/architecture and docs/user-guide.`
  5. `README.md`: link the user guide. Keep Worktrees marked as shipped only once GATE-5 passes; until then mark it "Preview". Do not document a `Features:Enabled` setting; there is none (DEC-6 = No).
- **Done when:** every claim matches the code after all units.
- **Regression guard:** docs only. Do not describe behaviour that a unit marked BLOCKED or skipped did not ship.

### R2 Features kill switch

> **DROPPED (owner, 2026-10-04, DEC-6 = No).** Features are a core part of GrayMoon and are not hidden behind a flag. Do not implement anything in this unit. The text below is kept only as history. R4 treats the checklist's "Rollback" kill-switch item as "dropped per DEC-6"; rollback of a bad release is the downgrade note in R1 (restore the backup, run the previous build).

- **Goal:** The owner can hide Feature creation and the Features section without a code change.
- **Ref:** 04 checklist "Rollback". Requires DEC-6.
- **Touches:** `appsettings.json`, an options class, `WorkspaceFeatureSelector.razor`, `CreateFeatureModal` entry points, the facade `CreateFeatureAsync` (refuse when disabled).
- **Tests to add:** create refused when disabled.
- **Done when:** tests pass.
- **Regression guard (R-R2):** disabled hides only the Features section and the create button; the Workspace entry and the Workspace page work unchanged (manual check plus WORKSPACE-SMOKE).

### R3 GitVersion parity check

- **Goal:** Prove that versions computed at the default-branch tip match GitVersion run on a real checkout.
- **Ref:** 04 P1-11, UX-Gaps open spike.
- **Read first:** `GetGitVersionAtDefaultTip` (Agent) and `ApplyDefaultTipVersionsForMergedFeatureReposAsync` (App).
- **Touches:** one Agent real-git test.
- **Steps:** in a temp repo with GitVersion config and tags, compare `GetGitVersionAtDefaultTip` output with running GitVersion in a fresh checkout of `origin/main`. If GitVersion is not installed on the machine, mark the test as skipped with a clear reason and tell the owner.
- **Done when:** test passes or a mismatch is reported to the owner as a new unit.
- **Regression guard:** test-only; if it finds a mismatch, do not change version calculation in this unit; report it as a new unit.

### R4 Release regression suite and checklist

- **Goal:** Everything in the release checklist is checked and green.
- **Touches:** this file (Part C notes), tests if any are missing.
- **Steps:**
  1. Run the full build and all test projects in both repos; record counts.
  2. Walk the checklist in `04-worktree-v1-release-roadmap.md` section "Release-readiness checklist"; tick each item with the unit that covered it, mark the items DEC-8 moved to v1.1 as "moved per DEC-8", mark the kill-switch item as "dropped per DEC-6", or add a unit for anything else uncovered and stop.
  3. Confirm `Discovered issues` (Part F) has no item marked "release blocker".
  4. This is the **final task** of the plan - once steps 1-3 are green, run `GrayMoon.Desktop\build\Publish-GrayMoonBundle.ps1` so a release-candidate bundle exists under `artifacts\bundle\` for the owner to use in GATE-5. Do **not** run this script after any other unit; it only runs here, once, after the whole plan's tests and checklist are green.
- **Done when:** all of the above, then set GATE-5 to `READY FOR USER TEST`.
- **Regression guard:** includes running WORKSPACE-SMOKE on the release build; any failure there is a release blocker.

### GATE-5 Release candidate sign-off (owner)

```
GATE-5 - Release candidate
1. Fresh install of the Desktop build on a clean user profile (or a VM): first start, add a connector, Workspace, create a Feature, sync, push, PR, remove.
2. Upgrade install over a 0.1.0 Desktop with real data: everything from GATE-1 again, then create and remove a Feature.
3. Rerun GATE-2 steps 1, 3 and 5 and GATE-3 step 1 on the release build.
4. Read docs/user-guide/features.md as a new user. Anything unclear goes into Part F.
5. Run WORKSPACE-SMOKE on the upgraded install from step 2 (real data).
Reply with PASSED to sign off v1, or the failing step.
```

### WORKSPACE-SMOKE Standard Workspace regression check (owner, at every gate)

Use a Workspace with at least 3 repositories, one at a higher dependency level, and no Feature selected. Purpose: prove nothing that worked before the plan is broken. Risk background: `08-plan-regression-risk-review.md` section 1.

```
WORKSPACE-SMOKE
1. Start GrayMoon. The Worker connects within 30 s; no error toasts.
2. Sync all. Versions, branches and ahead/behind fill in.
3. In a terminal, commit in one repo. Within a few seconds the grid shows it (hooks work).
4. Branch: switch all to a new branch (create). All repos switch. Return to Default brings them back.
5. Git Changes: change a file, stage it, commit from GrayMoon.
6. Update Dependencies, then Push with levels. Versions flow to the higher level.
7. Create a PR for one repo, see the badge, then close it.
8. Dependencies page and Restore: each project appears once.
9. Open in... VS Code and Terminal for a Workspace repo.
10. If you use REST API scripts, run one.
Reply with PASSED, or the step number and what you saw.
```

---

# PART E - Changelog of this plan

Agents append one line per session: `YYYY-MM-DD <unit> <status> <agent> - <one sentence>`.

- 2026-10-01 plan created from review documents 00-05.
- 2026-10-01 added Workspace regression guards to every unit, rules 13-14, A4 step 0 and WORKSPACE-SMOKE from `08-plan-regression-risk-review.md`. Design changes: legacy migrations stay tolerant (U0-2), the Worker secret is not enforced in v1 (F2), `AllowedHosts` is unchanged for Docker and the header check applies only to requests with an `Origin` (F3), NeedsRepair opens read-only (C3), Unknown PR state warns instead of blocking (A3), D1 delete guards and junction handling, G1 reuses the existing hook marker. F3 no longer claims hooks post to `/api/sync`. C2 now depends on A1.
- 2026-10-01 added G2 (Worker `UnhookRepository`), G3 (unhook when repositories leave a Workspace, block Workspace delete while Features exist) and G4 (self-heal stale hooks); background in `07` section 6, risks R-G2 to R-G4 in `08`. GATE-4 now depends on G3 and G4.
- 2026-10-01 added lane I from `09-switch-branch-in-feature-analysis.md` (option B): I1 (Remove judges the Feature branch), I2 (own Feature branch checkable, Return to Feature branch, upgrade-badge occupancy fix), I3 (Feature-aware Switch Branch dialog and service guard), I4 (drift badge). C2 now compares the worktree path only. GATE-2 gets step 6 and depends on I1; GATE-4 gets step 7 and depends on I3 and I4. Risks R-I1 to R-I4 in `08`.
- 2026-10-01 A6: the commit message is now part of the required final message, with format rules (one per repository, imperative summary ending with the unit id, `none` when nothing changed).
- 2026-10-01 A6: the final message now lists 2 to 5 owner checks with expected results, plus a Workspace check, to run before the next unit.
- 2026-10-01 follow-up review of all docs against the code. New units: B5 (connector refresh and delete keep Feature repositories), B6 (Edit Workspace partial save; rename blocked while Features exist), D5 (locked worktrees in Remove), E4 (refuse Create Feature on a non-Windows Worker). Changed units: C2 reports untracked worktrees and skips removed rows; D2 adds `Removing`/`Removed` row states; C4 refuses create-style repair for a remove-incomplete Feature and seeds the whole Feature once; C3 and C5 show "Removal incomplete" and Continue removal; E1 adds a `git check-ref-format` parity test; F2 never puts the secret in the install script, adds pairing and sticky enforcement; F3 adds Origin checks for the hubs; D1 guard 5 wording aligned with `08`; B2 foreign-key column name fixed. New decisions DEC-7 (Linux Worker) and DEC-8 (checklist items moved to v1.1). Dependencies: C4 on D2, G3 on B5 and B6, GATE-2 on B3 and D5, GATE-4 on B5, B6 and E4. GATE-2 step 5b, GATE-3 step 7, GATE-4 steps 4, 5, 8 and 9 added or extended. Risks R-B5, R-B6, R-C2c, R-C4b, R-D2b, R-D5, R-E4, R-F2c, R-F3d in `08`.
- 2026-10-01 U0-1 DONE Claude Sonnet 5 - fixed the CS8619 warning in `WorkspaceGitService.Context.cs` by typing the dictionary projection as `string?`; deleted `test-agent.txt`, `test-app.txt`, `test-common.txt` and added a `test-*.txt` rule to `.gitignore`; made the two PowerShell pipe-deadlock tests in `GrayMoon.Common.Tests/CommandLineServiceTests.cs` (the test named in Ref T1; the Touches list said Agent.Tests, which has no such test) deterministic by raising their timeout to 30s and dropping the flaky wall-clock assertion in favour of asserting completion and output size.
- 2026-10-01 U0-2 DONE Claude Sonnet 5 - added a `PRAGMA user_version`-tracked migration runner to `Migrations.cs`. Step 1 ("legacy baseline") bundles every existing migration method and runs in tolerant mode exactly once (every empty `catch {}` now logs at Error and continues). A new `StrictSteps` list (currently empty; future units append to it) runs in its own transaction each, rolling back and throwing `DatabaseMigrationException` on failure, which stops startup (new explicit catch in `Program.cs`). A `VACUUM INTO` backup is written next to the database before the first pending step (legacy or strict) runs, verified with `PRAGMA integrity_check`, keeping the 3 newest and pruning older ones; nothing is backed up when nothing is pending or the database is not file-backed (in-memory tests). Also replaced the N+1 `AnyAsync`-per-row existence checks in every `BackfillContext*Async` helper in `Migrations.Features.cs` with one preloaded `HashSet` each. All public `Migrate*Async` methods gained an optional `ILogger? logger = null` parameter so existing call sites compile unchanged.
- 2026-10-01 U0-3 DONE Claude Sonnet 5 - produced `Fixtures/graymoon-0.1.0.db` by adding a temporary worktree of the shipped `0.1.0` tag under `$env:TEMP`, running a throwaway test there that called `EnsureCreated` against the 0.1.0-era `AppDbContext` and inserted 1 connector, 1 workspace, 3 linked repositories, 2 projects and 1 project dependency, then copying the resulting file (200 KB) into `Fixtures` and removing the temporary worktree. Added `<None Include="Fixtures\graymoon-0.1.0.db" CopyToOutputDirectory="PreserveNewest" />` to `GrayMoon.App.Tests.csproj`. New test class `UpgradeFrom010Tests` copies the fixture to a temp file, runs `Migrations.RunAllAsync` against it, queries one row from every `AppDbContext` DbSet, and asserts the seeded rows survive plus the legacy baseline's backfilled special Workspace context and its `WorkspaceSelectedFeatureContext` and three `WorkspaceRepositoryContextState` rows exist.
- 2026-10-01 GATE-1 PASSED Owner - owner upgraded their real database from this branch with no issues.
- 2026-10-01 A1 DONE Claude Sonnet 5 - added the Agent `InspectWorktree` command end to end per the add-agent-command skill: `InspectWorktreeRequest`/`InspectWorktreeResponse` DTOs, `InspectWorktreeCommand`, `GitService.InspectWorktreeAsync` (new method; probes registration via the existing worktree list, folder existence, lock state, HEAD/branch, `git status --porcelain=v1` counts, upstream ahead/behind, and ahead-of-default), registrations in `RunCommandHandler`, `CommandDispatcher` and `CommandJobFactory`, the `AgentHubMethods.InspectWorktree` constant, and a `ReadOnlyCommands` entry in `SignalRConnectionHostedService`. `GitWorktreePorcelainParser` and `GitWorktreeInfo` gained `locked`/`lockReason` parsing (additive; `prunable` parsing already existed). New tests: 7 in `GrayMoon.Agent.Tests/InspectWorktreeCommandTests.cs` (clean worktree, untracked/staged/unstaged counts, missing folder, locked worktree, no-upstream-ahead-of-default) and 2 in `GrayMoon.Common.Tests/GitWorktreePorcelainParserTests.cs` (locked with/without reason, a repo with no linked worktrees for the regression guard). Not called from the App yet; that is A2's scope.
- 2026-10-01 DEC-1 DECIDED Owner - Docker deployment (App in a container, Worker on the host) is supported for worktrees v1: Desktop is an addon-wrapper around the webapp. Agent-side disk checks (A1/A2) are required at full severity and the Docker test stays in GATE-2.
- 2026-10-01 A2 DONE Claude Sonnet 5 - `WorkspaceFeatureOperations.AnalyzeRemoveFeatureAsync` and `WorkspaceExternalWorktreeOperations.AnalyzeExternalWorktreeCleanupAsync` now get worktree existence (and, for external cleanup, dirty state) from the Agent's `InspectWorktree` command via `IAgentBridge.SendCommandAsync` instead of `Directory.Exists`. A new `RemoveFeatureRepositoryPlan.WorktreeStatusUnknown` / `ExternalWorktreeCleanupPlan.WorktreeStatusUnknown` field distinguishes Unknown (Agent unreachable or InspectWorktree failed) from Missing (Agent confirms the folder is gone): Unknown makes `IsAutomaticallySafe` false, blocks `RemoveFeatureAsync` outright (new check before the existing safety-flag check), and the Remove Feature dialog shows "Could not check this repository. Make sure the Worker is running, then try again." with Remove disabled and the discard/force checkboxes hidden. For external-worktree cleanup, an old Worker ("Unknown command") or an offline Worker keeps non-forced cleanup working exactly as before (Git itself refuses a dirty removal without `--force`); an explicit force/discard request in that state is refused with "Update the Worker to use this." instead of being honored blindly. A new architecture test (`FeaturesDiskAccessArchitectureTests`) scans `App/Services/Features/*.cs` and fails on any `Directory.`/`File.`/`Path.Exists(` call, allow-listing only `WorkspaceContextPathResolver.cs` (pure path-string building). `RemoveFeatureWorkspaceRefreshTests` no longer creates real directories on the App host; every test that reaches worktree-existence logic now mocks `InspectWorktree`. New tests: 3 in `RemoveFeatureWorkspaceRefreshTests` (Agent disconnected is Unknown and blocks Remove; Agent says missing keeps existing Missing behaviour; Agent says exists+dirty for a path that does not exist on the test machine, proving Docker correctness) and 4 in new `ExternalWorktreeCleanupTests` (dirty from Agent, Unknown when disconnected, non-forced cleanup still works against an old Worker, forced cleanup against an old Worker is refused). `GrayMoon.App.Tests` 456/456 (448 baseline + 1 architecture test + 3 + 4 new). No Agent- or Common-side code changed; `GrayMoon.Common.Tests` 181/181 unaffected. Build 0 warnings.
- 2026-10-01 A3 DONE Claude Sonnet 5 - `WorkspaceFeatureOperations.AnalyzeRemoveFeatureAsync` now reads `OutgoingCommits` (ahead of upstream), `HasUpstream` and the new `AheadOfDefault` field live from the Agent's `InspectWorktree` response (already carried by A1; the App DTO `InspectWorktreeAgentResponse` and `WorktreeDiskStatus` gained the three fields, and the App now sends `defaultBranch` in the request) instead of the stale `WorkspaceRepositoryContextState.OutgoingCommits` database value. Before classifying, it refreshes pull request state once for the Feature's own repos through `WorkspacePullRequestService.RefreshContextPullRequestsAsync` (changed to return the per-repository `PullRequestRefreshOutcome`, same persistence behaviour as before, just now observable); any `Failed` outcome sets a new `RemoveFeaturePlan.PullRequestStatusUnknown` flag. `IsAutomaticallySafe` now requires `OutgoingCommits == 0` exactly (a null count, meaning unknown, is never treated as zero) and requires `PullRequestStatusUnknown` to be false. The "Completed" classification rule (`IsPrMergedOrNeverCreated`) now also requires `AheadOfDefault == 0` for the never-opened-PR bucket, so a repo with live pushed commits and no pull request yet is classified Active, not Completed. `RemoveFeatureModal.razor` shows a distinct headline ("Some repositories have commits that are not in a pull request yet.") for that Active-with-no-PR case, and a new "Could not check pull requests" callout when `PullRequestStatusUnknown` is true (Remove stays enabled through the existing discard/force checkboxes; only Unknown disk state from A2 disables it). `WorkspaceExternalWorktreeOperations.cs` needed a one-line compile fix for the extended `WorktreeDiskStatus.Known` signature (not otherwise changed; A3 does not touch its live-fact usage). New tests in `RemoveFeatureWorkspaceRefreshTests`: live commits ahead of default with no PR is Active and not safe; a null live ahead count is not safe even with a Completed classification; a PR refresh forced to fail via `IGitHubRateLimitTracker.PauseUntil` (no network call needed) sets `PullRequestStatusUnknown` and is not safe; an old-Worker-shaped InspectWorktree response (no ahead fields) still deserializes and is treated as unknown, not zero. `GrayMoon.App.Tests` 460/460 (456 baseline + 5 new, 1 redundant test removed after discovering the test harness's token-less GitHub connector always resolves "no pull request" live, same as before this unit). No Agent- or Common-side code changed. Build 0 warnings. This code path never runs for the special Workspace (`AnalyzeRemoveFeatureAsync` already refuses `IsSpecialWorkspace`), so no Workspace characterization test was needed.
- 2026-10-01 B1 DONE Claude Sonnet 5 - `WorkspaceFeatureOperations.RemoveFeatureCoreAsync` now deletes the removed Feature context's own `ProjectDependencies`, `WorkspaceProjects` and `WorkspaceFileLineStatuses` (new private `DeleteContextScopedProjectDataAsync` helper, `ExecuteDeleteAsync`, no reliance on SQLite foreign-key cascade) before dropping the `WorkspaceFeatureContexts` row, guarded by `context.Kind == WorkspaceFeatureContextKind.Feature` so this never runs for the special Workspace context. New test file `RemoveFeatureProjectDataTests.cs`: seeds two Feature contexts each with their own `WorkspaceProject`/`ProjectDependency`/`WorkspaceFileLineStatus` rows and removes one, asserting its rows are gone and the other Feature context's rows plus the special Workspace context row are untouched; a second test repeats this with `PRAGMA foreign_keys = OFF` on the test database to prove the delete does not depend on cascade (as an upgraded real database might lack the physical constraint). Both tests were confirmed to fail for the right reason (orphaned rows left behind) before the fix. `GrayMoon.App.Tests` 462/462 (460 baseline + 2 new). No Agent- or Common-side code changed. Build 0 warnings. `RemoveFeatureCoreAsync` is only ever reached for a Feature context (`RemoveFeatureAsync` already refuses `IsSpecialWorkspace` before calling it), so the Workspace guard here is defence in depth rather than a reachable path; the Workspace-row check in the new test (the context row itself, not reachable project data) stands in for the Part A4 step 0 characterization test.
- 2026-10-01 B2 BLOCKED Claude Sonnet 5 - stopped before writing the migration step. Confirmed by a throwaway test that dumped `sqlite_master.sql` and `pragma_foreign_key_list` for a brand-new `EnsureCreated()` database (then deleted the test): `WorkspaceProjects` already gets `FOREIGN KEY ("WorkspaceFeatureContextId") REFERENCES "WorkspaceFeatureContexts" ... ON DELETE CASCADE` from the current EF model (`AppDbContext.cs`), but `WorkspaceFileLineStatus` has no navigation property and no Fluent API configuration for that column at all, so a fresh database has zero foreign key on `WorkspaceFileLineStatuses.WorkspaceFeatureContextId`. The unit's step 2 says to rebuild both tables with that FK "matching the exact constraints EF produces for a fresh database", but for `WorkspaceFileLineStatuses` a fresh database produces no such constraint, so that instruction and the required schema-parity test (fresh vs upgraded `sqlite_master.sql` identical) contradict each other unless the EF model itself also gains the missing relationship, which is a model/schema decision outside this unit's Touches list. No files changed other than this plan's tracker and changelog.
- 2026-10-01 B2 DONE Claude Sonnet 5 - owner decided the FK-scope question (WorkspaceProjects gets the FK rebuild, WorkspaceFileLineStatuses gets orphan cleanup only, matching the owner's instruction that this is true schema parity with today's EF model). Added strict step version 2 to `Migrations.cs`/`Migrations.Features.cs`: `MigrateFeatureContextOrphanCleanupAndWorkspaceProjectsForeignKeyAsync` deletes orphan `ProjectDependencies`, `WorkspaceProjects` and `WorkspaceFileLineStatuses` rows (context id set but missing from `WorkspaceFeatureContexts`; null context id rows are never touched), logs the counts at Information, then rebuilds `WorkspaceProjects` with the `WorkspaceFeatureContextId` foreign key to match a fresh `EnsureCreated()` database (column list and all four foreign keys copied verbatim from a dumped fresh schema), running only when that foreign key is missing. Discovered during implementation: SQLite cascade-deletes every row of a table's children when the table itself is dropped and a child's `ON DELETE CASCADE` foreign key still names it, and this happens even though the plan's documented `PRAGMA foreign_keys=OFF` is attempted, because that pragma is a no-op once a transaction is open (which `Migrations.RunStrictStepAsync` already has by the time a step's action runs). Confirmed with a throwaway experiment test (deleted after use) that dropping `WorkspaceProjects` the naive way silently wiped every `ProjectDependencies` row. Fixed by rebuilding `ProjectDependencies` first to retarget it at the new `WorkspaceProjects_new` table before the old `WorkspaceProjects` table is dropped (so no live foreign key still points at the table being dropped), then letting SQLite's automatic foreign-key-text rewrite on the final rename restore `ProjectDependencies`' reference to the live `WorkspaceProjects` name; both tables' original indexes are recreated afterwards and `pragma_foreign_key_check` is asserted empty for both. New test file `WorkspaceFeatureContextOrphanCleanupTests.cs`: orphan rows (null/valid/orphan context id) are cleaned up correctly across all three tables and `ProjectDependencies`; the foreign key rebuild preserves row ids and recreates every original index; `WorkspaceFileLineStatuses` keeps zero foreign keys after the step (matching a fresh database); running the step twice is a no-op the second time; schema parity between a fresh database and the upgraded 0.1.0 fixture (full `WorkspaceProjects` table definition plus foreign key lists for both tables); the Workspace dependency graph (projects and edges) on the 0.1.0 fixture is unchanged after the step. One pre-existing test (`MigrationsRunnerTests.RunAllAsync_is_idempotent_...`) updated to compute the expected post-migration `user_version` from `Migrations.StrictSteps` instead of hardcoding `LegacyBaselineVersion`, since that list is no longer empty; this test is unrelated to B2's own logic and was only asserting a version number that changed because a strict step now exists, exactly as U0-2's own notes anticipated. `GrayMoon.App.Tests` 468/468 (462 baseline + 6 new). Build 0 warnings.
- 2026-10-01 B3 DONE Claude Sonnet 5 - added `WorkspaceFeatureContextId?`-scoped overloads for `WorkspaceProjectRepository.GetByWorkspaceIdAsync`, `GetDependencyEdgesAsync`, `GetPackageDependencyLinesByRepoAsync`, `GetPackageDependencyLinesForRepoAsync`, `GetMismatchedDependencyLinesForRepoAsync`, `GetDependencyGraphAsync` and `GetRepositoryDependencyGraphAsync`, alongside (never replacing) the existing unscoped methods, so every other caller not named by this unit keeps its exact legacy behaviour. The special Workspace filter is `WorkspaceFeatureContextId IS NULL OR == <specialContextId>` (plus the existing `IsGenerated` carve-out); a Feature's filter is `== <featureContextId>` only. `GetMismatchedDependencyLinesForRepoAsync`'s new overload compares a Feature repo against its own `WorkspaceRepositoryContextState.GitVersion` (null, never the shared link's value, when no context-state row exists yet), via a new `GetRepositoryVersionMapAsync` helper. Updated the three named callers (`WorkspaceGitService.Restore.cs`'s two Restore methods, `WorkspaceDependencies.razor`, `WorkspaceRepositories.Loading.cs` grid tooltips) to pass their already-resolved context id. New `WorkspaceProjectRepositoryContextScopingTests.cs` (15 tests): 8 cross-context isolation tests (Workspace vs Feature, including the legacy-null-row fallback applying only to the special Workspace) and 7 "pin the Workspace first" characterization tests (one per method, no Features seeded) proving the new scoped call matches the old unscoped call exactly. Added a Desktop README bullet. `GrayMoon.App.Tests` 483/483 (468 baseline + 15 new), run twice. Build 0 warnings. Filed one Discovered issue (Part F) about a pre-existing, out-of-scope fallback-to-shared-version bug in `DependencyStats.cs` found while reading `feature-context-scoping.mdc`.
- 2026-10-01 B4 DONE Claude Sonnet 5 - closed the four remaining context leaks named in F-8/U-11/04 P1-7: `GetPushPlanPayloadAsync`'s tag exclusion, the notification panel's `repoIdsThatNeedPush`, `IWorkspaceFileOperations.ListAsync`, generated packages on the Packages page, and the bulk Branch modal's checkout now pre-checking `WorkspaceBranchOccupancyService` and skipping occupied repos instead of failing mid-run. See the Part C notes on this row for the full file-by-file breakdown. `GrayMoon.App.Tests` 496/496 (483 baseline + 13 new). Build 0 warnings. Appended " Done (B4)." to the P1-7 Fix cell in `04-worktree-v1-release-roadmap.md`.
- 2026-10-01 B5 DONE Claude Sonnet 5 - `GitHubRepositoryRepository.MergeRepositoriesAsync` PHASE E now splits candidates for deletion (no longer returned by the provider) into those with no `WorkspaceFeatureRepositories` row through their link (deleted exactly as today) and those with one (kept; a new `KeptRepositoryForFeaturesInfo` list on `MergeRepositoriesResult`, logged at Warning). `ConnectorRepository.DeleteAsync` now refuses with "Remove the Features that use this connector's repositories first." and deletes nothing when any repository of the connector has a `WorkspaceFeatureRepositories` row; the existing delete-modal error handling in `Connectors.razor` already surfaces a thrown message with no code change needed there. `KeptForFeatures` flows through `RefreshRepositoriesResult`/`GitHubRepositoryService.RefreshRepositoriesAsync` to a new one-line dismissible notice on `Repositories.razor` (the page that actually shows refresh results; `Connectors.razor` only shows delete errors). New `ConnectorRepositoryFeatureProtectionTests.cs` (4 tests): two Workspace-only characterization tests per A4 step 0 (refresh deletes an unreturned repository as today; connector delete with no Feature usage deletes as today) plus the two new behaviours (refresh keeps a Feature-used repository and reports it; connector delete with a Feature-used repository is refused and nothing is deleted). `GrayMoon.App.Tests` 500/500 (496 baseline + 4 new). Build 0 warnings. Rename detection (PHASE A-C) was not touched, so renamed repositories keep updating their row, link and Feature rows in place as before.
- 2026-10-01 B6 DONE Claude Sonnet 5 - `WorkspaceRepository.UpdateAsync` now checks whether the name or root path actually changes before any write, and refuses with "Rename and root changes are not possible while Features exist. Remove Features first." up front when the Workspace has Features, instead of saving the rename and only then failing on membership. The rename/root save and the membership change now share one transaction: `ReplaceRepositoriesAsync` was split into a thin transaction wrapper and a new `ReplaceRepositoriesCoreAsync` (no transaction of its own) that `UpdateAsync` calls inside its own transaction, so a membership failure rolls back the rename too. `ReplaceRepositoriesCoreAsync` now compares the requested repository set (after invalid-id filtering) against the current set and only throws the membership message when they actually differ; an unchanged set (the common case of saving a Workspace with Features when only unrelated fields are touched, or saving the exact same repository list) is a no-op and never blocked. `Workspaces.razor`'s Edit Workspace dialog now resolves "has Features" via the existing `IWorkspaceFeatureOperations.ListFeaturesAsync` facade (no new repository method) and shows the name field read-only with the refusal reason as a hint (`WorkspaceModal.razor` gained a `NameDisabledHint` parameter rendered under the name input). Deleted the unused `WorkspaceFeatureOperations.EnsureNoFeaturesBeforeMembershipChangeAsync` (confirmed never called, not on the `IWorkspaceFeatureOperations` interface). New tests in `WorkspaceRepositoryReplaceTests.cs` (5): a Workspace-without-Features characterization test (A4 step 0: rename, root change and membership change together, kept green) plus the unit's four Workspace-with-Features cases (unchanged save succeeds; rename refused and nothing saved; root change refused and nothing saved; membership change refused and the name stays unchanged). Verified the 3 partial-save tests fail against the pre-fix code for the exact described reason (temporarily restored the old `UpdateAsync`/`ReplaceRepositoriesAsync` logic, confirmed 3 failures with the right messages/values, then restored the fix) before confirming green. `GrayMoon.App.Tests` 505/505 (500 baseline + 5 new), run twice after a clean rebuild. Build 0 warnings. Added a Desktop README bullet (user-visible: Edit Workspace behaviour change while Features exist).
- 2026-10-01 D1 DONE Claude Sonnet 5 - `GitService.RemoveWorktreeAsync` now follows a successful (or already-unregistered) `git worktree remove` with its own residue cleanup: a custom recursive walk (never `Directory.Delete(path, true)`) that clears read-only attributes, deletes reparse points as the link itself without entering them, and retries each entry up to 5 times (200/400/800/1600/3200 ms), only when every one of the unit's 5 safety guards passes (`internal static GitService.ValidateResidueRemovalGuards`); otherwise it deletes nothing and reports why. New optional request fields `featureRootPath`/`featureStorageRoot` gate all deletion; an old App that sends neither gets today's behaviour exactly (residue only reported, never removed). Discovered empirically with real git on Windows: `git worktree remove` unregisters the worktree from `git worktree list` even when the final directory delete fails (for example a file still open elsewhere, which exits non-zero); the method now decides success by re-checking the worktree list after the git call, not by its exit code alone, so a locked file is reported as an honest residue instead of a bare git failure. `CreateWorktreeAsync` now allows creating into an existing, empty folder. New `RemoveGitWorktreeResponse` fields `residueRemaining`/`residueFileCount`/`residueSampleFiles`/`residueMessage`, new `Agent/Models/WorktreeResidueResult.cs`. New `RemoveWorktreeResidueTests.cs` (15 tests, `GrayMoon.Agent.Tests` 187/187, re-run 3 times for determinism); `GrayMoon.App.Tests` 505/505 unaffected (no App files touched; D1 is Agent-only). Build 0 warnings.
- 2026-10-01 D1 follow-up fix (CI failure) Claude Sonnet 5 - the Linux GitHub Actions runner reported 2 failures in `RemoveWorktreeResidueTests.cs` that never showed up on Windows. Root cause for both was the test fixture, not production code: (1) `Junction_inside_worktree_is_removed_without_entering_it_and_target_survives` shelled out to `cmd.exe /c mklink /J`, which does not exist on Linux; `GitService`'s residue walk already detects links generically via `FileAttributes.ReparsePoint`, which .NET also sets for Linux symlinks, so the production code needed no change. `CreateJunctionAsync` now branches on `OperatingSystem.IsWindows()`: the Windows path is unchanged (mklink /J), and the Linux path calls `Directory.CreateSymbolicLink` to create a directory symlink instead, exercising the exact same reparse-point skip-and-report code path on both platforms with no loss of coverage. (2) `Locked_file_produces_residue_remaining_with_sample` opened a file with `FileShare.None` and asserted the delete failed; on Linux a process can unlink a file it still has open (the inode survives until the last handle closes), so the delete silently succeeded and `ResidueRemaining` came back false. The locked file now lives in its own `locked-dir` subfolder; on Windows the test still opens the file exclusively, and on Linux it instead removes the write permission bit on `locked-dir` with `File.SetUnixFileMode` (restored in a `finally`), since Unix requires directory write access to unlink an entry, producing a genuine platform-equivalent delete failure with the same assertions (1 residue file, sample contains "locked.bin"). Both are real OS-semantics differences, not test bugs or production bugs; no change to `GitService.cs`. Verified locally on Windows: `GrayMoon.Agent.Tests` 187/187 (one unrelated, pre-existing flaky test, `GitStatusRefreshCoordinatorTests.Cancelled_owner_scan_does_not_leave_repository_stuck`, failed once in the full run and passed alone, matching the known-flaky note in A4); `GrayMoon.Common.Tests` 181/181; `GrayMoon.App.Tests` run showed 29 failures, all pre-existing from unrelated uncommitted work-in-progress already present in this working tree before this session (D2-area files), unaffected by this change. Could not run the Linux CI from this Windows machine; the owner should confirm the next CI run is green.
- 2026-10-01 D2 DONE Claude Sonnet 5 - added `Removing = 3` and `Removed = 4` to `WorkspaceFeatureRepositoryState` (no schema change). Rewrote `WorkspaceFeatureOperations.RemoveFeatureCoreAsync`: every live row (not already `Removed`) moves to `Removing` in the same save as the Feature's own `Removing` state; a row already `Removed` from an earlier partial remove is skipped by both `AnalyzeRemoveFeatureAsync` and `RemoveFeatureCoreAsync`, so a retry issues zero Agent calls for it. After a repo's worktree is unregistered, that row is saved as `Removed` at once with its own short-lived `IDbContextFactory<AppDbContext>` context, so a crash mid-Feature keeps already-finished repos finished. Passed the D1 `featureRootPath`/`featureStorageRoot` fields (resolved with `WorkspaceContextPathResolver`, falling back to null on resolution failure) into the Agent's `RemoveGitWorktree` call, and read the response's residue fields back out of `AgentCommandResponse.Data` with a small private DTO. Local branch delete is now categorized into `RemoveFeatureBranchOutcome.NotApplicable` (tag-pinned repo), `Deleted`, `KeptUnmerged` (non-force refusal; Git's own reason is unmerged commits), or `Failed` (including the case where repository details cannot be resolved, which used to be silently skipped) - never only logged. A failed repo's row stays `Removing` with its error in `LastError`; the Feature's own lifecycle state still becomes `NeedsRepair`. The final row delete changed from `RemoveRange(rows)` to an `ExecuteDeleteAsync` filtered by context id alone, so an already-`Removed` row left out of the in-memory list is still purged once the whole Feature succeeds; the bulk delete bypasses the change tracker, so `rows` are explicitly detached afterwards to stop EF from cascade-deleting them a second time when the context row is removed (root cause of an initial `DbUpdateConcurrencyException` during testing). `OperationResult` gained a `RemoveFeatureReport` property (new `RemoveFeatureRepositoryReport` record, new `RemoveFeatureBranchOutcome` enum, both in `GrayMoon.Application`). No existing stop/unwatch-by-context-id mechanism existed for Git Changes monitoring (it was tracked per-workspace only), so added a new minimal `IWorkspaceGitChangesMonitoringPause` (ref-counted per context id, `GrayMoon.App/Services/GitChanges/WorkspaceGitChangesMonitoringPause.cs`) and a skip check in `GitChangesMonitoringBackgroundService`'s per-context sweep loop; `RemoveFeatureCoreAsync` takes the pause as a `using` at the top so it always releases, including on cancellation. `RemoveFeatureModal.razor` now shows the removal report (a plain-language headline plus a warning list for anything kept or left over) with a single Done button, instead of closing immediately on success. New `RemoveFeatureReportTests.cs` (6 tests): branch-delete failure reported as kept (not only logged); Agent-reported residue appears in the report; one of three repos failing leaves the other two `Removed` and the failed row `Removing` with its `LastError` set, Feature `NeedsRepair`; a second Remove skips an already-`Removed` row with zero Agent calls for it; Workspace monitoring stays active while a Feature is removed; a failed remove restarts the Feature's own monitoring. Updated one pre-existing test (`RemoveFeatureWorkspaceRefreshTests.Remove_partial_worktree_failure_still_cleans_successful_repos_and_keeps_Feature`) whose assertion matched the now-superseded row-level `NeedsRepair` behaviour. Confirmed no exhaustive switch over `WorkspaceFeatureRepositoryState` exists anywhere (`rg -n "WorkspaceFeatureRepositoryState\."` reviewed; only direct `==`/`!=` comparisons). `GrayMoon.App.Tests` 511/511 (505 baseline + 6 new), run twice after a clean rebuild. Build 0 warnings. Added a Desktop README bullet.
- 2026-10-01 D3 DONE Claude Sonnet 5 - `RemoveFeatureModal.razor`'s discard and force checkboxes no longer both appear whenever the plan is merely "not automatically safe" (U-17: ticking only one of them when the real risk was the other used to enable Remove and then fail with a raw Git error). The discard checkbox ("Permanently discard uncommitted changes in N repositories", with the affected repositories listed) now shows only when at least one repository is actually dirty (`HasUncommittedChanges`/`HasStagedChanges`/`HasConflicts`); the force checkbox ("Delete local branches that have commits not in the default branch (N)") shows only when at least one repository has live `AheadOfDefault > 0` and no merged pull request. Remove's enable rule is now "every checkbox the dialog actually shows must be ticked, and never while any repository's disk state is Unknown" (A2), extracted into a small `internal static CanRemove` helper on the component together with `IsRepositoryDirty` and `HasUnmergedBranchAheadOfDefault`, following the existing `MergePullRequestModal.BuildChecksActionsUrl` convention for unit-testing razor logic directly from App.Tests. Added "Local branches are deleted. Remote branches are kept." under the repository removal list (fixes U-19). New `RemoveFeatureModalCheckboxTests.cs` (20 tests): dirty detection (uncommitted/staged/conflicts/clean), ahead-of-default-without-merged-PR detection (including the null-count-is-unknown case), and an 11-case `CanRemove` combination theory covering every shown/ticked permutation plus Unknown disk state always blocking regardless of checkbox state. `GrayMoon.App.Tests` 531/531 (511 baseline + 20 new). `GrayMoon.Common.Tests` 181/181 unaffected (no Agent or Common code touched). `GrayMoon.Agent.Tests` 187/187 unaffected. Build 0 warnings. No Workspace characterization test needed: the Remove Feature dialog is reachable only for a Feature context, never the special Workspace.
- 2026-10-01 D5 DONE Claude Sonnet 5 - a locked worktree (A1's existing `IsLocked`/`LockReason` from `InspectWorktree`) is now explained in the Remove dialog instead of only surfacing as a raw Git error from a failed remove. `RemoveFeatureRepositoryPlan` gained `IsLocked`/`LockReason` (sourced through `WorktreeDiskStatus` and the App's `InspectWorktreeAgentResponse`, both of which gained the same two fields; an old Worker's response has neither, which deserializes to "not locked"), and `IsAutomaticallySafe` now also requires `!p.IsLocked`. `RemoveFeatureModal.razor` shows "Unlock and remove N locked worktrees" (listing each locked repository and its reason) only when at least one repository is locked, gated by the D3 `CanRemove` helper, which now also takes `showUnlockCheckbox`/`allowUnlock`; a new `HasLockedWorktree` static helper matches the existing `IsRepositoryDirty`/`HasUnmergedBranchAheadOfDefault` pattern. `RemoveFeatureOptions` gained `AllowUnlockWorktrees`, which `RemoveFeatureAsync`'s safety gate and `RemoveFeatureCoreAsync` both honour; the latter forwards it as a new optional `unlock` field on the Agent's `RemoveGitWorktreeRequest`. `GitService.RemoveWorktreeAsync` (and `IGitService`) gained an `unlock` parameter (default false) that runs `git worktree unlock <path>` before `git worktree remove` only when true; a failed unlock is logged and the remove is attempted anyway. External-worktree cleanup (`WorkspaceExternalWorktreeOperations.cs`) was not touched and still never sends `unlock` (R-D5). New tests: `RemoveFeatureModalCheckboxTests.cs` gained 2 `HasLockedWorktree` facts and 4 new `CanRemove` combinations for the unlock checkbox (26 test cases total, up from 20); `RemoveFeatureReportTests.cs` gained 3 tests (a locked repo appears in the plan with its reason and is not automatically safe; an old-Worker-shaped response with no `isLocked` field is not locked; `AllowUnlockWorktrees` is forwarded as `unlock` on the `RemoveGitWorktree` command); `RemoveWorktreeResidueTests.cs` gained 2 real-git tests (a locked worktree is removed when `unlock = true`; without it, remove fails and the worktree is still registered and locked) plus one Worker-compatibility assertion added to an existing test, that an old request JSON with no `unlock` field deserializes to `false`. `GrayMoon.Common.Tests` 181/181 unaffected (no Common code touched). `GrayMoon.App.Tests` 540/540 (531 baseline + 6 in `RemoveFeatureModalCheckboxTests.cs` + 3 in `RemoveFeatureReportTests.cs` = 9 new). `GrayMoon.Agent.Tests` 189/189 (187 baseline + 2 new). Build 0 warnings. Added a Desktop README bullet. No Workspace characterization test needed: the Remove Feature dialog, `RemoveFeatureOptions` and `RemoveWorktreeAsync`'s own guards are unchanged for the special Workspace; `git worktree lock`/`unlock` behave identically on Windows and Linux, so no OS-specific guard was needed for the new Agent tests.
- 2026-10-01 I1 DONE Claude Sonnet 5 - Remove Feature's analysis now judges the Feature branch itself (09 SB-2), not whatever happens to be checked out. Agent: `InspectWorktree` gets an optional `featureBranch` request field and 5 nullable response fields (`featureBranchExists`/`featureBranchSha`/`featureBranchAheadOfDefault`/`featureBranchHasUpstream`/`featureBranchAheadOfUpstream`), computed purely from `git rev-parse`/`rev-list`/`for-each-ref` against `refs/heads/<featureBranch>` (new `GitService` helpers `ProbeAheadOfDefaultForRefAsync`, `ProbeFeatureBranchUpstreamCountAsync`, `GetRevisionShaAsync`), with nothing checked out and all fields null when `featureBranch` is absent. App: `AnalyzeRemoveFeatureAsync` sends `featureBranch = info.FeatureName` for non-tag-pinned rows only; each `RemoveFeatureRepositoryPlan` gains `FeatureBranchName`/`CheckedOutBranch`/`IsOffFeatureBranch` plus the 4 live Feature-branch facts, and two computed properties `EffectiveAheadOfDefault`/`EffectiveOutgoingCommits` that fall back to the old checked-out-branch fields whenever `FeatureBranchName` is null, so tag-pinned repos and every hand-built test plan keep their exact pre-existing meaning with zero changes. `IsAutomaticallySafe`, `IsPrMergedOrNeverCreated` and the D3 unmerged-branch checkbox (`HasUnmergedBranchAheadOfDefault`) now read the Effective* fields instead of the checked-out branch's own counts. `RemoveFeatureModal.razor` explains drift per repository ("This repository is on `<X>`, not on its Feature branch `<name>`. `<X>` is kept; `<name>` is deleted." / "The Feature branch is already gone; nothing to delete."), the force checkbox now lists each affected Feature branch by name with its own unmerged commit count, and the D2 report gained a `KeptBranchName` field surfaced as a new warning line ("`<repo>`: `<branch>` kept (this repository was not on its Feature branch).") computed in `RemoveFeatureCoreAsync` from the already-computed `RemoveFeaturePlan` (no extra git probing at remove time). An old Worker (missing Feature-branch fields, all null) makes that repository not automatically safe and hides the force checkbox for it, so Git's own non-force refusal keeps an unmerged branch and D2 reports it kept, exactly per the unit's step 6. `GrayMoon.Common.Tests` 181/181 unaffected (no Common code touched). `GrayMoon.App.Tests` 545/545 (540 baseline + 5 new in `RemoveFeatureWorkspaceRefreshTests.cs`: drift with an unmerged Feature branch is not safe and names the kept branch; drift with a merged Feature branch stays safe; an old Worker without Feature-branch fields is not safe and the force checkbox is not offered; a not-drifted repository produces the identical plan as before, A4 step 0 - plus 1 new in `RemoveFeatureReportTests.cs` for `KeptBranchName` flowing through an actual Remove). Three existing test files' `CleanInspectWorktree()`-style helpers were updated to default every canned `InspectWorktree` response to "no drift" (mirroring the seeded Feature's own name and the existing ahead/upstream values into the new `featureBranch*` fields), so none of their pre-existing assertions silently broke by now looking drifted; `RemoveFeatureModalCheckboxTests.cs` needed no change, since its hand-built plans never set `FeatureBranchName` and the Effective* fallback keeps them reading the old fields. `GrayMoon.Agent.Tests` 194/194 (189 baseline + 5 new in `InspectWorktreeCommandTests.cs`: Feature branch ahead of default while the worktree sits on another branch; a missing Feature branch reports `FeatureBranchExists = false`; no `featureBranch` in the request leaves every new field null; an old-shape request JSON and an old-shape response JSON both still deserialize). 2 existing fake `IGitService` test doubles (`ReturnToDefaultBranchCommandTests.cs`, `DeleteBranchCommandTests.cs`) updated for the new interface parameter. Build 0 warnings. `ExternalWorktreeCleanupTests.cs`/`WorkspaceExternalWorktreeOperations.cs` left untouched per the regression guard (that cleanup path never sends `featureBranch`). Added a Desktop README bullet. GATE-2's last dependency is now DONE.
- 2026-10-01 D3 follow-up fix Grok - `RemoveFeatureAsync`'s safety gate no longer demands any discard/force/unlock flag whenever `IsAutomaticallySafe` is false. After D3/A3/I1 that flag covers PR-unknown, null outgoing, live-status-unavailable, Abandoned/Active/NeedsRepair with a clean worktree, and similar cases where the dialog correctly shows no checkboxes and enables Remove; the old AND-of-flags check then failed with "Feature removal is not automatically safe; authorize discard/force options explicitly." The gate now mirrors the dialog: require discard only when dirty, force only when `EffectiveAheadOfDefault > 0` with no merged PR, unlock only when locked (Unknown disk state still blocks outright). New tests in `RemoveFeatureWorkspaceRefreshTests`: empty options succeed for PR-unknown, null-outgoing, live-status-unavailable, and Abandoned-clean; refuse dirty without discard and unmerged-ahead without force.
- 2026-10-02 plan-only (no code) Grok - Owner Remove Feature / Create Feature UX follow-up folded into the plan without implementing UI. **DEC-2** decided Yes for v1: local + remote cleanup checkboxes (default on when remotes exist; hide/explain when never pushed); replaces D3's static "Local branches are deleted. Remote branches are kept." **D4** rewritten for that checkbox UX + lease-based remote delete. New **D6** (Remove dialog polish): all-clean repo status summary, ~70% list height cap, neutral finished-copy (suggested: "This feature is finished and safe to remove."), green/red Remove CTA by safety, loading "Checking feature statusâ€¦" â†’ "Checked x of y", prefer "repository/repositories" in overlays; includes **NOTE: Post-create sync indicators** (explanation only â€” create overlay vs Worker task line vs red NeedsSync badges from hook `SyncCommand`s). New **E5**: structured BranchExists dialog (title X of Y, scrollable repo/ref list, short guidance). Tracker + GATE-4 deps + R1 `features.md` bullet updated. No app code changed.
- 2026-10-02 D4+D6+E5 DONE Grok - Implemented owner UX for Remove/Create Feature: repo status summary + capped list, neutral finished copy, local/remote cleanup checkboxes with lease-based remote delete and bearer token, green/red Remove CTA, analysis progress "Checking feature status..." / "Checked x of y", structured BranchExists dialog. Desktop README updated.
- 2026-10-02 GATE-2 PASSED Owner - owner confirmed Remove Feature state is good; proceed to lane C.
- 2026-10-02 C1 DONE Grok - `CreateFeatureCoreAsync` gathers and validates every repository HEAD (rejects blank SHA) before writing any Feature rows, then saves Feature (`Creating`), Feature context and all `Pending` repository rows inside one database transaction so a failure while building or saving those rows rolls back the Feature and context too. Worktree creation after the commit is unchanged. New `CreateFeatureIntentAtomicityTests` (incomplete heads leave no Feature; path-resolve exception during Pending-row build leaves no Feature/context/repo rows). `SyncStateTestContext.CreateAsync` gained an optional `configureServices` hook so tests can replace `IWorkspaceContextPathResolver`. Desktop README bullet added. `GrayMoon.App.Tests` 571/571. Build 0 warnings.
- 2026-10-02 C1-fix DONE Grok - Owner hit SQLite Error 6 `database table is locked: WorkspaceFeatureContexts` on Create Feature: the C1 transaction called `pathResolver.GetRepositoryPathAsync`, which opens a second `AppDbContext` via `GetRequiredAsync` while `WorkspaceGitChangesWriteQueue` also writes. Fix: build Pending `WorktreePath` from `ManagedFeatureStorageRoot` + Feature name + repo name (`CombineWindowsPath`, same shape as `WorkspaceContextPathResolver`) with no second DbContext inside the transaction; keep the atomic intent save. Rollback test now uses a `SaveChangesInterceptor` instead of a throwing path resolver; added path-shape coverage.
- 2026-10-02 C2 DONE Grok - added `WorkspaceFeatureReconciler` (singleton + hosted service) hooked to `AgentConnectionTracker` Online: interrupts stuck Creating/Removing Features, reconciles worktree inventory via ListGitWorktrees/InspectWorktree, reports untracked worktrees under Feature storage, 60s throttle and single-flight gate. `WorkspaceFeatureReconcilerTests` (18).
- 2026-10-02 C3 DONE Grok - Feature selector lists every lifecycle state with friendly labels; NeedsRepair selectable and opens read-only (toolbar Sync/Push/Update disabled + attention banner). `FeatureSelectorPresentationTests` (9).
- 2026-10-02 C4 DONE Grok - `RepairFeatureAsync`, `RollbackFeatureAsync`, `IsRemoveIncompleteAsync` on `IWorkspaceFeatureOperations`; remove-incomplete refused; repair seeds projections once; rollback non-force and skips default/primary checkout branches. `WorkspaceFeatureRepairTests` (7).
- 2026-10-02 C5 DONE Grok - `FeatureStatusPanel` with Retry / Roll back / Remove / Continue removal; Create Feature NeedsRepair closes modal and opens panel; Feature menu Status and repair; banner Show details.
- 2026-10-02 GATE-3 READY FOR USER TEST Grok - lane C complete; owner runs GATE-3 script.
- 2026-10-03 C5 UX fix Grok - owner feedback on `FeatureStatusPanel`/the NeedsRepair banner during GATE-3 testing: (1) replaced the bordered Bootstrap `alert-warning` "Show details" banner, whose button was effectively invisible, with a `.page-warning-callout` status line (same dark-strip/inset-accent-bar look as the existing error callout, just amber instead of red) and no button; the Workspace header's Sync button is now replaced outright by a visible `btn-warning` **Repair** button (`WorkspaceRepositoriesHeader.razor`) that opens the same Status and repair dialog, instead of merely graying out a disabled Sync button. (2) `FeatureStatusPanel`'s outer `.modal` now drops its z-index to 50 while `_busy` (Retry/Roll back in flight), matching the existing `SwitchBranchModal`/`MergePullRequestModal` pattern, so the page's `BackgroundJobOverlay` (already showing "Repairing feature..."/"Rolling back feature...") visually covers the dialog for the duration instead of the dialog sticking out above it. (3) `modal-dialog-scrollable` added so the repo list scrolls and the dialog never grows taller than the viewport. (4)/(5) when every repository in the action-result list or the persisted-rows list shares the exact same message (e.g. "Worker not connected. Start the GrayMoon Worker to sync repositories." repeated once per repository because the Worker is simply offline), the panel now shows that message once instead of once per repository. Updated the C5 step and summary row above to match. Files: `FeatureStatusPanel.razor`, `WorkspaceRepositoriesHeader.razor`, `WorkspaceRepositories.razor`, `WorkspaceRepositories.razor.css`. Build 0 warnings/0 errors (`dotnet build src/GrayMoon.App/GrayMoon.App.csproj`); no test changes needed (UI-only, no test asserted the removed markup).
- 2026-10-03 C5 UX fix round 2 Grok - further owner feedback: (1) `FeatureStatusPanel` now embeds its own `<LoadingOverlay IsVisible="@_busy" Message="@_busyMessage">` (not page-scoped, same pattern as `Repositories.razor`/`WorkspaceFiles.razor`'s own fetch/update overlays) set to "Repairing feature..."/"Rolling back feature..." the instant Retry/Roll back is clicked, so the user gets feedback immediately instead of waiting for the page's `BackgroundJobOverlay` to notice the structural operation started (the earlier z-index-drop-below-page-overlay fix from round 1 stays, as a second layer once that overlay does catch up). (2) Dropped the literal parentheses around state labels: `WorkspaceFeatureSelector.razor`'s three label sites (dropdown item, cross-workspace switcher item, `FeatureTitle` tooltip) now render "`<name>` â€” Needs attention" (em dash) instead of "`<name>` (Needs attention)". (3) `FeatureStatusPanel`'s header now uses the shared `.modal-title--compound` convention (`.modal-title__primary`/`.modal-title__secondary`) already used by `BranchModal`/`SwitchBranchModal`/`MergePullRequestModal`: "Feature Status" plus the Feature name in small gray text, replacing the ad hoc "`{name}` - status and repair" title. (4) All three Feature dialogs (`CreateFeatureModal` already had this; `RemoveFeatureModal` and `FeatureStatusPanel` did not) now support Esc (closes/cancels, ignored while busy/analyzing) and Ctrl/Cmd+Enter (runs the dialog's primary action) via the existing shared `data-default-action` attribute + global `modal-default-action.js`/`@onkeydown` convention - no new JS. Updated the C5 step above (steps 1, 5-7) to match. Files: `FeatureStatusPanel.razor`, `RemoveFeatureModal.razor`, `WorkspaceFeatureSelector.razor`. `GrayMoon.App.Tests` 103/103 relevant tests passed (`FeatureStatus`/`WorkspaceFeatureRepair`/`FeatureSelectorPresentation`/`RemoveFeature`/`ModalKeyboard` filter); build 0 warnings/0 errors.
- 2026-10-03 C5 UX fix round 3 Grok - round 2's own `LoadingOverlay` embedded directly in `FeatureStatusPanel` turned out to double up with the page's `BackgroundJobOverlay` (both show "Rolling back feature..." at once, as two visibly stacked overlays) since Retry/Roll back are structural operations that already drive that page-scoped overlay. Removed the embedded `<LoadingOverlay>` and the `_busyMessage` field entirely; kept only the dialog's own z-index drop to 50 while `_busy` (below the page overlay's z-index 100), so the single page-level `BackgroundJobOverlay` is the only loading overlay shown, exactly as it already is for `SwitchBranchModal`/`MergePullRequestModal`. Also removed the em dash separator from round 2's "`<name>` â€” Needs attention" per owner feedback (no dash of any kind) - now plain "`<name>` Needs attention" in all three `WorkspaceFeatureSelector.razor` label sites and `FeatureTitle`. Files: `FeatureStatusPanel.razor`, `WorkspaceFeatureSelector.razor`. `GrayMoon.App.Tests` 103/103 relevant tests passed; build 0 warnings/0 errors.
- 2026-10-03 C5 UX fix round 4 Grok - further owner feedback: (1) removed "Status and repair" from the Feature dropdown menu entirely (`WorkspaceRepositoriesHeader.razor`, dead `HandleFeatureStatusClick` deleted) - the dialog (and therefore Roll back) is now reachable only through the `Repair` button, which only renders while the Feature actually `IsReadOnlyContext` (`NeedsRepair`), never for a healthy `Ready` Feature. (2) Round 3's plain-space label fix still read as "too close" in practice (bare leading space characters next to other inline content don't reliably render as a visible gap); switched to an explicit `ms-1` Bootstrap margin class on the label `<span>` in all three `WorkspaceFeatureSelector.razor` sites. (3) `FeatureStatusPanel`'s two error messages now use the shared `.gm-callout`/`.gm-callout--error` convention (same dark-strip/inset-accent-bar look already used by `CreateFeatureModal`/`RemoveFeatureModal`) instead of the old bordered Bootstrap `alert alert-danger` box. (4) Pressing the overlay's Abort button on Repair/Roll back previously surfaced the raw "A task was canceled." from .NET's cancellation plumbing; `WorkspaceFeatureOperations` now has a small `DescribeOperationFailure` helper that turns any `OperationCanceledException` caught in either job's callback into "Repair was cancelled."/"Roll back was cancelled." (5) Root-caused "something is being done when the button is clicked" for Roll back specifically: `RollbackFeatureAsync`'s dirty check (`CollectDirtyReposForRollbackAsync`, one sequential Agent round trip per repository) ran *before* `operationLock.TryStartStructural` - which raises the page's `BackgroundJobOverlay` synchronously - so for a Feature with many repositories the button looked unresponsive for however long that check took, with no overlay yet. Moved the dirty check to run as the first step *inside* the structural-lock callback instead, so "Rolling back feature..." is already on screen before the check starts; `RepairFeatureAsync` had no equivalent pre-check and needed no change. This is a pure timing fix - still exactly one overlay, matching the request not to reintroduce the round-3 double-overlay bug. Files: `WorkspaceRepositoriesHeader.razor`, `WorkspaceFeatureSelector.razor`, `FeatureStatusPanel.razor`, `WorkspaceFeatureOperations.cs`. `GrayMoon.App.Tests` 607/607 (full suite, since `WorkspaceFeatureOperations.cs` is shared); build 0 warnings/0 errors.
- 2026-10-03 C5 UX fix round 5 Grok - owner follow-up on round 4's changelog wording ("one sequential Agent round trip per repository"): `CollectDirtyReposForRollbackAsync`'s per-repository `InspectWorktree` dirty check was indeed a sequential `foreach`, unlike `RepairFeatureCoreAsync`/`RollbackFeatureCoreAsync`'s per-repository loops in the same file. Switched it to the same `SemaphoreSlim(MaxParallel)` + `Task.WhenAll` pattern (results collected into a `ConcurrentBag<(int WrId, FeatureRepairRepositoryResult Result)>`, then `OrderBy(WrId)` before converting to the `IReadOnlyList` the result record expects, for deterministic ordering regardless of which repository's Agent call finishes first) so this pre-check runs concurrently like the rest of Repair/Roll back instead of being the one remaining sequential step. Files: `WorkspaceFeatureOperations.cs`. `GrayMoon.App.Tests` 607/607 (full suite); build 0 warnings/0 errors.
- 2026-10-03 C5 UX fix round 6 Grok - owner report: aborting a New Feature mid-create left it stuck at `Creating` forever - not selectable (`FeatureSelectorPresentation.CanSelect` only allows `Ready`/`NeedsRepair`) and not removable (`ShowRemoveAction` excluded `Creating`), a dead end. Root cause: `CreateFeatureCoreAsync` only turned a *soft* per-repository failure (agent responded but `success:false`) into `NeedsRepair`; any *exception* after the Feature + context + Pending rows already committed - including `OperationCanceledException` from the overlay's Abort button - propagated out as a generic `Condition: "Exception"` result with no DB write, leaving `LifecycleState` at `Creating`. Fixed by wrapping everything from the per-repository `CreateGitWorktree` loop onward in a try/catch: any exception there now marks the Feature `NeedsRepair` (via `CancellationToken.None` for the write, since the ambient token is usually the one that just got cancelled) with a friendly message (`DescribeOperationFailure(ex, "Create feature")`) and returns a `NeedsRepair`-shaped result, so `CreateFeatureModal` opens Status and repair exactly as it already does for a partial per-repo failure - Retry re-attempts the still-`Pending` repos, Roll back/Remove clean up (both already tolerate `Pending` rows with no worktree yet created). Separately, as a safety net for Features already stuck at `Creating` from before this fix (and for the rare case an exception happens before any DB row commits, which still can't be marked): `FeatureSelectorPresentation.ShowRemoveAction` now allows `Creating` (still excludes `Removing`, which already has its own remove in flight) so the dropdown's Remove action is always reachable regardless of how a Feature got stuck; it never required `CanSelect` to be true. Files: `WorkspaceFeatureOperations.cs`, `FeatureSelectorPresentation.cs`. Added `CreateFeatureIntentAtomicityTests.Exception_after_transaction_commit_marks_Feature_NeedsRepair_instead_of_stuck_Creating` and updated `FeatureSelectorPresentationTests.ShowRemoveAction_hides_for_Removing_only`. `GrayMoon.App.Tests` 608/608 (full suite); build 0 warnings/0 errors.
- 2026-10-03 GATE-3 PASSED Owner - owner confirmed Feature recovery (lane C) is good.
- 2026-10-03 E1 DONE Claude Sonnet 5 - new `FeatureNameValidator.Validate` (`GrayMoon.Common/Features/FeatureNameValidator.cs`) replaces the old permissive regex in `WorkspaceFeatureOperations.CreateFeatureAsync`: rejects spaces, control characters, `~ ^ : ? * [ \`, `..`, `@{`, a bare `@`, a leading `-`, a trailing `/` or `.`, `//`, names over 100 characters, any path segment starting with `.` or ending with `.lock`/space/`.`, Windows reserved device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9`, with or without an extension), and the Windows-only characters `< > " |` and shell metacharacters `& ; % ! $` and backtick. The duplicate-name check in `CreateFeatureCoreAsync` is now case-insensitive (`Name.ToLower() == normalizedName`), so `Foo` and `foo` are the same Feature name. `AppDbContext.Features.cs` adds `.UseCollation("NOCASE")` on `WorkspaceFeature.Name` so a fresh database's unique index is case-insensitive from `EnsureCreated()`; new strict migration step 3 (`MigrateFeatureNameIndexCollationAsync` in `Migrations.Features.cs`) recreates `IX_WorkspaceFeatures_WorkspaceId_Name` with `COLLATE NOCASE` on existing databases, skipping (with a warning, never a startup failure) when a case-only duplicate already exists so the new unique index would not fail to create. `CreateFeatureModal.razor` now validates on every keystroke and shows the reason under the Feature name field; New Feature stays disabled while the name is empty or invalid. Regression guard confirmed: the old regex (`BranchNamePattern`) had no other callers (`rg -n "BranchNamePattern"` found none outside this unit before the change); the validator is not called anywhere a Feature is loaded, selected or removed. Tests: `FeatureNameValidatorTests` (Common.Tests, 15 valid + 36 invalid names table-driven); `FeatureNameValidatorGitParityTests` (Agent.Tests, every accepted name from the same table also passes a real `git check-ref-format --branch`); `FeatureNameValidationTests` and `FeatureNameIndexCollationMigrationTests` (App.Tests, invalid-name refusal before any write, case-only duplicate refusal, index recreation / duplicate-skip / idempotency). `GrayMoon.Common.Tests` 234/234 (181 baseline + 53 new), `GrayMoon.App.Tests` 613/613 (608 baseline + 5 new), `GrayMoon.Agent.Tests` 248/248 (194 baseline + 54 new, includes the git-parity theory re-running `TempGitRepositoryFixture` per accepted name). Build 0 warnings. Desktop README bullet added.
- 2026-10-03 E2 DONE Claude Sonnet 5 - new `WorkspaceRepositoryHeaderStateDto.AllFeaturePrsCompleted` computed in `WorkspaceRepositoryLinkListQueryService.GetHeaderStateAsync`'s Feature branch only (`hasAnyPr && !hasOpenPr && !hasCreatablePr`; always `false` for the special Workspace path). `WorkspaceRepositoriesHeader.razor` replaces the old `ShowRemoveFeaturePrimary` rule (`IsFeatureContext && !HasCreatablePr && !HasOpenPr`, which showed "Remove" for a brand-new Feature with zero commits and no pull request at all) with an `internal static DeterminePrimaryAction(bool isFeatureContext, bool hasCreatablePr, bool allFeaturePrsCompleted)` returning a new `HeaderPrimaryAction` enum (`CreatePr`/`Remove`/`Feature`/`Branch`); priority stays Create PR > Remove > Feature/Branch. `WorkspaceRepositories.State.cs` adds `allFeaturePrsCompleted` reading the new DTO field; `WorkspaceRepositories.razor` passes it to the header as a new parameter. Tests: `WorkspaceRepositoriesHeaderPrimaryActionTests` (8: fresh Feature -> Feature, creatable -> Create PR, open PR -> Feature not Remove, all merged/closed -> Remove, creatable wins over Remove even when other PRs are completed, plus the 3 Workspace regression cases from R-E2 - Workspace with creatable PR -> Create PR, otherwise -> Branch, and Branch even if `allFeaturePrsCompleted` were somehow true); 5 new tests in `WorkspaceRepositoryLinkListQueryServiceTests.cs` for the DTO aggregation (false for the Workspace, false with no PR at all, true with only a merged PR, false with an open PR, false when a second repository still has commits outside a PR). `GrayMoon.App.Tests` 626/626 (613 baseline + 13 new). `GrayMoon.Common.Tests` 234/234 unaffected. Build 0 warnings. Desktop README bullet added.
- 2026-10-03 F1 DONE Claude Sonnet 5 - `TokenEncryptionKeyProvider` now generates a per-install key file (`graymoon.key`, next to the database, DPAPI-protected on Windows / mode 600 elsewhere) instead of a hard-coded source-derived key; a configured `TokenKey` still always wins. The old key is kept forever, decrypt-only, under `LegacyKeyId`; new `TokenReencryptionService.ReencryptLegacyTokensAsync` runs once at startup (after the U0-2 migration backup) to move existing tokens onto the current key, never failing startup. New shared `DatabasePathResolver` (Program.cs's own path-parsing logic, extracted so the key provider uses the same rule). `TokenHealthBackgroundService` reports an undecryptable token as "Token needs to be re-entered." Fixed the inaccurate Data-Protection claim in `CLAUDE.md`. `GrayMoon.App.Tests` 670/670 (660 baseline + 10 new); build 0 warnings. Desktop README bullet added.
- 2026-10-03 F3 DONE Claude Sonnet 5 - new `RequestSecurityMiddleware` (`GrayMoon.App/Services/Security/`) blocks cross-site and hub-rebinding requests. For every non-GET request under `/api/` or `/repos/`: a request with no `Origin` header passes unchanged; one with an `Origin` header is rejected (403) unless the Origin's host is the request's own host (also accepting `X-Forwarded-Host` and the new `Security:AllowedOrigins` setting) or a loopback name, and the request also carries `X-GrayMoon-Request: 1` (a cross-site page cannot add that header without a CORS preflight GrayMoon does not allow). For the hubs: `/hub/agent` rejects any request carrying an `Origin` header outright (the Worker's .NET SignalR client never sends one); `/hubs/workspace-sync`, `/hubs/desktop` and `/_blazor` apply the same own-host-or-loopback-or-allowed-origin rule as the REST check, with no `X-GrayMoon-Request` requirement (same-origin negotiate/WebSocket requests from the page itself cannot add custom headers either). All decision logic is pure `internal static` methods on the middleware (`IsRestRequestAllowed`, `IsHubOriginAllowed`, `IsRestPath`, `IsHubPath`) so it is unit-tested directly with `DefaultHttpContext`, without a `WebApplicationFactory`. `rg -n "fetch\("` against `GrayMoon.App/wwwroot` found no first-party JS posting to `/api/` (only vendor bundles), so no caller needed the new header added. The shared `appsettings.json`'s `AllowedHosts` stays `"*"` (Docker/manual installs may be reached by host name or LAN IP); `GrayMoon.Desktop/AppProcessManager.cs` now sets `GRAYMOON_AllowedHosts=localhost;127.0.0.1;[::1]` only for the App process it launches, since Desktop always uses loopback. Documented both rules in a new "33. Local network security" section of `docs/architecture/05-user-capability-reference.md`. New `RequestSecurityMiddlewareTests.cs` (37 tests): path classification, the REST rule (cross-site/same-origin-with-header/same-origin-without-header/loopback/forwarded-host/configured-allowed-origin/malformed-origin), the hub rule (agent hub rejects any Origin, other hubs accept own host and reject foreign), and full `InvokeAsync` pipeline tests per the unit's own Tests-to-add list (GET unaffected even with a foreign Origin, POST with no Origin passes, cross-site POST is 403, same-origin POST with the header passes, each hub's own-origin/foreign-origin/no-origin cases, forwarded-host and configured-allowed-origin hub cases). `GrayMoon.App.Tests` 707/707 (670 baseline + 37 new). `GrayMoon.Common.Tests` 234/234, `GrayMoon.Agent.Tests` 249/249 unaffected (no Agent or Common code touched). `GrayMoon.Desktop.Tests` 185/185 unaffected (no test touches `AppProcessManager`'s launch env vars directly; the one-line addition is covered by the App-side middleware tests proving the setting value's effect). Build 0 warnings. Desktop README bullet added.
- 2026-10-04 I2 DONE Claude Sonnet 5 - new `FeatureBranchPolicy` (`ExpectedBranch`, `IsOffFeatureBranch`). `WorkspaceBranchOccupancyService`'s second loop now emits `BranchOccupancyKind.FeatureOwn` (checkable, no cleanup routing) for the viewing Feature's own branch instead of the blocked `Feature` badge, leaving every other Feature's badge and the worktree-list-matched first loop unchanged. `SwitchBranchModal.razor` gets a `FeatureBranchName` parameter: "This Feature" badge with no delete button on a `FeatureOwn` row, and a warning plus "Return to Feature branch" button when the dialog's repository has drifted off that branch (Workspace dialogs always get `FeatureBranchName = null`, so neither renders there). `WorkspaceRepositories.razor.cs` computes it once per modal open from the selected Feature's name and the dialog repository's own `CheckedOutTag`. Also fixed SB-8: `ShowSwitchBranchModalOnTagsTab` now sets `WorkspaceRepositoryId`/`CurrentBranch` like `ShowSwitchBranchModal` does. New `WorkspaceBranchOccupancyServiceFeatureOwnTests.cs` (2, the Workspace-view test doubles as the A4 step 0 characterization) and `FeatureBranchPolicyTests.cs` (7 theory cases). `GrayMoon.App.Tests` 716/716 (707 baseline + 9 new); `GrayMoon.Common.Tests` 234/234 unaffected. Build 0 warnings. Desktop README bullet added.
- 2026-10-03 I3 DONE Claude Sonnet 5.5 - new `FeatureBranchGuard` enforces `FeatureBranchPolicy.Evaluate` in `WorkspaceBranchOperations.CheckoutAsync`/`CreateBranchAsync`/`ReturnToDefaultAsync` and the bulk checkout (Feature contexts only; the Workspace returns before any Feature query), tag upgrades move a pinned repo's `PinnedTag`, and `SwitchBranchModal` hides New Branch, disables checkout of anything but the Feature branch (tags only when pinned) and adds the shared-branch delete notice in a Feature.
- 2026-10-03 DEC-4 DECIDED Owner - Yes (default): shared Worker secret, no user login.
- 2026-10-03 F2 DONE Claude Sonnet 5.5 - new `WorkerSecretService` (secret in `graymoon-worker.secret` next to the database, regenerated and "seen" reset if the file is missing at startup), `WorkerSecretMiddleware` (after `RequestSecurityMiddleware`) guarding `/hub/agent` and `/repos/{id}/connector`, and `WorkerPairingService` (8-digit one-time code, 10 minutes, single use, discarded after 5 wrong attempts) behind `POST /api/worker/pair`. Wrong secret: 401 (constant-time compare). Missing secret: accepted with one warning per Worker connection while `Security:RequireWorkerSecret` is false and no Worker has proved it has the secret; once one has (`Security:WorkerSecretSeen`, a Settings row, loaded at startup) or the option is on, 401. Connector and pair requests with any `Origin` header: 403. The Worker (`WorkerSecretProvider`: `GRAYMOON_WORKER_SECRET`, then `worker.secret` under `%ProgramData%\GrayMoon`, outside the install folder so updates keep it) sends `X-GrayMoon-Worker-Secret` on the hub connection and the connector request. Install script gets `{SECRET_REQUIRED}` (only a 0/1 flag, never the secret): Desktop installs reuse the file Desktop wrote; a manual install exchanges the pairing code (prompt, or `GRAYMOON_WORKER_PAIRING_CODE`), keeps an existing secret on Enter or when non-interactive (self-update), stops with "Open GrayMoon > Worker and copy a new pairing code." when pairing fails, and refuses to continue without a secret once one is required. Worker page shows the pairing code and, with the status badge tooltip, "Reinstall the Worker to finish securing GrayMoon" while an unsecured Worker is connected. Desktop `WorkerInstaller` gets `--secret-file` (accepted only as a rooted path named `graymoon-worker.secret`), validates the content as a plain token and copies it to the Worker location before running the script. Deviations: the App's secret file is plain text (not DPAPI) so Desktop, same user, can read and hand it over, and the Worker's `Update Now` self-update cannot add a secret to a Worker that has none (it runs non-interactively); only a reinstall does. Tests: `WorkerSecretTests` (37), `WorkerSecretProviderTests` (6, Agent), `WorkerSecretHandoffTests` (14, Desktop). Common.Tests 234/234, App.Tests 774/774, Agent.Tests 255/255, Desktop.Tests 199/199. Build 0 warnings. Documented in `05-user-capability-reference.md` section 33. Desktop README bullet added.

- 2026-10-03 I4 DONE Claude Sonnet 5.5 - a Feature repository that has drifted off its Feature branch now shows an "Off Feature branch" badge in the grid's Branch cell (`WorkspaceRepositoriesRow.razor`, new `OffFeatureBranchName` parameter) with a tooltip naming the current branch/tag/detached commit; clicking it opens the existing Switch Branch dialog and its I2 "Return to Feature branch" button. Drift comes from new pure `FeatureBranchPolicy.GetOffFeatureBranch`, evaluated per row by `WorkspaceRepositories.razor.cs` `GetOffFeatureBranchName` from data already loaded (no Agent calls, null outside a Feature). Added `FeaturePinnedTag` to `WorkspaceRepositoryLinkListItemDto`, the mapper and `WorkspaceRepositoryLink` (NotMapped), filled from the Feature join the query service already does; `GetSwitchBranchModalPinnedTag` now reads it instead of `CheckedOutTag` so a terminal tag checkout in an unpinned repo counts as drift. Tests: 10 theory cases in `FeatureBranchPolicyTests` and 1 in `WorkspaceRepositoryLinkListQueryServiceTests` (Feature rows carry the pinned tag, Workspace rows do not). The reconciler test for "registered at path but on another branch stays Ready" already existed from C2. `GrayMoon.App.Tests` 785/785. Build 0 warnings. Desktop README bullet added.

- 2026-10-04 I4 ad-hoc tweak Claude Sonnet 5.5 - owner confirmed I4 works on build .127 and asked for the badge text in lower case like other badges: "Off Feature branch" is now "off feature branch" in `WorkspaceRepositoriesRow.razor` and the Desktop README bullet. Text only; no logic or test changes. No bundle published (the owner tests CI builds).
- 2026-10-04 SB-list ad-hoc fix Claude Sonnet 5.5 - Switch Branch Locals for a non-pinned Feature repository now always lists the Feature branch (`WorkspaceBranchOperations.GetBranchesAsync`, via `FeatureBranchPolicy.ExpectedBranch`, only for Ready/NeedsRepair Feature repositories) even when the repository sits on another branch and the shared RepositoryBranches rows lack it; the Agent already returns every local branch from the Feature worktree (real-git test added). Tradeoff: the App cannot tell cheaply that the branch was deleted, so it stays listed until the Feature is removed. No bundle published.
- 2026-10-04 G1 DONE Claude Sonnet 5.5 - hook writing now follows the owner's DEC-3 decision instead of the plan's detect-and-warn: an existing non-GrayMoon hook is renamed to `<hook>.replaced-by-graymoon` and reported in the Worker log, a `core.hooksPath` outside the Git directory is left alone, and GrayMoon hooks are rewritten only when their content changed. This stops GrayMoon from destroying a team's own hooks while keeping every repo that already carries the GrayMoon marker working unchanged; the renamed hook does not run until renamed back (chaining stays a possible v1.1 item).
- 2026-10-04 flaky-test fix Claude Sonnet 5.5 - Case_only_duplicate_name_is_refused flaked because CreateFeatureAsync (and Remove/Repair/Rollback feature, Remove external worktree) completed its result source inside the structural job, but the runner released the Workspace structural lock only after the job delegate returned, so a fast second call saw WorkspaceBusy instead of DuplicateName; real product race (3/30 failures before). Fix: IWorkspaceLockedOperation.WhenCompleted, and each of those five operations now awaits it after the result so the lock is free when the call returns (0/40 failures after).
- 2026-10-04 flaky-test fix Claude Sonnet 5.5 - Desktop `WingetConsoleScript_ExitsWithoutWaiting_WhenCommandSucceeds` failed once on the CI windows runner (build .130, 12 s, `WaitForExit(10_000)` returned false); not reproducible locally (about 80 ms, 0/20 failures), the code under test was unchanged, so this is a test-only environment/timing flake and the winget console script is unchanged. Most likely cause (not proven): a cold or loaded runner stalling the first launch of a freshly written .cmd past the hard 10 s limit, or a stdin-inheriting `pause` if the stand-in failed. Fix in the test only: stdin is redirected and closed, stdout/stderr are drained asynchronously and included in the failure message, and the limit is 60 s with a process-tree kill (a passing run still ends immediately); the assertions are unchanged (script exits, exit code 0). 0/20 failures after.
- 2026-10-04 R2 DROPPED owner - DEC-6 answered No: Features are part of GrayMoon, so no `Features:Enabled` kill switch; R2 marked DROPPED in the tracker, R4 and R1 adjusted, plain-words and risk-review docs updated.
- 2026-10-04 E6 DONE Claude Sonnet 5.5 - `core.longpaths` pulled into v1 from DEC-8: Agent sets `core.longpaths=true` in the repository's own config on Windows before `git worktree add` and `git worktree remove`; 4 new real-git tests.
- 2026-10-04 docs owner - Added `11-post-release-plans.md` (REST API for Features and the `WorkspaceFeatureOperations` refactor, both v1.1, documentation only); Part F entries link to it and R1 gets a REST paragraph step (2c).
- 2026-10-04 G2/G3/G4 MOVED owner - Not worktree work: moved to the new plan 12-hook-cleanup-plan.md (runs after this plan). Removed from the tracker dependencies and from GATE-4 (G3, G4 no longer gate it); GATE-4 step 6 now only tests G1 and its stale warning-icon wording is fixed; the unhook steps live in GATE-H. Consequence recorded as HK-1 there: until that plan runs, deleting a Workspace that has Features is not blocked.
- 2026-10-04 B7 DONE Claude Sonnet 5.5 - Owner reversed one part of the G3 move: Workspace delete is refused while Features exist (Remove Workspace dialog explains and disables Remove; repository throws as a second line of defence). Unhooking stays in `12-hook-cleanup-plan.md`; GATE-4 depends on B7 and step 8 tests it.

---

# PART F - Discovered issues (not in scope of any unit)

Agents add items here instead of fixing them. Format: `- [<unit where found>] <file:symbol> - <problem> - <suggested severity: release blocker | v1.1 | later>`. The owner triages them into new units or the backlog.

Known non-worktree items from `05-general-code-and-ux-review.md`, deliberately not in v1 scope unless the owner promotes them:
- [review] `dotnet restore` failures reported as success (05 A2) - v1.1, high.
- [review] Connector delete cascades with a one-line confirm (05 U1) - v1.1, high.
- [review] Server-side SignalR client to own hub breaks behind proxies (05 A4) - v1.1.
- [review] Desktop telemetry ping without opt-out (05 D1) - owner decision.
- [review] Git option injection through string-built arguments, PAT in argv (05 S5, S6) - v1.1.
- [review] REST API cannot target a Feature context (05 R5) - document in R1 (suggested text and the items to verify first are in `11-post-release-plans.md` section 1.1), implement v1.1 (plan: `11-post-release-plans.md` section 1).
- [review] `WorkspaceFeatureOperations` refactor (now 1,977 lines; duplicated fan-out and structural wrappers; 04 P2-8) - after release, not in v1 (plan: `11-post-release-plans.md` section 2).
- [07] GrayMoon's own commits and pulls run with `skipHooks: true` (`GitService.GetHooksConfigPrefix`, used by `DependencyUpdateOrchestrator`), so they bypass the team's `pre-commit` hooks too - owner decision, not changed in v1.
- [U0-1] Plan text (Touches list and A4 commands note) says the flaky PowerShell pipe test is in `GrayMoon.Agent.Tests`; `rg -n "Pipe" GrayMoon/src/GrayMoon.Agent.Tests` finds nothing there. The test matching Ref T1 (and the known-flaky description) is actually `RunAsync_ArgumentListOverload_DoesNotDeadlock_...` and `RunAsync_StringStdinOverload_DoesNotDeadlock_...` in `GrayMoon.Common.Tests/CommandLineServiceTests.cs`. Fixed it there since Ref T1 is unambiguous; worth correcting the plan text itself later - v1.1, low.
- [08] Enforce the Worker secret by default (`Security:RequireWorkerSecret = true`) - v1.1, after users have reinstalled the Worker.
- [07] Hook chaining for repos with their own hooks or `core.hooksPath` (DEC-3 = chain) - v1.1.
- [09] Create PR and Push for a Feature repo that is off its Feature branch (I4 shows it) probably act on the checked-out branch, not the Feature branch; not verified - v1.1, check before deciding.
- [09] Multi-branch Features (stacked branches inside a Feature, 09 option C) - later, needs a design.
- [01] `WorkspaceStateRecomputeScope`, `WorkspaceBranchOperations` - legacy `WorkspaceSynced` events are still sent for Feature contexts (01 section 5 item 7), so Workspace tabs may refresh on Feature work - v1.1.
- [01] `GrayMoon.Desktop` `MainWindow.xaml.cs` - `WebViewMessageSourceValidator` covers only the install commands; `OpenIn*` launches are not origin-validated (01 section 5 item 9) - v1.1.
- [review] Moving the primary checkout under existing Features (Workspace rename or root change, then `git worktree repair`) - v1.1; B6 blocks it in v1.
- [DEC-8] ~~`core.longpaths` for Feature worktrees and tests for paths over 260 characters (04 P2-3) - v1.1.~~ Done in v1 as E6 (owner, 2026-10-04).
- [DEC-8] Counters, diagnostics export (DB Features vs `git worktree list` vs disk), startup foreign-key schema check, bUnit component tests (04 checklist) - v1.1.
- [B3] `WorkspaceProjectRepository.DependencyStats.cs:PersistRepositoryDependencyLevelAndDependenciesAsync` falls back to the shared `WorkspaceRepositoryLink.GitVersion` for a Feature repo that has no `WorkspaceRepositoryContextState` row yet (`contextVersionByLink.GetValueOrDefault(x.WorkspaceRepositoryId, x.GitVersion)`), which reads as a violation of rule 2 of `feature-context-scoping.mdc` ("never fall back to the Workspace's own value when a Feature has no context row yet"). Not in B3's Touches list, so left unchanged; B3's own new `GetRepositoryVersionMapAsync` (DependencyLines.cs) returns null instead, per the rule - v1.1 or sooner, medium (affects dependency-level/unmatched-count persistence, not just a read).
- [DEC-7/E4] `WorkspaceGitChanges.CopyPath.cs:BuildAbsoluteFilePath` hardcodes backslashes with a comment claiming "GrayMoon workspaces only ever exist on Windows machines" - already false today for a plain (non-Feature) Workspace on a Linux Worker, independent of the Feature-path fix in this unit. "Copy absolute path" in Git Changes would produce a backslash-joined path for a Linux Worker's repository. Not touched here (out of this unit's Touches list) - v1.1, medium (cosmetic/clipboard-only, not data loss).
