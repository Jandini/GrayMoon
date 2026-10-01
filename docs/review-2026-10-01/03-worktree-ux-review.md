# Worktree Features — UX Review (2026-10-01)

Method: walkthrough from the Razor components and the services they call, cross-checked against real leftovers on this machine (DB, `.graymoon\…\features\` folders, remote branches). The app was not driven in a browser for this review, so visual details such as spacing and colours are out of scope. Every point below cites the markup or code that produces it.

Severity: **High** (user loses work, gets stuck, or is misled about what happened) · **Medium** (confusing; there is a workaround) · **Low** (polish).

Code-level detail for the referenced findings (F-n) is in `02-worktree-code-and-approach-review.md`.

---

## 1. Flow: discover and create

**What the user sees.** The Workspace page has a context selector button (`WorkspaceFeatureSelector.razor:8-32`) with a "+" next to it (33-44). The dropdown has a "Workspace" section and a "Features" section. "+" opens **New Feature _worktree_** (`CreateFeatureModal.razor:16`). The modal has a name field labelled "Feature name _branch_" (25), a read-only "Based on: Current Workspace `<branch>`" (33-41), and the line "The Feature starts from the current committed state of `<branch>`. Uncommitted changes in Workspace are not copied and remain in Workspace." (43-51). Pressing Enter creates the Feature (150-153). While it runs, the modal hides and the page overlay shows "Created feature in N of M repos".

**Good**
- The explanation of committed vs uncommitted state is clear and accurate. It sets the right expectation for worktrees.
- Enter-to-submit, autofocus, and live progress counts work.
- Name collisions are caught before anything is created, and the error lists which repos already have the branch (`WorkspaceFeatureOperations.cs:120-130`).

**Problems**

| # | Severity | Issue | Evidence |
|---|---|---|---|
| U-1 | High | **A failed create leaves a Feature behind, but the modal looks like nothing happened.** The modal reappears with "One or more worktrees failed to create." Pressing Create again with the same name returns "A Feature named 'x' already exists." The Feature it refers to sits in the selector as a disabled row "(NeedsRepair)". No per-repo reason is shown, and there is no Retry or Roll back. | `WorkspaceFeatureOperations.cs:265-271`, 92-93; `CreateFeatureModal.razor:123-127`; selector 86-90 |
| U-2 | High | **Stuck Features are invisible.** If the app restarts or Desktop closes while a Feature is being created or removed, it stays `Creating`/`Removing`. The selector doesn't list those states, so the user can't see or remove the Feature, but its name is still taken. | Selector `:370-371`; F-3 |
| U-3 | Medium | **No validation while typing.** Names with spaces, `..`, `@` and similar are rejected only after clicking, with "Feature name is not a valid Git branch name.", which doesn't say which character is the problem. Names Windows cannot use as folders (`CON`, `a<b`) or that are unsafe in a shell (`a&b`) are accepted. `Foo` and `foo` are treated as different names but collide on disk. | `:35`, `:48-50`; F-7 |
| U-4 | Medium | **No preview of the cost.** The modal doesn't show which repositories get a worktree, where on disk they go (`%USERPROFILE%\.graymoon\<ws>\features\<name>\`), or that tag-pinned repos stay detached. Every Workspace repository always gets a worktree. | `CreateFeatureCoreAsync` 101-105, 169-181 |
| U-5 | Medium | "Based on" only shows a branch when every repo is on the same one. With mixed branches it says just "Current Workspace", and the per-repo bases (`ParentBranchName`) captured at create time are never shown. | `CreateFeatureModal.razor:37-40`, `WorkspaceBranchName` doc comment 72 |
| U-6 | Low | The selector has no typing filter and no "type a new name, press Enter to create" behaviour (design §22.2). With many Features the list is long and unsearchable. | Selector markup 47-105 |
| U-7 | Low | Inconsistent terms: "New Feature _worktree_", "Feature name _branch_", "Context" (aria-label), and the header's "Feature" button. A first-time user is shown three concepts for one thing. | `CreateFeatureModal.razor:16,25`; selector 13 |

## 2. Flow: switch and open

**What the user sees.** Picking a Feature navigates with `?context=<id>`. The grid, PR badges, Actions and Git Changes then show that Feature's worktrees. With Desktop installed, an **Open in…** flyout offers Cursor, Claude CLI, VS Code, Visual Studio, Terminal and Explorer for the Feature root, plus a per-repo submenu (`WorkspaceFeatureSelector.razor:106-…`).

**Good**
- Switching is instant (no checkout) and safe. Uncommitted work in the primary Workspace is untouched. This is the key worktree benefit, and it comes through.
- A deep link to a stale context falls back to Workspace with a toast instead of crashing. This fixes item 5 of the 09-21 review (`WorkspaceRepositories.razor.cs:89-108`).
- "Open in…" for the whole Feature root is a real productivity win for agent-driven work (Cursor/Claude).

**Problems**

| # | Severity | Issue | Evidence |
|---|---|---|---|
| U-8 | Medium | NeedsRepair Features **cannot be opened** (the row is disabled), so the user can't inspect which repos are broken before deciding to remove. | Selector 83-84 `disabled="@(!CanSelect(option))"` |
| U-9 | Medium | The raw enum is shown: "(NeedsRepair)", and "(Creating)"/"(Removing)" if they were ever listed. There's no tooltip with `LastError`. | Selector 86-90 |
| U-10 | Low | The Claude CLI fallback (without Windows Terminal) fails for paths containing `&`, so the terminal doesn't open and no error is shown. | `WebMessageBridgeService.cs:507`; F-11 |
| U-11 | Low | Switch Branch in the **Workspace** context is occupancy-aware: branches are badged "Feature" or "Worktree", and stray external worktrees can be cleaned up. Bulk **Branch → switch all** is not occupancy-aware, so it fails part-way for repos whose branch is held by a Feature. | `SwitchBranchModal.razor` vs `BranchModal.razor:278`; F-8 |

## 3. Flow: view changes, sync, PR

**Good**
- Git Changes, Sync, Update Dependencies, Push and PR creation are scoped to the selected Feature. After a Feature PR merges, Feature Sync writes default-branch versions so higher-level repos pick up released packages (`WorkspaceGitService.Sync.cs:304-381`). That is the multi-repo value proposition, and it works as designed.
- The header primary button adapts to the state: Create PR → Remove → Feature/Branch (`WorkspaceRepositoriesHeader.razor:60`).

**Problems**

| # | Severity | Issue | Evidence |
|---|---|---|---|
| U-12 | Medium | **"Remove" is the primary button of a brand-new Feature.** The rule is "Feature context, nothing to PR and no open PR". A Feature that was just created has no commits, so its biggest button is a destructive one. | `WorkspaceRepositoriesHeader.razor:457-462` |
| U-13 | Medium | Dependency tooltips, the Dependencies page and Restore can show or act on data from other Features, including deleted ones (orphan projects). The user may see version mismatches that don't belong to the current context. | F-2 |
| U-14 | Low | Feature and Workspace operations on the same repo can run at the same time by design. Concurrent fetches may produce occasional `*.lock` retries or slowdowns that the user can't explain. | F-14 |

## 4. Flow: remove

**What the user sees.** The user removes a Feature with the "−" on its selector row, the header "Remove" button, or the Feature menu. The **Remove Feature** modal first shows "Checking what will happen…". It then shows a coloured headline, a section titled "This will remove the following from your computer:" with one plain-language line per repo (for example "Has uncommitted changes · Pull request #12 is still open"), and two opt-in checkboxes when the removal is not automatically safe (`RemoveFeatureModal.razor:20-57`).

**Good**
- The per-repo plain-language descriptions are a big improvement over the earlier raw fields.
- Uncommitted changes are checked live, not from cached data.
- Discarding files and force-deleting the branch are separate choices.
- When any worktree fails to delete, the modal stays open and shows the error (no false success).

**Problems**

| # | Severity | Issue | Evidence |
|---|---|---|---|
| U-15 | **High** | **When the App runs in Docker (the documented install), every repo shows "Folder is already missing on disk", even though it isn't.** The only way forward is ticking "permanently discard any uncommitted changes". That forces deletion of real uncommitted work the dialog never mentioned. | `WorkspaceFeatureOperations.cs:340`, 667-668; modal 115-118, 45-50; F-1 |
| U-16 | High | **"Feature removed." is not the whole story.** The remote branch is never deleted, and the dialog has no option for it. A failed local-branch delete (for example, unpushed commits without the force box) is swallowed. Leftover files from Windows file locks stay in `.graymoon\…\features\<name>\`. On this machine: about 18 leftover folders, up to 78 MB each, and 5 orphaned remote branches. A later attempt to reuse the name fails with "Branch 'x' already exists…", which the user can't connect to the earlier removal. | `:536-561`; modal 218-219; F-5, F-6 |
| U-17 | Medium | **Either checkbox enables Remove.** Ticking only "delete the local branch even if…" when the actual problem is uncommitted files enables the button, but the remove then fails with a raw Git error. The checkboxes aren't tied to the conditions that apply. | Modal 62-63 |
| U-18 | Medium | **Optimistic headline.** "This Feature is finished (pull request merged, or never opened), so it's safe to remove." also appears for a Feature with pushed commits and no PR. Commit and PR facts come from the last Sync, not a live check. | Modal 91-92; `:361-365`; F-9 |
| U-19 | Low | "This will remove the following from your computer" lists repositories but doesn't say that local **branches** are deleted, or that remote branches are kept. | Modal 29 |
| U-20 | Low | The "−" remove icon sits right next to every Feature row in the dropdown, which makes it easy to hit by accident. The confirm dialog protects the user, but for a "safe" Feature the delete is only one click further. | Selector 92-103 |

## 5. Errors and recovery (cross-cutting)

- There is **no in-product recovery**: no Repair, Retry, Prune or "Clean up leftovers". `git worktree prune`/`repair` exist and are cheap to expose (F-3/F-4/F-5).
- Errors are generic at the Feature level ("One or more worktrees failed to create.") while specific errors sit unused in `WorkspaceFeatureRepository.LastError`.
- Long paths: worktrees sit 25–40 characters deeper than the primary checkout, and `core.longpaths` is never set. Deep trees can fail with raw Git messages (F-12).
- A locked worktree (`git worktree lock`) fails removal with a raw Git message (F-12).

## 6. Recommended UX changes (ordered)

1. Fix F-1 so the Remove dialog tells the truth in Docker. Until then, block force-remove when the existence check couldn't reach the Agent (U-15).
2. Add a **Feature status panel** for NeedsRepair/stuck Features: per-repo state and `LastError`, with **Retry**, **Roll back** and **Remove** buttons. List all lifecycle states in the selector with friendly labels: "Setting up…", "Needs attention", "Removing…" (U-1, U-2, U-8, U-9).
3. In Remove: show the checkboxes only for the conditions that apply. Add "Also delete remote branches (N)", which is off by default and only deletes if the remote tip matches. Show a final report: "Removed 6 worktrees · 6 local branches · 2 folders could not be fully deleted (files in use) [Open folder] [Retry cleanup]" (U-16–U-19).
4. Validate names live, using Git's rules plus Windows folder rules, and show the reason for rejection (U-3).
5. Show a create preview: list of repos (with the option to deselect repos in a later version), target folder, and per-repo base branch (U-4, U-5).
6. Make "Create PR" or "Open in…" the primary header action for a fresh Feature, and keep "Remove" in the menu (U-12).
7. Add type-ahead filtering and Enter-to-create to the selector (U-6), and use one term consistently: "Feature" (U-7).
