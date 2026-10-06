# Workspace as a Git Repository - Design v3 Review Against the Codebase

**Reviewed document:** `GrayMoon-Workspace-As-Git-Repository-Design-v3.md`  
**Code baseline:** `workspace-as-git-repository` branch at `f7f94ce` (parent `ee5b536`, which includes workspace profiles `a6d951d`)  
**Date:** 2026-10-06  
**Outcome:** The architecture is sound and matches the code's existing boundaries. The design is not implementable as written because it assumes the App resolves repository paths and writes files; in the real system the Worker does both. Those gaps are closed by `Workspace-Repository-Design-Supplement-v3.1.md`, which is binding over v3 where they differ.

---

## 1. How to read this review

Every finding cites the file that proves it. Severity:

| Level | Meaning |
|---|---|
| BLOCKER | The design cannot ship, or would corrupt a Workspace, until this is resolved. |
| MAJOR | A phase of the design is under-specified and an implementer would have to invent architecture. |
| MINOR | Clarification, hygiene, or a cheap improvement. |

Section 2 is what the design gets right and why that matters for the implementer. Section 3 is the findings. Section 4 is the list of improvements, each mapped to the supplement decision that adopts it.

---

## 2. What is good

### 2.1 Role on the link, not a new `WorkspaceType`

The codebase already separates global repository identity from Workspace membership:

- `src/GrayMoon.App/Models/Repository.cs` - connector, `CloneUrl`, `GitHubRepositoryId`, `NodeId`.
- `src/GrayMoon.App/Models/WorkspaceRepositoryLink.cs` - table `WorkspaceRepositories`, one row per `(WorkspaceId, RepositoryId)` (unique index in `AppDbContext.cs` line 170), carrying the special Workspace context's Git projection.

Adding `Role` to the link is the only place that fits. The three profile axes on `Workspace` (`Type`, `VersioningMode`, `CiProvider`, `Workspace.cs` lines 36-48) are explicitly documented as "says nothing about" storage, so refusing a fourth `WorkspaceType` value is consistent with how the profiles work was done.

### 2.2 "Always part of every Feature" matches the code's own invariant

`WorkspaceFeatureOperations.CreateFeatureCoreAsync` (`src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs` lines 116-233) loads every link for the Workspace and creates one `WorkspaceFeatureRepository` row per link. There is no per-repository opt-out today, so the design's hard invariant adds no new concept; it only adds ordering (root first).

### 2.3 Context isolation rules are already enforced and documented

`AGENTS.md` "Feature-context scoping" and `.cursor/rules/feature-context-scoping.mdc` already require every read/write to be context-scoped. The design's 5.6 / 15.2 / 26.2 (Feature `.graymoon.json` is non-authoritative) is a direct application of that rule rather than a new one.

### 2.4 Hook attribution needs no new mechanism

`WorkspaceHookContextAttributor.ResolveAsync` (`src/GrayMoon.App/Services/Features/WorkspaceHookContextAttributor.cs` lines 56-73) compares the hook's `repositoryPath` against `pathResolver.GetRepositoryPathAsync(...)` for the special context and against every `WorkspaceFeatureRepository.WorktreePath`. Once the resolver is role-aware (design 17), hooks from the Workspace repository attribute correctly with zero changes to the attributor or to the hook scripts. The design's 33.4 is therefore already satisfied by construction.

### 2.5 Watcher model is already "invalidation only"

`GitRepositoryWatcher` (`src/GrayMoon.Worker/Services/GitChanges/GitRepositoryWatcher.cs`) raises `Changed` as a hint and the coordinator re-runs `git status`. Design 34.5 describes exactly this. No new watcher subsystem is needed, which the design also says (34.4).

### 2.6 Portable identity has an index waiting for it

`AppDbContext.cs` line 91 already defines `IX_Repositories_CloneUrl`. Matching manifest `repositoryUrl` against local `Repository.CloneUrl` is an indexed lookup. `RepositoryUrlHelper` (`src/GrayMoon.App/Services/GitHub/RepositoryUrlHelper.cs`) already parses `git@host:owner/repo.git` and HTTPS forms and strips `.git`, so the URL identity service (design 38.2) is a consolidation of existing code, not new parsing.

### 2.7 Structural guards already exist where the design wants them

`WorkspaceRepository.AddRepositoriesAsync` (line 437) and `ReplaceRepositoriesCoreAsync` (line 515) refuse membership changes while Features exist; `UpdateAsync` (line 131) refuses rename and root changes. Design 30 and 31 only need the same guard applied to role changes and to enabling/disabling the Workspace repository.

### 2.8 Migration pattern is established

`Migrations.StrictSteps` (`src/GrayMoon.App/Migrations.cs` line 34) is the versioned, transactional path. The next free version is 5. The SQLite filtered unique index the design asks for (7.3) has a precedent: `IX_Repositories_ConnectorId_GitHubRepositoryId` uses `.HasFilter(...)` (`AppDbContext.cs` line 88).

---

## 3. Findings

### F1 - BLOCKER - The Worker computes repository paths itself; the App resolver is not the source of truth

Design 17 states: "The path resolver remains the single source of truth. Callers must not reproduce these rules."

The Worker reproduces the rule in 35 command handlers:

```text
src/GrayMoon.Worker/Commands/SyncRepositoryCommand.cs:31          var repoPath = Path.Combine(workspacePath, repositoryName);
src/GrayMoon.Worker/Commands/GetGitChangeStatusCommand.cs:25      var repoPath = Path.Combine(workspacePath, repositoryName);
src/GrayMoon.Worker/Commands/GetHeadCommitsCommand.cs:49          var repoPath = Path.Combine(workspacePath, repoName);
src/GrayMoon.Worker/Commands/PushRepositoryCommand.cs:44          ...
(31 more)
```

where `workspacePath = git.GetWorkspacePath(request.WorkspaceRoot, request.WorkspaceName)`. The App only sends `(workspaceRoot, workspaceName, repositoryName)` - see `WorkspaceGitService.Sync.cs` line 39 and `GitChangesWorkerClient.cs` line 68 - and `IWorkspaceContextPathResolver.GetWorkerWorkspaceArgsAsync` exists precisely to split the context root into that pair. The App never sends an absolute repository path except for worktree commands.

Consequence if a Workspace-role link is created before this is fixed: `SyncRepositoryCommand` computes `<root>\AVR\AVR.Workspace`, finds no directory, and **clones a second copy of the Workspace repository into a subfolder**. Every Git operation on the Workspace repository would then target that stray clone, not the Workspace root.

A second consequence is deployment ordering: an old Worker that ignores a new request field does the same thing. The design has no compatibility gate.

Adopted as supplement D1 (Worker path contract) and D2 (compatibility gate). This is Phase 0 of the plan; nothing else may ship before it.

### F2 - BLOCKER - The App cannot write `.graymoon.json` or `.gitignore`

Design 26.1, 16.1 and 26.4 say GrayMoon "writes" and "regenerates" files. `CLAUDE.md` Architecture: "GrayMoon.App ... Never touches the local filesystem or runs git directly."

The Worker has no general file-write command. `GetFileContentsCommand` reads a repository-relative file; `UpdateFileVersionsCommand` only substitutes version tokens in already-pinned files. Writing the manifest and the managed `.gitignore` section needs a new Worker command with path validation (`GitRepositoryPathValidator`) and deterministic content handling.

Adopted as supplement D5.

### F3 - BLOCKER - A local-only Workspace repository is not representable

Design 29.1 step 6: "optionally publish/connect remote later".

`Repository.ConnectorId` and `Repository.CloneUrl` are `[Required]` (`Repository.cs` lines 11-12, 30-32). `ConnectorType` is `GitHub | NuGet` only (`src/GrayMoon.App/Models/ConnectorType.cs`). Repositories enter the database through GitHub discovery (`GitHubRepositoryService`); there is no "local Git" connector and no way to create a `Repository` row without one. A `WorkspaceRepositoryLink` cannot exist without a `Repository` row, so "initialize Git at the root and link it as the Workspace repository" has nothing to link.

Options: (a) introduce a Local connector type and a Repository row with no remote, or (b) require the Workspace repository to be an existing GitHub repository reachable through a connector (remote-first). Option (b) is a strict subset of the design, reuses every existing code path (PRs, Actions, clone-on-sync), and defers only the offline case.

Adopted as supplement D3 (remote-first for v1).

### F4 - MAJOR - The convert flow is mis-scoped

Design 29.4 defers "attach unrelated remote to non-empty root". But the normal conversion target (29.2) is a Workspace root that **already contains Source repository folders**, which is exactly a non-empty root. `GitService.CloneAsync(workingDir, cloneUrl, ...)` (`src/GrayMoon.Worker/Services/GitService.cs` line 29) clones into a subdirectory named from the URL and cannot populate an existing non-empty directory.

The required Worker operation is: `git init` at the root (if no `.git`), `git remote add origin <url>`, `git fetch`, then either check out the remote default branch (fails cleanly if tracked files collide with existing files) or, for an empty remote, stay on the unborn branch. Design 29.2 and 29.3 both reduce to this one operation plus "write managed files".

Adopted as supplement D4 (`AttachWorkspaceRepository` command).

### F5 - MAJOR - Connector URL mapping is unspecified

Design 10 uses `"url": "https://github.com"`. The persisted `Connector.ApiBaseUrl` is `https://api.github.com/` (`Connector.cs` line 19) or `https://ghe.company.com/api/v3`. The mapping exists in `RepositoryUrlHelper.GetWebRootFromConnectorApiBase` (lines 161-179), but the design never says which value is the manifest identity, nor how a manifest `url` is matched back to a local connector.

Adopted as supplement D11.

### F6 - MAJOR - Project discovery and file search would recurse into Source repositories

Design 8.1 says the Workspace-role repository "never participates in .csproj discovery" and "this should be decided at an orchestration boundary using repository role".

`SyncRepositoryCommand` runs `.csproj` discovery for every repository whose request carries `discoverDotNetProjects != false`; `CsProjFileService` (line 71) excludes only `.git` folders. The App resolves capabilities once per Workspace and sends the same object for every repository (`WorkspaceGitService.Sync.cs` lines 56-88). To honour 8.1 at the App boundary, every one of those send sites would need a per-repository capabilities override.

The same recursion affects `WorkspaceFileSearchService` (Worker, line 43 excludes `.git` only) and `GetWorkspaceRepositoriesCommand`, which lists root subfolders with Git metadata and would now also see the root itself as "a repository" if it walks one level too high.

Cheaper and safer: the Worker already learns which repository is the Workspace root from D1; it can suppress project discovery for that repository in one helper. The App-side rule then only has to suppress UI (25.1).

Adopted as supplement D6.

### F7 - MAJOR - Watcher exclusion has no implementation path

Design 34.2: "watcher exclusions = all Source repository roots ... built from GrayMoon's repository/context model, not from filename heuristics."

`GitRepositoryWatcher` is constructed with one `repoPath` and `IncludeSubdirectories = true` (lines 48-51); `GitRepositoryWatcherManager.Acquire(string repoPath)` (line 33) has no exclusion parameter. The Worker has no model of Workspace membership. To build the exclusion list from the model, `GetGitChangeStatusRequest` would need a `nestedRepositoryNames` list and every caller would need to supply it.

Observation: a nested Git repository is a physical fact (`<child>\.git` exists, file or directory), and git itself treats it as a boundary. `GetWorkspaceRepositoriesCommand.HasGitMetadata` already uses that test. Excluding events under any immediate child directory that has Git metadata is deterministic, needs no wire change, and matches what `git status` reports.

Adopted as supplement D7 with the rationale recorded; the model-driven list remains a possible later refinement.

### F8 - MAJOR - Manifest change detection cannot come from Git Changes

Design 34.7: the watcher "may be used as the low-level signal that `.graymoon.json` changed".

After `git pull` or `git checkout`, the manifest matches HEAD, so `git status` shows no entry and the Git Changes projection carries no signal. The only reliable trigger is: after a Workspace-role repository operation completes in the special context (sync, checkout, pull, hook sync), read `.graymoon.json` through `GetFileContents` and compare it semantically against the canonical manifest generated from the database.

Adopted as supplement D8.

### F9 - MAJOR - Feature create, repair and remove are single-phase today

`CreateFeatureCoreAsync` fans out all rows in one `Task.WhenAll` behind a `SemaphoreSlim` (lines 257-326). Remove does the same (lines 759-790). The repair "Retry" path re-attempts Pending rows the same way. Design 20 and 21 require the Workspace-role row to be created before, and removed after, all Source rows. Each of the three paths needs a two-phase split.

`RemoveGitWorktreeRequest.FeatureRootPath` / `FeatureStorageRoot` (Worker residue cleanup that deletes the Feature root when empty) must not run against a root that is itself a worktree; `git worktree remove` on the root removes the directory.

Adopted as supplement D10.

### F10 - MINOR - `.gitignore` committed state and Features

If a Feature is created from a Workspace-repository branch that does not yet contain the managed `.gitignore` section, the Source worktrees under the Feature root show as untracked directories in the Workspace repository's Feature Git Changes. This is correct Git behaviour and self-heals once the section is committed and merged; the design should say so rather than imply it never happens.

### F11 - MINOR - Push ordering and null dependency level

`PushOrchestrator` and `WorkspaceRepository.GetRepositoryIdsAtLevelAsync` order by `DependencyLevel`. The Workspace repository has no level. The plan must verify that a null-level repository is pushed (likely as its own group) rather than silently skipped in a `.NET Dependency` Workspace.

### F12 - MINOR - Worker and App version skew

Beyond F1, the design should state the general rule used elsewhere in the codebase (`RepositoryOperationCapabilities` remarks): a payload from an old App deserialises to "not stated" and the Worker keeps today's behaviour. For this feature, "today's behaviour" is wrong for the Workspace repository, hence the explicit gate (D2).

### F13 - MINOR - Document hygiene

- Section headings 37-46 have sub-numbers one lower than their parent (`## 37` contains `### 36.1`, `## 46` contains `### 45.1`). Fixed in v3 as part of this review.
- The file is LF-only; `CLAUDE.md` requires CRLF. Converted.
- Design 17 says `GetRepositoryPathAsync` "effectively assumes context root + repository name". For Feature contexts it already prefers the persisted `WorktreePath` (`WorkspaceContextPathResolver.cs` lines 66-81); only the special-context branch and the fallback assume the name. 17.2 is therefore already true for Source rows.
- Design 43 phases omit the Worker entirely. Every phase that touches paths, files or watchers is Worker work first.

---

## 4. Improvements adopted

| # | Improvement | Supplement decision | Plan unit |
|---|---|---|---|
| 1 | Worker path contract: `workspaceRepositoryName` on `WorkspaceCommandRequest`, one Worker helper, App resolver returns a triple | D1 | W1, C |
| 2 | Compatibility gate via `GetHostInfo.supportedFeatures` | D2 | W1, E |
| 3 | Remote-first Workspace repository in v1 | D3 | D, E |
| 4 | `AttachWorkspaceRepository` Worker command covers init, convert and restore | D4 | W2, D, G |
| 5 | `WriteRepositoryFile` Worker command for manifest and `.gitignore` | D5 | W2, D |
| 6 | Workspace-root repository never discovers projects (Worker rule) | D6 | W1 |
| 7 | Watcher excludes nested repositories physically | D7 | W2 |
| 8 | Manifest drift detected by read-and-compare after Workspace-repo operations | D8 | D |
| 9 | Feature create/repair/remove become two-phase | D10 | F |
| 10 | Connector identity = normalized web root derived from `ApiBaseUrl` | D11 | B, D |
| 11 | Source-scan guard test so no App send site forgets the new field | D1 | C |

---

## 5. What was not reviewed

- GitHub Enterprise connector behaviour beyond URL mapping.
- Desktop (`GrayMoon.Desktop`) integration; no change expected because the Workspace repository is an ordinary repository row.
- Performance of `git status` on a root containing many nested repositories; expected to be fine because ignored directories are not enumerated, but Unit I should measure it once with a 10-repository Workspace.
