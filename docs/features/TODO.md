# GrayMoon Feature Documentation Gap Analysis

Compared `docs/features/` (46 pages + 2 walkthroughs) against `src/GrayMoon.App`, `GrayMoon.Agent`, `GrayMoon.Desktop`, and design docs under `docs/`. Coverage is strong for the happy-path feature lifecycle (branch → update → synchronized push → PR → merge cleanup). The biggest holes are **team collaboration flows**, **modal-level detail**, **Desktop shell**, and **index completeness**.

---

## 1. Explicitly deferred in existing docs (highest priority)

These are called out in the docs themselves as not yet written:

| Gap | Evidence in docs | What to document |
|-----|------------------|------------------|
| **Pull / incoming commits walkthrough** | `repositories.md` Fetch section: *"Incoming will be simulated separately later"* | Red header **Pull**, red `↑N ↓M` commits badge, notification card **Pull**, overlay **Synchronizing commits...**, per-repo row errors after merge conflict |
| **Sync To Default after merged PRs (reason 2)** | `sync-to-default.md`: *"not demoed here"* | Workspace-wide cleanup when PRs are merged on GitHub: no red alert, no countdown, blue **Proceed**, delete feature branch refs, pull latest `main` |
| **Incoming + unmatched deps together** | `repositories.md` mentions red **Update** when both exist; no walkthrough | Notification card **Update** (not Push Updated), order-of-operations guidance (Pull first vs Update first), header button colors |

---

## 2. Missing feature pages (implemented in code, no dedicated doc)

### High value

| Proposed doc | Code / UX | Why it matters |
|--------------|-----------|----------------|
| **`workspace/pull.md`** | `WorkspaceCommitSyncHandler`, header **Pull**, commits badge click, notification card **Pull**, level **Sync commits** (`bi-arrow-down-up`) | Core team workflow; merge-conflict path exists in code (`MergeConflict` → row error banner) but is never shown |
| **`workspace/update-branch-from-default.md`** | `UpdateBranchModal.razor`, divergence **behind** click | Different from **Pull** (upstream incoming) vs rebasing feature branch onto moving **default** |
| **`workspace/create-pull-request.md`** | `NewPullRequestModal.razor` | Walkthrough phase-4 covers bulk create only; missing: draft checkbox, optional reviewers/teams search, PR template fallback, single-repo via `create` badge, level-header git icon |
| **`07-desktop.md` or `getting-started/04-desktop.md`** | `GrayMoon.Desktop` tray, WebView2 host, `DesktopNotificationService`, minimize-to-tray, system-menu top-bar toggle | Only one paragraph in `shared.md` / `00-layout.md`; Desktop is a first-class delivery path |
| **`workspace/level-actions.md`** | `WorkspaceRepositoriesLevelHeader.razor` | Share (copy open PR URLs), rewind (level Sync To Default), sync commits, open PR, sync level, repo-count GitHub dropdown with PR/pull map - all named in `repositories.md` but not walked through |

### Medium value (modal / sub-feature depth)

| Proposed doc | Code | Current coverage |
|--------------|------|------------------|
| **`workspace/custom-dependencies.md`** | `CustomDependenciesModal.razor` | Mentioned via deps badge click only; locked vs user-selected repos, effect on synchronized push wait list |
| **`workspace/version-files.md`** (extend `files.md` or split) | `VersionConfigModal`, `VersionFilesCommitModal`, `WorkspaceFileVersionService` | `files.md` is 43 lines; missing `@` autocomplete, unknown-token warnings, "not found in file", commit checkbox on update |
| **`workspace/add-files.md`** (section or page) | `AddFilesModal.razor` | Wildcard `*`/`?`, repo filter dropdown, 20-result cap, agent file search |
| **`workspace/push-with-dependencies.md`** | `PushWithDependenciesModal.razor` | Synchronized push dialog is documented; non-default unchecked push and per-repo push paths deserve a failure/recovery page |
| **`workspace/update-and-push.md`** | `WorkspaceRepositories.Update.cs` `OnUpdateAndPushClickAsync`, level-only variant | Menu item **Update & Push...** listed in `repositories.md` but never demonstrated (distinct from one-click **Push Updated**) |

### Lower value (error / edge UX)

| Proposed doc | Code | Notes |
|--------------|------|-------|
| **`workspace/errors-and-recovery.md`** | `OperationErrorModal`, per-row **Error:** banner, `repositoryErrors` map | No user-facing doc for what happens when sync/push/pull/update fails mid-job |
| **`workspace/git-conflicts.md`** | Commit sync merge abort, Changes `status:conflict` filter | Conflict resolution is always "go to IDE" - worth a short scenario |
| **`workspace/protected-branch-and-push-failure.md`** | Push errors, `undo-push-commits.md` motivation | NuGet timeout covered in library-update phase 3; git push rejected by branch protection is not |

---

## 3. Scenarios worth adding (from design / obsolete docs)

These explain *why* features exist but never appear as user scenarios in `docs/features/`:

| Scenario | Source hint | Suggested doc home |
|----------|-------------|-------------------|
| **Teammate pushed to your feature branch** (Fetch → red incoming → Pull) | `Push-Hook-Implementation.md`, deferred in `repositories.md` | `pull.md` |
| **Teammate moved `main` while you are on a feature branch** (divergence `2 \| 0`, behind click → Update Branch) | Divergence column design | `update-branch-from-default.md` |
| **Push outside GrayMoon** (IDE/CLI → pre-push hook → badge/card updates) | `05-agent.md` Live git tracking, `Push-Hook-Implementation.md` | Extend `shared.md` or short `outside-git.md` |
| **Existing folder on disk → Import Repositories** | `02-workspaces.md` Import modal | Getting-started add-on or workspaces section |
| **App in Docker, Agent on host** (architecture, ports 8384/9191) | `GrayMoon.Agent-Design.md`, getting-started | `getting-started/00-architecture.md` or README cross-link |
| **csproj deps vs version-file deps orchestration** | `design/dependency-update-orchestration.md` | User summary in `dependencies.md` or `files.md` ("two kinds of dependency update") |
| **PR badge mergeability tints** (conflicts, checks running, blocked) | `Pull-Request-Column-Design.md` | Extend `repositories.md` PR section with screenshots |
| **Connector token storage** (encrypted at rest) | `Token-Encryption-Design.md` | Optional security note in `04-connectors.md` |
| **Linux Agent** (no badge self-update, systemd) | `05-agent.md` footnote | Expand Agent page with Linux install path |

---

## 4. Index / navigation gaps (`features/README.md`)

`workspace/README.md` lists pages **not** in the main `features/README.md` table:

- `repository-branch-management.md`
- `tag-upgrade.md`
- `checkout-from-tags-to-main.md`
- `undo-push-commits.md`
- Both feature walkthroughs (`library-update`, `tape-density`)

Users starting from `features/README.md` will miss half the workspace catalog.

Also missing from any index as a **first-level product**:

- **GrayMoon Desktop** (separate repo, bundles App + local process manager)

---

## 5. Walkthrough coverage vs full product matrix

| Area | Documented? | Gap |
|------|-------------|-----|
| Clean install → clone | getting-started (4 steps) | Good |
| New branch / switch / new feature | Yes | Good |
| Update Only / Push Only / Push Updated | Yes (MezzoRecovery) | Good |
| Synchronized push + NuGet wait failure | library-update phase 3 | Good |
| Tag pin + upgrade + consumer deps flip | tag-upgrade, checkout-from-tags-to-main | Good |
| Create PRs bulk | library-update phase 4 | Reviewers/draft not shown |
| Merge PRs + Sync To Default per level | phase 5, sync-to-default | Merged-PR reason 2 not demoed |
| Undo unpushed commits | undo-push-commits | Good |
| Git Changes multi-repo commit | changes.md | Good |
| GHA run / logs / deploy | actions.md, tape-density | Good |
| **Pull incoming** | No | **Missing** |
| **Update branch from default** | No | **Missing** |
| **Pull + Update same workspace** | No | **Missing** |
| **Desktop tray workflow** | No | **Missing** |
| **Custom deps for push ordering** | No | **Missing** |
| **Version file configure + commit modal** | Shallow | **Needs depth** |

---

## 6. Suggested next documents (priority order)

1. **`workspace/pull.md`** - Simulate teammate push: Fetch → `↓N` → header **Pull** → success and merge-conflict failure (row error banner).
2. **`workspace/update-branch-from-default.md`** - Divergence behind click, merge into feature branch, no auto-push.
3. **`07-graymoon-desktop.md`** - Install, tray, close-to-tray, show/hide top bar, native notifications, bundled App startup.
4. **`workspace/create-pull-request.md`** - Standalone reference (eligibility, draft, reviewers, templates, single/bulk/level).
5. **`workspace/level-actions.md`** - All five level-header icons with screenshots.
6. **`sync-to-default.md` addendum** - Reason 2 merged-PR cleanup scenario.
7. **Extend `files.md`** - Version config editor + version-files commit modal + Add Files modal.
8. **`workspace/custom-dependencies.md`** - Push wait graph customization.
9. **Update `features/README.md`** - Sync with `workspace/README.md` + Desktop entry.

---

## 7. What is already well covered (no urgent gap)

- First-level pages: Home, Workspaces, Repositories catalog, Connectors, Agent, Settings, Layout, Shared chrome
- Workspace grids: Projects, Packages, Dependencies graph, Actions (including logs download/collapse)
- Branch workflows: new-branch, switch-branch, new-feature, sync-to-default (discard path), per-repo branch dialog
- Restore, tag-upgrade, undo-push-commits
- Two end-to-end walkthroughs (library-update deps-only, tape-density full lifecycle)
- Git Changes (stage/unstage/commit across repos, Monaco diff, default-branch commit warning)

---

## Summary

The documentation set reads like a **feature-branch shipping manual** (branch → deps → push → CI → PR → cleanup). It is thinner on **day-to-day collaboration** (pull, rebase onto default, conflicts, protected branches) and on **supporting modals** (custom deps, version files, PR creation details). **GrayMoon Desktop** is the largest product surface with almost no feature doc. Several pages already admit the next step (*incoming simulated later*, *merged PR sync not demoed*) - those are the clearest places to continue.
