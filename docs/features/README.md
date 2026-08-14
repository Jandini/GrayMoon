# GrayMoon UI features

This folder documents every user-visible feature in the GrayMoon web UI, starting from the first-level pages and then the nested workspace pages those routes open.

Screenshots are local PNG files under [`screenshots/`](screenshots/). They were captured from a running instance at `http://127.0.0.1:8384/`.

## How to read this

- **First-level pages** are the six sidebar items when you are not inside a workspace: Home, Workspaces, Repositories, Connectors, Agent, Settings.
- **Workspace pages** replace that sidebar once you open a workspace (click a workspace name). They are documented under [`workspace/`](workspace/).
- Repeating controls (search, toasts, loading overlay, agent badge) are defined once in [`shared.md`](shared.md) and referenced from page docs.

## First-level pages

| Page | Route | Doc |
| --- | --- | --- |
| Home | `/` | [01-home.md](01-home.md) |
| Workspaces | `/workspaces` | [02-workspaces.md](02-workspaces.md) |
| Repositories | `/repositories` | [03-repositories.md](03-repositories.md) |
| Connectors | `/connectors` | [04-connectors.md](04-connectors.md) |
| Agent | `/agent` | [05-agent.md](05-agent.md) |
| Settings | `/settings` | [06-settings.md](06-settings.md) |

## Workspace pages (after opening a workspace)

| Page | Route | Doc |
| --- | --- | --- |
| Repositories | `/workspaces/{id}` | [workspace/repositories.md](workspace/repositories.md) |
| Changes | `/workspaces/{id}/changes` | [workspace/changes.md](workspace/changes.md) |
| Projects | `/workspaces/{id}/projects` | [workspace/projects.md](workspace/projects.md) |
| Packages | `/workspaces/{id}/packages` | [workspace/packages.md](workspace/packages.md) |
| Files | `/workspaces/{id}/files` | [workspace/files.md](workspace/files.md) |
| Dependencies | `/workspaces/{id}/dependencies` | [workspace/dependencies.md](workspace/dependencies.md) |
| Actions | `/workspaces/{id}/actions` | [workspace/actions.md](workspace/actions.md) |

## Shared chrome

See [shared.md](shared.md) for sidebar, top bar, agent badge, filter search, toasts, loading overlay, reconnect dialog, and workspace action notification cards.
