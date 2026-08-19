# Getting started after a clean install

This walkthrough is for a **clean GrayMoon database**. The App is running (Docker on port 8384). There are no connectors, no catalog repositories, and no workspaces yet. The Agent is not installed; the top-bar badge is **offline**.

## Before GrayMoon can do real work

GrayMoon needs **two host-side prerequisites** before repository fetch, workspace clone, push, package checks, or GitHub Actions features work:

| Prerequisite | Why it matters |
| --- | --- |
| **Agent online** | The Agent runs git and filesystem work on your machine. The App never touches the disk. Without the Agent, nothing clones, fetches, or syncs. |
| **Working connectors** | GitHub and NuGet connectors hold the tokens GrayMoon uses at runtime. Each connector you rely on must be **Active** with status **OK**. Inactive or **Error** connectors block fetch, catalog import, synchronized push, and Actions. |

Install the Agent first (step 1). Add and test connectors next (step 2). Do not skip connector testing - a saved connector with **Error** or an inactive switch is the same as having no credentials.

See [Connectors](../04-connectors.md) for inactive vs **Error**, activation, and what happens when a test fails.

The Agent does every clone, fetch, and hook write on the host. Install the Agent first (Administrator PowerShell, and a user password for the Windows service). Later steps need the badge green **online**.

Workspace folders are created under **Settings -> Root Path** (here `C:\Workspace`). Add Workspace is disabled until that path is set. See [Settings](../06-settings.md).

## Steps

| Step | What you do | Doc |
| --- | --- | --- |
| 1 | Install the Agent Windows service (Administrator PowerShell) | [00 - Install the Agent](00-agent.md) |
| 2 | Add GitHub and NuGet connectors, activate them, and confirm **OK** status | [01 - Add connectors](01-connectors.md) |
| 3 | Fetch the GitHub catalog | [02 - Fetch repositories](02-fetch-repositories.md) |
| 4 | Create a workspace, pick repos, first **Sync** (clone) | [03 - Create workspace and clone](03-workspace-clone.md) |

After step 4 the clones sit as **sibling folders** under `{Root Path}\{WorkspaceName}` (here `C:\Workspace\Demo\TapeTools`, `C:\Workspace\Demo\MezzoRecovery`, ...). That is the main first-day benefit: one Sync clones every selected repository next to each other, instead of cloning each repo by hand.

Reference pages for the same screens: [Connectors](../04-connectors.md), [Repositories catalog](../03-repositories.md), [Workspaces](../02-workspaces.md), [workspace Repositories](../workspace/repositories.md).
