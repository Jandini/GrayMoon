# GrayMoon

[![Docker Build](https://github.com/Jandini/GrayMoon/actions/workflows/docker-build.yml/badge.svg)](https://github.com/Jandini/GrayMoon/actions/workflows/docker-build.yml)

.NET multi-repository orchestration tool for teams building with microservices and shared packages.

GrayMoon helps you work across many repositories as one coordinated workspace:

- clone and organize multiple repositories under one workspace
- create/switch feature branches across repositories
- keep repository status and activity up to date while you work in your IDE (checkout/commit/push/merge events are tracked)
- track versions and dependency drift
- update `PackageReference` versions automatically
- one-click sync back to default branch after merge (single repo or multiple repos by dependency level)
- synchronize pushes to reduce CI failures caused by missing dependency versions
- group repositories by dependency levels and type (services vs packages/libraries) for easier dependency visualization
- when version alignment is required, GrayMoon can update the impacted `PackageReference` versions and commit those changes as part of the same coordinated workflow

If a shared package changes and dozens of repositories need updates, GrayMoon can handle the rollout flow in one place.

## Who GrayMoon is for

GrayMoon is useful for:

- .NET teams with multiple services and shared libraries/packages
- teams that use semantic versioning with GitVersion
- developers doing cross-repository feature work
- teams using AI IDEs (VS Code/Cursor) where cross-repo context improves productivity

## Why teams use GrayMoon

- You get one view of branch/version/PR/action status across repositories.
- You can see GitHub Actions results for the current branch across all repositories in a workspace.
- You can open multiple repositories (and multiple new PR pages by dependency level) in GitHub with one action.
- You can run coordinated dependency updates instead of editing each repo manually.
- You can create one feature branch (for example `feature/dependency-update`) across repos, auto-update package refs, auto-commit, and push together.
- When rolling out changes, GrayMoon can push multiple repositories in parallel so the full workflow finishes faster than pushing repo-by-repo manually.
- GrayMoon tracks changed files across repositories so you can avoid unnecessary PRs when changes are only dependency-version updates.

## Quick Start

GrayMoon runs as two parts:

- **GrayMoon App** in Docker (web UI + orchestration API)
- **GrayMoon Agent** on your host machine as a service/process (executes git and filesystem operations in your repositories)

Keep both running for full functionality.

## Docker

Run on port `8384`; optional volume to persist the SQLite database:

```bash
docker run -d --restart unless-stopped --name graymoon -p 8384:8384 -v graymoon:/app/db jandini/graymoon:latest
```

To override the default token encryption key (recommended for non-dev use), set `TokenKey` to any password-like value (it will be hashed into a key automatically). Advanced users can also pass a Base64-encoded key if they prefer:

```bash
docker run -d --restart unless-stopped --name graymoon -p 8384:8384 -v graymoon:/app/db -e TokenKey="my strong password here" jandini/graymoon:latest
```

Open http://localhost:8384 in your browser.



Update running container:

```sh
docker pull jandini/graymoon:latest && docker stop graymoon && docker rm graymoon && docker run -d --restart unless-stopped --name graymoon -p 8384:8384 -v graymoon:/app/db jandini/graymoon:latest
```

Update running container with token encryption key:

```sh
docker pull jandini/graymoon:latest && docker stop graymoon || true && docker rm graymoon || true && docker run -d --restart unless-stopped --name graymoon -p 8384:8384 -v graymoon:/app/db -e TokenKey="my strong password here" jandini/graymoon:latest
```



## Agent

The app runs in a container and does not run git or access your workspace filesystem. A **host-side agent** does that: it runs on the machine where your repos live, connects to the app via SignalR, and runs git, GitVersion, and workspace I/O.

1. **Download** - On the home page, use **Download Agent**. Your browser gets a **zip** of the framework-dependent build (Windows or Linux): extract it and run `graymoon-agent` / `graymoon-agent.exe`. The host needs the **.NET 8 runtime** installed for that RID.
2. **Run on the host** - Run the agent on the same machine (or network) as your repositories, e.g. as a console app or Windows/Linux service.

When the agent is connected, the app shows an **online** badge; sync and repository operations use the agent.

On the **Agent** page, GrayMoon provides an **Install/Upgrade** command you can run on the host (PowerShell), for example:

```powershell
Set-ExecutionPolicy Bypass -Scope Process -Force; [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072; iex ((New-Object System.Net.WebClient).DownloadString('http://localhost:8384/api/agent/install'))
```

## Typical workflow

1. Configure connectors and fetch repositories.
2. Create a workspace and select the repositories needed for a feature.
3. Clone/check out repositories into the workspace.
4. Create or switch a branch across selected repositories.
5. Run dependency/version updates (GrayMoon updates the impacted `PackageReference` versions for you when needed).
6. Continue working in your IDE while GrayMoon tracks changes in the background; GrayMoon UI does not need to stay open as long as the Docker app and agent services are running.

## What's new

### Pin a repository to a specific tag

The Switch Branch dialog now has a dedicated **Tags** tab. Pick any tag and GrayMoon will check that repository out at that exact version (detached HEAD), then keep it parked there.

- **What it gives you:** a clean way to freeze one consumer on a known-good release of a shared package while the rest of the workspace keeps moving forward, without forking a branch just to hold the pin.
- **Tags are listed newest first** (creator-date descending) both when freshly fetched and when reloaded from the local cache, so the latest release is always at the top.
- **The grid clearly marks pinned repositories:** the Branch column shows a tag icon and the tag name; the Divergence, Commits, and PR columns intentionally render blank (a tag has no upstream and no branch-relative diff, so showing "0" or "-" would be misleading).
- **All write actions are blocked for pinned repositories** - Update, Push, Pull, Commit Sync, and Sync-to-Default - both from the per-row buttons and from the level-header actions. You get a clear toast explaining why; nothing is silently skipped. To resume normal work on that repo, just check out a branch again from the same dialog.
- **No more confusing "Push to set upstream" hint** when a repo is on a detached HEAD - GrayMoon now recognizes the tag state end-to-end (agent, database, UI) instead of treating it as a branch without upstream.

### Richer dependency badge tooltip

The dependency count badge in the grid now opens a detailed popup when you hover it - both when there are updates to apply (orange badge, "x of y") and when everything is already aligned (green badge).

- **What it gives you:** see exactly which workspace-internal packages a repository pulls in and at which version, without opening the dependency graph or the .csproj files.
- **Up-to-date case (green badge):** lists every internal package dependency with its current version. Clicking the badge still opens the dependency graph as before.
- **Mismatch case (orange badge):** lists every package that needs updating with `CurrentVersion -> NewVersion`. Clicking the badge still triggers the per-repository update flow.
- **Copy to clipboard:** the popup has a small clipboard icon in the top-right corner that copies the list (prefixed with the repository name) so you can paste it into a PR description, chat, or ticket. The copy action does not trigger the badge's main click.
- **Pinned repositories** show the tooltip and the Copy button but the badge itself is non-clickable - exactly what you want when the repo is intentionally frozen.

