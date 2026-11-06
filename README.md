# GrayMoon

[![Docker Build](https://github.com/Jandini/GrayMoon/actions/workflows/docker-build.yml/badge.svg)](https://github.com/Jandini/GrayMoon/actions/workflows/docker-build.yml)
[![Wiki](https://img.shields.io/badge/docs-wiki-blue)](https://github.com/Jandini/GrayMoon/wiki)

GrayMoon is one workspace for a set of Git repositories. The App is the UI and the orchestrator. The Worker, on your machine, does the git and filesystem work. The App does not touch the disk.

A **.NET Dependency** workspace reads project references, orders the repositories into levels, and updates, commits, and pushes shared packages from the lowest level up. A **Basic** workspace is git only: branches, Changes, and pull requests, without package levels.

## What you can do

Related repositories sit side by side. GrayMoon clones them and keeps branch, version, and sync status for all of them.

**Branch.** Create or switch a branch on every repository together. **Prepare Workspace** creates that branch across the workspace, and can then update dependencies and push.

**Features.** A Feature is a git worktree of every repository, on a branch of the same name, taken from the current Workspace branch. The context selector switches between the Workspace and its Features, so more than one cross-repository change can be open at once. **New Feature** can build a CodeGraph index for that Feature. **Remove Feature** checks for unsaved work, then deletes the worktrees.

**Changes.** One tree shows staged and unstaged files for the whole workspace, with a diff. A Markdown file can show Source or Preview (Changes, After, Before). One Commit commits the staged files or everything. **Generate commit message** fills the message from the Claude, Codex, or Cursor Agent CLI on the machine. A provider that is not installed is left out. The same control writes a pull-request title and description.

**Packages.** Levels come from `.csproj` references, version-file tokens, and edges you declare. **Push Updated** rewrites package versions, including versions kept in `Directory.Packages.props`, commits them, and pushes by level. A level can push while the next level is still updating. GrayMoon then waits, 5 minutes per required package, for that package to show up on the feed. Restore is separate: **Restore** on the Sync menu, or the restore option on the update dialog. Push Updated from the notification card does not restore.

**Pull requests.** **Create PR** opens a pull request for each repository that is ahead and does not already have one. **Merge PRs...** merges from the grid. The method starts as Squash, and you can choose another. The same dialog can return repositories to the default branch. Check status stays on the row.

**Actions.** One grid shows GitHub Actions for the current branch, across the workspace.

**Definition.** The workspace folder can itself be a Git repository. `graymoon.json` stores the repositories, the profile, and the version files, so another machine can restore the same workspace. The display name stays on this computer.

## How it looks

| Repository grid | Git Changes |
| --- | --- |
| ![Workspace repositories](https://raw.githubusercontent.com/wiki/Jandini/GrayMoon/screenshots/workspace2-repositories.png) | ![Git Changes diff viewer](https://raw.githubusercontent.com/wiki/Jandini/GrayMoon/screenshots/workspace-changes-md-diff.png) |

| Dependency graph | GitHub Actions |
| --- | --- |
| ![Dependency graph](https://raw.githubusercontent.com/wiki/Jandini/GrayMoon/screenshots/workspace2-dependencies.png) | ![GitHub Actions status grid](https://raw.githubusercontent.com/wiki/Jandini/GrayMoon/screenshots/workspace2-actions-filters.png) |

| Version drift | Push Updated |
| --- | --- |
| ![Version drift badges](https://raw.githubusercontent.com/wiki/Jandini/GrayMoon/screenshots/workspace2-deps-out-of-date-after-tag-upgrade.png) | ![Push Updated in progress](https://raw.githubusercontent.com/wiki/Jandini/GrayMoon/screenshots/workspace2-tape-density-overlay-waiting-packages.png) |

![Markdown Preview in Changes](https://raw.githubusercontent.com/wiki/Jandini/GrayMoon/screenshots/workspace-changes-md-preview.png)

## Run

Desktop and Docker never share one App.

**Desktop (Windows).** Download the installer from [Jandini/GrayMoon.Release](https://github.com/Jandini/GrayMoon.Release) and launch GrayMoon. Desktop starts the App on `http://127.0.0.1:8384` in its own window. An installed app stays on the channel it was built for, and checks that channel for updates.

**Docker** is the web app in a browser. The Worker is still installed on the host.

```bash
docker run -d --restart unless-stopped --name graymoon -p 8384:8384 -v graymoon:/app/db jandini/graymoon:latest
```

Open `http://localhost:8384` and install the Worker from the Worker page. The walkthrough is [Getting Started](https://github.com/Jandini/GrayMoon/wiki/Getting-Started).

## Documentation

The [wiki](https://github.com/Jandini/GrayMoon/wiki) is the user guide: [Home](https://github.com/Jandini/GrayMoon/wiki), [Getting Started](https://github.com/Jandini/GrayMoon/wiki/Getting-Started), [Library Update](https://github.com/Jandini/GrayMoon/wiki/Feature-Walkthrough-Library-Update), and [Tape Density](https://github.com/Jandini/GrayMoon/wiki/Feature-Walkthrough-Tape-Density).

[docs/architecture](docs/architecture/README.md) describes the current behavior. Build commands and coding rules are in [CLAUDE.md](CLAUDE.md) and [AGENTS.md](AGENTS.md).

## License

GrayMoon is dual-licensed: free for personal, educational, and evaluation use under [LICENSE-NON-COMMERCIAL.txt](LICENSE-NON-COMMERCIAL.txt), with a commercial license under [LICENSE-COMMERCIAL.txt](LICENSE-COMMERCIAL.txt). See [graymoon.io](https://graymoon.io/).
