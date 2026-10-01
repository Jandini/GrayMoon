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

Use exactly this shape:

```
Unit: <id> <title> - <DONE | BLOCKED | READY FOR USER TEST>
What changed: <one or two sentences>
Tests: <new tests added>; <project>: <passed>/<total>
Files: <list>
Next: <next unit id, or the gate the owner must run, or the question you need answered>
```

Then the commit message block(s).

## A7. Working in parallel (several agents)

- Lanes (Part C) are designed so agents in different lanes touch mostly different files. **One agent per lane at a time.**
- **Each parallel agent works in its own checkout** (for example a GrayMoon Feature worktree created by the owner). Two agents must never edit the same working folder at the same time.
- Hot file: `GrayMoon/src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs` is touched by lanes A, B, C, D and H. Keep your edits to the methods your unit names. Do not reformat, reorder or rename anything else in that file, so the owner can merge lanes cleanly.
- Shared migration file: `GrayMoon/src/GrayMoon.App/Migrations.Features.cs`. Only units marked "Migration" edit it, and each adds a **new, separate step method**. Never edit another unit's step.
- **Merging lanes (owner, or an agent the owner asks):** merge one lane at a time into the main checkout. After each merge run the build, all test projects and `WORKSPACE-SMOKE` (Part D) before merging the next lane. Gates are always run on the merged tree, never on a single lane's checkout.

---

# PART B - Decisions (owner answers; agents read, never decide)

Write the answer in the `Answer` column and change `OPEN` to `DECIDED`. The `Default` is what the plan assumes if the owner says "go with default".

| ID | Question | Default | Blocks units | Status | Answer |
|---|---|---|---|---|---|
| DEC-1 | Is the Docker deployment (App in a container, Worker on the host) supported for worktrees v1? | Yes. The README documents it as the main install, so Agent-side disk checks are required. If No, A1/A2 are still done because they are correct for both setups, but severity drops and the Docker test in Gate 2 is skipped. | A2, Gate 2 | OPEN | |
| DEC-2 | Should v1 offer "also delete remote branches" in Remove Feature? | No for v1. Hide it and remove the dead `DeleteRemoteBranches` path from the modal call; ship the safe version (D4) in v1.1. | D4 | OPEN | |
| DEC-3 | Git hooks when a repo uses `core.hooksPath` (husky, lefthook) or already has its own hook: chain them, or detect and warn? | Detect and warn in v1 (G1). Chaining in v1.1. | G1 | OPEN | |
| DEC-4 | Local security without user login: App and Worker share a generated secret that the Worker must present to `/hub/agent` and `/repos/{id}/connector`. OK for v1? | Yes. No user login anywhere; the secret is created on first start and handed to the Worker by the existing install flow. | F2 | OPEN | |
| DEC-5 | Every Workspace repo always gets a worktree (no repo picker) in v1? | Yes. Repo picker is v1.1. | none (scope) | OPEN | |
| DEC-6 | Hide Features behind a setting (kill switch) for v1? | Yes, a setting `Features:Enabled` (default true) that hides create and the selector's Features section. Existing worktrees stay usable from plain Git. | R2 | OPEN | |

---

# PART C - Tracker (the live part; update it every session)

Status values: `TODO`, `IN PROGRESS`, `BLOCKED`, `DONE`, and for gates `WAITING`, `READY FOR USER TEST`, `PASSED`, `FAILED`.

Lanes: **0** foundation, **A** Agent truth, **B** data integrity, **C** lifecycle and recovery, **D** honest removal, **E** names and UX, **F** local security, **G** hooks, **H** logging, **R** release.

| Order | Unit | Title | Lane | Depends on | Size | Status | Owner | Notes |
|---|---|---|---|---|---|---|---|---|
| 1 | U0-1 | Baseline and housekeeping | 0 | - | S | TODO | | |
| 2 | U0-2 | Safe migration runner | 0 | U0-1 | M | TODO | | Migration |
| 3 | U0-3 | Golden 0.1.0 upgrade test | 0 | U0-2 | S | TODO | | |
| 4 | GATE-1 | Upgrade your real database | gate | U0-3 | - | WAITING | | |
| 5 | A1 | Agent `InspectWorktree` command | A | GATE-1 | M | TODO | | |
| 6 | A2 | Remove analysis uses the Agent, not App disk | A | A1, DEC-1 | M | TODO | | |
| 7 | A3 | Live outgoing commits and PR facts in remove analysis | A | A2 | S | TODO | | |
| 8 | B1 | Remove deletes context-scoped project data | B | GATE-1 | S | TODO | | |
| 9 | B2 | Orphan cleanup and foreign keys on upgraded databases | B | B1 | M | TODO | | Migration |
| 10 | B3 | Scope project queries by context | B | B1 | M | TODO | | |
| 11 | B4 | Close remaining context leaks | B | B3 | M | TODO | | |
| 12 | C1 | Create Feature intent in one transaction | C | GATE-1 | S | TODO | | |
| 13 | C2 | Reconcile stuck Features at startup and on Worker reconnect | C | C1, A1 | M | TODO | | |
| 14 | C3 | Selector shows every state with friendly labels | C | C2 | S | TODO | | |
| 15 | C4 | Repair (retry) and Roll back service operations | C | C2 | M | TODO | | |
| 16 | C5 | Feature status panel | C | C3, C4 | M | TODO | | |
| 17 | D1 | Agent removes worktree residue and reports what is left | D | GATE-1 | M | TODO | | |
| 18 | D2 | App removal report and no swallowed branch failures | D | D1 | M | TODO | | |
| 19 | D3 | Remove dialog: checkboxes that match the situation | D | D2, A2 | S | TODO | | |
| 20 | GATE-2 | Remove and analysis | gate | A3, B2, D3 | - | WAITING | | |
| 21 | GATE-3 | Failure and recovery | gate | C5 | - | WAITING | | |
| 22 | E1 | Feature name validation | E | GATE-1 | M | TODO | | Migration (index) |
| 23 | E2 | Header primary button rule | E | - | S | TODO | | |
| 24 | E3 | Desktop "Open in..." launch quoting | E | - | S | TODO | | Desktop repo |
| 25 | F1 | Per-install token encryption key | F | GATE-1 | M | TODO | | |
| 26 | F2 | Worker secret for hub and token endpoint | F | F1, DEC-4 | M | TODO | | |
| 27 | F3 | Block cross-site and rebinding requests | F | - | S | TODO | | |
| 28 | G1 | Detect hook conflicts and warn | G | DEC-3 | M | TODO | | |
| 28a | G2 | Worker `UnhookRepository` command | G | G1 | S | TODO | | |
| 28b | G3 | Unhook when repositories leave a Workspace; block Workspace delete while Features exist | G | G2 | M | TODO | | |
| 28c | G4 | Self-heal stale hooks from pings | G | G2 | S | TODO | | Deferrable to v1.1 if time is short |
| 29 | H1 | Structured logging for Feature operations | H | C4, D2 | S | TODO | | |
| 30 | D4 | Safe remote branch deletion (only if DEC-2 = Yes) | D | D2, F2 | M | TODO | | |
| 31 | GATE-4 | Names, header, security, hooks, Desktop | gate | E1, E2, E3, F3, F2, G1, G3, G4 | - | WAITING | | |
| 32 | R1 | Docs: user guide, troubleshooting, changelog, mark old designs superseded | R | GATE-4 | M | TODO | | |
| 33 | R2 | Features kill switch | R | DEC-6 | S | TODO | | |
| 34 | R3 | GitVersion parity check | R | GATE-1 | S | TODO | | |
| 35 | R4 | Release regression suite and checklist | R | all above | M | TODO | | |
| 36 | GATE-5 | Release candidate sign-off | gate | R4 | - | WAITING | | |

**What can run in parallel after GATE-1:** lanes A, B, C, D, E, F, G each have one agent. Lane H waits for C4 and D2. E2, E3 and F3 have no dependencies and can be done at any time, even before GATE-1.

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
  2. Rebuild `WorkspaceProjects` and `WorkspaceFileLineStatuses` with `FOREIGN KEY (WorkspaceFeatureContextId) REFERENCES WorkspaceFeatureContexts(Id) ON DELETE CASCADE`. SQLite cannot add a constraint, so: `PRAGMA foreign_keys=OFF`; create `<Table>_new` with the exact column list and constraints EF produces for a fresh database (generate the reference by calling `EnsureCreated` on an empty database in a test and reading `sqlite_master.sql`); copy rows; drop old; rename; recreate indexes; `PRAGMA foreign_key_check`; `PRAGMA foreign_keys=ON`. The step runs only if the FK is missing (`pragma_foreign_key_list`).
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
- **Done when:** tests pass; update the "remaining" list status in `04-worktree-v1-release-roadmap.md` P1-7 row Notes to "Done (B4)".
- **Regression guard (R-B4a, R-B4b):** characterization tests (A4 step 0) for the Workspace push plan (levels, tag-pinned repos excluded) and the Workspace notification push list, before the change. For the special Workspace these read the shared link exactly as today. In the bulk Branch modal, skip a repo only on a positive "held by a Feature / worktree" answer; if occupancy cannot be determined, attempt the switch as today and show "Could not check worktrees".

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
  2. For each Feature, call `ListGitWorktrees` once per main repository; a repo row in `Ready` whose worktree is not registered **and** whose folder `InspectWorktree` (A1) reports as missing becomes `NeedsRepair` with `LastError = "Worktree is missing"`. If either call fails or the Worker lacks `InspectWorktree`, change nothing for that repo. A repo row in `Pending` or `NeedsRepair` whose worktree **is** registered on the expected branch becomes `Ready`.
  3. Run it once at startup when the Worker first connects, and again on every Worker reconnect. Never run two reconciles at once (use a `SemaphoreSlim(1)`); skip workspaces that have a structural operation running.
  4. Use `IDbContextFactory`.
- **Tests to add:** stuck `Creating` becomes `NeedsRepair`; a running operation is left alone; Ready repo with unregistered worktree becomes NeedsRepair; Pending repo with registered worktree becomes Ready; Worker not connected -> no changes, no exception; `ListGitWorktrees` fails -> no changes; two reconnects within 60 s -> one reconcile.
- **Done when:** tests pass.
- **Regression guard (R-C2a, R-C2b):** the reconciler only reads and writes Feature rows (context `Kind` 1) and never deletes anything. It takes no structural lock, runs at most 4 Worker calls at a time, skips workspaces with a running operation, and runs at most once per 60 s, so Workspace Sync at startup is not delayed. A test asserts the special Workspace context's rows are byte-identical after a reconcile. C2 depends on A1 for `InspectWorktree`.

### C3 Selector shows every state with friendly labels

- **Goal:** Every Feature is visible in the selector; broken ones can be opened.
- **Ref:** U-2, U-8, U-9, 04 P0-3 (part 3).
- **Read first:** `App/Components/Features/WorkspaceFeatureSelector.razor` (`CanSelect`, the state filter that lists only Ready and NeedsRepair).
- **Touches:** `WorkspaceFeatureSelector.razor` (+ its code-behind if present).
- **Steps:**
  1. List all states. Labels: `Creating` -> "Setting up...", `Removing` -> "Removing...", `NeedsRepair` -> "Needs attention", `Ready` -> no label.
  2. Tooltip on the label shows the Feature's `LastError` when present.
  3. `NeedsRepair` rows are selectable; `Creating` and `Removing` rows are not selectable but stay visible.
  4. Keep the existing "−" remove action; hide it for `Creating`/`Removing`.
- **Tests to add:** if the project has no bUnit, add a unit test for the pure label/selectable mapping (extract it into a small static helper).
- **Done when:** test passes; no raw enum names appear in markup (`rg -n "NeedsRepair\)" App/Components/Features` returns nothing).
- **Regression guard (R-C3):** a NeedsRepair Feature opens **read-only**: show the "This Feature needs attention" banner and disable Sync, Push, Update, Prepare and Git Changes actions for that context (the header and toolbar already receive the context; add an `IsReadOnlyContext` flag). Test or manual check: opening a NeedsRepair Feature with one missing worktree does not throw and the browser tab stays usable; switching back to Workspace works.

### C4 Repair (retry) and Roll back service operations

- **Goal:** A user can retry the failed repos of a Feature, or roll back everything that was created.
- **Ref:** F-4, U-1, 04 P0-4.
- **Read first:** `FeatureOps` `CreateFeatureCoreAsync` (per-repo create loop and `NeedsRepair` handling), `SeedInitialFeatureProjectionsAsync`, `RemoveFeatureCoreAsync`; Agent `GitService.CreateWorktreeAsync` (idempotent when the path already holds the branch); the facade interface that exposes Create/Remove Feature to the UI (`rg -n "CreateFeatureAsync" GrayMoon/src/GrayMoon.Application GrayMoon/src/GrayMoon.App`).
- **Touches:** `FeatureOps` (new methods), the facade interface and implementation, tests.
- **Steps:**
  1. `RepairFeatureAsync(contextId)`: under the structural lock, for each repo in `Pending` or `NeedsRepair`, call `CreateGitWorktree` with the stored `BaseCommitSha`, `ParentBranchName` and pinned tag. Mark each repo Ready or NeedsRepair with the Agent's message in `LastError`. Re-run projection seeding for repos that became Ready. Feature becomes Ready only when every repo is Ready.
  2. `RollbackFeatureAsync(contextId)`: same as Remove with `force = false` for worktrees and normal (non-force) branch delete, but only for repos whose worktree is registered; then delete the Feature's rows. If any repo has uncommitted changes (per `InspectWorktree`), refuse and return the list.
  3. Both return a per-repo result list (repo name, outcome, message).
  4. Expose both on the facade.
- **Tests to add:** two repos fail then succeed on repair -> Feature Ready; one still fails -> NeedsRepair with that repo's message; rollback removes created worktrees and rows; rollback refuses when a repo is dirty.
- **Done when:** tests pass.
- **Regression guard (R-C4):** Roll back deletes only the branch names stored in that Feature's repo rows, with non-force delete, and never the repo's default branch or the branch checked out in the primary checkout (check both before calling the Agent; test both).

### C5 Feature status panel

- **Goal:** For a Feature that needs attention, the user sees each repo's state and error and can Retry, Roll back or Remove.
- **Ref:** U-1, U-8, U-9, 03 section 6 item 2.
- **Read first:** `App/Components/Features/CreateFeatureModal.razor` (how failure is shown today), `WorkspaceRepositoriesHeader.razor` (Feature menu), C4 facade methods.
- **Touches:** new `App/Components/Features/FeatureStatusPanel.razor`, `CreateFeatureModal.razor`, the Feature menu in `WorkspaceRepositoriesHeader.razor`, `GrayMoon.Desktop/README.md` (one bullet).
- **Steps:**
  1. Panel lists repos with state and `LastError`, and buttons Retry, Roll back, Remove. Buttons call the facade and show the per-repo result list afterwards.
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
7. Run WORKSPACE-SMOKE.
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
     5. The path is at least 2 levels below `featureStorageRoot`.
     If any guard fails: delete nothing, set `residueRemaining = true` and `residueMessage` to the guard that failed, and log a Warning.
- **Tests to add (real git, Windows-aware):** leftover untracked build output is removed; a file held open (`FileStream` with `FileShare.None`) produces `residueRemaining = true` with that file listed; empty feature root removed, non-empty kept; create into empty folder succeeds; **one test per safety guard** (path outside root, path equal to the primary checkout, registered worktree, folder with a `.git` directory, too shallow) proving nothing is deleted; **junction test**: a junction inside the worktree pointing to a folder outside it; after cleanup the target folder and its files still exist.
- **Done when:** tests pass.
- **Regression guard (R-D1a, R-D1b, R-D1c, X-1):** this is the only unit that recursively deletes folders, so the guards and their tests are mandatory, not optional. An old App that does not send `featureStorageRoot` gets today's behaviour (no residue deletion). Create into an existing folder is allowed only when the folder is empty and under `featureStorageRoot`.

### D2 App removal report and no swallowed branch failures

- **Goal:** After Remove, the user sees exactly what was removed and what was not, per repo; branch delete failures are reported, not hidden.
- **Ref:** F-5, F-6, U-16, 04 P0-5.
- **Read first:** `FeatureOps` `RemoveFeatureCoreAsync` (local/remote `DeleteBranch` failures only logged); the Git Changes file watcher service for a context (`rg -n "class .*GitChanges.*Monitor|FileSystemWatcher" GrayMoon/src`); `RemoveFeatureModal.razor`.
- **Touches:** `FeatureOps` (`RemoveFeatureCoreAsync`), the result type it returns, `RemoveFeatureModal.razor`, `GrayMoon.Desktop/README.md` bullet.
- **Steps:**
  1. Before removing, stop Git Changes monitoring for the context (call the existing stop/unregister method; ask the Agent to stop watching those paths if the watcher lives in the Agent).
  2. Pass `featureRootPath` and `featureStorageRoot` to the Agent remove (D1), computed with `WorkspaceContextPathResolver`.
  3. Collect a per-repo report: worktree removed (yes/no), local branch deleted (yes / kept because it has unmerged commits / failed + message), leftover files (count + sample).
  4. Database rows are deleted only when every worktree is unregistered. Leftover files or kept branches do not block row deletion, but they are listed.
  5. The modal shows the report after Remove: "Removed 6 worktrees, 6 local branches." plus a warning list for anything left, with "Open folder" (Desktop) for leftovers.
- **Tests to add:** branch delete fails -> report says kept with message, Feature still removed; Agent reports residue -> report lists it.
- **Done when:** tests pass; no `DeleteBranch` failure path only logs.
- **Regression guard (R-D2):** stop Git Changes monitoring **by Feature context id only**; the Workspace's monitoring must keep running. Restart the Feature's monitoring in a `finally` when Remove fails or is cancelled. Tests: Workspace monitoring stays active during a Feature remove; a failed remove restarts the Feature's monitoring.

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
6. Database: after these steps, the Dependencies page and Restore in the Workspace show only Workspace projects (no duplicates).
Docker setup (skip if DEC-1 = No):
7. Run the App in Docker with the Worker on the host. Repeat step 1. Expected: identical result to Desktop; no repo shows "Folder is already missing".
8. Stop the Worker and open Remove. Expected: "Could not check this repository", Remove disabled, no discard option.
Workspace regression:
9. In the Workspace, open Switch Branch for a repo whose target branch is held by a stray worktree (create one with git worktree add outside GrayMoon). Expected: cleanup offered and works as before.
10. Run WORKSPACE-SMOKE.
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
- **Tests to add:** a table-driven test with at least 30 names (valid: `feature/login`, `fix-123`, `user@host`; invalid: each rule above) in Common.Tests; case-only duplicate rejected in App.Tests.
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
- **Touches:** those files, Agent configuration, tests.
- **Steps:**
  1. App generates a random Worker secret on first start (stored like F1's key).
  2. The install flow writes the secret into the Worker's config (the same place the App URL is written).
  3. Worker sends `X-GrayMoon-Worker-Secret` on the hub connection and on the connector request.
  4. A **wrong** secret is always rejected (401), using a constant-time comparison (`CryptographicOperations.FixedTimeEquals`).
  5. A **missing** secret is handled by setting `Security:RequireWorkerSecret` (default **false** in v1): when false, the request is accepted, a Warning is logged once per Worker connection, and the App shows "Reinstall the Worker to finish securing GrayMoon" in its Worker status UI. When true, it gets 401. v1.1 flips the default.
  6. Desktop: `GrayMoon.Desktop/src/GrayMoon.Desktop/Services/WorkerInstaller.cs` writes the secret automatically on install and update.
- **Tests to add:** connector endpoint: wrong secret -> 401; right secret -> 200; missing secret with `RequireWorkerSecret=false` -> 200 plus warning; missing with `true` -> 401. Same three cases for the hub connection.
- **Done when:** tests pass; owner verifies Worker still connects after reinstall (GATE-4).
- **Regression guard (R-F2a, R-F2b, X-1):** without the Worker GrayMoon does nothing, so v1 must accept already-installed Workers that have no secret (step 5). The secret is stored next to the database and the install flow always rewrites it, so a reinstall always fixes a mismatch.

### F3 Block cross-site and rebinding requests

- **Goal:** A web page open in the browser cannot trigger push, sync or other actions on GrayMoon.
- **Ref:** 05 S3.
- **Read first:** `App/appsettings.json` (`AllowedHosts: "*"`), `App/Api/Endpoints/*.cs` (all `MapPost`), `Program.cs` middleware order.
- **Touches:** `Program.cs`, new small middleware, tests, Desktop App launch configuration (where Desktop passes settings to the App process), `docs/architecture/05-user-capability-reference.md`.
- **Steps:**
  1. Middleware for every non-GET request under `/api/` and `/repos/`:
     - If the request has an `Origin` header (browsers always send it on cross-site POSTs) and the Origin's host is not the request's own host or a loopback name, return 403.
     - If the request has an `Origin` header, also require header `X-GrayMoon-Request: 1`; a cross-site page cannot add a custom header without a CORS preflight, which GrayMoon does not allow.
     - Requests **without** an `Origin` header (scripts, `curl`, the Worker, Desktop's own HTTP client) pass exactly as today.
  2. Update GrayMoon's own browser-side callers to send the header: `rg -n "fetch\(" GrayMoon/src/GrayMoon.App/wwwroot` and any JS interop that posts to `/api/`.
  3. `AllowedHosts`: do **not** change the shared `appsettings.json` (it stays `*` for Docker). Desktop sets `AllowedHosts=localhost;127.0.0.1;[::1]` for the App process it starts, because Desktop always uses loopback. Docs explain how Docker users restrict it.
  4. Document both rules in the REST API section of `docs/architecture/05-user-capability-reference.md`.
- **Tests to add:** cross-site Origin POST -> 403; same-origin POST with header -> passes; same-origin POST without header -> 403; POST with no Origin (script) -> passes; GET unaffected; `/_blazor` and `/hub/agent` unaffected.
- **Done when:** tests pass.
- **Regression guard (R-F3a, R-F3b, R-F3c):** existing REST API scripts and the Worker send no `Origin` header and must keep working unchanged (test). Docker users who open GrayMoon by host name or LAN IP must not get HTTP 400, so `AllowedHosts` changes only for Desktop. The middleware applies only to `/api/` and `/repos/`. Note: GrayMoon's Git hooks post to the **Worker's** listener (`127.0.0.1:<port>/hook/*`, `HookListenerHostedService`), not to the App, so they are not affected by this unit.

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
   If you use REST API scripts, run one. Expected: it works as before.
5. Worker, before reinstalling: it still connects (with a "Reinstall the Worker" notice); Sync works.
   Reinstall the Worker from GrayMoon. Expected: it connects, the notice is gone, Sync works.
   Then open http://localhost:8384/repos/1/connector in the browser. Expected: no token shown (401 once RequireWorkerSecret is true; with the v1 default, check that the log warns about a request without the secret).
6. Hooks: in a test repo run git config core.hooksPath .husky, Sync. Expected: warning icon with the explanation; .husky contents unchanged.
   In another repo add your own .git/hooks/pre-push (e.g. echo "user hook"), Sync. Expected: your file unchanged, warning shown.
   In a repo where GrayMoon hooks already exist, commit from a terminal. Expected: grid updates within seconds (hooks still work).
   Unhook: Edit Workspace, untick 2 repos, save. Expected: their .git\hooks no longer contain GrayMoon hooks (your own hook files untouched); the remaining repos still have theirs.
   Commit in one of the removed repos. Expected: no new "not found" warning in the App log, no fetch or GitVersion activity in the Worker log.
   Tick them again, Sync. Expected: hooks are back and a terminal commit updates the grid.
   Stop the Worker, untick a repo, save, start the Worker. Expected: the removal succeeded with a warning in the log. Commit in that repo: the first ping removes the hooks (Information log line); later commits are quiet.
   Delete a test Workspace that has a Feature. Expected: refused with "Remove the Features first". Remove the Feature, delete the Workspace. Expected: its repos are unhooked; a repo folder you deleted by hand beforehand only produces a warning.
7. Run WORKSPACE-SMOKE.
Reply with PASSED, or the failing step and what you saw.
```

---

## Lane R - Release

### R1 Docs

- **Goal:** Users and support have what they need for v1.
- **Ref:** 01 sections 4.2 and 4.3; 04 checklist "Docs".
- **Touches:** new `GrayMoon/docs/user-guide/features.md`, new `GrayMoon/docs/user-guide/troubleshooting.md`, new `GrayMoon/CHANGELOG.md`, a superseded banner at the top of every file in `GrayMoon/docs/worktree/`, `GrayMoon/README.md`.
- **Steps:**
  1. `features.md`: what a Feature is; where it lives on disk; what Create copies and what it does not (uncommitted changes); switching; Open in...; what Remove deletes (worktrees, local branches) and keeps (remote branches unless D4); Needs attention and repair.
  2. `troubleshooting.md`: Feature stuck or needs attention; leftover folders; branch already exists; Worker not connected; hooks warning; manual cleanup recipe (`git worktree list`, `git worktree remove --force <path>`, `git worktree prune`, `git branch -D <name>`, delete `features\<name>`); where logs and DB backups are.
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
  2. Walk the checklist in `04-worktree-v1-release-roadmap.md` section "Release-readiness checklist"; tick each item with the unit that covered it, or add a unit for anything uncovered and stop.
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
- [08] Enforce the Worker secret by default (`Security:RequireWorkerSecret = true`) - v1.1, after users have reinstalled the Worker.
- [07] Hook chaining for repos with their own hooks or `core.hooksPath` (DEC-3 = chain) - v1.1.
