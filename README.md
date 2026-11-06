# GrayMoon

[![Docker Build](https://github.com/Jandini/GrayMoon/actions/workflows/docker-build.yml/badge.svg)](https://github.com/Jandini/GrayMoon/actions/workflows/docker-build.yml)
[![Wiki](https://img.shields.io/badge/docs-wiki-blue)](https://github.com/Jandini/GrayMoon/wiki)

AI coding does not stay in one repository. A feature lands in a library, in the services that use it, and in the docs, often in the same hour. GrayMoon is the workspace for that work.

You review the whole feature in one place. You commit it as one change across the repositories that moved. You open a pull request whose description is about the system, not about the small edits inside a single repo.

## From the author

GrayMoon is where the feature lives. I branch every repository it touches, review the code and the documentation as one change, and commit that feature across them. I open the pull requests from here, with a description of the change to the system, and I merge them in one pass. When a shared package has to move with the feature, GrayMoon updates and pushes the repositories from the lowest dependency level up.

A feature is developed across repositories at the same time. The commit is that feature. The pull request describes that feature. Neither one is a pile of per-repo fragments.

## One feature, across every repository

A **Feature** is a branch and a worktree in every repository, taken from the current workspace. The context selector switches between the Workspace and its Features, so more than one feature can be open at once.

**Changes** is one tree. Staged and unstaged files from every repository are there, with the diff. Markdown can be read as Preview, so the documentation is reviewed beside the code.

The commit covers the feature. One message, for every repository that changed. **Generate commit message** can draft it from the Claude, Codex, or Cursor Agent CLI already on the machine.

The pull request covers the feature too. **Create PR** opens it in each repository that is ahead. The title and the description explain the change to the system. **Merge PRs...** merges them from the grid. The method starts as Squash. The same step can return the repositories to the default branch.

A **.NET Dependency** workspace orders repositories into levels and pushes a shared package update from the lowest level up, waiting for each package to appear on the feed. A **Basic** workspace is git only. The App orchestrates. The Worker on your machine does the git and the files.

## How it looks

| The repositories, in dependency order | The feature, as one diff |
| --- | --- |
| ![Workspace repositories](https://raw.githubusercontent.com/wiki/Jandini/GrayMoon/screenshots/workspace2-repositories-on-main.png) | ![Git Changes diff](https://raw.githubusercontent.com/wiki/Jandini/GrayMoon/screenshots/changes-diff.png) |

| The dependency graph | The documentation, as Preview |
| --- | --- |
| ![Dependency graph](https://raw.githubusercontent.com/wiki/Jandini/GrayMoon/screenshots/dependencies.png) | ![Markdown Preview in Changes](https://raw.githubusercontent.com/wiki/Jandini/GrayMoon/screenshots/changes-preview.png) |

| Version drift | Push Updated |
| --- | --- |
| ![Version drift badges](https://raw.githubusercontent.com/wiki/Jandini/GrayMoon/screenshots/workspace2-deps-out-of-date-after-tag-upgrade.png) | ![Push Updated in progress](https://raw.githubusercontent.com/wiki/Jandini/GrayMoon/screenshots/workspace2-tape-density-overlay-waiting-packages.png) |

![GitHub Actions](https://raw.githubusercontent.com/wiki/Jandini/GrayMoon/screenshots/workspace2-actions-running.png)

## Run

Desktop and Docker never share one App.

**Desktop (Windows).** Download the installer from [Jandini/GrayMoon.Release](https://github.com/Jandini/GrayMoon.Release) and launch GrayMoon. Desktop starts the App on `http://127.0.0.1:8384` in its own window and stays on the update channel it was built for.

**Docker** is the web app in a browser. The Worker is still installed on the host.

```bash
docker run -d --restart unless-stopped --name graymoon -p 8384:8384 -v graymoon:/app/db jandini/graymoon:latest
```

Open `http://localhost:8384` and install the Worker from the Worker page. The walkthrough is [Getting Started](https://github.com/Jandini/GrayMoon/wiki/Getting-Started).

## Documentation

The [wiki](https://github.com/Jandini/GrayMoon/wiki) is the guide: [Home](https://github.com/Jandini/GrayMoon/wiki), [Getting Started](https://github.com/Jandini/GrayMoon/wiki/Getting-Started), [Library Update](https://github.com/Jandini/GrayMoon/wiki/Feature-Walkthrough-Library-Update), and [Tape Density](https://github.com/Jandini/GrayMoon/wiki/Feature-Walkthrough-Tape-Density).

[docs/architecture](docs/architecture/README.md) describes the current behavior. Build commands and coding rules are in [CLAUDE.md](CLAUDE.md) and [AGENTS.md](AGENTS.md).

## License

GrayMoon is dual-licensed: free for personal, educational, and evaluation use under [LICENSE-NON-COMMERCIAL.txt](LICENSE-NON-COMMERCIAL.txt), with a commercial license under [LICENSE-COMMERCIAL.txt](LICENSE-COMMERCIAL.txt). See [graymoon.io](https://graymoon.io/).
