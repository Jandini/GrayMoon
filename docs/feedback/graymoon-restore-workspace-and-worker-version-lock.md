# GrayMoon - Restore Workspace UX and Worker Version Lock

Date: 2026-10-08  
Target branch for implementation/review: `feedback`  
Primary repository: `Jandini/GrayMoon`  
Status: **Units A-E implemented, uncommitted; Unit F (manual end-to-end) pending.** See section 0 for the review against the code and section 20 for the implementation record.

## Purpose

This document captures the next feedback block after the first three feedback items were implemented.

It deliberately combines two related areas:

1. **Make Restore Workspace from repository feel safe, clear and polished.**
2. **Remove the Workspace-repository feature-capability compatibility mechanism and use GrayMoon's existing App/Worker version lock instead.**

These should be designed together because Restore Workspace currently depends on the `workspaceRepository` Worker capability check, and the Repositories page displays a compatibility banner driven by the same mechanism.

The desired product rule is simpler:

> The GrayMoon App and Worker are a matched pair. A Worker with a different GrayMoon version is not an alternate capability set. It is the wrong Worker version and should be updated.

Do not build an expanding per-feature compatibility matrix between App and Worker.

---

# 0. Review against the code (2026-10-08)

Sections 1-19 below are the original feedback. Reading the `feedback` branch confirmed them, with these corrections and additional findings.

## 0.1 Confirmed

- `RestoreCoreAsync` returned `Success = true` with "Restored without definition: ..." for a missing, unreadable or unparsable `.graymoon.json`, after the Workspace row, the Workspace-role link and the clone already existed.
- `WorkerConnectionTracker` already had `VersionMismatch`, and the self-update flow already waited for a Worker reporting the App's version.
- `GetCapabilities` had no consumer other than `WorkerFeatureSupportService` (searched the App, the Worker, the tests and `GrayMoon.Desktop`).

## 0.2 Corrections to section 1

- The capability did not come from `GetHostInfo` any more: it had moved to a dedicated `GetCapabilities` command on the Worker read lane, cached by the App for 60 seconds. The banner was shown only on Workspaces that have a Workspace-role link, and both Enable and Restore were gated.
- The version comparison was an exact string compare of `AssemblyInformationalVersion`. GitVersion is configured with `assembly-informational-format: '{SemVer}'` and both projects set `IncludeSourceRevisionInInformationalVersion=false`, so no build metadata is emitted today; the risk in section 14 was latent, not live.
- The dialog title was "Restore Workspace from repository".
- The dialog's folder check only counted Git repositories in the folder. A folder with ordinary files passed the dialog and was refused only later by the Worker (`requireEmptyRoot`), after the Workspace row had been created.
- There is no "Review Workspace" action in the UI. Repositories are added to a Workspace from its repository list (the repository count link on the Repositories page), so the post-restore guidance points there.

## 0.3 Additional problems found

- **Restore with missing repositories damaged the definition.** After linking only the locally resolvable repositories, restore always called `WriteAuthoritativeManifestAsync`, which rebuilds `.graymoon.json` from the database. Every repository that was not imported on this computer was silently removed from the file, leaving a dirty working tree that would lose them on the next commit. Fixed by section 7's "write only if required" (see 20.2).
- **Unknown profile values were silently ignored.** An unknown `type`, `versioning` or `ci` kept the Basic / None / None default for that axis. Restore now refuses such a definition (section 3.3 example).
- **Cancellation after the Workspace was created left it behind.** `LinkAndAttachAsync` removed the link on cancellation, but the Workspace row stayed. Restore now rolls the whole Workspace back on cancellation too.

---

# 1. Current state found in code

## 1.1 Restore Workspace modal

Current UI:

`src/GrayMoon.App/Components/Modals/RestoreWorkspaceModal.razor`

The dialog currently contains:

- repository search/filter
- repository selector
- Workspace name
- folder-existence check
- Restore / Cancel
- post-restore warning panel
- unresolved connector list
- unresolved repository list

The restore itself is started as a page background job and then runs a full Sync.

This foundation is sound, but the important validation happens **too late**.

## 1.2 Current restore operation

Current service:

`src/GrayMoon.App/Services/Application/WorkspaceRepositoryOperations.cs`

`RestoreCoreAsync` currently does approximately:

```text
validate name
    ↓
check Worker feature capability
    ↓
create Workspace database row
    ↓
link selected repository as Workspace repository
    ↓
clone/attach repository into Workspace root
    ↓
read .graymoon.json
    ↓
if missing/invalid:
    return SUCCESS as "Restored without definition"
    ↓
otherwise apply profile
    ↓
resolve connectors and repositories
    ↓
link resolved repositories
    ↓
rewrite .gitignore / manifest
    ↓
Sync
```

The most important problem is this branch:

```csharp
if (read.Error is not null)
    return RestoredWithoutDefinition(workspaceId, read.Error);

if (!read.Found)
    return RestoredWithoutDefinition(
        workspaceId,
        $"{WorkspaceRepositoryFileAccess.ManifestFilePath} was not found in the Workspace repository");

if (!manifestService.TryParse(...))
    return RestoredWithoutDefinition(...);
```

A command called **Restore Workspace from repository** should not report success after discovering that the repository cannot actually define a Workspace.

That leaves the user with a partially restored Workspace and makes an input-validation problem look like a successful restore with a warning.

## 1.3 Current Worker capability mechanism

Current compatibility path includes:

- `WorkerFeatures.WorkspaceRepository`
- `GetCapabilities`
- `IWorkerFeatureSupportService`
- `SupportsAsync(WorkerFeatures.WorkspaceRepository)`
- Workspace repository operation gates
- `ShowWorkerCompatibilityBanner`
- banner text: `The connected Worker does not support Workspace repositories. Update the Worker.`

This was introduced to protect a newer App from an older Worker.

However, GrayMoon already has a stronger concept:

`WorkerConnectionTracker` tracks the actual Worker semantic version and already exposes:

```csharp
WorkerConnectionState.VersionMismatch
```

It compares the Worker's reported `AssemblyInformationalVersion` with the App's own informational version.

The self-update flow already considers the update finished only after a Worker reconnects with the **matching version**.

Therefore GrayMoon already knows whether the Worker is the correct build.

The feature-capability layer duplicates a problem that the version layer already solves.

---

# 2. Product decisions

These are binding decisions for this feedback unit.

## Decision A - App and Worker versions are locked

For normal GrayMoon operation:

```text
App version == Worker version
```

is required.

A connected Worker with a different GrayMoon version is a **version mismatch**, not a partially compatible Worker.

GrayMoon should direct the user to update the Worker.

Do not ask individual product features whether the Worker supports them.

## Decision B - remove Workspace-repository capability gating

The following concept should be removed once the version gate is authoritative:

```text
workspaceRepository capability
```

Restore Workspace, Enable Workspace Repository, Feature operations, and future functionality should not grow individual Worker capability flags merely because they add Worker commands.

The Worker protocol remains backward-safe at serialization boundaries where reasonable, but product UX should not pretend mismatched App/Worker builds are a supported normal operating state.

## Decision C - no special Repositories-page compatibility banner

Do not display:

> The connected Worker does not support Workspace repositories.

The problem is not specific to a Workspace.

If the Worker version is wrong, the **global Worker status** should say so and direct the user to update it.

This avoids feature-specific warning banners appearing on random product pages.

## Decision D - Restore must preflight before mutating

Before GrayMoon creates a Workspace row or clones into the final Workspace folder, it must know that the selected repository is a valid GrayMoon Workspace repository.

At minimum:

- repository is accessible
- `.graymoon.json` exists
- manifest parses
- manifest schema/version is supported
- Workspace profile values are understood or explicitly handled
- the Workspace folder can be created safely

Restore must not create a "restored without definition" Workspace.

---

# 3. Restore Workspace - desired user experience

## 3.1 Entry state

Title:

```text
Restore Workspace
```

Help text:

```text
Restore a GrayMoon Workspace from a repository containing .graymoon.json.
```

Avoid making the title unnecessarily technical.

## 3.2 Repository selector

The current searchable repository selector is useful and should remain.

Display choices clearly as:

```text
org/repository
```

rather than relying on repository name alone.

If practical, display connector/account context as secondary text when names collide.

### Empty state

Current behavior says no imported GitHub repositories are available. Make this more actionable:

```text
No GitHub repositories are available.

Import the Workspace repository through Connectors first, then return here.
```

Restore stays disabled.

## 3.3 Preflight after repository selection

Selecting a repository should start a lightweight preflight.

The dialog should show:

```text
Checking Workspace definition...
```

Then either a valid preview or a clear blocking error.

### Valid repository preview

Example:

```text
GrayMoon Workspace

Profile
.NET Dependency
GitVersion
GitHub Actions

Repositories
24

Connectors
1 configured locally
0 missing
```

The user should understand what is about to be restored **before cloning**.

### Invalid repository

Examples:

```text
This repository does not contain .graymoon.json.
```

```text
The Workspace definition is invalid:
Unknown workspace profile value "..."
```

```text
This Workspace definition was created by a newer GrayMoon version.
Update GrayMoon before restoring it.
```

Restore stays disabled.

No Workspace row or final Workspace directory is created.

---

# 4. How preflight should work

## 4.1 Preferred source

The selected repository already exists in GrayMoon's imported GitHub repository catalog.

Prefer reading `.graymoon.json` through the existing GitHub connector/API path when possible.

That avoids:

- creating a Workspace
- cloning a repository
- starting Git
- deleting a failed partial restore

The preflight should be cheap and read-only.

## 4.2 Fallback

If the connector abstraction cannot currently fetch a repository file cleanly, introduce a focused read-only operation rather than abusing the full restore path.

Do not create the final Workspace as a preflight mechanism.

A temporary clone may be used only if there is no reasonable connector/API read path, but it should be a last resort and cleaned deterministically.

## 4.3 Preflight result model

Introduce a model along the lines of:

```csharp
public sealed record RestoreWorkspacePreflight
{
    public bool Success { get; init; }
    public string? Error { get; init; }

    public WorkspaceManifest? Manifest { get; init; }

    public WorkspaceType? WorkspaceType { get; init; }
    public WorkspaceVersioningMode? VersioningMode { get; init; }
    public WorkspaceCiProvider? CiProvider { get; init; }

    public int RepositoryCount { get; init; }
    public int ConnectorCount { get; init; }

    public IReadOnlyList<string> MissingConnectors { get; init; } = [];
    public IReadOnlyList<string> MissingRepositories { get; init; } = [];
}
```

The exact type can be adjusted to existing boundaries.

Do not leak EF entities into the modal.

---

# 5. Missing connectors and repositories

The current restore process discovers missing connectors/repositories only after cloning and creating the Workspace.

That information is valuable **before Restore**.

## 5.1 Show the situation before the user commits

Example:

```text
2 repositories are not currently imported into GrayMoon.
1 connector is not configured on this computer.

The Workspace can be restored now, but those repositories will be missing.
```

List them in a compact scrollable section.

## 5.2 Restore policy

A missing Source repository should not invalidate the Workspace repository itself.

The user may intentionally be restoring on another computer before all connectors are configured.

Therefore:

- valid `.graymoon.json` + missing Source repositories = Restore allowed
- valid `.graymoon.json` + missing connector(s) = Restore allowed if the Workspace repository itself is already accessible
- missing/invalid `.graymoon.json` = Restore blocked

This preserves portability.

## 5.3 Post-restore guidance

If items remain unresolved, the success panel should be concise and actionable:

```text
Workspace restored with 2 repositories not yet available.

Import them through Connectors, then use Review Workspace to add them.
```

Use a button/link to the relevant GrayMoon page if there is an established navigation path.

Do not dump raw URLs without context when a friendlier repository name can be shown.

---

# 6. Workspace name and folder behavior

The current automatic naming behavior is useful:

- selecting a repository supplies a default Workspace name
- user edits are preserved
- folder existence is checked asynchronously

Keep this behavior.

## 6.1 Make folder state explicit

Display the resolved destination path in muted text:

```text
Location
C:\Users\...\Workspaces\MyWorkspace
```

This makes the restore operation predictable.

## 6.2 Existing empty folder

If the destination exists and is empty:

```text
The folder already exists and is empty. GrayMoon will use it.
```

This is informational, not alarming.

## 6.3 Existing non-empty folder

Block Restore clearly:

```text
This folder already contains files.
Choose another Workspace name or move the existing files.
```

Do not offer force behavior here.

A restore operation should not merge an arbitrary existing directory with a Workspace repository.

---

# 7. Restore execution

After successful preflight, execution becomes straightforward:

```text
Preflight already valid
    ↓
create Workspace
    ↓
attach/clone Workspace repository into root
    ↓
read manifest from local clone and verify it again
    ↓
apply profile
    ↓
link all locally resolvable Source repositories
    ↓
write managed .gitignore
    ↓
write authoritative .graymoon.json only if required
    ↓
full Sync
    ↓
success panel / open Workspace
```

## 7.1 Revalidate after clone

The local `.graymoon.json` should still be parsed after clone.

This protects against the remote branch changing between preflight and clone.

If it has become invalid:

- fail restore
- rollback the new Workspace database state
- clean up the newly created root when it is safe to do so
- explain the failure

Do not convert this into `RestoredWithoutDefinition`.

## 7.2 Remove `RestoredWithoutDefinition`

The concept should not exist for this workflow.

A Workspace restored from a repository **requires a definition**.

If future GrayMoon supports "clone this folder and create a new Workspace around it", that is a separate feature with a different name.

---

# 8. Progress UX

The page-level background overlay is appropriate.

Progress text should use user-facing phases rather than internal command names.

Suggested sequence:

```text
Preparing Workspace...
Cloning Workspace repository...
Applying Workspace profile...
Linking repositories...
Preparing Workspace files...
Syncing repositories...
Finishing...
```

If repository linking has meaningful volume:

```text
Linking repositories 8 of 24...
```

is useful.

---

# 9. Success UX

## Clean success

If everything resolves and Sync succeeds:

- close the dialog
- open the restored Workspace automatically

No extra success confirmation is necessary.

## Success with missing local configuration

Keep the result panel open only when the user needs to act.

Example:

```text
Workspace restored

2 repositories are not available on this computer.
1 connector must be configured.
```

Actions:

- `Open Workspace` - primary
- optional `Open Connectors` - secondary, if navigation support is clean

## Sync warning

A successful structural restore followed by a failed Sync must be communicated differently from a failed restore.

Example:

```text
Workspace restored, but the initial Sync did not complete.

You can open the Workspace and retry Sync.
```

Do not label the entire restore as failed if the Workspace and manifest were restored correctly.

---

# 10. Failure and rollback guarantees

Restore should have explicit states.

## Failure before mutation

Examples:

- invalid manifest
- unsupported manifest version
- invalid Workspace name
- Worker unavailable
- Worker version mismatch
- destination non-empty

Result:

```text
No Workspace created.
No filesystem changes.
```

## Failure after Workspace creation but before usable restore

Examples:

- clone failure
- manifest changed and became invalid
- profile application fails
- critical write failure

Expected behavior:

- remove newly created Workspace links/state
- delete the newly created Workspace row
- clean the newly created root when GrayMoon can prove it owns it and cleanup is safe
- preserve the original error
- report residue separately if cleanup could not complete

Do not leave half-restored Workspaces in the normal Workspace list without an explicit recovery state.

---

# 11. Worker version lock - replace capability probing

## 11.1 Existing mechanism to reuse

`WorkerConnectionTracker` already knows:

```csharp
WorkerConnectionState.Offline
WorkerConnectionState.Connecting
WorkerConnectionState.Online
WorkerConnectionState.VersionMismatch
```

It already receives the Worker's informational semantic version through the SignalR connection.

It already compares:

```text
Worker SemVer vs App SemVer
```

It already requires matching versions to finish a Worker self-update.

This should become the authoritative compatibility mechanism.

## 11.2 Required operating rule

Only:

```csharp
WorkerConnectionState.Online
```

means the Worker may execute normal GrayMoon commands.

`VersionMismatch` means:

```text
Worker connected, but unusable until updated.
```

It must not be treated as "connected enough" for product operations.

## 11.3 Centralize the gate

Do not make every feature manually inspect the state.

The cleanest design is to make the Worker command boundary enforce the rule for normal commands.

Conceptually:

```csharp
if (WorkerConnectionTracker.State == WorkerConnectionState.VersionMismatch)
    return WorkerCommandResponse.Fail(
        "The GrayMoon Worker version does not match this GrayMoon version. Update the Worker.");
```

There may need to be a small allow-list for commands required to:

- identify the Worker
- perform/update/reconnect the Worker
- show diagnostics

Do not create a feature-by-feature allow-list.

## 11.4 Restore Workspace behavior

The Restore dialog should simply see:

```text
Worker update required
```

before any Worker-dependent execution.

Suggested callout:

```text
Update the GrayMoon Worker before restoring this Workspace.

App:    0.x.y
Worker: 0.x.z
```

Primary action, where supported:

```text
Update Worker
```

No mention of Workspace repository support/capabilities.

---

# 12. Remove capability-specific code

After the version lock is authoritative, remove the now-redundant Workspace-repository compatibility system.

Review and remove/update:

```text
WorkerFeatures.WorkspaceRepository
GetCapabilitiesCommand
GetCapabilitiesRequest / Response
IWorkerFeatureSupportService
WorkerFeatureSupportService
SupportsAsync(...)
ShowWorkerCompatibilityBanner
_workerSupportsWorkspaceRepository
RefreshWorkerSupportInBackgroundAsync
UnsupportedWorkerMessage referring to Workspace repository support
```

Also update:

- DI registrations
- Worker dispatcher
- job factory/read-lane registrations
- tests
- architecture documentation
- Workspace repository design documents that describe `supportedFeatures`

Before deleting `GetCapabilities`, search the whole solution and confirm it has no other meaningful consumer.

If future code has started using it for a separate concern, migrate that concern deliberately rather than deleting blindly.

---

# 13. Repositories-page banner cleanup

Remove this banner:

```text
The connected Worker does not support Workspace repositories. Update the Worker.
```

A Worker version mismatch is global and should be represented by:

- global Worker badge/status
- Worker page
- command gating
- optional global-level notification

It should not be a Workspace-specific red banner.

This also prevents the Repositories page from doing a background capability round trip merely to decide whether to show a warning.

---

# 14. Version comparison details

GrayMoon already compares informational versions.

Before changing behavior, normalize the version comparison so build metadata does not accidentally create false mismatches if the build pipeline can emit it.

Define one explicit comparison contract.

Recommended:

```text
Compare normalized GrayMoon product SemVer.
```

For example, if builds report:

```text
0.2.0+abcdef
0.2.0+ghijkl
```

decide whether those are the same product version.

For release builds they normally should be treated as the same `0.2.0` **only if** App and Worker are guaranteed protocol-identical for that product version.

If CI/dev builds intentionally need exact commit matching, make that policy explicit.

Do not leave comparison semantics accidental.

---

# 15. Tests

## 15.1 Restore preflight

Add tests for:

- valid `.graymoon.json`
- missing `.graymoon.json`
- invalid JSON
- supported/unsupported manifest schema version if versioned
- unknown profile values
- missing local connector
- missing local repository
- repository URL normalization
- multiple repositories resolving correctly
- selected Workspace repository not duplicated as a Source repository

## 15.2 No mutation on invalid input

Prove:

- invalid manifest creates no Workspace row
- invalid manifest creates no WorkspaceRepository link
- invalid manifest does not call attach/clone
- non-empty destination does not mutate DB/disk
- Worker version mismatch does not start restore

## 15.3 Restore rollback

Inject failures at:

- attach/clone
- local manifest reread
- profile application
- managed `.gitignore` write
- manifest write

Assert the defined rollback behavior.

## 15.4 Dialog behavior

Tests for:

- repository selection triggers preflight
- stale preflight result cannot overwrite a newer repository selection
- Restore disabled while preflight is pending
- Restore disabled on invalid manifest
- Restore allowed with unresolved Source repositories
- Workspace name manual edit is not overwritten by later selection refresh
- existing non-empty folder blocks
- existing empty folder permits
- clean success navigates directly
- warning success stays in result panel

## 15.5 Worker version lock

Tests for:

- matching normalized version => Online
- mismatching version => VersionMismatch
- normal Worker command blocked on VersionMismatch
- Worker update/diagnostic path remains usable
- matching reconnect returns to Online
- no Workspace-repository capability check is performed
- Repositories page has no compatibility banner

---

# 16. Implementation units

Keep the work split into testable units.

## Unit A - Restore preflight model/service

Owner: App/service layer

Deliver:

- preflight contract
- manifest fetch/read
- parse/validation
- local connector/repository resolution preview
- tests

No UI mutation yet.

## Unit B - Restore dialog UX

Owner: App UI

Deliver:

- selection + preflight state
- preview
- missing-items summary
- destination path/folder state
- polished validation
- tests

No version-capability cleanup yet.

## Unit C - Restore transactional behavior

Owner: application service

Deliver:

- remove `RestoredWithoutDefinition`
- revalidate manifest after clone
- defined rollback behavior
- separate restore-success / sync-warning result
- tests

## Unit D - Worker version lock

Owner: Worker/App connection boundary

Deliver:

- authoritative use of `WorkerConnectionTracker.VersionMismatch`
- centralized normal-command gate
- normalized version comparison
- update/diagnostic allow-path
- tests

## Unit E - remove capability system and banner

Owner: App + Worker cleanup

Deliver:

- remove `workspaceRepository` feature capability
- remove feature-support service if unused
- remove `GetCapabilities` if unused
- remove Workspace-specific compatibility banner
- update operation gates
- update docs/tests

## Unit F - end-to-end regression

Test:

1. matching App/Worker
2. mismatched Worker
3. missing manifest
4. malformed manifest
5. 24-repository real Workspace manifest
6. missing connector(s)
7. missing repository(s)
8. existing empty folder
9. existing non-empty folder
10. full restore + initial Sync
11. reopen restored Workspace
12. restart App and Worker and verify restored state

---

# 17. Acceptance criteria

## Restore Workspace

- The dialog validates `.graymoon.json` before mutating the Workspace.
- The user sees what profile and repositories will be restored.
- Missing connectors/repositories are visible before Restore.
- Missing Source repositories do not block a valid Workspace restore.
- Missing or invalid `.graymoon.json` does block Restore.
- `RestoredWithoutDefinition` no longer exists as a successful outcome.
- Non-empty destination folders are never merged into.
- Restore progress is understandable.
- Clean success opens the Workspace automatically.
- Partial-but-usable success explains exactly what remains.
- Structural failure rolls back safely.

## Worker compatibility

- GrayMoon treats App and Worker as a matched-version pair.
- `VersionMismatch` blocks normal Worker operations globally.
- The user is directed to update the Worker.
- Workspace-repository operations no longer ask for feature support.
- The Repositories page no longer shows the Workspace-specific Worker compatibility banner.
- `workspaceRepository` capability plumbing is removed.
- `GetCapabilities` is removed if it has no remaining valid use.
- Worker update/diagnostic flows still work while versions differ.

---

# 18. Non-goals

Do not turn this into:

- automatic GitHub connector creation
- automatic authentication setup
- automatic importing of repositories the user cannot access
- arbitrary restoration into non-empty directories
- a generic App/Worker protocol-negotiation framework
- per-command compatibility matrices
- a new manifest migration system unless the current schema genuinely requires it
- a rewrite of Workspace repository architecture

The objective is a **great Restore Workspace workflow and a simpler App/Worker compatibility rule**, not a broader platform redesign.

---

# 19. AI implementation bootstrap prompt

Use this when handing the work to an implementation agent:

> Review the current `feedback` branch before writing code. Read `RestoreWorkspaceModal`, `WorkspaceRepositoryOperations`, `WorkerConnectionTracker`, the Worker connection/handshake code, the current `GetCapabilities` / `IWorkerFeatureSupportService` implementation, and existing Workspace repository tests. Then review this document and produce a concrete implementation plan before changing anything.
>
> Two product decisions are fixed:
>
> 1. Restore Workspace must preflight and validate `.graymoon.json` before creating a Workspace or cloning into the final root. A missing/invalid definition is a failure, never "restored without definition".
> 2. GrayMoon App and Worker are version-locked. Replace the Workspace-repository feature-capability gate and compatibility banner with the existing App/Worker version mismatch mechanism. Do not introduce another compatibility framework.
>
> Split implementation into independently testable units. Prefer minimal, cohesive changes. Preserve existing Workspace/Feature behavior outside the named changes. Add regression tests before or with each behavior change. Keep the living implementation document updated with discoveries, deviations, test counts, and manual-test gates. Do not commit or push until the requested review gate.

---

# 20. Implementation record (2026-10-08)

## 20.1 Status

| Unit | Status |
|---|---|
| A - Restore preflight model/service | Done |
| B - Restore dialog UX | Done |
| C - Restore transactional behavior | Done |
| D - Worker version lock | Done |
| E - Remove capability system and banner | Done |
| F - End-to-end regression | **Manual - pending** (checklist in 20.6) |

All changes are uncommitted on `feedback`.

## 20.2 Decisions made where the feedback left room

- **Version comparison contract (section 14).** Normalized product SemVer must be equal: trim, drop a leading `v`, drop build metadata after `+`, ignore case. The pre-release label counts, so two CI builds from different commits (`0.2.0-feedback.5` and `0.2.0-feedback.6`) are different versions. One place: `WorkerVersionPolicy`. Used for both `VersionMismatch` and the end of a self-update.
- **The Worker update works without the exact version (owner requirement).** While versions differ, `WorkerBridge` sends only `SelfUpdate` (the update) and `GetHostInfo` (the Worker page diagnostics); everything else returns "The GrayMoon Worker version (x) does not match this GrayMoon version (y). Update the Worker." The `SelfUpdate` payload (`installUrl`) is frozen so any older Worker can still be updated. This is a fixed two-command allow-list, not a per-feature list.
- **Gate placement (section 11.3).** The gate is in `WorkerBridge.SendCommandAsync`, the single command boundary. `IWorkerBridge.GetUnavailableReason()` (default interface method, so test fakes keep compiling) lets Enable and Restore refuse before they change anything. `IsWorkerConnected` keeps its meaning (a Worker is connected), so existing pages still tell "not connected" apart from "wrong version".
- **Preflight source (section 4).** The connector path exists: `GitHubService.GetRepositoryFileUtf8TextAsync` (contents API, default branch). `IRemoteWorkspaceManifestReader` / `GitHubRemoteWorkspaceManifestReader` wraps it; no temporary clone.
- **One evaluator for both checks.** `RestoreDefinitionEvaluator` validates and resolves the definition for the preflight and again for the cloned copy, so the two cannot disagree.
- **Unknown profile values block** with "The Workspace definition is invalid: Unknown workspace profile value "x"." rather than falling back silently.
- **What rolls back (section 10).** Everything up to and including linking Source repositories is structural: a failure, an exception or a cancellation deletes the new Workspace (row, links) and asks the Worker to discard the root. The managed `.gitignore` and the definition rewrite happen after the Workspace is usable; their failures are a **warning on a successful restore**, because the existing drift banner ("Write Workspace definition to disk") repairs them. This is the defined rollback behavior for the two write steps in section 15.3.
- **Root cleanup ownership proof.** New Worker command `DiscardWorkspaceRoot` deletes only an empty folder or a clean clone whose origin is the restored repository (`git status --porcelain --untracked-files=all` empty). Other files, another repository or local changes leave the folder in place, and the reason is returned as `CleanupResidue`. A folder that existed (empty) before the restore is emptied but kept. Deletion reuses the Feature-removal walker (read-only attributes cleared, reparse points never entered, retries for locked files).
- **"Write the definition only if required" (section 7).** Rewritten only when nothing is missing on this computer and the canonical form built from the database differs from the file (for example non-canonical URLs, duplicates, or the Workspace repository listed as its own Source). The Workspace name in the file is not a reason to rewrite, so a local name choice does not dirty the repository.
- **Folder state.** `GetWorkspaceExists` now also returns `isEmpty`. The dialog shows the location, "The folder already exists and is empty. GrayMoon will use it." (info) or "This folder already contains files. Choose another Workspace name or move the existing files." (blocks). Restore checks it again before creating anything.
- **Progress (section 8).** Phases: Checking Workspace definition, Preparing Workspace, Cloning Workspace repository, Applying Workspace profile, Linking N repositories, Preparing Workspace files, Syncing repositories, Finishing. Linking is a single database batch, so there is no "8 of 24" counter.
- **Update Worker action (section 11.4).** The dialog's "Update Worker" button opens the Worker page, which already has the update action and installer details.
- **Missing items wording.** Repositories are shown as "owner/name" (from the URL), connectors as their host name.
- **Choices.** "org/repository"; when two imported repositories read the same, the connector name is appended.

## 20.3 What changed

Worker version lock and cleanup (Units D, E):

- New `WorkerVersionPolicy`; `WorkerConnectionTracker` uses it and exposes `AppSemVer`; `WorkerBridge` gates commands and implements `GetUnavailableReason()`.
- Removed `WorkerFeatures`, `GetCapabilitiesCommand` / Request / Response, its dispatcher, job factory, DI and read-lane entries, `IWorkerFeatureSupportService` / `WorkerFeatureSupportService`, `GetCapabilitiesWorkerResponse`, the Repositories-page banner and its background capability round trip (`RefreshWorkspaceRepositoryBannerStateAsync`, which also saved a database read per page load).
- `WorkerHubMethods.GetHostInfo` and `WorkerHubMethods.DiscardWorkspaceRoot` constants.

Restore (Units A, B, C):

- Application: `IWorkspaceRepositoryOperations.PreflightRestoreAsync`, `RestoreWorkspacePreflight`, `RestoreWorkspaceResult` reshaped (`Warning`, `CleanupResidue`, `UnresolvedConnectors`, `UnresolvedRepositories`).
- App: `RestoreDefinitionEvaluator`, `GitHubRemoteWorkspaceManifestReader`, `WorkspaceService.GetDirectoryStateAsync`, `WorkspaceManifestSerializer.IsNewerSchema`, rewritten `RestoreCoreAsync` (preflight, folder check, create, clone, revalidate, profile, link, files, rollback). `RestoredWithoutDefinition` is gone.
- Worker: `DiscardWorkspaceRootCommand`, `isEmpty` on `GetWorkspaceExists`.
- Dialog: title "Restore Workspace", help text, actionable empty state, preflight on selection with stale-answer protection (`RestorePreflightGate`), preview (profile, repository count, connectors configured / missing), missing-items callout with a scrollable list, Location line, folder verdicts, Worker update callout with App / Worker versions, result panel only when the user must act (missing items, warning, or "Workspace restored, but the initial Sync did not complete."), "Open Connectors" secondary action. A clean success opens the Workspace directly.

Docs: `docs/architecture/05-user-capability-reference.md` (Workspace repository rules, restore, version lock), project `CLAUDE.md` (version lock rule), superseded notes on the Workspace-repository design documents and `docs/workspace-repositories-page-load.md`.

Not changed: `GrayMoon.Desktop/README.md` still describes the `GetCapabilities` banner in its release notes; that is a separate private repository.

## 20.4 Tests

| Project | File | Covers |
|---|---|---|
| App | `WorkspaceRepositoryOperationsTests` | Enable refused on version mismatch; preflight: valid preview without Worker or database change, missing file, invalid JSON, newer schema, three unknown profile values, connector read error, missing connectors and repositories, Workspace repository and duplicates not counted; restore: URL normalization and multiple repositories, canonical definition untouched, missing items never rewrite the definition, existing empty folder, progress phases; no mutation for missing / invalid definition (no attach), non-empty folder, version mismatch; rollback on clone failure, cloned definition invalid or missing, exception and cancellation after the clone, folder kept when it existed, residue reported; warnings (no rollback) for `.gitignore` and definition write failures |
| App | `RestoreWorkspaceFlowTests` | Restore then Sync; no definition creates nothing and never syncs; unresolved items keep the panel open with counts; failed Sync reported as a Sync warning; name follows the selection until typed; name validation; folder verdicts; Restore enabled only for a valid preflight, name, folder and Worker (unresolved Sources allowed); stale preflight answers dropped; choices and filtering; missing summary; profile labels |
| App | `WorkerVersionLockTests` | Normalization; same-version contract; allow-list; tracker Online / VersionMismatch / reconnect; self-update ends on the same product version with different build metadata; bridge refuses normal commands on mismatch, still sends `SelfUpdate` and `GetHostInfo`, sends normally when matched; capability types and banner members are gone |
| Worker | `DiscardWorkspaceRootCommandTests` | Real git: clean clone deleted; pre-existing folder emptied and kept; empty / missing folder; local changes, another repository and plain files are kept with a reason; `GetWorkspaceExists` reports `isEmpty` |

Build: 0 warnings, 0 errors. Full run: Common 261, Worker 567 (+1 pre-existing skip), App 1230 - all passed.

## 20.5 Known limits

- The definition is read from the repository's default branch through the GitHub contents API; the clone also checks out the default branch, and the cloned copy is validated again, so a change in between is caught.
- A Worker that connects is treated as Online until it reports its version (a moment after connecting). That window existed before and is unchanged.
- After a restore with missing repositories, drift detection will (correctly) report repositories that are in the file but not in the Workspace. The banner's "Write Workspace definition to disk" would drop them; import them first.

## 20.6 Manual test gates (Unit F)

1. Matching App and Worker: restore a valid Workspace repository; the dialog shows the preview, Restore opens the Workspace after Sync.
2. Older Worker: badge says "update"; the Restore dialog shows "Update the GrayMoon Worker before restoring this Workspace." with both versions and Restore disabled; Sync and other actions fail with the version message; **Update Worker from the badge or the Worker page succeeds** and everything works after the reconnect.
3. Repository without `.graymoon.json`: blocking error right after selection, Restore disabled, nothing created.
4. Malformed `.graymoon.json` and an unknown profile value: blocking error, nothing created.
5. The real 24-repository Workspace: preview shows 24 repositories and the profile; restore and Sync complete.
6. Missing connector(s) and repository(s): preview lists them; restore succeeds; the panel lists them with Open Connectors / Open Workspace; `.graymoon.json` in the new root is unchanged (`git status` clean).
7. Existing empty folder: info note, restore uses it.
8. Existing folder with a file: error, Restore disabled.
9. Force a clone failure (for example revoke the token): error, no Workspace in the list, no folder left (or a residue line naming it).
10. Reopen the restored Workspace; restart App and Worker; the Workspace, links and profile are intact.
11. The Repositories page of a Workspace with a Workspace repository shows no Worker compatibility banner.
