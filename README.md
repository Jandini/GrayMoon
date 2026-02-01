# GrayMoon

[![Docker Build](https://github.com/Jandini/GrayMoon/actions/workflows/docker-build.yml/badge.svg)](https://github.com/Jandini/GrayMoon/actions/workflows/docker-build.yml)

GrayMoon is a .NET project dependency and GitHub Actions orchestrator. It helps you push dependent NuGet packages in the right order, wait for GitHub Actions to publish new versions, and update package versions in project files. It provides connector management, repository visibility, and workspace grouping to organize and operate across multiple repositories.

## Features
- GitHub connector management with status checks
- Repository discovery and visibility tracking
- Workspace grouping with default workspace support
- Actions overview for selected repositories
- Background sync queue with controlled parallelism (up to 8 concurrent sync operations by default)

## Docker

### Build (from WSL)

Docker may not be available from PowerShell on Windows; use WSL to build:

```bash
wsl docker build -t jandini/graymoon:latest .
```

### Run

Basic run on port 8384:

```bash
docker run -p 8384:8384 jandini/graymoon:latest
```

Run with persistent database and workspace root (for WSL, mount Windows `C:\workspaces` as `/mnt/c/workspaces`):

```bash
docker run -p 8384:8384 \
  -v ./db:/app/db \
  -v /mnt/c/workspaces:/workspaces \
  -e Workspace__RootPath=/workspaces \
  jandini/graymoon:latest
```

```bash
wsl docker run -p 8384:8384 -v ./db:/app/db -v /mnt/c/workspaces:/workspaces -e Workspace__RootPath=/workspaces jandini/graymoon:latest
```


| Option | Description |
|--------|-------------|
| `-p 8384:8384` | Exposes the app on port 8384 |
| `-v ./db:/app/db` | Persists the SQLite database in the `db` folder |
| `-v /mnt/c/workspaces:/workspaces` | Mounts the workspace root (WSL path to `C:\workspaces`) |
| `-e Workspace__RootPath=/workspaces` | Configures the workspace root inside the container |

Then open http://localhost:8384 in your browser.

## Configuration

### Sync Queue

The sync queue processes repository sync requests in the background with controlled parallelism. Configure in `appsettings.json`:

```json
{
  "Sync": {
    "MaxConcurrency": 8,
    "EnableDeduplication": true
  }
}
```

Or via environment variables:

```bash
-e Sync__MaxConcurrency=8
-e Sync__EnableDeduplication=true
```

- `MaxConcurrency`: Number of parallel workers processing sync requests (default: 8)
- `EnableDeduplication`: Skip duplicate sync requests if same repo+workspace is already queued or being processed (default: true)

**API Endpoints:**
- `POST /api/sync` - Enqueue a sync request (called by git post-commit hooks)
- `GET /api/sync/queue` - Check queue status (returns pending request count)

**How it works:**
1. Git post-commit hooks call `POST /api/sync` with `repositoryId` and `workspaceId`
2. Requests are queued in an unbounded channel (duplicates are skipped if deduplication is enabled)
3. Background workers (default: 8) process requests concurrently
4. Each worker clones/updates repos and runs GitVersion to determine semantic version
5. Results are persisted to the database and broadcast via SignalR
6. After processing, the request is removed from the in-flight tracking (allows future syncs)
