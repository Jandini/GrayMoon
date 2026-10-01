# Worktree Features — v1 Release Roadmap (2026-10-01)

Inputs: `02-worktree-code-and-approach-review.md` (findings F-1…F-16) and `03-worktree-ux-review.md` (U-1…U-20). Effort estimates assume one developer who knows the codebase: **S** ≤ 1 day, **M** 2–3 days, **L** about 1 week.

Definitions:
- **P0** must be fixed before v1 ships: data loss, a broken primary flow in a supported deployment, or a state the user cannot get out of.
- **P1** should ship in v1.
- **P2** is fine for v1.x.

Baseline: the full suite passes. On 2026-10-01, `dotnet test GrayMoon.slnx` gave 788 passed and 0 failed (Common 179, App 444, Agent 165).

---

## P0 — release blockers

### P0-1 · Have the Agent establish worktree existence and dirty state (F-1, U-15) · M

- **Evidence:** `WorkspaceFeatureOperations.cs:340` and `:732` call `Directory.Exists(row.WorktreePath)` in the App. When that returns false, `:667-668` skips the live probe. `WorkspaceExternalWorktreeOperations.cs:79-80` does the same and hard-codes `IsDirty = false`. The architecture doc (`docs/architecture/02-system-architecture.md:39`) says the App runs in Docker without host paths.
- **Impact:** in Docker, the analysis reports every repo as missing. Removal is then possible only via "discard uncommitted", which forces deletion of uncommitted work the user was never warned about.
- **Fix:**
  - Add an Agent command `InspectWorktree(mainRepoPath, worktreePath)` returning registered, exists, dirty, staged, conflicted, locked, HEAD, upstream ahead/behind and default-branch ahead.
  - Use it in both analyses and remove every `System.IO` call on Agent paths from the App.
  - Add an architecture test that fails if `System.IO.Directory` or `System.IO.File` is used under `Services/Features`.
  - Add a regression test: the path is absent on the App host, and the fake Agent reports it present and dirty. The plan must say dirty and not safe.

### P0-2 · Clean up per-context projections on Remove; add FK parity (F-2, U-13) · M

- **Evidence:**
  - `Migrations.Features.cs:273-274` adds the context columns without an FK. `RemoveFeatureCoreAsync` (`:613-618`) relies on cascade to clean them up.
  - The live DB holds about 2,026 orphan `WorkspaceProjects` rows (about 85 % of the table), 499 orphan `ProjectDependencies` and about 117 orphan line statuses.
  - Several readers are scoped only by `WorkspaceId`: `WorkspaceProjectRepository.cs:69-74` (used by `WorkspaceGitService.Restore.cs:72,113`), `…DependencyGraph.cs:11,48,231` and `…DependencyLines.cs`.
- **Fix:**
  1. Explicitly delete context-scoped projects, dependencies and line statuses in the Remove transaction.
  2. Write a one-time migration that deletes orphans and rebuilds both tables with `FOREIGN KEY … ON DELETE CASCADE`. SQLite has no `ADD CONSTRAINT`, so the migration must create the new table, copy the rows, drop the old table and rename.
  3. Add a context id to every remaining `WorkspaceId`-only project query.
  4. Add a test comparing fresh and upgraded schemas.

### P0-3 · Recover stuck Features; make create intent atomic (F-3, U-2) · M

- **Evidence:**
  - Create saves the feature, the context and the per-repo rows separately (`:143`, `:154`, `:189`). Nothing runs on exception or cancel (`:71-75`, `:429-433`).
  - There is an early return after the rows are persisted (`:162`).
  - The selector hides `Creating` and `Removing` Features (`WorkspaceFeatureSelector.razor:370-371`).
  - The duplicate-name check still blocks the hidden name (`:92`).
- **Fix:**
  - Write the Feature, context and Pending rows in one transaction.
  - At startup and on Agent reconnect, reconcile:
    - Any `Creating` or `Removing` Feature not owned by a running operation becomes `NeedsRepair` with a reason.
    - Compare DB rows with `git worktree list` and disk (per design §28).
  - List every lifecycle state in the selector.

### P0-4 · Add a repair path for NeedsRepair (F-4, U-1, U-8, U-9) · M

- **Evidence:** failed creates keep their successful worktrees and branches and report a generic error (`:224-235`, `:265-271`). No repair API exists. NeedsRepair rows can't be selected (`WorkspaceFeatureSelector.razor:313-315`).
- **Fix:**
  - Add `RepairFeatureAsync`:
    - It retries Pending or NeedsRepair repos. `CreateWorktreeAsync` is already idempotent.
    - It re-runs projection seeding.
  - Add `RollbackFeatureAsync`, which removes the worktrees and branches that were created.
  - Add a status panel showing each repo's `LastError`, with Retry, Roll back and Remove buttons.
  - Let users open NeedsRepair Features read-only.

### P0-5 · Make removal honest about disk and branches (F-5, F-6, U-16) · M

- **Evidence:**
  - This machine has about 18 leftover `features\<name>` folders, 13 of them empty. The rest are unregistered remnants up to 77.8 MB.
  - `RemoveWorktreeAsync` treats a worktree that is "absent from the list" as success, even when files remain.
  - Branch-delete failures are only logged (`:536-561`).
  - `CreateWorktreeAsync` rejects an empty existing directory (`GitService.cs:1439`).
- **Fix:**
  - Stop the context's file watchers before removing.
  - After `worktree remove`, delete the residue with retry/backoff on sharing violations, then remove empty `features\<name>` parents.
  - Return a per-repo report covering the worktree, the local branch, leftover files and the locking process where available, and show it.
  - Allow create into an empty existing directory.

Remote branch deletion is P1 (P1-3). Today it is dead code, not a correctness risk.

---

## P1 — should ship in v1

| ID | Item | Evidence | Fix | Effort |
|---|---|---|---|---|
| P1-1 | Validate names with Git and Windows rules; make duplicate checks case-insensitive | `WorkspaceFeatureOperations.cs:35`, `:92`; unique index `(WorkspaceId, Name)` BINARY; `GitWorktreeOccupancy.FindByBranch` is OrdinalIgnoreCase (F-7, U-3) | Agent `git check-ref-format --branch`; reject Windows-illegal segments, reserved names, trailing dot/space and shell metacharacters (`&;|<>"%^`); case-insensitive duplicate check plus a `COLLATE NOCASE` index; inline validation in the modal | S–M |
| P1-2 | Live outgoing/PR facts in remove analysis | `:361-365` cached; `IsAutomaticallySafe` treats null as 0 (`:743`) (F-9, U-18) | Use the P0-1 `InspectWorktree` ahead counts; refresh PRs before classifying; reword "Completed" when no PR exists | S (after P0-1) |
| P1-3 | Remote branch deletion option, done safely | Modal never sets `DeleteRemoteBranches` (`RemoveFeatureModal.razor:218-219`); no `bearerToken` (`GitService.cs:1585-1586` says it is required); stale/fail-open collision check (`GitService.cs:208-226`) | Opt-in checkbox listing branches; fetch first; delete only if the remote tip equals the last pushed Feature SHA (lease); pass the token; report results | M |
| P1-4 | Tie Remove checkboxes to the conditions present | `RemoveFeatureModal.razor:45-56`, `:62-63` (U-17) | Show "discard" only when dirty and "force branch" only when there are unpushed commits; enable Remove only when every applicable box is ticked | S |
| P1-5 | Header primary action for fresh Features | `WorkspaceRepositoriesHeader.razor:461-462` (U-12) | Show "Remove" as primary only when the PR is merged or closed; otherwise show "Open in…" or a disabled "Create PR" | S |
| P1-6 | Fail loudly on migration errors | `Migrations.Features.cs:31-34` `catch { }` (F-10) | Log, abort startup with a clear message, keep steps idempotent; remove the N+1 backfill | S |
| P1-7 | Close the remaining context leaks | Wiring-Gaps "remaining" list re-verified (F-8): generated packages, `IWorkspaceFileOperations.ListAsync`, `GetPushPlanPayloadAsync` tag exclusion, notification `repoIdsThatNeedPush`, bulk `BranchModal.razor:278` occupancy | Scope each by context id; add occupancy badges and pre-flight to bulk switch | M |
| P1-8 | Desktop launch quoting | `WebMessageBridgeService.cs:507` (unquoted path in outer `cmd /c`), `:500`/`:535` `wt.exe -d` and `;` (F-11) | Use `ProcessStartInfo.WorkingDirectory` plus `ArgumentList`; drop the `cd /d` string building | S |
| P1-9 | Structured logging for Feature operations | Warnings only; no FeatureId/ContextId on most; create failures not logged (F-15) | Information start/finish with `{WorkspaceId} {FeatureId} {ContextId} {Repo} {DurationMs} {Outcome}`; log every per-repo failure | S |
| P1-10 | Remove committed test logs | `test-agent.txt`, `test-app.txt`, `test-common.txt` at repo root (commit `45583d1`) (F-16) | Delete them and add `test-*.txt` to `.gitignore` | S |
| P1-11 | GitVersion parity spike | UX-Gaps open spike `/c origin-default-sha` | Test that `GetGitVersionAtDefaultTip` equals GitVersion run on a real checkout of `origin/<default>` | S–M |

## P2 — v1.x

| ID | Item | Evidence | Fix | Effort |
|---|---|---|---|---|
| P2-1 | Choose which repos are in a Feature | All repos always (`:101-105`); see approach §4.1 | Repo picker on create; unselected repos resolve from Workspace | L |
| P2-2 | Adopt an existing branch as a Feature | Collision check blocks existing names (`:120-130`) | "Create from existing branch" uses `worktree add <path> <branch>` | M |
| P2-3 | `core.longpaths` and locked worktrees | Not configured anywhere; parser ignores `locked` (F-12) | Set `core.longpaths=true` on the worktree config (Windows); parse `locked`, show the reason, offer unlock | S |
| P2-4 | Shared-git-dir agent lock | `GitProcessRunner.GetRepoLock` keyed per working dir (`:244-248`) (F-14) | Key ref-writing commands on `--git-common-dir` | S |
| P2-5 | Selector type-ahead and Enter-to-create; consistent terminology | Design §22.2; U-6, U-7 | Filter input; "Create '<typed>'" row; use "Feature" everywhere | S–M |
| P2-6 | Create preview and per-repo base display | U-4, U-5 | Show repo list, target folder and per-repo `ParentBranchName` | S |
| P2-7 | Occupancy fails open | `WorkspaceBranchOccupancyService` empty on list failure; `FindBranchCollisionsAsync` returns `[]` on error (F-13) | Surface "could not check" instead of "free" | S |
| P2-8 | Refactor `WorkspaceFeatureOperations` | 1,181 lines; duplicated fan-out and TCS wrappers (approach §4.3) | Extract per-repo fan-out and `RunStructuralAsync<T>`; typed agent payloads | M |
| P2-9 | "Based on" options (O1) and tree view (O2) | Deferred in UX-Gaps | Per original design | L |
| P2-10 | Service-level guard for Return to Default in Feature contexts | Hidden only in UI (`WorkspaceRepositories.razor:387,409`) | Reject in `WorkspaceGitService.ReturnToDefault*` | S |

Rough total: P0 is about 3 weeks for one developer, and P0 plus P1 is about 5 weeks. P0-1, P0-2 and P0-3 are independent and can run in parallel. P0-4 depends on P0-3, and P1-2 depends on P0-1.

---

## Release-readiness checklist

### Tests
- [ ] Regression tests for each P0, including:
  - [ ] Docker topology: path absent on the App host, present and dirty on the Agent.
  - [ ] Orphan-free remove: projects, dependencies and line statuses gone; fresh and upgraded schemas match.
  - [ ] Crash mid-create and mid-remove, followed by reconciliation.
  - [ ] Partial create, then retry and rollback.
  - [ ] Remove with a locked file in the worktree (Windows-only test, hold an open handle).
- [ ] Real-git Agent tests for empty pre-existing directory create, locked worktree, `check-ref-format` names, case-only duplicates, and paths over 260 characters.
- [ ] bUnit tests for the selector (all lifecycle states), the Create modal (validation, failure, retry) and the Remove modal (checkbox gating, report).
- [ ] An architecture test that fails if App code under `Services/Features` touches `System.IO` on Agent paths, or if any `WorkspaceId`-only query targets a context-scoped table.
- [ ] Full suite green on Windows and in the Linux container build. The current 788 tests pass on Windows. Fix the `CS8619` warning (`WorkspaceGitService.Context.cs:38`).

### Docs
- [ ] User doc: what a Feature is, where it lives on disk, what Remove deletes (worktrees, local branches, and optionally remote branches) and what it keeps, and how to repair.
- [ ] Ops doc: the Feature storage root setting, behaviour when the App runs in Docker, and log fields.
- [ ] Update `docs/worktree/*` status tables. This needs the owner of those docs.

### Migration safety
- [ ] Back up the DB before the Feature migration runs (copy `graymoon.db` with `-wal`/`-shm`).
- [ ] Migration steps are idempotent and transactional. No `catch { }`.
- [ ] The orphan cleanup and FK rebuild is tested against a copy of a real upgraded DB. This machine's DB, with about 2k orphan rows, is a good fixture.
- [ ] A startup check refuses to run if the schema doesn't match the expected FK set.

### Rollback
- [ ] Downgrade path: v1 adds no columns the previous build can't ignore, or there is a documented "restore DB backup" step.
- [ ] A feature flag can hide Features UI and creation while existing worktrees stay usable from plain Git.
- [ ] Document a manual cleanup recipe: `git worktree list`, `git worktree remove --force`, `git worktree prune`, `git branch -D <name>`, and deleting `features\<name>`.

### Telemetry and logging
- [ ] Structured Information logs for Create, Remove, Repair and Reconcile, carrying WorkspaceId, FeatureId, ContextId, repo, duration and outcome.
- [ ] Counters for features created/removed, NeedsRepair entered, reconcile fixes applied, residue bytes left after remove, and remove-with-force usage.
- [ ] Log every per-repo Agent failure with the Git stderr. Today these are stored in `LastError` but not logged.
- [ ] A diagnostic export (or admin page) listing DB Features against `git worktree list` against disk, for support.
