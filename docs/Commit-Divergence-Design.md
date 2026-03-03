# Commit Divergence – Design Document

## Overview

**Commit Divergence** is a new column in the workspace repositories grid that displays how many commits the current branch is **behind** and **ahead** of the **default branch** (e.g. `main`). Format: **behind | ahead** (e.g. `0 | 12`). The column is placed **after the Branch column** in `WorkspaceRepositories.razor`. Values are persisted and updated whenever the user syncs or when git events (commit, checkout, merge) are reported by the agent.

**Column name:** Use **"Divergence"**; for the shortest possible header use **"Div"**.

---

## 1. Data Semantics (vs default branch)

All counts are relative to the **default branch** (e.g. `origin/main` or `origin/master`), not the tracking branch.

- **Behind:** Commits on the default branch that are not in the current branch. (Current branch is behind default.)
- **Ahead:** Commits on the current branch that are not in the default branch. (Current branch is ahead of default.)

When the default branch cannot be determined, show `—` or `— | —`. The existing **Commits** column keeps its role (colored badge + push/pull vs tracking branch); this column is a compact, read-only display of divergence vs default branch.

---

## 2. How the Information Is Obtained

### 2.1 Agent (Git) side – vs default branch

The agent must compute **behind** and **ahead** against the **default branch** (e.g. `main`/`master`), not the tracking branch.

**Git commands (after fetch so `origin/<default>` exists):**

- Resolve default branch: existing `GetDefaultBranchAsync` (e.g. `origin/HEAD` → `origin/main`).
- **Ahead (current branch vs default):**  
  `git rev-list --count origin/<default>..HEAD`
- **Behind (current branch vs default):**  
  `git rev-list --count HEAD..origin/<default>`

**Options:**

- **A) Extend `GetCommitCountsAsync`** to also return default-branch counts (e.g. `defaultBehind`, `defaultAhead`), in addition to existing tracking-branch counts (for the Commits badge). One round-trip; sync/refresh/hooks all get both.
- **B) New method** `GetCommitCountsVsDefaultAsync` (or a dedicated command) that returns only default-branch behind/ahead. App calls it when building the divergence column (or in parallel with existing GetCommitCounts).

Recommendation: **A** – extend the existing response so sync, refresh, and every hook flow automatically get default-branch counts without extra calls.

**Response shape (add to existing commit-counts response):**

- `DefaultBranchBehind` (int?) – commits on default not in current branch.
- `DefaultBranchAhead` (int?) – commits on current branch not in default.

When default branch cannot be resolved or rev-list fails, return null for both.

### 2.2 Who uses the data

- **GetCommitCountsCommand** – extend to return default-branch counts.
- **SyncRepositoryCommand** – include default-branch counts in response.
- **RefreshRepositoryVersionCommand** – include default-branch counts.
- **Hook sync commands** – include default-branch counts in SyncCommand payload.

### 2.3 App side

- **Persistence:** New fields on `WorkspaceRepositoryLink`: e.g. `DefaultBranchBehindCommits`, `DefaultBranchAheadCommits` (or `DefaultBehind`, `DefaultAhead`). SyncCommandHandler and PersistVersionsAsync must persist these when present.
- **Commits badge:** Continues to use existing `OutgoingCommits` / `IncomingCommits` (vs tracking branch). No change to push/pull semantics.
- **Divergence column:** Reads the new default-branch fields only.

---

## 3. Persistence

- **Model:** `WorkspaceRepositoryLink`:
  - **Existing** (unchanged for Commits badge): `OutgoingCommits`, `IncomingCommits` (vs tracking branch), `BranchHasUpstream`.
  - **New:** `DefaultBranchBehindCommits` (int?), `DefaultBranchAheadCommits` (int?) – used only by the Divergence column (vs default branch).
- **When persisted:** Same as today for sync/refresh/push/hooks; in addition, persist the new default-branch fields whenever the agent includes them in the response or SyncCommand.
- **Migration:** Add two nullable int columns to `WorkspaceRepositories`.

---

## 4. UI Specification

### 4.1 Placement

- **Location:** New column **after Branch**, before Dependencies.
- **Header:** e.g. “Divergence” or “Behind | Ahead”.
- **colSpan:** When adding the column, increment the grid’s `colSpan` for loading/empty/filtered rows (e.g. from 6 to 7 when selection column is off, 7 to 8 when on).

### 4.2 Display Format

**Option A – Simple (recommended for first version)**  
- Format: `behind | ahead` (e.g. `0 | 12`, `2 | 0`).
- Pipe `|` in gray (e.g. `color: #6c757d` or Bootstrap `text-muted`).
- When unknown (no upstream and no counts): show `—` or `— | —`.

**Option B – GitHub-style**  
- Same semantics, with a small visual indicator:
  - “Behind” has a short line under the number when behind &gt; 0 (e.g. `2̣` or `2_` with underline).
  - “Ahead” has a short line under the number when ahead &gt; 0 (e.g. `1̣` or `_1`).
  - Example: `0 |___1` (0 behind, 1 ahead) or `1_| 0` (1 behind, 0 ahead).
- Implement with a small inline element (e.g. `span` with `border-bottom` or a tiny SVG line) so it looks like a minimal branch diagram.

**Empty / no branch:**  
- If `BranchName` is empty or repo not cloned, show `—` in the divergence cell.

**Data source:** Use `DefaultBranchBehindCommits` and `DefaultBranchAheadCommits` (not `IncomingCommits`/`OutgoingCommits`, which remain for the Commits badge).

### 4.3 Styling

- Use a compact, monospace or tabular-nums font for the numbers so digits align.
- Pipe and optional underlines should be subtle (gray); numbers can use default text color, with optional muted color when both are 0.

---

## 5. Implementation Outline

1. **WorkspaceRepositories.razor**
   - Add `<th class="col-divergence">` after Branch (e.g. “Divergence”).
   - Add `<td class="col-divergence">` in the data row after the Branch `<td>`, rendering a fragment that:
     - Takes `wr.DefaultBranchBehindCommits`, `wr.DefaultBranchAheadCommits`, and optionally `wr.BranchName`.
     - Renders “behind | ahead” (Option A) or GitHub-style (Option B).
     - Handles null/unknown and no-branch cases.
   - Increment `colSpan` everywhere it’s used (e.g. 6 → 7, 7 → 8).

2. **CSS**
   - Add `.col-divergence` for width/alignment if needed.
   - Style the pipe (gray) and, if Option B, the underline elements.

3. **Backend / agent**
   - **Agent:** Extend `GetCommitCountsAsync` (or response DTOs) to compute and return `DefaultBranchBehind` and `DefaultBranchAhead` using `origin/<default>..HEAD` and `HEAD..origin/<default>`. Ensure SyncRepository, RefreshRepositoryVersion, and all hook sync commands include these in their response or SyncCommand payload.
   - **App:** Add `DefaultBranchBehindCommits` and `DefaultBranchAheadCommits` to `WorkspaceRepositoryLink` and migration; persist in `PersistVersionsAsync` and `SyncCommandHandler` when present; divergence column reads these fields.

---

## 6. When the Column Updates

| Event | Updates divergence? |
|-------|----------------------|
| Full Sync / Sync level / Sync single repo | Yes (agent returns default-branch counts → persist) |
| Refresh version (e.g. after push) | Yes |
| Push (single or batch) | Yes (GetCommitCounts response includes default-branch counts) |
| Commit outside app (post-commit hook) | Yes (CommitHookSync → SyncCommand with default-branch fields) |
| Checkout outside app (post-checkout hook) | Yes (CheckoutHookSync → SyncCommand) |
| Merge outside app (post-merge hook) | Yes (MergeHookSync → SyncCommand) |
| Branch change in app (checkout/create) | Yes (WorkspaceSynced + hook will run and persist) |

So the column stays in sync with the repo as long as the agent is running and hooks are installed; no extra “polling” or manual refresh is required for commit/branch changes done outside the app.

---

## 7. Summary

- **What:** A **Divergence** column (header: "Divergence" or "Div") showing **behind | ahead** vs **default branch** after the Branch column.
- **Data source:** Agent computes via `rev-list --count` vs `origin/<default>`; extend GetCommitCounts (and sync/refresh/hook responses) to return `DefaultBranchBehind` and `DefaultBranchAhead`.
- **Persistence:** New fields `DefaultBranchBehindCommits`, `DefaultBranchAheadCommits` on `WorkspaceRepositoryLink`. Existing `OutgoingCommits`/`IncomingCommits` stay for the Commits badge (vs tracking branch).
- **Updates:** Same triggers as today (sync, push, hooks); agent includes default-branch counts so the column refreshes automatically.
- **UI:** Simple `0 | 12` with gray pipe; optional GitHub-style underlines later.
