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

1. **Download** — On the home page, use **Download Agent**. The correct executable (Windows or Linux) is chosen from your browser.
2. **Run on the host** — Run the agent on the same machine (or network) as your repositories, e.g. as a console app or Windows/Linux service.

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

