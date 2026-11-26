# Outstanding defects: classification and order

Last checked 2026-11-21. Covers every note in `docs/todo/defects/`. The items fixed in that round are removed from this plan; their behavior is in `docs/architecture/` and their notes are deleted.

## Classes

- **Must** - loses or corrupts data, or leaves a secret exposed. Fix before the next release.
- **Should** - a real defect with a wrong or missing result, or a broken rule, but no data loss.
- **Optional** - hardening with no observed failure, a missing feature, or a test-only gap. Safe to defer.

## Open items

| Item | Note | Class | Size |
|---|---|---|---|
| "Pull" also pushes; Sync Commits may publish a never-pushed branch (unintended publish, same harm as the fixed Undo Push Commits bug of 2026-11-21) | [implicit-fallback-actions](defects/implicit-fallback-actions.md) (items 1, 2) | Should (high priority, validate first) | M |
| Interpolated git command lines in `GitService` (`CheckoutBranchAsync`, `DeleteBranchAsync`) | [implicit-fallback-actions](defects/implicit-fallback-actions.md) (item 6) | Optional | S |
| Signing secrets still at repository level; release workflow never run | [release-signing-secrets-and-workflow-run](defects/release-signing-secrets-and-workflow-run.md) | Must (owner action) | S |
| Unused GitHub secrets and app write access to GrayMoon.Desktop, left from the deleted Desktop notification | [remove-desktop-dispatch-github-access](defects/remove-desktop-dispatch-github-access.md) | Should (owner action) | S |
| Missing test: context flip in the tooltip load | [review-fix-test-gaps](defects/review-fix-test-gaps.md) | Optional | M |
| Convention scans narrower than the rules: page-state scan, context-read scan, App folders the catch scan does not reach; five silent Worker catches listed as `OPEN` | [convention-test-coverage](defects/convention-test-coverage.md) | Optional | M |
| Worker commit hardening C, E, F | [commit-all-partial-commit-race](defects/commit-all-partial-commit-race.md) (Phase 3) | Optional (needs repro) | L |
| Correlation id per commit run; fan-out audit; 240 s status scan timeout | [commit-all-partial-commit-race](defects/commit-all-partial-commit-race.md) (Other) | Optional | S-M |

## Notes per item

- **Implicit fallback actions** (added 2026-11-21, high priority): items 3, 4 and 5 were fixed on 2026-11-21. None of the open items was reproduced. Items 1 and 2 are Should but come first, because they publish commits the user did not ask to push; write the two Worker tests named in the note first and fix what reproduces. The sweep did not cover the orchestrators or the Feature operations; do that before calling the class of bug closed.

- **Signing secrets**: no code change. All `ESIGNER_*` uses in `GrayMoon.Desktop/.github/workflows/release.yml` are in the one `build-and-release` job, which carries the conditional `environment:`. The owner steps are in the note. It is independent of everything else and can happen at any time.
- **Missing test**: the context-flip test needs the load extracted from the page, or a component test harness. Worth doing when that page is next refactored, not on its own. The hub-attach check is in `WorkspacePageJobDisciplineTests`; the behavior is in `04-runtime-communication-and-concurrency.md` (page jobs).
- **Convention scans**: widen one scan at a time, in the same change as a fix it guards. Confirm each `OPEN` silent catch with a test before fixing it.
- **Worker commit hardening**: no log shows C, E or F happening. Item D (the staging pause, and a dirty submodule failing at once) is built; see `05-user-capability-reference.md` (Commit control). Build a repro for E first.
- **Correlation id and audit**: the correlation id is small and useful for the next incident. The audit of the `MultiSelect`, `Discard` and `BulkSectionAction` fan-outs for shared scoped services is read-only, about half a day. The 240 s status scan timeout needs its own investigation.

## Rules for the work

Every fix ships with a regression test that fails on the old code. After a behavior change, update the architecture document and add the bullet to `GrayMoon.Desktop/README.md`. When a note is fully shipped, delete it, remove its row here and update `docs/todo/README.md`. Nothing is committed without an explicit request.
