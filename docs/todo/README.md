# Open work

Notes for work that is not built, or only partly built. Anything fully shipped is deleted: its behavior belongs in `docs/architecture/`. A note that is partly shipped keeps only what is still open, and says what is built in a short section at the top.

Last checked against the code: 2026-11-24.

## `defects/` - something is wrong or missing today, and the cause is known

Classification (must / should / optional) and the order of work for all of these: [outstanding-defects-fix-plan](outstanding-defects-fix-plan.md).

| Note | Open work | Status |
|---|---|---|
| [implicit-fallback-actions](defects/implicit-fallback-actions.md) | **High priority.** Operations that do something other than what was asked, found after Undo Push Commits pushed a never-pushed branch (2026-11-21, fixed). Open: "Pull" also pushes; Sync Commits may publish a never-pushed branch; two `GitService` methods still interpolate their command line. Orchestrators and Feature operations not swept yet. | Original bug and items 3 to 5 fixed; three items open, to investigate and validate |
| [commit-all-partial-commit-race](defects/commit-all-partial-commit-race.md) | Worker `index.lock` retry and timeout tiers (Phase 3 items C, E, F, needs a repro first); run correlation id. The dirty-submodule failure is built. | Data-loss bug fixed; hardening open |
| [convention-test-coverage](defects/convention-test-coverage.md) | The convention tests check fewer fields, folders and pages than the rules name; five silent catches in the Worker are listed as open. | Partly built |
| [release-signing-secrets-and-workflow-run](defects/release-signing-secrets-and-workflow-run.md) | Desktop: move the signing secrets to the `production` environment and run the release workflow from a branch and a stable tag. | Owner action |
| [remove-desktop-dispatch-github-access](defects/remove-desktop-dispatch-github-access.md) | Remove the two unused `DESKTOP_APP_*` secrets and the GitHub App's access to GrayMoon.Desktop, left over from the deleted Desktop notification. The app itself stays: Desktop releases use it. | Owner action |
| [sync-performance-fix-plan](sync-performance-fix-plan.md) | The grid kept updating for up to a minute after Create Feature, Switch branch, Push and Update. Built: one recompute per burst of hook syncs, the version file check reads the stored commit, fewer checks inside Update. Every path that moves HEAD now stores the commit it left. Create Feature measured on Edx1: 51.8 s to 10.5 s, before its last 7 s were moved into the operation. Open: measure Update and Push. Measurements: [feature-create-background-sync-cost](ideas/feature-create-background-sync-cost.md). | Built; measurement open |
| [create-remove-feature-log-findings](defects/create-remove-feature-log-findings.md) | Found in the logs of 2026-11-24 (Create Feature, deploy, Remove Feature on a Workspace with 40 repositories). Fixed: Remove Feature failing the first time after the CodeGraph index was removed, the state label hidden behind a long name in the context selector, the sync queue waiting for a Feature in Create, the audit of already removed worktrees. Open: Create Feature's version check queues behind the hook burst on the Worker (82 s); the Repositories grid loads twice after Create and Remove; a Feature index without the Workspace switch looks like an uncommitted change. | Five fixed; two open, one to validate |
| [review-fix-test-gaps](defects/review-fix-test-gaps.md) | The tooltip load still lacks a behavioral test that a context change between its two queries stores nothing of the old context. The hub-attach check is in. | Partly built |

## `ideas/` - proposals and design notes, nothing is committed to

| Note | Idea | Status |
|---|---|---|
| [workspace-unhook-open-items](workspace-unhook-open-items.md) | Manual smoke and the follow-ups for removing GrayMoon's Git hooks when a Workspace or repository leaves, and for the Add / Import from disk / Restore split. The feature is built; how it works is in `docs/architecture/` (01, 02, 04 section 7, 05 section 1, 06 section 20G). | Built; smoke and follow-ups open |
| [skip-push-of-unchanged-new-branch](ideas/skip-push-of-unchanged-new-branch.md) | Bulk push and the orchestrators leave out a repository on a new, never-pushed branch with no commits, unless another repository depends on it (its package must be built under the new branch version). A click on the repository's own badge still pushes it. | Superseded: built as effective versions and selective push (see the note) |
| [app-background-work-visibility](ideas/app-background-work-visibility.md) | Show the App's own background queues (hook sync notifications, Git Changes writes, App syncs) like the Worker's task count, and report progress while an operation waits for them ("Applying pending repository changes..." shows nothing today). | Decided, not started |
| [changes-commit-timeline](ideas/changes-commit-timeline.md) | Commit timeline strip on the Changes page to review a Feature commit by commit across repositories (read-only "time machine"), persisted per branch, Feature contexts first. | Draft, design only |
| [connector-token-failure-handling](ideas/connector-token-failure-handling.md) | Classify connector token failures instead of showing "unknown" errors. | Draft, analysis only |
| [git-changes-large-changeset-scale](ideas/git-changes-large-changeset-scale.md) | Folder summaries, chunked Worker transfer, deltas for very large change sets. | Closed for now |
| [push-continue-waiting](ideas/push-continue-waiting.md) | "Continue waiting" on a failed push level: resume the package wait without pushing the finished levels again. | Not started |
| [read-only-repositories-open-items](read-only-repositories-open-items.md) | Manual smoke and the remaining follow-ups (tests only) for read only repositories. The feature is built: a Workspace default for new Sources, an independent flag per Source, no GrayMoon commit or push of a read only Source, and the branch and commit recorded in `graymoon.json`. How it works is in `docs/architecture/` (01, 02 section 7B, 03, 04, 05, 06, 07). | Built; smoke and follow-ups open |
| [pr-description-commit-history](ideas/pr-description-commit-history.md) | Read commit bodies as a fallback for PR descriptions. | Not started |
| [ai-generation-lane-decoupling](ideas/ai-generation-lane-decoupling.md) | Gate the New Pull Request AI button separately from the dialog's busy state. | Deliberately not built |

## Rules

- A defect note names the symptom, the cause, the code involved, and the fix plan. An idea note names the problem and the constraints; it does not need a fix plan.
- When a note ships, fold the behavior into `docs/architecture/`, delete the note, and update this index in the same change.
- Update the table above whenever a note is added, moved or deleted.
