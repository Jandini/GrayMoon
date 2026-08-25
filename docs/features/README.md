GrayMoon UI features

This folder documents every user-visible feature in the GrayMoon web UI, starting from the first-level pages and then the nested workspace pages those routes open.

Screenshots are local PNG files under [`screenshots/`](screenshots/). They were captured from a running instance at `http://127.0.0.1:8384/`.

## Getting started (clean install)

Clean database, Agent not installed yet: [getting-started/](getting-started/) - [install the Agent](getting-started/00-agent.md), [add and activate working connectors](getting-started/01-connectors.md) (each **Active** with **OK** status), [fetch the catalog](getting-started/02-fetch-repositories.md), [create a workspace and Sync-clone](getting-started/03-workspace-clone.md).

## How to read this

- **First-level pages** are the six sidebar items when you are not inside a workspace: Home, Workspaces, Repositories, Connectors, Agent, Settings.
- **Workspace pages** replace that sidebar once you open a workspace (click a workspace name). They are documented under [`workspace/`](workspace/).
- Repeating controls (search, toasts, loading overlay, agent badge) are defined once in [`shared.md`](shared.md) and referenced from page docs.
- **Layout** (sidebar collapse, top bar) is [00-layout.md](00-layout.md).

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
| Restore (NuGet) | (from Repositories **Sync** caret) | [workspace/restore.md](workspace/restore.md) |
| New Branch | (from Repositories **Branch**) | [workspace/new-branch.md](workspace/new-branch.md) |
| Switch Branch | (from Repositories **Branch**) | [workspace/switch-branch.md](workspace/switch-branch.md) |
| Update branch from default | (yellow divergence **behind** on a feature branch) | [workspace/update-branch-from-default.md](workspace/update-branch-from-default.md) |
| Incoming commits (Pull) | (red commits badge / header **Pull**) | [workspace/incoming-commits.md](workspace/incoming-commits.md) |
| Custom dependencies | (deps badge on Repositories row) | [workspace/custom-dependencies.md](workspace/custom-dependencies.md) |
| Protected branch and push failure | (commit on default, failed Push, undo, feature branch, PR) | [workspace/protected-branch-and-push-failure.md](workspace/protected-branch-and-push-failure.md) |
| Checkout from tags to main | (from Repositories **Branch** or row tag click) | [workspace/checkout-from-tags-to-main.md](workspace/checkout-from-tags-to-main.md) |
| New Feature | (from Repositories **Branch**) | [workspace/new-feature.md](workspace/new-feature.md) |
| Sync To Default | (from Repositories **Branch**) | [workspace/sync-to-default.md](workspace/sync-to-default.md) |
| Changes | `/workspaces/{id}/changes` | [workspace/changes.md](workspace/changes.md) |
| Projects | `/workspaces/{id}/projects` | [workspace/projects.md](workspace/projects.md) |
| Packages | `/workspaces/{id}/packages` | [workspace/packages.md](workspace/packages.md) |
| Files | `/workspaces/{id}/files` | [workspace/files.md](workspace/files.md) |
| Dependencies | `/workspaces/{id}/dependencies` | [workspace/dependencies.md](workspace/dependencies.md) |
| Actions | `/workspaces/{id}/actions` | [workspace/actions.md](workspace/actions.md) |

## Shared chrome

See [00-layout.md](00-layout.md) for sidebar collapse and top bar, and [shared.md](shared.md) for agent badge, filter search, toasts, loading overlay, reconnect dialog, and workspace action notification cards.
