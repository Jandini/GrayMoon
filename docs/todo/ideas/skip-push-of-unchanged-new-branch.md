# Do not push a new branch that has no commits, unless another repository depends on it

Status: idea only, written 2026-11-21. **A full analysis of the code is required before any design.** Nothing below was verified against the push code beyond the two pointers named under "Where to start reading".

Related: [implicit-fallback-actions](../defects/implicit-fallback-actions.md) (GrayMoon publishing something the user did not ask to publish).

## Problem

When a new branch is created, or a Feature creates worktrees, every repository gets the new branch. Most of them never receive a commit. Today a bulk push still pushes those repositories: the push sets the upstream, so an empty branch appears on the remote for every repository of the Workspace. The remote fills with branches that carry no work, and each one may start CI for nothing.

## Idea

A repository on a new branch with nothing to push (no unpushed commits, branch never pushed) is left out of:

- the orchestrators (Push, Update and Push, Prepare Workspace with push), and
- the header **Push** button and other bulk push actions.

It is still pushed when **another repository in the Workspace depends on it**. Reason: a repository that others consume as a package must exist on the remote under the new branch, otherwise its package with the new branch version (GitVersion) is never built and the repositories that depend on it cannot get that version. So such a repository is pushed under the new branch even when it has no commits of its own.

Decide "is depended on" from the Workspace dependency graph (does any other repository have an edge to this one), not from whether the repository looks like a package.

The user can always push one repository on purpose: a click on that repository's own badge pushes it, commits or not.

## Possibly simpler variant

In a .NET Dependency Workspace, a repository that no other repository depends on is not pushed (no upstream is set) while it has no commits. Everything else stays as it is. This may cover the whole need without a new rule for orchestrators.

## Questions the analysis has to answer

- Where does each bulk push path choose its repositories, and is there one place to apply the rule? Do not add the check in several places.
- What counts as "depended on": csproj package references only, or also file-config token edges and user-declared custom dependencies? What about the Workspace-role repository, which is always the top level?
- Basic Workspaces have no dependency graph. Does the rule apply there (never push an empty new branch in bulk) or not at all?
- A dependency chain: A has commits, B depends on A but has no commits of its own. After Update, B gets a dependency-update commit, so it has commits. Confirm no case is left where a consumer needs a new version and is skipped.
- What later needs the remote branch to exist: New Pull Request, the PR badge, CI status, "no upstream" badge, Return to Default with remote delete, Remove Feature's remote branch handling. Each must cope with a branch that was never pushed.
- Synchronized push waits for package availability per level. A skipped repository must not be waited for.
- How is a skipped repository reported: in `Skipped` with a reason, like tag-pinned and read only repositories (`RepositoryOperationPolicy`)? Should the rule live in that policy?
- Does "no commits" mean zero outgoing against the divergence base / default branch (`GetNoUpstreamCompareRefAsync`), and is that count always known when the push plan is built?
- What should happen to a branch that already has an upstream but nothing to push? (Probably nothing changes: there is nothing to push anyway.)

## Where to start reading

- `PushRepositoryCommand` (Worker): pushes with `-u` when the branch has no upstream, "so the branch is upstreamed even when there are no commits to push" (`IGitService.PushAsync`).
- `WorkspaceProjectRepository.GetPushPlanPayloadAsync` / `GetPushableRepositoryIdsAsync` (App): the push plan and the policy partition for `RepositoryOperation.Push`.
- Then `WorkspacePushService`, `PushOrchestrator`, `UpdateAndPushOrchestrator`, `PrepareWorkspaceOrchestrator`, and the single-repository badge push.
