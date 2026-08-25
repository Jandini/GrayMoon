# GrayMoon Feature Documentation Gap Analysis

Compared `docs/features/` against `src/GrayMoon.App`, `GrayMoon.Agent`, `GrayMoon.Desktop`, and design docs under `docs/`. Coverage is strong for the happy-path feature lifecycle (branch → update → synchronized push → PR → merge cleanup) and includes **Pull**, **Update branch from default**, and **Custom dependencies**. Remaining holes are **combined Pull + Update**, **version-file depth**, **Desktop shell**, and some **index completeness**.

---

## Recently completed

| Doc | Covers |
|-----|--------|
| [`workspace/update-branch-from-default.md`](workspace/update-branch-from-default.md) | Yellow divergence **behind** on a feature branch → **Update Branch** (fetch + merge `origin/<default>`); complementary second-workspace clone example (`/workspaces/9`) |
| [`workspace/incoming-commits.md`](workspace/incoming-commits.md) | Red commits badge `↑N ↓M` / header **Pull**; per-repo badge vs header Pull for all incoming; before / synchronizing / after screenshots on `/workspaces/9`; untracked-file abort error banner (merge-conflict abort behavior described) |
| [`workspace/custom-dependencies.md`](workspace/custom-dependencies.md) | MezzoRecovery grid with/without deps; TapeTools dialog with locked **`project`** checkboxes; Solution → Api custom edge bubbles Solution to Level 4 for last PR close; why custom edges exist |

Also linked from `features/README.md`, `workspace/README.md`, and related `repositories.md` sections.

**Come back when version files are documented:** extend `custom-dependencies.md` (and/or `files.md`) with a live **`file`** locked badge in the dialog - version-file token edges are already supported in code but not walked through yet.

---

## 1. Explicitly deferred in existing docs (highest priority)

These are still called out in the docs themselves as not yet written:

| Gap | Evidence in docs | What to document |
|-----|------------------|------------------|
| **Sync To Default after merged PRs (reason 2)** | `sync-to-default.md`: *"not demoed here"* | Workspace-wide cleanup when PRs are merged on GitHub: no red alert, no countdown, blue **Proceed**, delete feature branch refs, pull latest `main` |
| **Incoming + unmatched deps together** | `repositories.md` mentions red **Update** when both exist; no walkthrough | Notification card **Update** (not Push Updated), order-of-operations guidance (Pull first vs Update first), header button colors |

---

## 2. Missing feature pages (implemented in code, no dedicated doc)

### High value

| Proposed doc | Code / UX | Why it matters |
|--------------|-----------|----------------|
| **`workspace/create-pull-request.md`** | `NewPullRequestModal.razor` | Walkthrough phase-4 covers bulk create only; missing: draft checkbox, optional reviewers/teams search, PR template fallback, single-repo via `create` badge, level-header git icon |
| **`07-desktop.md` or `getting-started/04-desktop.md`** | `GrayMoon.Desktop` tray, WebView2 host, `DesktopNotificationService`, minimize-to-tray, system-menu top-bar toggle | Only one paragraph in `shared.md` / `00-layout.md`; Desktop is a first-class delivery path |
| **`workspace/level-actions.md`** | `WorkspaceRepositoriesLevelHeader.razor` | Share (copy open PR URLs), rewind (level Sync To Default), sync commits, open PR, sync level, repo-count GitHub dropdown with PR/pull map - all named in `repositories.md` but not walked through |

### Medium value (modal / sub-feature depth)

| Proposed doc | Code | Current coverage |
|--------------|------|------------------|
| **`workspace/version-files.md`** (extend `files.md` or split) | `VersionConfigModal`, `VersionFilesCommitModal`, `WorkspaceFileVersionService` | `files.md` is short; missing `@` autocomplete, unknown-token warnings, "not found in file", commit checkbox on update. **Also revisit [`custom-dependencies.md`](workspace/custom-dependencies.md)** for a **`file`** locked-badge screenshot once version-file edges exist in a demo workspace. |
| **`workspace/add-files.md`** (section or page) | `AddFilesModal.razor` | Wildcard `*`/`?`, repo filter dropdown, 20-result cap, agent file search |
| **`workspace/push-with-dependencies.md`** | `PushWithDependenciesModal.razor` | Synchronized push dialog is documented; non-default unchecked push and per-repo push paths deserve a failure/recovery page |
| **`workspace/update-and-push.md`** | `WorkspaceRepositories.Update.cs` `OnUpdateAndPushClickAsync`, level-only variant | Menu item **Update & Push...** listed in `repositories.md` but never demonstrated (distinct from one-click **Push Updated**) |

### Lower value (error / edge UX)

| Proposed doc | Code | Notes |
|--------------|------|-------|
| **`workspace/errors-and-recovery.md`** | `OperationErrorModal`, per-row **Error:** banner, `repositoryErrors` map | No user-facing doc for what happens when sync/push/pull/update fails mid-job |
| **`workspace/git-conflicts.md`** | Commit sync merge abort, Changes `status:conflict` filter, Update Branch leaves `MERGE_HEAD` | Pull untracked-file abort is in `incoming-commits.md`; still missing a dedicated merge-conflict screenshot and Changes `status:conflict` walkthrough vs Update Branch leaving `MERGE_HEAD` |
| **`workspace/protected-branch-and-push-failure.md`** | Push errors, `undo-push-commits.md` motivation | NuGet timeout covered in library-update phase 3; git push rejected by branch protection is not |

---

## 3. Scenarios worth adding (from design / obsolete docs)

These explain *why* features exist but never appear as user scenarios in `docs/features/`:

| Scenario | Source hint | Suggested doc home |
|----------|-------------|-------------------|
| **Push outside GrayMoon** (IDE/CLI → pre-push hook → badge/card updates) | `05-agent.md` Live git tracking, `Push-Hook-Implementation.md` | Extend `shared.md` or short `outside-git.md` |
| **Existing folder on disk → Import Repositories** | `02-workspaces.md` Import modal | Getting-started add-on or workspaces section |
| **App in Docker, Agent on host** (architecture, ports 8384/9191) | `GrayMoon.Agent-Design.md`, getting-started | `getting-started/00-architecture.md` or README cross-link |
| **csproj deps vs version-file deps orchestration** | `design/dependency-update-orchestration.md` | User summary in `dependencies.md` or `files.md` ("two kinds of dependency update") |
| **PR badge mergeability tints** (conflicts, checks running, blocked) | `Pull-Request-Column-Design.md` | Extend `repositories.md` PR section with screenshots |
| **Connector token storage** (encrypted at rest) | `Token-Encryption-Design.md` | Optional security note in `04-connectors.md` |
| **Linux Agent** (no badge self-update, systemd) | `05-agent.md` footnote | Expand Agent page with Linux install path |

Done (moved out of this list):

- Teammate pushed to your branch → [`incoming-commits.md`](workspace/incoming-commits.md)
- Teammate moved `main` while you are on a feature branch → [`update-branch-from-default.md`](workspace/update-branch-from-default.md)

---

## 4. Index / navigation gaps (`features/README.md`)

`workspace/README.md` still lists pages **not** in the main `features/README.md` table:

- `repository-branch-management.md`
- `tag-upgrade.md`
- `undo-push-commits.md`
- Both feature walkthroughs (`library-update`, `tape-density`)

Recently added to both indexes: `update-branch-from-default.md`, `incoming-commits.md`, `checkout-from-tags-to-main.md`, `custom-dependencies.md`.

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
| **Pull incoming** | [`incoming-commits.md`](workspace/incoming-commits.md) | Good (before/after + untracked abort; true merge-conflict screenshot still optional) |
| **Update branch from default** | [`update-branch-from-default.md`](workspace/update-branch-from-default.md) | Good |
| **Pull + Update same workspace** | No | **Missing** |
| **Desktop tray workflow** | No | **Missing** |
| **Custom deps for push ordering** | [`custom-dependencies.md`](workspace/custom-dependencies.md) | Good (project locks + Solution→Api bubble; **`file`** badge deferred until version-files doc) |
| **Version file configure + commit modal** | Shallow | **Needs depth** (then revisit custom-deps **`file`** badge) |

---

## 6. Suggested next documents (priority order)

1. **Incoming + unmatched deps** (extend `incoming-commits.md` or short addendum) - red header **Update**, Pull-first vs Update-first.
2. **`07-graymoon-desktop.md`** - Install, tray, close-to-tray, show/hide top bar, native notifications, bundled App startup.
3. **`workspace/create-pull-request.md`** - Standalone reference (eligibility, draft, reviewers, templates, single/bulk/level).
4. **`workspace/level-actions.md`** - All five level-header icons with screenshots.
5. **`sync-to-default.md` addendum** - Reason 2 merged-PR cleanup scenario.
6. **Extend `files.md` / version-files** - Version config editor + commit modal + Add Files; then add **`file`** locked-badge example to [`custom-dependencies.md`](workspace/custom-dependencies.md).
7. **Update `features/README.md`** - Add remaining workspace-only pages (`repository-branch-management`, `tag-upgrade`, `undo-push-commits`, walkthroughs) + Desktop entry.

---

## 7. What is already well covered (no urgent gap)

- First-level pages: Home, Workspaces, Repositories catalog, Connectors, Agent, Settings, Layout, Shared chrome
- Workspace grids: Projects, Packages, Dependencies graph, Actions (including logs download/collapse)
- Branch workflows: new-branch, switch-branch, new-feature, sync-to-default (discard path), per-repo branch dialog, **update-branch-from-default**, **incoming-commits (Pull)**, **custom-dependencies**
- Restore, tag-upgrade, undo-push-commits
- Two end-to-end walkthroughs (library-update deps-only, tape-density full lifecycle)
- Git Changes (stage/unstage/commit across repos, Monaco diff, default-branch commit warning)

---

## Summary

The documentation set still reads like a **feature-branch shipping manual** (branch → deps → push → CI → PR → cleanup), with **Pull**, **Update branch from default**, and **Custom dependencies** covered for day-to-day collaboration and level ordering. It remains thinner on **combined states** (incoming + unmatched deps), **version files** (revisit custom-deps **`file`** badge then), **PR creation details**, and **GrayMoon Desktop**. Clearest next steps: Pull+Update together, Desktop, create-PR reference, Sync To Default reason 2, then version files.
