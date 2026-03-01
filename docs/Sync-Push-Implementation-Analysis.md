# Sync Push Implementation Analysis

This document describes how **dependency-synchronized push** (sync push) is implemented in GrayMoon: the flow that pushes workspace repositories in dependency order and optionally waits for required packages to appear in NuGet registries before pushing dependents.

---

## 1. Overview

- **Purpose:** Push multiple repositories in a workspace while respecting inter-repo dependencies. Lower-level repos (packages consumed by others) are pushed first; the app can wait until their packages are visible in the configured NuGet registries before pushing higher-level repos.
- **Entry point:** UI "Push" button on the Workspace Repositories page.
- **Main orchestration:** `WorkspaceGitService.RunPushAsync` (App) and agent command `PushRepository` (Agent).
- **Key data:** Push plan from `WorkspaceProjectRepository.GetPushPlanPayloadAsync` — one payload per repo with dependency level and list of required packages (from lower-level repos) that must be in the registry before that repo is pushed.

---

## 2. Data Model

### 2.1 Push plan payloads

- **`PushRepoPayload`** (`GrayMoon.App/Models/PushPayload.cs`): One per workspace repository.
  - `RepoId`, `RepoName`, `DependencyLevel` (nullable), `RequiredPackages`.
- **`RequiredPackageForPush`**: A package (id + version) that a repo depends on, plus `MatchedConnectorId` (NuGet connector/registry where that package is expected).
  - Used to decide if “synchronized push” is possible (all required packages have a matched connector) and to poll registries before pushing a level.

### 2.2 Push plan computation

- **`WorkspaceProjectRepository.GetPushPlanPayloadAsync`** builds the push plan:
  - Loads workspace repo links (with `DependencyLevel`) and workspace projects + `ProjectDependencies`.
  - For each dependency edge (DependentProject → ReferencedProject), if the referenced project’s repo has a **lower** dependency level than the dependent’s repo, the referenced package (PackageId, Version, MatchedConnectorId) is added to the dependent repo’s `RequiredPackages`.
  - Deduplicates by (PackageId, Version) per repo.
  - Returns list of `PushRepoPayload` ordered by dependency level then repo name.

So the push plan is: all workspace repos, each with its dependency level and the set of packages (from lower-level repos) that must be in a registry before that repo is pushed.

---

## 3. UI Flow

### 3.1 Push button and filtering

- **`WorkspaceRepositories.razor`**
  - **`OnPushClickAsync`** (Push button):
    1. Calls `WorkspaceGitService.GetPushPlanAsync(WorkspaceId)` to get full push plan and `IsMultiLevel`.
    2. Filters to repos that have **unpushed commits** (`OutgoingCommits > 0`).
    3. If selection is active (`EffectiveActionRepositoryIds`), further filters to selected repos.
    4. If no repos left → toast “No repositories to push” and return.
    5. Stores `pushPlanRepoIds` (set of repo IDs to push), multi-level flag, and count.
    6. If exactly one repo → calls `RunPushCoreAsync()` directly.
    7. Otherwise → opens **PushModal** (`showPushModal = true`); user clicks “Proceed” → `OnPushProceedAsync` → `RunPushCoreAsync()`.

- **`PushModal.razor`**: Confirmation dialog. When `IsMultiLevel`, explains that push runs by level and waits for required packages in the registry; otherwise shows repository count. “Proceed” triggers `OnProceed` → `RunPushCoreAsync`.

### 3.2 Running the push

- **`RunPushCoreAsync`**:
  - Sets `isPushing = true`, builds progress message, creates `CancellationTokenSource` for abort.
  - Resolves `WorkspaceGitService` from scoped provider and calls:
    - `RunPushAsync(WorkspaceId, repoIdsToPush: pushPlanRepoIds, onProgressMessage, onRepoError, cancellationToken)`.
  - On success: clears `isPushing`, calls `RefreshFromSync`.
  - On cancel: clears `isPushing` and shows message.
  - Errors are reported via `onRepoError` and stored in `repositoryErrors[repoId]` for per-row display.

So the UI only pushes repos that have unpushed commits (and optionally only selected repos), and passes that set as `repoIdsToPush` into the service.

---

## 4. App Service: `WorkspaceGitService.RunPushAsync`

**Location:** `GrayMoon.App/Services/WorkspaceGitService.cs` (approx. lines 541–664).

### 4.1 Prerequisites and setup

- Requires `IAgentBridge.IsAgentConnected`; throws if not.
- Loads workspace; creates workspace directory if needed.
- **Step 1 — Sync package registries:**  
  If `PackageRegistrySyncService` is registered, calls `SyncWorkspacePackageRegistriesAsync(workspaceId)`. This updates `MatchedConnectorId` for workspace packages by checking active NuGet connectors so the push plan’s “required package → registry” is up to date.

### 4.2 Push plan and filtering

- Calls `GetPushPlanPayloadAsync(workspaceId)` to get full plan.
- If `repoIdsToPush` is non-null and non-empty, filters payload to those repo IDs; otherwise uses full plan.
- If payload is empty → “No repositories to push” and return.

### 4.3 Bearer tokens

- Loads `WorkspaceRepositories` with `Repository` and `Connector` to build `bearerByRepoId` (repository ID → connector `UserToken`) for authenticated push.

### 4.4 Synchronized vs unsynchronized push

- **Synchronized push possible** when: every repo in the payload has all its `RequiredPackages` with `MatchedConnectorId` set, and `NuGetService` and `ConnectorRepository` are available.
- If **not** possible (e.g. some dependencies have no matched registry, or NuGet/connector services missing):
  - Single step: “Pushing all repositories...”, then `PushReposAsync(workspace, payload, ...)` for the whole payload, then `RefreshVersionsAfterPushAsync`, then `WorkspaceSynced` broadcast. No level-by-level wait.

- If **synchronized push is possible**:
  - Process **one dependency level at a time** (levels ascending).
  - For each level:
    1. **Wait for required packages:** Collect all `RequiredPackages` for repos at this level (with `MatchedConnectorId`). For each, poll `NuGetService.PackageVersionExistsAsync(connector, packageId, version)` until all are found or timeout. Timeout = `totalDeps * PushWaitDependencyTimeoutMinutesPerDependency` (from `WorkspaceOptions`, default 1.0 minute per dependency). Progress message shows “Waiting for N dependencies...” and countdown.
    2. **Push repos at this level:** `PushReposAsync(workspace, reposAtLevel, ...)` — all repos at this level pushed in parallel (throttled by `_maxConcurrent`).
    3. **Refresh versions:** `RefreshVersionsAfterPushAsync(workspaceId, reposAtLevel)` so the UI sees updated versions after push.

- After all levels (or after single-step push): broadcast `WorkspaceSynced` if hub is available.

### 4.5 Pushing a batch of repos

- **`PushReposAsync`** (private):
  - For each repo in the list, acquires a semaphore (`_maxConcurrent` from `WorkspaceOptions.MaxConcurrentGitOperations`, default 8), then:
    - Builds args: `workspaceName`, `repositoryId`, `repositoryName`, `bearerToken`, `workspaceId`, `workspaceRoot`.
    - Sends agent command **`PushRepository`** via `_agentBridge.SendCommandAsync("PushRepository", args, cancellationToken)`.
    - Parses response as `PushRepositoryResponse`; on failure calls `onRepoError(repoId, message)`.
    - Updates progress “Pushed X of Y”.
  - Uses `Task.WhenAll` so repos in the batch run concurrently (up to semaphore limit).

### 4.6 After push: refresh versions

- **`RefreshVersionsAfterPushAsync`**: For each repo in the pushed list, calls `SyncSingleRepositoryAsync(repo.RepoId, workspaceId, cancellationToken)`.
- **`SyncSingleRepositoryAsync`**: Validates repo and workspace link, then sends agent **`RefreshRepositoryVersion`** and persists the returned version/branch/projects so the grid shows up-to-date state.

---

## 5. Agent: Push command

### 5.1 Command registration and dispatch

- **`PushRepositoryCommand`** implements `ICommandHandler<PushRepositoryRequest, PushRepositoryResponse>`.
- Registered in `RunCommandHandler`; `CommandDispatcher` maps `"PushRepository"` to this handler; `CommandJobFactory` deserializes JSON to `PushRepositoryRequest`.

### 5.2 Request

- **`PushRepositoryRequest`** (extends `WorkspaceCommandRequest`): `WorkspaceName`, `RepositoryId`, `RepositoryName`, `BearerToken`, `WorkspaceId`, plus base (e.g. `WorkspaceRoot`).

### 5.3 Execution (`PushRepositoryCommand.ExecuteCoreAsync`)

1. Resolve repo path: `GetWorkspacePath(workspaceRoot, workspaceName)` then `Path.Combine(..., repositoryName)`.
2. If directory does not exist → response `Success = false`, “Repository not found”.
3. **Get current branch:** `git.GetVersionAsync(repoPath)`; branch = `versionResult.BranchName ?? versionResult.EscapedBranchName`. If missing → “Could not determine branch name”.
4. **Push:** `git.PushAsync(repoPath, branch, bearerToken, setTracking: true, ct)`. Uses `-u` so the branch is upstreamed even when there are no new commits.
5. Return `PushRepositoryResponse { Success, ErrorMessage }`.

### 5.4 Git layer (`GitService.PushAsync`)

- Validates path and branch.
- Builds `git push` args: `-u origin <branch>` when `setTracking` is true.
- If `bearerToken` is set: sets `http.extraHeader` with `Authorization: Basic <base64("x-access-token:" + bearerToken)>` for the process.
- Runs `git` in the repo directory; returns `(Success, ErrorMessage)` from exit code and stdout/stderr.

---

## 6. Package registry sync (before push)

- **`PackageRegistrySyncService.SyncWorkspacePackageRegistriesAsync`**:
  - Loads workspace packages and active NuGet connectors.
  - For each package, checks each connector via `NuGetService.PackageExistsAsync(packageId)` (any version); sets `MatchedConnectorId` to the first connector that has the package.
  - Persists via `WorkspaceProjectRepository.SetPackagesMatchedConnectorsAsync`.
- This ensures `RequiredPackageForPush.MatchedConnectorId` in the push plan points to a registry that can be polled for `PackageVersionExistsAsync` during the “wait for dependencies” step.

---

## 7. Configuration

- **`WorkspaceOptions`** (e.g. `Workspace` config section):
  - `PushWaitDependencyTimeoutMinutesPerDependency`: minutes per required dependency when waiting for packages in registry (default 1.0). Total wait per level = this × number of distinct required packages at that level.
  - `MaxConcurrentGitOperations`: used as `_maxConcurrent` in `WorkspaceGitService` for limiting parallel push (default 8).

---

## 8. Summary flow (synchronized path)

1. User clicks Push → UI filters to repos with unpushed commits (and selection if any) → optional PushModal → `RunPushCoreAsync`.
2. App: sync package registries → get push plan → filter by `repoIdsToPush`.
3. If all required packages have matched connectors and NuGet/connector services exist: for each dependency level (low to high):
   - Wait (with timeout) until all required packages for that level are present in their registries (`PackageVersionExistsAsync`).
   - Push all repos at that level in parallel via agent `PushRepository` (throttled by `MaxConcurrentGitOperations`).
   - Refresh version for those repos (`RefreshRepositoryVersion`).
4. If synchronized push not possible: push all repos in one batch, then refresh versions.
5. Broadcast `WorkspaceSynced`; UI refreshes.

Agent: `PushRepository` resolves repo path, gets current branch (GitVersion), runs `git push -u origin <branch>` with optional bearer token, returns success or error message.
