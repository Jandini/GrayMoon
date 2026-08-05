# GrayMoon

[![Docker Build](https://github.com/Jandini/GrayMoon/actions/workflows/docker-build.yml/badge.svg)](https://github.com/Jandini/GrayMoon/actions/workflows/docker-build.yml)

.NET multi-repository orchestration tool for teams building with microservices and shared packages.

GrayMoon helps you work across many repositories as one coordinated workspace:

- clone and organize multiple repositories under one workspace
- create/switch feature branches across repositories
- keep repository status and activity up to date while you work in your IDE (checkout/commit/push/merge events are tracked)
- track versions and dependency drift
- update `PackageReference` versions automatically
- run `dotnet restore --force --no-cache` so NuGet does not fall back to stale cached packages after dependency changes
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
- You can create pull requests across one, several, or all workspace repositories from one dialog, with optional reviewers and draft support.
- You can run coordinated dependency updates instead of editing each repo manually.
- After package refs change, GrayMoon can force a fresh NuGet restore so local builds match the versions you just rolled out (not what happened to be cached).
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

1. **Download** - On the home page, use **Download Agent**. Your browser gets a **zip** of the framework-dependent build (Windows or Linux): extract it and run `graymoon-agent` / `graymoon-agent.exe`. The host needs the **.NET 10 runtime** installed for that RID.
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

### Git Changes refresh keeps running when you leave the page

Workspace **Changes** refresh now uses the same circuit-scoped background job pipeline as **Repositories**, so a scan is not cancelled when you navigate away.

- **Empty workspace ("No changes"):** Refresh (and the automatic warm-up scan when you first open Changes for an inactive workspace) runs as a non-overlay background job. The page shows an inline spinner with progress (`Refreshing x of y repositories...`) and an **Abort** button. Leave the page and the scan continues; come back while it is still running and the spinner returns.
- **When there are already changes:** Refresh still uses the standard LoadingOverlay / terminal job for that Changes URL, so you can navigate away and see the overlay again when you return.
- **No header subtitle status:** the old warm-up-only subtitle next to the Changes title is gone - progress for empty/warm-up scans lives in the empty-state UI (or updates the tree silently when changes are already showing).

### Fixed: navigating to Changes no longer aborts running jobs or breaks the diff viewer

Two related navigation bugs in the Git Changes page are fixed.

- **Background jobs on Repositories no longer abort when you open Changes.** Starting an Update, Push, Sync, or similar operation on the workspace **Repositories** page and then navigating to **Changes** used to kill the job partway through (for example, an Update-then-Push run would never reach the push phase) because the still-running job called back into page state that had already been torn down. The job now keeps running to completion in the background regardless of which page you are on, exactly as intended.
- **The Monaco diff viewer no longer breaks after leaving and returning to Changes.** Syntax highlighting disappearing, scrolling behaving erratically, and a tiny stray resizable box appearing near the top of the diff were all the same root cause: Monaco injects its own styling at runtime, and Blazor's navigation was silently discarding it every time you navigated away from and back to the Changes page. That styling is now preserved across navigation, so the diff viewer looks and behaves the same whether it is the first file you have opened or the fifth time you have returned to the page.

### Enterprise GitHub link support, dependency-count fixes, and workspace repositories code cleanup

- **Works with GitHub Enterprise Server (GHES):** repository web links, owner/repo parsing, and pull-request URLs are no longer hardcoded to `github.com`. `RepositoryUrlHelper` now derives the host from any `git@host:...` SSH URL or `https://host/...` HTTPS URL, so clone links, PR pages, and "Open in GitHub" actions work correctly against GHES instances as well as github.com.
- **Fixed "1 of 0 dependencies require update":** the dependency graph builder could count a project referencing another project inside the *same* repository, or a project outside the current workspace, as an unmatched dependency. Both are now excluded, so the notification badge count matches the dependencies actually listed in the popup.
- **`WorkspaceRepositories.razor.cs` split into partials:** the ~4,100-line code-behind file was split into focused partial classes (`.Branches`, `.CommitSync`, `.Dependencies`, `.FileVersions`, `.NewFeature`, `.Push`, `.PullRequests`, `.Realtime`, `.Search`, `.State`, `.Sync`, `.SyncToDefault`, `.UndoPush`, `.Update`, and more) for maintainability - no behavior change for users.

### File version config validation

The version configuration dialog now validates pattern lines against the actual file on disk and highlights any that cannot be matched.

- **On dialog open:** as soon as the dialog appears, GrayMoon calls the agent to scan the file and checks which configured pattern lines exist. Results appear within a second.
- **While editing:** after 2 seconds of inactivity in the textarea, the check reruns automatically so you get immediate feedback without any button click.
- **Red badges for missing lines:** token names whose pattern line is not found in the file appear as red badges below the textarea under "Not found in file:". Tokens that ARE found (even with a wrong version) are not flagged here.
- **"Checking file..." indicator:** a small muted label appears while the agent call is in flight so you know validation is running.

### Missing file lines no longer counted as out of date

When a configured version pattern line does not exist in the file (for example a `PackageReference` was removed from a `.csproj` or a variable was dropped from a `.env`), GrayMoon previously counted it as out of date and showed it in the dependency tooltip as `-> expected-version` with nothing on the left. Those absent lines are now silently ignored: they do not contribute to the "X of Y" out-of-date badge count and produce no row in the file dependency tooltip. Only lines that actually exist in the file but carry a different version from the expected one are reported as out of date.


### One-click agent update from the badge

When the agent version does not match the app, the header badge shows a red **update** label. Clicking it now triggers a self-update directly - no need to visit the Agent page and copy a command.

- **How it works:** clicking the badge sends a `SelfUpdate` command to the running agent over SignalR. The agent launches the install script in a fully detached process (`cmd.exe /c start /b powershell.exe -NoProfile -NonInteractive -Command "irm .../api/agent/install | iex"`), which stops the old service, downloads the new binary, and restarts the service.
- **Badge changes to blue "updating"** immediately after the command is dispatched and stays there while the service restarts.
- **Returns to green "online"** automatically once the new agent connects and its version matches.
- **On failure:** if the command cannot be dispatched (agent not connected, process failed to start), a toast shows **Agent update failed.** and the app navigates to the Agent page where you can run the command manually.

### No more red "fatal" noise in the terminal during New Feature runs

The upstream tracking probe used by commit-count and fetch operations now uses `git for-each-ref --format=%(upstream:short)` instead of `git rev-parse @{u}`. The old command wrote `fatal: no upstream configured for branch '<name>'` to stderr every time GrayMoon checked a branch that had not yet been pushed - which always happens during a New Feature orchestrator run (branches are created locally before the first push). Because GrayMoon streams stderr to the terminal overlay in real time, that `fatal:` line appeared as red terminal output even though nothing was wrong. The new command exits 0 with empty output when no upstream is set, so no message is produced and the terminal stays clean.

### Undo Push Commits

When a branch has outgoing commits that have not yet been merged, GrayMoon can now reset them back to `origin` without leaving the app.

- **Two entry points:** the **Push** split button in the workspace header gains an **Undo Push Commits** item when any repository has outgoing commits; the same option also appears in the **Branch** split menu's push section.
- **Confirmation dialog:** lists every repository that will be reset along with its outgoing commit count so you can review the scope before proceeding.
- **Keep or discard changes:** a checkbox lets you choose between a mixed reset (changes stay in the working tree, default) or a hard reset (changes are permanently discarded). A warning is shown when the hard reset option is selected.
- **Runs in parallel:** all targeted repositories are reset concurrently (up to the workspace's configured parallelism limit). Progress is reported in the loading overlay as each reset completes.
- **Instant UI update:** after each reset, the agent sends a sync notification so the Commits badge and version column update without a manual sync.
- **Only eligible repositories are targeted:** pinned tags and repositories with no outgoing commits are automatically skipped.

### Terminal output for commit sync

The commit sync (pull-with-rebase) operation now streams agent command output into the loading overlay terminal, consistent with push, restore, and other long-running operations.

### Global workspace action notifications

A floating notification panel now appears anywhere in the app when a tracked workspace has pending actions - dependency updates, commits ready to push, or incoming commits to pull.

- **Works from any page:** the panel appears in the bottom corner of the UI even when you are on the Projects, Actions, or Connectors pages - you do not need to navigate back to the workspace repositories view to act on it.
- **Suppressed on the workspace page itself:** when you are already on the workspace repositories page, notifications for that workspace are hidden to avoid duplicating what is already visible in the grid.
- **Up to two notifications at once:** if multiple workspaces need attention, the most recent two are shown. Older ones drop off as new ones arrive.
- **Three conditions trigger a card:** unmatched dependency versions, outgoing commits with no upstream or commits ahead of remote (push recommended), or incoming commits on the default branch.
- **Per-repository breakdown:** each card lists the affected repositories with the same badges used in the workspace repositories grid - a red **x of y** badge for dependency updates and a **up-arrow/down-arrow** commits badge (yellow for push-only, red when there are incoming commits). Hover over a red update badge to see exactly which packages need updating and what the version change is, in the same popup format as the grid.
- **One-click actions without navigating away:** each card shows the relevant action buttons - **Update** or **Update & Push** for dependency mismatches, **Push** for outgoing commits, **Pull** for incoming commits on the default branch. All run as background jobs using the same pipeline as the workspace repositories page.
- **Dismiss per workspace:** use the x button on a card to dismiss it. The card reappears if the workspace syncs again with pending actions.
- **Notifications update automatically:** the panel subscribes to the workspace sync hub and recomputes state each time any repository sync event arrives, so cards appear and disappear without a page reload.

### Background jobs on the workspace repositories page

Long-running workspace operations now run as **background jobs** instead of blocking the page with an inline loading overlay. Sync, push, dependency update, restore packages, branch checkout/switch, commit sync, sync-to-default, and pull-request creation all use the same job pipeline.

- **Keep working in the app:** start an operation on the workspace **Repositories** page, then navigate to **Projects**, **Actions**, or another workspace view. The job keeps running on the server for your browser tab. When you return to **Repositories**, the loading overlay reappears with the same progress message and live terminal output.
- **Page-scoped overlay:** the spinner and terminal sit over the main content area (not full-screen), so the sidebar and workspace navigation stay usable while a job runs.
- **Per-job terminal:** agent command output and GitHub API request/response lines stream into that job's own terminal buffer. Toggle the terminal icon in the overlay top bar as before; output is not mixed with other pages or stale global logs.
- **Abort:** use **Abort** on the overlay to cancel the in-flight job. Closing the browser tab cancels all running jobs for that session.
- **One job per page:** only one running job is allowed per workspace repositories URL at a time. Starting the same kind of work again while one is already running reuses the existing handle instead of launching a duplicate.
- **Safer grid refresh:** while a job is running, automatic grid refresh from agent hook sync is paused so in-progress operations are not overwritten by stale database reads.

Initial page load and repository fetch still use a short inline overlay on the page itself; only orchestration work (git, restore, push, GitHub calls) runs as a background job.

### Raw log output in the GitHub Actions live terminal

The right pane of the GitHub Actions live terminal (visible while a workflow run is in progress on the Workspace Actions page) now shows the actual log text from the current job instead of a formatted list of step-transition events.

- **What it shows:** the last 8 lines of the running job's log, pulled directly from the GitHub API and displayed as-is with timestamps stripped. No interpretation or reformatting — you see exactly what GitHub recorded.
- **Why it works this way:** the GitHub Actions API does not expose a streaming log endpoint; it returns the full accumulated log for a job in one download. GrayMoon fetches that text on each poll cycle and tails it to the 8 most recent non-blank lines.
- **Left pane is unchanged:** the left pane still shows a live step-status board (step names with run/success/fail/skip icons) scrolled to the active step. The right pane is now the raw log tail.
- **White text:** raw log lines are rendered in white to distinguish them visually from the step-status board and from the green/yellow terminal output used elsewhere in the overlay.
- **Poll rate:** the right pane updates on the same adaptive interval as the step board — every 2 seconds when a job is actively running, 3 seconds when waiting, and 15 seconds when idle.

### Workspace `dotnet restore` for fresh NuGet resolution

GrayMoon now runs `dotnet restore --force --no-cache` on workspace projects so dependency rollouts do not silently reuse stale local NuGet cache entries.

- **Why it matters:** after GrayMoon updates `PackageReference` versions across repositories, `dotnet` can still resolve older packages from the global or local NuGet cache. That leads to mismatched builds, false confidence that a rollout worked, and confusing drift between what is in `.csproj` files and what MSBuild actually restores. Forcing restore without cache makes NuGet fetch the versions you just wrote instead of falling back to cached artifacts.
- **Restore Packages (manual):** **Sync** is now a split button. Open the dropdown and choose **Restore Packages** to restore every tracked `.csproj` in the workspace (repos pinned to a tag are skipped). A loading overlay shows progress and can be cancelled.
- **Automatic during Update:** after dependency versions are applied at each dependency level and before commits are created, GrayMoon restores the updated projects so commit and build state match the new package refs.
- **Automatic during Push:** before each dependency level is pushed during synchronized push, GrayMoon restores projects that have cross-repository project references at that level, so downstream consumers pick up freshly published packages instead of cached ones.
- **Best-effort by design:** per-project restore failures are logged but do not abort the wider Update, Push, or manual restore workflow.

### Boolean search filters across workspace and catalog pages

Grid and list filters now use one shared search control (`FilterSearchInput`) backed by a boolean expression parser in `GrayMoon.Common`. Syntax highlighting runs inside the search box; operators and field prefixes are colored, and invalid syntax turns the typed text red while still applying a simple word match so rows are not hidden by a typo.

- **Where it works:** workspace **Repositories** (grid), **Projects**, **Packages**, **Files**, **Dependencies**, **Actions**, and the workspace header search; global **Repositories** and **Connectors** lists; **Workspaces** list; and the **Select repositories** modal when editing workspace membership.
- **Operators:** combine terms with `and`, `or`, and parentheses. Spaces between terms mean implicit `and` (`api web` is the same as `api and web`). `and` binds tighter than `or`, so `a or b and c` means `a or (b and c)` unless you group with `( )`.
- **Syntax highlighting:** `and`, `or`, `()`, and `field:value` segments use distinct colors. Unbalanced parentheses or other parse errors show red text; filtering falls back to matching each word with implicit `and`.
- **Field tokens (page-specific):**
  - **Repositories** (global list and modal): `topic:blazor`
  - **Workspace repositories** (grid and header): plain text across name, branch, version, dependency level, sync status
  - **Projects:** `type:`, `framework:`
  - **Packages:** `registry:`, `framework:`
  - **Files:** `repo:`
  - **Dependencies** graph: repository names (plain terms)
  - **Actions:** `repo:`, `workflow:`
  - **Connectors:** `type:`, `status:`
  - **Workspaces:** `path:` plus name and repository counts
- **Examples:** `api or web`; `(topic:blazor or topic:angular) and acme`; `repo:payments and workflow:build`; `type:GitHub and status:Error`; `path:repos and dev`.
- **Keyboard and clear:** press **Escape** to clear the filter; use the **x** button when the box has text.

### GitHub API log in the loading overlay terminal

Every GitHub REST call made by the app is now mirrored into the loading overlay command terminal, alongside existing agent command output and GitHub Actions lines during synchronized push.

- **Toggle:** use the terminal icon in the overlay top bar (same control as agent/git logs). The feed is always collected; it is visible when the overlay is open and the terminal is enabled.
- **Two lines per request:** an outbound line (`-> GET /repos/...`) in green, then a response line (`<- 200 (124ms)`) in gray for success or red for errors. A short body preview (up to 80 characters) may appear on a third indented line for JSON/text responses.
- **Covers all GitHub operations:** pull request create, repository fetch, workflow polling, and any other call through `GitHubService` - not only push-time GHA updates.
- **Safe by design:** no request bodies, no auth headers, and no tokens in the log. Query strings drop sensitive keys; response previews redact common secret field patterns. Logging never affects HTTP behavior if the terminal fails to append.
- **Retries are visible:** Polly retries on rate limits or transient errors show as separate request/response pairs, which helps when diagnosing 429 storms.

### Create pull requests from the workspace repositories view

GrayMoon can now create GitHub pull requests without leaving the app. Use one dialog for a single repository, every repository in a dependency level, or all eligible workspace repositories.

- **Three entry points:** the yellow **create** badge on a row (one repo), the dependency-level GitHub icon (repos in that level with commits ahead of default), and **Branch** split menu **Create PRs...** (all eligible repos in the workspace).
- **One dialog, shared fields:** title (default generated from the branch name), optional description, draft checkbox, and optional reviewers. Title rules turn branch names like `feature/ABC-123-update-dependencies.v2` into readable subjects (ticket-style tokens such as `ABC-123` stay grouped).
- **Reviewers:** users and teams are loaded from GitHub (merged across targets when multiple repos are selected). Search supports multiple words (space/comma separated). Teams appear first in the list.
- **Open in GitHub:** opens compare/create pages in the browser (with a confirm when more than five repositories are involved).
- **Create flow:** a confirmation step, then a loading overlay (`Creating N pull requests...`, then `Created x of N pull requests` after the first success). Per-repository results are summarized in toasts; a single successful PR can open in the browser automatically.
- **Branch menu:** **Branch** is a split button with **New Branch**, **Switch Branch**, and **Create PRs...**. The main **Update** button is unchanged.

Only repositories that are eligible are included: not on a tag, on a feature branch (not default), with commits ahead of default, no open PR already, and a configured GitHub connector.

### Update dependencies: custom commit message and smarter default-branch warning

The workspace **Update dependencies** confirmation dialog now lets you control how git commits are created during a coordinated dependency rollout.

- **Optional commit subject:** enter a custom commit message (for example `chore(deps): bump AuroraVerityReview packages`). Leave it blank to keep the default `chore(deps): update package versions` for `.csproj` commits and `chore(deps): update versions (N)` for version-file commits.
- **Include updated dependencies in commit message:** when checked (default), each repository commit still appends the package list in the body (`- PackageId to x.y.z`). Uncheck to commit only the subject line with no dependency list.
- **Per-workspace memory:** your last commit message and checkbox choice are remembered for the current workspace for the rest of the app session (no database persistence). Reopening the dialog restores what you last used on Proceed; Cancel does not clear it.
- **Default-branch warning only for repos that will change:** the protected-branch warning before update now lists only repositories that are on their default branch **and** have pending dependency updates in the current plan. Repos on default with nothing to update are omitted, so you are not asked to confirm a long list of repos GrayMoon will not touch.

### Live GitHub Actions feed in synchronized push overlay

When GrayMoon performs a dependency-synchronized push and is waiting for required package versions to appear in registries, the loading overlay terminal now streams live GitHub Actions updates for the repositories that were already pushed.

- **Same live feed logic as Workspace Actions:** the overlay uses the same run/job/step polling path as the workspace GitHub Actions terminal, so status formatting and step transitions stay consistent.
- **No duplicate retrieval logic:** the implementation reuses a shared service for workflow polling and step transition formatting.
- **Only tracks real running workflows:** it subscribes when a run is actually in progress and appends updates into the overlay terminal stream.
- **Stops retrying only when no workflows exist for a repo:** if GitHub reports no workflow definitions for that repository, GrayMoon marks that repo as "no workflows" and skips further discovery for it.
- **Does not stop just because nothing is running yet:** repositories with workflow files but no current run continue to be checked on the normal discovery interval.

### Branch updates: PR workflows, commits visibility, and UI polish

This branch adds several workflow-focused improvements in the workspace repositories view.

- **Dependency-level PR links are smarter:** from the level header GitHub dropdown, **Pull Requests** now opens the exact open PR for each repository (when one exists on the current branch), instead of only opening the generic repo pull-requests page.
- **Copy open PR links in one click:** dependency-level headers now include a **Share** action that copies all open PR URLs for that level to the clipboard (one URL per line), with a confirmation toast.
- **Better non-upstream commit visibility:** the Commits badge now shows outgoing commit count for branches without an upstream (for example `↑3`), making it clearer when a first push is needed.
- **Loading overlay stability improvements:** overlay terminal/spinner presentation was tuned so status/timer updates do not cause visual shifting.
- **Workspace nav polish:** workspace sidebar navigation was updated for clearer repository/dependency affordances and improved icon consistency.

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
