# Workspace Profiles - Design

Living design document. It describes the **implemented** design, not the original proposal. When
implementation proves an assumption wrong, this document is updated in the same change that adjusts
the code.

Companion: `Workspace-Profiles-Implementation-Plan.md` (execution state).

Status: Phases 1-4, the CI provider boundary (section 11a), host readiness (section 11b), the
Repositories grid (section 10a), page access (section 11), and create/edit plus profile transitions
(section 10) are implemented. Remaining work is regression and Feature assurance (Unit I).

---

## 1. Problem statement

GrayMoon assumes every workspace is a .NET solution-style workspace: it runs GitVersion on every
repository, scans for `.csproj` files, builds a NuGet dependency graph, orders pushes by dependency
level, and shows GitHub Actions. A user who just wants multi-repository Git (branches, Features,
pull requests, changes, workspace files) pays for all of it, and is told their repositories have an
"unresolved version" when GitVersion was never relevant.

The goal is first-class workspace types without scattering `if (workspace.Type == ...)` through the
codebase.

## 2. The three axes

GitVersion is **not** the same concern as .NET dependency management, and CI is neither. A user may
legitimately want a Basic Git workspace that still uses GitVersion, or a .NET workspace with no CI
integration. So three independent settings are persisted on `Workspace`:

```text
WorkspaceType            Basic | DotNetDependency
WorkspaceVersioningMode  None  | GitVersion
WorkspaceCiProvider      None  | GitHubActions
```

These are the **only** persisted choices. Everything else is derived (section 4). `WorkspaceCiProvider`
is an enum rather than a `UseGitHubActions` bool so a future provider needs no schema redesign.

### Defaults

| Situation | Type | Versioning | CI |
|---|---|---|---|
| Model default (fresh DB row) | `Basic` | `None` | `None` |
| Existing workspace (migration) | `DotNetDependency` | `GitVersion` | `GitHubActions` |
| New Basic workspace | `Basic` | `None` | `None` |
| New .NET Dependency workspace | `DotNetDependency` | `GitVersion` | `GitHubActions` |

The migration default is a **compatibility requirement**, not a convenience: every existing GrayMoon
workspace is assumed to be a .NET/GitVersion/GitHub Actions workspace until the user says otherwise,
and must behave exactly as it does today with no user action.

The model defaults are deliberately the opposite (`Basic`/`None`/`None`) so a brand-new database
created by `EnsureCreated()` starts clean; the application supplies the .NET triple explicitly when
the user picks that type. See section 9 for why this split matters.

## 3. Features inherit; there is no per-Feature profile

A profile is a property of the parent `Workspace`. Worktree-backed Features inherit it.

The only Feature-level metadata that exists is worktree identity - `PinnedTag`, `ParentBranchName`,
`BaseCommitSha`, `WorktreePath` - never configuration. So:

> `IWorkspaceCapabilitiesResolver` resolves by `WorkspaceId` only. It must never take a
> `WorkspaceFeatureContextId` or branch on `WorkspaceFeatureContextKind`.

This mirrors the existing `isSpecialWorkspace` convention, where the context changes only *where*
state is mirrored, never *what* is computed.

## 4. Derived capabilities

One resolver turns the three persisted values into one immutable record. Call sites ask the
capability layer, not the enum.

```csharp
public sealed record WorkspaceCapabilities(
    WorkspaceType Type,
    WorkspaceVersioningMode VersioningMode,
    WorkspaceCiProvider CiProvider)
{
    public bool UsesRepositoryVersioning => VersioningMode != WorkspaceVersioningMode.None;
    public bool UsesGitVersion          => VersioningMode == WorkspaceVersioningMode.GitVersion;
    public bool DiscoversDotNetProjects => Type == WorkspaceType.DotNetDependency;
    public bool UsesDependencyGraph     => Type == WorkspaceType.DotNetDependency;
    public bool UsesNuGetPackages       => Type == WorkspaceType.DotNetDependency;
    public bool UsesDependencyAwareUpdate => Type == WorkspaceType.DotNetDependency;
    public bool UsesDependencyAwarePush => Type == WorkspaceType.DotNetDependency;
    public bool UsesPackageRestore      => Type == WorkspaceType.DotNetDependency;
    public bool UsesGeneratedPackagesFromVersionFiles => Type == WorkspaceType.DotNetDependency;
    public bool UsesCiIntegration       => CiProvider != WorkspaceCiProvider.None;
    public bool UsesGitHubActions       => CiProvider == WorkspaceCiProvider.GitHubActions;

    public RepositoryOperationCapabilities ToRepositoryOperationCapabilities();  // Worker wire subset
    public static WorkspaceCapabilities Legacy { get; }                          // the pre-profile triple
}
```

The .NET-type capabilities all derive from `Type` today, but they are separate properties because they
gate different producers (section 8) and a future workspace type may want only some of them.

Capability booleans are **derived, never persisted**. If the implementation starts accumulating
`if (workspace.Type == ...)` checks in unrelated files, that is the signal to refactor toward a
strategy seam instead of adding another branch.

Capability decisions belong at orchestration and UI boundaries. Low-level Git code must not know
what a "Basic workspace" is.

## 5. How capabilities reach the Worker

This is the part of the design most likely to be got wrong, so it is written out in full.

GrayMoon has **two directions** of App/Worker traffic.

```mermaid
flowchart LR
  subgraph d1 [Direction 1: App-initiated - the normal case]
    UI["Page / REST / orchestrator"] --> Bridge["WorkerBridge.SendCommandAsync"]
    Bridge --> Hub["WorkerHub: RequestCommand"]
    Hub --> Exec["Worker command handler"]
    Exec --> Resp["ResponseCommand - typed result"]
    Resp --> Persist["App persists"]
  end
  subgraph d2 [Direction 2: Worker-initiated - git hooks]
    Git["Developer runs git commit / checkout / merge / push"] --> Script["Managed hook script"]
    Script --> Listener["Worker loopback listener 127.0.0.1:9191"]
    Listener --> Job["Worker notify job probes the repo"]
    Job --> Notify["SyncCommand notification to the App"]
    Notify --> Writer["SyncCommandHandler + state writer"]
  end
```

**Direction 1** covers every user-initiated operation. The App is the caller, so capabilities ride in
the request: `RepositoryOperationCapabilities` on `WorkspaceCommandRequest`, the base class every
command request already inherits.

**Direction 2** is the exception. The trigger is the developer's own `git` invocation, possibly from
the CLI with GrayMoon closed. Git runs the hook script, which POSTs
`{repositoryId, workspaceId, repositoryPath}` to the Worker's loopback listener; the Worker probes the
repository and pushes a notification *up* to the App. The App is the recipient, not the caller, so the
four hook commands have no request to read capabilities from.

The Worker resolves them instead through `IWorkspaceCapabilityProvider`, modelled directly on the
existing `WorkerTokenProvider`:

1. a per-`workspaceId` in-memory cache, warmed in `CommandDispatcher.ExecuteAsync` as a side effect of
   every inbound command that carries capabilities. Note that `WorkspaceCommandRequest` has no
   `WorkspaceId` of its own - three derived types declare their own - so warming is an explicit
   per-type list rather than a base-class check;
2. on a cold miss, `GET /workspaces/{workspaceId}/capabilities` with the worker-secret header - a new
   endpoint alongside the existing `/repos/{id}/connector`, covered by the same `WorkerSecretMiddleware`
   rule;
3. if neither is available (no `AppApiBaseUrl`, App unreachable), fall back to **today's full
   enrichment**. An existing .NET workspace must never silently lose its version because the App was
   down; a Basic workspace doing one wasted probe is the strictly safer failure direction.

The Worker therefore never queries App persistence. It asks the App's API for a typed answer, exactly
as it already does for connector tokens, and never opens the SQLite file.

A fallback answer is never cached as if it were authoritative, and a hook sync never fails because
capabilities could not be resolved.

In practice the cache is nearly always warm, because GrayMoon only installs hooks into repositories it
has synced at least once. The REST path exists for Worker restarts.

Feeding capabilities to the four hooks required one preparatory change. The checkout and merge hooks
already funnelled everything through `RepositoryStateProbe`, so each needed a single line. The commit and
push hooks called `GetVersionAsync` and `FindAsync` directly and hand-built their snapshots, so they were
converted onto the probe first - which also removed the hand-built snapshots. The pre-push hook needed
`RepositoryStateProbeOptions.IncludeCommitCounts`, because it fires before the push data is transferred
and must not report counts it cannot yet have read.

### Rejected: baking capabilities into the hook script

The hook script already embeds a JSON payload, so adding two flags looks attractive. It is wrong:

- Hook scripts are **deliberately context-agnostic**. They resolve `$(git rev-parse --show-toplevel)`
  at runtime and send the path so the *App* can attribute it to a context, specifically so no decision
  state is baked into the script. Capability flags would be exactly that decision state.
- Worktrees share the main checkout's hooks directory, and the installer skips rewriting unchanged
  content, so a profile change would leave stale flags on disk until the next successful sync of every
  repository - inventing a hook-rewrite lifecycle nothing else in the system needs.
- It duplicates a problem the repository has already solved once, for tokens.

## 6. Sync and enrichment flow

Pure Git synchronization is the baseline. Versioning and .NET discovery are optional enrichment.

```mermaid
flowchart TD
    Git["Common Git synchronization"] --> Snap["Common repository Git snapshot"]
    Snap --> Ver["Optional: version enrichment"]
    Snap --> Net["Optional: .NET project / package enrichment"]
```

The common snapshot always includes clone/fetch, current branch, current tag, tag list, default
branch, parent/default divergence, upstream state, incoming/outgoing commits, local/remote branch
lists, HEAD identity, and the managed Git hooks.

**Basic + Versioning=None requires neither GitVersion nor the .NET SDK.** No `dotnet gitversion`, and
no `dotnet tool restore` merely because sync ran. The latter needs no separate gate: the Worker's only
`dotnet tool restore` lives inside `GitService.GetVersionAsync` and is reached only after GitVersion has
already failed, so a no-op version provider removes it for free.

### Probed groups are the mechanism

`RepositoryStateSnapshot` already carries per-group `*Probed` markers, and
`WorkspaceRepositoryStateWriter` only replaces a group when its marker is `true`. A skipped enrichment
must therefore report `Probed = false`, never `Probed = true` with a null value - the latter would
clear a known version or prune every project row.

One correction was required before anything could be skipped. `SyncCommandHandler` *inferred* the
markers for notifications that arrive without a `State` snapshot:

```csharp
GitVersionProbed = n.Version != "-" || n.GitVersionFailed,
ProjectsProbed   = n.Projects is { Count: > 0 },
```

`ProjectsProbed` is wrong even today: a genuinely project-free repository is indistinguishable from one
that was never scanned.

The defect was confined to this **notification** path. The command-response path
(`WorkspaceGitService.ResponseParsing.cs`) already built honest snapshots, including a `HasProjectsBlock`
helper that exists precisely to tell an empty project list from an absent one, so no App-side work was
needed there. Four Worker call sites relied on the inference, not two: the commit and push hooks plus
`PushRepositoryCommand.SendPostOperationSyncAsync` and `UndoPushCommand.SendPostResetSyncAsync`. All four
now send an explicit `State` snapshot, as the checkout and merge hooks already did. The inference remains
only as a fallback for notifications from a Worker that predates the `State` field.

No new fields were needed on `RepositorySyncNotification`: `RepositoryStateSnapshot` already carried both
markers, so the correction reduced to making every current Worker send a `State`.

## 7. Versioning behaviour

Version state is three-valued, not two:

```text
not applicable    versioning is disabled for this workspace
provider failed   the version provider ran and could not produce a version
resolved          a version was produced
```

Today "synced but no GitVersion" means unresolved/failure: `WorkspaceRepositoryLink.IsVersionUnresolved`
is `GitVersion` empty while a branch or tag is known. Under `Versioning=None` that would render every
row as permanently broken. The fix is an explicit not-applicable state consumed by the UI, **not** a
redefinition of `IsVersionUnresolved`, whose current meaning is pinned by existing tests.

`"-"` stays a Worker wire placeholder only. It is not the domain model, and the App continues to map it
to `null`.

A Worker-side `IRepositoryVersionProvider` centralizes the decision: a GitVersion implementation and a
no-op implementation, selected from the request capabilities. Every existing GitVersion call site routes
through it, so no call site learns about workspace type.

### How the three states are actually observed

The not-applicable state is **derived**, with no new persisted column and no new enum on the link. Two
pieces make it work:

- `RepositoryVersionResult.Probed` in the Worker, surfaced on `GetRepositoryVersionResponse` as a
  nullable `versionProbed`. This separates "nothing ran" from "a provider ran and failed". A null flag
  means an older Worker and keeps the previous behaviour.
- `WorkspaceCapabilities.UsesRepositoryVersioning` on the App side, consulted at the point of
  consumption. A row is only treated as unresolved if its workspace actually versions its repositories.

Three consumers needed it: `ParseGetRepositoryVersionToStatus`, which otherwise reports `VersionMismatch`
for every row of an unversioned workspace; the version cell in `WorkspaceRepositoriesRow`, whose
three-way decision is extracted as a pure `DetermineVersionCell` method so it can be tested without
bUnit; and the workspace-level `isInSync` rollup in `WorkspaceGitService.SyncAsync`, which would
otherwise mark an unversioned workspace permanently out of sync.

Two further readers treat an absent version as a problem but are **not** version-display issues and are
deliberately left to Unit C, which must stop producing dependency state for a Basic workspace at all:
the unmatched-dependency counts in `WorkspaceProjectRepository.DependencyStats.cs` and
`DependencyLines.cs`.

### Every version entry point

| Entry point | Trigger |
|---|---|
| `SyncRepositoryCommand` | manual / bulk Sync |
| `RefreshRepositoryVersionCommand` | single-repo refresh version |
| `GetRepositoryVersionCommand` | sync-status poll |
| `GetGitVersionAtDefaultTipCommand` | **Feature-only**, see below |
| `CommitSyncRepositoryCommand` | commit sync (pull/push orchestration) |
| `PushRepositoryCommand` | post-push notification |
| `CommitHookSyncCommand` | post-commit / post-update hook |
| `CheckoutHookSyncCommand` | post-checkout hook |
| `MergeHookSyncCommand` | post-merge hook |
| `PushHookSyncCommand` | pre-push hook (immediate + deferred) |
| `RepositoryStateProbe` | shared probe, gated by `IncludeGitVersion` |

`GrayMoon.App/Services/Git/GitVersionCommandService.cs` has no callers. It is left untouched rather than
opportunistically deleted.

### The Feature-only default-tip path

`WorkspaceGitService.Sync.cs` calls `ApplyDefaultTipVersionsForMergedFeatureReposAsync` after **every**
Feature-context sync. It returns early for the special Workspace, then issues `GetGitVersionAtDefaultTip`
for each repository with a merged PR and overwrites that context's `GitVersion`. This exists so Update
Dependencies can write default-branch version strings without checking out the default branch.

It has no Workspace-context equivalent, so it does not appear when tracing Workspace sync. Without
gating, a Basic Feature would run GitVersion behind the user's back. It is gated on
`UsesRepositoryVersioning`.

Its known behaviour - a default-branch-tip version can mismatch a real Feature checkout - is accepted
and documented elsewhere. Gating must not silently change it.

## 8. .NET dependency behaviour

The existing dependency engine is reused behind a capability boundary, not rewritten. It covers
`.csproj` discovery, `WorkspaceProject` reconciliation, project/package classification, NuGet
`PackageReference` discovery, generated/virtual packages, dependency edges, dependency levels, unmatched
dependencies, dependency-aware update, package restore, registry synchronization, synchronized push, the
repository-grid dependency counters, the dependency graph, and the Projects/Packages pages.

Basic workspaces must not **produce** this state, rather than producing it and hiding it. In particular
Basic must not gain synthetic NuGet packages or dependency levels merely because a version file is
configured, and must not be presented as "Level 0" or "No dependencies" - levels are simply not part of
that profile.

### The capability boundary

Dependency state is gated where it is **produced**, never inside `WorkspaceProjectRepository`. The
repository stays profile-agnostic; its callers decide whether to call it.

| Producer | Gate | Where |
|---|---|---|
| Dependency-stat recompute (levels, unmatched counts) | `UsesDependencyGraph` | `WorkspaceStateRecomputeScope.RecomputeDependencyStatsAsync` |
| Project/dependency merge from a sync | `UsesDependencyGraph` | `WorkspaceGitService.PersistVersionsAsync` |
| Standalone project refresh | `DiscoversDotNetProjects` | `WorkspaceGitService.RefreshWorkspaceProjectsAsync` / `RefreshSingleRepositoryProjectsAsync`, and the Worker's `RefreshRepositoryProjectsCommand` |
| Generated packages from version files | `UsesGeneratedPackagesFromVersionFiles` | `WorkspaceFileVersionService.SyncGeneratedPackageDependenciesAsync` |
| Recompute after a file-version check | `UsesDependencyGraph` | inside `WorkspaceFileVersionService` (it cannot use the scope, which depends on it) |
| Recompute after seeding a new Feature | `UsesDependencyGraph` | `WorkspaceFeatureOperations`, through `RecomputeDependencyStatsAsync` |
| Project rows from any Worker snapshot | `DiscoversDotNetProjects` | `WorkspaceRepositoryStateWriter`, the single writer of probed state |

`WorkspaceStateRecomputeScope.RecomputeAsync` always runs the file-version check and then calls
`RecomputeDependencyStatsAsync`, which is a no-op unless the workspace uses the dependency graph. Both
entry points into the scope - the App write paths and the hook sync in `SyncCommandHandler` - go through
it, so the hook path is gated without a check of its own. Every former direct caller of the repository's
recompute (version refresh, project refresh, dependency sync, the Files page) now routes through the
scope.

Gating the producer, not the readers, is what makes a Basic workspace carry **no** dependency state
rather than a misleading one. The two readers that treat a missing `GitVersion` as an unmatched
dependency (`DependencyStats.cs`, `DependencyLines.cs`) are untouched: for a Basic workspace they are
simply never invoked, so `DependencyLevel` and `UnmatchedDeps` stay null.

### Generic file versioning versus generated packages

Version files are a generic feature and work in every profile. The file-version check, the
"out of date" flags and updating files all run for Basic. What is .NET-specific is the **interpretation**
of a configured `.csproj` version file as a synthetic NuGet package with consumer edges, and that only
happens when `UsesGeneratedPackagesFromVersionFiles` (DotNetDependency) holds.

Version-file tokens split by what they need:

| Token | Needs |
|---|---|
| `{@Repo:branch}`, `{@Repo:commit}` | nothing - always resolved from git |
| `{@Repo}` (default token, GitVersion) | `UsesRepositoryVersioning` |

When repository versioning is off, `{@Repo}` is rejected at configuration time in the Files page version
dialog with "requires repository versioning (GitVersion) to be enabled"; the page also refuses to save such a
pattern if the dialog check is bypassed. A pattern that already contains it (configured before versioning was
turned off) is skipped during checks and updates rather than reported as a GitVersion failure, and the
repository-grid "version lines" for it are not applicable.

### Worktrees

`CreateGitWorktreeRequest` derives from `WorkspaceCommandRequest` and carries the workspace's
capabilities. The command itself runs no enrichment; the capabilities warm the Worker's capability
cache so the `post-checkout` hook that `git worktree add` fires skips GitVersion and the project scan
for a Basic workspace.

The writer gate exists because not every Worker path can be told not to scan: return-to-default-branch
still asks for projects unconditionally, and the hook fallback (App unreachable, cold capability cache)
deliberately does full enrichment (section 5). The writer is the one place every probed snapshot passes
through, so a workspace that does not discover projects never persists one, whichever path produced it.

### Known gaps

- Generated packages are workspace-global, but they are synced from one context's view: a Feature's
  missing-file overlay and its resolved versions are applied workspace-wide.

## 8a. Push, update and restore strategies

Push, update and restore choose their behaviour once, at the application boundary, from the workspace's
capabilities. Nothing below that boundary asks what kind of workspace it is working on.

### Push

`WorkspacePushOperations` resolves the capabilities once per call and asks
`WorkspacePushStrategySelector` for an `IWorkspacePushStrategy`. The chosen strategy is passed down through
`WorkspacePushHandler` and `PushOrchestrator`; the orchestrator only runs it and folds failures into one
result.

| Strategy | Selected when | Plan | Push |
|---|---|---|---|
| `BasicGitPushStrategy` | not `UsesDependencyAwarePush` | every non-tag-pinned repository, no levels, no required packages | all selected repositories in parallel |
| `DotNetDependencyPushStrategy` | `UsesDependencyAwarePush` | the context's dependency graph, levels and required packages | unchanged: synchronized (registry sync, level order, package wait, restore) or parallel |

The Basic strategy reads no project or dependency rows, queries no registry, waits for no package, orders
nothing by level and restores nothing, even when the caller asks for a synchronized push. The .NET strategy
is the previous code path; it syncs registries only when `UsesNuGetPackages` and restores between levels
only when `UsesPackageRestore`, both true for that profile. The CI run watch stays inside the .NET package
wait, so a Basic push never ticks it.

### Update

`DependencyUpdateOrchestrator` checks `UsesDependencyAwareUpdate` once at the top of a run. Without it the
run is version files only: out-of-date version files across every non-tag-pinned repository are updated and
committed as one group, the committed repositories' versions are refreshed, and the run finalizes as before.
No project refresh, no dependency levels, no `.csproj` rewrite. `WorkspaceGitService.GetUpdatePlanAsync`
and `SyncDependenciesAsync` carry the same gate, so the single-repository update and any direct caller
never send `SyncRepositoryDependencies` for such a workspace. Version-file updates themselves stay
available for every profile.

### Restore

`WorkspaceGitService.RestoreDependenciesAsync`, `RestoreAllWorkspacePackagesAsync` and
`RestoreSyncedWorkspacePackagesAsync` are no-ops (they return 0) unless `UsesPackageRestore`. None of them
needs the .NET SDK for a Basic workspace.

### Worker refresh after push, undo and return-to-default

`PushRepository`, `UndoPush`, `FetchCommits`, `GetGitChangeStatus` and `ReturnToDefaultBranch` all carry
the workspace's capabilities, and each warms the Worker's capability cache. The post-operation refresh in
`PushRepositoryCommand` and `UndoPushCommand` asks the version provider built from the request's
capabilities and scans projects only when `DiscoverDotNetProjects` holds; return-to-default passes the
capabilities to the state probe. A skipped step reports itself as not probed, so the App keeps whatever it
already has. For Basic with no versioning none of these launch GitVersion.

## 9. Persistence and migration

Schema is owned by EF Core but applied through `EnsureCreated()` for new databases and guarded,
idempotent `Migrate*Async` methods for existing ones. Per `CLAUDE.md`, a schema change requires the
entity update, the `OnModelCreating` block, **and** a new migration method called from `RunAllAsync`;
new columns must be nullable or defaulted; a shipped migration method is never deleted.

```text
MigrateWorkspaceProfileColumnsAsync
  guard   pragma_table_info('Workspaces') - add each column only when absent
  backfill  UPDATE Workspaces SET Type=1, VersioningMode=1, CiProvider=1
```

The backfill runs **only on the branch that just created the columns**. An unconditional update would
stomp a user who deliberately switched a workspace to Basic, on the next startup. This is why the model
default is `Basic`/`None`/`None` while the migration default is the .NET triple: the two defaults serve
different populations and must not be conflated.

Workspace type is never inferred from whether projects currently exist. A .NET workspace may legitimately
have none right now.

## 10. UX

Simplicity is a requirement. The profile adds **three controls to the existing workspace modal and
nothing else** - no workspace-settings page, no capability matrix, no new list badges, no new UI
primitives.

```text
Workspace Name     [existing]
Workspace Path     [existing, read-only]

Workspace type
  ( ) Basic              Git repositories, branches, Features, pull requests, changes and files.
  (o) .NET Dependency    Adds .NET projects, NuGet packages, dependency levels and package-aware push.

[x] Calculate versions with GitVersion
[x] GitHub Actions
```

- The type picker is a plain `form-check` radio pair with one muted description line per option.
- The two settings are plain checkboxes. Choosing a type sets both to that type's defaults (Basic clears
  them, .NET Dependency checks both); the user may then override either. Three independent persisted
  axes, one decision to make.
- Help text is inline muted text. These modals use no tooltips or info popovers anywhere, and per
  `CLAUDE.md` short labels get `text-nowrap`.

### Changing the type later

Type changes are **blocked while the workspace has Features**, reusing the existing disabled-field
mechanism and wording ("Rename and root changes are not possible while Features exist. Remove Features
first."). GrayMoon already freezes repository membership and name/root edits under exactly this rule, so
this is consistent rather than new - and it removes most of the profile-transition lifecycle problem
instead of solving it.

Remaining transition behaviour is implemented with the modal: Basic to .NET leaves discovery to the
next sync (producers are already gated on `DiscoversDotNetProjects`); .NET to Basic deletes project
rows, dependency edges, and derived level/type/count columns on links and context states. Versioning
off leaves GitVersion and `{@Repo}` configs in place; readers already treat them as not applicable.
CI off leaves Actions rows. The Worker's in-process capability cache is not invalidated by a profile
save; the next App command for that workspace re-warms it. Git hooks in that window can still see the
previous profile.

## 10a. Repositories grid and header

There is one Repositories page. A Basic workspace uses the same page, queries and virtualization, and
just renders less of it. The page builds one `WorkspaceGridPresentation` from `WorkspaceCapabilities` when the
workspace loads. The markup reads that record and never checks the profile itself.

| Presentation flag | From | Effect when false |
|---|---|---|
| `ShowVersionColumn` | `UsesRepositoryVersioning` | Version `<th>` and `<td>` are not rendered; colspan drops to 3 |
| `ShowDependencyMetrics` | `UsesDependencyGraph` | no dependency badge, tooltip, counts or custom-dependency entry; the metrics grid lays out 4 blocks |
| `GroupByDependencyLevel` | `UsesDependencyGraph` | flat list of rows: no level headers, no "Level N" or "No dependencies" group |
| `ShowDependencyUpdateActions` | `UsesDependencyAwareUpdate` | no Update / Push Updated / Level Only / Update &amp; Push controls (absent, not disabled) |
| `ShowPackageRestore` | `UsesPackageRestore` | no Restore item in the Sync menu |
| `ShowCiLinks` | `UsesCiIntegration` | the merge dialog gets no Actions link |

- `ColumnCount` (3 plus Version) replaces the former `TableColSpan` const. Every full-width row uses it:
  level headers, error rows, spacers and placeholders.
- `ComputeSlots(index, groupByDependencyLevel)` is the pure slot layout. A flat layout ignores any
  `DependencyLevel` left over from an earlier profile.
- Header decisions are pure static methods next to `DeterminePrimaryAction`: `DetermineUpdateControl`
  returns None / Update / PushUpdated, and `DetermineShowsFileVersionUpdate` decides the file-version button.
- Version files still work in Basic (section 8), so a workspace without dependency-aware update gets a
  standalone **Update Files** button. It only appears while a version file is out of date, and it calls the same
  file-version operation the .NET Update menu's "Update Files" uses. It is never part of a dependency update.
- The level-header menu was the only way to reach bulk **Merge PRs...**. A flat grid has no level headers, so
  the header's Branch/Feature menu offers it across all repositories. Level errors also have no header to sit
  under in a flat grid, so they appear in the page error callout.
- The query is capability-aware at its boundary. `GetHeaderStateAsync(..., capabilities)` issues no
  dependency aggregate without the dependency graph: `HasUnmatchedDependencies` is false,
  `LowestLevelNeedingWork` is null, and `HasOutOfDateFiles` is computed instead. Row and index DTOs keep
  their optional dependency fields. Those fields are plain link columns (no project join), and the
  presentation ignores them. The per-row dependency tooltip loader, which does run project queries, never
  runs without `ShowDependencyMetrics`.
- `TotalFileConfigRepos` is not shown by the grid, so its `{@Repo}` counting is left as is.

## 11. Page and navigation rules

Page availability is a capability question, resolved centrally; individual pages do not invent their own
workspace-type checks, and direct navigation cannot bypass the rules.

| Page | Enabled by |
|---|---|
| Repositories | always |
| Changes | always |
| Files | always |
| Projects | `.NET Dependency` |
| Packages | `.NET Dependency` |
| Dependencies | `.NET Dependency` |
| Actions | `GitHub Actions` |

`Actions` depends on the **CI provider**, not the workspace type. GitHub source control and GitHub
Actions are separate capabilities: a workspace may use GitHub repositories and pull requests while using
another CI provider or none.

The **Files** page stays available to Basic. It is a catalog of arbitrary files in linked repositories,
not a .NET feature - its own token syntax proves it (`{@Repo:branch}` and `{@Repo:commit}` need no
`.csproj`). Only the default `{@Repo}` GitVersion token requires versioning to be enabled.

Capability badges belong in documentation and settings copy, not in the running left navigation, which
should simply contain the pages currently available.

### How it is enforced

One pure policy, `WorkspacePageAccess`, answers `IsAvailable(page, capabilities)`. The left navigation
and every gated page ask it; no page invents its own workspace-type check.

`IWorkspacePageAccessResolver` applies that policy to a persisted workspace, by workspace id only
(section 3). A Feature-context route (`?context=`) therefore gets exactly the parent Workspace's answer.
It uses `GetManyAsync` rather than `GetAsync`, so a missing workspace is an outcome
(`WorkspaceNotFound`), not an exception.

The `NavMenu` never joins a live circuit. It rebuilds the item set from persisted state on every
location change through this resolver, not through the CI provider, so the static-SSR nav pulls in no
GitHub services. A failed lookup hides the gated items rather than showing them.

Direct navigation to an unavailable page does not redirect. The page checks access before any data
query (`LoadIfAvailableAsync`) and renders one standard state (`WorkspacePageGate`): an info callout
and a "Back to Repositories" link that keeps the Feature context. The Actions page's inline
"CI is not enabled for this workspace." fallback stays as defence in depth.

## 11a. CI provider boundary

CI is reached through one small seam in `GrayMoon.App/Services/Ci/`. There is no plugin framework and
no second provider; the seam exists so CI=None does no CI work and so a later provider is one more
implementation instead of a branch at every call site.

```text
IWorkspaceCiProviderResolver        GetForWorkspaceAsync(workspaceId) | Get(WorkspaceCiProvider)
  -> IWorkspaceCiProvider           Kind, IsEnabled, GetPersistedStatusesAsync, RefreshStatusesAsync,
                                    CreatePushRunWatch(overlay)
       NoCiProvider                 None: empty, null, no-op watch - touches nothing
       GitHubActionsCiProvider      adapter over WorkspaceActionService / GitHubActionsService /
                                    GhaWorkflowLiveFeedService, behaviour unchanged
  -> IPushCiRunWatch                TickAsync(pushedRepos, links) during the synchronized-push package wait
       NoOpPushCiRunWatch           None, or no overlay to stream into
       GitHubActionsPushRunWatch    the discovery + live-feed loop formerly inline in WorkspacePushService
```

- The resolver is the **only** place that maps `WorkspaceCiProvider` to behaviour. It resolves by
  `workspaceId` only (section 3); an unknown enum value selects `NoCiProvider`, the side that does no work.
- `IsEnabled` always equals `WorkspaceCapabilities.UsesCiIntegration`. UI that only needs "is there CI"
  (navigation, page access, links) reads the capability; code that does CI work goes through the provider.
- Context follows the existing convention: a `WorkspaceFeatureContextId?` that is null for the special
  Workspace (legacy link rows) and set for a Feature (context rows only, never a fallback).
- Consumers today: the Actions page (`WorkspaceActions.Loading.cs`, `WorkspaceActions.AutoRefresh.cs`)
  for persisted reads and refresh, and `WorkspacePushService` for push-time run watching. With CI=None the
  Actions page builds no rows, so no background refresh, auto-poll or hub-driven refresh can start.

### What stays GitHub-specific behind the boundary

`GitHubActionsService`, `GhaWorkflowLiveFeedService`, `GitHubActionEntry`, `GhaLiveFeedJobsCache`, the
`WorkspaceRepositoryAction(s)` / `WorkspaceRepositoryContextAction(s)` tables and the Actions page's
rerun / run / cancel / logs actions. The page's mutations still call `GitHubActionsService` directly; they
act on rows that exist only when CI is enabled, so they are unreachable for CI=None. A second provider would
need its own page actions anyway, which is why they were not abstracted.

### Source control is not CI

`GitHubService` stays one partial class, and one `Connector` row, token and `IGitHubRateLimitTracker` still
serve both. Nothing on the repository, pull-request or connector paths consults the CI provider - including
the pull-request **check-run** summary in the merge dialog, which is a PR fact reported by GitHub regardless
of which CI produced it. Only its link to the Actions page depends on CI.

`link.Repository.Connector != null` on the Actions page is now only a repository-reachability filter; the
"is CI enabled" decision is `IWorkspaceCiProvider.IsEnabled`.

## 11b. Host readiness requirements

Host-info probing is unchanged: the Worker still reports .NET SDK, Git and GitVersion. What changed is
which missing tools count as *required*.

```text
HostPrerequisiteRequirements.For(capabilities)
  Git              always
  GitVersion       UsesGitVersion
  .NET SDK         UsesGitVersion | UsesPackageRestore | UsesDependencyAwareUpdate
HostPrerequisiteRequirements.For(all workspaces)
  union; no workspaces => Git only
```

GitVersion is invoked as `dotnet gitversion` or `dotnet-gitversion`, so a Basic workspace that uses
GitVersion also requires the .NET SDK.

`AnyMissing` still describes the probe. Attention (Host tab highlight, Home, nav dot) uses
`AnyRequiredMissing`. Missing optional tools stay visible, labelled "(optional)", and remain
installable; they never mark the host as broken. Install-Now success is judged against the tools that
install was asked to add, not against every probed tool.

If the workspace list cannot be read, every tool is treated as required so a genuine gap is never
hidden.

## 12. Feature and worktree implications

- Capabilities resolve by `WorkspaceId` only (section 3).
- Hook scripts stay context-agnostic (section 5). `WorkspaceHookContextAttributor` keeps skipping
  unmatched and ambiguous paths rather than defaulting them to the Workspace.
- `CreateGitWorktreeCommand` rewrites the main checkout's hooks **before** `git worktree add`, so the new
  worktree's `post-checkout` is attributed correctly. That ordering is preserved.
- Hook installation was gated on a version existing (`version != "-"`). A Basic+None workspace resolves
  no version, so it would silently never get hooks. The gate is removed.
- Context-scoped reads and writes keep their existing rules: Feature contexts read and write
  `WorkspaceRepositoryContextState` and never fall back to the shared link row; writes mirror onto the
  shared link only for the special Workspace.

## 13. Non-goals

- Workspace-as-Git-repository. Workspace type and repository role stay orthogonal; a future workspace
  repository must be able to participate in common Git behaviour without joining dependency discovery.
- Any CI provider other than GitHub Actions. Only the seam is established.
- Any workspace type other than Basic and .NET Dependency.
- A plugin framework, reflection-based provider discovery, or a DI graph built around individual
  capability booleans.
- Renaming the GitHub-specific subsystems. Existing GitHub-specific persistence may stay
  GitHub-specific behind the provider boundary.
- Unrelated cleanup and refactoring.

## 14. Tradeoffs and rejected alternatives

| Decision | Rejected alternative | Why |
|---|---|---|
| Worker pulls capabilities from the App API on hook paths | Bake flags into the hook script payload | Hook scripts are deliberately context-agnostic; flags go stale until every repo re-syncs; duplicates the solved token problem (section 5) |
| Three persisted enums | One `WorkspaceType` with GitVersion and CI implied | GitVersion and CI are genuinely independent concerns; a Basic workspace may want GitVersion |
| `WorkspaceCiProvider` enum | `UseGitHubActions` bool | A bool forces a schema redesign for the second provider |
| Derived capability record | Persisted capability booleans | Boolean soup; multiple sources of truth |
| Explicit not-applicable version state | Redefine `IsVersionUnresolved` | Its current meaning is pinned by existing tests and legacy readers |
| Block type change while Features exist | Full bidirectional transition lifecycle for Features | Matches the existing membership/rename freeze; removes the problem rather than solving it |
| Model default `Basic`, migration default `.NET` | Same default in both places | New databases should start clean; existing rows must preserve today's behaviour |
| Fall back to full enrichment when the App is unreachable | Fall back to Git-only | Losing a known version is worse than one wasted probe |

## 15. Future extension notes

The seams are sized for the next plausible addition, not for a general plugin system:

- **another workspace type** (Node, CMake) - add an enum value and a discovery/enrichment capability;
  the common Git snapshot is already shared.
- **another version provider** - add an `IRepositoryVersionProvider` implementation; no call site
  changes.
- **another CI provider** - add an enum value behind the CI provider boundary; page availability already
  follows the CI capability rather than the workspace type.

`WorkspaceFeature.BaseKind` / `BaseWorkspaceFeatureId` exist but are unused - a reserved "based on
another Feature" hierarchy. Profile inheritance must not be built in a way that collides with it.
