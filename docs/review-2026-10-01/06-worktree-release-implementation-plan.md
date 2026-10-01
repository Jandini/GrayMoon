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
- Hot files outside Features: `App/Repositories/WorkspaceRepository.cs` (B6, G3), `RepositoryRepository.cs` and `ConnectorRepository.cs` (B5, G3). G3 starts only after B5 and B6 are merged.
- Lane I also touches shared code: I1 edits `AnalyzeRemoveFeatureAsync` in that hot file and the A1 `InspectWorktree` files, so it starts only after lanes A and D are merged. `WorkspaceBranchOccupancyService.cs` is edited by I2 only; B4 only calls it.
- Shared migration file: `GrayMoon/src/GrayMoon.App/Migrations.Features.cs`. Only units marked "Migration" edit it, and each adds a **new, separate step method**. Never edit another unit's step.
- **Merging lanes (owner, or an agent the owner asks):** merge one lane at a time into the main checkout. After each merge run the build, all test projects and `WORKSPACE-SMOKE` (Part D) before merging the next lane. Gates are always run on the merged tree, never on a single lane's checkout.

---

# PART B - Decisions (owner answers; agents read, never decide)

Write the answer in the `Answer` column and change `OPEN` to `DECIDED`. The `Default` is what the plan assumes if the owner says "go with default".

| ID | Question | Default | Blocks units | Status | Answer |
|---|---|---|---|---|---|
| DEC-1 | Is the Docker deployment (App in a container, Worker on the host) supported for worktrees v1? | Yes. The README documents it as the main install, so Agent-side disk checks are required. If No, A1/A2 are still done because they are correct for both setups, but severity drops and the Docker test in Gate 2 is skipped. | A2, Gate 2 | DECIDED | Yes. Desktop is an addon-wrapper around the webapp; Docker (App in container, Worker on host) is a supported deployment for worktrees v1. |
| DEC-2 | Should v1 offer "also delete remote branches" in Remove Feature? | No for v1. Hide it and remove the dead `DeleteRemoteBranches` path from the modal call; ship the safe version (D4) in v1.1. | D4 | OPEN | |
| DEC-3 | Git hooks when a repo uses `core.hooksPath` (husky, lefthook) or already has its own hook: chain them, or detect and warn? | Detect and warn in v1 (G1). Chaining in v1.1. | G1 | OPEN | |
| DEC-4 | Local security without user login: App and Worker share a generated secret that the Worker must present to `/hub/agent` and `/repos/{id}/connector`. OK for v1? | Yes. No user login anywhere; the secret is created on first start and handed to the Worker by the existing install flow. | F2 | OPEN | |
| DEC-5 | Every Workspace repo always gets a worktree (no repo picker) in v1? | Yes. Repo picker is v1.1. | none (scope) | OPEN | |
| DEC-6 | Hide Features behind a setting (kill switch) for v1? | Yes, a setting `Features:Enabled` (default true) that hides create and the selector's Features section. Existing worktrees stay usable from plain Git. | R2 | OPEN | |
| DEC-7 | Are Features supported with a Linux Worker in v1? Feature paths are always built Windows-shaped (`CombineWindows` in `WorkspaceContextPathResolver`), and a Linux Worker ships. | No. Create Feature is refused on a non-Windows Worker with a clear message (E4). Existing Workspace use of a Linux Worker is unchanged. | E4 | OPEN | |
| DEC-8 | Release checklist items in `04` that no unit covers: bUnit component tests, counters, diagnostics export, startup foreign-key schema check, downgrade path, ops doc, paths over 260 characters (`core.longpaths`). Add units, or move them to v1.1? | Move to v1.1, except: the ops doc goes into R1, the downgrade note goes into R1 troubleshooting ("restore the backup with the previous build"), and pure-helper unit tests replace bUnit. R4 ticks these as "moved per DEC-8". | R1, R4 | OPEN | |

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
| 6 | A2 | Remove analysis uses the Agent, not App disk | A | A1, DEC-1 | M | IN PROGRESS | Claude Sonnet 5 (2026-10-01) | |
| 7 | A3 | Live outgoing commits and PR facts in remove analysis | A | A2 | S | TODO | | |
| 8 | B1 | Remove deletes context-scoped project data | B | GATE-1 | S | TODO | | |
| 9 | B2 | Orphan cleanup and foreign keys on upgraded databases | B | B1 | M | TODO | | Migration |
| 10 | B3 | Scope project queries by context | B | B1 | M | TODO | | |
| 11 | B4 | Close remaining context leaks | B | B3 | M | TODO | | |
| 11a | B5 | Connector refresh and delete never drop a Feature's repository | B | GATE-1 | M | TODO | | Data-loss fix; before G3 |
| 11b | B6 | Edit Workspace: no partial save; rename and root change blocked while Features exist | B | GATE-1 | S | TODO | | Bug fix in Workspace code; before G3 |
| 12 | C1 | Create Feature intent in one transaction | C | GATE-1 | S | TODO | | |
| 13 | C2 | Reconcile stuck Features at startup and on Worker reconnect | C | C1, A1 | M | TODO | | |
| 14 | C3 | Selector shows every state with friendly labels | C | C2 | S | TODO | | |
| 15 | C4 | Repair (retry, continue remove) and Roll back service operations | C | C2, D2 | M | TODO | | Needs D2's row states |
| 16 | C5 | Feature status panel | C | C3, C4 | M | TODO | | |
| 17 | D1 | Agent removes worktree residue and reports what is left | D | GATE-1 | M | TODO | | |
| 18 | D2 | App removal report and no swallowed branch failures | D | D1 | M | TODO | | |
| 19 | D3 | Remove dialog: checkboxes that match the situation | D | D2, A2 | S | TODO | | |
| 19b | D5 | Locked worktrees in Remove | D | D3 | S | TODO | | |
| 19a | I1 | Remove analysis judges the Feature branch, not the current checkout | I | A3, D3 | M | TODO | | Data-loss fix (09 SB-2); after lanes A and D are merged |
| 20 | GATE-2 | Remove and analysis | gate | A3, B2, B3, D3, D5, I1 | - | WAITING | | Step 7 needs B3 |
| 21 | GATE-3 | Failure and recovery | gate | C5 | - | WAITING | | |
| 22 | E1 | Feature name validation | E | GATE-1 | M | TODO | | Migration (index) |
| 23 | E2 | Header primary button rule | E | - | S | TODO | | |
| 24 | E3 | Desktop "Open in..." launch quoting | E | - | S | TODO | | Desktop repo |
| 24a | E4 | Refuse Create Feature on a non-Windows Worker | E | DEC-7 | S | TODO | | |
| 25 | F1 | Per-install token encryption key | F | GATE-1 | M | TODO | | |
| 26 | F2 | Worker secret for hub and token endpoint | F | F1, DEC-4 | M | TODO | | |
| 27 | F3 | Block cross-site and rebinding requests (REST and hubs) | F | - | S | TODO | | |
| 28 | G1 | Detect hook conflicts and warn | G | DEC-3 | M | TODO | | |
| 28a | G2 | Worker `UnhookRepository` command | G | G1 | S | TODO | | |
| 28b | G3 | Unhook when repositories leave a Workspace; block Workspace delete while Features exist | G | G2, B5, B6 | M | TODO | | Shares `WorkspaceRepository.cs`, `RepositoryRepository.cs`, `ConnectorRepository.cs` with B5/B6 |
| 28c | G4 | Self-heal stale hooks from pings | G | G2 | S | TODO | | Deferrable to v1.1 if time is short |
| 28d | I2 | Own Feature branch is checkable; "Return to Feature branch" | I | GATE-1 | S | TODO | | |
| 28e | I3 | Feature-aware Switch Branch dialog and service rules | I | I2 | M | TODO | | |
| 28f | I4 | Show drift in the grid | I | I2, C2 | S | TODO | | |
| 29 | H1 | Structured logging for Feature operations | H | C4, D2 | S | TODO | | |
| 30 | D4 | Safe remote branch deletion (only if DEC-2 = Yes) | D | D2, F2 | M | TODO | | |
| 31 | GATE-4 | Names, header, security, hooks, Desktop | gate | B5, B6, E1, E2, E3, E4, F3, F2, G1, G3, G4, I3, I4 | - | WAITING | | |
| 32 | R1 | Docs: user guide, troubleshooting, changelog, mark old designs superseded | R | GATE-4 | M | TODO | | |
| 33 | R2 | Features kill switch | R | DEC-6 | S | TODO | | |
| 34 | R3 | GitVersion parity check | R | GATE-1 | S | TODO | | |
| 35 | R4 | Release regression suite and checklist | R | all above | M | TODO | | |
| 36 | GATE-5 | Release candidate sign-off | gate | R4 | - | WAITING | | |

**What can run in parallel after GATE-1:** lanes A, B, C, D, E, F, G, I each have one agent. Lane H waits for C4 and D2. In lane I, I2 can start right after GATE-1 and I3 follows it; I1 waits for A3 and D3, and I4 waits for C2. C4 waits for D2 (row states during Remove). G3 waits for B5 and B6 (same repository files). E2, E3 and F3 have no dependencies and can be done at any time, even before GATE-1.

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
- **Read first:** `FeatureOps` methods `AnalyzeRemoveFeatureAsync`, `ProbeFeatureWorktreeLiveStatusAsync`, `ComposeRemoveWarning`, `IsAutomaticallySafe`, `Classify`; `App/Services/Features/WorkspaceExternalWorktreeOperations.cs` (the plan builder with `Directory.Exists` and hard-coded `dirty = false`); `GrayMoon/src/GrayMoon.App.Tests/RemoveFeatureWorkspaceRefreshTests.cs` (how the fake Agent bridge is set up).
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
  4. Keep the existing "−" remove action; hide it for `Creating`/`Removing`.
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
  1. Panel lists repos with state and `LastError`, and buttons Retry, Roll back, Remove. Buttons call the facade and show the per-repo result list afterwards. For a remove-incomplete Feature (C4 `IsRemoveIncomplete`), show only **Continue removal** (opens the Remove dialog) and list `Removed` repos as "already removed".
  2. When create ends with failures, the Create modal closes and opens the panel instead of saying "One or more worktrees failed to create."
  3. When a `NeedsRepair` Feature is selected, show a banner "This Feature needs attention" with a "Show details" button that opens the panel.
  4. Feature menu gets "Status and repair".
- **Tests to add:** none required beyond C4 (UI). Build must pass.
- **Done when:** builds; owner verifies at GATE-3.
- **Regression guard:** the Feature menu entry and banner render only for Feature contexts; the Workspace header is unchanged (WORKSPACE-SMOKE at GATE-3).

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
  4. Text under the list: "Local branches are deleted. Remote branches are kept." (unless D4 ships).
- **Tests to add:** extract the enable rule to a small pure helper and unit-test the combinations.
- **Done when:** tests pass.
- **Regression guard:** the Remove dialog is Feature-only; external-worktree cleanup in the Workspace Switch Branch modal keeps its current checkboxes and text.

### D5 Locked worktrees in Remove

- **Goal:** A locked worktree (`git worktree lock`) is explained in the Remove dialog instead of failing with a raw Git error, and is unlocked only with consent.
- **Ref:** F-12, 04 P2-3 (lock part only; `core.longpaths` is v1.1 per DEC-8). A1 already reports `isLocked` and `lockReason`, but no other unit uses them.
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

### D4 Safe remote branch deletion (only if DEC-2 = Yes)

- **Goal:** Optional "Also delete remote branches" that never deletes someone else's work.
- **Ref:** F-6, 04 P1-3, 05 R3.
- **Read first:** `GitService.DeleteBranchAsync`, the comment near it saying the remote call needs `bearerToken`; `GitService.FindBranchCollisionsAsync`; how other commands pass `bearerToken` (`rg -n "bearerToken" GrayMoon/src/GrayMoon.App`).
- **Touches:** `RemoveFeatureModal.razor`, `FeatureOps` `RemoveFeatureCoreAsync`, Agent delete-branch path, tests.
- **Steps:** opt-in checkbox listing the remote branches; fetch the remote first; delete with a lease (`git push --force-with-lease=refs/heads/<b>:<expectedSha> origin :<b>` where expectedSha is the Feature's local branch tip); pass the connector token; report each result in the D2 report.
- **Tests to add:** remote tip equals local -> deleted; remote moved -> refused and reported.
- **Done when:** tests pass. If DEC-2 = No: instead remove the unused `DeleteRemoteBranches` wiring from the modal call path and mark this unit DONE with Note "Skipped per DEC-2".
- **Regression guard (R-D4):** never delete the repo's default branch or a branch name not stored in the Feature's repo rows; off by default on every Remove; the branch list is shown before confirming.

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

### E4 Refuse Create Feature on a non-Windows Worker

- **Goal:** With a Linux Worker, Create Feature is refused with a clear message instead of creating worktrees at Windows-shaped paths.
- **Ref:** 01 section 5 item 6 and R9. Requires DEC-7 (default: not supported in v1).
- **Read first:** `WorkspaceService.TryGetAgentDefaultFeatureStorageRootAsync` (calls `GetHostInfo`); the Agent `GetHostInfo` command and its response DTO; `GetHostInfoAgentResponse` in the App; `FeatureOps` `CreateFeatureCoreAsync` (start); `CreateFeatureModal.razor`; the selector's "+" button.
- **Touches:** the `GetHostInfo` response DTOs (Agent and App, one new nullable field), the Agent command, `FeatureOps` (`CreateFeatureCoreAsync` start only), `CreateFeatureModal.razor`, `WorkspaceFeatureSelector.razor` ("+" tooltip), tests.
- **Steps:**
  1. Agent: `GetHostInfo` adds `osPlatform` (`"Windows"`, `"Linux"`, `"OSX"`), from `OperatingSystem.Is*`.
  2. App: at the start of create, ask `GetHostInfo`. If `osPlatform` is present and not `"Windows"`, return the error "Features need a Windows Worker in this version." without writing anything. If the field is missing (old Worker), allow create as today (old Workers were Windows-only in practice; the Desktop Worker is Windows).
  3. Disable the selector's "+" with the same message as tooltip when the connected Worker reports a non-Windows platform.
- **Tests to add:** Linux Worker -> create refused, no rows written, zero `CreateGitWorktree` calls; Windows Worker -> create proceeds; old response without `osPlatform` deserializes and create proceeds.
- **Done when:** tests pass; Desktop README bullet.
- **Regression guard (R-E4):** the new field is additive (rule 13); every other `GetHostInfo` caller ignores it; Workspace use of a Linux Worker is untouched.

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

### G2 Worker `UnhookRepository` command

- **Goal:** The Worker can safely remove GrayMoon's hooks from one repository folder.
- **Ref:** `07-appendix-why-lane-g-hooks.md` section 6.
- **Read first:** `07` section 6; `GrayMoon/.claude/skills/add-agent-command/SKILL.md`; G1's hooks-folder resolution and marker recognition in `GitService.cs`.
- **Touches:** new DTOs, `Agent/Commands/UnhookRepositoryCommand.cs`, a new `GitService.RemoveSyncHooksAsync`, registrations per the skill, constant in `AgentHubMethods.cs`, tests.
- **Steps:**
  1. Request: `repositoryPath`, `workspaceId`. Response: `outcome` (`Removed`, `NothingToRemove`, `PathMissing`, `NotAGitRepository`, `HooksPathCustom`, `Error`), `removedHooks` (names), `skippedHooks` (names with a reason), `message`.
  2. Resolve the hooks folder with G1's helper (`git rev-parse --git-path hooks`). If the folder or repository does not exist, return `PathMissing` or `NotAGitRepository`. Never throw.
  3. For each of `post-commit`, `post-checkout`, `post-merge`, `post-update`, `pre-push`: delete the file **only if** it carries the GrayMoon marker (`# Created by GrayMoon.Agent`) **and** its payload names the requested `workspaceId`. Parse the id with a regex that accepts both payload styles, the current escaped `\"workspaceId\":N` and the 0.1.0 single-quoted `"workspaceId":N`, for example `workspaceId\\?"\s*:\s*(\d+)`. Otherwise skip it and give the reason (`NotGrayMoon`, `OtherWorkspace`).
  4. Read-only pool is wrong for this (it deletes files); register it in the main pool.
- **Tests to add (real git):** GrayMoon hooks for workspace 3 are removed when asked for 3; kept when asked for 4 (`OtherWorkspace`); a user hook is kept (`NotGrayMoon`); the 0.1.0 hook text is recognised and removed; missing folder -> `PathMissing`; plain folder -> `NotAGitRepository`; `core.hooksPath` set -> `HooksPathCustom` and nothing deleted; in a linked worktree, the primary checkout's common hooks are the ones removed.
- **Done when:** tests pass.
- **Regression guard (R-G2):** deletes only files that pass both checks in step 3; never deletes the hooks folder itself or any other file. An old App never sends this command, so nothing changes for it.

### G3 Unhook when repositories leave a Workspace; block Workspace delete while Features exist

- **Goal:** Removing one or more repositories, deleting a Workspace, or a connector change that drops repositories unhooks those folders. Failures only log Warnings and never fail the user's action.
- **Ref:** `07` section 6.
- **Read first:** `07` section 6.1 (the four removal paths); `App/Repositories/WorkspaceRepository.cs` `ReplaceRepositoriesAsync` and `DeleteAsync`; `App/Components/Pages/Workspaces.razor` `DeleteWorkspaceAsync`; `App/Repositories/RepositoryRepository.cs` (merge, `toDeleteIds`); `App/Repositories/ConnectorRepository.cs` `DeleteAsync`; `App/Services/Workspaces/WorkspaceService.cs` `GetWorkspacePath`; `App/Services/Features/WorkspaceContextPathResolver.cs` `GetRepositoryPathAsync`; `ui-application-facades.mdc`.
- **Touches:** new `App/Services/Hooks/WorkspaceHookCleanupService.cs` (+ interface), the four removal paths, a Workspace facade method for delete, `Workspaces.razor`, tests, Desktop README bullet.
- **Steps:**
  1. `WorkspaceHookCleanupService.UnhookAsync(IReadOnlyList<(int WorkspaceId, int RepositoryId, string RepositoryPath)> targets, CancellationToken)`: if the Worker is not connected, log one Warning listing the targets and return. Otherwise send `UnhookRepository` per target, at most 4 at a time, with a 30 s timeout each. Log the outcome per repository: Information for `Removed` and `NothingToRemove`, Warning for everything else, including `Unknown command` from an old Worker. Never throw.
  2. **Collect targets before deleting rows.** In each removal path, build the repository paths of the primary checkouts (workspace root + repository folder name, the same way `WorkspaceContextPathResolver` does for the special Workspace) for the rows about to be removed, before the transaction. After the transaction commits, run `UnhookAsync` in the background through `IServiceScopeFactory` (rule: `dbcontext-scoped-lifetime.mdc`). The user's action returns without waiting.
  3. Removing repositories (`ReplaceRepositoriesAsync`): targets = the `toRemove` links. Repositories that stay or are added are not touched.
  4. Deleting a Workspace: move the delete behind a facade method (for example `IWorkspaceManagementOperations.DeleteWorkspaceAsync`, or whatever the existing workspace facade is called; find it with `rg -n "interface IWorkspace\w*Operations" GrayMoon/src`). The page calls the facade, not `WorkspaceRepository`. Before deleting: if the Workspace has any Feature, refuse with "Remove the Features first, then delete the Workspace." (matches `ReplaceRepositoriesAsync`). Targets = every linked repository.
  5. Connector refresh dropping repositories, and connector delete: targets = every link of those repositories in every Workspace.
  6. Re-adding a repository needs nothing new: it starts as `NeedsSync`, and Sync writes the hooks. Add a test that proves it: remove a repo (unhooked), add it back, Sync -> hooks present.
- **Tests to add (App.Tests, fake Agent bridge):** remove 2 of 3 repos -> 2 unhook calls with the right paths and workspace id, none for the third; delete Workspace -> one call per repo; Worker not connected -> the removal still succeeds and one Warning is logged; Agent returns `PathMissing` or throws -> removal still succeeds, Warning logged; Workspace with a Feature -> delete refused, nothing deleted, no unhook; connector delete -> unhook for its repos in every Workspace.
- **Done when:** tests pass; `Workspaces.razor` no longer injects `WorkspaceRepository` for delete.
- **Regression guard (R-G3a, R-G3b, R-G3c):** this unit edits standard Workspace flows (Edit Workspace, Delete Workspace, connector refresh), so do A4 step 0 first: characterization tests that today's removal results (rows deleted, rows kept) are unchanged. Unhooking runs only after the database commit and never changes the result, timing or error messages of the user's action. Repositories that stay in the Workspace are never unhooked. The new Feature guard on delete is the only intended behaviour change; add it to the Desktop README.

### G4 Self-heal stale hooks from pings

- **Goal:** Stale hooks that G3 could not remove (Worker offline, or left by an older GrayMoon) remove themselves the next time they fire.
- **Ref:** `07` section 6.2.
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
  2. **Badge:** a drifted row shows a warning badge "Off Feature branch" next to the branch, with the tooltip "This repository is on `<X>`, not on `<name>`. Click to return." Clicking opens the Switch Branch dialog, where I2's "Return to Feature branch" button is shown.
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
6. Hooks: in a test repo run git config core.hooksPath .husky, Sync. Expected: warning icon with the explanation; .husky contents unchanged.
   In another repo add your own .git/hooks/pre-push (e.g. echo "user hook"), Sync. Expected: your file unchanged, warning shown.
   In a repo where GrayMoon hooks already exist, commit from a terminal. Expected: grid updates within seconds (hooks still work).
   Unhook: Edit Workspace, untick 2 repos, save. Expected: their .git\hooks no longer contain GrayMoon hooks (your own hook files untouched); the remaining repos still have theirs.
   Commit in one of the removed repos. Expected: no new "not found" warning in the App log, no fetch or GitVersion activity in the Worker log.
   Tick them again, Sync. Expected: hooks are back and a terminal commit updates the grid.
   Stop the Worker, untick a repo, save, start the Worker. Expected: the removal succeeded with a warning in the log. Commit in that repo: the first ping removes the hooks (Information log line); later commits are quiet.
   Delete a test Workspace that has a Feature. Expected: refused with "Remove the Features first". Remove the Feature, delete the Workspace. Expected: its repos are unhooked; a repo folder you deleted by hand beforehand only produces a warning.
7. Branch rules in a Feature: create Feature gate4-b and open a repo's branch dialog.
   Expected: no New Branch tab, no Return to Default; other branches cannot be checked out and the tooltip explains why; tags cannot be checked out unless the repo is pinned to a tag.
   Delete a throwaway branch from inside the Feature. Expected: the confirmation says branches are shared by the Workspace and all Features.
   In a terminal, in that repo's Feature folder, run git switch -c side. Expected: within seconds the grid shows "Off Feature branch".
   Click it and press "Return to Feature branch". Expected: the repo is back on gate4-b and the badge is gone.
   Tag-pinned repo (if you have one): in the Feature, open its upgrade badge and check out a newer tag. Expected: works; the repo stays on the tag.
   In the Workspace, open a repo's upgrade badge, then the Locals tab. Expected: branches held by a Feature now show the Feature badge (missing on this path before); everything else as before.
8. Workspace edits while a Feature exists (keep gate4-b):
   Edit the Workspace and press Save without changing anything. Expected: saves, no error.
   Try to rename it. Expected: the name field is read-only with the reason; nothing changes.
   Try to delete the connector of its repositories. Expected: refused with "Remove the Features that use this connector's repositories first."; nothing is deleted.
   Remove gate4-b, then rename the Workspace. Expected: works as before.
9. Linux Worker (skip if you have none): connect a Linux Worker and open the Feature selector. Expected: "+" is disabled with "Features need a Windows Worker in this version."
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
  1. `features.md`: what a Feature is; where it lives on disk; what Create copies and what it does not (uncommitted changes); switching; Open in...; what Remove deletes (worktrees, local branches) and keeps (remote branches unless D4); Needs attention and repair.
  2. `troubleshooting.md`: Feature stuck or needs attention; "Removal incomplete"; leftover folders; untracked worktrees reported by the reconciler; locked worktrees; branch already exists; Worker not connected or pairing failed; hooks warning; manual cleanup recipe (`git worktree list`, `git worktree unlock <path>`, `git worktree remove --force <path>`, `git worktree prune`, `git branch -D <name>`, delete `features\<name>`); where logs and DB backups are; **downgrade**: close GrayMoon, restore the newest `graymoon.db.bak-<timestamp>` (and the key file from F1 if it was created after the backup), then start the previous build.
  2b. `GrayMoon/docs/user-guide/operations.md` (DEC-8): the Feature storage root setting, what changes when the App runs in Docker (Agent-side checks, `AllowedHosts`, pairing the Worker), the Linux Worker limitation (DEC-7), the security settings (`Security:RequireWorkerSecret`), and the log fields from H1.
  3. `CHANGELOG.md`: a v1 entry summarizing user-visible changes from all units (use the Desktop README bullets).
  4. Banner for `docs/worktree/*`: `> Superseded design history. The current behaviour is documented in docs/architecture and docs/user-guide.`
  5. `README.md`: link the user guide. Keep Worktrees marked as shipped only once GATE-5 passes; until then mark it "Preview".
- **Done when:** every claim matches the code after all units.
- **Regression guard:** docs only. Do not describe behaviour that a unit marked BLOCKED or skipped did not ship.

### R2 Features kill switch

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
  2. Walk the checklist in `04-worktree-v1-release-roadmap.md` section "Release-readiness checklist"; tick each item with the unit that covered it, mark the items DEC-8 moved to v1.1 as "moved per DEC-8", or add a unit for anything else uncovered and stop.
  3. Confirm `Discovered issues` (Part F) has no item marked "release blocker".
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

---

# PART F - Discovered issues (not in scope of any unit)

Agents add items here instead of fixing them. Format: `- [<unit where found>] <file:symbol> - <problem> - <suggested severity: release blocker | v1.1 | later>`. The owner triages them into new units or the backlog.

Known non-worktree items from `05-general-code-and-ux-review.md`, deliberately not in v1 scope unless the owner promotes them:
- [review] `dotnet restore` failures reported as success (05 A2) - v1.1, high.
- [review] Connector delete cascades with a one-line confirm (05 U1) - v1.1, high.
- [review] Server-side SignalR client to own hub breaks behind proxies (05 A4) - v1.1.
- [review] Desktop telemetry ping without opt-out (05 D1) - owner decision.
- [review] Git option injection through string-built arguments, PAT in argv (05 S5, S6) - v1.1.
- [review] REST API cannot target a Feature context (05 R5) - document in R1, implement v1.1.
- [07] GrayMoon's own commits and pulls run with `skipHooks: true` (`GitService.GetHooksConfigPrefix`, used by `DependencyUpdateOrchestrator`), so they bypass the team's `pre-commit` hooks too - owner decision, not changed in v1.
- [U0-1] Plan text (Touches list and A4 commands note) says the flaky PowerShell pipe test is in `GrayMoon.Agent.Tests`; `rg -n "Pipe" GrayMoon/src/GrayMoon.Agent.Tests` finds nothing there. The test matching Ref T1 (and the known-flaky description) is actually `RunAsync_ArgumentListOverload_DoesNotDeadlock_...` and `RunAsync_StringStdinOverload_DoesNotDeadlock_...` in `GrayMoon.Common.Tests/CommandLineServiceTests.cs`. Fixed it there since Ref T1 is unambiguous; worth correcting the plan text itself later - v1.1, low.
- [08] Enforce the Worker secret by default (`Security:RequireWorkerSecret = true`) - v1.1, after users have reinstalled the Worker.
- [07] Hook chaining for repos with their own hooks or `core.hooksPath` (DEC-3 = chain) - v1.1.
- [09] Create PR and Push for a Feature repo that is off its Feature branch (I4 shows it) probably act on the checked-out branch, not the Feature branch; not verified - v1.1, check before deciding.
- [09] Multi-branch Features (stacked branches inside a Feature, 09 option C) - later, needs a design.
- [01] `WorkspaceStateRecomputeScope`, `WorkspaceBranchOperations` - legacy `WorkspaceSynced` events are still sent for Feature contexts (01 section 5 item 7), so Workspace tabs may refresh on Feature work - v1.1.
- [01] `GrayMoon.Desktop` `MainWindow.xaml.cs` - `WebViewMessageSourceValidator` covers only the install commands; `OpenIn*` launches are not origin-validated (01 section 5 item 9) - v1.1.
- [review] Moving the primary checkout under existing Features (Workspace rename or root change, then `git worktree repair`) - v1.1; B6 blocks it in v1.
- [DEC-8] `core.longpaths` for Feature worktrees and tests for paths over 260 characters (04 P2-3) - v1.1.
- [DEC-8] Counters, diagnostics export (DB Features vs `git worktree list` vs disk), startup foreign-key schema check, bUnit component tests (04 checklist) - v1.1.
