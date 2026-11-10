# Catch blocks that swallow a failure without a trace

Status: not built. The five places below were read and are as described; the first three matter, the last two are low.

## Symptom

A failed root-path refresh, registry lookup, repository path lookup, commit step or git read leaves no log line and no outcome, so the next symptom (a missing package mapping, a Feature that is not reconciled) cannot be traced to its cause.

## Where

| Place | What is swallowed | Why it matters |
|---|---|---|
| `src/GrayMoon.App/Hubs/WorkerHub.cs:34` | The root-path refresh started when a Worker connects. | The refresh silently does not happen. |
| `src/GrayMoon.App/Services/Connectors/PackageRegistrySyncService.cs:152` | A failed `PackageExistsAsync` becomes "does not exist". | A network error reads as "no registry has this package". |
| `src/GrayMoon.App/Services/Features/WorkspaceFeatureReconciler.cs:467` | A failed repository path lookup becomes `null`. | Callers may treat `null` as "no path", not "lookup failed". |
| `src/GrayMoon.Worker/Commands/StageAndCommitCommand.cs:58` | A failed status refresh after the commit. | Low: the watcher refreshes the repository anyway, but the failure is not logged. |
| `src/GrayMoon.Worker/Services/GitService.cs:96` | A failed "clear read-only" on one file before a folder delete. | Low: the delete that follows reports its own failure. Keep as is, or log at debug. |

Line numbers are from `code-review-fixes`; search for the bare `catch` near them.

## Fix plan

1. Start with `PackageRegistrySyncService` and `WorkspaceFeatureReconciler`: they return a value callers may read as "none found". Return a distinct outcome or log at warning with the exception, and check each caller.
2. Log at warning with the exception in `WorkerHub` and `StageAndCommitCommand`.
3. Extend the best-effort convention test to these folders (see [convention-test-coverage](convention-test-coverage.md)).

About 20 more bare catches exist outside `Services/Workspaces` and `Services/Git`; most are parse fallbacks. List them when the scan is widened and decide each one.

## Rule this falls under

`AGENTS.md`, "Failures, cancellation and waiting", rule 1.
