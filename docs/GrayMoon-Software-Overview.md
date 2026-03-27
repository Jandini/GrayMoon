# GrayMoon: Software Overview

## What GrayMoon is

GrayMoon is a **workspace orchestration tool for multi-repository .NET development**.

It is built for teams working across many repositories (for example microservices, shared libraries, and NuGet packages) where one feature often requires coordinated changes in multiple places.

GrayMoon provides:

- a web app to manage workspaces, repositories, dependencies, branches, and automation status
- a host-side agent that runs on your machine and executes Git/file operations safely on local repositories

In short: GrayMoon helps you treat many repositories like one coordinated system during feature development.

## The main problem it solves

When you change one service or package, other repositories often need updates too:

- dependency versions drift
- branches are inconsistent across repos
- pushes happen in the wrong order
- CI fails because a dependent package/version is not available yet
- developers spend time manually checking statuses, PRs, actions, and branch divergence repo-by-repo

GrayMoon centralizes this workflow so these steps are visible, repeatable, and much less error-prone.

## Core capabilities

### 1) Workspace-based multi-repo management

- Create workspaces and assign repositories to each workspace.
- Set a default workspace for fast navigation.
- Import repository assignments based on repositories already present on disk.
- Manage workspace root path centrally.

### 2) Connector management (GitHub and NuGet)

- Add/edit/delete connectors.
- Test connector health and automatically mark failed connectors.
- Fetch repositories from GitHub connectors and persist them.
- Use NuGet connector metadata for package-registry synchronization.

### 3) Agent-powered local execution

- GrayMoon App does not directly touch local git repos in a containerized setup.
- GrayMoon Agent performs local git and filesystem actions and communicates with the app over SignalR.
- Includes install/uninstall support and host prerequisite checks (.NET, Git, GitVersion).

### 4) Workspace repository control panel

For each repository in a workspace, GrayMoon tracks and displays:

- current version
- current branch
- incoming/outgoing commits
- divergence from default branch (ahead/behind)
- pull request status
- CI action status
- dependency mismatch indicators
- sync state/errors

This gives one operational view instead of opening each repository separately.

### 5) Branch orchestration across repositories

- Create branches (single repo or workspace-wide patterns).
- Checkout/switch branches.
- Refresh local and remote branch lists.
- Set upstream tracking.
- Delete local/remote branches safely.
- One-click sync back to default branch (single repo or multiple repos in a dependency level).
- After a PR is merged, GrayMoon can sync back to default by checking out the default branch, pruning the local feature branch when there is no commit drift, and pulling the latest changes.
- Detect common branches across workspace repositories.

### 6) Sync and commit synchronization

- Sync installs repository hooks (checkout/commit/push/merge) so GrayMoon can keep the workspace state updated even while developers keep working normally in their IDE.
- When the workspace needs version/dependency alignment, GrayMoon can automatically run the dependency update flow (updating `PackageReference` versions and committing those changes as part of the coordinated workflow).
- Commit synchronization endpoint for repository-level commit state refresh.
- Agent queue visibility and progress overlays in UI.
- Real-time UI updates through SignalR workspace sync events.

### 7) Dependency-aware updates for .NET repositories

- Reads project data from `.csproj` files.
- Builds dependency update plans and executes them in dependency-level order.
- Performs sync -> commit -> version refresh flow level-by-level to reduce breakage.
- Recomputes dependency mismatch stats after operations.

### 8) Synchronized push workflow (important differentiator)

GrayMoon supports **dependency-synchronized pushes**:

- computes push plan by dependency levels
- can sync package registries first
- waits for required package versions to appear in registries (when mappings exist)
- pushes repositories in level order
- supports parallel push mode when synchronization is not required
- can push multiple repositories simultaneously (up to configured concurrency), which is faster than pushing one repository at a time from an IDE

This directly addresses CI failures caused by missing dependency versions at push time.

### 9) Files and version-pattern automation

- Add workspace files to track.
- Search files via agent in workspace repositories.
- View file content from the UI.
- Configure version replacement patterns using tokens like `{RepositoryName}`.
- Bulk-update configured files with latest repository versions from workspace state.
- Optional commit flow for version-file updates.

### 10) Dependency graph, projects, and packages visibility

- Workspace dependency graph visualization with filtering by repo or dependency level.
- Automatic repository grouping by dependency levels based on dependency hierarchy.
- Grouped visualization by repository type (services and packages/libraries) to make dependency flow easier to understand.
- Project inventory view (type/framework/path).
- Package inventory view and registry mapping checks.
- Package registry sync actions for workspace packages.

### 11) PR and GitHub Actions awareness

- Pull request state persistence and refresh per repository branch.
- GitHub Actions aggregate status visibility per repository/branch.
- Workspace-level view of GitHub Actions results for the current branch across all repositories in the workspace.
- Open multiple repositories in GitHub from GrayMoon in one action.
- Open new pull request pages in GitHub for multiple repositories in a selected dependency level.
- Track changed files across repositories so you can verify whether feature-branch commits actually produce PR-worthy changes; if only dependency-version updates exist, you can skip creating unnecessary PRs.
- Re-run failed workflows from the actions workspace view.

### 12) GitVersion compatibility per repository

- GrayMoon can use globally installed GitVersion.
- If a repository contains a .NET tool manifest for GitVersion, GrayMoon can use the version required by that repository.
- This supports mixed environments where some repositories need GitVersion 5.x and others require 6.x.

## Typical feature-development workflow with GrayMoon

1. Configure connectors (GitHub/NuGet) and fetch repositories.
2. Create a workspace and add all repositories involved in your feature.
3. Create/switch to a feature branch across required repositories.
4. Implement changes (including with AI IDE tooling across all repos).
5. Run GrayMoon sync/update to align dependency versions and project state.
6. Use dependency-aware push (or synchronized push) so package and repo ordering is safe.
7. Monitor PR status, CI actions, branch divergence, and commit flow from one UI.
8. Sync repositories back to default branch after merge.

## Benefits

- **Fewer integration surprises**: dependency and push order awareness reduces avoidable CI failures.
- **Faster multi-repo delivery**: one coordinated workflow replaces many manual per-repo steps.
- **Better visibility**: branch/version/PR/action/commit health in one place.
- **Safer automation**: concurrency control, queueing, and explicit progress for long operations.
- **Good fit for AI-assisted development**: workspaces map naturally to cross-repo feature work where AI needs context from many codebases.
- **Bulk dependency rollout**: when a shared package changes, GrayMoon can create a feature branch across required repositories, update package references automatically, commit changes, and let you push and test all updates in one coordinated flow.

## Who this helps most

GrayMoon is especially useful for:

- teams maintaining microservices plus shared libraries
- organizations publishing internal/shared NuGet packages
- platform teams that coordinate version bumps and dependency rollouts
- developers doing frequent cross-repository feature branches and synchronized releases

## Positioning statement

GrayMoon is a multi-repository orchestration layer for .NET teams that coordinates branches, dependency updates, and pushes across workspaces so cross-repo feature delivery becomes faster, safer, and easier to operate.

## Stack and versioning model

GrayMoon is designed around a .NET stack:

- .NET / C#
- semantic versioning with GitVersion
- repository-level versions captured in workspace state
- cross-repository dependency updates based on current branch versions

In practical terms, teams often run a model where each feature-branch commit advances package/service versions. GrayMoon helps keep those versions aligned across dependent repositories and updates references so builds stay consistent both inside and outside developer machines.

## How the dual-reference `.csproj` pattern works

The common pattern is:

1. Define a `UseProjectRefs` switch based on `$(SolutionFileName)`.
2. Define `*ProjectPath` properties for dependency projects in sibling repositories.
3. Add `PackageReference` entries when `UseProjectRefs` is `false`.
4. Add `ProjectReference` entries when `UseProjectRefs` is not `false` and project files exist.
5. Use `Exists(...)` guards for optional dependencies or partial checkouts.

Result:

- building from the service's own solution (or no solution, e.g., Docker) uses NuGet packages
- building from the sibling solution context can use project references automatically
- fallback package references still work if a dependency repository is missing locally

## New example

### Example system shape

- Workspace root: `C:\Workspace\Platform`
- Sibling repository for cross-repo development: `platform-workspace\Platform.sln`
- Service repository: `platform-orders-service`
- Shared dependency repos:
  - `Platform.Contracts`
  - `Platform.Messaging`
  - `Platform.Persistence`

In this document, a **sibling repository** means a repository that lives next to another repository under the same workspace root, so projects can be referenced through relative paths during integrated development.

### Recommended setup (what is developer convenience vs what GrayMoon manages)

In this setup:

- `ProjectReference` switching is **developer convenience only** (it makes IDE “open the solution” experience nicer by enabling source-level references in the sibling solution).
- `PackageReference` versions are the **canonical dependency contract** that GrayMoon manages.

GrayMoon updates `PackageReference` entries by the `Include` name, so keep explicit `PackageReference Include="..."` lines in the `.csproj`.

### Service `.csproj` (project refs for sibling-solution dev, packages as the managed baseline)

`platform-orders-service\src\Platform.Orders.Service\Platform.Orders.Service.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- Default: package mode -->
    <UseProjectRefs>false</UseProjectRefs>
    <!-- Sibling solution context: project-reference mode -->
    <UseProjectRefs Condition="'$(SolutionFileName)' == 'Platform.sln'">true</UseProjectRefs>
  </PropertyGroup>

  <PropertyGroup>
    <ContractsProjectPath>..\..\..\Platform.Contracts\src\Platform.Contracts\Platform.Contracts.csproj</ContractsProjectPath>
    <MessagingProjectPath>..\..\..\Platform.Messaging\src\Platform.Messaging\Platform.Messaging.csproj</MessagingProjectPath>
    <PersistenceProjectPath>..\..\..\Platform.Persistence\src\Platform.Persistence\Platform.Persistence.csproj</PersistenceProjectPath>
  </PropertyGroup>

  <ItemGroup>
    <!-- Package mode, plus fallback when sibling project is missing -->
    <PackageReference Include="Platform.Contracts"
                      Version="1.2.0-feature-x.4"
                      Condition="'$(UseProjectRefs)' == 'false' OR !Exists('$(ContractsProjectPath)')" />
    <PackageReference Include="Platform.Messaging"
                      Version="2.3.1-feature-x.7"
                      Condition="'$(UseProjectRefs)' == 'false' OR !Exists('$(MessagingProjectPath)')" />
    <PackageReference Include="Platform.Persistence"
                      Version="3.1.0-feature-x.2"
                      Condition="'$(UseProjectRefs)' == 'false' OR !Exists('$(PersistenceProjectPath)')" />

    <!-- Project mode when sibling project exists -->
    <ProjectReference Include="$(ContractsProjectPath)"
                      Condition="'$(UseProjectRefs)' != 'false' AND Exists('$(ContractsProjectPath)')" />
    <ProjectReference Include="$(MessagingProjectPath)"
                      Condition="'$(UseProjectRefs)' != 'false' AND Exists('$(MessagingProjectPath)')" />
    <ProjectReference Include="$(PersistenceProjectPath)"
                      Condition="'$(UseProjectRefs)' != 'false' AND Exists('$(PersistenceProjectPath)')" />
  </ItemGroup>
</Project>
```

## How GrayMoon organizes repositories in one Workspace

GrayMoon is designed to bring many existing repositories together under one Workspace folder so cross-repository development becomes a single flow instead of a repeated manual process.

### Workspace-first repository organization

- Choose a Workspace root path (for example, `C:\Workspace\Platform`).
- Create a workspace in GrayMoon (for example, `PlatformFeatureA`).
- Select multiple repositories from your connector list and assign them to that workspace.
- GrayMoon keeps those repositories grouped as one working set for branch, sync, update, push, and status operations.

### Multi-repository clone and checkout workflow

- In one workspace operation, select many repositories and clone them into the same workspace folder structure.
- Create or checkout a branch across selected repositories in one action.
- Keep branch names aligned (for example, `feature/dependency-update`) across all selected repositories.
- Continue with synchronized updates, commits, and pushes from the same workspace context.

### Why this matters

- You avoid repetitive per-repo clone and checkout commands.
- You can start feature work across many repositories much faster.
- You keep repositories organized by feature/workstream, not just by individual repo ownership.
- You reduce mistakes caused by one repository being on a different branch than the others.

## Version files: why they matter and how GrayMoon helps

GrayMoon can update version tokens not only inside `.csproj`, but also in any configured file where service/package versions are required for runtime or deployment consistency.

Examples:

- `docker-compose.yml` image tags
- `.env` values
- deployment manifests (Helm values, Kubernetes YAML)
- pipeline YAML files
- app config files (`appsettings.*.json`, custom config)

Using version-file patterns, GrayMoon can:

- map placeholders to repository names/tokens
- replace values with current tracked versions from workspace repositories
- update many files in one operation
- optionally commit those file changes as part of orchestration flow

This is especially valuable when teams need reproducible builds and deployments outside local IDE scenarios, because the same version alignment is carried into configuration artifacts.

## Package references: the GrayMoon-managed dependency contract

GrayMoon’s dependency engine is built around `PackageReference` versions:

- when you create a workspace and work on a feature branch, GrayMoon tracks the versions it detects for each repository in that workspace
- when you run dependency updates, GrayMoon updates `PackageReference` versions (by `Include` name) across all impacted repositories
- it can commit those `.csproj` version changes for you, so you can push them together and test as one coordinated release unit

This is what enables the “shared library/package changed once, everything else updates automatically” workflow:

- create a feature branch for a dependency rollout (for example `feature/dependency-update`)
- let GrayMoon update `PackageReference` versions across the required repositories
- automatically commit the changes
- push all updates in one go, then run your normal build/test/CI validation

### Why the centralized solution + package refs help AI tools

When developers open the sibling solution in editors like VS Code or Cursor, `ProjectReference`-based source wiring improves navigation and understanding for code-aware AI tooling.

At the same time, keeping `PackageReference` versions aligned ensures those edits compile consistently and remain correct for CI and for isolated builds that use packages.


