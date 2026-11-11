# Push: "Continue waiting" on a failed level

Status: not started. Moved here on 2026-11-11 from the push wait defect note, which is closed (it is a missing feature, not a defect).

## Problem

When the package wait of a synchronized push times out at a dependency level, the failed-level callout tells the user to run Push Synchronized again. That repeats the push of the levels already done, although the only thing missing is that the registry did not yet report the packages.

## Idea

A "Continue waiting" action on the failed-level callout that resumes the same operation at that level: wait again for the missing packages, then carry on with the higher levels, without pushing the finished levels again.

## Constraints

- GrayMoon is not CI-aware. The wait is decided only by what the package registry reports; the action must not query, assume or name a CI product.
- The resumed run uses the context and the repository set of the original operation, captured when it was queued, not what the page shows now.
- The outcome is recorded in `WorkspaceOperationOutcomeStore` like the original failure, and replaces it when the resumed run succeeds.
- Tag-pinned repositories stay excluded, as in every push.

## Code involved

`WorkspacePushService` (package wait and its timeout message), `PushOrchestrator`, the level callout on the Repositories page, `WorkspaceOperationOutcomeStore`.
