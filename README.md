# GrayMoon

**One workspace. Every repository. Always in sync.**

[![Docker Build](https://github.com/Jandini/GrayMoon/actions/workflows/docker-build.yml/badge.svg)](https://github.com/Jandini/GrayMoon/actions/workflows/docker-build.yml)
[![Wiki](https://img.shields.io/badge/docs-wiki-blue)](https://github.com/Jandini/GrayMoon/wiki)

If your .NET solution has spread across a dozen GitHub repositories - services, shared libraries, NuGet packages, all versioned with **GitVersion** - GrayMoon is the missing control plane. It clones, branches, updates, and pushes across every repository in a workspace as one coordinated action, in the correct dependency order, so a single feature or a single package rollout doesn't turn into an afternoon of repo-by-repo bookkeeping.

It is also built for how teams work today: fast, parallel, AI-assisted development across many small repositories instead of one giant monolith.

![GrayMoon highlights](https://raw.githubusercontent.com/wiki/Jandini/GrayMoon/screenshots/graymoon-highlights.gif)

## Why teams pick GrayMoon

- **Built for AI-assisted, multi-repo development.** Short-lived feature branches across several repositories at once are exactly the workflow AI coding agents create - GrayMoon is what keeps them (and you) coordinated instead of drowning in open terminals.
- **One coordinated workspace, not a dozen open folders.** Group related repositories into a workspace; GrayMoon clones them side by side and keeps branch, version, and sync status live for all of them at once.
- **Dependency-aware, always.** Repositories are sorted into dependency levels from `.csproj` references, version-file tokens, and your own declared edges - so updates, pushes, and merges always happen lowest-level-first.
- **Branch across every repo in one click.** Create or switch a feature branch on every workspace repository together - no more hunting for which repo you forgot to check out.
- **Safe, synchronized package rollouts.** When a shared package changes, GrayMoon bumps `PackageReference` versions, forces a clean `dotnet restore`, commits, and pushes level by level - waiting for NuGet to catch up so downstream builds never race a package that isn't published yet.
- **GitHub Actions status across the whole workspace.** One grid shows every workflow, on the current branch, for every repository - filter by status, re-run a failed job, or tail live logs without opening GitHub.
- **Pull requests at scale.** Open PRs for one repository, a whole dependency level, or the entire workspace from a single dialog - reviewers and draft mode included. Merge approved pull requests from the repository grid with live GitHub checks, approvals, and conflict status - no round trip to github.com.

## How it looks

| Repository grid by dependency level | Git Changes across repositories |
| --- | --- |
| ![Workspace repositories](https://raw.githubusercontent.com/wiki/Jandini/GrayMoon/screenshots/workspace2-repositories.png) | ![Git Changes diff viewer](https://raw.githubusercontent.com/wiki/Jandini/GrayMoon/screenshots/workspace-changes-md-diff.png) |

| Dependency graph | GitHub Actions across repositories |
| --- | --- |
| ![Dependency graph](https://raw.githubusercontent.com/wiki/Jandini/GrayMoon/screenshots/workspace2-dependencies.png) | ![GitHub Actions status grid](https://raw.githubusercontent.com/wiki/Jandini/GrayMoon/screenshots/workspace2-actions-filters.png) |

| Version drift, at a glance | Push with dependency update |
| --- | --- |
| ![Version drift badges](https://raw.githubusercontent.com/wiki/Jandini/GrayMoon/screenshots/workspace2-deps-out-of-date-after-tag-upgrade.png) | ![Push Updated in progress](https://raw.githubusercontent.com/wiki/Jandini/GrayMoon/screenshots/workspace2-tape-density-overlay-waiting-packages.png) |

See the full tour, with real screenshots from a running instance, in the **[GrayMoon wiki](https://github.com/Jandini/GrayMoon/wiki)**.

## Quick start

GrayMoon is two small pieces - keep both running:

- **GrayMoon App** - the web UI and orchestration engine, runs in Docker.
- **GrayMoon Agent** - a lightweight service on your machine that does the actual git and filesystem work. The App never touches your disk directly.

```bash
docker run -d --restart unless-stopped --name graymoon -p 8384:8384 -v graymoon:/app/db jandini/graymoon:latest
```

Open `http://localhost:8384`, install the Agent from the **Agent** page (one PowerShell command), add a GitHub connector, and you're cloning your first workspace in minutes.

![PowerShell Agent install](https://raw.githubusercontent.com/wiki/Jandini/GrayMoon/screenshots/agent-install.gif)

Full walkthrough, from empty install to a working workspace: **[Getting Started](https://github.com/Jandini/GrayMoon/wiki/Getting-Started)**.

## Documentation

Everything else - every page, every workflow, every real-world walkthrough - lives in the **[GrayMoon wiki](https://github.com/Jandini/GrayMoon/wiki)**, including two complete end-to-end examples on an 11-repository sample project:

- **[Library Update](https://github.com/Jandini/GrayMoon/wiki/Feature-Walkthrough-Library-Update)** - a coordinated dependency rollout, including recovering from a partial push.
- **[Tape Density](https://github.com/Jandini/GrayMoon/wiki/Feature-Walkthrough-Tape-Density)** - a real feature shipped end to end across a multi-repo dependency graph.

## License

GrayMoon is dual-licensed: free for personal, educational, and evaluation use under [LICENSE-NON-COMMERCIAL.txt](LICENSE-NON-COMMERCIAL.txt), with a commercial license available under [LICENSE-COMMERCIAL.txt](LICENSE-COMMERCIAL.txt) for business use. See [graymoon.io](https://graymoon.io/) for details.


