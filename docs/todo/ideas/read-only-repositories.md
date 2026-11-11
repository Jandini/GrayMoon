# Read-only (reference) repositories

Status: draft, design only. Written 2026-11-11 on branch `readonly-repos`. Nothing is built.

Naming: the UI says "read-only" and shows an `ro` badge. The file, the database and the code say `reference`.

## Decisions (owner, 2026-11-11)

1. **Read-only is read-only.** GrayMoon makes no modification to a read-only repository: no commit, no stage, no branch, no push, no PR.
2. **Nothing goes to origin.** Fetch and pull are the only traffic with origin.
3. **The Workspace flag comes first.** Marking the Workspace read-only makes every Source repository in it read-only. It is the first step and the first way to turn read-only on or off. The per-repository flag is the second step.
4. **The Workspace repository is never read-only.** GrayMoon must be able to update the definition there, and the user must be able to commit and push it.
5. **A repository must be clean to become read-only.** Unpushed commits or uncommitted changes refuse the change, with a message that the repository must be clean. The check is live and covers Feature worktrees.
6. **Git Changes shows, but does not act.** Files changed from outside GrayMoon are visible on the Changes page. They cannot be staged or committed. The user has two ways out: undo the changes, or switch the repository off read-only and then commit.
7. **The commit is recorded.** The commit a read-only repository is on is written to `graymoon.json`. A Workspace restored from the definition gets the repository at that commit, still read-only.
8. **Read-only repositories are part of a Feature.**
9. **The JSON property is `reference`.**
10. **Badge and switch.** A small `ro` badge sits next to the branch name or tag on the Repositories page. The mode is changed in the footer of the branch dialog.

## Phases

- **Phase 1 - Workspace flag.** `workspace.reference` in `graymoon.json`, `Workspaces.IsReference` in the database, one switch in the footer of the Workspace branch dialog, recorded commits, restore. Every Source repository is read-only; the Workspace repository is not. All filters are built against the effective value, so phase 2 adds no new filter.
- **Phase 2 - Repository flag.** `repositories[].reference`, `WorkspaceRepositories.IsReference`, the per-repository switch, mixed Workspaces. The dependency-graph question (see "Not yet decided", item 2) only exists in a mixed Workspace, so it belongs to this phase.

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
| Manual switch of branch or tag (per repository) | Yes | Yes (assumed, see "Not yet decided", item 1) |
| Workspace-wide operations | Excluded | Excluded |
| Stage, commit | Possible | Never |
| Stored in `graymoon.json` | `tag` + `commit` | `reference` + `branch` + `commit` |

A read-only repository can also be on a tag. Then both apply: no pull (there is no branch), and it stays read-only after the user leaves the tag.

### Allowed

- Sync, Quick Fetch, branch and tag refresh, hook-driven state updates.
- Pull (the incoming side of Sync Commits), per repository and as part of the workspace-wide pull.
- Switch Branch and tag checkout from the repository row.
- Everything that only reads: Open in, the diff viewer, CodeGraph `include`, the dependency graph display.

### Refused or skipped

Workspace-wide operations skip the repository without an error, the same way they skip a tag checkout today. Per-repository write actions on its row are disabled with a tooltip ("Read-only repository").

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
| Commit All, stage, unstage, commit (Git Changes) | skipped | not offered |
| Create / Repair Feature | included, detached, no Feature branch | n/a |

### Turning read-only on

A repository must be clean before it can be marked read-only. Otherwise work would be stranded: GrayMoon would never commit or push it.

- Clean means: no uncommitted changes, no unpushed commits on the current branch, and the branch has an upstream (a local branch that was never pushed counts as unpushed).
- The check is live, through the Worker, when the user confirms. The stored counters can be stale. A repository whose state cannot be read is treated as not clean.
- It covers every context: the Workspace checkout and each Feature worktree of the repository.
- When the check fails nothing is changed. The message names each repository and what is in the way, for example "Billing must be clean: 2 unpushed commits on `feature/x`".
- For the Workspace flag every Source repository must be clean. One repository that is not clean refuses the whole change, and the message lists all of them. The Workspace repository is not checked.
- Turning read-only off needs no check.
- Later states are not prevented. A file changed from outside is shown on the Changes page. A commit made with git outside GrayMoon is shown as outgoing and never pushed.

### Pull

- Pull is fast-forward only. A read-only repository should never have local commits; when it has (made outside GrayMoon), the pull is refused for that repository with a message, never merged or rebased.
- A pull that fails because of local changes says so: "Billing has local changes. Undo them, or switch read-only off and commit."
- Outgoing commits on a read-only repository are shown, but never counted in "push recommended", the Push button counter, or the notification panel.
- Incoming commits on a read-only repository do count: the notification panel and the pull counter include them.

### Git Changes

A read-only repository is shown on the Changes page, because files can be changed from outside GrayMoon and the user should see that. GrayMoon does not act on them.

- The repository and its changed files are listed, with the `ro` badge on the repository node. The diff viewer works.
- Stage and unstage are not offered for its files or folders. A "stage all" on a parent node skips them.
- Commit and Commit All skip the repository. When read-only repositories with changes were left out, the commit dialog says how many.
- The AI commit message collects no evidence from read-only repositories.
- A file staged from outside GrayMoon is shown as staged, and stays that way.
- The refusal is in the App service path (`IWorkspaceGitChangesOperations`), not only hidden in the page.
- The two ways out are stated on the repository node: undo the changes, or switch read-only off and commit. Whether "undo" is a Discard button in GrayMoon is item 3 of "Not yet decided".

In a read-only Workspace only the Workspace repository can be staged and committed on the Changes page.

### Features

A read-only repository is part of a Feature. A Feature is a set of worktrees, one per repository, each on the Feature branch. A read-only repository does not get a Feature branch, but its source is in the Feature folder: that is the purpose (AI tools and CodeGraph run in the Feature root).

It is handled like a tag-pinned repository. The Feature gets a detached worktree at the commit the Workspace checkout is on (`BaseCommitSha` from the same `GetHeadCommits` snapshot), with no Feature branch, no PR, no parent branch. Remove Feature and Roll back have no branch to delete for it.

A detached worktree cannot pull, and git refuses to check out, in the Feature worktree, the branch that the Workspace checkout is on. So "pull" for a read-only repository in a Feature would mean "move the detached worktree to `origin/<branch>`". See "Not yet decided", item 4.

In a read-only Workspace a Feature has a Feature branch in the Workspace repository only; every Source is a detached copy. That is still a complete Feature: the definition can be changed, committed and pushed on the Feature branch.

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

- **"May GrayMoon change this repository?"** False when on a tag or read-only. This replaces most `IsOnTag` tests.
- **"Can this repository pull?"** False only when on a tag (no branch). Read-only does not matter.

`IsOnTag` stays for what is really about the tag: the tag badge, `HasNewerTag`, tag pins, detached-HEAD handling in the state writer.

Read-only is per Workspace and the tag is per context, so the first question takes both: the link and the Workspace for read-only, the context state for the tag. The Feature-context scoping rules apply unchanged: read the tag from the context the operation runs in. The Workspace-role link always answers "not read-only".

### Enforce in the service path, not only in the page

The existing rule says "filter at the call site, and the service path leaves tags untouched". For read-only the service-side check matters more, because the REST endpoints (`WorkspaceOperationsEndpoints`) and the notification panel start the same operations without the Repositories page. A repository id that still reaches push, commit, stage, branch create or update is skipped there and reported as skipped, not as a failure.

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
- Turning the switch on runs the clean check first; a refusal is shown in the dialog and the switch stays off.
- Workspaces page: shows that a Workspace is read-only (the same `ro` badge).
- Search filter `ro:yes` / `ro:no` in `FilterSearchExpression`, and a header counter "Read-only (n)" next to "Tagged (n)". Both matter in phase 2 only.
- Setting or clearing the flag writes the definition to disk (`SyncDefinitionToDiskAsync`), like a membership change.

## Not yet decided

1. **Manual switch of branch or tag.** The first request allows the user to change branch or tag per repository; a later one lists "change branches" among what GrayMoon cannot do. The note assumes: per-repository Switch Branch and tag checkout stay allowed (they are needed to choose what is on disk, and they update the recorded commit), every workspace-wide branch change is excluded.
2. **Dependency graph (phase 2).** A read-only repository can produce packages that writable repositories consume. Its local GitVersion is a version the user will never publish. If it stays a normal dependency source, consumers show "unmatched dependencies" against a version that does not exist in the registry, Update writes that version into their `.csproj`, and Push Synchronized waits for a package that never arrives. Proposal: a read-only repository keeps its place in the level graph (display and ordering) but is never a version source. How a tag-pinned source is treated today must be checked first; the same question applies there.
3. **Discard on the Changes page.** "Undo the changes" is one of the two ways out. Proposal: offer Discard for a read-only repository, as the only action there, because it puts the repository back to the recorded state and unblocks a pull. The strict reading of "GrayMoon makes no modification" would leave undo to git outside GrayMoon.
4. **Read-only worktree in a Feature: movable or fixed?** Is "move to `origin/<branch>`" wanted as its pull, or is a fixed snapshot for the life of the Feature better (reproducible)?
5. **"Commits ahead" in the clean check.** The note checks commits ahead of the upstream (unpushed). If "ahead of the parent" also means a pushed branch that is ahead of the default branch, the check must refuse that too; then a read-only repository can only be on a branch with no work of its own.
6. **Workspace flag and the repository flags (phase 2).** When the Workspace flag is cleared, do repositories keep their own flag (proposed: yes, the Workspace flag never writes the repository flags)?
7. **Recorded commit without a Workspace repository.** Nothing is recorded there (proposed). The alternative is to require a Workspace repository before read-only can be turned on.

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
- The flag is captured when the operation is queued, with the repository set. A change of the flag during a run does not retarget it.
- New database columns have a default; the migration is a new strict step.
- Recording a commit never fails the pull, switch or sync that caused it; a failed write is reported and the next Sync records it.
- When built: update `docs/architecture/01`, `03`, `05`, `06` (the tag-pinned section becomes "repositories GrayMoon does not change"), the tag-pinned rule in `AGENTS.md` and `CLAUDE.md`, and add tests that seed a read-only repository with outgoing commits, local changes and unmatched dependencies and assert that each operation leaves it unchanged.
