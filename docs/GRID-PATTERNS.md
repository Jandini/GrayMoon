# Grid Patterns: Static Grid vs Scrollable Grid

This document describes two table/grid patterns used in GrayMoon and how to implement them. Column names and content are not specified—they vary by page.

**Reference implementations:**
- **Scrollable grid:** `src/GrayMoon.App/Components/Pages/Repositories.razor` (and `Repositories.razor.css`)
- **Static grid:** `src/GrayMoon.App/Components/Pages/Workspaces.razor` (and `Workspaces.razor.css`)

---

## 1. Static Grid

Use when the table has few rows and the whole page can scroll. The table grows with content; no fixed height or internal scroll.

### 1.1 Markup structure (Razor)

```html
<div class="container-fluid page-container">
    <div class="d-flex justify-content-between align-items-center mb-4">
        <!-- Page title and optional toolbar (buttons, search, etc.) -->
    </div>

    @if (errorMessage != null)
    {
        <div class="alert alert-danger">...</div>
    }
    else
    {
        <div class="card page-card">
            <div class="card-body p-0">
                <div class="table-responsive">
                    <table class="table table-striped table-hover mb-0 YOUR-TABLE-CLASS">
                        <thead class="table-dark">
                            <tr>
                                <th class="col-...">...</th>
                                <!-- more <th> as needed -->
                            </tr>
                        </thead>
                        <tbody>
                            <!-- rows -->
                        </tbody>
                    </table>
                </div>
            </div>
        </div>
    }
</div>
```

### 1.2 CSS (page-specific `.razor.css`)

- No layout/height constraints required for the grid itself.
- Define column widths with classes (e.g. `.col-name`, `.col-actions`) and optional table cell styling:

```css
.YOUR-TABLE-CLASS th,
.YOUR-TABLE-CLASS td {
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}

.col-name { width: 50%; }
.col-actions { width: 35%; }
/* etc. */
```

### 1.3 Bootstrap usage

- **`container-fluid`** – full-width container.
- **`card`** / **`card-body`** – card wrapper; **`p-0`** removes card body padding so the table is edge-to-edge.
- **`table-responsive`** – Bootstrap wrapper for horizontal scroll on small screens only; no vertical behavior.
- **`table table-striped table-hover`** – standard Bootstrap table styling.

### 1.4 Behavior

- Page scrolls normally if content exceeds viewport.
- Table grows with number of rows.
- No scrollbar on the table itself.

---

## 2. Scrollable Grid

Use when the table can have many rows and you want:
- The **page** to not scroll (fixed viewport height).
- A **fixed header row** and only the **data rows** to scroll, with the vertical scrollbar only beside the body.

This pattern depends on **layout constraints** (MainLayout + page container) and a **split header/body** structure.

### 2.1 Layout prerequisites (already in GrayMoon)

The scrollable grid only works if the app layout constrains height and passes flex down to the page. In this project that is done in:

**`MainLayout.razor.css`:**
- `.page`: `height: 100vh`, `overflow: hidden`, flex column.
- `main`: `flex: 1`, `min-height: 0`, `overflow: hidden`, flex column.
- `.top-row`: `flex-shrink: 0`.
- `article.content`: `flex: 1 1 0`, `min-height: 0`, `overflow: hidden`, flex column.

So the page content (your grid page) is inside a flex chain that has a fixed height and does not overflow the viewport.

### 2.2 Markup structure (Razor)

- **Do not** use Bootstrap’s **`border`** class on the grid wrapper (it forces a light border). Use **`rounded`** only if you want rounded corners; border is set in CSS.
- Split into:
  1. A **header block** – one table with only `<thead>` (no scroll).
  2. A **body block** – a scrollable wrapper containing a second table with only `<tbody>`.

Use **two tables** so the scrollbar is only on the body. Both tables must share the same column width classes and `table-layout: fixed` so columns align.

```html
<div class="container-fluid page-container">
    <div class="page-header d-flex justify-content-between align-items-center mb-4">
        <!-- Page title and toolbar; must have class page-header -->
    </div>

    @if (errorMessage != null)
    {
        <div class="alert alert-danger">...</div>
    }
    else
    {
        <div class="card page-card">
            <div class="card-body p-0">
                <div class="table-responsive">
                    <div class="YOUR-GRID-WRAPPER rounded">
                        <!-- Header: no scroll -->
                        <div class="YOUR-GRID-WRAPPER-header">
                            <table class="table table-dark mb-0 YOUR-TABLE-CLASS YOUR-TABLE-CLASS-header">
                                <thead>
                                    <tr>
                                        <th class="col-...">...</th>
                                    </tr>
                                </thead>
                            </table>
                        </div>
                        <!-- Body: scrollable -->
                        <div class="YOUR-GRID-WRAPPER-body">
                            <table class="table table-striped table-hover mb-0 YOUR-TABLE-CLASS">
                                <tbody>
                                    <!-- rows only -->
                                </tbody>
                            </table>
                        </div>
                    </div>
                </div>
            </div>
        </div>
    }
</div>
```

Replace `YOUR-GRID-WRAPPER` and `YOUR-TABLE-CLASS` with your own names (e.g. `repository-list` / `repositories-table`).

### 2.3 CSS (page-specific `.razor.css`)

Use the following pattern. Names are placeholders; replace with your wrapper and table class names.

#### 2.3.1 Page container and fixed header

- Page fills the layout and does not create a page-level scrollbar.
- Header and alert must not shrink.

```css
.page-container {
    padding: var(--bs-gutter-x);
    padding-bottom: 1.5rem;
    display: flex;
    flex-direction: column;
    flex: 1 1 0;
    min-height: 0;
    overflow: hidden;
    box-sizing: border-box;
}

.page-container .page-header,
.page-container .alert {
    flex-shrink: 0;
}
```

#### 2.3.2 Card and table-responsive (flex chain)

- Card and inner divs participate in the flex chain so height is constrained and nothing overflows.

```css
.page-card {
    flex: 1 1 0;
    min-height: 0;
    max-height: 100%;
    overflow: hidden;
    display: flex;
    flex-direction: column;
}

.page-card .card-body {
    display: flex;
    flex-direction: column;
    flex: 1 1 0;
    min-height: 0;
    max-height: 100%;
    overflow: hidden;
    padding: 0;
}

.page-card .table-responsive {
    display: flex;
    flex-direction: column;
    flex: 1 1 0;
    min-height: 0;
    max-height: 100%;
    overflow: hidden;
}
```

#### 2.3.3 Grid wrapper (header + body)

- Wrapper is a flex column; only the body section scrolls.
- Border and radius are set here (do not rely on Bootstrap `border`).
- The header block has a **header/body divider**: a light line (`border-bottom: 1px solid #c0c0c0`) so the fixed header is visually separated from the scrollable body, matching the static grid’s header/body separation.

```css
.YOUR-GRID-WRAPPER {
    display: flex;
    flex-direction: column;
    flex: 1 1 0;
    min-height: 0;
    max-height: 100%;
    overflow: hidden;
    border: 1px solid #252526;
    border-radius: 6px;
}

.YOUR-GRID-WRAPPER-header {
    flex-shrink: 0;
    overflow: hidden;
    border-bottom: 1px solid #c0c0c0;
}

.YOUR-GRID-WRAPPER-body {
    flex: 1 1 0;
    min-height: 0;
    overflow-y: auto;
    overflow-x: hidden;
    scrollbar-color: #4e4e52 #2d2d30;
    scrollbar-width: thin;
}

.YOUR-GRID-WRAPPER-body::-webkit-scrollbar {
    width: 8px;
}

.YOUR-GRID-WRAPPER-body::-webkit-scrollbar-track {
    background: #2d2d30;
}

.YOUR-GRID-WRAPPER-body::-webkit-scrollbar-thumb {
    background: #4e4e52;
    border-radius: 4px;
}

.YOUR-GRID-WRAPPER-body::-webkit-scrollbar-thumb:hover {
    background: #5e5e62;
}

/* Optional: remove double border between header and first body row */
.YOUR-GRID-WRAPPER-body .YOUR-TABLE-CLASS tbody tr:first-child td {
    border-top: none;
}
```

#### 2.3.4 Tables (fixed layout and alignment)

- Both header and body tables use the same width and `table-layout: fixed` so column widths match.
- Use the same column classes on both tables.

```css
.YOUR-TABLE-CLASS,
.YOUR-TABLE-CLASS-header {
    width: 100%;
    margin-bottom: 0;
    table-layout: fixed;
}

.YOUR-TABLE-CLASS th,
.YOUR-TABLE-CLASS td,
.YOUR-TABLE-CLASS-header th {
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}

.YOUR-TABLE-CLASS-header thead th {
    background-color: var(--bs-dark);
}

/* Your column widths (same classes on both tables) */
.col-first { width: 30%; }
.col-second { width: 20%; }
/* etc. */
```

### 2.4 Bootstrap usage (scrollable grid)

- **`container-fluid`** – full-width container.
- **`page-header`** – required class so the header is `flex-shrink: 0` and does not shrink.
- **`card`** / **`card-body p-0`** – same as static grid.
- **`table-responsive`** – used as a flex child; flex and overflow are controlled by your CSS, not Bootstrap’s default overflow.
- **`rounded`** – optional; border and radius are set in CSS. **Do not** add Bootstrap **`border`** on the grid wrapper.
- **`table table-dark`** – header table only.
- **`table table-striped table-hover`** – body table only.

### 2.5 Behavior

- Page does not scroll (layout + page-container fill viewport and use `overflow: hidden`).
- Header row stays fixed at the top of the grid.
- Only the body section scrolls; the vertical scrollbar appears only beside the data rows.
- Scrollbar is dark (track and thumb) via the CSS above.

---

## 3. How to Add a New Page with a Scrollable Grid

Follow these steps to create a new page that uses the scrollable grid pattern.

### Step 1: Add the Razor page

1. Create `YourPage.razor` under `Components/Pages/`.
2. Use the **scrollable grid** markup from **Section 2.2**:
   - Root: `container-fluid page-container`.
   - Top block: `page-header d-flex justify-content-between align-items-center mb-4` (or similar).
   - Optional: `alert` for errors.
   - Then: `card page-card` → `card-body p-0` → `table-responsive`.
   - Inside `table-responsive`: one wrapper div with your **grid wrapper class** and **`rounded`** (no `border`).
   - Inside that: **header** div → table with only `thead`; **body** div → table with only `tbody`.
3. Use a single **table class** and a **header modifier** (e.g. `your-table` and `your-table-header`) and the same **column classes** on both tables.
4. Fill in your columns and row content; keep structure as in 2.2.

### Step 2: Add the CSS file

1. Create `YourPage.razor.css` in the same folder.
2. Copy the **scrollable grid** CSS from **Section 2.3**:
   - Replace `YOUR-GRID-WRAPPER` with your wrapper class (e.g. `my-list`).
   - Replace `YOUR-TABLE-CLASS` with your table class (e.g. `my-table`).
3. Add your column width classes (e.g. `.col-first`, `.col-second`) so both tables use them.
4. Keep the **page-container**, **page-header**, **page-card**, **card-body**, **table-responsive**, and scrollbar rules as-is for the scrollable behavior.

### Step 3: Layout

- Ensure the page is rendered inside the main layout that uses **MainLayout.razor.css** (Section 2.1). In GrayMoon, routes use `MainLayout` by default, so no change is usually needed.

### Step 4: Optional styling

- Adjust **border** color in `.YOUR-GRID-WRAPPER` (e.g. `#252526`).
- Adjust **scrollbar** colors in `.YOUR-GRID-WRAPPER-body` and the `::-webkit-scrollbar*` rules.
- Add any extra table/cell styles (badges, buttons) without changing the flex/overflow structure.

### Checklist

- [ ] Page has `page-container` and header has `page-header`.
- [ ] Grid wrapper has **no** Bootstrap `border` class; border is in CSS.
- [ ] Two tables: one with only `thead`, one with only `tbody`; same column classes and `table-layout: fixed`.
- [ ] CSS includes the full flex chain (page-container → page-card → card-body → table-responsive → grid wrapper → header + body).
- [ ] Scrollable area is only the **body** div (`overflow-y: auto` and scrollbar styles).

---

## 4. Summary

| Aspect | Static grid (e.g. Workspaces) | Scrollable grid (e.g. Repositories) |
|--------|-------------------------------|--------------------------------------|
| **Structure** | Single `<table>` with `<thead>` + `<tbody>` | Two tables: header table + body table inside a wrapper |
| **Page scroll** | Yes, whole page scrolls | No, page height fixed (layout + CSS) |
| **Table scroll** | No | Yes, only body section; scrollbar beside data rows |
| **Bootstrap `border` on grid** | N/A or optional | Do not use; use CSS border |
| **CSS** | Table/column styles only | Full flex chain + grid wrapper + scrollbar styling |
| **Layout** | No special layout required | Requires MainLayout with height/overflow constraints |

Use the **static grid** for short lists and normal page scroll. Use the **scrollable grid** when you want a fixed-height page and a header that stays visible while only the data rows scroll with a dark scrollbar.

---

## Appendix: Scrollable Grid vs Original Table (Summary of Changes)

Compared to a simple static table (single `<table>` with thead + tbody inside card/table-responsive), the scrollable grid introduces:

### Razor (markup)

1. **Page header** – The top bar (title + toolbar) gets class **`page-header`** so it can be set to `flex-shrink: 0` and not shrink.
2. **No Bootstrap `border`** – The grid wrapper uses class **`rounded`** only; border is applied in CSS so it can be dark. Bootstrap’s **`border`** class is not used (it would show a light/white border).
3. **Split into two tables** – One table contains only `<thead>` inside a header div; a second table contains only `<tbody>` inside a body div. The body div is the only scrollable region, so the scrollbar appears only beside data rows.
4. **Shared column classes** – Both tables use the same column classes (e.g. `col-repo`, `col-owner`) and `table-layout: fixed` so column widths align.

### CSS (page)

1. **Page container** – `flex: 1 1 0`, `min-height: 0`, `overflow: hidden` (no `height: 100vh`) so the page fills the layout without causing a page-level scrollbar.
2. **Flex chain** – `page-card`, `card-body`, `table-responsive` are given `display: flex`, `flex-direction: column`, `flex: 1 1 0`, `min-height: 0`, `max-height: 100%`, `overflow: hidden` so height is constrained and nothing overflows.
3. **Grid wrapper** – A flex column with a **header** block (`flex-shrink: 0`) and a **body** block (`flex: 1 1 0`, `min-height: 0`, `overflow-y: auto`). Border and border-radius are set here (e.g. `border: 1px solid #252526`).
4. **Scrollbar** – Dark scrollbar via `scrollbar-color` (Firefox) and `::-webkit-scrollbar*` (Chrome/Edge/Safari) on the body div only.
5. **Tables** – Both tables use `table-layout: fixed` and the same column width classes; first body row can have `border-top: none` to avoid a double line under the header.

### Layout (MainLayout.razor.css)

For the scrollable grid to work without a page scrollbar, the layout must:

- Constrain **`.page`** to `height: 100vh` and `overflow: hidden`.
- Give **`main`** and **`article.content`** `flex: 1 1 0`, `min-height: 0`, `overflow: hidden`, and flex column so the page content receives a bounded height.

These layout rules are already present in GrayMoon’s `MainLayout.razor.css` and are required for any scrollable grid page.
