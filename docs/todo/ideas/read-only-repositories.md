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
3. **Workspace default and repository state are independent.** `workspace.reference` means the default for *new* Source repositories. `repositories[].reference` is the actual state of each existing Source repository, with explicit `false` allowed even when the default is `true`. The Workspace default does not override existing repository choices.
4. **The Workspace repository is always writable.** It may be branched, committed, pushed and included in PRs normally; neither flag makes it reference.
5. **Enabling reference guards unfinished branch work.** Check live state of the Workspace checkout and every existing Feature worktree. Staged/unstaged file changes warn but do not block. Unpushed commits or a branch without an upstream block. **A non-default branch with any commits ahead of the repository's actual default branch also blocks, even when all those commits are pushed.** No extra warning is needed for pushed-but-unmerged work: the switch is refused with an actionable reason. Never push/reset/stash automatically.
6. **Git Changes supports local change management.** Show diffs; allow manual stage/unstage and explicit individual Undo; never commit a reference repository or include it in Commit All or automatic workspace-wide staging.
7. **Manifest reproducibility.** Record the default for new Source repositories, the explicit reference state of every existing Source repository, and the checked-out branch/tag and full commit of each reference repository in `graymoon.json`. Restore imports states before syncing and reproduces the saved reference revisions; this reflects the committed manifest snapshot, not subsequent changes in another user's Workspace.
8. **Feature reference worktrees are fixed snapshots.** Include them in the Feature as detached worktrees at the commit captured on creation, without Feature branches, and do not refresh them automatically or introduce a new refresh operation. Show the originating branch or tag and short hash, without representing the detached worktree as actually checked out on that branch.
9. **The dependency graph stays complete.** Reference repositories retain normal discovery, version/package relationships and dependency levels. They are skipped only as write/publish targets; missing packages required from them are surfaced as real unmet dependencies, not silently ignored.
10. **A mode switch is locked during relevant orchestration.** Disable it while an orchestrator is queued/running for the Workspace, and enforce this atomically in the service path. No mid-run retargeting.
11. **Safe manual branch switches.** Warn about local edits and proceed only when Git can preserve them. Never force-checkout, stash or discard to bypass a conflict. A blocked repository must not unnecessarily abort independent work on others.
12. **Two distinct settings surfaces.** Edit Workspace displays the derived none/mixed/all read-only state, offers batch changes, and controls the default for new Source repositories. A repository's Branch dialog can change **only that selected repository**. No read-only numeric counter is needed; the `ro` badge remains.
13. **GrayMoon is not an AI sandbox.** Read-only is a GrayMoon orchestration policy, not a guarantee that outside tools cannot change the repository.

## Phases

- **Phase 1 - Workspace default and batch action.** Store `workspace.reference` as the default for newly added Source repositories (database field e.g. `Workspaces.ReferenceDefault`), show its controls in **Edit Workspace**, and support applying all/none to existing Source repositories with safety validation. Restore preserves the Workspace default and fixed reference revisions. The Workspace repository stays writable; use a per-repository eligibility policy from the outset.
- **Phase 2 - Individual states and mixed Workspaces.** Persist `repositories[].reference` and `WorkspaceRepositories.IsReference` for each Source, with both `true` **and** `false` explicitly representable. Add the per-repository Branch dialog switch, derived none/mixed/all Workspace summary, and manifest round-trip tests. Phase 1 definitions without per-repository flags import each Source at the saved Workspace default. The dependency graph remains complete in both phases.

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

Staged or unstaged file changes must not strand the user: they can still inspect, stage/unstage, or explicitly undo their local changes after changing mode. **Unpublished or unfinished branch commits are different:** turning read-only on removes GrayMoon's development write and push paths. Activation is refused if any existing checkout has unpushed commits, lacks a required upstream, or is on a non-default branch containing commits ahead of the repository's default branch—even if already pushed.

- The Worker checks **live** Git state at confirmation, not cached counters, across the Workspace checkout and every existing Feature worktree for the Source repository.
- Uncommitted file modifications (staged or unstaged) produce a warning and **do not block** activation. Never automatically discard, stash, reset or push them.
- Unpushed commits on a branch (ahead of its configured upstream) block activation. A local branch with no configured upstream also blocks, since GrayMoon cannot establish that its work was published. Detect outgoing commits in any existing Feature context too.
- **New definitive rule:** if the checkout is on a branch other than the repository's actual default branch and HEAD has one or more commits not reachable from the default branch, **refuse reference activation even when the feature branch is fully pushed**. Use a real ahead-of-default Git comparison (conceptually `git rev-list --count origin/<actual-default-branch>..HEAD`), not the ahead-of-upstream count, PR status, branch-name guessing, or whether the branch has been published. Resolve the repository's real default branch (not hard-coded `main` or `master`) and evaluate the Workspace checkout and every existing Feature worktree for that Source. If the comparison cannot be made reliably, block with an actionable reason. A non-default branch with zero ahead-of-default commits may be eligible if all other checks pass. Do not merge, push, reset or switch branches automatically to make it eligible.
- A repository whose Git state or default-branch comparison cannot be determined safely blocks activation with an actionable reason. An open PR by itself does not block; its checked-out branch must still pass the ahead-of-default rule. No special warning is needed for pushed, unmerged commits, because such a branch is already ineligible.
- For the Workspace flag, validate every Source repository and make the switch atomic: any blocking repository refuses the **entire** switch, with an aggregated list of reasons and warnings. The Workspace repository is excluded from this check.
- Clearing read-only needs no Git cleanliness check, but is subject to the same orchestration-in-progress lock.
- Later external edits or commits are still visible. External outgoing commits are never pushed by GrayMoon while the reference flag is set.

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

**The Workspace-level setting is a default, not a permanent override.** Two independently persisted facts are required:

- `workspace.reference` is the **default read-only state for newly added Source repositories**. Changing only this setting does not change existing repositories.
- `repositories[].reference` is the **actual read-only state of each existing Source repository**; `false` must remain valid even when the Workspace default is `true`. For an existing Source, eligibility depends on its own saved state, not `workspace.reference || repository.reference`.

**Edit Workspace** displays a three-state none/mixed/all summary computed from current Source states. This aggregate is not stored separately and shows no numeric count. The user may choose batch **Make all read-only** or **Make all writable**; these actions update actual Source flags with the usual validation and may set the default for new additions to the same choice. If the user wants to change *only* the future default, offer an explicit distinct action or confirmation option. A tri-state summary alone cannot represent both dimensions.

The repository **Branch dialog** controls only the selected Source repository. For example, starting with 20 reference Source repositories and `workspace.reference: true`, unchecking `Authentication` changes only that repository to writable: 19 remain reference, Edit Workspace shows mixed, and newly added repositories still default to reference. The Workspace default must never force the per-repository switch on or disable it.

When unchecking Workspace-wide read-only, distinguish **Make existing Source repositories writable** from **Change only the default for future repositories**. Never silently erase individual choices. Batch activation follows both the unpushed-commit and ahead-of-default checks and is atomic; relevant orchestration locks apply. A Workspace with zero Sources has no aggregate, but still has a configurable future default.

Workspace-wide write operations stay available for all eligible writable Sources and the Workspace repository; the Workspace repository is excluded from Source calculations, toggles, and inheritance, and is always writable.

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
      "reference": true,
      "branch": "main",
      "commit": "3f2a9c0d5e7b1a4c6d8e0f1a2b3c4d5e6f7a8b9c"
    },
    {
      "name": "Platform",
      "repositoryUrl": "https://github.com/acme/Platform.git",
      "connectorUrl": "https://github.com",
      "reference": false,
      "branch": "develop"
    }
  ]
}
```

The saved Workspace is **mixed**: `Billing` is reference at a recorded commit, `Platform` is explicitly writable, and **future** Sources default to reference. A user restoring this committed definition receives these same settings, not an implicit all-read-only override. The Workspace repository always remains writable.

### The flag

- `workspace.reference` is the future **Source repository default** (missing defaults to `false` for legacy definitions). Its database field should convey this meaning, e.g. `Workspaces.ReferenceDefault`.
- `repositories[].reference` is the **current individual Source state**. Write both `true` and `false` explicitly for every Source in Phase 2 version-2 definitions; omitting `false` would lose an intentional override when the default is `true`.
- Existing Phase 1 manifests with no per-repository values import each Source with the Workspace default; legacy manifests with neither field import as writable. Once imported, existing Source flags are independent of later changes to the default.
- Database: Workspace default and per-link `WorkspaceRepositories.IsReference` (booleans NOT NULL DEFAULT 0) with strict, phase-appropriate migrations. The Workspace-role link never becomes reference.
- `BuildFromDatabaseAsync` writes the Workspace default and individual Source values, Restore imports them **before the first Sync**, and drift detection compares both kinds of state. Rewrites, merges, membership replacement and Feature operations must preserve their distinction; `WithBranchOverride` must carry per-link state.
- Use `LenientBooleanJsonConverter` with property-specific error messages and no silent coercion of invalid values.
- **Snapshot, not live coordination:** the same **committed manifest revision** reproduces saved modes and recorded reference revisions. A different user's later pulls, local edits, or Feature changes are not automatically synchronized. Writable repositories keep their existing restore behavior; the design does not promise a byte-identical copy of another user's uncommitted state.

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

### One authoritative capability policy, not sixty ad hoc checks

**Owner decision (2026-11-11): a central repository-operation policy is mandatory.** The `IsOnTag` / `CheckedOutTag` search identified roughly 280 uses in 87 files, including UI components, counters, service entry points, orchestration paths and Worker commands. Do not implement reference mode by appending `|| IsReference` to each existing tag check, or by introducing independent UI and service booleans. This is one source of truth for what GrayMoon may do to a repository, regardless of who initiated the action.

Introduce a single application/domain policy service (suggested name `IWorkspaceRepositoryOperationPolicy`; finalize the interface with the existing architecture). It returns a **structured capability decision** for a named operation, repository and context: allowed or not, scope (individual versus bulk/orchestrated), and a stable reason code suitable for logging, disabled-action tooltips, API responses and skipped-repository reporting. Distinguish an explicit request that must be refused from a bulk operation that skips an ineligible repository. A skip must never masquerade as a successful write.

Inputs are the repository's **role** (Workspace versus Source), the Source link's **actual** `IsReference` flag, the **correct Workspace or Feature context** (tag, branch, detached snapshot), the operation and its interaction scope, plus any relevant live safety state. `workspace.reference` is only the default for new Source links and must **never** be combined with an existing Source's flag or applied to the Workspace-role repository. A Feature's context state must not overwrite shared Workspace link state. Existing tag-specific semantics must be preserved; `IsOnTag` still describes checkout state for badges, pin recording and tag-specific Git behavior, but it must not be the scattered authority for operation eligibility.

**Capability distinctions are essential; one universal `CanWrite` boolean is insufficient:**

| Operation/capability | Reference Source on a normal Workspace branch | Reference Source in a detached Feature snapshot | Writable Source / Workspace repository |
|---|---|---|---|
| Read, inspect, diff, dependency graph, CodeGraph | allowed | allowed | current behavior |
| Fetch and safe fast-forward pull | allowed | no pull or automatic movement | current behavior, subject to tag/checkout state |
| Manually switch to an existing branch or tag | allowed if Git can preserve edits | not allowed to move the fixed Feature snapshot | current behavior |
| Explicit local stage/unstage and selected Undo | allowed with normal Git safety checks | allowed with normal Git safety checks | current behavior |
| Bulk stage, Commit All, ordinary commit | skipped/refused | skipped/refused | current behavior, subject to tag-specific rules |
| Create Feature branch, update tracked dependencies or version files, push, publish, create PR, bulk merge/cleanup | skipped/refused | skipped/refused | current behavior, subject to tag-specific rules |

For a reference Source **on a tag**, preserve the existing tag checkout restrictions as well as reference restrictions: no pull or new development writes, but do not accidentally disable the intentionally allowed explicit local change-management actions. A detached reference Feature worktree is a fixed snapshot, not a moving branch. The Workspace repository remains eligible for normal development even when all Sources are reference.

**Every consumer must use the same policy decision:** repository rows, dialogs and action badges; header counts, notification sets and SQL-backed candidate queries; Workspace and Feature service methods; REST/automation endpoints; Prepare Workspace, Dependency Update and Push orchestrator phases and both execution lanes; and Commit All / Git Changes dispatch. Where filtering must run in SQL, expose a canonical, SQL-translatable eligibility expression or query built from the same policy semantics, with parity tests against the in-memory decision; do not maintain an independent hand-written predicate per counter. UI disabling is convenience only: service/command-dispatch checks are the enforcement boundary. Workers may retain Git-level safety checks, but they cannot infer a Source link's `IsReference` state from the checkout alone and must never be treated as the source of truth for this Workspace policy.

**Race safety:** share the existing Workspace-scoped coordination boundary between operation launch, queued/running execution and mode toggles. Revalidate eligibility at the service boundary before dispatch, and preserve the operation's captured repository set while the mode is locked; no mid-run retargeting. Return structured skip/refusal reasons consistently across UI, logs, API and orchestrators. Preserve independent work on eligible repositories when others are skipped.

**Refactor and verification requirement:** inventory every relevant existing `IsOnTag` / `CheckedOutTag` condition, classify tag-only presentation/state logic separately from operation-eligibility logic, then migrate the latter to the central policy without altering unrelated Git behavior. Add a policy decision matrix and end-to-end regression coverage for Workspace versus Source, individual versus bulk actions, Workspace versus Feature contexts, tag versus branch versus detached, reference versus writable, concurrent toggles, REST and notification triggers, SQL counter parity, and Worker dispatch. Execute the relevant integration tests—including longer-running suites—before calling this complete.

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
- **Edit Workspace:** tri-state none/mixed/all visual based on existing Source flags (no count), explicit batch actions, and the saved default for future Source additions. Changing the default only is distinguishable from updating existing Sources.
- **Branch dialog (`BranchModal`):** the footer Read-only checkbox changes **only the selected Source repository**. It is never forced on or disabled merely because the Workspace default is read-only; no Workspace-wide toggle is here.
- Turning the switch on runs the live outgoing-commit **and ahead-of-default** checks first. Dirty-file warnings do not prevent the toggle; unpushed commits, a missing upstream, a non-default branch ahead of default (pushed or not), or unreadable Git state do. Report which repository/context and branch blocked activation; this is a refusal, not a separate warning about an otherwise allowed pushed branch.
- Disable the Read-only switch while any relevant orchestration is queued or running for that Workspace; enforce the same lock in the service/API path, not only in Razor. Other Workspaces remain independent.
- Workspaces page: may show the derived none/mixed/all read-only summary; do not imply that a mixed Workspace is wholly read-only.
- Search filter `ro:yes` / `ro:no` in `FilterSearchExpression` may be introduced in Phase 2; do not add a read-only numeric counter.
- Setting or clearing the flag writes the definition to disk (`SyncDefinitionToDiskAsync`), like a membership change.

## Not yet decided

The following genuinely remain to be decided; the Feature snapshot, local staging/Undo, dirty-file warnings, missing-package graph inclusion, in-flight toggling and ahead-of-default activation questions were settled in discussion.

1. **Default-only change UX.** The design now settles that the Workspace default and the actual per-repository state are separate and stored in the manifest. The exact Edit Workspace wording for changing only the future default versus applying a batch action can be finalized during implementation; no action may silently overwrite existing individual settings.
2. **No Workspace repository.** The database-level reference flag can work without one, but there's no `graymoon.json` in which to record an exact reproducible selected commit. Proposed: permit reference mode with a clear limitation; restore-by-definition requires a Workspace repository.
3. **Diagnostics depth.** Decide whether the first implementation must explicitly say "required package is missing; its producer is read-only" or whether existing precise missing-package diagnostics are sufficient. In either case a missing package must be surfaced as an error, never success.

### Resolved in the 2026-11-11 review

- **Manual branches/tags:** allowed only as per-repository user actions when Git can preserve local changes. Workspace-wide branch switching skips reference Sources.
- **Dependency graph:** all reference nodes and version relationships remain; only writes/publishing are skipped. Configuration mistakes may cause real missing-package failures.
- **Feature worktrees:** fixed detached snapshots from Feature creation, with source branch/tag and commit visible; no refresh action.
- **Local changes:** selected stage/unstage and explicit selected Undo allowed, but no reference commits, automatic staging, mass discard, push or PR.
- **Activation:** staged/unstaged edits warn and allow; unpushed commits, a missing upstream, or **any non-default branch ahead of the actual default branch** block, including already-pushed commits. Check the Workspace checkout and existing Feature worktrees. No extra pushed-but-unmerged warning is needed.
- **Running orchestrators:** lock the mode switch Workspace-wide during relevant queued/running work; release after the run fully finishes or stops. Serialize launch/toggle to prevent races.
- **Workspace settings and manifest Restore:** derived none/mixed/all Workspace status; individual Branch-dialog toggle; independent default for new Sources; version-2 manifest persists both true and false per-Source flags, and Restore imports them before Sync.

## Smaller points

- **GrayMoon's own files in a repository.** Git hooks (`.git/hooks`, not tracked) stay installed; the state updates depend on them. Version-file and `.csproj` updates write tracked files: skipped. Package restore writes only ignored files: allowed.
- **Hard protection at git level.** GrayMoon can only restrict its own operations; `git push` from a terminal still works. Setting `remote.origin.pushurl` to an invalid value would stop that, but it changes repository config and must be undone when the flag is cleared. Not proposed for the first version.
- **Open PR when the flag is turned on.** A PR whose branch is fully pushed does not block. It is no longer shown or acted on.
- **Existing Features when the flag is turned on.** A pushed Feature branch stays as it is; nothing pushes it again. Repair Feature does not convert it to a detached worktree.
- **CI, PR and Actions pages.** They skip read-only repositories, which also saves GitHub API calls on every refresh.
- **REST and automation.** `WorkspaceOperationsEndpoints` returns the skipped repositories with a reason.
- **Workspace merge and membership replace.** `ReplaceRepositoriesCoreAsync` and `MergeRepositoriesAsync` recreate link rows; the flag must survive both.
- **Clone depth.** Read-only repositories are candidates for a shallow or blobless clone later. Out of scope.

## Implementation verification gate

**Required integration tests must be executed, even if they take substantially longer than unit tests.** Do not skip relevant integration or end-to-end coverage simply to shorten a run, and do not declare the reference-repository implementation complete based on unit tests or compilation alone. Run the applicable integration suites, including tests marked `Long` when they exercise the changed behavior. Capture the commands, environment, and pass/fail results in the implementation handoff. If required tests cannot run in the available environment, identify the blocker and leave validation explicitly incomplete; never report those tests as passed or the feature as fully verified.

## Constraints

- Feature-context scoping: read-only comes from the Workspace and the link, the tag comes from the context the operation runs in. No new write to the shared link from a Feature.
- A skipped repository is reported as skipped, with the reason. It is never a failure and never silent.
- Treat orchestration launch and mode changes as mutually exclusive **per Workspace**, with a shared service-level guard. While relevant orchestration is queued/running, refuse mode changes to the Workspace or any of its Source repositories (even through REST/automation). The switch unlocks after the run fully finishes, fails, or is cancelled and stopped. The run uses its originally captured eligibility and is not retargeted mid-flight. Independent Workspaces are unaffected.
- New database columns have a default; the migration is a new strict step.
- Recording a commit never fails the pull, switch or sync that caused it; a failed write is reported and the next Sync records it.
- When built: update `docs/architecture/01`, `03`, `05`, `06` and tag-pinned guidance in `AGENTS.md` and `CLAUDE.md`. Tests must cover: a reference repo with local edits (warning + manual stage/unstage/Undo); unpushed commits (activation refused); pushed-but-unmerged feature branch ahead of default (activation refused without a separate warning); non-default branch with zero ahead-of-default commits (eligible if otherwise safe); actual default branch with no unpublished commits (eligible); missing or unresolvable default branch (activation refused safely); evaluation of existing Feature worktrees; fixed Feature snapshots and source-label metadata; safe branch-switch conflicts; service-side commit/push rejection; full graph with writable consumer and reference producer; absent registry packages; and mode-toggle/orchestrator concurrency. Also test: a Workspace default of `true` with one explicit per-repository `false` survives serialization and Restore; changing the default alone does not modify existing Source states; toggling one Source does not modify its siblings. No reference repository may be committed or pushed by GrayMoon.
