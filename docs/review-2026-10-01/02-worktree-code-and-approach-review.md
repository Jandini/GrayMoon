# Worktree Features — Code and Approach Review (2026-10-01)

Scope: the GrayMoon "Features" capability (one Git worktree per Workspace repository, per Feature), as implemented on branch `opus-review` (`a06fe33`, 80 commits / 345 files / +34,974 −2,763 over `origin/main` `22a1da3`) in `GrayMoon` and `GrayMoon.Desktop`.

Method: static reading of the code paths end to end, plus read-only inspection of the real environment on this machine:

- a copy of the live SQLite database (`%LOCALAPPDATA%\GrayMoon\Data\graymoon.db`, queried through a throwaway Microsoft.Data.Sqlite console outside the repo);
- `git worktree list` / `git branch` / remote refs in `C:\Workspace\GrayMoon\{GrayMoon,GrayMoon.Desktop}`;
- the Feature storage tree under `%USERPROFILE%\.graymoon\*\features\`.

No source code or existing doc was modified.

Severity scale: **Critical** (data loss or a broken primary flow in a supported deployment) · **High** (user gets stuck / state becomes inconsistent with no in-product way out) · **Medium** (wrong result or confusing behaviour with a workaround) · **Low** (polish, hardening, hygiene).

---

## 1. End-to-end map

### 1.1 Data model (App, SQLite)

| Entity | Purpose | Notes |
|---|---|---|
| `WorkspaceFeature` | One Feature per Workspace; `Name` = branch name; `LifecycleState` Creating / Ready / NeedsRepair / Removing; `LastError` | Unique index `(WorkspaceId, Name)` — BINARY collation (case-sensitive). |
| `WorkspaceFeatureContext` | `Kind` 0 = special Workspace (primary checkout), 1 = Feature | Every per-context projection hangs off this id. |
| `WorkspaceFeatureRepository` | Per Feature × repo: `WorktreePath`, `BaseCommitSha`, `ParentBranchName`, `PinnedTag`, `State`, `LastError` | `WorktreePath` stored forward-slashed (`C:/Users/...`) — confirmed in live DB. |
| `WorkspaceRepositoryContextState`, `…ContextPullRequests`, `…ContextActions`, Git-changes and file-context tables | Per-context projections that replaced the shared `WorkspaceRepositoryLink` columns | FK `ON DELETE CASCADE` to the context (works — zero orphans found in live DB). |
| `WorkspaceProjects.WorkspaceFeatureContextId`, `WorkspaceFileLineStatuses.WorkspaceFeatureContextId` | Per-context project graph / line statuses | Added by `ALTER TABLE … ADD COLUMN … NULL` **without FK** on upgraded DBs (`Data/Migrations.Features.cs:273-274`); fresh DBs get the EF-model FK (`AppDbContext.cs:261-264`). See F-2. |
| `Workspaces.ManagedFeatureStorageRoot` | Persisted Feature storage root (default `%USERPROFILE%\.graymoon`) | `WorkspaceFeatureOperations.EnsureManagedFeatureStorageRootAsync` (1072-1091). |

Migrations: hand-written in `Migrations.Features.cs` (expand → backfill → switch, contract not yet done; legacy link columns still read in places, see F-8). Fresh databases use `EnsureCreated`.

### 1.2 Paths on disk

`<ManagedFeatureStorageRoot>\<WorkspaceName>\features\<FeatureName>\<RepoName>` — resolved by `WorkspaceContextPathResolver` (Feature root = root + feature name; `/` in a name becomes nested directories). The design (§6.13) originally placed Features next to the Workspace root; the shipped layout keeps them in the profile directory. This review itself runs from such a worktree (`C:\Users\matth\.graymoon\GrayMoon\features\opus-review\GrayMoon`).

### 1.3 Agent (developer host)

| Command | File | Behaviour |
|---|---|---|
| `CreateGitWorktree` | `Agent/Commands/CreateGitWorktreeCommand.cs` → `GitService.CreateWorktreeAsync` (1384-1488) | Writes sync hooks into the **common** git dir, then `git worktree add -b <branch> <path> <sha>` (or `--detach <sha>` for tag-pinned repos), argument array (no shell), no `--force`; idempotent when the path already holds the branch; returns `WorktreePathConflict` / `BranchOccupied` / `PathExists` (even for an empty dir, 1439); verifies via `worktree list`; writes the worktree-private `graymoon-divergence-base` file. |
| `RemoveGitWorktree` | `GitService.RemoveWorktreeAsync` (1490-1554) | Absent from `worktree list` → success ("AlreadyRemoved"); refuses the primary worktree; `--force` only when asked; verifies. |
| `ListGitWorktrees` | `GitService.ListWorktreesAsync` (1359) | `git worktree list --porcelain`, parsed by `Common/Git/GitWorktreePorcelainParser.cs` (no `locked` support). |
| `GetHeadCommits` | `GetHeadCommitsCommand.cs` | HEAD sha, branch, tag and branch-collision probe per repo (parallel 8). Collision probe = `GitService.FindBranchCollisionsAsync` (208-226): `for-each-ref refs/heads/<n> refs/remotes/*/<n>`. |
| `DeleteBranch` | `GitService.DeleteBranchAsync` (816+) | Used by Remove Feature for local and (optionally) remote deletes. |

Concurrency inside the agent: `GitProcessRunner` serialises git per **working directory path** (`GetRepoLock`, 244-248). The primary checkout and each of its worktrees therefore have *different* locks although they share refs, `packed-refs`, config and objects. `GitRetryClassifier` retries `*.lock` contention, which mitigates but does not remove the race (F-14).

### 1.4 App services

- `Services/Features/WorkspaceFeatureOperations.cs` (1,181 lines) — Create, Analyze-remove, Remove, projection seeding, storage root.
- `WorkspaceExternalWorktreeOperations.cs` — analyse/remove worktrees GrayMoon did not create (used from Switch Branch when a target branch is occupied).
- `WorkspaceBranchOccupancyService.cs` — badges branches as Current / Feature / Workspace / external Worktree using `ListGitWorktrees` + DB rows.
- `WorkspaceContextPathResolver`, `WorkspaceFeatureContextResolver`, `WorkspaceSelectedContextService`, `WorkspaceContextNavigationService`.
- `WorkspaceOperationRunner` — in-memory hierarchical locks: **structural** (Create/Remove Feature, external worktree removal, legacy `TryStart`, all REST operations via `WorkspaceOperationsEndpoints.RunExclusiveAsync` 383-410) vs **per-context** (Sync, Push, Update, etc. scoped to one context). Different contexts run concurrently.
- `WorkspaceHookContextAttributor` — maps a hook callback's repo path to the owning context; unknown/ambiguous → ignored, legacy null path → special Workspace.
- `WorkspaceGitService.Sync.ApplyDefaultTipVersionsForMergedFeatureReposAsync` (310-381) — for merged Feature PRs, uses `GetGitVersionAtDefaultTip` so Update Dependencies writes default-branch versions.

### 1.5 SignalR / UI / Desktop

- Hub messages are now context-tagged (`?context=<id>` in the URL selects the context; `WorkspaceRepositories.razor.cs` 61-253 resolves, falls back with a toast on stale ids).
- UI: `Components/Features/WorkspaceFeatureSelector.razor`, `CreateFeatureModal.razor`, `RemoveFeatureModal.razor`; header primary button / Feature menu in `Components/Shared/WorkspaceRepositoriesHeader.razor`; occupancy badges and external-worktree cleanup in `Components/Modals/SwitchBranchModal.razor`.
- Desktop: `GrayMoon.Desktop/.../WebMessageBridgeService.cs` adds "Open in…" (Cursor, Claude CLI, VS Code, Visual Studio, Terminal, Explorer) for a Feature root or a repo worktree; `ToolAvailabilityService` detects installed tools.

### 1.6 Tests (worktree-related)

| Project | File | Tests | Real git? |
|---|---|---|---|
| Agent.Tests | `GitWorktreeCommandTests` | 6 (roundtrip, branch occupied, detached tag, collisions, dirty remove refused, primary refused) | Yes |
| App.Tests | `RemoveFeatureWorkspaceRefreshTests` | 14 | No (fake agent bridge, temp dirs on the *App* host) |
| App.Tests | `FeatureContextIsolationTests` | 10 | No |
| App.Tests | `CreateFeatureParentBranchTests` | 6 | No |
| App.Tests | `WorkspaceHookContextAttributorTests`, `WorkspaceContextNavigationServiceTests` | 6 + 6 | No |
| App.Tests | `WorkspaceOperationLockTests`, `WorkspaceFeatureContextMigrationTests` | 3 + 3 | No |
| Common.Tests | `GitWorktreeOccupancyTests`, `GitWorktreePorcelainParserTests` | 4 + 4 | No |

≈62 tests. Full suite run on 2026-10-01 (`dotnet test GrayMoon.slnx`, net10.0): **788 passed, 0 failed** (Common 179, App 444, Agent 165; Agent suite 2 m 20 s); one build warning `CS8619` in `WorkspaceGitService.Context.cs:38`. No component (bUnit) tests for any Feature UI. Not covered at all: partial-create failure and retry, cancellation/crash mid-create or mid-remove, invalid/Windows-hostile names, case-only duplicates, orphan directory cleanup, projections cleanup on remove, remote branch deletion, App and Agent on different hosts, Windows file locks during remove, long paths.

---

## 2. Findings

Each finding: severity · evidence · consequence · recommended fix (effort in the roadmap, `04-…`).

### F-1 — Critical — Remove analysis probes the App's filesystem, not the Agent's

- Evidence: `WorkspaceFeatureOperations.cs:340` `var exists = Directory.Exists(row.WorktreePath);`, `:732` (`ComposeRemoveWarning`), and `WorkspaceExternalWorktreeOperations.cs:79`. When `exists` is false, `ProbeFeatureWorktreeLiveStatusAsync` returns `Unavailable` **without asking the Agent** (667-668). `Classify` (1110-1125) turns any missing worktree into `NeedsRepair`; `IsAutomaticallySafe` (737-746) then returns false.
- Architecture contract: `docs/architecture/02-system-architecture.md:39` — "GrayMoon.App normally runs in Docker and cannot assume access to the developer's local repository paths." README:69-77 documents the Docker install as the primary deployment.
- Consequence in the Docker deployment (paths like `C:/Users/...` never exist inside a Linux container): every Feature analyses as "folder already missing", no dirty/staged/conflict probe runs, removal is never "automatically safe", and the only way to remove is to tick **Discard uncommitted changes**, which sends `force = true` to `git worktree remove` (497-500). Result: uncommitted work is destroyed without the user ever having been told it existed. The external-worktree path has the same flaw (and hard-codes `IsDirty = false`, line 80).
- Why it was not caught: it works under Desktop (App and Agent on the same Windows host), and `RemoveFeatureWorkspaceRefreshTests` creates the "worktree" directories on the App host (`Path.GetTempPath()`, lines 423-597).
- Fix: move existence + dirty probing to the Agent (one `InspectWorktree` command returning exists / registered / dirty / staged / conflicts / locked / head), never call `System.IO` on Agent paths in the App. Add a test that runs analysis with paths that do not exist on the App host but the fake Agent reports as present and dirty.

### F-2 — High — Removing a Feature leaves its project graph and line statuses behind (orphan rows); some queries then read across contexts

- Evidence: `Migrations.Features.cs:273-274` adds `WorkspaceFeatureContextId` to `WorkspaceProjects` and `WorkspaceFileLineStatuses` without an FK; `RemoveFeatureCoreAsync` (613-618) deletes only the feature, context and feature-repo rows and relies on cascade. Fresh DBs have the FK (`AppDbContext.cs:261-264`), upgraded DBs do not — schemas diverge.
- Live DB on this machine: ≈2,026 orphan `WorkspaceProjects` rows referencing 40 deleted contexts (ids 32, 34–73) vs ≈344 live rows (≈85 % of the table is orphaned), 499 orphan `ProjectDependencies`, ≈117 orphan `WorkspaceFileLineStatuses`.
- Queries still scoped by `WorkspaceId` only — so they include every context **and** the orphans:
  - `WorkspaceProjectRepository.cs:69-74` `GetByWorkspaceIdAsync`, used by `WorkspaceGitService.Restore.cs:72,113` (Restore All / Restore Synced restore the same project once per context, including deleted ones whose paths no longer exist).
  - `WorkspaceProjectRepository.DependencyGraph.cs:11,48,231` → Dependencies page (`WorkspaceDependencies.razor:420`).
  - `WorkspaceProjectRepository.DependencyLines.cs` (`GetDependencyEdgesAsync`, `GetPackageDependencyLinesByRepoAsync`, `GetPackageDependencyLinesForRepoAsync`, `GetMismatchedDependencyLinesForRepoAsync` — the last also compares against the shared link's `GitVersion`) → grid tooltips (`WorkspaceRepositories.Loading.cs:493-494`).
- Fix: (a) explicit delete of context-scoped projects / dependencies / line statuses inside the Remove transaction; (b) a migration that deletes orphans and rebuilds the two tables with the FK (SQLite needs table rebuild); (c) add `contextId` to every remaining `WorkspaceId`-only project query; (d) a schema-parity test (fresh vs upgraded DB).

### F-3 — High — Features can get permanently stuck in `Creating` or `Removing`, and are then invisible

- Evidence:
  - Create persists Feature (143), context (154) and repo rows (189) in three separate `SaveChanges` with no transaction; any exception, cancellation, App restart or Desktop close after line 143 leaves `LifecycleState = Creating` — the catch (71-75) only completes the TCS. Line 162 even returns early (`HeadCommitsIncomplete`) *after* the Feature and context were saved.
  - Remove sets `Removing` (453-455) and has the same exposure.
  - `WorkspaceFeatureSelector.razor:370-371` lists only Ready and NeedsRepair → stuck Features disappear from the UI, yet still own the name (`CreateFeatureCoreAsync` 92 → "DuplicateName"), the branches and the directories.
  - No startup reconciliation (design §28 / §25.7) exists.
- Fix: one reconciliation pass at startup and on Agent reconnect that converts `Creating`/`Removing` older than the current process to `NeedsRepair` with a reason; show all states in the selector; transaction for the intent rows.

### F-4 — High — `NeedsRepair` has no repair path, and partial creates leave real Git artefacts

- Evidence: per-repo failure marks the row `NeedsRepair` (224-235) but the successful worktrees and branches stay; the Feature gets the generic `"One or more worktrees failed to create."` (268); per-repo `LastError` is never shown in the create flow. There is no `RepairFeatureAsync` / retry API anywhere. The selector shows NeedsRepair rows **disabled** with the raw text "(NeedsRepair)", so the user cannot open the Feature to inspect it; the only action is Remove.
- Fix: "Retry failed repositories" (idempotent create is already supported by the Agent — `CreateWorktreeAsync` treats an existing matching worktree as success) and "Roll back" (remove the worktrees/branches that were created); surface per-repo errors in the modal.

### F-5 — High — Remove does not clean the disk, and Windows file locks silently leave full working trees behind

- Evidence (this machine): about 18 leftover directories under `%USERPROFILE%\.graymoon\*\features\`, 13 empty, the rest unregistered remnants without `.git` — e.g. `change-watcher` (GrayMoon 821 files / 77.8 MB; GrayMoon.Desktop `bin/obj/artifacts\bundle\app` 462 files), `swtich-workspace` (49 MB), `workspace-load` (49.1 MB), AVR `my-great-feature` (254 files). `git worktree list` in both repos shows only the primary and `opus-review`.
- Mechanism: `git worktree remove` deletes the admin dir even when it cannot delete locked files (running `dotnet`/IDE/`bin` locks). On retry, the Agent sees the worktree absent and reports success (`RemoveWorktreeAsync` "AlreadyRemoved"), and the App deletes the DB rows. Nothing removes residual files or the empty `features\<name>` folder (design §27.7 last step). Also, `CreateWorktreeAsync` rejects an existing directory even when empty (1439), so a leftover folder blocks re-creating a Feature with the same name.
- Missing: stop the GrayMoon file watchers for the context before removing; detect residue after `worktree remove`; retry/backoff on sharing violations; report "N files could not be deleted (in use by …)"; remove empty parents; allow create into an empty existing directory.

### F-6 — Medium — Branch-deletion failures are swallowed; remote delete is unreachable and unauthenticated

- Evidence: local/remote `DeleteBranch` failures are logged and ignored (536-540, 556-561), and the user sees "Feature removed." The remote delete payload carries no `bearerToken`, although `GitService.cs:1585-1586` states that this exact call "must also send bearerToken on the DeleteBranch payload". `RemoveFeatureModal.razor:218-219` never sets `DeleteRemoteBranches`, so the remote path is dead code today — origin of `GrayMoon.Desktop` still carries `cosmetics`, `feature-error`, `parallel-feature`, `swtich-workspace`, `workspace-load` from removed Features.
- Risk once enabled: collisions are checked against possibly stale `refs/remotes/*` (`FindBranchCollisionsAsync`, no fetch first, and it fails open — returns `[]` on error), so a same-named branch pushed by a colleague can be deleted. Use `push --force-with-lease=<branch>:<expected-sha>` style delete, or compare the remote tip to the Feature's last pushed sha.

### F-7 — Medium — Feature-name validation is a hand-written regex, not Git's rules, and ignores Windows

- Evidence: `WorkspaceFeatureOperations.cs:35`. Accepts names Git rejects (`*.lock`, leading `-` or `.`, trailing `.`, control chars) and names Windows cannot use as folders (`<>|"`, `CON`, `NUL`, trailing space/dot in a segment); rejects names Git accepts (`@` anywhere). Duplicate check (92) and unique index are case-sensitive while the folder is case-insensitive on Windows → `Foo` and `foo` both pass validation and collide on disk. `GitWorktreeOccupancy.FindByBranch` compares branches case-insensitively, i.e. the opposite mistake.
- Fix: validate via Agent `git check-ref-format --branch`, plus a Windows path-segment check and case-insensitive duplicate check; validate live in the modal.

### F-8 — Medium — Remaining context leaks / legacy shared-link reads

Known and still open (from `…Dependency-And-Wiring-Gaps-2026-09-21.md` "remaining" list, re-verified): generated packages not context-scoped; `IWorkspaceFileOperations.ListAsync` legacy; `GetPushPlanPayloadAsync` tag exclusion reads the shared link's `CheckedOutTag`; notification panel `repoIdsThatNeedPush` uses shared links; plus the project queries in F-2. Bulk `Components/Modals/BranchModal.razor` has no occupancy awareness (only `IsSelectedBranchWorkspaceCurrent`, line 278), so switching all repos to a branch held by a Feature fails per repo mid-run.

### F-9 — Medium — Remove analysis mixes live and cached facts

- Evidence: dirty/staged/conflicts are live (good), but `OutgoingCommits`, `HasUpstream`, PR state and `MergedAt` come from the last Sync (`state?.OutgoingCommits`, `pr?.MergedAt`, 361-365). `IsAutomaticallySafe` treats `null` outgoing as 0 (743). A Feature committed but never synced since, or with no upstream, can be classified "Completed / safe".
- Mitigation that exists: local delete is `branch -d` (non-force) unless the box is ticked, so Git keeps unmerged commits — but the failure is swallowed (F-6), so the user is told "removed" while a branch remains and later blocks re-using the name ("BranchExists").
- Fix: compute ahead/behind vs upstream and vs `origin/<default>` live in the Agent inspection (F-1), refresh PR state before classification.

### F-10 — Medium — Migration errors are swallowed

- Evidence: `Migrations.Features.cs:31-34` wraps the Feature migration body in `catch { }`. A half-applied migration (e.g. lock, disk full) leaves the app starting on an unknown schema with no log line. The backfill also does N+1 `AnyAsync` per row.
- Fix: log and fail startup (or mark the DB as needing repair); make each step idempotent and transactional.

### F-11 — Medium — Desktop "Open in Claude CLI" fallback breaks on `&` in paths

- Evidence: `WebMessageBridgeService.cs:507` `cmd.exe /c start "" cmd.exe /k "cd /d "{path}" && claude"`. For the outer `cmd /c` the quotes pair as `"cd /d "` … `" && claude"`, leaving `{path}` unquoted; Feature names may contain `&` (F-7), so a folder such as `features\a&b\Repo` splits the command (at best the terminal fails to open; at worst a second command runs). Line 500/535 use `wt.exe -d "{path}"`; Windows Terminal treats `;` as its own sub-command separator, so `;` in names is also unsafe (not reproduced). Only the fallback without `wt.exe` is affected for `&`.
- Fix: pass `WorkingDirectory` in `ProcessStartInfo` instead of `cd /d`, use `ArgumentList`, and forbid shell metacharacters in Feature names.

### F-12 — Low — Long paths and locked worktrees are not handled

- No `core.longpaths` anywhere in the repo; Feature paths are 25–40 characters deeper than the primary checkout (`C:\Users\<u>\.graymoon\<Ws>\features\<name>\<Repo>`), which matters for `node_modules`/deep `obj` trees on Windows.
- `GitWorktreePorcelainParser` ignores `locked`; `RemoveWorktreeAsync` will fail on a locked worktree (needs `--force --force`) with a raw Git message.

### F-13 — Low — Occupancy/listing fails open

`WorkspaceBranchOccupancyService` turns a failed `ListGitWorktrees` into "no worktrees", and `FindBranchCollisionsAsync` returns `[]` on error. Git itself still refuses double checkouts, so the cost is a worse error message, not corruption. DB-owned Feature names are also badged "Feature" in tag-pinned repos where the Feature never created that branch.

### F-14 — Low — Per-path agent lock does not cover the shared common git dir

`GitProcessRunner.GetRepoLock` (244-248) keys by working-dir path; a Feature Sync and a Workspace Sync can `fetch` into the same `refs/remotes` concurrently (allowed by the per-context lock design). `GitRetryClassifier` retries `*.lock` errors, so expect occasional retries/slowdowns rather than corruption. Key the lock on `git rev-parse --git-common-dir` for ref-writing commands.

### F-15 — Low — Logging is thin for an operation that mutates many repos

`WorkspaceFeatureOperations` logs only warnings (327, 483, 504, 537, 558, 652, 699, 723, 1003); no Information-level start/finish for Create/Remove, no structured `FeatureId`/`ContextId`/`WorkspaceId` on most, and per-repo create failures are stored in `LastError` but never logged. There is no telemetry/counter for create/remove success, duration, or NeedsRepair.

### F-16 — Low — Repository hygiene

`test-agent.txt`, `test-app.txt`, `test-common.txt` (UTF-16 test-run logs) are committed at the repo root (commit `45583d1`). `ReturnToDefault` is hidden for Feature contexts only in the UI (`WorkspaceRepositories.razor:387,409`); the service has no guard (Git would refuse checking out `main` in a worktree anyway).

### What is good (evidence-backed)

- The Agent worktree primitives are careful: argument arrays, no `--force` by default, idempotent create, refusal to remove the primary, verification after each mutation, explicit error codes (`GitService.cs:1384-1554`), with real-git tests.
- The per-context projection model is consistently applied to state, PRs, Actions and Git Changes; cascade FKs on those tables work (zero orphan context states in the live DB).
- Hooks are installed in the common dir and attributed by path with a safe "unknown → ignore" rule (`WorkspaceHookContextAttributor`).
- Remove distinguishes "discard dirty files" from "force-delete unmerged branch", probes dirty/staged/conflict state live, and refuses to report success when any worktree removal failed (578-597, covered by tests).
- Tag-pinned repos stay detached in Features and never get a Feature branch — a thoughtful edge case, tested.

---

## 3. Status of the 2026-09-21 review items

| Source doc | Item | Status 2026-10-01 | Evidence |
|---|---|---|---|
| Code-Review-Findings | 1. `WriteSyncHooks` crashes on worktree `.git` file | **Fixed** | Uses `--git-common-dir` (`GitService.cs:1337-1357`) |
| Code-Review-Findings | 2. Feature header button | **Fixed** | `WorkspaceRepositoriesHeader.razor:56-137` |
| Code-Review-Findings | 3. Grid not context-aware | **Fixed** (per Wiring-Gaps items 1-9) | per-context state tables; but see F-2/F-8 for residual project queries |
| Code-Review-Findings | 4. Remove dialog copy | **Fixed** | `RemoveFeatureModal.razor` plain-language classification |
| Code-Review-Findings | 5. Stale selector crashes circuit | **Fixed** | selector reloads on open; fallback in `WorkspaceRepositories.razor.cs:89-108` |
| Code-Review-Findings | Bonus: PR reconcile corrupting link | **Fixed** | per-context PR table |
| Code-Review-Findings | 2.2 Actions per context / 2.4 PR badges / 28A | **Done** | per-context tables |
| Wiring-Gaps | Items 1–9 | **Done** | as stated in doc, spot-checked |
| Wiring-Gaps | Generated packages not context-scoped | **Open** | F-8 |
| Wiring-Gaps | `IWorkspaceFileOperations.ListAsync`, dependency graph visualisation legacy | **Open** | F-2 / F-8 (`DependencyGraph.cs:11,48,231`) |
| Wiring-Gaps | Bulk `BranchModal` no occupancy awareness | **Open** | `BranchModal.razor:278` |
| Wiring-Gaps | `GetPushPlanPayloadAsync` tag exclusion via link `CheckedOutTag` | **Open** | F-8 |
| Wiring-Gaps | Notification panel `repoIdsThatNeedPush` from shared links | **Open** | F-8 |
| UX-Gaps | O3–O8, versions-from-default | **Done** | `ApplyDefaultTipVersionsForMergedFeatureReposAsync` (`WorkspaceGitService.Sync.cs:310-381`) |
| UX-Gaps | O1 "Based on" choice, O2 tree view | **Deferred** | `CreateFeatureModal` has a disabled "Based on" field; `UnsupportedBaseKind` (52-53) |
| UX-Gaps | Spike: `/c origin-default-sha` GitVersion parity | **Open** | no parity test found |
| (new) | F-1 … F-16 above | **New in this review** | — |

---

## 4. Approach and model review

### 4.1 The model

"A Feature is one branch name, materialised as a worktree in every repository of the Workspace, plus a full copy of GrayMoon's per-repo projections" is the right model for GrayMoon's multi-repo problem: a cross-repo change gets isolated working copies, its own dependency levels, its own PRs and its own version propagation, while the primary checkout keeps running. Worktrees (vs. extra clones) are the correct primitive: shared object store, instant creation, Git-enforced "one branch, one checkout".

Weak points of the model as built:

1. **All repos, always.** Every Feature creates a worktree in every repository (`links` = all Workspace repos, 101-105). For a 15-repo Workspace and a change touching 2, that is 13 unneeded worktrees, branches and (with F-5) 13 sets of residue. VS Code / Rider / `git worktree` users choose per repo. A "repositories in this Feature" selection (default: all, or the ones you pick; others resolve from the Workspace) would cut cost and noise. This is the biggest model decision to settle before v1 because it affects paths, projections and dependency resolution.
2. **Name = branch = folder** with no mapping. Convenient, but it couples three namespaces with different rules (Git refs, Windows paths, case sensitivity) — F-7, F-11. Keep the identity, but store a sanitised folder slug separately.
3. **State duplication by copy.** `SeedInitialFeatureProjectionsAsync` (776-1014) copies the Workspace's state/projects/deps into the Feature. Copy-on-create is simple, but every new per-context table must remember to (a) seed, (b) scope its queries, (c) cascade on delete — F-2 shows (c) and parts of (b) were missed. A single "context-scoped" base convention plus an architecture test that fails when a table has `WorkspaceId` but no `WorkspaceFeatureContextId` filter would make this durable.
4. **Two sources of truth without reconciliation.** DB rows (intent) vs `git worktree list` + disk (reality). The design calls for reconciliation (§28); none runs. Every High finding above (F-3, F-4, F-5) is a variant of "DB and disk disagree and nothing notices".
5. **App/Agent split ignored once.** F-1 is a model violation, not just a bug: all disk facts must come from the Agent.

### 4.2 Comparison with other tools

| Concern | git CLI | VS Code (recent built-in worktree support / GitLens) | Rider / IntelliJ (recent worktree support) | GrayMoon Features |
|---|---|---|---|---|
| Unit | one repo | one repo | one repo | **N repos at once** (unique strength) |
| Create | `worktree add [-b]`, any base | pick branch/base, choose folder | pick branch, folder | always from current Workspace HEADs, fixed folder, new branch only |
| Existing branch → worktree | yes | yes | yes | **no** (name must not exist; F-6/F-9 make leftovers block re-use) |
| Open | `cd` | opens new window | opens new window | in-app context switch + Desktop "Open in…" (good) |
| Dirty-safe remove | refuses unless `--force` | prompts | prompts | live probe + two explicit opt-ins (good) — broken under Docker (F-1) |
| Prune / repair | `worktree prune`, `repair` | prune command | — | **none** (F-3, F-4, F-5) |
| Locked worktrees | `lock`/`unlock` | shows lock | — | ignored (F-12) |
| Cross-repo dependency versions | — | — | — | per-Feature levels + default-tip versions after merge (unique strength) |

Takeaways: GrayMoon's differentiators (multi-repo atomic Feature, per-Feature dependency propagation, PR roll-up) are real and well-built in the core. The gaps are exactly the "boring" lifecycle operations every single-repo tool ships: prune/repair, adopt an existing branch, choose repos, and honest cleanup. Recommended additions, in order: Repair/Retry, Reconcile on startup, adopt-existing-branch, repo subset.

### 4.3 Duplication / structure

- `WorkspaceFeatureOperations` (1,181 lines) mixes orchestration, Git command payload building, projection seeding and classification; Create and Remove repeat the same "parallel per-repo agent call with progress + gate" pattern (194-259, 486-575) and the same TCS-over-`TryStartStructural` wrapper (55-82, 413-440, and again in `WorkspaceExternalWorktreeOperations` 133-172). Extract a per-repo fan-out helper and a `RunStructuralAsync<T>`.
- Agent payloads are anonymous objects with string command names (`"DeleteBranch"`, `"GetGitChangeStatus"`) next to `AgentHubMethods.*` constants — mixed style, no compile-time contract.
