## WorkspaceRepositories grid CSS fix plan

This plan addresses the regressions after moving the Workspace Repositories grid into `WorkspaceRepositoriesLevelHeader` and `WorkspaceRepositoriesRow` components. All changes should preserve the previous look from the old `/workspaces/1` view, and will be implemented **incrementally** so you can verify after each step.

---

### Step 1 – Restore dependency level header layout and background

- **Goal**: Make the dependency level header row look exactly like before:
  - Dark background (black / near-black).
  - Single-line layout: left side `Level X [graph icon]`, right side `[icons] X repositories` on the same row.
- **Files**:
  - `WorkspaceRepositories.razor.css`
  - `WorkspaceRepositoriesLevelHeader.razor`
- **Changes**:
  1. In `WorkspaceRepositories.razor.css`, keep using `::deep` but **verify** and, if needed, re‑introduce these original rules:
     - Header cell container: `::deep .repositories-table .dependency-level-header td.dependency-level-header-cell { ... }`
       - Ensure `display: flex; align-items: center; justify-content: space-between; gap: 1rem; background-color: #0d0d0d;`.
     - Left side group: `::deep .repositories-table .dependency-level-header .dependency-level-header-start { display: flex; align-items: center; }`.
     - Right side group: `::deep .repositories-table .dependency-level-header .dependency-level-header-end { display: flex; align-items: center; gap: 0.75rem; }`.
  2. Confirm `WorkspaceRepositoriesLevelHeader.razor` still renders:
     - A single `<td class="dependency-level-header-cell">` that wraps both `.dependency-level-header-start` and `.dependency-level-header-end`.
  3. **Verification step**: Reload `/workspaces/46` and check that:
     - Header background is dark again.
     - The header is a single row with left and right segments horizontally aligned (no wrapping to two lines).

---

### Step 2 – Fix dependency level header icon colors

- **Goal**: Use the same icon colors as before (dedicated gray tones, not global text variables).
- **Files**:
  - `WorkspaceRepositories.razor.css`
- **Changes**:
  1. In `WorkspaceRepositories.razor.css`, ensure these rules exist with `::deep` and use the old colors:
     - Default: `color: #505050 !important;`
     - Hover: `color: #707070 !important;`
     - Targets:
       - `::deep .repositories-table .dependency-level-header .dependency-level-pr-link, .dependency-level-pr-link i`
       - `::deep .repositories-table .dependency-level-header .dependency-level-sync-to-default-link, .dependency-level-sync-to-default-link i`
       - `::deep .repositories-table .dependency-level-header .dependency-level-sync-commits-link, .dependency-level-sync-commits-link i`
       - `::deep .repositories-table .dependency-level-header .dependency-level-sync-link, .dependency-level-sync-link i`
       - `::deep .repositories-table .dependency-level-header .dependency-level-graph-link, .dependency-level-graph-link i`
  2. Remove any **remaining** global overrides for these selectors from `app.css` (if any survived) so the scoped+`::deep` rules win.
  3. **Verification step**:
     - Hover each of the four icons in the level header and confirm:
       - Default = dark gray.
       - Hover = lighter gray.

---

### Step 3 – Restore dependency column badge (background + text color)

- **Goal**: Make the dependency badge cells look like before:
  - Solid background color per state (none/ok/mismatch).
  - White (or light) text, not black on transparent.
- **Files**:
  - `WorkspaceRepositories.razor.css`
  - `WorkspaceRepositoriesRow.razor`
- **Changes**:
  1. In `WorkspaceRepositories.razor.css`, confirm the following rules exist and are prefixed with `::deep`:
     - `::deep .repositories-table .build-dep-badge { font-weight: 400; }`
     - `::deep .repositories-table .build-dep-badge-none { background-color: var(--bs-secondary); color: var(--bs-light); }`
     - `::deep .repositories-table .build-dep-badge-ok { background-color: #198754; color: #fff; }`
     - `::deep .repositories-table .build-dep-badge-mismatch { background-color: #dc3545; color: #fff; }`
  2. Verify `WorkspaceRepositoriesRow.razor` is still using:
     - `<span class="badge build-dep-badge build-dep-badge-none">`
     - `<span class="badge build-dep-badge build-dep-badge-ok">`
     - `<span class="badge build-dep-badge build-dep-badge-mismatch">`
  3. Remove any dependency‑badge styling for `.repositories-table` from `app.css` (we already removed most, but re‑check to avoid double definitions).
  4. **Verification step**:
     - Inspect each dependency badge state and confirm the filled backgrounds and text colors are back.

---

### Step 4 – Restore ellipsis behavior for Repository / Version / Branch columns

- **Goal**:
  - `Repository`, `Version`, and `Branch` **never wrap**; they truncate with `...` when too long.
  - Other columns **do not use ellipsis** and should not wrap/overlap unexpectedly.
- **Files**:
  - `WorkspaceRepositories.razor.css`
  - `WorkspaceRepositoriesRow.razor`
- **Changes**:
  1. In `WorkspaceRepositories.razor.css`, enforce ellipsis **only** on the correct pieces:
     - `::deep .repositories-table td.col-repo .repo-link,`
       `::deep .repositories-table td.col-repo strong,`
       `::deep .repositories-table .version-inner,`
       `::deep .repositories-table .branch-inner {`
       - `display: inline-block;`
       - `max-width: 100%;`
       - `overflow: hidden;`
       - `text-overflow: ellipsis;`
       - `white-space: nowrap;`
       `}`
  2. Ensure that **other columns** (divergence, PR, deps, commits, sync) do **not** carry `white-space: nowrap` + `text-overflow: ellipsis` from a generic `.repositories-table td` rule; adjust or remove any such generic rule if present.
  3. **Verification step**:
     - Use a very long repository name / version / branch and confirm:
       - They stay on one line and end with `...`.
       - Other columns remain readable and don’t introduce unwanted ellipsis.

---

### Step 5 – Restore hover “frame” (pill) for Version and Branch

- **Goal**: When hovering Version and Branch:
  - Show the pill‑like frame and background, identical to Divergence/Commits badges behavior.
- **Files**:
  - `WorkspaceRepositories.razor.css`
- **Changes**:
  1. Ensure the original pill styling is active with `::deep`:
     - Base pill: `::deep .repositories-table .version-inner, .branch-inner { display: inline-flex; align-items: center; height: calc(1.5rem + 4px); line-height: calc(1.5rem + 4px); padding: 0 0.5rem; border-radius: 0.25rem; border: 1px solid transparent; }`
     - Version/branch containers: `::deep .repositories-table .version-container`, `::deep .repositories-table .branch-container { display: inline-flex; align-items: center; height: calc(1.5rem + 4px); }`
     - Hover effects:
       - `::deep .repositories-table .version-container:hover .version-inner { ... }`
       - `::deep .repositories-table .branch-container:hover .branch-inner { ... }`
       - `::deep .repositories-table .version-inner:hover`, `::deep .repositories-table .branch-inner:hover { ... }`
  2. Confirm no conflicting global styles in `app.css` are overriding these (we removed the duplicated block; just re‑verify).
  3. **Verification step**:
     - Hover over Version and Branch cells and compare to Divergence/Commits: pill height, border, and background behavior should match the old view.

---

### Step 6 – Fix Repository link hover color (underline should match text color)

- **Goal**:
  - Repository names remain white (or current text color).
  - On hover, only the underline appears, and it uses the **same color** as the text (not blue).
- **Files**:
  - `WorkspaceRepositories.razor.css`
- **Changes**:
  1. Ensure the original repo-link rules are active with `::deep`:
     - `::deep .repositories-table td .repo-link, ::deep .repositories-table td a.repo-link {`
       - `text-decoration: none !important;`
       - `color: inherit !important;`
       `}`
     - `::deep .repositories-table td .repo-link:hover, ::deep .repositories-table td a.repo-link:hover {`
       - `text-decoration: underline !important;`
       `}`
  2. Confirm there are no anchor color overrides in `app.css` that reintroduce the default blue link color for these cells.
  3. **Verification step**:
     - Hover over a repository name and confirm:
       - Text color stays the same.
       - Underline appears in the same color, not blue.

---

### Implementation order (for step‑by‑step verification)

1. **Step 1 + Step 2** together: restore dependency level header layout + icon colors; you verify the header row only.
2. **Step 3**: restore dependency badges (deps column) and verify backgrounds/text.
3. **Step 4 + Step 5**: fix ellipsis and hover pills for Repository/Version/Branch.
4. **Step 6**: finalize Repository link hover behavior.

After each numbered step above, we will pause and you can refresh `/workspaces/46` to visually compare against the old `/workspaces/1` view before moving on.

