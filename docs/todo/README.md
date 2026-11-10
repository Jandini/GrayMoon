# Open work

Notes for work that is not built, or only partly built. Anything fully shipped is deleted: its behavior belongs in `docs/architecture/`. A note that is partly shipped keeps only what is still open, and says what is built in a short section at the top.

Last checked against the code: 2026-11-10 (branch `code-review-fixes`, on top of `565f02a1`).

## `defects/` - something is wrong or missing today, and the cause is known

| Note | Open work | Status |
|---|---|---|
| [commit-all-partial-commit-race](defects/commit-all-partial-commit-race.md) | Stale targets (Phase 2); Worker `index.lock` retry and timeout tiers (Phase 3); run correlation id. | Data-loss bug fixed; hardening open |
| [push-package-wait-and-circuit-crash](defects/push-package-wait-and-circuit-crash.md) | Hub start retry and logging, tests, Continue waiting, top bar writes from browser tabs, wiki wording. | Mostly shipped |
| [generated-package-context-write](defects/generated-package-context-write.md) | A Feature that cannot see a version file can delete workspace-global generated package rows. | Not built |
| [sqlite-synchronous-per-connection](defects/sqlite-synchronous-per-connection.md) | `PRAGMA synchronous=NORMAL` only runs on the startup connection. | Not built |
| [worker-damaged-install-probe](defects/worker-damaged-install-probe.md) | No startup probe reports a damaged Worker install once. | Not built |

## `ideas/` - proposals and design notes, nothing is committed to

| Note | Idea | Status |
|---|---|---|
| [connector-token-failure-handling](ideas/connector-token-failure-handling.md) | Classify connector token failures instead of showing "unknown" errors. | Draft, analysis only |
| [git-changes-large-changeset-scale](ideas/git-changes-large-changeset-scale.md) | Folder summaries, chunked Worker transfer, deltas for very large change sets. | Closed for now |
| [pr-description-commit-history](ideas/pr-description-commit-history.md) | Read commit bodies as a fallback for PR descriptions. | Not started |
| [ai-generation-lane-decoupling](ideas/ai-generation-lane-decoupling.md) | Gate the New Pull Request AI button separately from the dialog's busy state. | Deliberately not built |

## Rules

- A defect note names the symptom, the cause, the code involved, and the fix plan. An idea note names the problem and the constraints; it does not need a fix plan.
- When a note ships, fold the behavior into `docs/architecture/`, delete the note, and update this index in the same change.
- Update the table above whenever a note is added, moved or deleted.
