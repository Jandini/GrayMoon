# GrayMoon Feedback Collection

Date: 2026-10-08  
Scope: recent feedback collected for the Workspace-as-Git-repository / Workspace Profiles work and related workflows.

This package intentionally separates observations into **BUG**, **NEW FEATURE**, and **IMPROVEMENT**. Items that naturally belong in one implementation effort are grouped together instead of being split into tiny unrelated tasks.

## Summary

| Category | Item | Priority | Suggested implementation grouping |
|---|---|---:|---|
| BUG | Git Changes renders some directories as files | High | Fix Git Changes path/tree modelling and add regression tests |
| IMPROVEMENT | New Pull Request target-branch loading is far too slow for large workspaces | High | Replace per-repository fetch/read/persist flow with bulk cached reads and optional background freshness |
| NEW FEATURE | Show processes blocking Feature removal | High | Add Worker-side lock diagnostics and integrate them into Remove Feature failure UX |
| IMPROVEMENT | Restore Workspace UX and App/Worker version lock | High | Preflight `.graymoon.json` before mutating, transactional restore, replace the Workspace-repository capability gate with the version lock ([details](graymoon-restore-workspace-and-worker-version-lock.md)) |

## Suggested implementation order

1. **BUG: Git Changes directory rendering**
   - User-visible correctness issue.
   - Smallest and safest item.
   - Should be fixed independently and regression-tested.

2. **IMPROVEMENT: New Pull Request initialization performance**
   - Large productivity impact in 20+ repository workspaces.
   - Current implementation performs network-heavy work just to populate target branches.
   - This should be treated as a design correction, not as micro-optimization.

3. **NEW FEATURE: Feature removal blocker diagnostics**
   - Valuable quality-of-life improvement for worktree-heavy AI workflows.
   - Best implemented after the removal path is stable, because it hooks into the failure path rather than replacing removal logic.

## Guiding principles

- Avoid changing unrelated behavior while addressing the feedback.
- Prefer bulk operations over N-per-repository operations.
- Do not put remote/network operations on the critical UI-open path unless freshness is strictly required.
- Keep local filesystem/process diagnostics in the Worker, not the App.
- When a failure is unavoidable, expose actionable diagnostics rather than generic Git/IO errors.
- Add focused regression/performance tests for each item.
