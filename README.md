# GrayMoon

[![Docker Build](https://github.com/Jandini/GrayMoon/actions/workflows/docker-build.yml/badge.svg)](https://github.com/Jandini/GrayMoon/actions/workflows/docker-build.yml)

GrayMoon is a lightweight orchestration and monitoring UI for GitHub Actions and repository dependencies. It focuses on connector management, repository visibility, and workspace grouping to help organize and operate across multiple repositories.

## Features
- GitHub connector management with status checks
- Repository discovery and visibility tracking
- Workspace grouping with default workspace support
- Actions overview for selected repositories

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

| Option | Description |
|--------|-------------|
| `-p 8384:8384` | Exposes the app on port 8384 |
| `-v ./db:/app/db` | Persists the SQLite database in the `db` folder |
| `-v /mnt/c/workspaces:/workspaces` | Mounts the workspace root (WSL path to `C:\workspaces`) |
| `-e Workspace__RootPath=/workspaces` | Configures the workspace root inside the container |

Then open http://localhost:8384 in your browser.
