# GrayMoon

[![Docker Build](https://github.com/Jandini/GrayMoon/actions/workflows/docker-build.yml/badge.svg)](https://github.com/Jandini/GrayMoon/actions/workflows/docker-build.yml)

GrayMoon is a lightweight orchestration and monitoring UI for GitHub Actions and repository dependencies. It focuses on connector management, repository visibility, and workspace grouping to help organize and operate across multiple repositories.

## Features
- GitHub connector management with status checks
- Repository discovery and visibility tracking
- Workspace grouping with default workspace support
- Actions overview for selected repositories

## Local Development
1. Open the solution `GrayMoon.sln` in your IDE.
2. Run `GrayMoon.App` and browse to the configured port.

## Docker
Build the image:
```
docker build -t graymoon:local .
```

Run the container:
```
docker run -p 8384:8384 graymoon:local
```
