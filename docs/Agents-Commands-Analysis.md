# GrayMoon Agent Commands Analysis

This document describes the **purpose**, **usage**, and **execution** of all agent-related commands in GrayMoon: both the **CLI verbs** (run, install, uninstall) and the **SignalR request/response commands** the app sends to the agent.

---

## 1. Overview

The **GrayMoon Agent** is a host-side process that:

- Connects to the GrayMoon App via **SignalR** (`/hub/agent`).
- Receives **RequestCommand** invocations from the app and executes git/repository operations locally.
- Sends **ResponseCommand** back with results (or **SyncCommand** when git hooks fire).
- Can be run interactively or installed as a **Windows service** or **systemd** unit.

There are two distinct command layers:

| Layer | Purpose | Invoked by |
|-------|---------|------------|
| **CLI commands** | How the agent process is started or installed | User / service manager |
| **SignalR commands** | Operations the app asks the agent to perform | GrayMoon App via `IAgentBridge.SendCommandAsync` |

---

## 2. CLI Commands

The agent executable is built with **System.CommandLine**. Entry point: `Program.cs` parses arguments and invokes the root command built by `AgentCli.Build()`.

### 2.1 Root command

- **Description:** `GrayMoon Agent: host-side worker for git and repository operations.`
- **Subcommands:** `run` (default), `install`, `uninstall`.
- **Usage:** `graymoon-agent [run|install|uninstall] [options]`

### 2.2 `run` (default)

| Aspect | Details |
|--------|---------|
| **Purpose** | Run the agent process: connect to the app hub, listen for RequestCommand and HTTP hook notifications, execute commands and notify sync. |
| **How used** | Default when no verb is passed; also used by the installed service (e.g. `graymoon-agent run --hub-url "..."`). |
| **Execution** | 1. Load config from appsettings (and optional CLI overrides). 2. Build a .NET Host with Serilog (console + file under `%ProgramData%\GrayMoon\logs`). 3. Register services: `IGitService`, `ICsProjFileService`, `IWorkspaceFileSearchService`, `CommandJobFactory`, `ICommandDispatcher`, all command handlers, hook handlers, `SignalRConnectionHostedService`, `HookListenerHostedService`, `JobBackgroundService`. 4. Run the host (with Windows service / systemd integration when applicable). 5. On startup, connect to `AppHubUrl`, report SemVer, and listen for `RequestCommand`. |

**Shared options (also used by `install`):**

- `--hub-url`, `-u`: SignalR hub URL the agent connects to.
- `--listen-port`, `-p`: HTTP port for hook notifications (`/notify`).
- `--concurrency`, `-c`: Max concurrent command executions (default from `AgentOptions.MaxConcurrentCommands`).

### 2.3 `install`

| Aspect | Details |
|--------|---------|
| **Purpose** | Install the agent as a Windows service or systemd unit so it runs in the background with the same options passed at install time. |
| **How used** | Run once (e.g. `graymoon-agent install --hub-url "https://..."`) with optional `--hub-url`, `--listen-port`, `--concurrency`. |
| **Execution** | 1. Resolve the agent executable path. 2. Build run arguments from parsed options (`run --hub-url "..."` etc.). 3. **Windows:** run `sc create GrayMoonAgent binPath= "<exe> run ..." start= auto`. 4. **Linux:** write `/etc/systemd/system/GrayMoonAgent.service` with `ExecStart=<exe> run ...`, then `systemctl daemon-reload` and `systemctl enable GrayMoonAgent.service`. |

### 2.4 `uninstall`

| Aspect | Details |
|--------|---------|
| **Purpose** | Remove the agent Windows service or systemd unit. |
| **How used** | Run once: `graymoon-agent uninstall`. |
| **Execution** | **Windows:** `sc delete GrayMoonAgent`. **Linux:** `systemctl disable GrayMoonAgent.service --now`, delete the unit file, then `systemctl daemon-reload`. |

---

## 3. SignalR Command Flow

When the **app** wants the agent to do something, it uses `IAgentBridge.SendCommandAsync(command, args)`.

1. **App** gets the agent’s SignalR connection ID from `AgentConnectionTracker`, generates a `requestId`, and calls `hubContext.Clients.Client(connectionId).SendAsync("RequestCommand", requestId, command, argsJson)`.
2. **Agent** receives `RequestCommand` in `SignalRConnectionHostedService`: it calls `CommandJobFactory.CreateCommandJob(requestId, command, args)` to deserialize the request into a typed object, then enqueues a `JobEnvelope` (kind: Command) to `IJobQueue`.
3. **JobBackgroundService** workers dequeue envelopes. For Command jobs they call `CommandDispatcher.ExecuteAsync(job.Command, job.Request, ct)`, which invokes the registered handler (e.g. `SyncRepositoryCommand.ExecuteAsync`).
4. The handler runs **git/filesystem operations** (via `IGitService`, `ICsProjFileService`, etc.) and returns a response object.
5. **JobBackgroundService** sends back to the hub: `connection.InvokeAsync("ResponseCommand", requestId, success, data, error)`.
6. **App** has a waiter keyed by `requestId` in `AgentResponseDelivery`; when the hub receives `ResponseCommand`, it completes that waiter and the caller gets `AgentCommandResponse`.

All workspace-scoped commands use a base **WorkspaceCommandRequest** with at least `workspaceRoot`; the app supplies `workspaceName`, `workspaceRoot`, and often `repositoryName` or `repositoryId` so the agent can resolve paths as `GetWorkspacePath(workspaceRoot, workspaceName)` and then `Path.Combine(workspacePath, repositoryName)` where applicable.

---

## 4. SignalR Commands Reference

For each command: **purpose**, **how it’s used** (where the app calls it), **main request parameters**, and **what the agent executes**.

### Workspace & repository discovery

| Command | Purpose | How used | Request parameters | What executes |
|---------|---------|----------|---------------------|----------------|
| **GetWorkspaceExists** | Check if a workspace directory exists (or can be created). | `WorkspaceService` when validating or listing workspaces. | `workspaceName`, `workspaceRoot` | Resolve path via `GetWorkspacePath`, then check directory existence. |
| **GetWorkspaceRepositories** | List repository folder names and their origin URLs under a workspace. | `WorkspaceService` for workspace detail and repository list. | `workspaceName`, `workspaceRoot` | List subdirectories of workspace path; for each, run `git remote get-url origin` (parallel, capped concurrency). |
| **EnsureWorkspace** | Ensure the workspace directory exists (create if missing). | `WorkspaceService` when creating or opening a workspace. | `workspaceName`, `workspaceRoot` | `GetWorkspacePath` then `Directory.CreateDirectory`. |
| **ValidatePath** | Validate that a path is usable as a workspace root (syntax, create if missing). | `WorkspaceService` when user picks a new workspace root. | `path` | `Path.GetFullPath` (validates syntax), then `Directory.CreateDirectory`. |

### Repository sync and version

| Command | Purpose | How used | Request parameters | What executes |
|---------|---------|----------|---------------------|----------------|
| **SyncRepository** | Full sync: ensure repo exists (clone if needed), fetch, run GitVersion, write sync hooks, get branches and commit counts. | `WorkspaceGitService` when syncing a repository (e.g. from UI or open workspace). | `workspaceName`, `workspaceRoot`, `repositoryId`, `repositoryName`, `cloneUrl`, `bearerToken`, `workspaceId` | Create workspace dir; if repo missing and `cloneUrl` set, clone and add safe directory. Then: add safe directory, find .csproj projects (parallel), run GitVersion, fetch (with tags), write post-checkout/post-commit/post-merge hooks (workspaceId/repositoryId), get commit counts (current branch and vs default), get local/remote/default branches. Return version, branch, projects, outgoing/incoming, branch lists, default behind/ahead. |
| **RefreshRepositoryVersion** | Re-read version, branch, commit counts and branch lists without cloning/fetch. | App when refreshing repo state after operations. | `workspaceName`, `workspaceRoot`, `repositoryName` | If repo exists: GitVersion, commit counts for current branch, commit counts vs default, remote/local branches, hasUpstream. Return version, branch, counts, branch lists. |
| **GetRepositoryVersion** | Lightweight: only current SemVer and branch name. | `WorkspaceGitService` when needing quick version/branch. | `workspaceName`, `workspaceRoot`, `repositoryName` | If repo exists: run GitVersion; return exists, version, branch. |
| **GetCommitCounts** | Outgoing/incoming (and vs default) for current branch only; no GitVersion or branch listing. | `WorkspaceGitService` after push to refresh counts. | `workspaceName`, `workspaceRoot`, `repositoryName` | GitVersion for branch name, then `GetCommitCountsAsync` and `GetCommitCountsVsDefaultAsync`. |
| **RefreshRepositoryProjects** | Re-scan .csproj files only; no git operations. | `WorkspaceGitService` when refreshing project list. | `workspaceName`, `workspaceRoot`, `repositoryName` | `CsProjFileService.FindAsync` over repo path; return project list. |
| **SyncRepositoryDependencies** | Apply package version updates to .csproj files. | `WorkspaceGitService` when syncing dependency versions (e.g. from file config). | `workspaceName`, `workspaceRoot`, `repositoryName`, `projectUpdates` (projectPath + packageUpdates: packageId, newVersion) | For each project with non-empty package updates, call `UpdatePackageVersionsAsync` to patch .csproj PackageReference versions. Return updated count. |

### Branch operations

| Command | Purpose | How used | Request parameters | What executes |
|---------|---------|----------|---------------------|----------------|
| **GetBranches** | Fetch and return local/remote branches, current branch, default branch. | Available for callers that need branch list directly from agent; branch list UI often uses DB populated by RefreshBranches. | `workspaceName`, `workspaceRoot`, `repositoryName` | Fetch (with tags), get local branches, remote branches, default branch name, current branch (via GitVersion). |
| **RefreshBranches** | Fetch and return local/remote branches, current branch, default branch. | Branch endpoints (e.g. branch list refresh). | `workspaceName`, `workspaceRoot`, `repositoryName` | Fetch (with tags), then get local branches, remote branches, default branch name, and current branch (via GitVersion). |
| **CheckoutBranch** | Switch repo to a branch. | Branch endpoints (checkout). | `workspaceName`, `workspaceRoot`, `repositoryName`, `branchName` | `git checkout` (or checkout remote branch); hooks later send SyncCommand. |
| **SyncToDefaultBranch** | Switch to default branch, optionally delete previous branch if merged, pull. | Branch endpoints (sync to default). | `workspaceName`, `workspaceRoot`, `repositoryName`, `currentBranchName`, `bearerToken` | Get default branch, checkout, delete old local branch if not default (force=false), pull. |
| **CreateBranch** | Create and checkout a new branch from a base branch. | Branch endpoints and `WorkspaceGitService`. | `workspaceName`, `workspaceRoot`, `repositoryName`, `newBranchName`, `baseBranchName` | Create branch from base and checkout; return current branch from GitVersion. |
| **SetUpstreamBranch** | Set upstream for branch (push with -u). | Branch endpoints. | `workspaceName`, `workspaceRoot`, `repositoryName`, `branchName` | `git push -u origin <branch>`. |
| **DeleteBranch** | Delete a local or remote branch. | Branch endpoints. | `workspaceName`, `workspaceRoot`, `repositoryName`, `branchName`, `isRemote` | `git branch -d` or `git push origin --delete` per request. |

### Commit and push

| Command | Purpose | How used | Request parameters | What executes |
|---------|---------|----------|---------------------|----------------|
| **StageAndCommit** | Stage given paths and commit with message. | `WorkspaceGitService` (commit action). | `workspaceName`, `workspaceRoot`, `repositoryName`, `commitMessage`, `pathsToStage` | `git add` (paths or .) and `git commit -m "..."`. |
| **PushRepository** | Push current branch to origin and set upstream (-u). | `WorkspaceGitService` and branch flows. | `workspaceName`, `workspaceRoot`, `repositoryName`, `branchName` (optional), `bearerToken` | If branch not provided, get from GitVersion. Then `git push -u origin <branch>`. |
| **CommitSyncRepository** | Pull then push for the current branch (fetch, pull if incoming, push if outgoing); detect merge conflict and abort. | CommitSync API (e.g. “Commit & Sync” action). | `workspaceName`, `workspaceRoot`, `repositoryName`, `bearerToken` | Get branch via GitVersion; fetch; get commit counts; if incoming > 0: pull (on conflict abort merge and return merge conflict); if outgoing > 0: push; return version, branch, counts, success/error. |

### File and search

| Command | Purpose | How used | Request parameters | What executes |
|---------|---------|----------|---------------------|----------------|
| **SearchFiles** | Search workspace (optionally scoped to one repo) by pattern. | `WorkspaceFileSearchService`, workspace/search endpoints. | `workspaceName`, `workspaceRoot`, `repositoryName` (optional), `searchPattern` | `IWorkspaceFileSearchService.SearchAsync` under workspace path; return up to 20 matches. |
| **GetFileContents** | Read full text of a file under a repo. | View file modal. | `workspaceName`, `workspaceRoot`, `repositoryName`, `filePath` | Resolve path under repo, `File.ReadAllTextAsync`. |
| **UpdateFileVersions** | Replace version placeholders in a file using a pattern and repo version map. | `WorkspaceFileVersionService` (e.g. version pinning in config files). | `workspaceName`, `workspaceRoot`, `repositoryName`, `filePath`, `versionPattern`, `repoVersions` | Parse pattern lines (e.g. `PREFIX={repoName}`); for each line matching a prefix, replace with `repoVersions[repoName]`; write back file. |

### Host and misc

| Command | Purpose | How used | Request parameters | What executes |
|---------|---------|----------|---------------------|----------------|
| **GetHostInfo** | Report host tool versions (dotnet, git, GitVersion). | Agent status page (`Agent.razor`). | None (or empty) | Run `dotnet --version`, `git --version`, `dotnet gitversion version`; return first line of stdout per tool. |

---

## 5. Hook-driven sync (NotifySync)

Separate from **RequestCommand**/ **ResponseCommand**, the agent receives **HTTP hook** calls (e.g. after checkout/commit/merge) and enqueues **NotifySync** jobs. The handler runs GitVersion and commit counts, then invokes **SyncCommand** on the hub with workspace/repo id, version, branch, counts, and upstream flag. The app’s `SyncCommandHandler` persists this and broadcasts **WorkspaceSynced**. This is **push** from agent to app, not a request/response command.

---

## 7. Optimization Analysis

This section identifies opportunities to **reduce work** and **avoid redundant or heavy operations** so commands run as fast as possible. The main levers are: (1) **use lightweight git commands when version is not required**, (2) **avoid duplicate git calls**, and (3) **reuse results within a flow** (e.g. resolve default branch once).

### 7.1 GitVersion vs lightweight branch/version

**GitVersion** (`dotnet-gitversion`) is relatively expensive: it runs a .NET tool, parses repo history, and returns SemVer and branch. Use it **only when the response needs version (SemVer) or when branch name is not trivially available**.

When **only the current branch name** is needed, use **`git branch --show-current`** (one fast process, no network, no GitVersion). The agent already uses this internally in `DeleteLocalBranchAsync` and `DeleteBranchAsync`; the same pattern should be used everywhere only branch name is required.

| Command / flow | Currently | Version in response? | Optimization |
|----------------|-----------|------------------------|---------------|
| **GetCommitCounts** | `GetVersionAsync` → branch, then counts | No | Use `GetCurrentBranchNameAsync()` (e.g. `git branch --show-current`) instead of GitVersion. |
| **PushRepository** (when branch not provided) | `GetVersionAsync` to get branch | No | Use `GetCurrentBranchNameAsync()` instead of GitVersion. |
| **GetBranches** | `GetVersionAsync` for current branch | No | Use `GetCurrentBranchNameAsync()` instead of GitVersion. |
| **RefreshBranches** | `GetVersionAsync` for current branch | No | Use `GetCurrentBranchNameAsync()` instead of GitVersion. |
| **CreateBranch** | `GetVersionAsync` after create for current branch | No | Use `GetCurrentBranchNameAsync()` instead of GitVersion. |
| **GetRepositoryVersion** | `GetVersionAsync` | **Yes** (version + branch) | Keep GitVersion. |
| **SyncRepository** | `GetVersionAsync` | **Yes** | Keep GitVersion. |
| **RefreshRepositoryVersion** | `GetVersionAsync` | **Yes** | Keep GitVersion. |
| **CommitSyncRepository** | `GetVersionAsync` (version + branch in response) | **Yes** | Keep GitVersion. |
| **CommitHookSyncCommand** / **CheckoutHookSyncCommand** / **MergeHookSyncCommand** | `GetVersionAsync` | **Yes** (SyncCommand carries version) | Keep GitVersion. |

**Recommendation:** Add **`GetCurrentBranchNameAsync(string repoPath, CancellationToken ct)`** to `IGitService` (implement with `git branch --show-current`). Use it in GetCommitCounts, PushRepository when branch omitted, GetBranches, RefreshBranches, and CreateBranch instead of calling `GetVersionAsync` when only branch name is needed.

---

### 7.2 Duplicate default-branch resolution

**GetCommitCountsVsDefaultAsync** and **GetDefaultBranchNameAsync** both call **GetDefaultBranchAsync** (private in `GitService`). In flows that need **both** default-behind/ahead counts **and** the default branch name, the default branch is resolved twice.

- **SyncRepositoryCommand:** Calls `GetCommitCountsVsDefaultAsync(repoPath)` (which resolves default branch), then in parallel calls `GetDefaultBranchNameAsync(repoPath)`, `GetLocalBranchesAsync`, `GetRemoteBranchesAsync`. So default branch is resolved **twice** (once inside GetCommitCountsVsDefault, once in GetDefaultBranchName).

**Recommendation:** Add an overload or variant that returns both default-behind/ahead **and** the default branch name in one go (e.g. `GetCommitCountsVsDefaultAsync` returns `(DefaultBehind, DefaultAhead, DefaultBranchName?)`), or have `GetCommitCountsVsDefaultAsync` accept an optional pre-resolved default branch so callers can resolve once and pass in. Then in SyncRepository, resolve default branch once and use it for both commit counts vs default and the DefaultBranch response field.

---

### 7.3 GetDefaultBranchAsync: multiple git calls

**GetDefaultBranchAsync** currently does up to **four** git invocations in the worst case:

1. `git rev-parse --verify origin/main`
2. `git rev-parse --verify origin/master`
3. `git symbolic-ref refs/remotes/origin/HEAD`
4. Optionally `git rev-parse --verify origin/<branch>`

**Recommendation:** Prefer a **single** call that gives the configured default branch when possible, e.g. **`git symbolic-ref refs/remotes/origin/HEAD`** first (if supported and set). Only if that fails, fall back to `origin/main` then `origin/master`. That reduces the common case to one process instead of two or more.

---

### 7.4 GetRemoteBranchesAsync after a fetch: avoid second network round-trip

**GetRemoteBranchesAsync** uses **`git ls-remote --heads origin`**, which performs a **network** request. When the caller has **just run fetch** (e.g. SyncRepository, GetBranches, RefreshBranches), remote refs are already in **refs/remotes/origin/** locally. Reading those refs is **local only** and much faster than another ls-remote.

**Recommendation:** Add **GetRemoteBranchesFromRefsAsync** (or a parameter `useLocalRefsOnly: bool`) that lists **refs/remotes/origin/** (e.g. `git for-each-ref refs/remotes/origin --format='%(refname:short)'` and strip `origin/`) instead of ls-remote. Use this in SyncRepository, GetBranches, and RefreshBranches **when fetch has just been run** so remote branch list is local-only and faster.

---

### 7.5 CommitSyncRepository: redundant fetches

**CommitSyncRepositoryCommand** currently runs **FetchAsync** up to **four** times in one execution:

1. Once at the start (before commit counts).
2. Again after aborting a merge (to refresh counts).
3. Again after a successful pull (to refresh counts).
4. Again after a successful push (to refresh counts).

Each fetch is a full `git fetch origin --prune --tags` (network round-trip). The extra fetches are only to refresh commit counts; refs are already updated by pull/push.

**Recommendation:** Run **one fetch at the start**. After pull or push, **recompute commit counts from existing refs** without fetching again (pull/push already update refs). Only do an extra fetch after **merge abort** if we need to ensure remote refs are consistent; otherwise, a single fetch per CommitSync run is enough for correct counts in most cases.

---

### 7.6 GetCommitCountsAsync + GetCommitCountsVsDefaultAsync: shared default branch

When a command needs **both** (1) outgoing/incoming for current branch and (2) behind/ahead vs default (e.g. **SyncRepository**, **RefreshRepositoryVersion**, **GetCommitCounts**), we currently:

- Call **GetCommitCountsAsync(repoPath, branch)** (which may call **GetDefaultBranchAsync** internally when the branch has no upstream).
- Call **GetCommitCountsVsDefaultAsync(repoPath)** (which calls **GetDefaultBranchAsync** again).

So **GetDefaultBranchAsync** can be invoked **twice** in one command. Combining these (e.g. resolve default branch once and pass into both count methods, or provide a combined API that returns all four counts in one pass) would avoid duplicate resolution and reduce git process spawns.

---

### 7.7 Rev-list count pairs: two processes vs one

**GetCommitCountsAsync** runs two **rev-list --count** commands (outgoing and incoming). **GetCommitCountsVsDefaultAsync** runs two more (behind and ahead). Each is a separate process. Git does not offer a single command that returns all four counts, but we could:

- Run the two pairs **in parallel** (outgoing+incoming in parallel with behind+ahead) where both are needed, to reduce wall-clock time.
- Ensure we only resolve default branch once when computing both pairs (see 7.2 and 7.6).

---

### 7.8 Summary: recommended changes (in order of impact)

| Priority | Change | Benefit |
|----------|--------|---------|
| 1 | Add **GetCurrentBranchNameAsync** (e.g. `git branch --show-current`) and use it in **GetCommitCounts**, **PushRepository** (branch omitted), **GetBranches**, **RefreshBranches**, **CreateBranch** instead of **GetVersionAsync**. | Avoids expensive GitVersion when only branch name is needed; large speedup for those commands. |
| 2 | **CommitSyncRepository:** Use a single fetch at start; drop redundant fetches after pull/push (recompute counts from existing refs). | Fewer network round-trips per CommitSync run. |
| 3 | **GetRemoteBranches** after fetch: use local refs (e.g. for-each-ref refs/remotes/origin) instead of ls-remote in **SyncRepository**, **GetBranches**, **RefreshBranches**. | Avoids second network call when remote list was just updated by fetch. |
| 4 | Resolve default branch **once** per flow: extend or combine APIs so **GetCommitCountsVsDefault** and **GetDefaultBranchName** don’t both call GetDefaultBranchAsync in the same command (e.g. SyncRepository). | Fewer git processes and no duplicate symbolic-ref/rev-parse. |
| 5 | **GetDefaultBranchAsync:** Try **symbolic-ref refs/remotes/origin/HEAD** first; only then fall back to origin/main, origin/master. | Fewer git calls in the common case. |
| 6 | Where both **GetCommitCountsAsync** and **GetCommitCountsVsDefaultAsync** are used, run the two rev-list pairs in parallel and pass a single resolved default branch. | Less wall-clock time and no duplicate default-branch resolution. |

---

## 8. Summary

- **CLI:** `run` starts the agent (connect to hub, process commands); `install`/`uninstall` register or remove the agent as a service.
- **SignalR commands** are requested by the app via `RequestCommand` and answered with `ResponseCommand`; the agent deserializes the request, runs the corresponding handler (git/filesystem/csproj), and returns the result.
- **Hook sync** is agent-initiated: hooks trigger NotifySync, then the agent sends `SyncCommand` to the app so the UI can stay in sync with local git state.
