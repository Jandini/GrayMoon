# GrayMoon - Restore Workspace UX and Worker Version Lock

Date: 2026-10-08  
Target branch for implementation/review: `feedback`  
Primary repository: `Jandini/GrayMoon`

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
