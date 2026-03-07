# Workspace Root Path Analysis

**Goal:** Ensure the workspace root path used by commands comes from the **Workspaces table** (per-workspace `RootPath`), not from the global Settings value. Settings should only be the **default** when creating a new workspace in the "Add Workspace" dialog.

---

## Current State

### Where the root path is stored

| Location | Purpose |
|----------|---------|
| **AppSettings** (`AppSettingRepository.WorkspaceRootPathKey`) | Single global "workspace root path" in Settings. Persisted in DB (e.g. `AppSettings` table). |
| **Workspaces.RootPath** | Per-workspace root path. Column added by migration; existing rows may have `NULL`. |

### How it is used today

1. **WorkspaceService**
   - **`GetRootPathAsync()`** – Reads from Settings (with in-memory cache). Used as the "global" default.
   - **`GetRootPathForWorkspaceAsync(workspace)`** – Returns `workspace.RootPath` if set, otherwise falls back to `GetRootPathAsync()` (Settings). This is the **correct** source for command execution when a workspace is known.

2. **WorkspaceRepository**
   - **AddAsync** – Sets `workspace.RootPath = await _workspaceService.GetRootPathAsync()`. New workspaces get the **current Settings value** (intended as default).
   - **UpdateAsync** – Updates only `Name` and repository links. **Does not update `RootPath`.** So any edited "root path" in the UI is not persisted.

3. **Settings page** – Edits and saves the global Setting only. On save it calls `WorkspaceService.ClearCachedRootPath()` so the next `GetRootPathAsync()` re-reads from DB.

4. **AgentHub** – On agent connect: `RefreshRootPathAsync()` (refreshes cache from Settings). On disconnect: `ClearCachedRootPath()`. This only affects the **global** cache, not per-workspace `RootPath`.

---

## Problem

- **Commands** receive `WorkspaceRoot` from the app. The app is supposed to pass the root for the **specific workspace**. Most call sites use `GetRootPathForWorkspaceAsync(workspace)`, which is correct.
- **One bug:** In **BranchEndpoints.cs** (Sync-to-default flow), the code uses `GetRootPathAsync(CancellationToken.None)` instead of `GetRootPathForWorkspaceAsync(workspace, ...)`. So that endpoint uses the **Settings** root, not the workspace’s `RootPath`. If the workspace has a different root (or Settings was changed), the repo can’t be found.
- **Workspaces with `RootPath == null`** (e.g. created before the column existed, or never set) always fall back to Settings. If the user then changes the Settings path, those workspaces effectively "move" to the new path and existing repos are no longer found.
- **Edit workspace** – The Workspaces page has `editingWorkspaceRootPath` and uses it for validation and display, but **UpdateAsync does not persist RootPath**, so changes to root path in the edit modal are never saved.

---

## Call sites summary

| File | Method / usage | Current source | Should use |
|------|----------------|----------------|------------|
| **BranchEndpoints.cs** | Sync to default branch (~L259) | `GetRootPathAsync()` | `GetRootPathForWorkspaceAsync(workspace)` |
| BranchEndpoints.cs | Other branch ops | `GetRootPathForWorkspaceAsync(workspace)` | ✓ correct |
| WorkspaceEndpoints.cs | Workspace API | `GetRootPathForWorkspaceAsync(workspace)` | ✓ correct |
| WorkspaceGitService.cs | All agent commands | `GetRootPathForWorkspaceAsync(workspace)` | ✓ correct |
| CommitSyncEndpoints.cs | Commit sync | `GetRootPathForWorkspaceAsync(workspace)` | ✓ correct |
| WorkspaceFileSearchService.cs | File search | `GetRootPathForWorkspaceAsync(workspace)` | ✓ correct |
| WorkspaceFileVersionService.cs | File versions | `GetRootPathForWorkspaceAsync(workspace)` | ✓ correct |
| WorkspaceFiles.razor | ViewFileModal | `GetRootPathForWorkspaceAsync(workspace)` | ✓ correct |
| **WorkspaceRepository.cs** | AddAsync | `GetRootPathAsync()` → set `workspace.RootPath` | ✓ correct (Settings as default for new workspace) |
| **WorkspaceRepository.cs** | UpdateAsync | — | Should persist `RootPath` if we allow editing it |
| Workspaces.razor | Create modal default / validation | `GetRootPathAsync()`, `editingWorkspaceRootPath` | ✓ Settings as default for new; edit should persist RootPath |
| Settings.razor | Single global root path | AppSettingRepository | ✓ only default for new workspace |

---

## Effort: Use Workspaces Table as Source of Truth

### 1. Fix BranchEndpoints to use workspace root (required)

- **File:** `src/GrayMoon.App/Api/Endpoints/BranchEndpoints.cs`
- **Change:** In the Sync-to-default-branch endpoint, replace `GetRootPathAsync(CancellationToken.None)` with `GetRootPathForWorkspaceAsync(workspace, CancellationToken.None)` so the root comes from the workspace (Workspaces table) when set, else Settings.
- **Effort:** Small (single line change).

### 2. Backfill Workspaces.RootPath for existing rows (recommended)

- **Where:** Migration or startup in `Program.cs` (e.g. extend `MigrateWorkspaceRootPathAsync` or add a one-time data migration).
- **Logic:** For each `Workspace` where `RootPath` is null or empty, set `RootPath = (current value from AppSettingRepository.WorkspaceRootPathKey)`.
- **Effect:** Existing workspaces keep using the same path they effectively used before (Settings). After that, each workspace has its own stored path; changing Settings later only affects **new** workspaces (and any workspace that still falls back because RootPath is null).
- **Effort:** Small.

### 3. Persist RootPath when editing a workspace (optional but recommended)

- **WorkspaceRepository.UpdateAsync:** Add an optional parameter (e.g. `string? rootPath`) and set `workspace.RootPath = rootPath` when provided (or only when the caller passes a value). Normalize empty string to null.
- **Workspaces.razor SaveWorkspaceAsync:** When `editingWorkspaceId != null`, pass `editingWorkspaceRootPath` into `UpdateAsync` so the edited root path is saved.
- **UI:** If the Add/Edit workspace modal does not yet expose an editable "Root path" field, add one (prefilled from Settings for Add, from `workspace.RootPath` for Edit), bound to `editingWorkspaceRootPath`, so users can set/change the workspace root.
- **Effort:** Medium (repository + modal + optional UI for root path).

### 4. Clarify semantics in code and UI

- **Settings page:** Add a short note that this path is the **default root for new workspaces** only; each workspace can have its own root (and list where to edit it, e.g. "Edit workspace").
- **WorkspaceService / comments:** Document that `GetRootPathAsync()` is the global default (from Settings); `GetRootPathForWorkspaceAsync(workspace)` is the source of truth for commands and should be used whenever a workspace is known.
- **Effort:** Small.

### 5. No change to Agent or Settings storage

- Agent continues to receive `WorkspaceRoot` in each request; the app keeps populating it from `GetRootPathForWorkspaceAsync(workspace)`.
- Settings keep storing a single global default; no need to remove it. It is used only as default when creating a new workspace and as fallback when a workspace has no `RootPath` set.

---

## Summary

- **Minimal fix:** Use `GetRootPathForWorkspaceAsync(workspace)` in BranchEndpoints (Sync-to-default) so that endpoint uses the Workspaces table (with Settings fallback) like all others.
- **Robust fix:** Above + backfill `Workspaces.RootPath` for existing rows + persist `RootPath` in `UpdateAsync` and wire the edit modal so the workspace root is stored in the Workspaces table and Settings remain only the default for new workspaces.
