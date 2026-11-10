# GrayMoon

[![Docker Build](https://github.com/Jandini/GrayMoon/actions/workflows/docker-build.yml/badge.svg)](https://github.com/Jandini/GrayMoon/actions/workflows/docker-build.yml)
[![Wiki](https://img.shields.io/badge/docs-wiki-blue)](https://github.com/Jandini/GrayMoon/wiki)

GrayMoon helps teams work across many repositories as one coordinated workspace: clone related repositories together, branch across all of them at once, and carry one feature through commit, push, and pull request as a single change.

AI coding does not stay in one repository. A feature lands in a library, in the services that use it, and in the docs, in the same time. GrayMoon is the repo orchestrator that keeps it together: one place to review it, one commit across the repositories that moved, one pull request whose description is about the system.

## One feature, across every repository

A feature is developed across repositories at the same time. The commit is that feature. The pull request describes that feature. Neither one is a pile of per-repo fragments.

A **Feature** is a branch and a worktree in every repository, taken from the current workspace. The context selector switches between the Workspace and its Features, so more than one feature can be open at once. GrayMoon can also build a [CodeGraph](https://github.com/Jandini/GrayMoon/wiki/Workspace-Features#codegraph-on-new-feature) index for a new Feature, so an AI coding agent has accurate cross-repository context from the first commit.

**Changes** is one tree. Staged and unstaged files from every repository are there, with the diff. Markdown can be read as Preview, so the documentation is reviewed beside the code.

The commit covers the feature. One message, for every repository that changed. **Generate commit message** can write it with the Claude, Codex, or Cursor Agent CLI already on the machine, reading only bounded, read-only git evidence.

The pull request covers the feature too. **Create PR** opens it in each repository that is ahead. **Write the title and description** uses the same AI providers to summarize the whole branch, not just the latest commits. **Merge PRs...** merges them from the grid. The method starts as Squash. The same step can return the repositories to the default branch.

When a shared package has to move with the feature, a **.NET Dependency** workspace orders repositories into levels and pushes the update from the lowest level up, waiting for each package to appear on the feed. A **Basic** workspace is git only. The App orchestrates. The Worker on your machine does the git, the files, and the AI CLI calls.

## Plain repositories, portable workspace

A workspace is a set of ordinary git repositories, cloned side by side on disk - never submodules. No nested `.git`, no pointer commits to keep in sync, no detached-HEAD surprises. Every repository works the same in GrayMoon, your IDE, or a plain terminal.

The workspace itself can be a git repository too, carrying its own `graymoon.json`: which repositories belong to it, the workspace profile, and the configured version files. **Restore from repository** reads that file and clones the whole workspace - every repository, set up and ready - in a few clicks. Moving your work to another machine, or bringing a teammate up to the same workspace, takes no time: point GrayMoon at the repository and Restore.

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

Open `http://localhost:8384` and install the Worker from the Worker page. The install script asks for the one-time pairing code shown there; a Worker that is not paired with this GrayMoon is refused. The walkthrough is [Getting Started](https://github.com/Jandini/GrayMoon/wiki/Getting-Started).

Out of the box the container accepts browser pages only from the machine it runs on (`localhost`). To open GrayMoon from another machine, list every host name or address people type in the browser in `GRAYMOON_AllowedHosts`, separated by `;`, and keep `localhost` in the list:

```bash
docker run -d --restart unless-stopped --name graymoon -p 8384:8384 -v graymoon:/app/db -e "GRAYMOON_AllowedHosts=localhost;nas.local;192.168.1.10" jandini/graymoon:latest
```

Without it a page opened under another name loads but cannot connect (the log has one warning that names the variable). A host that is not in the list is answered with 400 once the variable is set.

## Documentation

The [wiki](https://github.com/Jandini/GrayMoon/wiki) is the guide: [Home](https://github.com/Jandini/GrayMoon/wiki), [Getting Started](https://github.com/Jandini/GrayMoon/wiki/Getting-Started), [AI commit and pull request text](https://github.com/Jandini/GrayMoon/wiki/AI-Commit-and-Pull-Request-Text), [Library Update](https://github.com/Jandini/GrayMoon/wiki/Feature-Walkthrough-Library-Update), and [Tape Density](https://github.com/Jandini/GrayMoon/wiki/Feature-Walkthrough-Tape-Density).

[docs/architecture](docs/architecture/README.md) describes the current behavior. Build commands and coding rules are in [CLAUDE.md](CLAUDE.md) and [AGENTS.md](AGENTS.md).

## License

GrayMoon is dual-licensed: free for personal, educational, and evaluation use under [LICENSE-NON-COMMERCIAL.txt](LICENSE-NON-COMMERCIAL.txt), with a commercial license under [LICENSE-COMMERCIAL.txt](LICENSE-COMMERCIAL.txt). See [graymoon.io](https://graymoon.io/).
