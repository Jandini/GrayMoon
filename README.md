# GrayMoon

[![Docker Build](https://github.com/Jandini/GrayMoon/actions/workflows/docker-build.yml/badge.svg)](https://github.com/Jandini/GrayMoon/actions/workflows/docker-build.yml)

.NET dependency orchestrator: connector management, repository visibility, workspace grouping, and sync with GitVersion.

## Features

- GitHub connector management and status checks
- Repository discovery and workspace grouping (with default workspace)
- Actions overview for selected repositories
- Background sync queue (concurrency, deduplication)

## Docker

Build (use WSL on Windows if Docker isn’t available from PowerShell):

```bash
docker build -t jandini/graymoon:latest .
```

Run on port 8384; optional volume to persist the SQLite database:

```bash
docker run -p 8384:8384 -v ./db:/app/db jandini/graymoon:latest
```

Open http://localhost:8384 in your browser.

## Agent

The app runs in a container and does not run git or access your workspace filesystem. A **host-side agent** does that: it runs on the machine where your repos live, connects to the app via SignalR, and runs git, GitVersion, and workspace I/O.

1. **Download** — On the home page, use **Download Agent**. The correct executable (Windows or Linux) is chosen from your browser.
2. **Run on the host** — Run the agent on the same machine (or network) as your repositories, e.g. as a console app or Windows/Linux service.

When the agent is connected, the app shows an **online** badge; sync and repo operations use the agent. When it’s offline, sync fails with a clear message. 
