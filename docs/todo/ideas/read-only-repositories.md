# Read-only repositories

Status: draft, design only. Written 2026-11-11 on branch `readonly-repos`. Nothing is built.

## Decisions (owner, 2026-11-11)

1. **Read-only is read-only.** GrayMoon makes no modification to a read-only repository: no commit, no stage, no branch, no push, no PR. This includes the Workspace repository of a read-only Workspace.
2. **Read-only repositories are part of a Feature.** They are not left out.
3. **The Workspace flag comes first.** Marking the Workspace read-only makes every repository in it read-only. This is the first step and the first way to turn read-only on or off. The per-repository flag is the second step.
4. **Git Changes shows, but does not act.** Files changed from outside GrayMoon are visible on the Changes page. They cannot be staged or committed, and Commit All skips those repositories.

## Phases

- **Phase 1 - Workspace flag.** `workspace.readOnly` in `graymoon.json`, `Workspaces.IsReadOnly` in the database, one switch on the Workspaces page. Every repository of the Workspace, including the Workspace repository, is read-only. All filters are built against the effective value, so phase 2 adds no new filter.
- **Phase 2 - Repository flag.** `repositories[].readOnly`, `WorkspaceRepositories.IsReadOnly`, the per-repository switch, mixed Workspaces (some repositories read-only, some not). The dependency-graph question ("What the request did not cover", item 2) only exists in a mixed Workspace, so it belongs to this phase.

## Problem

GrayMoon treats every Source repository as something the user develops in. Some repositories are in a Workspace only so their source is on disk next to the others: to read, to search, to give an AI tool or CodeGraph the full picture. Today these repositories are picked up by every workspace-wide operation: Create Branch makes a branch in them, Prepare Workspace switches and pushes them, Update Dependencies commits to them, Push pushes them.

The only protection today is being checked out on a tag (`IsOnTag` / `CheckedOutTag`). That is not the same thing: a tag checkout is frozen at one commit, cannot pull, and the protection disappears the moment the user switches to a branch.

## Idea

A Source repository can be marked **read-only** in a Workspace. A read-only repository:

- is cloned, fetched and synced like any other repository,
- can pull changes from origin on its current branch,
- can be switched to another branch or tag by the user, one repository at a time,
- is never changed by a workspace-wide operation (an orchestrator): no branch is created, switched, committed, pushed or merged there.

The flag is carried in `graymoon.json` next to the repository, so everyone who restores the Workspace gets the same read-only set.

A Workspace can be marked read-only as a whole. Then every repository in it is read-only, including repositories added later and the Workspace repository. This is built first (phase 1).

## Behavior

### Compared with a tag checkout

| | On a tag | Read-only |
|---|---|---|
| What it is | State of the checkout (changes with a checkout) | Setting of the repository in the Workspace (stays until switched off) |
| Scope | Per context (Workspace or one Feature) | Per Workspace, the same in every context |
| Pull | No | Yes, on the current branch |
| Manual switch of branch or tag (per repository) | Yes | Yes |
| Workspace-wide operations | Excluded | Excluded |
| Stored in `graymoon.json` | `tag` + `commit` pin, file-owned | `readOnly`, database-owned |

A read-only repository can also be on a tag. Then both apply: no pull (there is no branch), and it stays read-only after the user leaves the tag.

### Allowed

- Sync, Quick Fetch, branch and tag refresh, hook-driven state updates.
- Pull (the incoming side of Sync Commits), per repository and as part of the workspace-wide pull.
- Switch Branch and tag checkout from the repository row. Checking out a tag records the tag pin as today.
- Everything that only reads: Open in, the diff viewer, CodeGraph `include`, AI evidence collection, the dependency graph display.

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
| Return to Default | skipped | open question 3 |
| Update Branch (merge default into the branch) | n/a | disabled |
| Create PR, bulk Merge PRs and their return follow-ups | skipped | disabled |
| Commit All, stage, unstage, commit (Git Changes) | skipped | not offered, see "Git Changes" |
| Create / Repair Feature | included, detached, no Feature branch, see "Features" | n/a |

### Pull details

- Pull is fast-forward only. A read-only repository should never have local commits; when it has (made outside GrayMoon), the pull is refused for that repository with a message, never merged or rebased.
- Outgoing commits on a read-only repository are shown, but never counted in "push recommended", the Push button counter, or the notification panel.
- Incoming commits on a read-only repository do count: the notification panel and the pull counter include them.

### Git Changes

Decided: a read-only repository is shown on the Changes page, because files can be changed from outside GrayMoon and the user should see that. GrayMoon does not act on them.

- The repository and its changed files are listed, with a lock mark on the repository node. The diff viewer works.
- Stage and unstage are not offered for its files or folders. A "stage all" on a parent node skips them.
- Commit and Commit All skip the repository. The commit dialog lists only repositories that can be committed; when read-only repositories with changes were left out, it says how many.
- The AI commit message collects no evidence from read-only repositories.
- A file staged from outside GrayMoon is shown as staged, and stays that way. GrayMoon neither commits nor unstages it.
- The Worker commands (`StageGitChangesCommand`, `UnstageGitChangesCommand`, `CommitGitChangesCommand`) are refused in the App service path (`IWorkspaceGitChangesOperations`), not only hidden in the page.
- Discard is a modification too. It is open question 2.

In a read-only Workspace (phase 1) this means the Changes page is a viewer: no stage, no commit, no commit box.

### Features

Decided: a read-only repository is part of a Feature. A Feature is a set of worktrees, one per repository, each on the Feature branch. A read-only repository must not get a Feature branch, but its source is in the Feature folder: that is the whole purpose (AI tools and CodeGraph run in the Feature root).

Handle it like a tag-pinned repository. The Feature gets a detached worktree at the commit the Workspace checkout is on (`BaseCommitSha` from the same `GetHeadCommits` snapshot), with no Feature branch, no PR, no parent branch. Remove Feature and Roll back have no branch to delete for it.

Consequence: a detached worktree cannot pull. Git also refuses to check out, in the Feature worktree, the branch that the Workspace checkout is on. So "pull" for a read-only repository in a Feature means "move the detached worktree to `origin/<branch>`" (`git fetch`, then `git checkout --detach origin/<branch>`). This is open question 1.

In a read-only Workspace every repository is read-only, so a Feature there is a second, detached copy of the sources with no Feature branch anywhere. It is still useful as a separate folder for an AI tool, but "Create Feature" could be offered under another label there, or not at all. This is open question 5.

### The Workspace as a whole

Decided, and built first. `workspace.readOnly: true` makes every repository read-only, the Workspace repository included:

- effective read-only = Workspace flag OR repository flag,
- a repository added later is read-only without any further step,
- the per-repository switch is shown checked and disabled, with the reason,
- workspace-wide write actions (Create Branch, Prepare Workspace, Update, Push, New Pull Request, Commit All) are hidden or disabled for the whole Workspace instead of running over an empty set,
- what is left is Sync, Pull, per-repository Switch Branch and tag checkout, and everything that reads.

The Workspace repository (Workspace role) is covered too, with one exception. See the next section.

### The Workspace repository

Decided: in a read-only Workspace the Workspace repository is read-only like the others. GrayMoon creates no branch, commit, push or PR there, and the Changes page does not stage or commit its files.

One exception cannot be avoided: the definition files. GrayMoon itself writes `graymoon.json` (membership, profile, tag pins, version files), the managed `.gitignore` section and `codegraph.json` into the Workspace repository, and setting the read-only flag is itself a write to `graymoon.json`. Without that write the flag could not be carried in the file.

- GrayMoon keeps writing these three files in a read-only Workspace. They are never committed by GrayMoon (true today as well). They show up on the Changes page as changes that cannot be staged there; the owner commits them with git outside GrayMoon.
- A user who only consumes a read-only Workspace normally causes no such write: Restore does not rewrite a definition that already matches, and nothing else changes membership or profile. The one case is a tag checkout, which records a tag pin in the file. Confirming this exception is open question 6.
- Phase 2: the Workspace repository never carries the per-repository flag (it is not listed in `repositories[]`). It is read-only only through the Workspace flag.

## `graymoon.json`

```json
{
  "version": 1,
  "workspace": {
    "profile": { "type": "dotNetDependency", "versioning": "gitVersion", "ci": "githubActions" },
    "readOnly": true
  },
  "repositories": [
    { "name": "Billing", "repositoryUrl": "https://github.com/acme/Billing.git", "connectorUrl": "https://github.com" },
    { "name": "Platform", "repositoryUrl": "https://github.com/acme/Platform.git", "connectorUrl": "https://github.com", "readOnly": true }
  ]
}
```

- `repositories[].readOnly` and `workspace.readOnly`: optional bool, written only when true (`JsonIgnoreCondition.WhenWritingDefault`), so existing files do not change.
- Schema `version` stays 1. An older GrayMoon ignores the unknown property and treats the repository as writable. It also drops the property when it rewrites the file. This is acceptable for an optional property, but worth a line in the release notes.
- Use the lenient bool converter (`LenientBooleanJsonConverter`, today used for `codegraph`), and make its error message name the property.

### Ownership: database-owned, not file-owned

Tag pins are file-owned: there is no database copy, so they only work when the Workspace has a Workspace repository. The read-only flag should be database-owned:

- it must work for a Workspace that has no Workspace repository,
- every orchestrator filter runs against the database, and several run inside SQL (`WorkspaceRepositoryLinkListQueryService` counters); reading a file through the Worker there is not possible,
- it is shareable definition data like membership, not local checkout state.

So:

- New column `WorkspaceRepositories.IsReadOnly` (INTEGER NOT NULL DEFAULT 0) and `Workspaces.IsReadOnly` (same). One new strict migration step, idempotent.
- `BuildFromDatabaseAsync` writes both flags. `WriteAuthoritativeManifestAsync` needs no copy step for them.
- Restore reads both flags into the database.
- Drift detection compares them. `WorkspaceManifestDrift` gets a list of repositories whose flag differs (and the Workspace flag joins `ChangedProfileFields` or gets its own entry). The drift banner then offers the existing two directions: write the database to disk, or take the file.
- `IsDefinitionRewriteRequiredAsync` compares serialized manifests, so it picks the flag up without a change, once Restore has imported it.

## Design

### One predicate, not sixty edits

`IsOnTag` / `CheckedOutTag` is tested in about 280 places in 87 files, many of them the same sentence written again (`!wr.IsOnTag`, `string.IsNullOrWhiteSpace(link.CheckedOutTag)`, and SQL forms of it). Adding `|| IsReadOnly` next to each one would repeat the pattern that the review rules warn about.

Proposal: introduce two named questions and move the call sites to them.

- **"May a workspace-wide operation change this repository?"** False when on a tag or read-only. This replaces most `IsOnTag` tests.
- **"Can this repository pull?"** False only when on a tag (no branch). Read-only does not matter.

`IsOnTag` stays for what is really about the tag: the tag badge, `HasNewerTag`, tag pins, detached-HEAD handling in the state writer.

Read-only is per Workspace and the tag is per context, so the first question takes both: the link (or Workspace) for read-only, the context state for the tag. The Feature-context scoping rules apply unchanged: read the tag from the context the operation runs in.

### Enforce in the service path, not only in the page

The existing rule says "filter at the call site, and the service path leaves tags untouched". For read-only the service-side check matters more, because the REST endpoints (`WorkspaceOperationsEndpoints`) and the notification panel start the same operations without the Repositories page. A repository id that still reaches push, commit, branch create or update is skipped there and reported as skipped, not as a failure.

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
- **Manifest**: `WorkspaceManifest` records, `WorkspaceManifestFile` (parse, serialize), `WorkspaceManifestService` (build, drift), `RestoreDefinitionEvaluator` and `WorkspaceRepositoryOperations` (restore import).
- **Row and DTO**: `WorkspaceRepositoryLink`, `WorkspaceRepositoryLinkListItemDto` and its mapper (including `WithBranchOverride`), `WorkspaceRepositoriesRow.razor`, `CommitsBadge`, `DivergenceBadge`.
- **Git Changes**: `IWorkspaceGitChangesOperations` commit / stage / unstage, Commit All, the AI commit message scope.

### UI

- Repositories grid: a lock icon next to the name, and a "Read-only" entry in the row menu to switch it. Write badges (push, update, PR) render as inactive, like on a tag.
- Search filter: `readonly:yes` / `readonly:no` in `FilterSearchExpression`.
- Workspace repositories modal (membership): a read-only checkbox per repository, so a repository can be added as read-only from the start and is never touched in between.
- Workspaces page (profile): a "Read-only Workspace" switch.
- Header counter "Read-only (n)" next to "Tagged (n)".
- Setting or clearing the flag writes the definition to disk (`SyncDefinitionToDiskAsync`), like a membership change.

## What the request did not cover

1. **Features.** Whether a read-only repository is in a Feature at all, and in what form. See "Features" above and open question 2.
2. **Dependency graph.** A read-only repository can produce packages that writable repositories consume. Its local GitVersion is a version the user will never publish. If it stays a normal dependency source, consumers show "unmatched dependencies" against a version that does not exist in the registry, Update writes that version into their `.csproj`, and Push Synchronized waits for a package that never arrives. Proposal: a read-only repository keeps its place in the level graph (display and ordering) but is never a version source: edges pointing at it are not counted as unmatched, are not updated, and are not waited for. The same applies to version-file tokens that name a read-only repository. This needs a check of how a tag-pinned source is treated today, because the same question applies there.
3. **Local changes in a read-only repository.** The files are not write-protected, and a build or an editor can change them. Decided: they are visible on the Changes page and cannot be staged or committed. A pull can then fail on a changed file; the message must say that the repository has local changes, since the user cannot fix it by committing in GrayMoon.
4. **GrayMoon's own writes into a repository.** Git hooks are installed in every repository (`.git/hooks`, not tracked): keep them, the state updates depend on them. Version-file update and `.csproj` update write tracked files: skip. Package restore writes only ignored files: allow, since the code must build to be read with tooling.
5. **Hard protection at git level.** GrayMoon can only restrict its own operations. A `git push` from a terminal still works. Optional extra: set `remote.origin.pushurl` to an invalid value in a read-only repository so that any push fails. This changes repository config, must be undone when the flag is cleared, and is easy to forget. Not proposed for the first version.
6. **Switching the flag on a repository that is not clean.** Marking a repository read-only while it has outgoing commits, uncommitted changes or an open PR. Proposal: allow it and show a warning that lists what is left behind; do not clean up.
7. **Switching the flag while Features exist.** An existing Feature has a Feature branch in that repository. Proposal: the flag applies at once to all operations (the Feature branch stays, nothing pushes it); Repair Feature does not convert it. Converting is a separate, explicit step, or not offered.
8. **Clone depth.** Read-only repositories are candidates for a shallow or blobless clone to save disk space and time. Out of scope, but the flag makes it possible later.
9. **CI, PR and Actions pages.** A read-only repository has no PR of the user and its workflow runs are not the user's. Proposal: the Actions page and the PR column skip read-only repositories, which also saves GitHub API calls on every refresh.
10. **REST and automation.** `WorkspaceOperationsEndpoints` must return the skipped repositories with a reason, so an automation does not read a skip as success for that repository.
11. **Workspace merge and membership replace.** `ReplaceRepositoriesCoreAsync` and `MergeRepositoriesAsync` recreate link rows; the flag must survive both.
12. **Old GrayMoon with a new file.** See "`graymoon.json`": the flag is lost on a rewrite by an older version.

## Open questions

Closed by the decisions above: the Workspace repository in a read-only Workspace (read-only), whether a read-only repository is in a Feature (yes), stage and commit on the Changes page (no), and whether the Workspace flag is in the first version (it is the first version).

1. **Read-only worktree in a Feature: movable or fixed?** It is a detached worktree. Is "move to `origin/<branch>`" wanted as its pull, or is a fixed snapshot for the life of the Feature better (reproducible)?
2. **Discard on the Changes page.** Discard changes the working tree, so by "GrayMoon makes no modification" it is not offered (proposed). Without it the only way to clear a file changed by accident is git outside GrayMoon, and a pull stays blocked until then.
3. **Manual switch of branch or tag.** The first request allows the user to change branch or tag per repository; the later wording lists "change branches" among what GrayMoon cannot do. The note assumes: per-repository Switch Branch and tag checkout stay allowed, every workspace-wide branch change is excluded. Per-repository Return to Default is then a manual switch as well; it only differs by the cleanup it offers, which would not be offered.
4. **Workspace flag and the repository flags (phase 2).** When the Workspace flag is cleared, do repositories keep their own flag (proposed: yes, the Workspace flag never writes the repository flags), or does clearing it make everything writable?
5. **Features in a read-only Workspace.** Offer Create Feature there (a detached copy of all sources, no branch), or hide it?
6. **Definition files in the Workspace repository.** GrayMoon writes `graymoon.json`, the managed `.gitignore` section and `codegraph.json` even when the Workspace is read-only, and never commits them (proposed, see "The Workspace repository"). The alternative is to refuse every definition change while the flag is on, except the flag itself.
7. **Naming.** "Read-only" suggests that files cannot change. "Reference repository" says what it is for. The JSON property name is fixed once shipped.

## Constraints

- Feature-context scoping: read-only comes from the Workspace link, the tag comes from the context the operation runs in. No new write to the shared link from a Feature.
- A skipped repository is reported as skipped, with the reason. It is never a failure and never silent.
- The flag is captured when the operation is queued, with the repository set. A change of the flag during a run does not retarget it.
- New database columns have a default; the migration is a new strict step.
- GrayMoon stays CI-agnostic in the wait logic; read-only only removes repositories from the set.
- When built: update `docs/architecture/01`, `03`, `05`, `06` (the tag-pinned section becomes "repositories excluded from workspace-wide operations"), the tag-pinned rule in `AGENTS.md` and `CLAUDE.md`, and add tests that seed a read-only repository with outgoing commits and unmatched dependencies and assert that each operation leaves it unchanged.
