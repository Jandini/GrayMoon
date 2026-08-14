# Getting started after a clean install

This walkthrough is for a **clean GrayMoon database** where the **Agent is already installed** and showing **online** in the top bar. There are no connectors, no catalog repositories, and no workspaces yet.

The Agent does every clone, fetch, and hook write on the host. The App never touches the disk. If the Agent badge is not green **online**, finish [Agent install](../05-agent.md) first.

Workspace folders are created under **Settings -> Root Path** (here `C:\Workspace`). Add Workspace is disabled until that path is set. See [Settings](../06-settings.md).

## Steps

| Step | What you do | Doc |
| --- | --- | --- |
| 1 | Add GitHub and NuGet connectors, then test them | [01 - Add connectors](01-connectors.md) |
| 2 | Fetch the GitHub catalog | [02 - Fetch repositories](02-fetch-repositories.md) |
| 3 | Create a workspace, pick repos, first **Sync** (clone) | [03 - Create workspace and clone](03-workspace-clone.md) |

After step 3 the clones sit as **sibling folders** under `{Root Path}\{WorkspaceName}` (here `C:\Workspace\Demo\TapeTools`, `C:\Workspace\Demo\MezzoRecovery`, ...). That is the main first-day benefit: one Sync clones every selected repository next to each other, instead of cloning each repo by hand.

Reference pages for the same screens: [Connectors](../04-connectors.md), [Repositories catalog](../03-repositories.md), [Workspaces](../02-workspaces.md), [workspace Repositories](../workspace/repositories.md).
