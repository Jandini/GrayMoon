# Read-only (reference) repositories - implementation plan

Status: plan only, nothing is built. Written 2026-11-11 against commit `1515d32b` (branch `readonly-repos`). Reviewed against the code on 2026-11-12; the owner answered decisions D1-D12 the same day, all as recommended.

Design source: [ideas/read-only-repositories](ideas/read-only-repositories.md). That note holds the decisions; this plan holds the order of work, the code that changes, and the tests. Where the two disagree, section 2 says so and names the decision that is needed.

Line numbers are from `1515d32b`. Most were collected by a code survey, not all were re-read by hand; Phase 0A re-verifies every one before it is relied on.

## 1. Working rules

- **Order is fixed:** 0A baseline -> 0B central policy (existing semantics only) -> 0C gate -> Phase 1 -> Phase 2 -> reference gate. No reference flag, migration, manifest change or toggle before 0C has passed.
- **Stop at every phase boundary** (0A, 0B, 0C, each Phase 1 work package group, Phase 2) and wait for the owner's go-ahead. Phase 1 is large; it has check-in points of its own (section 8).
- **No commits or pushes** by the implementer. Changes stay uncommitted for the owner to review. Each phase must still be a separately reviewable diff: do not start the next phase on top of an unreviewed one.
- **A gate that did not run is a gate that did not pass.** Record commands, environment and results in `docs/todo/read-only-repositories-test-log.md` (created in 0A, deleted when the feature ships).
- Repository conventions apply: CRLF, ASCII hyphen only, `sealed`, primary constructors, Worker DTO rules, `AGENTS.md` sections for DbContext, Feature-context scoping, page jobs, migrations and Worker input.
- **The manual smoke list is run by the owner** at both gates (D4). An implementer that cannot drive the running App records the list as "not run" and the gate as "not passed".
- **Where the work happens:** the code is in the Workspace at `C:\Workspace\GrayMoon`; the application repository is `C:\Workspace\GrayMoon\GrayMoon` (branch `readonly-repos`), and that is the "repository root" every command and path in this plan is relative to. The `readonly-repos` Feature folder under `.graymoon\GrayMoon\features` was removed on 2026-11-11: nothing is read from it or written to it, even when a session starts there.

## 2. What the code says that the design note does not

These findings change the work. Decisions D1-D12 were answered by the owner on 2026-11-12; the answers are written into the rows and into the list below the table.

| # | Finding | Consequence |
|---|---|---|
| F1 | Pull is not fast-forward only. `GitService.PullAsync` runs plain `git pull origin <branch>` (`GitService.cs:405`); a conflict is aborted with `git merge --abort`. | Reference pull needs a new fast-forward-only path in the Worker. |
| F2 | Pull and push are one Worker command. `CommitSyncRepositoryCommand` pushes whenever outgoing > 0 after the pull (`:70-73`); `PushRepositoryCommand` pulls before it pushes. | A reference repository must never reach `CommitSyncRepository` as it is today. Add a pull-only mode. |
| F3 | The per-repository branch dialog is `SwitchBranchModal.razor`, not `BranchModal`. `BranchModal` is the Workspace-wide Create/Switch dialog and holds the "Skip repos on tags" checkbox. | The Read-only checkbox goes into the `SwitchBranchModal` footer (`:525-606`). The note should be corrected. |
| F4 | "Merge Recent Commits" does not exist. The nearest action is Update Branch from Default (`WorkspaceBranchUpdateHandler.UpdateBranchFromDefaultAsync`), guarded only in the UI. | Policy operation `UpdateBranchFromDefault`; service-side check is new. |
| F5 | Several operations have a tag check only in the page, none in the service: pull (`WorkspaceCommitSyncHandler`), bulk Create Branch and Prepare Workspace (`SkipReposOnTags` is page-only; REST `/prepare-workspace` with no ids branches tag-pinned repositories), Workspace-wide checkout, Git Changes commit/stage, Update Branch from Default, Actions bulk rerun. | Closing these in 0B would change REST behavior for tags, which breaks "no behavior change". **D1, decided:** 0B keeps them as characterized gaps; step 0D after the gate closes them on purpose, with its own tests, before Phase 1. |
| F6 | Update reads the tag from the shared link in every context (`DependencyUpdateOrchestrator.cs:370/420/559`, `WorkspaceGitService.Projects.cs:46/141/191/215/254`, `.Commit.cs:34/134`); push, restore and needs-push read per-context state. In a Feature the two can disagree. | **D2, decided:** 0B preserves this (a `LegacySharedLink` subject source on those call sites); 0D fixes it. |
| F7 | The per-context tag rule is written five times and the copies differ for a Feature repository with no state row (`WorkspaceProjectRepository.Push.cs:159`, `WorkspacePushService.cs:1643`, `WorkspaceGitService.Restore.cs:148`, `WorkspaceRepository.cs:330` and `:462`). | 0B replaces all five with one subject loader; the "no state row" difference is characterized first and kept per call site. |
| F8 | Three tag tests exist: SQL `== null \|\| == ""`, Feature SQL `IsNullOrEmpty`, in-memory `IsNullOrWhiteSpace`. | 0A proves no writer stores a whitespace-only tag (`WorkspaceRepositoryStateWriter` uses `Blank`). If proven, one test is used everywhere and that is not a behavior change. **0A:** proven for current writers - the state writer normalizes blank to null (`WorkspaceRepositoryStateWriter.cs:224-249`, `:280-291`), so no Worker or user input can store a whitespace-only tag. A legacy row that already holds one would be copied verbatim by three paths (`WorkspaceRepositoryStateWriter.cs:203`, `Migrations.Features.cs:364`, `WorkspaceFeatureOperations.cs:2450/2482`); whether any database holds such a row was not checked. `PinnedTag` has one unguarded writer (`WorkspaceBranchOperations.cs:888`). 0B uses `IsNullOrWhiteSpace` everywhere. |
| F9 | Tag skips are silent almost everywhere. Existing skip shapes: `RepositoryOutcomes.Skipped` (Git Changes), `FeatureRepositoryOperationOutcome.Skipped`, "Skipped: ..." strings in `WorkspaceBranchBulkResult` errors, `OperationResult.Warnings`. | One new shape (section 4.4). In 0B it is filled and logged but not shown; showing it is Phase 1. |
| F10 | `RefreshWorkspaceProjectsAsync` leaves tag-pinned repositories out of the project scan (`Projects.cs:46`). | A reference repository must be scanned (the graph stays complete, and it can be pulled). `RefreshProjects` is its own policy operation: skipped on a tag, allowed for reference. |
| F11 | A producer that is not pushed in this run is never `ProducedByThisRun` (`WorkspacePushService.cs:699-703`). A matched package is polled in the registry; an unmatched one raises `SynchronizedPushNotPossibleException` and makes `CanRunPushLaneAsync` false. | This is already the behavior the note asks for reference producers. Only the diagnostics text is new ("NotPushed" -> read-only reason). |
| F12 | Every "detached" decision in Features keys off `WorkspaceFeatureRepository.PinnedTag != null` (about 12 places in `WorkspaceFeatureOperations`, plus `FeatureBranchPolicy`, `FeatureBranchGuard`, `UpdateFeaturePinnedTagAsync`). There is no detach flag and no source-branch field; `ParentBranchName` is nulled for tag rows. | Two new columns on `WorkspaceFeatureRepositories` and one `IsDetached` helper (section 8, WP6). |
| F13 | Nothing returns "dirty + strictly ahead of own upstream" for a Workspace checkout by repository name. `InspectWorktree` does, but takes explicit paths; `RepositoryStateProbe` falls back to the default branch when there is no upstream and has no dirty flag. | Activation check uses `InspectWorktree` with the main checkout path. Code reading on 2026-11-12: `InspectWorktreeAsync` (`GitWorktreeService.cs:605`) accepts any worktree path of the repository and reports `hasUpstream` false when the upstream is not configured or its ref is missing. A Worker test in 0A proves it on a main checkout; only if that fails is a new command added. **0A:** proven (`InspectWorktreeMainCheckoutTests`, 8 tests): pass the main checkout path as both `mainRepositoryPath` and `worktreePath`. No new command is needed. Limits the activation check must respect: a detached HEAD and a tag checkout look the same (`branch` null, `hasUpstream` null); nothing is fetched, so "not ahead" means not ahead of the local remote-tracking ref; `aheadOfDefault` is null unless `defaultBranch` is sent and `origin/<default>` exists. |
| F14 | Head commit for recording: `CheckoutBranchResponse` has no commit; `SyncRepositoryResponse.Commit` is set only on a tag; hook snapshots always carry `HeadCommit`. `SyncCommandHandler` never writes `graymoon.json`. `RecordCheckedOutTagPinsAsync` throws and fails the Sync. | Recording `branch` + `commit` needs the head commit in the pull and checkout responses and must not copy the throwing pattern. Sync and hooks do not record (D8). |
| F15 | `CheckoutBranchAsync` already creates a tracking branch for a remote-only branch and never forces or stashes. There is no dirty warning before it. | Only the warning and the conflict message are new. |
| F16 | Source links are created in four places with only `{WorkspaceId, RepositoryId, Role, SyncStatus}` (corrected in 0A): `WorkspaceRepository.cs:515` (`AddRepositoriesAsync`) and `:617` (`ReplaceRepositoriesCoreAsync`), `WorkspaceRepositoryOperations.cs:370` (Restore) and `:563`. `WorkspaceRepository.AddAsync` (`:60`) constructs no link. "Workspace merge" does not exist; `MergeRepositoriesAsync` merges the connector catalog. Existing links are untouched by a membership replace. | The default for new Sources is applied in these places only. The note's "merge" concern is void. |
| F17 | Edit Workspace is `WorkspaceModal.razor`, saved from `Workspaces.razor:704` (0A: not `:629`) straight through `WorkspaceRepository.UpdateAsync`; there is no facade. | Mode changes need a new facade (`IWorkspaceReferenceOperations`), not more page code. |
| F18 | The lock: `WorkspaceOperationRunner` is the one process-wide gate. Pages lock through `BackgroundJobService.StartJob` (context or structural by job key), REST through `RunExclusiveAsync` (structural). Push/update/prepare/pull services take no lock themselves. `TryStartStructural` is refused while any context or structural operation runs, under one `lock`. | A mode change runs as a structural operation. That guards every page job and every `WorkspaceOperationsEndpoints` operation, because they hold the runner. It does not guard the row actions behind `/api/branches/*` (F23), which take no lock: those check the policy at dispatch (4.5). The Git Changes page key (`/workspaces/{id}/changes`) is a mutation key and takes the lock; 0A confirms the per-file stage, unstage and Undo paths and the notification panel. **0A:** REST uses the legacy `TryStart` (`WorkspaceOperationsEndpoints.cs:487`), which shares the structural core with `TryStartStructural` (`WorkspaceOperationRunner.cs:55-62`); `WorkspaceCommandHttp` is file-scoped. The context lock is unreachable in production: no caller builds a context job key, so every page job is structural. **Outside the lock:** Git Changes per-file Stage and Unstage (`WorkspaceGitChanges.razor.cs:832`, `:845`) and Undo (`WorkspaceGitChanges.Discard.cs:129`, which also bypasses the facade); Delete Branch in `SwitchBranchModal.razor:1464`; every `/api/branches/*` route; `POST /api/sync` and the hook path; `POST /api/workspaces/{id}/files`; the Feature removal queue; membership saves; Actions bulk run and rerun. These must check the policy at dispatch. Not verified: notification panel job bodies and the Files, Dependencies, Projects and Packages pages. Full list: inventory Part B. |
| F19 | The note says every Source gets an explicit `reference` in a version-2 file, and also that a file without the new fields stays version 1. Writing `false` for every Source of every Workspace would make every file version 2 and unreadable by older builds. | **D3, decided:** write `workspace.reference` and every `repositories[].reference` (true and false) only when the default is true or at least one Source is reference; otherwise write none and stay at version 1. All-false and absent mean the same. |
| F20 | No end-to-end harness exists (no `WebApplicationFactory`, bUnit or Playwright). "Integration" today means App.Tests on SQLite with a fake `IWorkerBridge`, and Worker.Tests on real git (`GitScenarios`). Four test classes carry `Category=Long`. | The gates run those. App-plus-Worker behavior is covered by a manual smoke list (section 9), stated as manual in the log. **D4, decided:** accepted; no harness is added and the owner runs the list. |
| F21 | `_isReadOnlyContext` / `FeatureSelectorPresentation.IsReadOnlyContext` already mean "Feature needs repair". | Code uses `Reference` everywhere, never `ReadOnly`, as the note already says. |
| F22 | **Existing bug, confirmed by test in 0A and fixed on 2026-11-12, before 0B.** `WorkspaceBranchOperations.CheckoutAsync` read `wr.CheckedOutTag` after the state writer had cleared it on the same tracked link, so the tag pin in the Workspace definition was never cleared on a branch checkout. The Workspace-wide checkout had the same defect and bulk Create Branch never touched the pin. A Sync does not clear a pin (it only records tags), and a pin is applied when a repository is cloned, so a fresh clone went back to the tag. | Fixed: the three paths read the tag before the state write and clear the pin (`WorkspaceBranchOperations.CheckoutAsync`, `WorkspaceGitService.CreateBranchesWithOutcomeAsync`). Pin changes of one Workspace are serialized in `WorkspaceManifestService.SetRepositoryTagPinsAsync`, because the Workspace-wide checkout runs repositories in parallel and two changes would otherwise overwrite each other. Tests: `TagPinCharacterizationTests` (13), `WorkspaceManifestTagPinConcurrencyTests` (3). Not changed: a pin left by the old defect is not repaired, and leaving a tag outside the app (a terminal checkout) does not clear the pin. Reference recording must not depend on this path. |
| F23 | The per-repository branch actions behind `/api/branches/*` (`BranchEndpoints.cs`: checkout, create, set upstream, delete, update from default, return to default) call `IWorkspaceBranchOperations` without the runner lock. `DeleteBranchAsync` (`WorkspaceBranchOperations.cs:770`) deletes local and remote branches and had no policy operation in the first draft of this plan. | A policy check at dispatch in each of the six (4.5). Two delete operations in the policy. **D12, decided:** a safe local delete is allowed on a reference repository; a remote or forced delete is refused. |
| F24 | The recorded commit is applied only to a clone made by that Sync (`clonedNow && request.ApplyRepositoryTagPin`, `SyncRepositoryCommand.cs:178`). An existing clone is never moved. Sync-time recording (`applyRepositoryTagPin`, `WorkspaceGitService.Sync.cs:48` and `:190`) writes this machine's HEAD into the file. | For a tag that is harmless: the commit is the same for everyone. On a branch every user is on a different commit, so recording on Sync would change `graymoon.json` for every teammate after every Sync. **D8, decided:** record on activation and after a GrayMoon pull or switch only. |
| F25 | The note's sample gave the writable `Platform` entry `"branch": "develop"` with no commit; the note's rule and WP2 allow `branch` only together with `commit` on a reference entry. | **D9, decided:** no `branch` on a writable Source. The sample in the note is corrected. |
| F26 | `WorkerBridge` refuses normal commands when the Worker and App versions differ (`WorkerBridge.cs:67`, `WorkerVersionPolicy`). | A new request field such as `pullOnly` cannot be ignored by an older Worker and turn into a push. A flag is safe; no new command name is needed for that reason. |
| F27 | `src/GrayMoon.Agent` and `src/GrayMoon.Agent.Tests` hold only `obj` output and are not in `GrayMoon.slnx`. | Nothing to cover. |
| F28 | A comment in `CodeGraphCli.cs:14` says `.codegraph/.gitignore` is committed. Not checked: whether CodeGraph init creates that file as a new tracked change in a Source that does not have it. | 0A checks. If it does, the owner decides between skipping init for a reference Source and leaving the change for the user. **0A:** it does not. `codegraph init` runs only in the Feature root (`FeatureCodeGraphService.cs:54-59`, `CodeGraphCli.cs:147-148`) and writes only `<root>/.codegraph/` with a self-ignoring `.gitignore`; a nested Source repository is not touched, so a reference Source needs no special handling. (The Workspace repository's Feature worktree can show `?? .codegraph/.gitignore`; that repository is never read-only.) No decision is needed. |

Decisions not tied to one row (owner, 2026-11-12):

- **D5 - Git Changes Undo scope.** The note allows "explicitly selected Undo" and forbids bulk discard. Decided: file, folder and repository-row Undo are allowed on a reference repository; "Undo all changes" at a section that spans repositories skips reference repositories and says how many.
- **D6 - importing an existing folder as a new Source while the default is read-only** (`Workspaces.razor:1254`). The folder may hold unpushed commits. Decided: run the activation check; a blocked repository is added writable with a warning.
- **D7 - a Feature branch that existed before the Source became reference.** The note says it is left alone and never pushed. Decided: it follows the "reference on a branch" column (fetch and fast-forward pull allowed, no writes).
- **D8 - when the commit is recorded.** Decided: when read-only is turned on and after a pull or a manual switch through GrayMoon. Not on Sync and not from hooks (F24). A commit reached with git outside GrayMoon is recorded by the next GrayMoon pull or switch of that repository.
- **D9 - `branch` on a writable Source.** Decided: not written and not accepted (F25).
- **D10 - a branch with no upstream at activation.** Decided: it passes when its HEAD is contained in a remote branch (the case of a new Feature branch with no commits); otherwise it blocks. Without this, one untouched Feature would block read-only for the whole Workspace.
- **D11 - a Feature worktree that is missing or not `Ready` at activation.** Decided: a missing folder or a row that is not `Ready` gives a warning; a folder that exists but cannot be read blocks.
- **D12 - Delete Branch on a reference repository.** Decided: a local delete that git accepts without force is allowed (manual switching leaves local tracking branches behind); a remote delete and a forced delete are refused (F23).
- The note's own open items 1-3 (default-only wording, no Workspace repository, diagnostics depth) stay open; the plan uses the proposals written there.

## 3. Phase overview

| Phase | Result | Behavior change |
|---|---|---|
| 0A | Inventory, characterization tests, recorded baseline | none |
| 0B | One policy; every eligibility check goes through it | none |
| 0C | Full suites incl. `Long`, parity with 0A, written log | none |
| 0D | Service-side tag gaps closed, Feature-context update fixed | tags only, deliberate |
| 1 | Flags, migration, manifest v2, restore, enforcement everywhere, pull-only, recorded commit, Feature snapshots, Git Changes, Edit Workspace batch actions | reference mode usable Workspace-wide |
| 2 | Per-repository toggle, `ro:` filter, Workspaces list summary, drift UX, remaining polish | per-repository control |
| Gate 2 | Legacy suites again plus reference suites, manual smoke | - |

## 4. Target design

### 4.1 Policy core (pure, in `GrayMoon.Application`)

New folder `src/GrayMoon.Application/Repositories/Policy/`. No database, no DI, usable from Razor, services and tests.

```csharp
public enum RepositoryOperation
{
    // read / refresh
    Sync, Fetch, RefreshProjects, RestorePackages,
    // incoming
    Pull,
    // checkout
    ManualCheckout, WorkspaceCheckout, ReturnToDefault, ReturnToDefaultCleanup,
    // development writes
    CreateBranch, DeleteLocalBranch, DeleteBranchRemoteOrForced,
    UpdateBranchFromDefault, UpdateDependencies, UpdateVersionFiles,
    Commit, Push, SetUpstream, UndoPush,
    CreatePullRequest, MergePullRequest, RunWorkflow,
    // local change management
    Stage, Unstage, Discard,
    // features
    CreateFeatureBranch,
}

public enum RepositoryOperationScope { Individual, Bulk }

public enum RepositoryOperationOutcome { Allowed, Refused, Skipped }

public enum RepositoryOperationReason { None, OnTag, Reference, ReferenceSnapshot, WorkspaceRepository, NotOnBranch }

public sealed record RepositoryPolicySubject(
    WorkspaceRepositoryRole Role,
    bool IsReference,            // always false until Phase 1
    string? BranchName,
    string? CheckedOutTag,
    bool IsFeatureContext,
    bool IsReferenceSnapshot);   // always false until Phase 1

public sealed record RepositoryOperationDecision(
    RepositoryOperationOutcome Outcome,
    RepositoryOperationReason Reason,
    string? Message);

public static class RepositoryOperationPolicy
{
    public static RepositoryOperationDecision Evaluate(
        RepositoryOperation operation, RepositoryOperationScope scope, RepositoryPolicySubject subject);
}
```

Rules of `Evaluate`:

- `Individual` scope never returns `Skipped`; `Bulk` scope never returns `Refused`. A caller that gets `Skipped` must report it and must not count it as done.
- The rule table is data (one row per operation: allowed on tag, allowed for reference on a branch, allowed in a reference snapshot), not nested `if`s, so the matrix test can enumerate it.
- `WorkspaceRepositoryRole.Workspace` forces `IsReference` to false inside `Evaluate`. The Workspace default is not an input at all.
- Messages live here once. `TagBlockedActionMessage` (`WorkspaceRepositories.State.cs:118`, duplicated at `Dependencies.cs:209`) and the push refusal text (`WorkspacePushService.cs:1009`) move here unchanged in 0B.
- `WorkspaceRepositoryRole` moves to (or is mirrored in) `GrayMoon.Application` if it is not reachable from there; check in 0A.

Rule table after Phase 1 (0B implements the "On a tag" column only, taken from the 0A characterization, not from this table):

| Operation | On a tag (today) | Reference, on a branch | Reference snapshot (Feature, detached) |
|---|---|---|---|
| Sync, Fetch, RestorePackages | as today | allowed | allowed |
| RefreshProjects | skipped | allowed | allowed |
| Pull | skipped | allowed, fast-forward only | skipped |
| ManualCheckout | allowed | allowed, stays reference | refused |
| WorkspaceCheckout, ReturnToDefault (bulk) | skipped | skipped | skipped |
| ReturnToDefault (row) | n/a | allowed as a plain switch, no cleanup | refused |
| CreateBranch, CreateFeatureBranch, UpdateBranchFromDefault | as today | skipped / refused | skipped / refused |
| UpdateDependencies, UpdateVersionFiles, Commit | skipped | skipped / refused | skipped / refused |
| Push, SetUpstream, UndoPush | skipped / refused | skipped / refused | skipped / refused |
| DeleteLocalBranch (git accepts it without force) | as today | allowed | refused |
| DeleteBranchRemoteOrForced | as today | refused | refused |
| CreatePullRequest, MergePullRequest, RunWorkflow | as today | skipped / refused | skipped / refused |
| Stage, Unstage, Discard (Individual) | as today | allowed | allowed |
| Stage, Unstage, Discard (Bulk across repositories) | as today | skipped | skipped |

A reference repository on a tag gets the stricter of the two columns, except the Individual local-change row, which stays allowed.

### 4.2 Subject loading (App)

`IWorkspaceRepositoryOperationPolicy` in `GrayMoon.Application`, implemented by `WorkspaceRepositoryOperationPolicyService` in `src/GrayMoon.App/Services/Policy/`. Uses `IDbContextFactory<AppDbContext>` only (never the circuit's context).

```csharp
Task<IReadOnlyDictionary<int, RepositoryPolicySubject>> GetSubjectsAsync(
    int workspaceId, WorkspaceFeatureContextId contextId, CancellationToken ct);

Task<RepositoryEligibility> PartitionAsync(
    int workspaceId, WorkspaceFeatureContextId contextId,
    RepositoryOperation operation, IReadOnlyCollection<int>? repositoryIds, CancellationToken ct);
// RepositoryEligibility(IReadOnlyList<int> Eligible, IReadOnlyList<RepositorySkip> Skipped)

Task<RepositoryOperationDecision> EvaluateAsync(
    int workspaceId, WorkspaceFeatureContextId contextId,
    RepositoryOperation operation, int repositoryId, CancellationToken ct);
```

- The loader is the single replacement for the five copies in F7: special Workspace reads the link, a Feature reads `WorkspaceRepositoryContextState` joined to the link and `WorkspaceFeatureRepositories`.
- 0B only: an internal `SubjectSource` option (`Context`, `LegacySharedLink`, and the "missing Feature state row" behavior) so each migrated call site keeps exactly what it does today. Each legacy use carries a comment naming F6/F7. 0D removes the option if approved.
- `IsReference` comes from the link in every context; `IsReferenceSnapshot` from the Feature row. Nothing in a Feature writes the link (Feature-context scoping rule).

### 4.3 SQL parity

`RepositoryOperationPolicyExpressions` (App, next to the query service) exposes translatable predicates built from the same rule table:

- `Expression<Func<WorkspaceRepositoryLink, bool>> IsEligible(RepositoryOperation)` for the special Workspace,
- the same over the Feature join shape used at `WorkspaceRepositoryLinkListQueryService.cs:170-273`.

The four header projections (A-D) and `WorkspaceRepository.cs:290/419` use these instead of hand-written tag tests. A parity test seeds every subject combination, runs the SQL predicate and `RepositoryOperationPolicy.Evaluate`, and requires the same answer for every operation that has a SQL form.

### 4.4 Skip reporting

`RepositorySkip(int RepositoryId, string RepositoryName, RepositoryOperationReason Reason, string Message)` in `GrayMoon.Application`. Added as init-only `Skipped` on `OperationResult` (next to `Warnings`), and on `PrepareWorkspaceResult`, `UpdateAndPushResult`, `DependencyUpdateRunResult` and the push result. `RepositoryOutcomes.Skipped` (Git Changes) and the commit planner's `LeftOut` get the reason.

- 0B: filled and written to the log only. No new toast, no new REST field.
- Phase 1: shown by `ShowRepoErrors` as information (not error, not warning), added to the REST bodies, and counted in the overlay summary ("3 read-only repositories skipped").

### 4.5 Mode guard

A mode change (one Source, batch, or default-only) runs inside `IWorkspaceOperationLock.TryStartStructural(workspaceId, "reference-mode", ...)`.

- Refused start -> the facade returns "An operation is running for this Workspace" and changes nothing. This covers page jobs, `WorkspaceOperationsEndpoints` and the notification panel because they hold the same runner (F18).
- The UI disables the switch from `IWorkspaceOperationRunner.IsBusy(workspaceId)` and its `Changed` event; that is convenience only.
- Orchestrations capture their eligible set once at the start (`PartitionAsync`) and pass ids down; they do not re-read the flag mid-run. Because the toggle cannot start while they run, the captured set stays true.
- Known write entry points without the runner lock: the six actions of `IWorkspaceBranchOperations` behind `/api/branches/*` (checkout, create, set upstream, delete, update from default, return to default; F23). Each calls `EvaluateAsync` as its first step and refuses with the policy message. A toggle can still land between that check and the Worker call. The window is one request, and turning read-only on has just proven that nothing is unpublished, so this is accepted and documented, not locked.
- 0A lists any further entry point outside the lock (candidates: per-file Git Changes actions, `FeatureRemovalQueue`). Each gets the same dispatch-time check.

## 5. Phase 0A - baseline characterization

No production code changes except test seams that cannot change behavior.

1. **Inventory.** Turn Appendix A into `docs/todo/read-only-repositories-inventory.md`: one row per `IsOnTag` / `CheckedOutTag` / `IsPinnedToTag` / `PinnedTag` / `tagFilteredRepoIds` / `SkipReposOnTags` use in `src/GrayMoon.App`, `GrayMoon.Application`, `GrayMoon.Worker` (329 hits in 92 files, tests included). Columns: file:line, method, class (`eligibility-filter`, `eligibility-refuse`, `eligibility-ui`, `presentation`, `state-write`, `pin-bookkeeping`, `worker-git-state`), subject source (link / context / Feature row), policy operation, has service-side twin (y/n). Add one row per route in `WorkspaceOperationsEndpoints`, `BranchEndpoints`, `SyncEndpoints`, `WorkspaceEndpoints` and `WorkerEndpoints`, and one row per Worker command that writes (`src/GrayMoon.Worker/Commands`), each mapped to a policy operation or marked read-only. Not mapped yet: `InitCodeGraph`, `UninitCodeGraph`, `UpdateFileVersions`, `ResolveGeneratedPackageReferences`, `DotnetRestore`.
2. **Verify the survey.** Re-read every line cited in section 2 and in Appendix A. Correct this plan where it is wrong before 0B starts.
3. **Answer the open facts:** F8 (whitespace tags), F13 (`InspectWorktree` on a main checkout, with a Worker test), F18 (which entry points hold the lock), F22 (pin clear on branch checkout), F28 (does CodeGraph init leave a tracked change in a Source), whether `WorkspaceRepositoryRole` is visible to `GrayMoon.Application`.
4. **Characterization tests** (new, in `src/GrayMoon.App.Tests/Policy/Characterization/`; each asserts what the code does today, including the gaps):
   - Service matrix: for tag vs branch, special Workspace vs Feature, Source vs Workspace role - push plan and single push, update plan / sync dependencies / single update / version-file commit, commit dependency updates, undo push, return to default (analyze, execute, unattended, batch), restore packages, common branches, default-branch review.
   - Gap tests named `..._has_no_service_side_tag_check_today`: pull, bulk create branch, Prepare Workspace via the facade with null ids, Workspace checkout, Git Changes commit and stage, Update Branch from Default.
   - F6 tests: update in a Feature whose tag state differs from the Workspace.
   - F7 tests: Feature repository with no state row, per call site.
   - Counters: all seven header counters in all four projections, plus `WorkspacePendingActionsService.ComputeNotification` and all five `NotificationPanelRepoSets`, for tag / branch / no-upstream / Workspace-role rows. Record the known differences (Push vs `isPushRecommended`, `NeedingUpdate` vs `UpdateOnDefaultBranch`, SQL `CreatablePr` vs `PRBadge.ShowsCreateBadge`); they are not to be "fixed" in 0B.
   - Page filters: extract nothing yet; cover the page-side candidate filters through `ModalAndSharedUiGuardTests`-style tests where a static helper exists, and list the rest in the inventory as "covered by 0B extraction".
   - REST: there is no endpoint test host and the endpoints are thin. Test the facades they call (`IWorkspace*Operations`) with a tag-pinned repository in the Workspace for push, update, prepare, pull, undo-push and return-to-default, the six write actions of `IWorkspaceBranchOperations` (F23), and `WorkspaceCommandHttp.RunExclusiveAsync` for the refused start.
   - Worker: nothing new for tags (existing `SyncRepositoryTagPinTests`, `ReturnToDefaultBranchCommandTests` stand).
5. **Run and record the baseline** (from the repository root, Release not required):
   ```powershell
   dotnet build GrayMoon.slnx
   dotnet test src/GrayMoon.Common.Tests/GrayMoon.Common.Tests.csproj
   dotnet test src/GrayMoon.Worker.Tests/GrayMoon.Worker.Tests.csproj
   dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj
   dotnet test src/GrayMoon.Worker.Tests/GrayMoon.Worker.Tests.csproj --filter "Category=Long"
   dotnet test src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj --filter "Category=Long"
   ```
   The default runs include `Long` unless filtered; the explicit `Long` runs are recorded separately so a skipped one is visible. Log: commit, OS, git version, .NET SDK, totals, every failure with its name. Pre-existing failures are listed as such and not fixed here.

Exit: inventory complete, section 2 corrected, baseline logged. **Stop for review.**

## 6. Phase 0B - central policy, existing semantics only

`IsReference` and `IsReferenceSnapshot` exist as subject fields and are always false. No flag, column, migration, manifest field or UI control.

Order (each step compiles and passes the 0A tests before the next):

1. **Policy core** (section 4.1) with the tag column from 0A. Tests: `RepositoryOperationPolicyMatrixTests` enumerating operation x scope x {branch, tag} x role x context; a test that fails when a `RepositoryOperation` has no rule row.
2. **Subject loader** (4.2). Replace the five per-context copies (F7) one at a time; each keeps its `SubjectSource`.
3. **SQL expressions** (4.3). Replace the tag tests in `WorkspaceRepositoryLinkListQueryService.cs:76-273` (four projections) and `WorkspaceRepository.cs:290-353, 419-480`. Parity test added. Do not merge the four projections or change any counter's other terms.
4. **Services and orchestrators** (replace the tag expression with a policy call; filter and skip sets stay identical):
   - `DependencyUpdateOrchestrator.cs:370, 420-446, 556-563`
   - `WorkspaceGitService.Projects.cs:46, 140, 191, 213, 252`; `.Commit.cs:32, 132`; `.Restore.cs:148`
   - `WorkspacePushService.cs:167, 863/901/942, 1006, 1090, 1193, 1522, 1571, 1688`; `WorkspaceProjectRepository.Push.cs:267, 292`
   - `WorkspaceUndoPushHandler.cs:26`; `WorkspaceSyncHandler.cs:273, 383, 458-532, 546`; `WorkspaceSyncHandler.ReturnBatch.cs:183`
   - `WorkspaceBranchOperations.cs:524`; `WorkspaceUpdateAndPushOperations.cs:83`
   - Skips are collected into `RepositorySkip` and logged (4.4).
5. **Notifications**: `WorkspacePendingActionsService.cs:89-138`, `UiRaceGuards.cs:121-157` (`NotificationPanelRepoSets`). The private `IsOnTag` helpers go.
6. **Pages and components** (`eligibility-ui` rows only): `WorkspaceRepositoriesRow.razor:32, 460, 469, 658-675`; `WorkspaceRepositories.State.cs:135, 191, 386-395`; the bulk filters in `.Branches.cs`, `.Update.cs`, `.Push.cs`, `.UndoPush.cs`, `.CommitSync.cs`, `.ReturnToDefault.cs`, `.BulkMerge.cs`, `.PullRequests.cs`, `.PrepareWorkspace.cs`, `.Dependencies.cs`, `.FileVersions.cs`, `.Sync.cs`; `WorkspaceContextBar.razor:88`; `CommitsBadge`, `PRBadge.ShowsCreateBadge`. Pages call the static `RepositoryOperationPolicy.Evaluate` on the row model (add `ToPolicySubject()` on the link / DTO). `skipReposOnTags` stays a user option: when off, the page passes all ids as today.
7. **Features**: introduce `WorkspaceFeatureRepository.IsDetached` (`=> PinnedTag != null`) and use it for every detach / branch-name decision in `WorkspaceFeatureOperations` (`:283, 342, 386, 516, 551, 584, 934, 1977, 2019, 2252, 2442-2482`) and in `FeatureBranchPolicy.ExpectedBranch` callers. Pure rename-level change; it gives Phase 1 one place to extend.
8. **Left alone on purpose** (`presentation`, `state-write`, `pin-bookkeeping`, `worker-git-state`): tag icon and blank badges, `WorkspaceRepositoryStateWriter`, `RecordCheckedOutTagPinsAsync`, `Set/ClearRepositoryTagPinAsync`, `UpdateFeaturePinnedTagAsync`, everything in `GrayMoon.Worker`, `WorkspaceRepositoryLink.IsOnTag` itself (still the state property).
9. **Convention test**: new `RepositoryPolicyConventionTests` (same technique as `SharedLinkWriteConventionTests` in `Conventions/ServiceConventionTests.cs`): outside an allow-list (state writer, mapper, model, policy, Worker, the `presentation` files from the inventory), no code reads `IsOnTag` / `CheckedOutTag` for a decision. A stale allow-list entry fails the test. This keeps the policy from eroding.

Not in 0B: closing F5 gaps, fixing F6/F22, unifying counter definitions, showing skips, anything named reference in UI or data.

Exit: 0A tests unchanged and green, new policy tests green. **Stop for review, then 0C.**

### 0B as built (2026-11-12)

Committed as `1c67c21a`. Where the result differs from the steps above:

- **Subject carries a bool, not the role enum.** `RepositoryPolicySubject.IsWorkspaceRepository` replaces `WorkspaceRepositoryRole Role` (the enum is not visible to `GrayMoon.Application`). The enum did not move.
- **Subject source has three values:** `Context`, `ContextStateFirst` (the push plan: a state row wins in the Workspace too) and `LegacySharedLink` (the update path, F6). The "missing Feature state row" case needs no value of its own: a Feature repository without a state row is on no branch and no tag under `Context` and `ContextStateFirst`, as before.
- **Allowed on a tag, as today:** Sync, Fetch, manual and Workspace checkout, Create Branch, both branch deletions, Set Upstream, Run Workflow, Stage, Unstage, Discard. "Skip repos on tags" in the Workspace-wide dialogs stays a user option and reads `IsOnTag`; it is not a rule.
- **Where the policy is asked:** services that load their own rows use `IWorkspaceRepositoryOperationPolicy` (`WorkspaceRepositoryOperationPolicyService`); code that already holds rows uses the `Allows` extensions; the four header projections use `RepositoryOperationPolicyExpressions`, expanded into column comparisons before the query runs.
- **Messages:** the two existing texts are constants on `RepositoryOperationPolicy`. A page still shows the general text for every operation, push included, as before.
- **Skips (4.4):** `Skipped` is on `OperationResult` and `DependencyUpdateRunResult`, and computed on `UpdateAndPushResult` and `PrepareWorkspaceResult`. It is filled by a Workspace push (`WorkspacePushOperations.PushAsync`) and by a dependency update run, written to the log, and marked `[JsonIgnore]`. The pipelined push lane and Return to Default do not fill it yet.
- **Entry points outside the Workspace lock** were not touched in 0B; they have no tag rule to move. The decision "check the policy at dispatch" applies from Phase 1.

Left for 0C or later, found while moving the checks:

- **Sync from a row or a level leaves a tag checkout alone; Workspace Sync includes it.** One `Sync` operation cannot express both, so the row badge, the level Sync filter and the level action buttons still read the tag. They are marked OPEN in `RepositoryPolicyConventionTests`.
- **The commits badge of a row** is disabled by the tag itself, because it stands for pull and push together.
- **The header counters compare the column with null and the empty string; the policy also treats blank text as no tag.** Nothing stores blank text (F8). `Blank_text_is_a_tag_to_the_query_form_only` records the difference.
- **The convention test scans compiled calls.** A tag read written inside a database query expression is not seen by it.
- **Two existing tests failed when run together** (`AiAndGitChangesSweepTests.Cancel_racing_the_end_of_a_run_never_throws`, `BackgroundJobRefusedStartTests.Workspace_job_refused_by_the_lock_reports_not_started`). Not caused by 0B. Both are fixed; see `read-only-repositories-0b-follow-ups.md` and the test log.

## 7. Phase 0C - gate, then 0D

**0C.** Run the full command list of 0A step 5 again, including both explicit `Long` runs. Compare totals and names with the baseline; every difference is explained in the log. Then the manual smoke list M1-M8 in section 9 against a running App and Worker with one tag-pinned repository. Result in the log: passed / not passed, with what could not run. **Stop. Phase 1 starts only on the owner's written go-ahead.**

**0D (D1, D2).** Tags only, still no reference code:

- Service-side policy checks for pull, bulk Create Branch, Prepare Workspace, Workspace checkout, Update Branch from Default, Git Changes commit, REST endpoints (F5), and the write actions of `IWorkspaceBranchOperations` where a tag rule applies today only in the page (F23). The gap tests from 0A flip to `..._is_skipped_service_side`.
- `LegacySharedLink` removed from the update path (F6); `SubjectSource` option deleted if no call site still needs it.
- Run the 0C command list again and log it.

**Stop for review.**

### 0C and 0D as built (2026-11-12)

Not committed. The owner asked on 2026-11-12 to continue through the next phases without a stop here.

**0C.** The automated part passed (test log, "0B follow-ups" and "Phase 0C and 0D"). The manual smoke list M1-M8 was **not run**: it needs a running App and Worker and a person. It is still owed before a release.

**0D.** What the service now does for a repository on a tag:

| Entry point | Before | Now |
|---|---|---|
| Pull of one repository (`WorkspaceCommitSyncHandler.CommitSyncAsync`) | sent to the Worker | refused with the tag message |
| Pull of a level (`CommitSyncLevelAsync`) | sent to the Worker | left out, named in `OperationResult.Skipped` |
| Update Branch from Default (`WorkspaceBranchOperations`) | sent to the Worker | refused, HTTP 400 with the tag message |
| Return to Default of one repository (`WorkspaceBranchOperations`) | sent to the Worker | refused, HTTP 400 with the tag message |
| Dependency update, version files, project refresh and their commits in a Feature | judged by the Workspace checkout | judged by the Feature's own checkout (F6). `LegacySharedLink` is deleted. |

Not changed, against the first draft of this section:

- **Bulk Create Branch, Prepare Workspace with no ids, Workspace checkout, single checkout, Create Branch, Set Upstream, Delete Branch.** They are allowed on a tag in the rule table: a checkout or a new branch is how a repository leaves a tag, and "Skip repos on tags" is an option of the dialog, not a rule. A REST caller that passes no ids gets what the dialog does with the option off.
- **Git Changes commit, stage and unstage.** The Git Changes page has never checked the tag, so a commit on a tag checkout is possible there today. Refusing it would take something away from users, which 0D must not do. Consequence for Phase 1 (WP7): the reference rule for a Git Changes commit needs its own operation (`CommitGitChanges`), because `Commit` (the dependency and version-file commit) is not allowed on a tag.
- **Actions bulk rerun.** Run Workflow is allowed on a tag.

## 8. Phase 1 - persistence, enforcement, Workspace-wide control

**Work order: WP1, then WP4-WP7 (enforcement), then WP2-WP3 (file and restore), then WP8-WP9.** The packages keep their numbers, but enforcement is built before anything can set the flag: Restore of a version-2 file (WP3) sets `IsReference`, so it must not exist before the services refuse writes. Enforcement tests seed the flag in the database.

Check-in points: after WP1 with WP4-WP7, after WP2-WP3, after WP8-WP9. Each group is reviewable on its own. Phase 1 ships as one unit.

### WP1 - Database

- `Workspace.ReferenceDefault` (bool), `WorkspaceRepositoryLink.IsReference` (bool), `WorkspaceFeatureRepository.IsReferenceSnapshot` (bool), `WorkspaceFeatureRepository.SourceBranchName` (string?, 200).
- `AppDbContext` / `AppDbContext.Features.cs`: `HasDefaultValue(false)` for the three booleans.
- `Migrations.cs`: one new idempotent `MigrateReferenceRepositoriesAsync` using `AddBooleanColumnIfMissingAsync` (`Migrations.Forks.cs:79`) and `AddNullableTextColumnIfMissingAsync`; appended to `StrictSteps` as the next free number (19 at `1515d32b`; re-check). No try/catch.
- `WorkspaceRepositoryLink.WithBranchOverride` copies `IsReference` (`:149`). `WorkspaceRepositoryLinkListItemDto`, the mapper, both row projections in the query service (`:654`, `:724`) carry `IsReference`; the Feature projection also `IsReferenceSnapshot` and `SourceBranchName`.
- A guard in one place (`WorkspaceRepository` save path or the reference facade): a `Role == Workspace` link can never have `IsReference = true`.
- Tests: migration on an old database and on a fresh one (pattern of `WorkspaceManifestDriftMigrationTests`); `WithBranchOverride` copies the flag; role guard.

### WP2 - `graymoon.json` version 2

- `WorkspaceManifest.cs`: `WorkspaceManifestWorkspace(Profile, bool? Reference = null)`; `WorkspaceManifestRepository(..., bool? Reference = null, string? Branch = null)` with `WhenWritingNull`. `LenientBooleanJsonConverter` gets a property name for its message (today it always says "codegraph must be true or false", `:68`): either a small generic base with one subclass per property, or a converter factory; the existing codegraph message must not change.
- `WorkspaceManifestFile.cs`: `SchemaVersion` -> `MaxSchemaVersion = 2`; `Serialize` writes `version` 2 when any `reference` or `branch` is present, else 1 (D3). `TryParse` accepts 1 and 2 and rejects `reference` / `branch` in a version-1 file with a clear message, or accepts them leniently - pick strict, with a test.
- Pin rules move from `WorkspaceDefinitionTagPin.TryNormalize` to a wider `WorkspaceDefinitionRevisionPin.TryNormalize(tag, branch, commit, isReference, name, ...)`: valid forms are none, `tag`+`commit`, or `branch`+`commit` with `reference: true`. `branch` and `tag` together, `branch` without `commit`, or `branch`+`commit` on a non-reference entry are errors that name the repository and the property. `WorkspaceDefinitionTagPin` stays as the tag-only helper used by the tag paths.
- `IWorkspaceManifestFile.ReadTagPinCommit` becomes `ReadPinnedRevision(content, repositoryUrl)` returning `(Commit, Branch, Error)`; the Worker is the only caller.
- Tests in `WorkspaceManifestFileTests` / new `WorkspaceDefinitionRevisionPinTests`: round trip of the sample in the note; default true with one Source false; version stays 1 with no reference data; `"reference": "yes"` and `1` rejected with the property name; a version-2 file read by a parser pinned to 1 gives the "schema 2 is newer" message (keep a copy of the old constant in the test).

### WP3 - Manifest service, drift, restore, new Sources

- `WorkspaceManifestService.BuildFromDatabaseAsync` (`:35`): sets `Workspace.Reference` and each `Repositories[].Reference` per D3.
- `WorkspaceRepositoryTagPins.CopyOnto` (`:12`): also carries `Branch`+`Commit` from the file for entries the database marks reference; drops `Branch` (and a `Commit` that has no `Tag`) for entries that are not. Rename the class to `WorkspaceRepositoryRevisionPins` in the same change.
- New `SetRepositoryReferenceRevisionsAsync(workspaceId, changes)` beside `SetRepositoryTagPinsAsync` (`:338`), same read-apply-write-if-changed shape, returns a result and never throws.
- `Compare` (`:523`) and `WorkspaceManifestDrift`: add `ChangedReference` (repository names whose flag differs) and `ReferenceDefaultChanged`. `branch`/`commit` are ignored, like tag pins. The drift banner text names them.
- `RestoreDefinitionEvaluator` / `RestoreDefinitionPlan`: add `ReferenceDefault` and `ReferenceBySourceRepositoryId`. Rule for a Source with no `reference`: the file's `workspace.reference` when present, else false.
- `WorkspaceRepositoryOperations.RestoreIntoWorkspaceAsync` (`:333`): set `ReferenceDefault` with the profile, and `IsReference` on each link at `:370`, before the caller's first Sync. `IsDefinitionRewriteRequiredAsync` (`:418`) keeps working because both sides go through `CopyOnto`.
- New Sources take `Workspace.ReferenceDefault` where links are constructed (corrected in 0A, F16): `WorkspaceRepository.cs:515` (`AddRepositoriesAsync`, with D6) and `:617` (`ReplaceRepositoriesCoreAsync`), `WorkspaceRepositoryOperations.cs:370` and `:563`. Existing links are never touched by these paths.
- Tests (`WorkspaceManifestServiceTests`, new `ReferenceRestoreTests`): the note's mixed sample survives build -> serialize -> restore; default-only change alters no link; replace membership keeps flags and applies the default to new links only; drift detects a flag difference and ignores `branch`/`commit`; a tag pin survives turning reference off; legacy file with `workspace.reference` only.

**Check-in.**

### WP4 - Policy extension and service enforcement

- Fill the two reference columns of the rule table (4.1); matrix test grows; SQL parity test grows. The loader now reads the real flags.
- Because every eligibility site already goes through the policy, most enforcement is the table. What still needs code:
  - Service-side checks (0D added the tag side; the reference side is the same policy call): `WorkspaceCommitSyncHandler` (WP5), `WorkspaceBranchHandler.CreateBranchesWithOutcomeAsync` and `CheckoutBranchForWorkspaceAsync`, `PrepareWorkspaceOrchestrator` (branch phase and the id set handed to update and push), `WorkspaceBranchUpdateHandler`, `WorkspaceGitChangesOperations` (WP7), PR create and bulk merge with their return follow-ups, `WorkspaceFileVersionService.UpdateAllVersionsAsync`, the Actions page bulk run/rerun (`WorkspaceActions.BulkActions.cs:77/155/234`) and its row list (`WorkspaceActions.Loading.cs:42`).
  - Single-repository refusals: `PushSingleRepositoryWithUpstreamAsync` (`WorkspacePushService.cs:1006`), `RunUpdateSingleRepositoryAsync`, `CreateBranchAsync` (`WorkspaceBranchOperations.cs:691`), set-upstream, update from default, and `DeleteBranchAsync` (`:770`) per D12: `isRemote` or `force` maps to `DeleteBranchRemoteOrForced`, anything else to `DeleteLocalBranch`.
  - `RefreshProjects` stays allowed (F10): check `RefreshWorkspaceProjectsAsync`, `RefreshSingleRepositoryProjectsAsync` include reference repositories while `GetUpdatePlanAsync` / `SyncDependenciesAsync` exclude them as consumers.
  - Return to Default: bulk skips; the row action on a reference repository is a plain checkout of the default branch - no branch deletion, no discard offer (`ClassifyRepository` gets an `IsReference` outcome next to `IsOnTag`; `ReturnToDefaultRepositoryPlan` in `IWorkspaceSyncOperations.cs:51` carries it).
  - `GetCommonBranchesAsync` excludes reference repositories.
- Skips become visible (4.4): `ShowRepoErrors`, overlay summary, REST bodies in `WorkspaceOperationsEndpoints`.
- Dependency graph: no pruning anywhere. `PackageWaitDiagnostics` (`WorkspacePushService.PackageWait.cs:380`) reports a read-only producer as such; the unmatched-package failure message names the producer and says it is read-only (note open item 3).
- Tests: one service test per row of the note's "Refused or skipped" table, each seeding an outgoing commit / unmatched dependency on the reference repository so a wrong implementation would act on it; REST returns the skip with reason; a writable consumer of a read-only producer whose package is missing fails with the named package; the Workspace-role repository is still branched, committed and pushed when every Source is reference.

### WP5 - Worker: pull-only, activation check, checkout

- **Pull-only.** `CommitSyncRepositoryRequest` gets `pullOnly` (bool). In that mode `GitRemoteIntegrateService` runs `git pull --ff-only origin -- <branch>` semantics (through `ArgumentList`), never merges, and the command never pushes. Outgoing > 0 against the branch's own upstream -> refuse before pulling ("has local commits"). A fast-forward blocked by local changes returns a distinct `LocalChangesBlockPull` result; the App turns it into the note's message. `WorkspaceCommitSyncHandler` sets `pullOnly` from the policy subject, per repository, so one bulk pull mixes both modes. The response carries the full head sha. A flag is safe here because an older Worker is refused before the command is sent (F26).
- **Activation check.** `InspectWorktree` on the main checkout (F13) and on every `Ready` Feature worktree of the Source. A Feature row that is not `Ready`, or whose folder is missing, gives a warning and does not block; a folder that exists but cannot be read blocks (D11). If 0A showed it does not work for a main checkout, add `InspectReferenceEligibility` by the `add-worker-command` recipe (read pool). The App mirror `InspectWorktreeWorkerResponse` gains the fields it drops today. Rule per checkout:
  - `error` set, or `isDirty` null -> block ("state could not be read"),
  - `branch` null (tag or detached) -> pass,
  - `hasUpstream` null -> block ("state could not be read"),
  - `hasUpstream` false -> pass when HEAD is contained in a remote branch (new response field `headOnRemote`, from `git branch -r --contains HEAD`), else block ("branch has no upstream") (D10),
  - `aheadOfUpstream` > 0 or null -> block ("N unpushed commits"),
  - `isDirty` true -> warning only.
  Ahead of default, behind upstream and PR state are not read.
- **Checkout.** `CheckoutBranchResponse` gains `commit` (full sha). No force, no stash (already so). A checkout that git refuses because of local changes returns a distinct result; bulk callers continue with the other repositories.
- **Sync is unchanged.** `SyncRepositoryResponse.Commit` stays tag-only (`SyncRepositoryCommand.cs:242-257`); Sync does not record a reference revision (D8).
- **Restore at the recorded revision.** `CheckoutPinnedCommitAsync` (`SyncRepositoryCommand.cs:432`) uses `ReadPinnedRevision`: `tag`+`commit` as today; `branch`+`commit` -> verify the commit exists, `git checkout -B <branch> <sha>`, then set upstream to `origin/<branch>` when that ref exists. A missing commit removes the clone and fails that repository's sync, like an invalid tag pin.
- Worker tests (from `GitScenarios`; add a scenario for "clone behind its remote" and "clone with a local commit"): fast-forward pull moves HEAD; diverged history is refused and HEAD is unchanged; dirty-but-compatible pull succeeds; dirty-and-conflicting pull is refused with files intact; pull-only never calls push (assert on the remote); activation check for clean / dirty / unpushed / no upstream with HEAD on a remote branch / no upstream with a local-only commit / detached / unreadable / missing Feature folder; restore to `branch`+`commit` leaves a tracking branch behind origin with incoming commits; missing commit removes the clone.

### WP6 - Features

- Create (`WorkspaceFeatureOperations.cs:262-283`): for a Source with `IsReference`, the row gets `IsReferenceSnapshot = true`, `SourceBranchName = snapshot.Branches[repo]` (null on a tag; `PinnedTag` as today), `ParentBranchName = null`, `BaseCommitSha` from the same `GetHeadCommits` snapshot. `IsDetached` becomes `PinnedTag != null || IsReferenceSnapshot`, which makes Create, Repair and Roll back send `detach = true` and skip `PersistLocalBranchAsync` and branch deletion with no further edits (0B step 7).
- Seed (`SeedInitialFeatureProjectionsAsync`, `:2404`): a branch-sourced snapshot has no branch and no tag in its context state. Check every reader of "has identity" (`WorkspaceRepositoryStateWriter.cs:343`, `IsVersionUnresolved`, GitVersion pending) and the hook path for a detached non-tag HEAD so the row neither looks unsynced nor gets a synthetic branch name persisted (extension guide rule on detached HEAD descriptions).
- Remove analysis (`HasNothingUnpushed`, `:1628`): a reference snapshot is safe when its HEAD equals `BaseCommitSha` and it is clean; ahead-of-default is not used for it (today a detached row ahead of default is never automatically safe).
- `FeatureBranchPolicy.Evaluate` / `FeatureBranchGuard`: every checkout in a reference snapshot is refused with a new message; `UpdateFeaturePinnedTagAsync` does not touch such rows.
- `WorkspaceBranchOccupancyService` (`:111-144`): no Feature badge for a row that has no Feature branch.
- A Source that becomes reference later: existing Feature rows stay branch rows (`IsReferenceSnapshot = false`); the policy stops writes through the link flag (D7). Repair never converts them.
- Row display in a Feature: `<name> - <SourceBranchName or tag> (source) - ro - <short sha>`.
- Workspace-role repository: always a normal Feature branch.
- Tests (`CreateFeatureParentBranchTests` style, `FeatureBranchGuardTests`): mixed Feature creates detached and branch rows from one snapshot; pulling the Source in the Workspace does not move the snapshot; all-reference Workspace still branches the Workspace repository; Remove of a clean snapshot is automatically safe and deletes no branch; Repair recreates a snapshot at `BaseCommitSha`; no Feature write touches the link.

### WP7 - Git Changes

- `WorkspaceGitChangesRepositoryView` gains `IsReference` (and role); `GitChangesTree.razor:62` shows the `ro` badge.
- `WorkspaceGitChangesOperations`: `CommitAsync` refuses a reference repository (service boundary, covers REST `/git-changes/commit`). `StageAsync` / `UnstageAsync` stay allowed.
- `WorkspaceCommitTargetPlanner.Plan` (`:26`): reference repositories go to `LeftOut` with reason `Reference`; the commit dialog shows the count; the optional push after commit (`MultiRepo.cs:378`) never contains them.
- `BulkSectionActionAsync` (`MultiRepo.cs:425`): stage-all / unstage-all over more than one repository skips reference repositories and says so. Row, folder, file and multi-select actions are explicit and allowed.
- Discard (`WorkspaceGitChanges.Discard.cs`): per D5. `ConfirmDiscardAllChanged` (`:259`) skips reference repositories.
- AI: `BuildAiCommitScope` (`Ai.cs:85`) excludes them through the same planner set; `WorkspaceAiGenerationOperations.GenerateCommitMessageAsync` also filters by policy so the service does not trust the page.
- Tests: commit refused service-side with files staged; Commit All skips and reports; stage-all skips, single stage works; AI scope excludes.

**Check-in.**

### Phase 1, first group as built: WP1, WP4-WP7 (2026-11-12)

Not committed (base `d0f00d30`). Results are in the test log, "Phase 1, first group". The manual smoke list was **not run**. Nothing in the product can set the flag yet (WP8, WP9): everything below was exercised only by tests that seed `IsReference` in the database.

**Done**

- WP4, bulk: create branch and Prepare Workspace, Workspace-wide checkout, dependency update, version files, push, pull level, Return to Default (bulk) and the Actions page bulk run and rerun leave read-only repositories out and return them in `Skipped`. Prepare with only read-only repositories selected fails with its own message.
- WP4, single repository: the row actions are refused with the policy message (the checks were already in the committed part). Pull request create, merge and bulk merge refuse a read-only repository in `WorkspacePullRequestOperations`; `CreateAsync` gained `workspaceId` for that.
- WP4, skips visible (4.4): `Skipped` is part of the REST bodies (the reason is written by name), `RepositorySkipSummary.Describe` builds the one sentence ("2 read-only repositories skipped: a, b."), and `ShowRepoErrors` has an information action that is used only when there is no error and no warning. Wired on the Repositories page (push, update, Prepare, Workspace-wide checkout, Update and Push notice), the notification popup and the Git Changes push.
- WP5, pull-only: `CommitSyncRepositoryRequest.pullOnly`, `GitService.PullFastForwardOnlyAsync` (`git pull --ff-only`), `GitRemoteIntegrateService.IntegratePullOnlyAsync`. A branch with local commits is refused before the pull; a pull blocked by local changes returns `localChangesBlockPull`; the command never pushes; the response carries `commit`. `WorkspaceCommitSyncHandler` sets the flag per repository, for the single pull and the level pull.
- WP5, checkout: `CheckoutBranchResponse` carries `commit` and `localChangesBlockCheckout` (Worker and App mirror).
- WP6: a read-only Source becomes a detached snapshot in a new Feature (`IsReferenceSnapshot`, `SourceBranchName`, no Feature branch, no parent branch). The Feature guard refuses every checkout in a snapshot; branch occupancy, the state writer, Switch Branch, the grid row, Remove Feature (an unchanged snapshot has nothing to lose) and the pin update know about snapshots.
- WP7: the read service, the tree (`ro` badge), the commit planner (`ReferenceReason`), Commit, Stage All, Unstage All, Discard All, the AI commit message and `WorkspaceGitChangesOperations.CommitAsync` treat a read-only repository as never committed and left out of bulk actions; staging, unstaging and discarding one named repository still work.

**Deviations**

- Tag skips are now shown too, not only read-only ones: the summary names repositories on a tag in the same toast. Before, a tag skip was silent.
- `UpdateAllVersionsAsync` now also leaves out repositories on a tag, because it goes through the same policy call.
- Bulk discard is enforced on the Git Changes page only. The page calls the Worker client directly, so there is no service to put the check in.
- The fast-forward pull is sent as `pull --ff-only origin <branch>` in the same argument style as the existing pull, not through `ArgumentList` with `--`. A branch name that starts with `-` is refused.
- The wording of the two pull refusals is new (`GitRemoteIntegrateService.LocalCommitsMessage`, `LocalChangesBlockPullMessage`); the note's exact text was not found.
- The enum `RepositoryOperationReason` is written as a string in JSON by an attribute on the enum, so it is a string everywhere it is serialized.

**Not done in this group**

- WP5 activation check, App half: built ahead of WP8 on the owner's decision, see "Activation check, App half" below.
- WP5 restore at the recorded revision: built with WP2-WP3, see "Phase 1, second group as built".
- WP5: the App does not yet use `commit` or `localChangesBlockCheckout` from the checkout response (recording is WP8; the bulk checkout already continues with the other repositories on any failure).
- Tests listed under "Not run" in the test log.

**Leftovers closed (2026-11-12, after the first check-in)**

- Package wait: `PackageProducerDiagnostic.IsReference`; the producer status in the diagnostics is `ReadOnly`; the timeout message names the read-only repository that produces a missing package and no longer tells the user to push again (`PackageWaitTimeout.FormatMessage`).
- Return to Default on one read-only row: the Repositories page asks the policy in Individual scope and calls `IWorkspaceBranchOperations.ReturnToDefaultAsync`, which is a plain checkout of the default branch (that service path was already in place). No analysis, no options dialog, no branch deleted. `ReturnToDefaultRepositoryPlan` gained `IsReference`; in a bulk analysis a read-only repository is still reported as left out (`IsOnTag` true, as before) and the toast uses the read-only message.
- Worker half of the activation check: `InspectWorktree` returns `headOnRemote` (`git branch -r --contains HEAD`). It is probed only when the branch has no upstream or HEAD is detached, and is null otherwise. The App mirror has the field.
- `RefreshProjects` keeps a read-only repository and the update plan and dependency sync leave it out: now a test.

**Checked, no change needed**

- `GetCommonBranchesAsync` already excluded read-only repositories (`wr.Allows(RepositoryOperation.WorkspaceCheckout)`); the earlier "not done" line was wrong.
- Actions row list (`WorkspaceActions.Loading.cs`): rows are built from the Workspace links, which carry `IsReference`, and every run and rerun guard reads the row's link. Nothing to add.
- Pipelined push lane: the lane leaves read-only repositories out (`LoadPushSkippedRepoIdsAsync`), and `UpdateAndPushResult.Skipped` already names them through the update half, which evaluates the same set (tag or read-only). Adding them to the push half as well would list every repository twice in the REST body. Deviation from the plan text; not covered by a test.

**Activation check, App half (2026-11-12, built before WP8 on the owner's decision)**

- `ReferenceActivationRule` (`Services/Policy`): the rule of WP5 on one `InspectWorktree` answer, with the messages. Pure.
- `ReferenceActivationChecker.CheckAsync(workspaceId, repositoryIds)`: inspects the Workspace checkout and every Feature worktree of each Source and returns the findings (block or warning, each naming the repository and the Feature) and the HEAD (branch and commit) of every Workspace checkout that is not blocked - WP8 records from it. It reads only. The Workspace-role repository is left out.
- A repository that is not cloned on this machine is a warning and its Feature rows are not inspected. The Worker reports that case as `Repository not found.`; a Worker test pins the text.
- The App mirror of the response gained `headSha`.
- Nothing calls the checker for a mode change yet; that is WP8. Its one caller today is the import guard of the second group.

### Phase 1, second group as built: WP2-WP3 (2026-11-12)

**Built**

- WP2: `WorkspaceManifestWorkspace.Reference`, `WorkspaceManifestRepository.Reference` and `.Branch` (all omitted when null). `WorkspaceManifestFile.MaxSchemaVersion = 2`; `Serialize` writes version 2 when any `reference` or `branch` is present and version 1 otherwise, whatever version the caller passes. `TryParse` reads 1 and 2 and rejects `reference` or `branch` in a version-1 file. `reference` uses the lenient boolean with its own property name in the message; the `codegraph` message is unchanged. `WorkspaceDefinitionRevisionPin.TryNormalize` holds the revision rules (nothing, `tag` + `commit`, or `branch` + `commit` with `reference: true`); `ReadTagPinCommit` became `ReadPinnedRevision` and returns the commit and the branch.
- WP3: `BuildFromDatabaseAsync` writes `workspace.reference` and every `repositories[].reference` only when the default is on or a Source is read-only (D3). `WorkspaceRepositoryTagPins` is now `WorkspaceRepositoryRevisionPins`: `CopyOnto` carries `branch` + `commit` onto entries the database marks read-only and drops them otherwise; a tag pin is always carried. `SetRepositoryReferenceRevisionsAsync` records a branch and commit; it shares the per-Workspace gate and the read-apply-write body with `SetRepositoryTagPinsAsync`. Drift has `ChangedReference` and `ReferenceDefaultChanged`. `RestoreDefinitionPlan` has `ReferenceDefault` and `ReferenceBySourceRepositoryId`; restore sets both before the first Sync. New Source links take `Workspace.ReferenceDefault` in `AddRepositoriesAsync` and `ReplaceRepositoriesCoreAsync`.
- D6: `ReferenceImportGuard` runs the activation check on imported folders that start read-only; a blocked one is made writable and the Workspaces page shows the warning.
- WP5, restore at the recorded revision: `GitService.CheckoutBranchAtCommitAsync` (`git checkout -B <branch> <sha>`, then upstream `origin/<branch>` when that ref exists); `CheckoutPinnedCommitAsync` uses it when the entry has a branch. A missing commit or an invalid entry removes the clone and fails that repository's sync.

**Deviations and decisions made while building**

- An entry without its own `reference` takes `workspace.reference`, in `TryParse`, `ReadPinnedRevision`, drift and restore alike (the plan stated the rule for restore only). Otherwise a legacy file with the workspace value and a `branch` would parse in one place and fail in another.
- The drift banner text is unchanged. The banner shows one fixed sentence for every kind of drift and the page keeps only a timestamp, so there is no place where it names anything today. The two new fields are on the result for a caller that wants them.
- `WorkspaceRepositoryOperations.cs:563` is the Workspace-role link, which is never read-only. Not changed.
- `AddRepositoriesAsync` does not run the activation check itself: it is a data class. The check runs in `ReferenceImportGuard`, called by the page right after the add. Between the two calls a blocked repository is read-only for a moment, which is the safe side.
- `AddRepositoriesAsync` reads the default as a value from the database, not from the tracked Workspace: the context is circuit-scoped and can hold the Workspace as it was before the default changed.
- Clearing a tag pin (`SetRepositoryTagPinsAsync` with no tag and no commit) now leaves an entry that has a branch alone. Before, it removed any `commit`.
- Recording a branch revision for an entry that is not read-only in the file returns a failed result and writes nothing (D9). WP8 must write the flags before it records.
- A file that says `false` everywhere equals a Workspace with no read-only state for drift, but is rewritten as version 1 by a restore that is complete on this machine (canonical form).
- "A version-2 file read by a parser pinned to 1" cannot be tested against this code: the refusal is the unchanged `version > 1` check of older releases. The tests show that a file with read-only data says `"version": 2` and that version 3 is refused here with the same message.

**Not done in this group**

- Nothing sets `IsReference` or `ReferenceDefault` from the UI, and nothing calls `SetRepositoryReferenceRevisionsAsync`: both are WP8-WP9.
- `ReplaceRepositoriesCoreAsync` (the Workspace editor) gives new Sources the default without an activation check. Those repositories are normally not cloned yet; a folder that already exists is the import path, which is checked.

### Phase 1, third group as built: WP8-WP9 (2026-11-12)

Not committed (base `4b6590bf`). Results are in the test log, "Phase 1, third group". The manual smoke list was **not run**. This group makes read-only mode reachable from the UI for the first time.

**Built**

- WP8, facade: `IWorkspaceReferenceOperations` (`GetStateAsync`, `SetAllAsync`, `SetDefaultAsync`, `SetRepositoryAsync`) and `WorkspaceReferenceOperations`. Every change runs inside `TryStartStructural(workspaceId, "reference-mode", ...)`; a refused start returns "An operation is running for this Workspace." Turning read-only on runs the activation check on every repository that changes; one block refuses the whole call, returns every block and warning, and writes nothing. Turning it off runs no check. The Workspace-role link is never selected. Flags and the default are saved in one `SaveChanges`; then the definition is written; then the branch and commit from the check are recorded; then `WorkspaceSynced` is broadcast.
- WP8, recording: `ReferenceRevisionRecorder` (singleton, own DI scope, one write at a time per Workspace; changes that arrive during a write go out together in the next one). Called after a pull-only pull (`WorkspaceCommitSyncHandler`, single and level), after a manual branch checkout of a read-only Source (`WorkspaceBranchOperations.CheckoutAsync`), and by the facade when read-only is turned on. It records only the special Workspace, only read-only Source links, only when a Workspace repository exists, and only a full commit hash. It never throws.
- WP9, Edit Workspace: `WorkspaceReferenceSection` inside `WorkspaceModal` shows the state sentence (none / some / all, no count), "Make all read-only", "Make all writable" and the "New repositories are read-only" switch. A batch action first asks, with a checkbox for the default. A refusal lists each blocked checkout. Everything is disabled while `IsBusy(workspaceId)`.
- WP9, grid: `ro` badge beside the branch or tag; the dependency badge tooltip gives the read-only message; `CommitsBadge` has `PullOnly` (click pulls when there are incoming commits, never pushes); `PRBadge` has `CanCreate`; the behind count is not a button on a read-only row.
- WP9, dialogs: `BranchModal` and `PrepareWorkspaceModal` show "Read-only repositories are not affected"; `SwitchBranchModal` warns when a read-only repository has uncommitted changes.

**Deviations and decisions made while building**

- The read-only section applies each action at once, not with the Save button of the modal: the change needs the lock and the activation check and can be refused, which a Save of the other fields cannot express. The section is shown only when editing; a new Workspace gets its default after it is created.
- `PendingActionsService.RefreshAsync` is not called by the facade: it needs the circuit-scoped repositories of the page. The `WorkspaceSynced` broadcast makes open pages reload, which recomputes the notification.
- Making a repository writable does not call the recorder to remove `branch` and `commit`: the definition write from the database already drops them for an entry that is not read-only (WP3). One write, not two.
- When the definition cannot be written after the flags were saved, the result is a success with a warning and nothing is recorded (the file does not have the flags yet, so a branch would be refused, D9). The flags stay; the drift banner will show the difference.
- Turning read-only on needs a connected Worker (the check cannot run without it); the call is refused otherwise. Turning it off and the default-only change do not.
- A pull has no place to show a warning that is not an error, so a failed recording after a pull is logged only. After a manual switch it is returned in `CheckoutBranchApiResult.Warning` (also in the REST body), but the Repositories page does not show it yet: `WorkspaceBranchHandler.CheckoutBranchAsync` returns only success and error.
- A switch answered by an old Worker without `commit` succeeds with the warning that the commit could not be read.
- The header counters and notification sets needed no code change: they already go through the policy expressions with `IsReference` (first group). Tests were added.
- The fixed line in the two dialogs is shown only when the Workspace has a read-only repository.
- "The conflict result shows which files block": the git error text, which lists the files, is shown as the checkout error as before. `localChangesBlockCheckout` is still not read by the App.
- The finding has no separate reason field; its message is the whole sentence ("Repository X in Feature F: it has 2 unpushed commits.").

**Not done in this group**

- Per-repository switch in the UI (`SetRepositoryAsync` is built and tested; the UI is Phase 2).
- No component test renders the new section, the badges or the dialog lines.

### Phase 2 as built (2026-11-12)

Not committed (base `fb8df758`, which holds the third group of Phase 1). Results are in the test log, "Phase 2". The manual smoke list was **not run**. Appendix B (archived repositories) is a proposal only; none of it is built.

**Built**

- Switch Branch dialog: a "Read-only" checkbox in the footer, left side, wired to `SetRepositoryAsync`. It is hidden in a Feature view, for the Workspace repository, and while the "branch already exists" question is shown. It is disabled while the dialog works, while a page job runs and while `IWorkspaceOperationRunner.IsBusy(workspaceId)`. A refusal is shown in the dialog body with one line per blocked checkout; warnings are listed below it. It never reads or changes the default.
- Search: `ro:yes` and `ro:no` in `WorkspaceRepositoryLinkSearchExpressions`. The field name and the value are not case sensitive. Any other value of `ro:` matches nothing. An unknown field still matches everything, as before. The unused `WorkspaceRepositoryLinkSearchMatcher` is deleted (no caller in production or tests).
- Workspaces list: a "Read-only" column with `None`, `Some`, `All`, or `-` for a Workspace without Source repositories. The Workspace repository is not counted. `WorkspaceReferenceAggregates` (`Of`, `Label`) is the one place that decides it; the facade uses it too.
- Drift banner: when the banner is shown, the page compares once more and appends the read-only differences ("The read-only state differs for Api, Web." / "The read-only default for new repositories differs."; at most 5 names, then "and N more"). `WorkspaceManifestDrift.DescribeReferenceDifferences()` builds the text.
- Restore dialog: the preview has a "Read-only" row ("All repositories", "Some repositories", "New repositories only", each with ", and new repositories" when the definition's default is on). The row is absent when the definition has no read-only data. `RestoreWorkspacePreflight` carries `ReferenceAggregate` and `ReferenceDefault`; the count behind it includes listed repositories that are not found locally.
- Fix to the third group: a refused change left the checkbox ticked in the browser, because the stored value had not changed and Blazor saw nothing to update. The switch in the Edit Workspace section and the new checkbox are now recreated after every attempt (`@key`).

**Deviations and decisions made while building**

- The drift banner needs a second comparison because the database stores only the time drift was found, not what differs, and Phase 2 has no migration. So each page that shows the banner asks the Worker for the definition file once. Without a Worker, or when the file cannot be read, the plain banner stays. If the second comparison finds no drift, the banner goes away.
- The text is read once per banner. A read-only change made while the banner is open does not refresh it until the banner is hidden and shown again or the page is reloaded.
- `ro:` in a Feature view filters by the Workspace flag of the link, not by the Feature snapshot. The grid badge in a Feature shows the snapshot, so the two can differ for a repository whose flag changed after the Feature was created.
- `ro:no` also matches the Workspace repository (it is never read-only).
- The Workspaces list uses a column, not text beside the name: the name cell already holds the star, the link and the Default badge.
- The checkbox stays visible on the New Branch tab; the plan names only the footer.

**Not done**

- Appendix B: the tooltips (B.7) and the hint (B.8) are built. The open questions of B.5 are not decided.
- The warning of a manual switch (`CheckoutBranchApiResult.Warning`) is still not shown by the Repositories page.
- No component test renders the checkbox, the column, the banner or the preview row. The decisions behind them are tested as functions.
- Section 11 (architecture documents, `AGENTS.md`, Desktop README) is not started; it follows the gate.

### WP8 - Mode-change facade and recording

- `IWorkspaceReferenceOperations` (`GrayMoon.Application`), implementation in `Services/Application/`:
  - `GetStateAsync(workspaceId)` -> default, aggregate (`None` / `Mixed` / `All` / `NoSources`), per-Source flags.
  - `SetAllAsync(workspaceId, bool reference, bool alsoSetDefault, progress)`; `SetDefaultAsync(workspaceId, bool)`; `SetRepositoryAsync(workspaceId, repositoryId, bool, progress)` (used in Phase 2, built here).
  - All run under the structural lock (4.5). Turning on: activation check for every affected Source across Workspace and Feature checkouts; any block refuses the whole call and returns all reasons and warnings; nothing is written. Turning off: no git check. Workspace-role link is never in the set.
  - On success, in one `SaveChanges`: flags (and default). Then `SyncDefinitionToDiskAsync`, then record `branch`+`commit` for the Sources just made reference (HEAD from the check), or remove them for Sources made writable. Then `PendingActionsService.RefreshAsync` and a hub broadcast so open pages reload.
  - No Workspace repository: flags are saved, the result carries the limitation text (note open item 2).
- **Recording `branch` + `commit`** through one `ReferenceRevisionRecorder` (App, own DI scope, per-Workspace coalescing like `SyncRecomputeCoalescer`, serialized with other `graymoon.json` writes):
  - after a reference pull (`CommitSyncResponse.State.HeadCommit`),
  - after a manual branch or tag checkout in the special Workspace (`WorkspaceBranchOperations.CheckoutAsync`; tag keeps the existing pin path),
  - when read-only is turned on (HEAD from the activation check),
  - not from a Sync and not from a hook (D8, F24). `RecordCheckedOutTagPinsAsync` and `SyncCommandHandler` are not touched. A commit reached with git outside GrayMoon is recorded by the next GrayMoon pull or switch of that repository.
  - Special Workspace only, Source role only, never from a Feature. A failed write is logged and returned as a warning; it never fails the pull or the switch. The next recording event writes it.
- Tests: atomic refusal with two Sources (one blocked) leaves both unchanged; the toggle is refused while a context operation holds the lock and a context operation is refused while the toggle runs (`WorkspaceOperationLockTests` style, no real time); default-only changes no link; recording after pull and checkout; no change to `graymoon.json` after a Sync or a hook report with a new head; a failing manifest write does not fail the pull; nothing recorded from a Feature context.

### WP9 - UI for Phase 1

- `WorkspaceModal.razor` (Edit Workspace): read-only section with the none / mixed / all state (no count), "Make all read-only", "Make all writable", and a separate "New repositories are read-only" switch for the default. A batch action asks whether to set the default too. Blocked activation lists repository, checkout (Workspace or Feature name) and reason. Disabled while `IsBusy(workspaceId)`. The modal calls `IWorkspaceReferenceOperations`, not the repository.
- Repositories grid: `ro` badge beside the branch or tag (`WorkspaceRepositoriesRow.razor:125`), tooltip "Read-only repository"; write badges inactive through the policy; `CommitsBadge` still clickable for incoming (pull) and shows outgoing without offering push.
- Header counters and notification sets: `Push`, `Unmatched`, `OutOfDateFiles`, `CreatablePr`, `Lowest` exclude reference; `Incoming` keeps it - all through the policy expressions (`Pull` is allowed, so no special case).
- Manual switch on a reference row: warning when the repository has local changes; the conflict result shows which files block.
- `BranchModal` and `PrepareWorkspaceModal`: a fixed line "Read-only repositories are not affected"; the "Skip repos on tags" option is unchanged and does not apply to them.
- Checkbox styling rule (`form-check-input-gray`), buttons never wrap.

**Check-in. Phase 1 is complete here.**

## 9. Phase 2 - per-repository control and mixed-Workspace UX

No migration and no manifest change.

- `SwitchBranchModal.razor` footer (`:525-606`): "Read-only" checkbox for the selected Source, wired to `SetRepositoryAsync`. Hidden for the Workspace-role repository and in a Feature context (the flag belongs to the Workspace); never forced by the default; disabled while the Workspace is busy; shows the activation refusal inline.
- Search: `ro:yes` / `ro:no` in `WorkspaceRepositoryLinkSearchExpressions.BuildTermPredicate` (`:19-44`; today an unknown field prefix matches everything). Delete the unused twin `WorkspaceRepositoryLinkSearchMatcher` or keep both in step; tests in the existing search test class.
- Workspaces list (`Workspaces.razor:459`, already loads `Repositories`): none / mixed / all text per Workspace, no count.
- Drift banner and Restore dialog name reference differences.
- Tests: toggling one Source changes no sibling and not the default (the note's 20-repository example); a Workspace default of true with one Source false survives restore (regression of WP3 through the UI path); filter.

Manual smoke list (also used at 0C for M1-M8 with a tag-pinned repository instead of a read-only one):

- M1 Push / Push Synchronized / Push Updated with one excluded repository holding outgoing commits.
- M2 Update Dependencies and Update and Push (two-lane and sequential fallback).
- M3 Prepare Workspace from the page and through REST.
- M4 Create Branch and Switch Branch for the Workspace.
- M5 Sync Commits on a level and on one row.
- M6 Return to Default (dialog and unattended) and bulk Merge PRs.
- M7 Git Changes: stage, unstage, Commit All, Undo.
- M8 Create, Repair, Remove and Roll back a Feature.
- M9 Turn read-only on with: clean, dirty, unpushed, no upstream, Feature worktree with unpushed commits.
- M10 Pull a read-only repository; check `graymoon.json` changed in the Workspace repository.
- M11 Restore the committed definition on a second machine or folder; flags and revisions match; first Sync treats no Source as writable.
- M12 Toggle while an Update and Push runs (refused), and start a push while a batch toggle runs (refused).
- M13 Older build opens a version-2 definition and reports "schema 2 is newer".

## 10. Reference-mode gate

1. The full command list of section 5 step 5, including both `Long` runs, on the final tree.
2. All 0A characterization tests still green (those changed on purpose in 0D are listed).
3. All new reference tests green; the test list in the note's "Constraints" section is mapped one by one to a test name in the log.
4. Manual smoke M1-M13 recorded as run by a person.
5. Any item that could not run is written down and the gate is "not passed".

## 11. Documentation and housekeeping (with the phase that ships the behavior)

- `docs/architecture/01` (model: reference Sources, Feature snapshots), `03` (columns, manifest v2, file-owned `branch`+`commit`), `04` (mode change as a structural operation, pull-only command mode), `05` (capability reference: allowed / skipped table), `06` (extension guide section 11: use the policy, never a new `IsOnTag` check; new-operation checklist), `07` if the Git Changes view changes.
- `AGENTS.md` "Tag-pinned repositories" and the same section in `CLAUDE.md`: rewritten around the policy after 0B; reference rules added after Phase 1. `.cursor/rules` twin if one exists.
- `../GrayMoon.Desktop/README.md` "Recent GrayMoon changes": one bullet when Phase 1 ships, edited when Phase 2 ships. Not for 0A-0C (refactor and tests).
- `docs/todo/README.md`: this plan is linked from the read-only row; when the feature ships, the idea note, this plan, the inventory and the test log are deleted and the row removed.
- The idea note was corrected on 2026-11-12 for F3 (dialog name), F4 (action name), F16 (no Workspace merge), F25 (sample) and decisions D3-D12.

## 12. Risks

| Risk | Mitigation |
|---|---|
| 0B changes behavior by accident while moving 60+ checks | 0A tests written first; one call-site group per step; `SubjectSource` keeps per-site quirks; convention test |
| Counter SQL stops translating in EF after the expression swap | Parity test runs on SQLite, not in memory; existing `WorkspaceRepositoryLinkListQueryServiceTests` |
| A reference repository reaches the old pull+push command | `pullOnly` decided in the handler from the policy; Worker test asserts no push; service test seeds outgoing commits |
| The recorder and the definition sync write `graymoon.json` at the same time | One recorder, per-Workspace serialization, write-if-changed |
| The recorded commit is stale after a `git pull` outside GrayMoon | Accepted with D8; the next GrayMoon pull or switch records it, and the note says the file is a snapshot |
| A row action from `/api/branches/*` races a mode change | Dispatch-time policy check; the remaining one-request window is accepted (4.5) |
| Version-2 files lock out older builds for everyone | D3: version 2 only when reference data exists |
| Detached non-tag Feature rows break "has identity" assumptions | WP6 audit of readers before the first snapshot is created; tests on state writer and hook path |
| Entry point outside the runner lock toggles mid-run | 0A lock audit (F18); dispatch-time `EvaluateAsync` where the lock is not held |
| Phase 1 size | Three internal check-ins; no user-reachable switch until WP8/WP9 |

## Appendix A - inventory seed (production code, `1515d32b`)

Class: E-filter (bulk candidate filter), E-refuse (single refusal), E-ui (per-row disable or page filter), P (presentation), S (state write), B (pin bookkeeping). Subject: L link, C context, F Feature row.

**Superseded by Phase 0A.** The complete, re-verified list is [read-only-repositories-inventory](read-only-repositories-inventory.md) (379 lines in 78 files of production code, every REST route, every Worker command). This table is kept as the seed it was. Corrections found in 0A:

- Line numbers are the start of the statement in some rows and the line that names the tag in others; they are off by one or two against the search (`WorkspaceGitService.Projects.cs` 47/141/191/215/254, `.Commit.cs` 34/134, `WorkspaceUndoPushHandler.cs` 27, `WorkspaceSyncHandler.cs` 274, `.ReturnBatch.cs` 184, `WorkspaceUpdateAndPushOperations.cs` 84, single push refusal `WorkspacePushService.cs` 1009-1010). The inventory has the exact lines.
- `WorkspaceBranchOperations.cs:524` is the common-branch intersection (a read), not the Workspace-wide checkout. The Workspace-wide checkout is `WorkspaceBranchHandler.CheckoutBranchForWorkspaceAsync` (`:235`) and has no tag check.
- The special Workspace push plan reads the context state row when one exists and the link only when it does not (`WorkspaceProjectRepository.Push.cs:159-190`).
- `WorkspaceUpdateAndPushOperations.cs:84` reads the link DTO (subject L, not C).
- `WorkspaceRepositoryRole` (`GrayMoon.App/Models`) is **not visible** to `GrayMoon.Application`. The policy core needs its own enum or a bool, or the enum moves down; `WorkspaceFeatureBaseKindApplication` is the precedent for a mirrored enum.
- No service-side tag check exists in: pull (single and level), bulk Create Branch, Workspace-wide checkout, Prepare Workspace with no repository ids (branches are created in every repository, tag ones included), Update Branch from Default, Git Changes commit, stage and unstage, and every `/api/branches/*` write. Each is pinned by a test named `..._has_no_service_side_tag_check_today`.
- In the Worker only `StageAndCommitCommand.cs:26-28` declines on a tag. `CreateBranchCommand.cs:69` checks after the branch exists. There is no tag check in `PushRepository`, `CommitSyncRepository`, `UndoPush`, `UpdateBranchFromDefault`, `SyncRepositoryDependencies`, `UpdateFileVersions`, `WriteRepositoryFile`, `CheckoutBranch`, `DeleteBranch` or `ReturnToDefaultBranch`.
- `SyncStatus` is mapped with `InSync` as its insert sentinel: a row inserted as `InSync` is stored as `NeedsSync`. Tests and migrations that need an in-sync row must set it by update.

| Area | File | Lines | Class | Subject | Policy operation |
|---|---|---|---|---|---|
| Update | `Services/Orchestration/DependencyUpdateOrchestrator.cs` | 370, 420-446, 556-563 | E-filter | L | UpdateVersionFiles, UpdateDependencies, Commit |
| Update | `Services/Git/WorkspaceGitService.Projects.cs` | 46 | E-filter | L | RefreshProjects |
| Update | same | 140, 191 | E-refuse (silent) | L | RefreshProjects, UpdateDependencies |
| Update | same | 213, 252 | E-filter | L | UpdateDependencies |
| Commit | `Services/Git/WorkspaceGitService.Commit.cs` | 32, 132 | E-filter | L | Commit |
| Restore packages | `Services/Git/WorkspaceGitService.Restore.cs` | 148-167 | E-filter | C | RestorePackages |
| Push | `Services/Workspaces/WorkspacePushService.cs` | 167, 863/901/942, 1090, 1193 | E-filter | C | Push |
| Push | same | 1006 | E-refuse | C | Push |
| Push | same | 1522, 1571 | B (post-push refresh) | C | - |
| Push | same | 1643-1695 | loader copy | C | - |
| Push | `Repositories/WorkspaceProjectRepository.Push.cs` | 159-195 (loader), 267, 292 | E-filter | C | Push |
| Push | `Repositories/WorkspaceRepository.cs` | 290-353, 419-480 | E-filter / P | C | Push |
| Undo push | `Services/Orchestration/WorkspaceUndoPushHandler.cs` | 26 | E-filter | L (overwritten per context in `WorkspaceSyncOperations.cs:157`) | UndoPush |
| Return | `Services/Orchestration/WorkspaceSyncHandler.cs` | 273, 383, 458-532, 546 | E-filter / plan | L | ReturnToDefault |
| Return | `WorkspaceSyncHandler.ReturnBatch.cs` | 183 | E-filter | L | ReturnToDefault |
| Branch | `Services/Application/WorkspaceBranchOperations.cs` | 524 | E-filter | L | WorkspaceCheckout |
| Branch | same | 181-200, 285 | P / B | L or C | - |
| Branch | same | 339-376, 412-418, 691-697, 877-889, 930-985 | B | L, F | - |
| Update and Push | `Services/Application/WorkspaceUpdateAndPushOperations.cs` | 83 | E-filter / E-refuse | C | UpdateDependencies |
| Counters | `Services/Queries/WorkspaceRepositoryLinkListQueryService.cs` | 76-112, 115-154, 170-216, 219-273 | E-filter (SQL) | L / C | Push, Pull, UpdateDependencies, UpdateVersionFiles, CreatePullRequest |
| Notifications | `Services/Ui/WorkspacePendingActionsService.cs` | 89-138 | E-filter | C | same |
| Notifications | `Components/Shared/UiRaceGuards.cs` | 121-157 | E-filter | C | same |
| Row | `Components/Pages/WorkspaceRepositoriesRow.razor` | 32, 460, 469, 658-675 | E-ui | row | UpdateDependencies, Pull/Push, Sync |
| Row | same | 125-133, 163, 178, 321-333, 645 | P | row | - |
| Page | `WorkspaceRepositories.State.cs` | 135, 191, 386-395 | E-ui | row | several |
| Page | `.Branches.cs` | 221-229, 280-302 | E-ui (option `skipReposOnTags`) | row | WorkspaceCheckout, CreateBranch |
| Page | same | 68 | P | row | - |
| Page | same | 446 | E-ui | row | UpdateBranchFromDefault |
| Page | `.Update.cs` | 24-40, 135-158, 211-236 | E-ui | row | UpdateDependencies |
| Page | `.Push.cs` | 33, 89, 119 | E-ui | row | Push |
| Page | `.UndoPush.cs` | 18 | E-ui | row | UndoPush |
| Page | `.CommitSync.cs` | 18, 38, 56, 82, 98, 122 | E-ui | row | Pull, Push |
| Page | `.ReturnToDefault.cs` | 64, 127, 160, 295, 354, 390 | E-ui | row | ReturnToDefault |
| Page | `.BulkMerge.cs` | 136 | E-ui | row | MergePullRequest |
| Page | `.PullRequests.cs` | 62, 405 | E-ui | row | CreatePullRequest |
| Page | `.PrepareWorkspace.cs` | 46-65 | E-ui (option) | row | CreateBranch |
| Page | `.Dependencies.cs` | 70, 173, 209 | E-ui | row | UpdateDependencies |
| Page | `.FileVersions.cs`, `.Sync.cs` | 62, 126; 31 | E-ui | row | UpdateVersionFiles, Sync |
| Context bar | `Components/Features/WorkspaceContextBar.razor` | 88 | P | row | - |
| Badges | `CommitsBadge.razor`, `DivergenceBadge.razor`, `PRBadge.razor` | 3-70; 6; 13-127 | P, `ShowsCreateBadge` E-ui | row | CreatePullRequest |
| Features | `Services/Features/WorkspaceFeatureOperations.cs` | 283, 342, 386, 516, 551, 584, 934, 1977, 2019, 2252, 2425-2482 | detach decisions | F | CreateFeatureBranch |
| Features | `FeatureBranchGuard.cs`, `FeatureBranchPolicy.cs`, `SwitchBranchModal.razor:1070` | 52-64; 32-89 | E-refuse | F | ManualCheckout, CreateBranch, ReturnToDefault |
| State | `Services/Workspaces/WorkspaceRepositoryStateWriter.cs` | 133-624 | S | - | - |
| Pins | `Services/Git/WorkspaceGitService.Sync.cs` | 48, 190, 242-271 | B | - | - |

No service-side check today (F5): `WorkspaceCommitSyncHandler.cs:31/124`, `WorkspaceGitService.Branches.cs:121-288`, `WorkspaceBranchHandler.cs:235`, `PrepareWorkspaceOrchestrator` / `PrepareWorkspacePhases`, `WorkspaceBranchUpdateHandler.cs:31`, `WorkspaceGitChangesOperations.cs:69-105`, `WorkspaceGitChanges.Discard.cs` (page to Worker client), `WorkspaceActions.BulkActions.cs`, every endpoint in `Api/Endpoints/WorkspaceOperationsEndpoints.cs`.

Worker (`worker-git-state`, unchanged in 0B): `SyncRepositoryCommand`, the hook sync commands, `RepositoryStateProbe`, `LibGit2SharpLocalGitSnapshotReader`, `GitCliRepositoryReader`, `LocalGitSnapshot`, `StageAndCommitCommand`, `ReturnToDefaultBranchCommand`, `CreateBranchCommand`, `GetHeadCommitsCommand`, `GetBranchesCommand`, `FetchCommitsCommand`, `RefreshBranchesCommand`, `RefreshRepositoryVersionCommand`.

Existing tests that already pin tag behavior (keep green throughout): `ReturnToDefaultAnalyzeTests`, `ReturnToDefaultUnattendedBatchTests`, `ReturnToDefaultPersistenceTests`, `WorkspaceRepositoryLinkListQueryServiceTests`, `WorkspaceRepositoryLinkListQueryServiceRoleTests`, `WorkspacePushStrategyTests`, `WorkspaceUpdateAndPushOperationsTests`, `FeatureBranchGuardTests`, `FeatureContextBranchRefreshScopingTests`, `CreateFeatureParentBranchTests`, `WorkspaceB4ContextLeakTests`, `BranchRefsSyncTests`, `SyncCommandHandlerTests`, `WorkspaceRepositoryStateWriterTests`, `ModalAndSharedUiGuardTests`, `WorkspaceManifestServiceTests`, `WorkspaceManifestFileTests`, `WorkspaceDefinitionTagPinTests`, `WorkspaceOperationLockTests`, `WorkspacePageJobDisciplineTests`, `Conventions/ServiceConventionTests`, Worker `SyncRepositoryTagPinTests`, `ReturnToDefaultBranchCommandTests`.

## Appendix B - Archived repositories and the read only badge

Written 2026-11-12, after WP8-WP9. Line numbers are of commit `fb8df758` (the third group of Phase 1). All paths are under `src/GrayMoon.App/`. This appendix records what the code does and proposes a rule. Nothing in it is built, and the proposal is not a decision.

### B.1 What the code does today

**The flag is `Repository.Archived`, not `IsArchived`.** A search for `IsArchived` finds only `PushErrorFormatter.IsArchivedRejection`, which is a text test on a push error and not a stored flag.

**Stored**

- `Models/Repository.cs:28`: `public bool Archived { get; set; }`. It is on the repository, so it is the same in every Workspace that links the repository.
- `Data/AppDbContext.cs:111-112`: the column has default `false`. A search of the App project found no migration file that names the column.

**Set**

- `Models/GitHubDtos.cs:37-38`: the provider answer carries `archived`.
- `Services/GitHub/GitHubRepositoryService.cs:198`: `FetchConnectorAsync` copies `repo.Archived` to the new `Repository` object.
- `Repositories/RepositoryRepository.cs:114` (`MergeRepositoriesAsync`): a difference in `Archived` counts as a change (line 173) and the value is written in all three match paths (lines 200, 236, 293).
- Nothing else writes it. No push, pull, Sync, hook or Worker answer changes it.

**Refreshed**

- Only by `GitHubRepositoryService.RefreshRepositoriesAsync` (line 31; the merge is at line 115). It has three callers, all of them a "fetch repositories" action that the user starts: `Components/Pages/Repositories.razor.cs:243`, `Components/Pages/Workspaces.razor:953` and `Components/Pages/WorkspaceRepositories.RepositoriesModal.cs:133`.
- There is no timer and no background refresh. A repository that is archived on the provider stays not archived in GrayMoon until the next fetch, and the other way round.

**Read**

- The grid row gets it through the list query: `Services/Queries/WorkspaceRepositoryLinkListModels.cs:49`, `WorkspaceRepositoryLinkListQueryService.cs:685` and `:758`, `WorkspaceRepositoryLinkListMapper.cs:46`. A test covers the round trip (`WorkspaceRepositoryLinkListQueryServiceTests.cs:158`).
- It is also copied in `Components/Pages/WorkspaceActions.Loading.cs:75` and `Services/Ci/GitHubActionsPushRunWatch.cs:90`.
- The operation policy does not read it. `RepositoryPolicySubject` has no archived field, and no file under `Services/Policy` or `GrayMoon.Application/Repositories/Policy` names it. The header counters do not read it either.

**Shown**

- Yes, the grid already shows an archived badge: `Components/Pages/WorkspaceRepositoriesRow.razor:55-58` (name is a link) and `:86-89` (name is plain text). It is the text `archived` in a purple badge, class `repo-archived-badge` (`Components/Pages/WorkspaceRepositories.razor.css:213-219`), in the name cell, with no tooltip.
- The same class is also used for the `no connector` and `not found on provider` badges in the same cell (`WorkspaceRepositoriesRow.razor:61`, `:65`, `:92`, `:96`).
- Other places: the repository picker (`Components/Modals/WorkspaceRepositoriesModal.razor:114-117`) and the Repositories page (`Components/Pages/Repositories.razor:199-202`, class `badge-archived`).

**What happens on a write today**

- Nothing stops it in GrayMoon. A push to an archived repository goes to the provider, is rejected there, and the error is rewritten to "Push rejected: this repository is archived on GitHub and is read-only." (`Services/Workspaces/PushErrorFormatter.cs:9-10`, test `PushErrorFormatterTests.cs:8`).
- A local commit, a branch, a dependency update and a version file update all succeed locally. Only the push fails.

**The read only badge**

- `ro`, gray (`badge bg-secondary`), tooltip "Read-only repository", in the branch cell: `WorkspaceRepositoriesRow.razor:145` (snapshot in a Feature), `:162` (on a tag) and `:174` (on a branch). In the Git Changes tree: `Components/GitChanges/GitChangesTree.razor:67`.
- It shows `WorkspaceRepositoryLink.IsReference` (per Workspace, chosen by the user) or `IsReferenceSnapshot`.

### B.2 The two states are different things

| | Archived | Read-only (reference) |
|---|---|---|
| Who decides | the provider | the user, in GrayMoon |
| Scope | the repository, in every Workspace | one Workspace link |
| Stored in | `Repository.Archived` | `WorkspaceRepositoryLink.IsReference` |
| Written to `graymoon.json` | no | yes (`reference`) |
| Can be stale | yes, until the next fetch | no |
| Enforced by | the provider, on push | the policy, before the Worker is asked |
| Badge | `archived`, purple, name cell | `ro`, gray, branch cell |

So a repository can be archived and writable in GrayMoon (today's case: writes work locally, push fails), read-only and not archived (the normal read-only case), or both.

### B.3 Problems

1. **An archived repository looks writable.** The badges for commit, push, create pull request and update are active on it. The user finds out at push time.
2. **Two badges that both say "read-only" in words.** The push error calls an archived repository "read-only", and the `ro` tooltip says "Read-only repository". A user who sees `ro` cannot tell from the badge whether GrayMoon or the provider is the cause.
3. **Both badges on one row.** An archived repository that is also read-only shows `archived` in the name cell and `ro` in the branch cell. That is correct but says the same thing twice from the user's side.
4. **The flag can be wrong.** Any rule built on `Archived` acts on the value of the last fetch.

### B.4 Proposal (for the owner to decide)

Keep the two states and the two badges separate. Do not show `ro` for an archived repository that is not read-only, and do not set `IsReference` from `Archived`.

- **P1. Badges stay as they are.** `archived` means the provider refuses writes. `ro` means GrayMoon does not write. A row with both shows both.
- **P2. Tooltips say who decided.** `archived`: "Archived on the provider. Pushes are rejected." `ro`: "Read-only in this Workspace. GrayMoon does not commit, push or update it."
- **P3. Suggest, do not force.** In the read-only section of Edit Workspace (and in the per-repository control of Phase 2), an archived repository that is writable gets a hint: "Archived on the provider. Make it read-only?" The user decides. The activation check runs as for any other repository, so unpushed commits still block.
- **P4. No policy rule on `Archived` in Phase 2.** The reason is problem 4: a stale value would refuse a commit in a repository that was un-archived an hour ago, with no way out except a fetch. The push error text already names the cause.
- **P5. Archived does not enter `graymoon.json`.** It is a provider fact, read again on every fetch.

### B.5 Open questions

1. Is P4 right, or should the policy leave an archived repository out of bulk push with a skip reason of its own (`Archived`), accepting the stale value? If yes, single-repository push must stay possible so that a stale flag does not lock the user out.
2. Should a repository that becomes archived on a fetch raise a one-time notice in the Workspaces that link it as writable?
3. Should the hint of P3 also appear when a repository is added to a Workspace whose default is writable?
4. Is `ro` the right text once both badges can be on one row, or should the read-only badge say `read-only`? (The grid width was the reason for `ro`.)

All four questions were answered by the owner on 2026-11-12. See B.9.

### B.6 Work, if the proposal is accepted

Small, UI only, belongs to Phase 2:

- Tooltips of P2 on the three `archived` badge places and the four `ro` places listed in B.1.
- The hint of P3 in `WorkspaceReferenceSection.razor` and in the Phase 2 per-repository control. `IWorkspaceReferenceOperations.GetStateAsync` returns `Archived` per repository for that.
- Tests: the state carries `Archived`; the hint is shown only for archived and writable.
- No schema change, no manifest change, no policy change.

### B.7 Decision and what is built (2026-11-12)

The owner decided: the tooltips of P2 now, everything else about archived repositories after the feature ships.

**Built (not committed)**

- `Services/Ui/RepositoryBadgeTitles.cs` holds the four texts, so each badge place uses the same words.
- `archived` badge, tooltip "Archived on the provider. Pushes are rejected.": the grid row (both places), the repository picker and the Repositories page.
- `ro` badge on a branch or tag in the grid, tooltip "Read-only in this Workspace. GrayMoon does not commit, push or update it."
- `ro` badge on a snapshot in a Feature, tooltip "Read-only in this Feature. GrayMoon does not commit, push or update it."
- `ro` badge in the Git Changes tree, tooltip "Read-only in this Workspace. GrayMoon does not commit or push it, and actions on all repositories leave it out."
- The `ro` badge itself was already built in the third group of Phase 1 (commit `fb8df758`): beside the branch, beside the tag, and on a Feature snapshot. Only its tooltip changed here.

**Deferred at that time**

- P3 (the hint). It was built later the same day, see B.8.
- Open questions 1-4 of B.5. P4 and P5 stand as they are until then: no policy rule on `Archived`, nothing about it in `graymoon.json`.

**Not tested**

- The tooltips are text in Razor markup. No test reads them and they were not looked at in a browser.

### B.8 The hint for an archived repository (2026-11-12)

The owner asked for the rest of the appendix. Built is P3. Not committed (base `8e3b30f1`, which holds Phase 2 and the tooltips).

**Built**

- `WorkspaceReferenceRepositoryState` has `Archived` and `SuggestReference` (archived and still writable). `GetStateAsync` fills `Archived` from `Repository.Archived`. `WorkspaceReferenceSuggestions` holds the rule and the two texts.
- Edit Workspace, read-only section: below the default switch, a callout "Archived on the provider, so pushes are rejected. Make them read-only?" with one line per archived and writable Source and a "Make read-only" button on each. The button calls `SetRepositoryAsync` for that repository. It is disabled while the Workspace is busy. The callout is absent when no repository qualifies.
- Switch Branch dialog: for an archived and writable Source in the Workspace view, a callout "This repository is archived on the provider, so pushes are rejected. Make it read-only? Use the Read-only checkbox below." It goes away when the checkbox is ticked. It is shown only where the checkbox is shown (not in a Feature, not for the Workspace repository).
- The suggestion changes nothing by itself. The activation check runs as for any repository, so an archived repository with unpushed commits is refused and stays suggested.

**Decisions made while building**

- One button per repository, not one for all: each change is checked and can be refused by itself, and the list is expected to be short.
- The dialog has no second button; the checkbox is in the footer of the same dialog.
- The value is the one of the last fetch of repositories (B.1). A repository that was un-archived since then is still suggested until the next fetch. Because it is only a suggestion, a stale value cannot block anything.

**Still open (B.5) at that time - decided later the same day, see B.9**

- Question 1: bulk push and archived repositories. Not built; no policy rule reads `Archived`.
- Question 2: a notice when a repository becomes archived on a fetch. Not built.
- Question 3: the hint when a repository is added to a Workspace. Not built; the hint appears the next time Edit Workspace or the dialog is opened.
- Question 4: `ro` or `read-only` as badge text. Unchanged: `ro`.

**Not tested**

- The two callouts are Razor markup. The rule behind them and the state are tested; the markup is not, and it was not looked at in a browser.

### B.9 The answers to B.5 and what is built (2026-11-12)

Not committed (base `8e3b30f1`).

**The owner's answers**

1. Yes: an archived repository is left out of a push. A local commit that is already there stays; the user reverts it by hand.
2. No notice when a repository becomes archived; the badge is enough. New request instead: when a repository is already marked archived, GrayMoon checks whether it is still archived, because it can be un-archived.
3. No hint when a repository is added to a Workspace.
4. The badge says `readonly` (no hyphen) as a first try.

P4 of B.4 is replaced by answer 1. P5 stands.

**Built: the push rule**

- `RepositoryPolicySubject.IsArchived` (init property, default false; `ForCheckout` has `isArchived`). `RepositoryOperationReason.Archived`. `RepositoryOperationPolicy.ArchivedMessage` = "Repository is archived on the provider. Pushes are rejected."
- `Rule.AllowedWhenArchived` (default true). Only `Push` sets it to false. `Evaluate` checks it after the tag rule and before the read-only rules, so a repository on a tag reports the tag, and an archived read-only repository reports archived. The rule holds for the Workspace repository too.
- The subject is filled from `Repository.Archived` at every place that builds one: `RepositoryPolicySubjectExtensions` (link and list item), `WorkspaceRepositoryOperationPolicyService.LoadAsync`, the two "needs push" queries in `WorkspaceRepository.cs`, and the checkouts of `WorkspacePushService`.
- `RepositoryOperationPolicyExpressions.Allows` has a sixth argument `isArchived`; the expanded SQL adds the term only for an operation whose rule reads it. All 18 call sites in `WorkspaceRepositoryLinkListQueryService` pass the repository's column, so the header no longer recommends a push for an archived repository.
- `RepositorySkipSummary` has a group "archived repository / archived repositories".
- Grid: an archived row with outgoing commits shows the commits badge as pull-only; its tooltip is the archived message (new `CommitsBadge.PullOnlyTitle`). A click on the push part shows the archived message as a toast.
- Commit, pull, sync and every other operation do not read the archived state.

**Different from the text of question 1: a single-repository push is refused too**

Question 1 said that a single-repository push must stay possible, so that an out-of-date mark cannot lock the user out. As built, the push of one named repository is refused with the archived message. The reason: the check below now runs before every push and once per page, so the mark is compared with the provider at the moment it matters, and a push the provider is sure to reject is not started. What remains: when the provider cannot be reached, or does not answer, the mark stays and the push is refused; a push would fail in that situation anyway. The way out is "fetch repositories". This is for the owner to confirm; allowing the single push again means the archived check moves to the Bulk scope only, and the row badge, the badge click and two guards in `WorkspacePushService` change with it.

**Built: the check for a repository that is no longer archived**

- `ArchivedRepositoryVerifier.VerifyAsync(workspaceId)` (`Services/Policy/ArchivedRepositoryVerifier.cs`). For the repositories of the Workspace with `Archived == true` it asks the provider. When the provider says "not archived", it clears the mark and sends the `WorkspaceSynced` broadcast, so open pages reload. It never sets the mark. No answer, an error, a removed connector or a provider other than GitHub keeps the mark. It never fails its caller.
- `IRepositoryArchiveProbe` with `GitHubRepositoryArchiveProbe`: GET `repos/{owner}/{repo}` through the existing `GitHubService.GetRepositoryMergeSettingsAsync` (ETag, no rate-limit retry). `GitHubRepositoryMergeSettingsDto` has `Archived`.
- When it runs: once per opened Workspace page (after the first load), before a push plan is made (`WorkspacePushOperations.GetPlanAsync`) and before a push (`PushAsync`). With no repository marked archived it costs one database query and no request.
- Not built: a background timer. Archiving (the other direction) is still learned only from "fetch repositories" (B.1).

**Built: badge text**

- `readonly` in place of `ro` in the grid row (3 places, now with `text-nowrap flex-shrink-0`) and in the Git Changes tree. The search field is still `ro:yes` / `ro:no`.

**Not built, by decision**

- A notice when a repository becomes archived (answer 2). The hint on adding a repository (answer 3).

**Not tested**

- `GitHubRepositoryArchiveProbe` against GitHub. The tests use a fake probe.
- The page trigger, the tooltip, the toast and the width of the `readonly` badge in the grid. Nothing was looked at in a browser.

### B.10 Wording: "read only" without a hyphen (2026-11-12)

Decided by the owner. Not committed (base `ee992ac0`).

- Every text the user sees says "read only" / "Read only": "Read only repositories", "Make all read only", "Repository is read only.", "1 read only repository skipped: ...", the tooltips, the toasts, the progress and the warning texts, and the error texts of the Workspace definition ("... is not read only"). Also the pull refusal text of the Worker.
- The badge stays `readonly` (B.9). The search field stays `ro:yes` / `ro:no`. Identifiers stay `Reference`.
- Not changed: code comments, and texts where the words are not about this feature (the GitHub push rejection "archived on GitHub and is read-only", which repeats GitHub's own wording; the AI sandbox mode).
- The texts quoted earlier in this document are as they were when written, with the hyphen.

**Fix on the same day:** Edit Workspace failed to open, because `WorkspaceReferenceSection.razor` injected `ToastService` and only `IToastService` is registered. It now injects `IToastService`. No test renders the component, so no test saw it.

**Why "Make all read only" takes long and "Make all writable" does not**

- Before a repository becomes read only, `ReferenceActivationChecker.CheckAsync` asks the Worker to inspect its main checkout and every ready Feature worktree of it (uncommitted changes, unpushed commits, the commit to record). These requests run one after the other. So the time grows with repositories plus worktrees.
- After the save, the Workspace definition is written and the commits of the read only repositories are recorded in it.
- Making repositories writable needs no check: it saves the flags and writes the definition.
- Possible later: run the inspections in parallel. Built the same day, see B.11.

### B.11 Overlay, parallel check, no "also" checkbox (2026-11-12)

Asked for by the owner. Not committed (base `ee992ac0`).

- **Overlay.** While a change of the read only section runs, Edit Workspace shows the loading overlay (`LoadingOverlay`, without the command terminal) in place of the small spinner. Its text is the running step: "Making repositories read only...", "Checking repositories for local work... 7 of 20", "Writing Workspace definition...". The section passes an `IProgress<OperationProgress>` to `SetAllAsync` / `SetRepositoryAsync`; the texts come from the operation.
- **Parallel check.** `ReferenceActivationChecker.CheckAsync` inspects the repositories in parallel, at most `WorkspaceOptions.MaxParallelOperations` at a time (the existing option, default 16). The checkouts of one repository (main checkout, then its Feature worktrees) are still inspected one after the other. The paths are read from the database first, one after the other. Findings and recorded commits are in repository name order, as before. The check reports "n of total" after each repository.
- **The checkbox "Also make new repositories read only" is removed.** It did nothing the switch "New repositories are read only" below the buttons does not do; it only saved one click. The question "Make all repositories read only?" stays. `SetAllAsync` keeps its `alsoSetDefault` argument; the dialog passes false.

**Not tested**

- The overlay in the dialog. Nothing was looked at in a browser. How much faster the check is on a real Workspace was not measured.

### B.12 Switch Branch dialog: a button in place of the checkbox (2026-11-12)

Asked for by the owner. Not committed (base `ee992ac0`).

- The "Read only" checkbox in the footer is replaced by a button with the look of Fetch (`btn btn-outline-secondary`). Its text is the change a click makes: "Make readonly" for a writable repository, "Make writable" for a read only one. It shows a spinner while the change runs. Where it is shown, when it is disabled, and what it calls (`SetRepositoryAsync`) are unchanged.
- The `@key` attempt counter is gone: a button has no ticked state that a refused change could leave behind.
- The hint for an archived repository now ends with: Use "Make readonly" below.
- Earlier sections of this document that speak of the checkbox describe the state before this change.

**Not tested**

- The button as markup. Nothing was looked at in a browser.

### B.13 Progress texts and no overlay for the default switch (2026-11-12)

Asked for by the owner. Not committed (base `ee992ac0`). This replaces the progress text named in B.11.

- The check shows "Checking repositories for local work..." until the first repository is done, then "Checked X of Y repositories". A count of 0 is never shown. The same texts are used when one repository changes its mode.
- The switch "New repositories are read only" shows no overlay; it is a quick save. The buttons are still disabled while it runs.

### B.14 Decision: the `readonly` badge stays beside the branch (2026-11-12)

Decided by the owner. Documentation only; no code changed.

- The `readonly` badge stays where it is: in the branch cell, beside the branch name or the tag (`WorkspaceRepositoriesRow.razor`), and on a Feature snapshot.
- It is not moved to the name cell beside the repository name. The owner had considered that later and dropped it.
- So a row that is archived and read only keeps `archived` in the name cell and `readonly` in the branch cell (problem 3 of B.3 stands as accepted).

### B.15 The overlay shows the git commands of the check (2026-11-12)

Asked for by the owner. Not committed (base `ee992ac0`).

- The check runs git in the Worker for every checkout (`GitWorktreeService.InspectWorktreeAsync`: worktree list, HEAD and branch, status, counts against the upstream and the default branch).
- The overlay of the read only section now shows the command terminal, like the other overlays. It reads the terminal of the running Workspace operation (`IWorkspaceOperationRunner.GetRunning(workspaceId).Terminal`), which is where the Worker output of that operation is collected. The terminal toggle of the overlay applies as everywhere.
- With the parallel check the lines of several repositories are mixed.

**Not tested:** nothing was looked at in a browser; whether lines appear depends on the running app and Worker.

### B.16 Switch Branch dialog: Lock / Unlock and the badge in the title (2026-11-12)

Asked for by the owner. Not committed (base `ee992ac0`). This replaces the button texts of B.12.

- The button in the footer says "Lock" for a writable repository and "Unlock" for a read only one (lock / unlock icon as before). The hint for an archived repository ends with: Use "Lock" below.
- The title shows the same `readonly` badge as the grid, after the repository name: Branch {repository} [readonly]. It follows the button at once, and in a Feature it carries the snapshot tooltip.
- Only the button uses the words lock and unlock. The state is still called read only everywhere else (badge `readonly`, Edit Workspace, messages, `ro:` search). A rename of the state to Locked / Unlocked was discussed and is not decided.

**Not tested:** markup only; nothing was looked at in a browser.

### B.17 Padlock in place of the `readonly` badge (2026-11-12)

Asked for by the owner. Not committed (base `e0528057`). This replaces B.14 and the title badge of B.16.

- Workspace repositories: a read only repository shows a padlock right after its name in the Repository column. The `readonly` badge beside the branch name is gone (branch, tag and Feature snapshot rows). The tooltip is unchanged (`RepositoryBadgeTitles.Reference` / `ReferenceSnapshot`).
- Switch Branch dialog: the title shows the same padlock after the repository name in place of the badge. The name no longer grows to fill the title, so the padlock sits next to it.
- The padlock is a new component, `Components/Shared/ReferenceLockIcon.razor` (inline SVG, 1em, current text color). The Bootstrap Icons padlock is narrow (10 of 16 units wide); this one has a body 12 units wide. `Open` draws an open shackle. The Lock / Unlock button uses it too.
- Not changed: the `readonly` badge in the Git Changes tree, and the search field `ro:yes` / `ro:no`.

**Not tested**

- The icon as markup. Nothing was looked at in a browser.

### B.18 Keyhole, white hover underline (2026-11-12)

Asked for by the owner. Not committed (base `e0528057`).

- `ReferenceLockIcon`: the body has a small keyhole. It is cut out of the shape (even-odd fill), so it shows the background behind the icon.
- Workspace repositories, Repository column: on hover the underline of the repository name was blue. Cause: `app.css` has `.table tbody td a:not(.badge):hover` (added in #513), which is more specific than the `.repo-link` color rule; the name stayed white because it is a `<strong>`, but the underline takes the color of the link. Fix: the `.repo-link:hover` rule in `WorkspaceRepositories.razor.css` sets `color: inherit !important` too. Not caused by the padlock.
- The rule `.cursor/rules/no-default-blue-links.mdc` (in `C:\Workspace\GrayMoon`, outside this repository) now says that an override must cover `:hover` and that hover must be checked.
- Not checked: links of other grids (Repositories, Actions) for the same hover color.

**Not tested**

- Nothing was looked at in a browser.
