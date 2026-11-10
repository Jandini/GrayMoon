# GrayMoon documentation map

Everything under `docs/`, what it is for, and which code to compare it with. Use this as the checklist when reviewing the documents against the code.

| Folder | Kind | Rule |
|---|---|---|
| `architecture/` | Current state | Describes what the code does now. Wrong text is a bug. |
| `todo/defects/` | Open work | Known problem, cause, fix plan. Delete when fixed. |
| `todo/ideas/` | Open work | Proposals not committed to. Delete when built or rejected. |

The index of open work, with a status per note, is [todo/README.md](todo/README.md). The workspace-level `docs/documentation-update/` folder (one level above this repository) holds the plan for the wiki and README pass; it is not product documentation.

## Architecture documents and the code they describe

Each document records a code baseline in its header. The baseline is older than the current HEAD, so every document needs checking against the code changed since.

| Document | Main code to compare |
|---|---|
| [README](architecture/README.md) | Folder layout, project list, reading order |
| [01 Product model and user workflows](architecture/01-product-model-and-user-workflows.md) | `src/GrayMoon.App/Components` (pages, dialogs), workflows in `src/GrayMoon.App/Services/Workspaces` |
| [02 System architecture](architecture/02-system-architecture.md) | `src/GrayMoon.App` (`Program.cs`, services, connectors), `src/GrayMoon.Worker`, `src/GrayMoon.Application`, `src/GrayMoon.Abstractions` |
| [03 Data model and persistence](architecture/03-data-model-and-persistence.md) | `src/GrayMoon.App` models, `AppDbContext`, migrations (`Migrations.*.cs`), `graymoon.json` handling |
| [04 Runtime communication and concurrency](architecture/04-runtime-communication-and-concurrency.md) | `WorkerHub`, `WorkspaceSyncHub`, job pools, locks, Git Changes watchers, `src/GrayMoon.Worker` command handlers |
| [05 User capability reference](architecture/05-user-capability-reference.md) | Page and operation code in `src/GrayMoon.App`, AI generation in `src/GrayMoon.Common/Ai` and `src/GrayMoon.Worker/Services/GitEvidence` |
| [06 Developer extension guide](architecture/06-developer-extension-guide.md) | Rules and known gaps; check each gap against code and `todo/` |
| [07 Git Changes projection and tree](architecture/07-git-changes-projection-and-tree.md) | `GitChangesWorkspaceScanner`, `GitChangesSnapshotPushHandler`, `GitChangesTreeBuilder`, the Changes page |

Desktop documents live in the sibling repository: `GrayMoon.Desktop/docs/architecture/` (index in its `README.md`). Compare them with `GrayMoon.Desktop/src`.

## Other documents that make claims about the code

- `README.md`, `CLAUDE.md`, `AGENTS.md` in this repository, and the workspace-level `README`-style files (`CLAUDE.md`, `AGENTS.md`).
- `GrayMoon.Desktop/README.md`, including `## Recent GrayMoon changes`.
- The wiki checkout `GrayMoon.wiki/` (user guide). Its review plan is in the workspace `docs/documentation-update/`.

## Keeping it clean

- Behavior change: update the matching architecture document in the same change.
- New unbuilt work: add a note under `todo/defects/` or `todo/ideas/` and a row in `todo/README.md`.
- Shipped or rejected work: delete the note.
