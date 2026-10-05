# Cursor rules for GrayMoon

This folder contains rules and instructions for Cursor when working on this repo.

## Windows environment

- **Line endings:** Always use **Windows (CRLF)** in generated or edited code. Do not use LF-only.
- **Terminal:** Commands run in **PowerShell**. Use `;` to chain commands, not `&&` (e.g. `cd path; dotnet build`).
- **The request summary** must be very brief - one or two sentences. Ask if I want more details before generating more.

## Rules in `.cursor/rules/`

| Rule | Purpose |
| --- | --- |
| `windows-eol-and-shell.mdc` | CRLF line endings and PowerShell command syntax |
| `summary-of-changes.mdc` | Brief end-of-task summary |
| `hyphens-not-em-en-dashes.mdc` | ASCII hyphen only |
| `ascii-diagram-alignment.mdc` | Alignment of ASCII diagrams |
| `primary-constructors.mdc` | Prefer C# primary constructors |
| `dbcontext-scoped-lifetime.mdc` | No concurrent use of the circuit-scoped `AppDbContext` |
| `feature-context-scoping.mdc` | Feature (worktree) data must be filtered by `WorkspaceFeatureContextId` |
| `ui-application-facades.mdc` | Pages call `IWorkspace*Operations`, not internal services |
| `desktop-readme-on-feature-change.mdc` | Update the Desktop README "Recent GrayMoon changes" list |
| `no-commit-or-push.mdc` | Never commit or push; propose a commit message instead |

The same conventions are described in more detail in `CLAUDE.md` and `AGENTS.md` in the repository root.
