# Open work

Notes for work that is not built, or only partly built. Anything fully shipped is deleted: its behavior belongs in `docs/architecture/`. A note that is partly shipped keeps only what is still open, and says what is built in a short section at the top.

Last checked against the code: 2026-11-11 (branch `code-review-fixes`, on top of `565f02a1`).

## `defects/` - something is wrong or missing today, and the cause is known

Classification (must / should / optional) and the order of work for all of these: [outstanding-defects-fix-plan](outstanding-defects-fix-plan.md).

| Note | Open work | Status |
|---|---|---|
| [commit-all-partial-commit-race](defects/commit-all-partial-commit-race.md) | Stale targets (Phase 2); Worker `index.lock` retry and timeout tiers (Phase 3); run correlation id. | Data-loss bug fixed; hardening open |
| [push-package-wait-and-circuit-crash](defects/push-package-wait-and-circuit-crash.md) | Hub start retry and logging, tests, top bar writes from browser tabs, wiki wording. | Mostly shipped |
| [generated-package-context-write](defects/generated-package-context-write.md) | A Feature that cannot see a version file can delete workspace-global generated package rows. | Not built |
| [sqlite-synchronous-per-connection](defects/sqlite-synchronous-per-connection.md) | `PRAGMA synchronous=NORMAL` only runs on the startup connection. | Not built |
| [worker-damaged-install-probe](defects/worker-damaged-install-probe.md) | No startup probe reports a damaged Worker install once. | Not built |
| [connector-refresh-empty-answer](defects/connector-refresh-empty-answer.md) | Removing a connector deletes its repositories with their Workspace links; it becomes a soft delete. | Refresh part shipped; removal open |
| [worker-response-dropped-while-reconnecting](defects/worker-response-dropped-while-reconnecting.md) | A command result produced during a reconnect is dropped and the App waits out the timeout. | Not built, to confirm first |
| [silent-catch-blocks](defects/silent-catch-blocks.md) | Five catch blocks on state-changing or status paths swallow the failure without a log line. | Not built |
| [convention-test-coverage](defects/convention-test-coverage.md) | The convention tests check fewer fields, columns, folders and pages than the rules name. | Not built |
| [release-signing-secrets-and-workflow-run](defects/release-signing-secrets-and-workflow-run.md) | Desktop: move the signing secrets to the `production` environment and run the release workflow from a branch and a stable tag. | Owner action |
| [review-fix-test-gaps](defects/review-fix-test-gaps.md) | Five fixes lack the regression test that proves them (failed integrity check, serial cancel dispatch, context flip, tests against old code, hub attach). | Not built |

## `ideas/` - proposals and design notes, nothing is committed to

| Note | Idea | Status |
|---|---|---|
| [changes-commit-timeline](ideas/changes-commit-timeline.md) | Commit timeline strip on the Changes page to review a Feature commit by commit across repositories (read-only "time machine"), persisted per branch, Feature contexts first. | Draft, design only |
| [connector-token-failure-handling](ideas/connector-token-failure-handling.md) | Classify connector token failures instead of showing "unknown" errors. | Draft, analysis only |
| [git-changes-large-changeset-scale](ideas/git-changes-large-changeset-scale.md) | Folder summaries, chunked Worker transfer, deltas for very large change sets. | Closed for now |
| [push-continue-waiting](ideas/push-continue-waiting.md) | "Continue waiting" on a failed push level: resume the package wait without pushing the finished levels again. | Not started |
| [pr-description-commit-history](ideas/pr-description-commit-history.md) | Read commit bodies as a fallback for PR descriptions. | Not started |
| [ai-generation-lane-decoupling](ideas/ai-generation-lane-decoupling.md) | Gate the New Pull Request AI button separately from the dialog's busy state. | Deliberately not built |

## Rules

- A defect note names the symptom, the cause, the code involved, and the fix plan. An idea note names the problem and the constraints; it does not need a fix plan.
- When a note ships, fold the behavior into `docs/architecture/`, delete the note, and update this index in the same change.
- Update the table above whenever a note is added, moved or deleted.
