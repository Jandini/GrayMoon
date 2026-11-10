# Convention tests cover less than the rules they enforce

Status: not built. The rules in `AGENTS.md` read as applying everywhere; the tests under `src/GrayMoon.App.Tests/Conventions/`, `src/GrayMoon.Worker.Tests/Conventions/` and `WorkspacePageJobDisciplineTests` check a narrower set, so a new violation outside that set passes.

## Gaps

| Rule | What the test checks today | What is not checked |
|---|---|---|
| Page state only on the dispatcher | Four fields of the Repositories page (`_slots`, `_linkByRepoId`, `prByRepositoryId`, `repoSyncStatus`). | Other page fields the markup reads; the Actions and Git Changes pages. |
| Feature contexts do not write the shared link | The `BranchHasUpstream`, `BranchName` and `CheckedOutTag` setters. | `SyncStatus`, `OutgoingCommits`, `IncomingCommits` and `GitVersion` on `WorkspaceRepositoryLink`. |
| Best-effort steps report | `catch` blocks in `Services/Workspaces` and `Services/Git`. | Every other folder in the App, and the Worker (see [silent-catch-blocks](silent-catch-blocks.md)). |
| Context captured before the work starts | Every `RequireSelectedContextId()` that is not a capture into a local must be on an explicit list of reads made before the first `await`. | Direct reads of `_selectedContextId` and `_isFeatureContext` after an `await` in an event handler. The list is kept by hand; nothing proves a listed read is before the first `await`. |

## Fix plan

Widen one scan at a time, in the same change as a fix it guards where possible. Each widened scan starts with an explicit exception list of today's violations, and the list shrinks as they are fixed. Do not widen a scan by adding a blanket folder exclusion.

## Tests

The scans are the tests. For each widened scan, add one deliberately wrong sample in the test (a string, not a source file) to prove the scan can fail.
