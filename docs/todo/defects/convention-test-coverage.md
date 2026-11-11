# Convention tests cover less than the rules they enforce

Status: partly built. On 2026-11-11 two scans were widened: the shared-link scan now also guards `SyncStatus`, `OutgoingCommits`, `IncomingCommits` and `GitVersion`, and the best-effort catch scan now covers `Services/Connectors`, `Services/Features`, `Hubs` and the whole Worker (see `docs/architecture/06-developer-extension-guide.md`, "Convention tests"). What is below is still open.

The rules in `AGENTS.md` read as applying everywhere; the tests under `src/GrayMoon.App.Tests/Conventions/`, `src/GrayMoon.Worker.Tests/Conventions/` and `WorkspacePageJobDisciplineTests` check a narrower set, so a new violation outside that set passes.

## Gaps

| Rule | What the test checks today | What is not checked |
|---|---|---|
| Page state only on the dispatcher | Four fields of the Repositories page (`_slots`, `_linkByRepoId`, `prByRepositoryId`, `repoSyncStatus`). | Other page fields the markup reads; the Actions and Git Changes pages. |
| Best-effort steps report | `catch` blocks in `Services/Workspaces`, `Services/Git`, `Services/Connectors`, `Services/Features` and `Hubs` of the App, and in the Worker. | Every other folder in the App (`Components`, `Repositories`, `Api`, the other `Services` folders). |
| Context captured before the work starts | Every `RequireSelectedContextId()` that is not a capture into a local must be on an explicit list of reads made before the first `await`. | Direct reads of `_selectedContextId` and `_isFeatureContext` after an `await` in an event handler. The list is kept by hand; nothing proves a listed read is before the first `await`. |

## Silent catches the widened scan found and left in place

`BestEffortCatchConventionTests` lists these with a reason that starts with `OPEN`. Each returns "nothing" for a read that failed, with no log line, so the user sees a wrong result and no cause. None was seen to happen; confirm with a test before fixing.

| Where | What is hidden |
|---|---|
| `CsProjFileParser` (Worker), three catches | A project file that cannot be read or parsed is dropped from the project scan, or reported as "not updated" / "nothing to update". The class has no logger. |
| `GitWorktreeService.VisitResidueEntries` (Worker) | A folder that cannot be listed counts as empty in the residue scan after a worktree removal. |
| `WorkspaceFileSearchService` (Worker) | A workspace folder that cannot be listed gives an empty Files search result. |

## Fix plan

Widen one scan at a time, in the same change as a fix it guards where possible. Each widened scan starts with an explicit exception list of today's violations, and the list shrinks as they are fixed. Do not widen a scan by adding a blanket folder exclusion.

## Tests

The scans are the tests. For each widened scan, add one deliberately wrong sample in the test (a string, not a source file) to prove the scan can fail.
