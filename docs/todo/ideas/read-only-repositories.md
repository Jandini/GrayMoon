# Read-only (reference) repositories

Status: draft, design only. Written 2026-11-11 on branch `readonly-repos`. Nothing is built.

Naming: the UI says "read-only" and shows an `ro` badge. The file, the database and the code say `reference`.

## Product purpose and boundaries

This is **one GrayMoon orchestration feature for both developers and non-developers**, not a separate QA edition. GrayMoon remains a multi-repository orchestrator, not an AI instructor or agent.

- **QA and other non-developers** can assemble related source repositories, choose branches, tags and appropriate library versions, inspect dependency relationships, and give their own AI tools the source context needed to investigate a bug, compare versions, run tests or prepare reports. GrayMoon organizes and restores the code; it does not prescribe AI prompts or experiments.
- **Developers** can attach repositories for reading, search, CodeGraph and dependency context without including them in development write orchestration. A Workspace may mix writable and reference Source repositories.
- **The Workspace repository stays writable** even when all Source repositories are reference. It holds the definition and can also contain user-owned investigation notes, reports and scripts; those may be committed and pushed normally. GrayMoon does not require users to store their AI work there.
- **Reference mode limits GrayMoon, not external software or Git permissions.** Editors, AI agents, tests and terminals can still change local files or execute Git commands. This is not a filesystem security sandbox or an origin permission mechanism.

**Core rule: reference repositories remain fully represented in the Workspace and dependency graph, but GrayMoon excludes them from development writes and publication.** Manual local stage/unstage and explicitly selected Undo are exceptions for managing changes, not ways to create commits or publish them. In the main Workspace checkout, reference does not mean frozen: fetch, safe fast-forward pull, and manual switch to an existing branch or tag remain available.

## Decisions (owner, 2026-11-11)

1. **Read-only means no GrayMoon development writes.** No commit, Feature-branch creation, tracked dependency/version-file rewrite, push, PR or destructive branch cleanup for a reference Source repository. Manual local stage/unstage and explicit, selected Undo are allowed; no automatic stage, commit, push or bulk discard.
2. **Nothing is published to origin.** Fetch/pull are the only normal origin interactions for reference Source repositories; GrayMoon never pushes them.
3. **The Workspace flag comes first.** `workspace.reference` makes every Source repository reference (phase 1); an optional per-repository flag adds mixed Workspaces later (phase 2).
4. **The Workspace repository is always writable.** It may be branched, committed, pushed and included in PRs normally; neither flag makes it reference.
5. **Enabling reference warns on local edits, blocks on unpushed commits.** Check live state of the Workspace checkout and every existing Feature worktree. Staged/unstaged file changes produce a warning but do not block. Unpushed commits or a local branch without an upstream block activation; do not push/reset/stash automatically.
6. **Git Changes supports local change management.** Show diffs; allow manual stage/unstage and explicit individual Undo; never commit a reference repository or include it in Commit All or automatic workspace-wide staging.
7. **The commit is recorded.** The selected Workspace checkout branch/tag and full commit are written to `graymoon.json`; Restore reproduces that checkout with reference status intact.
8. **Feature reference worktrees are fixed snapshots.** Include them in the Feature as detached worktrees at the commit captured on creation, without Feature branches, and do not refresh them automatically or introduce a new refresh operation. Show the originating branch or tag and short hash, without representing the detached worktree as actually checked out on that branch.
9. **The dependency graph stays complete.** Reference repositories retain normal discovery, version/package relationships and dependency levels. They are skipped only as write/publish targets; missing packages required from them are surfaced as real unmet dependencies, not silently ignored.
10. **A mode switch is locked during relevant orchestration.** Disable it while an orchestrator is queued/running for the Workspace, and enforce this atomically in the service path. No mid-run retargeting.
11. **Safe manual branch switches.** Warn about local edits and proceed only when Git can preserve them. Never force-checkout, stash or discard to bypass a conflict. A blocked repository must not unnecessarily abort independent work on others.
12. **The JSON property is `reference`.** Show an `ro` badge next to the source branch/tag, and put the switch in the branch dialog footer.
13. **GrayMoon is not an AI sandbox.** Read-only is a GrayMoon orchestration policy, not a guarantee that outside tools cannot change the repository.

## Phases

- **Phase 1 - Workspace flag.** `workspace.reference` in `graymoon.json`, `Workspaces.IsReference` in the database, one switch in the footer of the Workspace branch dialog, recorded commits, restore. Every Source repository is read-only; the Workspace repository is not. All filters are built against the effective value, so phase 2 adds no new filter.
- **Phase 2 - Repository flag.** `repositories[].reference`, `WorkspaceRepositories.IsReference`, the per-repository switch, mixed Workspaces. The full dependency graph is retained in both phases; Phase 2 exercises mixed writable/reference execution plans.

## Problem

GrayMoon treats every Source repository as something the user develops in. Some repositories are in a Workspace only so their source is on disk next to the others: to read, to search, to give an AI tool or CodeGraph the full picture. Today these repositories are picked up by every workspace-wide operation: Create Branch makes a branch in them, Prepare Workspace switches and pushes them, Update Dependencies commits to them, Push pushes them.

The only protection today is being checked out on a tag (`IsOnTag` / `CheckedOutTag`). That is not the same thing: a tag checkout is frozen at one commit, cannot pull, and the protection disappears the moment the user switches to a branch.

## Behavior

### Compared with a tag checkout

| | On a tag | Read-only |
|---|---|---|
| What it is | State of the checkout (changes with a checkout) | Setting of the repository in the Workspace (stays until switched off) |
| Scope | Per context (Workspace or one Feature) | Per Workspace, the same in every context |
| Pull | No | Yes, on the current branch |
| Manual switch of branch or tag (per repository) | Yes | Yes, when Git can preserve local changes |
| Workspace-wide operations | Excluded | Excluded |
| Stage, commit | Possible | Manual stage/unstage permitted; commit never |
| Stored in `graymoon.json` | `tag` + `commit` | `reference` + `branch` + `commit` |

A read-only repository can also be on a tag. Then both apply: no pull (there is no branch), and it stays read-only after the user leaves the tag.

### Allowed

- Sync, Quick Fetch, branch and tag refresh, hook-driven state updates.
- Pull (the incoming side of Sync Commits), per repository and as part of the workspace-wide pull.
- Switch Branch and tag checkout from the repository row, with a warning for local edits and no forced checkout if Git would overwrite them.
- Manual stage/unstage of selected files, and explicitly selected Undo of local file changes in Git Changes.
- Everything that only reads: Open in, the diff viewer, CodeGraph `include`, the dependency graph display.

### Refused or skipped

Workspace-wide development write operations skip the repository rather than fail the entire run, but the skip is **reported with reason**. Per-repository development write actions on its row are disabled with a tooltip ("Read-only repository"). Manual stage/unstage and selected Undo in Git Changes are exceptions.

| Operation | Workspace-wide | On the repository row |
|---|---|---|
| Create Branch | skipped | n/a |
| Checkout common branch (Switch Branch for the Workspace) | skipped | allowed (manual) |
| Prepare Workspace (branch, update, commit, push) | skipped in every phase | n/a |
| Update Dependencies, Update and Push, version-file update | skipped | disabled |
| Push, Push Synchronized, Push Updated, set upstream | skipped | disabled |
| Undo Push Commits | skipped | disabled |
| Sync Commits | pull only | pull only |
| Return to Default | skipped | manual switch only, no cleanup offered |
| Update Branch (merge default into the branch) | n/a | disabled |
| Create PR, bulk Merge PRs and their return follow-ups | skipped | disabled |
| Commit All, commit (Git Changes) | skipped | disabled |
| Stage/unstage selected local changes, Undo selected changes | automatic/bulk staging skips reference | allowed, explicit selection only |
| Create / Repair Feature | included, detached, no Feature branch | n/a |

### Turning read-only on

Staged or unstaged file changes must not strand the user: they can still inspect, stage/unstage, or explicitly undo their local changes after changing mode. **Unpushed commits are different:** turning read-only on removes GrayMoon's push path, so the mode change is refused until those commits are resolved outside reference mode.

- The Worker checks **live** Git state at confirmation, not cached counters, across the Workspace checkout and every existing Feature worktree for the Source repository.
- Uncommitted file modifications (staged or unstaged) produce a warning and **do not block** activation. Never automatically discard, stash, reset or push them.
- Unpushed commits on a branch (ahead of its upstream) block activation. A local branch with no configured upstream also blocks, since GrayMoon cannot establish that its work was published. Detect outgoing commits in any existing Feature context too.
- A repository whose Git state cannot be determined safely blocks activation with an actionable reason. A fully pushed branch/PR does not block merely because a PR remains open.
- For the Workspace flag, validate every Source repository and make the switch atomic: any blocking repository refuses the **entire** switch, with an aggregated list of reasons and warnings. The Workspace repository is excluded from this check.
- Clearing read-only needs no Git cleanliness check, but is subject to the same orchestration-in-progress lock.
- Later external edits or commits are still visible. External outgoing commits are never pushed by GrayMoon while the reference flag is set.
- The separate question of whether a *published* branch ahead of its parent/default should also block remains open; the agreed minimum is no **unpublished** commits relative to tracked upstream.

### Pull

- Pull is fast-forward only. A read-only repository should never have local commits; when it has (made outside GrayMoon), the pull is refused for that repository with a message, never merged or rebased.
- A pull that cannot safely fast-forward because local changes would be overwritten says so: "Billing has local changes that block pulling. Review or Undo the affected changes, or turn read-only off if you need to commit." Never force, merge, rebase or silently stash them.
- A manual branch/tag switch may proceed with dirty files **only if Git preserves those changes safely**. Warn first; if Git would overwrite them, leave that checkout unchanged and show an actionable conflict. Do not block independent repositories in a larger operation.
- Outgoing commits on a read-only repository are shown, but never counted in "push recommended", the Push button counter, or the notification panel.
- Incoming commits on a read-only repository do count: the notification panel and the pull counter include them.

### Git Changes

A reference repository remains visible on the Changes page because an AI tool, editor or build may have modified files outside GrayMoon. Allow **local change management** without allowing a commit or push.

- Show the repository and its changed files with an `ro` badge. Diffs and the staged/unstaged grouping work normally.
- **Manual stage and unstage** are available for selected files/folders in that repository. A stage-all operation over the whole Workspace (or an ancestor containing multiple repositories) **skips reference repositories**; it never stages them automatically.
- Allow the existing explicit **Undo** action for selected file changes. Do not add a special Discard action, silently undo everything, or automatically reset the repository. The user chooses what to undo and may leave other local changes intact.
- **Commit, Commit All and AI commit-message evidence** exclude reference Source repositories even if some of their files are staged. The commit dialog reports how many reference repositories were skipped.
- Show staged files created outside GrayMoon exactly as they are; never auto-commit them. Reference-mode enforcement for commit and push remains at the App service boundary (`IWorkspaceGitChangesOperations`) and downstream Git services, not just in the page. Stage/unstage and selected Undo are *allowed* operations with their normal existing safety checks.

In a read-only Workspace, the Workspace repository remains the only repository GrayMoon can commit; Source reference repositories may still have manually staged local changes.

### Features

A reference repository is **part of a Feature for source context**, even though it does not get a Feature branch. The Feature folder includes its source alongside normal writable worktrees, for AI tools, CodeGraph, investigation and comparison.

- Create a **detached worktree at the exact Workspace checkout commit captured during Feature creation** (`BaseCommitSha` from the same `GetHeadCommits` snapshot). No Feature branch, PR, parent branch or branch deletion is created for the reference Source repository.
- Keep that reference worktree **fixed for the life of the Feature**. Pulling or switching the repository in the main Workspace must not move an existing Feature's reference checkout. No automatic update, no "Merge Recent Commits" for reference worktrees and **no new Refresh reference action** in this design.
- Preserve the **originating branch or tag identity and the captured commit** as Feature-context metadata. In the Feature UI show, for example, `Authentication · main (source) · ro · abc123`. The branch is provenance, not the checked-out Git branch; Git remains detached. Do not overwrite the Workspace definition's selected commit with Feature state.
- An existing writable Feature branch when a repository is *later* marked reference is not retroactively rewritten to detached; GrayMoon stops writing/pushing it. Future Features created while reference use detached snapshots.
- If all Source repositories are reference, the **Workspace repository alone** still gets a normal writable Feature branch. Its definition and user-owned work may be committed and pushed as normal.

### Dependency graph and orchestrator behavior

A reference repository is **not removed from dependency discovery or graph planning**. Retain its nodes, discovered projects, package/version information, producer-consumer relationships and dependency levels. The engine and graph remain the same for QA and developers.

- Calculate and display the complete graph and ordinary version/dependency relationships, including reference package producers. Do **not** prune references before level calculation or create a separate "QA" dependency mode.
- Filter on **execution eligibility**: skip tracked updates, version-file changes, commits, package publication and pushes *to* a reference repository at every relevant phase. Writable repositories continue through normal orchestration.
- Preserve existing version selection and registry/package lookup behavior for writable consumers. If a writable consumer requires a package version that a reference producer will not publish and the package does not exist, orchestration must **report the missing package and affected dependency**, not count the step as completed or pretend the reference producer published it. An incorrect reference configuration can legitimately make a workflow fail.
- Retaining a reference node must not create a false prerequisite that GrayMoon waits for it to *publish during this run*. Distinguish an existing registry package from an expected publication by an eligible writable producer; preserve ordering where relevant. Verify the current handling of tag-pinned producers before implementing.
- No special QA-only dependency editor or new graph algorithm is part of this feature. GrayMoon remains a repository orchestrator; choosing/test-running library versions remains a separate user workflow.

### The Workspace as a whole

`workspace.reference: true` makes every Source repository read-only:

- effective read-only = Workspace flag OR repository flag,
- a repository added later is read-only without any further step,
- the per-repository switch (phase 2) is shown on and disabled, with the reason,
- workspace-wide write actions stay available, but they act on the Workspace repository only. In a Workspace without a Workspace repository they are disabled, because nothing is writable.

### The Workspace repository

The Workspace repository is never read-only. GrayMoon writes `graymoon.json` (membership, profile, the flag, recorded commits, version files), the managed `.gitignore` section and `codegraph.json` there, and those changes must be committed and pushed like today.

- It never carries the flag and is not covered by the Workspace flag.
- Branch, commit, push, PR and Feature branch all work for it in a read-only Workspace.
- It is not part of the clean check.

## `graymoon.json`

```json
{
  "version": 2,
  "workspace": {
    "profile": { "type": "basic", "versioning": "none", "ci": "none" },
    "reference": true
  },
  "repositories": [
    {
      "name": "Billing",
      "repositoryUrl": "https://github.com/acme/Billing.git",
      "connectorUrl": "https://github.com",
      "branch": "main",
      "commit": "3f2a9c0d5e7b1a4c6d8e0f1a2b3c4d5e6f7a8b9c"
    },
    {
      "name": "Platform",
      "repositoryUrl": "https://github.com/acme/Platform.git",
      "connectorUrl": "https://github.com",
      "tag": "v4.2.0",
      "commit": "9b8a7f6e5d4c3b2a1f0e9d8c7b6a5f4e3d2c1b0a"
    }
  ]
}
```

With `workspace.reference` the repositories need no flag of their own. In phase 2 a single repository carries `"reference": true`.

### The flag

- `workspace.reference` and `repositories[].reference`: optional bool, written only when true, so existing files do not change.
- Database-owned: `Workspaces.IsReference` and `WorkspaceRepositories.IsReference` (INTEGER NOT NULL DEFAULT 0), one new strict migration step. It must work without a Workspace repository, and the filters run against the database, several of them in SQL.
- `BuildFromDatabaseAsync` writes it, Restore reads it, drift detection compares it. `WorkspaceManifestDrift` gets a list of repositories whose flag differs; the Workspace flag is reported with the profile fields.
- Use the lenient bool converter (`LenientBooleanJsonConverter`) and make its error message name the property.

### The recorded commit

Decided: the commit of each read-only repository is recorded, and Restore puts the repository back at that commit.

Today only a tag checkout is recorded (`tag` + `commit`, `WorkspaceDefinitionTagPin`). A read-only repository on a branch gets the same treatment with `branch` + `commit`:

- `commit`: the full hash HEAD is on.
- `branch`: the branch that is checked out. Without it Restore could only make a detached checkout, and a detached checkout cannot pull. With it Restore creates the local branch at the recorded commit, tracking `origin/<branch>`. When origin has moved on, the repository shows incoming commits and the user decides when to pull.
- On a tag the existing `tag` + `commit` pin is used, unchanged.
- An entry is valid with `tag` + `commit`, with `branch` + `commit` (read-only repositories only), or with none of them.

When it is written:

- when read-only is turned on (the clean check has just read HEAD),
- after a pull, a manual branch switch or tag checkout through GrayMoon,
- when a Sync or a hook finds the repository on another commit (pulled or switched with git outside GrayMoon).

These are the places that record tag pins today (`WorkspaceBranchOperations.SetRepositoryTagPinAsync`, `WorkspaceGitService.RecordCheckedOutTagPinsAsync`), plus the pull and the hook sync.

Ownership: file-owned, like tag pins. There is no database copy, rewrites keep it (`WorkspaceRepositoryTagPins.CopyOnto` is extended), drift ignores it. It is recorded from the special Workspace context only, never from a Feature.

Consequences:

- Every pull of a read-only repository changes `graymoon.json`. The change shows up in the Workspace repository on the Changes page, and the owner commits it when the new state should be shared. This is the reason the Workspace repository cannot be read-only.
- Recording needs a Workspace repository. Without one the flag still works from the database, but there is no file to record into and nothing to restore from.
- Turning read-only off removes `branch` + `commit` from the entry. A tag pin stays.

### Restore

- The Worker checks a freshly cloned read-only repository out at the recorded commit (`SyncRepositoryCommand.CheckoutPinnedCommitAsync` is extended): `branch` + `commit` gives a local branch at that commit with its upstream set; `tag` + `commit` works as today.
- A recorded commit that does not exist in the clone fails the sync for that repository and removes the clone, like an invalid tag pin today.
- The flags are imported into the database before the first Sync, so no operation sees the repository as writable in between.
- The repository stays read-only. Restore never turns the flag off.

### Schema version

An older GrayMoon rejects a `commit` without a `tag` as an invalid tag pin, so it could not read the file at all, and the message would be misleading. A file that contains `reference` or `branch` is therefore written with `"version": 2`; an older GrayMoon then reports "Workspace definition schema 2 is newer than this GrayMoon". A file without them stays at version 1 and keeps working everywhere.

## Design

### One predicate, not sixty edits

`IsOnTag` / `CheckedOutTag` is tested in about 280 places in 87 files, many of them the same sentence written again (`!wr.IsOnTag`, `string.IsNullOrWhiteSpace(link.CheckedOutTag)`, and SQL forms of it). Adding `|| IsReference` next to each one would repeat the pattern that the review rules warn about.

Proposal: introduce two named questions and move the call sites to them.

- **"May GrayMoon make a development write to this repository?"** False when on a tag or effectively reference. This guards commits, branches, tracked version/dependency updates, pushes, PRs and publication.
- **"May GrayMoon manage local changes here?"** Allow explicit stage/unstage and selected Undo for reference repositories, subject to normal Git safety checks; this is not permission to commit.
- **"Can this checkout pull?"** False on a tag or detached Feature snapshot; a reference Source checkout on an ordinary branch can pull if Git can safely fast-forward.

`IsOnTag` stays for what is really about the tag: the tag badge, `HasNewerTag`, tag pins, detached-HEAD handling in the state writer.

Read-only is per Workspace and the tag is per context, so the first question takes both: the link and the Workspace for read-only, the context state for the tag. The Feature-context scoping rules apply unchanged: read the tag from the context the operation runs in. The Workspace-role link always answers "not read-only".

### Enforce in the service path, not only in the page

The existing rule says "filter at the call site, and the service path leaves tags untouched". For read-only the service-side check matters more, because the REST endpoints (`WorkspaceOperationsEndpoints`) and the notification panel start the same operations without the Repositories page. A reference repository id that still reaches push, commit, branch creation or tracked-file update is skipped there and reported as skipped, not as a failure. **Manual local stage/unstage and explicitly selected Undo are allowed** through the existing Git Changes service. All entry points must share the same effective-reference policy. Mode changes also need an atomic Workspace-scoped orchestration guard so queued/running operations cannot race with activation.

### Places that need the change

Found by following the tag checks. Each is a place where a read-only repository would be changed today.

- **Branches**: `WorkspaceBranchOperations` (create branch filter at the `!wr.IsOnTag` line, workspace checkout), `WorkspaceRepositories.Branches.cs` (`CreateBranchesAsync`, `CheckoutCommonBranchAcrossWorkspaceAsync`, common-branch computation, Update Branch modal), `WorkspaceContextBar`, `BranchModal` (the "skip repositories on tags" option does not apply: read-only is never optional).
- **Prepare Workspace**: `WorkspaceRepositories.PrepareWorkspace.cs` (`tagFilteredRepoIds`), `PrepareWorkspaceOrchestrator` and `PrepareWorkspacePhases`.
- **Update**: `DependencyUpdateOrchestrator` (three filters: the version-file-only path, `IsPinnedToTag` in the level walk, the version-file commit), `WorkspaceGitService.Projects.cs` (`GetUpdatePlanAsync`, `SyncDependenciesAsync`, `RunUpdateSingleRepositoryAsync`), `WorkspaceUpdateAndPushOperations` (default-branch review), `WorkspaceRepositories.Update.cs`.
- **Push**: `WorkspacePushService` (plan, post-push bookkeeping), `WorkspaceProjectRepository.Push.cs` (push plan payload), `WorkspaceRepository` (context push candidates), `WorkspaceUndoPushHandler`, `WorkspaceRepositories.Push.cs` and `.UndoPush.cs`.
- **Commit and sync commits**: `WorkspaceGitService.Commit.cs` (two filters), `WorkspaceCommitSyncHandler`, `WorkspaceRepositories.CommitSync.cs` (here the filter splits: pull yes, push no).
- **Return to Default and PRs**: `WorkspaceSyncHandler` (`ClassifyRepository`, unattended batch), `WorkspaceRepositories.ReturnToDefault.cs`, `.BulkMerge.cs`, `.PullRequests.cs`, `PRBadge`.
- **Counters and notifications**: `WorkspaceRepositoryLinkListQueryService` (header counters `Unmatched`, `Push`, `OutOfDateFiles`, `CreatablePr`, `Lowest`; `Incoming` must keep read-only repositories), `WorkspacePendingActionsService`, `NotificationPanelRepoSets` in `UiRaceGuards.cs`.
- **Features**: `WorkspaceFeatureOperations` (Create, Repair, Remove, Roll back all branch on `PinnedTag == null`), `FeatureBranchPolicy` / `FeatureBranchGuard` (expected branch is null for a read-only repository), `WorkspaceBranchOccupancyService`.
- **Definition**: `WorkspaceManifest` records, `WorkspaceManifestFile` (parse, serialize, version), `WorkspaceDefinitionTagPin` and `WorkspaceRepositoryTagPins` (the `branch` + `commit` form), `WorkspaceManifestService` (build, drift), `RestoreDefinitionEvaluator` and `WorkspaceRepositoryOperations` (restore import), Worker `SyncRepositoryCommand` (checkout at the recorded commit).
- **Row and DTO**: `WorkspaceRepositoryLink`, `WorkspaceRepositoryLinkListItemDto` and its mapper (including `WithBranchOverride`), `WorkspaceRepositoriesRow.razor`, `CommitsBadge`, `DivergenceBadge`.
- **Git Changes**: `IWorkspaceGitChangesOperations` commit / stage / unstage, Commit All, the AI commit message scope.

### UI

- Repositories grid: a small `ro` badge next to the branch name or tag, with the tooltip "Read-only repository". Write badges (push, update, PR) render as inactive, like on a tag.
- Branch dialog (`BranchModal`): the mode is changed in the footer, for example a "Read-only" switch on the left of the footer buttons. The dialog changes the mode of what it was opened for: opened for the Workspace it sets the Workspace flag (phase 1), opened for one repository it sets that repository's flag (phase 2). In a read-only Workspace the per-repository dialog shows the switch on and disabled, with the reason.
- Turning the switch on runs the live outgoing-commit check first. Dirty-file warnings do not prevent the toggle; unpushed commits, absent upstream or unreadable Git state do. Report all affected contexts.
- Disable the Read-only switch while any relevant orchestration is queued or running for that Workspace; enforce the same lock in the service/API path, not only in Razor. Other Workspaces remain independent.
- Workspaces page: shows that a Workspace is read-only (the same `ro` badge).
- Search filter `ro:yes` / `ro:no` in `FilterSearchExpression`, and a header counter "Read-only (n)" next to "Tagged (n)". Both matter in phase 2 only.
- Setting or clearing the flag writes the definition to disk (`SyncDefinitionToDiskAsync`), like a membership change.

## Not yet decided

The following genuinely remain to be decided; the Feature snapshot, local staging/Undo, dirty-file warnings, missing-package graph inclusion and in-flight toggling questions were settled in discussion.

1. **Published commits ahead of the parent/default branch.** The agreed activation gate checks for *unpublished* commits relative to upstream, across all contexts. Should a branch that is fully pushed but ahead of its default/parent also block reference mode? Proposed: no; published branches are valid versions to inspect.
2. **Workspace flag versus repository flags (phase 2).** When `workspace.reference` is turned off, do existing `repositories[].reference` flags remain? Proposed: yes; Workspace toggle never overwrites each repository's choice.
3. **No Workspace repository.** The database-level reference flag can work without one, but there's no `graymoon.json` in which to record an exact reproducible selected commit. Proposed: permit reference mode with a clear limitation; restore-by-definition requires a Workspace repository.
4. **Diagnostics depth.** Decide whether the first implementation must explicitly say "required package is missing; its producer is read-only" or whether existing precise missing-package diagnostics are sufficient. In either case a missing package must be surfaced as an error, never success.

### Resolved in the 2026-11-11 review

- **Manual branches/tags:** allowed only as per-repository user actions when Git can preserve local changes. Workspace-wide branch switching skips reference Sources.
- **Dependency graph:** all reference nodes and version relationships remain; only writes/publishing are skipped. Configuration mistakes may cause real missing-package failures.
- **Feature worktrees:** fixed detached snapshots from Feature creation, with source branch/tag and commit visible; no refresh action.
- **Local changes:** selected stage/unstage and explicit selected Undo allowed, but no reference commits, automatic staging, mass discard, push or PR.
- **Activation:** staged/unstaged edits warn and allow; unpushed commits or a missing upstream block. Check the Workspace checkout and existing Feature worktrees.
- **Running orchestrators:** lock the mode switch Workspace-wide during relevant queued/running work; release after the run fully finishes or stops. Serialize launch/toggle to prevent races.

## Smaller points

- **GrayMoon's own files in a repository.** Git hooks (`.git/hooks`, not tracked) stay installed; the state updates depend on them. Version-file and `.csproj` updates write tracked files: skipped. Package restore writes only ignored files: allowed.
- **Hard protection at git level.** GrayMoon can only restrict its own operations; `git push` from a terminal still works. Setting `remote.origin.pushurl` to an invalid value would stop that, but it changes repository config and must be undone when the flag is cleared. Not proposed for the first version.
- **Open PR when the flag is turned on.** A PR whose branch is fully pushed does not block. It is no longer shown or acted on.
- **Existing Features when the flag is turned on.** A pushed Feature branch stays as it is; nothing pushes it again. Repair Feature does not convert it to a detached worktree.
- **CI, PR and Actions pages.** They skip read-only repositories, which also saves GitHub API calls on every refresh.
- **REST and automation.** `WorkspaceOperationsEndpoints` returns the skipped repositories with a reason.
- **Workspace merge and membership replace.** `ReplaceRepositoriesCoreAsync` and `MergeRepositoriesAsync` recreate link rows; the flag must survive both.
- **Clone depth.** Read-only repositories are candidates for a shallow or blobless clone later. Out of scope.

## Constraints

- Feature-context scoping: read-only comes from the Workspace and the link, the tag comes from the context the operation runs in. No new write to the shared link from a Feature.
- A skipped repository is reported as skipped, with the reason. It is never a failure and never silent.
- Treat orchestration launch and mode changes as mutually exclusive **per Workspace**, with a shared service-level guard. While relevant orchestration is queued/running, refuse mode changes to the Workspace or any of its Source repositories (even through REST/automation). The switch unlocks after the run fully finishes, fails, or is cancelled and stopped. The run uses its originally captured eligibility and is not retargeted mid-flight. Independent Workspaces are unaffected.
- New database columns have a default; the migration is a new strict step.
- Recording a commit never fails the pull, switch or sync that caused it; a failed write is reported and the next Sync records it.
- When built: update `docs/architecture/01`, `03`, `05`, `06` and tag-pinned guidance in `AGENTS.md` and `CLAUDE.md`. Tests must cover: a reference repo with local edits (warning + manual stage/unstage/Undo); unpushed commits (activation refused); fixed Feature snapshots and source-label metadata; safe branch-switch conflicts; service-side commit/push rejection; full graph with writable consumer and reference producer; absent registry packages; and mode-toggle/orchestrator concurrency. No reference repository may be committed or pushed by GrayMoon.
