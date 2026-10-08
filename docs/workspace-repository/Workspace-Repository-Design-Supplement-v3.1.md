# Workspace as a Git Repository - Design Supplement v3.1

> **Superseded in part (2026-10-08):** the `workspaceRepository` Worker capability (`supportedFeatures`, `GetCapabilities`, `IWorkerFeatureSupportService`) and the Repositories-page compatibility banner were removed. App/Worker compatibility is now the version lock (`WorkerVersionPolicy`), and Restore validates `.graymoon.json` before creating anything; "Restored without definition" no longer exists. See `docs/feedback/graymoon-restore-workspace-and-worker-version-lock.md`.

**Status:** Binding amendments to `GrayMoon-Workspace-As-Git-Repository-Design-v3.md`  
**Why it exists:** The v3 review (`Workspace-Repository-Design-Review-2026-10-06.md`) found that v3 assumes the App resolves repository paths and writes files. In GrayMoon the Worker does both. This document records the decisions that make v3 implementable. Where v3 and this document disagree, this document wins.  
**Date:** 2026-10-06

Decisions are numbered `D1`..`D15`. The implementation plan references them by number. Do not re-open a decision inside a unit; record a concern in the plan's "Discoveries" section and continue.

---

## D1 - Worker path contract

### Problem

The Worker computes `repoPath = Path.Combine(GetWorkspacePath(workspaceRoot, workspaceName), repositoryName)` in 35 command handlers. For the Workspace-role repository the correct path is `GetWorkspacePath(workspaceRoot, workspaceName)` itself.

### Decision

1. `WorkspaceCommandRequest` (Worker base request, `src/GrayMoon.Worker/Jobs/Requests/WorkspaceCommandRequest.cs`) gains one optional field:

   ```csharp
   /// <summary>
   /// Name of the repository whose working tree is the context root itself (the Workspace-role
   /// repository). Null when the Workspace has no Workspace repository. An old App never sends
   /// this and every repository then resolves to a subfolder, which is today's behaviour.
   /// </summary>
   [JsonPropertyName("workspaceRepositoryName")]
   public string? WorkspaceRepositoryName { get; set; }
   ```

2. One Worker helper owns the rule:

   ```csharp
   namespace GrayMoon.Worker.Services;

   public static class WorkerRepositoryPaths
   {
       /// <summary>Absolute working-tree path of <paramref name="repositoryName"/> inside <paramref name="workspacePath"/>.</summary>
       public static string Resolve(string workspacePath, string repositoryName, string? workspaceRepositoryName)
           => IsWorkspaceRepository(repositoryName, workspaceRepositoryName)
               ? workspacePath
               : Path.Combine(workspacePath, repositoryName);

       public static bool IsWorkspaceRepository(string repositoryName, string? workspaceRepositoryName)
           => !string.IsNullOrWhiteSpace(workspaceRepositoryName)
              && string.Equals(repositoryName, workspaceRepositoryName, StringComparison.OrdinalIgnoreCase);
   }
   ```

3. Every `Path.Combine(workspacePath, repositoryName)` (and the `repoName` variants) in `src/GrayMoon.Worker/Commands/*.cs` is replaced by `WorkerRepositoryPaths.Resolve(workspacePath, repositoryName, request.WorkspaceRepositoryName)`. No other logic in those handlers changes.

4. On the App side, `IWorkspaceContextPathResolver.GetWorkerWorkspaceArgsAsync` returns a record instead of a pair:

   ```csharp
   public sealed record WorkerWorkspaceArgs(
       string WorkspaceRoot,
       string WorkspaceFolderName,
       string? WorkspaceRepositoryName);
   ```

   `WorkspaceRepositoryName` is the `Repository.RepositoryName` of the link with `Role == Workspace` for that context's Workspace, or null. The compiler then lists every call site that deconstructed the old tuple; each one adds `workspaceRepositoryName = args.WorkspaceRepositoryName` to the anonymous object it sends.

5. A guard test in `GrayMoon.App.Tests` scans `src/GrayMoon.App/**/*.cs`: every anonymous object literal that contains `workspaceRoot` and `repositoryName` (or `repositoryNames`) must also contain `workspaceRepositoryName`. The test fails with the file name and line so a later contributor cannot forget the field.

### Consequences

- Hooks are unaffected. The hook script posts `git rev-parse --show-toplevel`; the attributor compares with the role-aware resolver (D9).
- Worktree commands (`CreateGitWorktree`, `RemoveGitWorktree`, `ListGitWorktrees`, `InspectWorktree`) already receive absolute paths and do not change.
- `GetWorkspaceRepositoriesCommand` (folder discovery under the root) is unchanged; it lists children only.

---

## D2 - Compatibility gate

### Problem

An old Worker ignores `workspaceRepositoryName` and clones the Workspace repository into a subfolder.

### Decision

1. `GetHostInfoResponse` gains `[JsonPropertyName("supportedFeatures")] public List<string>? SupportedFeatures { get; set; }`. The Worker always returns `["workspaceRepository"]` once D1 is implemented. The constant lives in `GrayMoon.Abstractions/Worker/WorkerFeatures.cs` as `WorkerFeatures.WorkspaceRepository = "workspaceRepository"`.
2. The App caches the last `GetHostInfo` response it received (there is already a host-readiness check that calls it; the plan names the service).
3. Every entry point that creates a `Role == Workspace` link (enable, convert, restore) first checks the cached host info. If the feature is absent the operation is refused with:

   ```text
   The connected Worker does not support Workspace repositories. Update the Worker and try again.
   ```

4. Reading an existing Workspace-role link never requires the gate; only creating one does. A database that already has a Workspace repository and is then served by an old Worker is a configuration error that the App surfaces on the Repositories page as a warning banner (plan Unit E).

---

## D3 - Remote-first in v1

### Problem

`Repository.ConnectorId` and `Repository.CloneUrl` are required. There is no local connector type.

### Decision

- In v1 the Workspace repository **must be an existing repository on a GitHub connector**, imported into the `Repositories` catalog the same way every Source repository is.
- "Create a local Git repository at the Workspace root with no remote" is out of scope. Design v3 section 29.1 steps 2-3 and 6 are replaced by: "pick an existing GitHub repository as the Workspace repository".
- A Local connector type is a possible later feature. Nothing in v1 may make it harder (do not hard-code `ConnectorType.GitHub` checks in the new services; use the connector that the `Repository` row points to).

---

## D4 - `AttachWorkspaceRepository` Worker command

### Problem

Conversion targets a non-empty root. `GitService.CloneAsync` clones into a subfolder.

### Decision

One Worker command, main pool, name constant `WorkerHubMethods.AttachWorkspaceRepository = "AttachWorkspaceRepository"`.

Request (`AttachWorkspaceRepositoryRequest : WorkspaceCommandRequest`):

```text
workspaceName       string   folder name under workspaceRoot
cloneUrl            string   remote URL
bearerToken         string?  connector token (runtime only, never persisted)
```

Algorithm, each step reported on `CommandOutput`:

```text
1. path = GetWorkspacePath(workspaceRoot, workspaceName); create the directory if missing.
2. If path\.git exists (file or directory):
     - read origin URL; if it is not URL-equivalent to cloneUrl -> fail "Root already has a different Git repository".
     - otherwise success (idempotent re-attach).
3. If path is empty:
     - git clone <cloneUrl> .   (with the same -c http.extraHeader token mechanism as CloneAsync)
     - add safe.directory, install hooks (same as SyncRepository), success.
4. Else (non-empty root without .git):
     - git init
     - git remote add origin <cloneUrl>
     - git fetch origin --tags
     - default = remote HEAD branch (git symbolic-ref refs/remotes/origin/HEAD, or ls-remote --symref)
     - if default exists:
         git checkout -b <default> --track origin/<default>
         (git refuses when a tracked file would overwrite an existing untracked file; return that
          message verbatim as the error; leave .git in place so the user can resolve and retry)
       else (empty remote):
         git symbolic-ref HEAD refs/heads/main   (unborn branch, nothing committed)
     - add safe.directory, install hooks, success.
```

Response: `{ success, errorMessage, branch, isUnborn }`.

The command never writes `.graymoon.json` or `.gitignore` and never commits. The App follows up with D5 writes, which then appear in Git Changes (v3 section 26.1).

---

## D5 - `WriteRepositoryFile` Worker command

### Problem

The App cannot write files.

### Decision

One Worker command, main pool, `WorkerHubMethods.WriteRepositoryFile = "WriteRepositoryFile"`.

Request (`WriteRepositoryFileRequest : WorkspaceCommandRequest`):

```text
workspaceName             string
repositoryName            string
workspaceRepositoryName   string?   (base field, D1)
filePath                  string    repository-relative, validated by GitRepositoryPathValidator
content                   string    full file content, UTF-8
onlyIfChanged             bool      default true: compare bytes first, skip the write when equal
```

Response: `{ success, errorMessage, written }` where `written` is false when the content was already identical.

Rules:

- UTF-8 without BOM. Line endings are whatever `content` contains; the App's manifest and `.gitignore` serializers emit `\n` with a trailing newline (D15), because the file must be byte-identical on every machine and git normalizes on checkout where configured.
- Write to a temp file in the same directory and `File.Move(overwrite: true)` so a crash never leaves a half-written manifest.
- Reject absolute paths, `..`, and any path under `.git`.
- No git operation of any kind.

Reading uses the existing `GetFileContents` command, which already resolves through the base request (D1 makes it root-aware).

---

## D6 - The Workspace-root repository never discovers projects (Worker rule)

### Decision

In the Worker, wherever a handler consults `request.EffectiveCapabilities.ShouldDiscoverProjects`, it uses instead:

```csharp
var discoverProjects = request.EffectiveCapabilities.ShouldDiscoverProjects
    && !WorkerRepositoryPaths.IsWorkspaceRepository(repositoryName, request.WorkspaceRepositoryName);
```

Affected handlers: `SyncRepositoryCommand`, `RefreshRepositoryProjectsCommand`, `CommitSyncRepositoryCommand`, the four hook sync commands via `RepositoryStateProbe` (the probe receives the flag; the plan names the exact parameter). The response for the Workspace repository therefore has `Projects == null` and `ProjectsProbed == false`, which the App already treats as "nobody looked".

Version calculation (GitVersion) stays governed by the Workspace profile only; the Workspace repository is versioned when `VersioningMode == GitVersion`, as v3 section 8.2 says.

`WorkspaceFileSearchService` (Worker) and `SearchFilesCommand` exclude immediate child directories that contain Git metadata when searching the Workspace-root repository, using the same `HasGitMetadata` test as `GetWorkspaceRepositoriesCommand`.

App side, v3 section 25.1 stands: the grid hides level / dependencies / projects for a `Role == Workspace` row and never places it in a dependency level group.

---

## D7 - Watcher excludes nested repositories physically

### Decision

`GitRepositoryWatcher` ignores any working-tree event whose path lies under an immediate child directory `<repoPath>\<child>` for which `<repoPath>\<child>\.git` exists (file or directory). The set of such children is computed when the watcher starts and refreshed when a `Created`/`Deleted`/`Renamed` event targets an immediate child directory. This applies to every watched repository, not only the root; a Source repository with a submodule benefits equally.

Rationale recorded against v3 section 34.2: the Worker has no membership model and a nested `.git` is a physical boundary that git itself honours. Sending the Source list from the App remains an option later; it is not needed for correctness.

`GitRepositoryWatcherManager.Acquire(string repoPath)` is unchanged.

---

## D8 - Manifest drift detection

### Decision

1. Add nullable column `Workspaces.ManifestDriftDetectedAt` (`DateTime?`). It is reconciliation state, not Git state, so v3 section 7.4 is not violated.
2. After any of these complete for the **special Workspace context** and the Workspace has a `Role == Workspace` link: full Sync, single-repository sync of the Workspace repository, Checkout Branch / Tag on it, hook sync attributed to it - the App calls `IWorkspaceManifestService.DetectDriftAsync(workspaceId)`, which:
   - reads `.graymoon.json` through `GetFileContents` (D1 makes it root-aware);
   - parses it (D15); an unparsable file counts as drift with the parse error as the reason;
   - builds the canonical manifest from the database;
   - compares the two canonical models (not bytes);
   - sets or clears `ManifestDriftDetectedAt`.
3. The Repositories page shows a non-blocking banner when the value is set: "Workspace definition on disk differs from this Workspace. Review." The Review action in v1 shows the diff (added/removed repositories, changed profile, connector differences) and offers two buttons: "Write Workspace definition to disk" (D5 write of the canonical manifest) and "Keep file, dismiss". Applying the file's content to the database (adding/removing repositories) is **not** v1; v3 sections 26.3 and 42 remain MVP-scoped.
4. Feature contexts never run drift detection. The service refuses a Feature context id.

---

## D9 - Role-aware path resolver

### Decision

`WorkspaceContextPathResolver.GetRepositoryPathAsync`:

```text
special context, Role == Workspace  -> GetContextRootAsync(contextId)
special context, Role == Source     -> contextRoot / RepositoryName          (unchanged)
Feature context, Role == Workspace  -> persisted WorktreePath if set, else GetContextRootAsync(contextId)
Feature context, Role == Source     -> persisted WorktreePath if set, else contextRoot / RepositoryName   (unchanged)
```

`WorkspaceFeatureOperations` persists `WorktreePath = featureRootPath` for the Workspace-role row, so the Feature branch above normally takes the persisted path.

No other code may compute a repository path from a name. The hook attributor already goes through the resolver.

---

## D10 - Two-phase Feature create, repair and remove

### Decision

Create (replaces v3 section 20 wording with the exact code shape):

```text
rows = pendingRows
root = rows.SingleOrDefault(r => link(r).Role == Workspace)
if root != null:
    create root worktree (one CreateGitWorktree call, worktreePath = featureRootPath)
    if it fails: mark root NeedsRepair, mark Feature NeedsRepair, return (do not touch Source rows; they stay Pending)
sources = rows where Role == Source
existing bounded fan-out over sources (unchanged)
```

Repair Retry: the same split; a Pending root row is always retried before any Pending Source row.

Remove:

```text
sources first: existing bounded fan-out
then root: one RemoveGitWorktree call with featureRootPath = null and featureStorageRoot = null
           (git worktree remove deletes the directory; the Worker residue cleanup must not run)
```

If any Source removal failed, the root is not removed and the Feature stays NeedsRepair, because `git worktree remove` on a root that still contains a Source worktree would either fail or require `--force`.

`WorkspaceFeatureRepository.WorktreePath` for the root row is `featureRootPath` exactly (no trailing separator).

---

## D11 - Connector and repository identity

### Decision

Manifest connector:

```json
{ "type": "github", "url": "https://github.com" }
```

- `url` = `RepositoryUrlHelper.GetWebRootFromConnectorApiBase(connector.ApiBaseUrl)` normalized by `RepositoryUrlIdentity.NormalizeConnectorUrl`.
- `type` = `connector.ConnectorType` lower-camel (`github`). NuGet connectors are never written to the manifest.
- Matching on restore: `NormalizeConnectorUrl(manifest.url) == NormalizeConnectorUrl(GetWebRootFromConnectorApiBase(local.ApiBaseUrl))` for connectors with `ConnectorType == GitHub`. If more than one local connector matches, prefer `IsHealthy`, then lowest `ConnectorId`.

Manifest repository:

```json
{ "name": "Avr.Api", "repositoryUrl": "https://github.com/example/Avr.Api.git", "connectorUrl": "https://github.com" }
```

- `name` = `Repository.RepositoryName` (this is also the folder name under the root; v3 section 14).
- `repositoryUrl` = `Repository.CloneUrl` as stored.
- Matching on restore: `NormalizeRepositoryUrl(manifest.repositoryUrl) == NormalizeRepositoryUrl(local.CloneUrl)` over the whole `Repositories` table (indexed by `CloneUrl`; the normalized compare is done in memory on the candidate set returned by host + path filter, or simply over all rows - the table is small).

`RepositoryUrlIdentity` (static, `GrayMoon.Common/Git/RepositoryUrlIdentity.cs`):

```text
NormalizeConnectorUrl(string)  : lower-case scheme and host, drop default port, drop trailing slash, drop path "/api/v3"
NormalizeRepositoryUrl(string) : accept https://host/owner/repo(.git)(/), http://..., ssh://git@host/owner/repo(.git), git@host:owner/repo(.git)
                                 -> "https://host/owner/repo" with lower-case host, original-case path, no .git, no trailing slash
RepositoryUrlsEqual(a, b)      : NormalizeRepositoryUrl(a) == NormalizeRepositoryUrl(b) ordinal-ignore-case
```

Unparsable input returns the trimmed input unchanged so equality still works for identical strings.

---

## D12 - Managed `.gitignore` section

Exact format, `\n` line endings, one entry per Source repository, sorted ordinal-ignore-case:

```gitignore
# <graymoon-repositories>
/Avr.Api/
/Avr.Shared/
/Avr.Web/
# </graymoon-repositories>
```

Rules:

- If the file has no section, append a blank line (when the file is non-empty and does not already end with one) and the section.
- If the file has a section, replace everything between and including the markers.
- Lines outside the section are preserved byte for byte.
- Only `Role == Source` repositories are listed.
- Written through D5 with `onlyIfChanged = true`.
- Regenerated whenever Source membership of the special Workspace context changes, and on enable/convert/restore.

---

## D13 - UI placement

- **Enable / change:** `WorkspaceModal.razor` (create and edit) gains an optional "Workspace repository" select listing imported GitHub repositories not already linked to this Workspace as Source. Blank means none. Changing it while Features exist is refused with the same wording as rename.
- **Grid:** the `Role == Workspace` row renders first, in its own group titled "Workspace repository", with a `Workspace` badge, and without level / dependencies / projects cells. All Git actions remain available. `WorkspaceRepositoryLinkListItemDto` gains `Role`.
- **Membership modal:** `WorkspaceRepositoriesModal` lists Source repositories only; the Workspace repository is neither selectable nor removable there.
- **Git Changes page:** no change; the Workspace repository is one more repository node named by `RepositoryName`.
- **Compat warning:** when a Workspace-role link exists and the cached host info lacks `workspaceRepository`, the Repositories page shows a warning banner (D2).

---

## D14 - Restore from an existing Workspace repository (v1 scope)

Entry point: `Workspaces.razor`, action "Restore from Workspace repository".

```text
1. User picks a GitHub repository from the imported catalog and a Workspace name (default: repository name).
   The name must not collide with an existing Workspace and the folder <root>\<name> must not exist or must be empty.
2. App creates the Workspace row with profile Basic/None/None and the Role == Workspace link, then
   calls AttachWorkspaceRepository (D4), which clones into the root.
3. App reads .graymoon.json (GetFileContents). Missing or unparsable -> Workspace stays as created,
   result "Restored without definition: <reason>". Done.
4. App applies the manifest profile to the Workspace row.
5. For each manifest connector: resolve (D11). Unresolved connectors are listed in the result; nothing is fabricated.
6. For each manifest repository: resolve by URL (D11) among imported Repositories. Resolved -> add Source link.
   Unresolved -> listed in the result with its connectorUrl so the user knows which connector to import from.
7. Regenerate .gitignore (D12) and write the manifest (D5) so disk and database agree.
8. Run the normal Sync; SyncRepository clones each missing Source repository (existing behaviour).
```

Importing a repository that is not yet in the catalog is done by the user on the Connectors / Repositories pages, then "Reconcile" (D8 Review) adds it. Automatic per-URL import from GitHub is a later refinement.

---

## D15 - Manifest schema v1 and serialization

Exact shape, property order fixed, arrays sorted by `name` / `url` ordinal-ignore-case:

```json
{
  "schemaVersion": 1,
  "workspace": {
    "name": "AVR",
    "profile": {
      "type": "basic",
      "versioning": "none",
      "ci": "none"
    }
  },
  "connectors": [
    { "type": "github", "url": "https://github.com" }
  ],
  "repositories": [
    { "name": "Avr.Api", "repositoryUrl": "https://github.com/example/Avr.Api.git", "connectorUrl": "https://github.com" }
  ]
}
```

Enum string values: `type` in `basic | dotNetDependency`; `versioning` in `none | gitVersion`; `ci` in `none | githubActions`.

Serializer: `System.Text.Json`, `WriteIndented = true`, camelCase, `\n` line endings (post-process `\r\n` to `\n`), trailing `\n`, UTF-8 no BOM. Parser: unknown properties are ignored; `schemaVersion` greater than 1 is rejected with "Workspace definition schema 2 is newer than this GrayMoon"; missing `schemaVersion` is treated as 1.

Model types live in `GrayMoon.Application/WorkspaceManifest/` as sealed records so pages and the service share them. Serializer and parser live in `GrayMoon.App/Services/WorkspaceManifest/WorkspaceManifestSerializer.cs`.

---

## Changes to v3 section 43 (phases)

Replace the phase list with the plan's unit map. In particular Phase 0 is the Worker path contract (D1, D2, D6), which v3 did not have.

## Changes to v3 section 45 (non-goals)

Add:

- local-only Workspace repository without a remote (D3);
- automatic import of unknown repositories from GitHub during restore (D14);
- applying a drifted manifest to the database (D8).
