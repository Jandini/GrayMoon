# GrayMoon Worktree Features — Post-Implementation UX Gaps Analysis (2026-09-21)

**Purpose:** Explain the user-reported gaps observed after the v3 design and the
`GrayMoon-Worktree-Features-Dependency-And-Wiring-Gaps-2026-09-21.md` fix pass.
This document is **analysis only** — no implementation.

**Reviewed against:**
- `GrayMoon-Worktree-Features-Detailed-Implementation-Design-v3.md`
- `GrayMoon-Worktree-Features-Dependency-And-Wiring-Gaps-2026-09-21.md`
- Live code on branch `worktree` (HEAD `b3ebfd6`)

**Observations covered (user-reported):**

| # | Observation |
|---|---|
| O1 | Create Feature “Based on” dropdown not implemented |
| O2 | Prefer hierarchical “based on” tree in `WorkspaceFeatureSelector` |
| O3 | After New Feature, dependency level headers missing until Sync/Fetch (must not “just call Sync”) |
| O4 | `SwitchBranchModal` stale after Feature create; `main` Current → Worktree; Fetch shows Feature as Current |
| O5 | Level-by-level merge + Return to Default broken (`main is already used by worktree`) |
| O6 | Delete branch → `Delete failed: 400` |
| O7 | External worktree cleanup: `Could not inspect the external worktree` |
| O8 | Remove Feature does not remove branches; cleanup after PR merge? |
| O9 | Want Feature-scoped “Discard Feature” like Workspace Return to Default / Abort |

---

## 0. Executive verdict

Most of these are **not** random UI bugs. They are the same underlying cut:

1. **Feature create seeds incomplete context projections** (branch/version/projects yes; dependency levels no).
2. **Branch dialogs still mix Workspace-shared persistence with context-aware Git paths.**
3. **Remove Feature removes worktrees + DB rows but does not delete local/remote Feature branches** (design §27 requires branch delete).
4. **Return to Default is product-defined as Workspace-only (§9.1) but the UI threads the selected Feature context into it**, so checkout of `main` inside a Feature worktree collides with the Workspace checkout. Separately, the old ladder’s **real value** was default-branch **GitVersion strings** in csproj refs — that needs a Feature-era “versions from default” path, not only A vs B checkout policy (see §5 / §11.1).
5. **“Based on” / hierarchical Features are schema-reserved but first-release intentionally incomplete** (§25.1); selector tree deferred (§11.4).

Recommended fix order (when RUN is authorized) is in §10.

---

## 1. O1 — “Based on” dropdown in Create Feature is a stub

### What you see

`CreateFeatureModal.razor` shows a disabled `<select>` with a single option “Current Workspace”.

### Why

By design for first release (§25.1): only `Based on: Current Workspace` is implemented.

Code matches that:

- UI never binds a selection.
- `CreateFeatureAsync` always passes `WorkspaceFeatureBaseKindApplication.CurrentWorkspace`.
- Application enum has **only** `CurrentWorkspace = 0`.
- Model enum already reserves `DefaultBranches` and `Feature`, and `WorkspaceFeature.BaseWorkspaceFeatureId` exists — unused by create.

“Current Workspace” means **special Workspace HEAD commits per repo**, not dirty files (§25.3). That part of create is implemented (`GetHeadCommitsAsync` → `BaseCommitSha` → `CreateGitWorktree`).

### Your assumption (common branches, ignore tags)

Correct product rule, and the Workspace already has the data path:

- `GetUnifiedWorkspaceCurrentBranch` / `GetCommonBranchesAsync` already **exclude tag-pinned repos**.
- Bulk `BranchModal` already loads common local/remote branch names the same way.

Wiring “Based on = pick a common branch” is **not** the same as Current Workspace:

| Base choice | Meaning | Create work today |
|---|---|---|
| Current Workspace | Snap HEAD of whatever each Workspace checkout is on | Implemented |
| Common branch name (e.g. `main`) | Create Feature branch from that ref in every non-tag repo | **Not implemented** — would need resolve-ref + collision preflight (§25.2) per repo |
| Another Feature | Stack Feature on Feature (`BaseKind=Feature`) | Schema only |

So: dropdown stays a stub for now (**decided §11.2: Current Workspace only**). Each non-current option would be a **backend base-kind**, not just UI, when revisited later.

---

## 2. O2 — Hierarchical Features in `WorkspaceFeatureSelector`

### Proposed UX (paraphrased)

```text
Workspace
  {workspace name}
--------------------
Features
   main                    ← section / base branch (darker, same font as "Features")
      feature-a
          feature-c        ← based on feature-a
      feature-b
   other-workspace-branch
      feature-d
      feature-e
--------------------
Open in ... >
```

### Is it easy with current design?

**Partially easy for nesting Feature→Feature; not easy for nesting under base branch names.**

| Piece | Current support | Effort |
|---|---|---|
| Flat Workspace / Features / Open in… sections | Already in `WorkspaceFeatureSelector.razor` | Done |
| Nest Feature under parent Feature | `BaseWorkspaceFeatureId` on model; create never sets it; `WorkspaceFeatureContextInfo` does **not** expose parent/base | Medium — extend list DTO + create API + CSS indentation |
| Group Features under **base branch labels** (`main`, `donald-duck`, …) | **Not stored.** Create records `BaseKind=CurrentWorkspace` and per-repo `BaseCommitSha`, not “based on branch X” | Harder — need a persisted base-branch snapshot (or recompute from SHAs) |
| Show Workspace’s *current* branch as the only group | Misleading once Workspace moves; Features stay based on old HEADs | Product decision required |

**Decision (§11.4):** no selector tree for now — flat list stays. Hierarchy deferred until higher-priority gaps are closed.

---

## 3. O3 — Missing dependency level headers after New Feature

### What you see

New Feature selected → Repositories grid lacks “Level N” headers until Sync or Fetch.

### Root cause (not “forgot to call Sync”)

`SeedInitialFeatureProjectionsAsync` clones special-Workspace context state into the new Feature context, but **omits dependency fields**:

Copied today: `GitVersion`, `Projects`, `RepositoryType`, commit counts, sync/file-config fields, `BranchName` = Feature name.

**Not copied:** `DependencyLevel`, `Dependencies`, `UnmatchedDeps`.

Grid level headers / sort / grouping for Feature contexts read those fields from `WorkspaceRepositoryContextState` (gap-doc item 5). Null levels → no level headers.

Sync/Fetch “fixes” it because they run the dependency recompute path that **writes** `DependencyLevel` into context state — which is why calling Sync feels like the workaround, but it is the wrong product fix.

### Correct solution direction

At Feature create (still inside create-feature structural job), after seeding projects/deps graph clones:

1. **Copy** `DependencyLevel` / `Dependencies` / `UnmatchedDeps` from special Workspace context state when cloning (fast, preserves Workspace’s last-known topology), **and/or**
2. Run the **same in-process** `RecomputeAndPersistRepositoryDependencyStatsAsync(contextId, …)` already fixed in the gaps doc — against the Feature’s cloned `WorkspaceProjects` / `ProjectDependencies`, without a full Agent Sync/Fetch.

Prefer (1)+(2): copy for immediate UI correctness, then recompute so Feature graph mismatches vs Workspace HEAD divergence are honest.

Also set `IsInSync` / header expectations consistently so the UI does not look “empty levels” while versions already appear.

---

## 4. O4 — Switch Branch dialog stale; Current → Worktree; Fetch fixes it

### What you see

1. After Feature create, open Switch Branch: `main` (or prior Workspace branch) shows as **Current**.
2. A few seconds later that badge becomes **Worktree**.
3. Click **Fetch**: Feature branch appears as **Current**.

### Root cause (three cooperating bugs)

**A. `GetBranchesAsync` is not context-aware.**

```text
GetBranchesAsync(workspaceId, repositoryId)     ← no contextId
```

It reads:

- `RepositoryBranches` (shared persistence), and
- `WorkspaceRepositoryLink.BranchName` / `CheckedOutTag` (special Workspace link)

as “current”.

It does **not** read `WorkspaceRepositoryContextState.BranchName` for the viewing Feature, and it does **not** probe the Feature worktree.

So right after create, the dialog’s “current” is still the **Workspace** checkout.

**B. Occupancy badges load asynchronously from live `git worktree list`.**

`WorkspaceBranchOccupancyService` classifies relative to `viewingContextId`’s path:

- Feature worktree path = “current” checkout for the Feature branch → Feature branch should be **Current**.
- Workspace path still holds `main` / `donald-duck` → that branch is **OccupiedElsewhere**.
- If that path is **not** a GrayMoon Feature row → badge **Worktree** (external).

So once occupancy returns, Workspace’s branch flips from the fake “Current” (from A) to “Worktree”. That matches your observation.

**C. Fetch calls `RefreshBranchesAsync(…, ViewingContextId, …)`** which **is** context-aware (resolves Feature workspace root via `GetAgentWorkspaceArgsAsync`). Agent probes the Feature worktree → Feature branch is truly current → UI updates.

Create Feature does create the local Feature branch in each worktree, but nothing updates `RepositoryBranches` or the Switch Branch open-state until Fetch/Refresh. Create also does not notify an already-open Switch Branch modal.

### Correct solution direction

1. Make initial Switch Branch load context-aware: either pass `contextId` into get-branches and prefer context state + optional light probe, or always use Refresh’s path without requiring a remote fetch when only local lists are needed.
2. After Feature create / context switch, invalidate branch lists and occupancy (event or forced reload if modal open).
3. Never show link.`BranchName` as Current while `ViewingContextId` is a Feature.

---

## 5. O5 — Level-by-level merge workflow vs worktrees

### Your historical Workspace workflow (pre-Features)

1. Prepare Workspace on `my-branch`
2. Develop multi-level
3. Deepest = libraries on work branch
4. Open PRs everywhere
5. Merge deepest first
6. **Return to Default for that level only** → pull default → get published versions for higher levels
7. Repeat up to level 1

That workflow assumes **one checkout per repo** that can freely move onto `main`.

### What Features change

Git rule: a branch can be checked out in **only one worktree**.

After Feature create:

- Workspace path: still on `donald-duck` (or whatever)
- Feature path: on `feature-name`

If Return to Default runs against a **Feature** context path, Agent tries `checkout main` **inside the Feature worktree**. Git responds:

```text
fatal: 'main' is already used by worktree at 'C:/Workspace/Mezzo/MezzoRecovery'
```

Design §9.1 explicitly lists **Return to Default as Workspace-only**. Implementation currently passes `RequireSelectedContextId()` into Analyze/Execute Return to Default — so when the Feature is selected, the operation targets Feature paths. That is the collision you hit.

Repos already on `main` in Workspace (e.g. `TapeImage` in your paste) skip or succeed; others fail with the worktree error.

### Constraints (honest product options)

| Approach | Checkout / Return to Default | Gets **main** GitVersion into higher csprojs? |
|---|---|---|
| Return to Default while Feature selected | No — git occupancy error | No |
| **B.** Switch to Workspace, Return to Default per level | Workspace path can sit on `main` | Only for **Workspace** context. Feature worktrees still report `0.1.0-my-feature.N` on Sync, so Feature Update Dependencies still writes feature-branch versions unless something else supplies default versions |
| **A.** Stay in Feature; Sync/Fetch + Update Dependencies | No need to checkout `main` in Feature | **Not by itself.** Sync of a Feature checkout runs GitVersion on the **feature branch**, so consumers still see feature version strings |
| Discard Feature after all merges, then Workspace ladder | Cleanup path | Too late for mid-ladder updates |

### The real goal (why Return to Default felt essential)

Same commit can be:

```text
0.1.0-my-feature.82   ← last commit on Feature before merge
0.1.0                 ← or 0.1.0-main.77 on default after merge
```

Developers reading a csproj do not care that the SHA matches. They care that the **reference string looks like a post-merge default version**, not a feature branch. Old GM was handy because Return to Default made the deep repo’s checkout **be** default, so Sync’s GitVersion **was** the default string, then Update Dependencies copied that into higher levels.

**Neither A nor B restores that by checkout alone** while a Feature worktree still holds the feature branch.

### Proposal — separate “versions from default” from “checkout default”

Treat mid-Feature version ladder as **GitVersion at `origin/<default>`** (see §11.1 for the exact CLI). Return to Default is **not** used per level inside a Feature; when csproj hygiene works that way, cleanup is **Remove Feature for the whole Feature** when done.

```text
After deep PRs are merged on GitHub
  → Fetch (network, once)
  → For those repos: dotnet-gitversion /c $(git rev-parse origin/<default>) /nofetch  (local)
  → Use those strings as package versions for Update Dependencies on higher Feature levels
```

Working directory may be the Feature worktree (shared git objects). No checkout of `main` in the Feature.

---

## 6. O6 — `Delete failed: 400` in Switch Branch

### What the UI does

On non-success HTTP outcomes, `SwitchBranchModal` shows:

```text
Delete failed: {outcome.StatusCode}
```

and logs `ErrorText` — so **400 hides the real reason** from the user.

### Likely 400 sources in `DeleteBranchAsync`

`WorkspaceBranchOperations.DeleteBranchAsync` returns `BadRequest` when:

1. Missing ids / branch name, or
2. **Local delete of the branch that equals `WorkspaceRepositoryLink.BranchName`**  
   (“Cannot delete the current branch. Check out another branch first.”)

Important: that guard uses the **shared link** branch, not the viewing context’s branch. So:

- Viewing a Feature while Workspace link still says `donald-duck` → deleting `donald-duck` can 400 even if Feature worktree is on another branch.
- Deleting a Feature-occupied branch should be routed to Remove Feature (`RequiresFeatureCleanup`); if the ordinary delete path is still hit, Agent may fail differently (usually 200 + `Success=false`, not 400).

Feature/Worktree occupied branches should already block ordinary delete via occupancy (`AllowOrdinaryDelete = false`). A 400 usually means the guard above, or a BadRequest from resolution — **not** a mysterious HTTP layer bug.

### Correct solution direction

1. Surface `outcome.ErrorText` in the modal (not only status code).
2. Make “cannot delete current” use **context** current branch.
3. Ensure Feature/Worktree badges always take the cleanup path (already intended for §28A/§28B).

---

## 7. O7 — “Could not inspect the external worktree” / remove that worktree

### What the control means

On a **Worktree** occupancy badge, the delete icon title is:

> Checked out in an external worktree at {path} — remove that worktree to delete this branch

Clicking it runs `AnalyzeExternalWorktreeCleanupAsync`. On exception, UI sets:

```text
Could not inspect the external worktree.
```

(Your “mainWorktree” label is almost certainly the **branch name + Worktree badge** reading adjacent to that error.)

### Why this fires after Feature create

When viewing a **Feature**, the Workspace checkout of `main` / `donald-duck` is classified as **OccupiedElsewhere** and, because that path is **not** a Feature-owned row, as **Worktree** (external).

So GM offers “remove external worktree” for what is actually the **primary Workspace checkout**. Analyze/remove is not designed to treat the special Workspace root as a disposable external worktree:

- Feature-owned paths are refused (“Use Remove Feature instead”).
- Primary Workspace path as “external” is a **misclassification** for cleanup UX.
- Exceptions or list/path mismatches become the opaque inspect error.

### Correct solution direction

1. Occupancy: if path equals special Workspace repository path, badge as **Workspace** (or Current-elsewhere), **not** external Worktree cleanup.
2. Only offer §28B cleanup for true non-GM worktrees.
3. Prefer showing analyze `Error` text always (already done on plan failure); log the exception message into that Error for supportability.

---

## 8. O8 — Remove Feature does not remove branches; post-merge cleanup?

### What Remove Feature does today

`RemoveFeatureCoreAsync`:

1. `git worktree remove` each Feature worktree (force if options allow)
2. Switch selection back to special Workspace if needed
3. Delete DB: Feature repository rows, context, feature

It does **not**:

- `git branch -d/-D` the Feature branch
- delete remote Feature branch
- close PRs

Design §27.4 / §27.8 require after worktree removal:

```text
delete local Feature branch (-d / -D per policy)
optionally delete remote
then delete projections
```

`RemoveFeatureOptions` already has `AllowForceDeleteLocalBranches` and `DeleteRemoteBranches`, but the core path never calls DeleteBranch / push-delete.

### After PR merge — does GM auto-cleanup?

**No.** There is no background “Feature completed → auto remove” job.

Intended human path (design §27):

1. PRs merge → classification **Completed**
2. User runs Remove Feature (safe when no outgoing commits / merged)
3. Remove should then delete worktrees **and** local (optional remote) branches

Until branch delete is implemented, Remove Feature leaves **orphan local branches** (and remotes if pushed). That matches your observation.

---

## 9. O9 — “Discard Feature” like Workspace Return to Default / Abort

### What you want

Workspace “Return to Default” (full) feels like **Abort**: abandon work branch, land on default, clean local branch.

Want a Feature analogue: **Discard Feature** — abandon the Feature completely.

### Mapping to design

Design already names this:

- §27.6 No completed PR / abort — explicit destructive confirmation
- §41 test list includes “abort Feature”
- Remove Feature classifications: Active / Abandoned vs Completed

So **Discard Feature ≈ Remove Feature with abort/destructive options**, not a second subsystem — if Remove Feature is completed to §27 (worktree + branch + optional remote + clear selection).

Suggested product naming:

| User intent | Suggested command | Options |
|---|---|---|
| Merged / done | Remove Feature (Completed) | Prefer safe local delete; optional remote |
| Give up | **Discard Feature** | Force local branch delete, discard dirty, optional remote/PR close |
| Workspace abort | Return to Default (Workspace only) | Unchanged |

**Decided:** one dialog (Remove Feature), with options/copy that cover both “done / merged” and “discard / abort” — not two separate menu items. See §11.

---

## 10. Recommended fix sequence (when RUN is authorized)

Ordered by user pain and dependency. Reflects product decisions in §11.

| Priority | Item | Nature |
|---|---|---|
| P0 | Seed `DependencyLevel`/`Dependencies`/`UnmatchedDeps` (+ optional in-process recompute) on Feature create | O3 |
| P0 | Context-aware Switch Branch initial load + occupancy: don’t label Workspace path as external Worktree | O4, O7 |
| P0 | Guard Return to Default while Feature selected (Workspace-only; Feature cleanup = Remove Feature at end) | O5 / §11.1 |
| P0 | **Versions from default:** `git fetch` + `dotnet-gitversion /c $(git rev-parse origin/<default>) /nofetch` → feed Update Dependencies | O5 / §11.1 |
| P1 | Complete Remove Feature §27.8 branch delete; one dialog covers Completed + Discard/abort options | O8, O9 |
| P1 | Show real delete errors; context-aware “current branch” delete guard | O6 |
| — | Based on beyond Current Workspace | **Deferred** (stay Current Workspace only) |
| — | Selector hierarchy / “based on” tree | **Deferred** (too many other changes) |

---

## 11. Product decisions

### 1. Merge ladder + default versions — **direction set**

#### Two different problems

| Problem | What you need | Old tool |
|---|---|---|
| **Checkout** | Put a repo on `main` in *some* worktree | Return to Default |
| **Version strings in csproj** | Higher levels reference **default-branch** GitVersion (e.g. `0.1.0`), not `0.1.0-my-feature.82` | Same Return to Default — because Sync then ran GitVersion **on main** |

With Features, checkout of `main` in the Feature worktree is often impossible (Workspace already holds it). So Return to Default no longer delivers the **version** goal by itself.

Same commit, different label:

```text
Feature tip before merge:  0.1.0-my-feature.82
Default after merge:       0.1.0  (or 0.1.0-main.77)
```

#### How to call GitVersion for `origin/{default}` (proposed)

GM today (Agent `GitService.GetVersionAsync`) always runs against **current HEAD**:

```text
dotnet-gitversion /output json /nofetch /verbosity quiet
```

(or `dotnet gitversion …` when a tool manifest is present). `/nofetch` means GitVersion itself does **not** talk to the network; Sync/Fetch already ran `git fetch` first.

**Proposed default-tip call** (still in the Feature worktree path — shared `.git`, no checkout of `main`):

```text
1. Resolve default branch name (already known to GM / git)
2. git fetch origin          ← needs network (or use already-fetched refs)
3. sha=$(git rev-parse origin/<default>)   ← local
4. dotnet-gitversion /output json /nofetch /verbosity quiet /c <sha>
```

| Step | Local or network? |
|---|---|
| `git fetch` | **Network** (unless `origin/<default>` is already up to date from a recent Fetch/Sync) |
| `git rev-parse origin/<default>` | **Local** |
| `dotnet-gitversion … /c <sha> /nofetch` | **Local** (reads local git history only) |

Notes for implementation:

- `/c <commit>` is the supported “version this commit” switch; do **not** use `/b` here — that flag is for remote `/url` clones, not for a local worktree.
- Keep `/nofetch` so GitVersion does not do its own fetch (matches existing GM policy).
- May need `/nonormalize` in some edge cases (Agent already has that path for hooks); validate against real Mainline/GitHubFlow configs used by Mezzo-style repos.
- Spike required: confirm InformationalVersion for a merge commit on `origin/main` matches what you get after a real checkout of `main` (branch detection must classify the commit as mainline, not as the Feature branch still checked out).

Working directory can be the **Feature worktree** or the Workspace path — same object database. No second checkout of `main` required.

#### Product decision (user)

If **csproj hygiene via versions-from-default works**, then:

- Mid-ladder **per-level Return to Default is not required** for the Feature workflow.
- **Return to Default / cleanup is at entire Feature scope** — i.e. when the Feature is finished (Remove Feature / discard), not level-by-level inside the Feature.
- Day-to-day ladder inside a Feature:

```text
Merge deep PRs
  → Fetch
  → GitVersion at origin/<default> for those repos
  → Update Dependencies on higher levels (Feature context)
  → repeat up the levels
  → when done: Remove Feature (whole Feature)
```

Checkout policy: treat Return to Default as **Workspace-only / Feature-complete**, not a mid-Feature level tool. (Aligns with design §9.1 + this version capability.)

**Still to validate in a spike:** `/c origin-default-sha` produces the same InformationalVersion as checking out `main` on a representative repo.

---

### 2. Based on v1 — **decided: Current Workspace only**

Keep the disabled “Based on: Current Workspace” stub. No common-branch picker and no Feature-parent base in this pass (§25.1 first-release scope).

---

### 3. Discard vs Remove — **decided: one dialog**

Single Remove Feature dialog. Options / wording cover both safe completion (merged) and destructive discard (abort), sharing `RemoveFeatureAsync`. No separate “Discard Feature” menu item for now.

---

### 4. Selector tree — **decided: not now**

Do not build the hierarchical Features / “based on” tree in `WorkspaceFeatureSelector`. Too many higher-priority gaps; flat list stays.

---


## 12. Relationship to prior gap doc

The dependency-wiring gap doc (items 1–6, 8–9) made Feature grids **able** to show levels and context state. This analysis explains why **create-time seeding and branch UX** still leave Features looking broken until Sync/Fetch, and why Workspace-era Return to Default collides with worktree occupancy.

---

## 13. RUN status (2026-09-22)

Implemented on branch `worktree` (this pass):

| Item | Status |
|---|---|
| O3 Seed DependencyLevel (+ recompute after create) | Done |
| O4 Context-aware GetBranches + Feature branch in list | Done |
| O7 Workspace occupancy (not external Worktree cleanup) | Done |
| O5 Return to Default blocked for Feature (service + UI) | Done |
| O6 Delete error text + context current-branch guard | Done |
| O8 Remove Feature deletes local (+ optional remote) branches | Done |
| Versions-from-default (`GetGitVersionAtDefaultTip` + Sync applies for merged Feature PRs) | Done (Agent + Sync hook) |
| Feature header yellow Create PR | Done (earlier) |
| Recent workspaces on Switch Workspace icon | Done (earlier) |
| Based on / selector tree | Deferred per §11 |

**Spike still recommended:** confirm `/c origin-default-sha` InformationalVersion matches a real `main` checkout on your GitVersion.yml configs.

No code was changed for this document section beyond this status note.
