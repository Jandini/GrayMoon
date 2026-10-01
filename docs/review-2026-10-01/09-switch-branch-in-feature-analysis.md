# Switch Branch while a Feature is selected: viability analysis

Status: the owner chose option B on 2026-10-01. It is planned as lane I (units I1 to I4) in `06-worktree-release-implementation-plan.md`; section 8 shows the mapping.

Code references name symbols rather than line numbers, because line numbers drift.

## 1. Question and short answer

**Question:** when the user is viewing a Feature, the per-repository Switch Branch dialog (opened by clicking a repository's branch in the grid) works almost exactly as it does in the Workspace. Is that helpful, and is it safe?

**Short answer:**

- **Not safe as it is.** Checking out another branch or a tag, or creating a new branch, inside a Feature worktree moves that repository off the Feature branch. GrayMoon has no notion of that state:
  - The user **cannot get back**, because the dialog blocks checking out the Feature's own branch.
  - **Remove Feature then examines the wrong branch and deletes a different one.** With "delete even if unmerged" ticked, that can destroy commits the dialog never showed.
- **Partly helpful.** Seeing branches and tags, fetching, deleting stale branches and upgrading a tag-pinned repository are useful inside a Feature. Switching a Feature repository to an arbitrary branch, or creating a new one there, conflicts with the Feature model ("one Feature = one branch name in every repository").
- **Recommendation for v1:** restrict the dialog in Feature context to the safe, useful subset. Add a "Return to Feature branch" action. Make Remove judge the Feature branch, not whatever is checked out. Handle drift that happens outside GrayMoon (a terminal `git checkout`), because that cannot be prevented by the UI. Real multi-branch Features are a v1.1+ design question.

## 2. What the dialog offers in a Feature today

Entry points:
- **Branch badge click:** `WorkspaceRepositories.razor` `OnBranchClick` calls `ShowSwitchBranchModal`.
- **Upgrade badge:** `ShowSwitchBranchModalOnTagsTab`.

Both pass the selected context (`ViewingContextId`, `IsFeatureContext`) into `Components/Modals/SwitchBranchModal.razor`.

The only Feature-specific difference inside the dialog is that **Return to Default is hidden** (`ShouldShowReturnToDefaultButton` returns false when `IsFeatureContext`). Everything else is identical.

| Dialog action | Available in a Feature? | What Git does in the Feature worktree | Service-level Feature guard? |
|---|---|---|---|
| Locals / Remotes / Tags lists, filter | Yes | Read only | n/a |
| Fetch | Yes (`RefreshBranchesAsync` with the context) | Fetches into the shared repository | n/a |
| Check out another local branch | Yes, unless occupied | `checkout X` moves **this worktree** off the Feature branch | None (`WorkspaceBranchHandler.CheckoutBranchAsync` takes the context and does it) |
| Check out a remote branch | Yes | Creates a local tracking branch and moves the worktree to it | None |
| Check out a tag | Yes | Detaches the worktree at the tag | None |
| New branch tab (create + switch) | Yes | Creates branch Y from a base and moves the worktree to Y | None (`CreateSingleBranchAsync`) |
| Delete local / remote branch | Yes, except the current branch and occupied ones | Deletes a ref in the **shared** repository (the Workspace and every Feature see it) | None |
| Return to Default | Hidden in the UI | n/a | **None** in the service (review F-16, plan P2-10) |
| Occupancy badges | Yes | `WorkspaceBranchOccupancyService`: Current, Workspace, Feature, Worktree | n/a |

By contrast, the header **deliberately removes bulk branch actions** for a Feature. In `WorkspaceRepositoriesHeader.razor`, "New Branch", "Switch Branch", "Prepare Workspace" and "Return to Default" are inside `@if (!IsFeatureContext)`, and `HandleBranchPrimaryClick` has the comment "No Branch dialog for a Feature - open the Feature menu". **The per-repository dialog is the path that bypasses that design decision.** That inconsistency is the root of every problem below.

## 3. How GrayMoon identifies a Feature's branch

There is no stored per-repository branch name for a Feature. The branch **is** the Feature name:

- `WorkspaceFeatureRepository` stores `WorktreePath`, `BaseCommitSha`, `ParentBranchName`, `PinnedTag`, `State` and `LastError`. It does not store the branch.
- **Remove deletes the branch named after the Feature:** `WorkspaceFeatureOperations.RemoveFeatureCoreAsync` sets `var branchName = row.PinnedTag == null ? info.FeatureName : null;` and sends `DeleteBranch` with `force = options.AllowForceDeleteLocalBranches`.
- **Remove *analysis* describes the branch that is checked out now:** `AnalyzeRemoveFeatureAsync` uses `BranchName = state?.BranchName ?? ...`, with `OutgoingCommits` and `HasUpstream` from the same context state, which is the current checkout.
- **Occupancy treats the Feature name as owned by the Feature:** `WorkspaceBranchOccupancyService.GetBadgesForRepositoryAsync`, second loop: "Feature branches owned in DB but not currently listed still block Workspace checkout". It adds the Feature name with `AllowCheckout = false` and `RequiresFeatureCleanup = true`. **It does not exclude the Feature being viewed.**

The design docs never cover a Feature worktree that is on a branch other than the Feature branch: no "drift", "different branch" or manual-checkout case in `Detailed-Implementation-Design-v3.md`.

## 4. Safety findings

| # | Severity | Finding | Evidence | Consequence |
|---|---|---|---|---|
| SB-1 | **High** | **One click strands the user off the Feature branch.** After checking out `X` in a Feature repo, reopening the dialog shows `X` as Current. The Feature's own branch is no longer in `git worktree list`, so the occupancy service's second loop badges it "Feature" with `AllowCheckout = false`. `IsCheckoutDisabled` then blocks it, and its delete icon routes to **Remove Feature for the same Feature** (`RequestFeatureCleanup`). | `WorkspaceBranchOccupancyService` second loop; `SwitchBranchModal.IsCheckoutDisabled`, `RequestFeatureCleanup` | The user cannot get back from the dialog that moved them. The only offered action on the Feature branch is "remove the Feature". A terminal is the only way out. |
| SB-2 | **High** | **Remove examines one branch and deletes another.** After a switch, the analysis reports the repo as "on `X`, N outgoing commits", based on `X`. Removal then deletes the Feature-named branch, with `force` when the user ticked "delete local branches even if not merged" (a checkbox they ticked for `X`'s state). `X` itself is left behind silently. | `AnalyzeRemoveFeatureAsync` (`state?.BranchName`, `state?.OutgoingCommits`) vs `RemoveFeatureCoreAsync` (`info.FeatureName`, `force`) | **Possible loss of unmerged commits on the Feature branch** that were never shown, plus a stray branch `X` and a later "branch exists" surprise. This also affects plan units A3 and D3, which build on the analysis. |
| SB-3 | Medium | **New Branch inside a Feature creates a branch the Feature does not know about** and moves the worktree to it. Same outcome as SB-1 and SB-2. A user who expects "a sub-branch of my Feature" gets a repo that GrayMoon still labels as the Feature but that is on an unrelated branch. | `CreateSingleBranchAsync` with the Feature context | Confusing model, plus SB-1 and SB-2. |
| SB-4 | Medium | **A tag checkout in a non-pinned repo detaches the Feature worktree**, again stranding the Feature branch (SB-1). For a **tag-pinned** repo (`PinnedTag != null`), detached is the designed state and moving to another tag is legitimate. The dialog does not tell the two cases apart. | `CheckoutBranchAsync(isTag: true)`; `PinnedTag` semantics in `CreateFeatureCoreAsync` | Legitimate use (pinned upgrade) and harmful use (non-pinned) look identical. |
| SB-5 | Low/Med | **Deleting a branch from inside a Feature deletes it for the whole repository.** Refs are shared by the primary checkout and every worktree. The confirmation says "Delete branch 'x'?" and does not mention that the Workspace and other Features are affected. Git refuses only when the branch is checked out somewhere. | `RequestDeleteBranch` message; shared `.git` | Surprise, not corruption. It is the same as deleting from the Workspace view, but the user may think the Feature is isolated. |
| SB-6 | Medium | **Drift also happens outside GrayMoon.** A `git checkout` in a terminal inside the Feature folder causes SB-1 and SB-2 without the dialog. The `post-checkout` hook reports the new branch, so GrayMoon's context state follows it, but nothing marks the repo as "off its Feature branch". | Hooks (appendix `07` section 1); context state `BranchName` | Restricting the dialog is necessary but not sufficient. GrayMoon must detect and show drift. |
| SB-7 | Medium | **No service-level rule.** Nothing in `WorkspaceBranchHandler` or `WorkspaceBranchOperations` treats a Feature context differently for checkout or create. Even the hidden Return to Default is UI-only. The REST API (`/api/branches/checkout`, `/api/branches/create`) can do the same, although today it always targets the Workspace (05 R5). | `rg -n "IsFeature" Services/Application/WorkspaceBranchOperations.cs` returns nothing | Any future caller (API with `contextId`, MCP) repeats SB-1 to SB-3. |
| SB-8 | Low | **Opening from the upgrade badge skips occupancy.** `ShowSwitchBranchModalOnTagsTab` does not set `WorkspaceRepositoryId`, so `LoadOccupancyAsync` returns early (`WorkspaceRepositoryId <= 0`) and no badges are shown. This affects the Workspace as well. | `WorkspaceRepositories.Branches.cs` `ShowSwitchBranchModalOnTagsTab` | Missing "Feature" or "Workspace" badges on that path; Git still refuses invalid checkouts. |

**What is already safe:**
- **Occupied branches are blocked.** A branch held by the Workspace or another Feature is badged and can't be checked out; Git would refuse a double checkout anyway.
- **Return to Default is hidden** in Features.
- **The read-only actions are harmless:** Fetch, the branch and tag lists, and Update Branch from Default (a separate dialog).
- **Feature worktrees can't be removed by mistake:** the external-worktree cleanup only targets worktrees that are neither Feature nor Workspace (`BranchOccupancyKind.Worktree`).

## 5. Is it helpful? Use cases

| Use case in a Feature | Helpful? | Today | Safer way that fits the model |
|---|---|---|---|
| See which branches and tags exist; filter | Yes | Works | Keep |
| Fetch to see new remote branches | Yes | Works | Keep |
| Get back onto the Feature branch after drift (dialog or terminal) | **Yes, essential** | **Blocked (SB-1)** | Add "Return to Feature branch" |
| Upgrade a tag-pinned repo to a newer tag | Yes | Works, but is indistinguishable from harmful tag checkouts | Keep for pinned repos only, and update `PinnedTag` |
| Delete stale local or remote branches | Yes, occasionally | Works | Keep, with a "shared by Workspace and all Features" warning |
| Look at someone else's branch in one repo | Sometimes | Moves the Feature off its branch | Use the Workspace, or a new Feature. A read-only "compare" is a better v1.1 idea. |
| Stacked or sub-branches inside a Feature (`feat` -> `feat-part2`) | Sometimes, for advanced users | Creates SB-1 to SB-3 | Not in v1. Needs a model where a Feature can own more than one branch per repo (section 7, option C). |
| Park a repo that the Feature does not touch on the default branch | Plausible (all repos always get a worktree, DEC-5) | Impossible anyway: the Workspace already holds `main`, so Git refuses | Repo picker on create (plan P2-1) or detached at `origin/<default>`; v1.1 |

Conclusion: the useful parts are viewing, fetching, returning to the Feature branch, pinned-tag upgrades and branch cleanup. Arbitrary checkout and create are not useful enough in v1 to justify the risk.

## 6. Options

| Option | Description | Safety | Effort | Fits v1? |
|---|---|---|---|---|
| A. Keep as is | No change | SB-1 to SB-7 remain, including possible data loss (SB-2) | 0 | No |
| B. Feature-aware dialog (recommended) | In Feature context: keep lists, filter, Fetch and delete (with a shared-repo warning). **Hide** New Branch and **block** checkout of other branches. Allow tag checkout only for tag-pinned repos. Add "Return to Feature branch". Add the same rules in the service layer. Add drift detection and a fixed Remove analysis. | Closes SB-1 to SB-7 | M (3 to 5 days) | Yes |
| C. First-class multi-branch Features | Store the current branch per Feature repo, let a Feature own several branches, and make Remove, PR and occupancy handle all of them | Safe if done fully | L+ (design work) | No, v1.1+ |
| D. Hide the dialog entirely in Features | Branch badge not clickable | Safe | S | No: loses the useful parts and gives no way back after terminal drift |

## 7. Recommended v1 behaviour (option B), per dialog part, Feature context only

The Workspace dialog stays exactly as it is.

| Part | Behaviour in a Feature |
|---|---|
| Header line | "Feature `<name>` - this repository is on `<current>`". If current differs from the Feature branch, add a warning: "This repository is not on its Feature branch." |
| Locals tab | List as today. The Feature branch row is badged **Feature (this)** with a **Return to Feature branch** button when the worktree is not on it. Other branches have **no checkout** and show a tooltip: "A Feature keeps every repository on its Feature branch. To work on another branch, create another Feature or use the Workspace." |
| Remotes tab | List only; no checkout. |
| Tags tab | Checkout allowed only when the repo is tag-pinned in this Feature (`PinnedTag != null`), and it updates `PinnedTag`. Otherwise list only, with the same tooltip. |
| New Branch tab | Hidden. |
| Delete | Allowed as today, plus the line "Branches are shared by the Workspace and all Features of this repository." |
| Fetch | As today. |
| Return to Default | Hidden as today, plus a **service** guard (plan P2-10). |

Supporting changes:
1. **Occupancy fix.** In `GetBadgesForRepositoryAsync`, the viewing context's own Feature name gets a new badge kind (for example `FeatureOwn`) with `AllowCheckout = true`, `RequiresFeatureCleanup = false`, and no delete action.
2. **Service guard.** `WorkspaceBranchHandler` checkout and create reject, for a Feature context, any target other than the Feature branch or (for pinned repos) a tag. The error text matches the tooltip. This also protects future API and MCP callers (SB-7).
3. **Drift detection (SB-6).**
   - The grid marks a Feature repo whose `BranchName` is not the Feature name (and is not a pinned tag) with "Off Feature branch", with a one-click "Return to Feature branch".
   - The C2 reconciler leaves drifted repos alone. Drift is not breakage, so it never turns into NeedsRepair.
4. **Remove analysis (SB-2).**
   - For each repo, analyse the **Feature branch** (`refs/heads/<FeatureName>`): whether it exists, commits not on default, upstream and ahead. This uses A1 `InspectWorktree`, extended with an optional `branchName`.
   - If the worktree is on another branch, list it: "Also on branch `X` (kept)".
   - The "force delete unmerged" checkbox names the Feature branch and its unmerged count.
5. **Fix SB-8.** Pass `WorkspaceRepositoryId` from `ShowSwitchBranchModalOnTagsTab`.

## 8. Plan units (lane I in `06`)

| Unit in `06` | Title | Depends on | Size | Closes |
|---|---|---|---|---|
| I1 | Remove analysis judges the Feature branch, not the current checkout; lists drift branches as kept | A3, D3 | M | SB-2 (data loss). Gates GATE-2. |
| I2 | Own Feature branch is checkable; "Return to Feature branch"; upgrade-badge occupancy fix | GATE-1 | S | SB-1, SB-8 |
| I3 | Feature-aware Switch Branch dialog and service rules (section 7 table, steps 1 and 2) | I2 | M | SB-3, SB-4, SB-5, SB-7 |
| I4 | Drift badge in the grid; reconciler compares the path only (step 3) | I2, C2 | S | SB-6 |

The regression guards and manual checks below are written into the units and into GATE-2 step 6 and GATE-4 step 7. The risks are listed as R-I1 to R-I4 in `08`.

Regression guards to attach (same format as `06`):
- **Workspace unchanged.** Every change is behind `IsFeatureContext` or a Feature context id. Before editing, a characterization test pins today's Workspace dialog behaviour: checkout, create, delete, tags, and Return to Default visibility. WORKSPACE-SMOKE step 4 covers it manually.
- **Old Worker.** The optional `branchName` on `InspectWorktree` must work with A1's existing response. An old Worker means "unknown", which requires acknowledgement in Remove.
- **Tag-pinned repos.** Upgrading a pinned tag in a Feature keeps working and updates `PinnedTag`. A test covers it.

Suggested manual check to add to GATE-2 or GATE-4:
1. In Feature `sb-test`, open a repo's branch dialog. Other branches cannot be checked out (tooltip explains), New Branch is hidden.
2. In a terminal, `git checkout -b side` inside that repo's Feature folder. The grid shows "Off Feature branch". "Return to Feature branch" works.
3. Repeat step 2, commit on the Feature branch first (unmerged), then open Remove while on `side`. The dialog talks about the Feature branch and its unmerged commit, and lists `side` as kept.
4. Tag-pinned repo in a Feature: upgrade to a newer tag from the badge. It works and stays detached.
5. WORKSPACE-SMOKE step 4: Workspace branch dialog unchanged.

## 9. Questions for the owner

1. **Do you want sub-branches inside a Feature at all** (option C, later)? If yes, v1 should still block them, but the wording should say "not yet" rather than "use another Feature".
2. **Should drift be allowed to persist** (a warning only, as proposed), or should GrayMoon offer to return automatically on Sync? The proposal is a warning only, because automatic checkouts can collide with uncommitted work.
3. **Should SB-A be promoted to P0** in `06`? The recommendation is yes, because it is a silent data-loss path in Remove.
