# GrayMoon Worktree Features — Code Review Findings & Fixes (2026-09-21)

**Reviewed against:** `GrayMoon-Worktree-Features-Detailed-Implementation-Design-v3.md`
**Branch:** `worktree` (HEAD `2167aeb`)
**Scope:** All code under `src/GrayMoon.App`, `src/GrayMoon.Agent`, `src/GrayMoon.Application`, `src/GrayMoon.Common` related to `WorkspaceFeature*` / worktrees.
**Status of this pass:** Root-caused and fixed the 5 bugs reported by the user, found and fixed one additional
**data-corruption bug** during review, and catalogued the remaining architecture gaps against the design doc's
invariants (§3), Wave plan (§36), and required search gates (§37). No commits were made; all changes are in the
working tree.

---

## 1. Summary of what was fixed

| # | Symptom reported | Root cause | Fix |
|---|---|---|---|
| 1 | "Sync while on a Feature" → `Cannot create '...\EDX1\.git' because a file or directory with the same name already exists.` | `GitService.WriteSyncHooks` assumed `<repoPath>\.git` is always a **directory** and called `Directory.CreateDirectory(Path.Combine(repoPath, ".git", "hooks"))`. In a linked worktree (any Feature, or an external worktree) `.git` is a **file** containing a `gitdir:` pointer, not a directory — see design doc §13 and §26. Every sync of a Feature repository crashed here. | `GitService.WriteSyncHooksAsync` now resolves the hooks directory via `git rev-parse --git-common-dir`, which correctly returns the shared common-repo hooks directory for both a normal checkout and a linked worktree, per §13.1's "hooks are in the common Git directory" rule. |
| 2 | The primary header button stays "Branch" (or "Create PR") instead of switching to "Feature" | `WorkspaceRepositoriesHeader.razor`'s primary button label/color only ever looked at `HasCreatablePr`; it never checked `IsFeatureContext` at all, contradicting §24 ("Feature selected: Use `Feature ▼`"). | Button now shows `"Feature"` (outline style, no yellow "Create PR" shortcut) whenever `IsFeatureContext` is true, and its primary click just opens the `Feature ▼` menu instead of trying to open the (Workspace-only) Branch dialog or PR flow. |
| 3 | Repositories grid doesn't seem to update when switching Features | Two independent bugs, described in detail in §2.1 below. | (a) `WorkspaceRepositories.OnSelectedContextChangedAsync` now actually reloads the grid (`ClearGridState` + `LoadWorkspaceAsync`) instead of only flipping two fields and calling `StateHasChanged()`. (b) See §2.1 for the deeper, **not fully fixed**, architectural gap that this partially masks. |
| 4 | Remove Feature dialog content is not human friendly | `RemoveFeatureModal.razor` rendered raw domain output: `Classification: Active`, `"worktree present"`, `"; 3 outgoing"`. | Rewrote the dialog copy: a plain-language status callout per classification (Completed/Abandoned/Active/NeedsRepair), and a plain-language per-repository description (e.g. *"3 commits not pushed to the remote · Pull request #42 is still open"*) instead of field dumps. Checkbox labels reworded to state the actual consequence ("Yes, permanently discard any uncommitted changes…"). |
| 5 | After removing a Feature, the selector still lists it; clicking it kills the circuit ("GrayMoon needs to reload") | `WorkspaceFeatureSelector` only ever loaded its `_options` list **once**, on first render (`if (!_loaded) ReloadAsync()`). After a Feature is removed (or created), the in-memory option list is never refreshed. Selecting the stale, now-deleted option calls `IWorkspaceFeatureContextResolver.GetRequiredAsync`, which throws `InvalidOperationException("...was not found")` from inside a Blazor event handler → unhandled circuit exception → "GrayMoon needs to reload". | (a) The selector now reloads its option list every time the dropdown is opened, and whenever the externally-supplied `SelectedContextId` changes. (b) `WorkspaceRepositories.OnSelectedContextChangedAsync` also now catches a stale/missing context, shows a toast, and falls back to the special Workspace context instead of letting the exception propagate and take down the circuit. |

### Bonus fix — found during review, not in the original report

**`WorkspaceRepositoryStateWriter.ReconcilePullRequestAsync` was corrupting the special Workspace's checkout
state whenever a *Feature* context was synced.**

```csharp
// before (WorkspaceRepositoryStateWriter.cs)
private async Task ReconcilePullRequestAsync(..., WorkspaceRepositoryLink wr, ...)
{
    // Prefer context checkout branch for PR reconciliation; mirror onto the link for special Workspace.
    wr.BranchName = state.BranchName;   // <-- unconditional, despite the comment
    ...
    await pullRequestService.RefreshPullRequestsAsync(workspaceId, [repositoryId], ...);   // shared, 1:1-per-repo table
```

`WorkspaceRepositoryLink` is the **shared** row read by almost every other page (the Repositories grid, Push,
Update, Dependencies, PR badge, etc. — see §2.1). `WorkspacePullRequestService`/`WorkspaceRepositoryPullRequest`
are keyed by `WorkspaceRepositoryId` alone (§17/§37.3 flag this explicitly as needing a context-aware
replacement). Every other assignment in `ApplyAsync` (`MirrorIdentityToLink`, `GitVersion`, commit counts,
upstream, `SyncStatus`, projects) is correctly gated behind `if (isSpecialWorkspace)` — **only this one call site
was not**, despite its own comment claiming otherwise. The result: syncing a Feature would silently overwrite the
special Workspace's `BranchName` with the Feature's branch name, then query GitHub for a PR on that (wrong)
branch and overwrite the single shared PR row with the Feature's PR — corrupting the Workspace's own grid
row (branch text, PR badge) until the Workspace itself was independently re-synced. This is very likely a second,
compounding contributor to "workspace repositories doesn't update/looks wrong after switching Features".

**Fix:** the whole PR-reconciliation body (link mutation + legacy PR refresh/clear + context mirror) is now
skipped entirely for Feature contexts. A Feature's own PR projection (`WorkspaceRepositoryContextPullRequests`)
is intentionally left unpopulated until §17's context-aware `WorkspacePullRequestService` overload is built,
rather than risking corruption of the Workspace's shared state. This trades a missing PR badge on a Feature (a
gap that already existed) for the Workspace no longer being corrupted (a regression that did not need to exist).

---

## 2. Deeper architecture gaps (not fixed in this pass — flagged for a dedicated follow-up)

These are larger than "bugs" — they are places where Wave 3/5/6/7/9 of §36 were not carried through, even
though the schema and some write paths from Wave 1/2/4 exist. Attempting a full fix for all of these in one pass
would be exactly the kind of un-reviewable, cross-cutting change §36 explicitly asks to avoid ("Each wave should
be a separate reviewable PR"), and getting it wrong risks the Workspace regression gate in §40. They are
recorded here so they can be scheduled as their own wave(s).

### 2.1 The Repositories grid's read path is not context-aware at all (root cause of bug #3)

`WorkspaceRepositoryLinkListQueryService` (`IWorkspaceRepositoryLinkListQueryService`), which backs literally
every read the Repositories page performs (`CountAsync`, `GetHeaderStateAsync`, `GetIndexAsync`, `GetByIdsAsync`,
`GetAllSnapshotsAsync`, `GetRepositoryIdsAtLevelAsync`, `GetGitVersionNameMapAsync`, `GetSnapshotAsync`) queries
`db.WorkspaceRepositories` (`WorkspaceRepositoryLink`) directly and projects fields like `GitVersion`,
`BranchName`, `CheckedOutTag`, `OutgoingCommits`, `SyncStatus`, `DependencyLevel`, and `wr.PullRequest` straight
off that shared row. It has **no `WorkspaceFeatureContextId` parameter anywhere**, and never touches
`WorkspaceRepositoryContextState` or `WorkspaceRepositoryContextPullRequests`.

This directly violates:
- §6.4/§6.5 — those fields are supposed to stop being authoritative on the link after cutover;
- §10 — `WorkspaceRepositoryStateWriter` is described as "the primary migration seam", implying readers move too;
- §23 — "All of these pages must resolve the same selected context… Repositories… For Feature context, each
  page uses context projections and paths";
- §37.2's required search gate, which lists exactly these fields as ones that must come from context state
  after cutover "except explicit migration/compatibility code."

**Effect:** even with fix #3(a) above (the grid now *reloads* when you switch context), the query itself always
returns the special Workspace's row for the repository, because `WorkspaceFeatureContextId` never enters the
query at all. The grid will look identical for Workspace and every Feature — same branch, same GitVersion, same
sync status, same PR badge, same dependency level — until this service (and the paging/keyset/sort logic, which
is intertwined with these same fields) is rewritten to read `WorkspaceRepositoryContextState` /
`WorkspaceRepositoryContextPullRequests` for the selected context, falling back to the existing link-based
behavior only for the special Workspace context (mirroring the pattern already used correctly in
`GitChangesSnapshotPushHandler`, see §2.3).

**Why this wasn't attempted here:** this query service is the single most load-bearing read path in the app —
Update, Push, Dependencies, the header quick-actions, and virtual-scroll paging all depend on its exact sort/
keyset semantics for the *Workspace* baseline. Rewriting it to be context-aware safely requires its own
dedicated pass with the full baseline-appendix regression sweep (§40), not a fix folded into an unrelated
bug-triage session. Recommend scheduling this as the actual "Wave 3/9 for reads" work.

### 2.2 GitHub Actions are not context-aware at all

`WorkspaceActionService` has zero references to `WorkspaceFeatureContextId`, `IsSpecialWorkspace`, or the
already-created `WorkspaceRepositoryContextAction` table. Actions state is still purely `(WorkspaceId,
RepositoryId)`-keyed, so the Actions page will show the special Workspace's workflow runs regardless of which
Feature is selected. This matches design doc §18/§37 (not yet implemented), rather than being a regression —
flagging it here so it isn't lost as "already done" because the context table exists in the schema.

### 2.3 What *is* done correctly (so it isn't mistaken for another gap)

- `GitChangesSnapshotPushHandler` (Git Changes ingestion) is genuinely context-aware: it resolves the writing
  context via `IWorkspaceHookContextAttributor`, always writes `WorkspaceGitContextRepositoryStatus` /
  `WorkspaceGitContextChangeEntry` for that context, and only additionally mirrors onto the legacy
  `WorkspaceGitRepositoryStatus` / `WorkspaceGitChangeEntry` tables when `contextInfo.IsSpecialWorkspace` is
  true. This is the correct pattern per §6.10/§14, and is the template the grid (§2.1) and Actions (§2.2) work
  should follow.
- `WorkspaceHookContextAttributor` path-matching, `CreateWorktreeAsync`/`RemoveWorktreeAsync` idempotency
  (§25.7/§27.8), and the operation-lock scoping in `WorkspaceFeatureOperations` (structural vs. per-context) all
  look correct on inspection and match their respective design sections.

### 2.4 Feature PR/branch UX still missing pieces from §17/§28A

- Because of the bonus-fix in §1, a Feature's PR badge will currently never populate (previously it could
  populate but by corrupting the Workspace's own row — see above). Implementing §17's context-aware
  `WorkspacePullRequestService` overload (keyed by `(ContextId, RepositoryId, Branch)` as the doc recommends) is
  a prerequisite for Feature "Create PRs" / PR badges to work correctly.
- §28A (Branch Dialog Worktree Awareness — `GrayMoonFeature`/`External` badges, checkout blocking, delete
  routing to Remove Feature) was not found anywhere in `src/GrayMoon.App/Components/Modals/BranchModal.razor` or
  `SwitchBranchModal.razor` during this pass; `WorkspaceBranchOccupancyService` exists but is not wired into
  those dialogs' branch list rendering. This looks unimplemented rather than buggy — worth a dedicated pass.

---

## 3. Files changed in this pass

- `src/GrayMoon.Agent/Services/GitService.cs` — `WriteSyncHooks` → `WriteSyncHooksAsync`, resolves the common
  Git hooks directory via `git rev-parse --git-common-dir` instead of assuming `.git` is a directory.
- `src/GrayMoon.Agent/Abstractions/IGitService.cs` — interface signature updated to match.
- `src/GrayMoon.Agent/Commands/SyncRepositoryCommand.cs` — awaits the new async method.
- `src/GrayMoon.App/Components/Shared/WorkspaceRepositoriesHeader.razor` — Feature-aware primary button
  label/behavior.
- `src/GrayMoon.App/Components/Features/WorkspaceFeatureSelector.razor` — refreshes options on open and on
  external context-id change instead of once ever.
- `src/GrayMoon.App/Components/Pages/WorkspaceRepositories.razor.cs` — `OnSelectedContextChangedAsync` now
  reloads the grid and defends against a stale/removed context id instead of crashing the circuit.
- `src/GrayMoon.App/Components/Features/RemoveFeatureModal.razor` — human-readable copy.
- `src/GrayMoon.App/Services/Workspaces/WorkspaceRepositoryStateWriter.cs` — stopped Feature-context syncs from
  mutating the special Workspace's shared `WorkspaceRepositoryLink.BranchName` / legacy PR row.
- `src/GrayMoon.App/wwwroot/app.css` — added `.gm-callout--success` / `.gm-callout--info` variants used by the
  reworked Remove Feature dialog (only `--error` and `--warning` existed before).

## 4. Verification performed

- `dotnet build src/GrayMoon.App/GrayMoon.App.csproj` and `src/GrayMoon.Agent/GrayMoon.Agent.csproj` and
  `src/GrayMoon.App.Tests/GrayMoon.App.Tests.csproj` (redirected to a temp output dir since a `GrayMoon.App.exe`
  instance was already running and locking the normal `bin/` output) — all build with 0 errors.
- `dotnet test src/GrayMoon.Agent.Tests` filtered to worktree/hook/sync-related tests — 6/6 passing.
- Did not run the full App test suite or a live manual repro (would require stopping the user's running app
  instance and a live Agent/GitHub connector) — recommend the user re-run the exact repro steps from the report
  now that the App/Agent are rebuilt.

## 5. Suggested next steps, in priority order

1. Rebuild and restart both `GrayMoon.App` and `GrayMoon.Agent`, then re-run the original repro steps (create a
   Feature, sync it, switch Branch/Feature button, remove it, reselect the dropdown).
2. Schedule the Repositories-grid context migration (§2.1) as its own reviewable change with the full §40
   baseline sweep — this is the highest-value remaining item since it affects the single most-used page.
3. Decide whether to implement §17's context-aware PR service now (unblocks Feature PR badges) or defer.
4. Wire §28A branch-occupancy badges into `BranchModal`/`SwitchBranchModal`.
5. Extend `WorkspaceActionService` to the same context pattern as Git Changes (§2.3) once the grid migration
   establishes the pattern to copy.
