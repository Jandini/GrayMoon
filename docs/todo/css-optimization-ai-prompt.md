# CSS Optimization & Build Robustness — AI Prompt

Purpose: hand this prompt to a strong coding model, run from the repo root `C:\Workspace\GrayMoon\GrayMoon`.
Do not commit between phases; leave changes uncommitted for human review.

---

## Prompt (copy everything below)

You are a senior front-end/.NET engineer. Analyze and optimize the CSS of the Blazor app `src/GrayMoon.App` so that (1) the app benefits (smaller, faster, more consistent styling), (2) the release build is robust against the failure described below, and (3) **nothing visually or functionally breaks**. Correctness and zero visual regression outrank size savings. Work in phases and **stop for my approval after each phase** — do not start the next phase on your own. Do not commit anything.

### 1. The problem

The GitHub Actions release build (`GrayMoon.Desktop` repo, `release.yml`, step `dotnet build GrayMoon.slnx --configuration Release --no-restore`, windows runner, .NET SDK 10.0.404) failed once, then passed on re-run:

```
Microsoft.NET.Sdk.StaticWebAssets.ScopedCss.targets(177,3): error MSB4018: The "RewriteCss" task failed unexpectedly.
System.AggregateException ---> System.Text.RegularExpressions.RegexMatchTimeoutException:
  The Regex engine has timed out while trying to match a pattern to an input string.
   at Regex.IsMatch(String)
   at RewriteCss.FindScopeInsertionEdits.<>c.<VisitSelector>b__5_0(SimpleSelector s)
   at RewriteCss.AddScopeToSelectors(...)
   at RewriteCss.Execute()  (Parallel.For over files)
```

The regex lives in the .NET SDK, not in our code (we have zero `Regex` usages in `.cs`/`.razor`). It is hit by Blazor **scoped CSS** (`*.razor.css`). Known facts:

- `GrayMoon.App` has ~50 `*.razor.css` files, ~240 KB total. Largest: `WorkspaceRepositories.razor.css` (~34 KB), `MergePullRequestModal` (~18 KB), `WorkspaceFeatureSelector` (~18 KB), `WorkspaceActions` (~17 KB), `NewPullRequestModal` (~16 KB), `BulkMergePullRequestsModal` (~15 KB), `MainLayout` (~11 KB).
- Global CSS already exists: `wwwroot/app.css`, `wwwroot/css/{ai-generation,git-changes,loading-overlay,workspace-grid-search}.css` (git-changes.css is ~43 KB), plus bootstrap and bootstrap-icons. They are linked from `Components/App.razor`, followed by the generated `GrayMoon.App.styles.css` (the scoped bundle).
- Suspected cause: an expensive selector and/or large scoped files processed in parallel on a loaded runner. Not yet proven — **prove or disprove it**.
- ~15 `*Modal.razor.css` files probably duplicate the same rules (modal layout, buttons, tables, form fields).

### 2. Constraints (hard rules)

- No visual regressions. CSS scoping changes specificity and which elements are matched (scoped rules only hit elements rendered by that component, not child components, unless `::deep` is used). Every extraction to global CSS must preserve exact cascade/specificity behavior; account for rule ORDER, since global files load before `GrayMoon.App.styles.css` and scoped selectors get an extra `[b-xxxx]` attribute (higher specificity).
- Do not remove a rule unless you have **verified** it is unused (search all `.razor`, `.cs`, `.js`, `wwwroot/js`, and dynamically built class names / string concatenation / `class=` interpolation / JS `classList`, and classes injected by Monaco/mermaid/bootstrap). When unsure, keep it and list it as "uncertain".
- Do not change behavior or markup unless required; if markup changes are needed (e.g. renaming a class), list each one explicitly for approval.
- Preserve `::deep` semantics, `@media`, `@keyframes` (note: scoped keyframes get renamed with the scope id — check animation-name references), CSS custom properties, dark/light theme handling (`moon-dark.svg`, theme toggles), and `!important` use.
- Keep the repo conventions (see `CLAUDE.md` / `AGENTS.md`). Do not touch the `GrayMoon.Desktop` repo except in the CI step in phase 5.

### 3. Phases

**Phase 0 — Evidence (read-only, no edits).**
1. Inventory every `*.razor.css` (path, size, rule count, selector count, longest selector, max combinator depth, number of `::deep`, `:is/:not/:where/:has` uses, `@media`, `@keyframes`). Produce a table sorted by size.
2. Reproduce the failure or explain why it cannot be reproduced: build `GrayMoon.App` in Release repeatedly (e.g. 10 clean builds, also under CPU stress / `-m:1` vs default) and time the `RewriteCss` task (`dotnet build -c Release -v:d` or a binlog: `-bl`, inspect with `dotnet msbuild`/binlog tools). Identify which file/selector is slowest. If the SDK source or issues are findable (dotnet/sdk, dotnet/aspnetcore: `RewriteCss`, `RegexMatchTimeoutException`, scoped CSS regex timeout), cite what is known: the exact regex, its timeout value, and whether a fix or an MSBuild property exists in newer SDKs. Do not guess; state confidence levels.
3. Report findings and root-cause hypothesis. **Stop for approval.**

**Phase 1 — Analysis of duplication and dead CSS (read-only).**
1. Find rules/blocks duplicated across `*.razor.css` files (normalize whitespace, compare declaration sets; group by identical or near-identical selector+declarations). Pay special attention to the `*Modal.razor.css` family, page-level tables/forms, buttons, badges, and spinners.
2. Find overlap between scoped CSS and global CSS (`app.css`, `css/*.css`, bootstrap overrides).
3. Find unused selectors (verify per the constraints above) and redundant/overridden declarations.
4. Propose a concrete target structure: which shared rules go to which global file (e.g. `css/modals.css`, `css/tables.css`), what stays scoped, and what gets split. For each proposed move give: source selectors, the specificity before/after, and the risk (low/med/high). Estimate byte savings and the effect on the scoped bundle size.
5. **Stop for approval.**

**Phase 2 — Safety net before changing anything.**
1. Build a visual-regression baseline: for every page and modal in the app, define how to capture before/after (e.g. Playwright screenshots against a running app in both themes at 2 viewport widths) — or if that is not feasible, produce a precise manual checklist per component. Capture the baseline BEFORE any CSS edit.
2. Add an automated guard that fails the build if any single `*.razor.css` exceeds an agreed size/selector-complexity budget (propose the numbers from phase 0 data), if practical (MSBuild target or test).
3. **Stop for approval.**

**Phase 3 — Implement, one group at a time.**
1. Apply the approved moves in small, independently reviewable groups (e.g. modals first, then the 34 KB `WorkspaceRepositories` split, then dead CSS removal). After each group: build Release, run the unit tests, re-capture visual diffs, and report any pixel/DOM-computed-style difference. Revert and rethink if there is any unexplained diff.
2. Prefer plain, simple selectors; avoid deep descendant chains, long `:is/:not/:where` lists and huge single files. Flatten where it does not change matching.
3. **Stop for approval after each group.**

**Phase 4 — Validate.**
1. Re-run the phase-0 timing measurements and show before/after: scoped CSS total size, largest file, `RewriteCss` task time, `GrayMoon.App.styles.css` size, page load transfer size of CSS.
2. Full Release build x10 clean, with no `RewriteCss` errors. Run all tests.
3. Final visual regression report (before/after per page/modal/theme). List anything uncertain.

**Phase 5 — Belt and braces for CI.**
1. In `GrayMoon.Desktop/.github/workflows/release.yml` (and `build.yml` if it has the same step), add a single retry of the build step on failure (PowerShell: rerun `dotnet build` once if `$LASTEXITCODE -ne 0`, then fail). Keep the log clear that a retry happened.
2. Evaluate (do not blindly apply) whether disabling scoping via `<ScopedCssEnabled>false</ScopedCssEnabled>` is viable given the cleaned-up CSS; state the risks. Recommend one option.

### 4. Deliverables

- A report in `docs/todo/css-optimization-report.md` with: evidence, root cause with confidence level, the duplication/dead-CSS inventory, the change list per group, before/after metrics, residual risks, and rollback notes.
- Changes left **uncommitted** in the working tree, grouped so that each phase can be reviewed and committed separately.
- A final summary listing every rule removed or moved, and every item you were unsure about and kept.

Rules of engagement: verify claims by running things; show command output as evidence; say clearly when something is a hypothesis; do not invent SDK behavior; if you hit ambiguity that would change the outcome, ask rather than assume.
