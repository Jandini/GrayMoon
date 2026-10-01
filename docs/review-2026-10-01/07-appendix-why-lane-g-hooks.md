# Appendix to the plan: why lane G (Git hooks) is in worktrees v1

Companion to `06-worktree-release-implementation-plan.md`, units G1 to G4. Code references are on branch `opus-review` (`a06fe33`); locate them by symbol, since line numbers drift.

## 1. What GrayMoon's hooks do today

GrayMoon keeps the repository grid current when the developer works **outside** GrayMoon: committing in the IDE, checking out a branch in a terminal, pulling, or pushing. It does this with Git hooks.

- **Who writes them:** the Worker, in `GitService.WriteSyncHooksAsync` (`GrayMoon.Agent/Services/GitService.cs`).
- **When:** on **every Sync** of a repository (`SyncRepositoryCommand`, the call guarded by `if (version != "-" && ...)`), and when a Feature worktree is created (`CreateGitWorktreeCommand`).
- **Where:** `<git-common-dir>/hooks`, resolved by `ResolveGitHooksDirectoryAsync` with `git rev-parse --git-common-dir`. That folder is shared by the primary checkout and every linked worktree, which is why one set of hooks serves the Workspace and all its Features.
- **Which hooks:** `post-commit`, `post-checkout` (branch checkouts only), `post-merge`, `post-update`, `pre-push`.
- **What each does:** a POSIX shell script that resolves the current worktree root (`git rev-parse --show-toplevel`) and runs `curl` to the Worker's local listener at `http://127.0.0.1:<ListenPort>/hook/{commit|checkout|merge|push}` with `repositoryId`, `workspaceId` and `repositoryPath`. It always ends in `|| true`, so the hook never blocks Git.
- **What happens next:** `HookListenerHostedService` (Worker) queues a `NotifySyncJob`. The Worker re-reads branch, version and ahead/behind state and pushes the result to the App over the hub (`AgentHub`, hook result method). The App uses `WorkspaceHookContextAttributor` to decide, from `repositoryPath`, whether the event belongs to the Workspace or to a Feature.

Every script starts with the line `# Created by GrayMoon.Agent at <UTC timestamp>Z`. The same marker was used in 0.1.0 (`git show 0.1.0:src/GrayMoon.Agent/Services/GitService.cs`), so GrayMoon-written hooks can be recognised on every installed version.

## 2. What is wrong with that

Evidence: `05-general-code-and-ux-review.md` A1 and R4, confirmed in code.

| # | Problem | Evidence | Who is hurt |
|---|---|---|---|
| H-1 | **User hooks are silently destroyed.** `WriteHookFile` is a plain `File.WriteAllText`. There is no check for an existing file, no backup and no chaining. A team's `pre-push` (tests, lint, secret scanning) or `post-checkout` (tooling setup) is replaced by a `curl` call on the first Sync, and nothing tells the user. | `GitService.WriteHookFile`, `WriteSyncHooksAsync` | Any repository that has its own hooks. This is the most serious problem: GrayMoon removes a safety net the team relies on, such as a secret scanner in `pre-push`. |
| H-2 | **`core.hooksPath` is ignored.** Repositories using husky, lefthook or a shared hooks folder set `core.hooksPath`. Git then runs hooks from that folder and never from `.git/hooks`, but GrayMoon still writes to `<common-dir>/hooks`. | `ResolveGitHooksDirectoryAsync` uses `--git-common-dir`, not `--git-path hooks` | Those repositories get **no** live updates at all, and nothing tells the user. The grid goes stale until the next Sync. |
| H-3 | **Silent failure is worse with Features.** Before Features, a stale grid was annoying. With Features, hooks also decide **which context** an outside commit belongs to. In a repository where hooks don't run, the Workspace and every Feature go stale together, and a commit made in a Feature worktree shows up only after a manual Sync of that Feature. | `WorkspaceHookContextAttributor`; the comment in `SyncRepositoryCommand` about stale hooks misattributing Feature events | Feature users, who now work in several checkouts of the same repository at once. |
| H-4 | **They are rewritten on every Sync.** The timestamp in the marker changes each time, so the files change on every Sync even when nothing else did. | `WriteSyncHooksAsync` | Minor. It makes H-1 unavoidable: even a user who restores their hook loses it again on the next Sync. |
| H-5 | **`post-update` does nothing here.** Git runs `post-update` on the **receiving** side of a push (normally a bare server repository), so it never fires in a developer's checkout. | Git documentation for `githooks` | Harmless noise, and one more user hook slot overwritten for no benefit. |

Related, not changed by G1: GrayMoon's own Git operations that pass `skipHooks: true` run with `-c core.hooksPath=<empty temp folder>` (`GetHooksConfigPrefix`). Commits and pulls made **by** GrayMoon (for example dependency-update commits in `DependencyUpdateOrchestrator`) therefore skip the team's `pre-commit` hooks too. That is a deliberate existing choice. It is listed in Part F of the plan for the owner to review, not changed in v1.

## 3. Why fix it in the worktrees release, not later

1. **Features make hooks part of the core worktree flow.** Hooks are the only signal that tells GrayMoon about changes made in a Feature worktree outside the app. A Feature release that silently stops updating in some repositories, with no explanation, will be reported as "Features are buggy".
2. **Features install hooks in more places, more often.** Creating a Feature writes hooks into the shared common directory of every repository in the Workspace. A user who only ran Sync occasionally before now triggers the overwrite on every Feature create.
3. **It is destructive.** H-1 deletes files the user owns. The release policy for this plan is "must not break things that already work", and a team's hook is something that already works.
4. **The minimal fix is small and low-risk** (section 4). Full chaining can wait.

## 4. What G1 does (default for DEC-3: detect and warn)

1. Resolve the hooks folder Git actually uses: `git rev-parse --git-path hooks`, which honours `core.hooksPath`.
2. Write a hook file only when it is missing **or** its first line after the shebang starts with the GrayMoon marker (`# Created by GrayMoon.Agent`). Never touch any other file.
3. If `core.hooksPath` points outside the repository's Git directory (husky, lefthook, a shared folder), write nothing there and report `HooksPathCustom`.
4. If a hook exists that GrayMoon did not write, leave it byte-for-byte and report `ExistingHook` with its name.
5. Stop changing the marker timestamp on every Sync: rewrite the file only when the content other than the marker line differs.
6. Stop writing `post-update`. Leave an existing GrayMoon-marked `post-update` alone (deleting it is not needed).
7. The App shows a small warning on affected repositories: "GrayMoon cannot install its Git hooks here, so changes made outside GrayMoon appear after the next Sync." The tooltip names the hook or the `core.hooksPath` value.

**Effect on existing installs:** repositories whose hooks GrayMoon already overwrote now hold GrayMoon-marked files, so they keep working exactly as before (see risk R-G1 in `08-plan-regression-risk-review.md`). Only repositories GrayMoon has **not** touched yet, and that have their own hooks, behave differently: their hooks are kept and they get the warning instead of live updates.

## 5. Later (v1.1): chaining

DEC-3 = "chain" would add: for an existing user hook, rename it to `<hook>.graymoon-user`, then write a GrayMoon hook that first runs the user hook and passes through its exit code (important for `pre-push`, which can block a push), then calls `curl`. For `core.hooksPath` repositories, offer an opt-in button that adds a marked block to the user's hook file. Chaining needs careful tests on Windows Git Bash, husky v8 and v9, and lefthook. That is why it is not the v1 default.

## 6. Unhooking repositories that leave a Workspace (units G2, G3, G4)

### 6.1 The problem

Hooks are installed but never removed. Nothing in GrayMoon deletes them:

| Path that removes a repository from GrayMoon | Code | Talks to the Worker? |
|---|---|---|
| Edit Workspace, untick one or more repositories | `WorkspaceRepository.ReplaceRepositoriesAsync` (deletes the `WorkspaceRepositoryLink` rows) | No |
| Delete Workspace | `Workspaces.razor` `DeleteWorkspaceAsync` calls `WorkspaceRepository.DeleteAsync` directly (it also bypasses the `IWorkspace*Operations` facade rule) | No |
| Connector refresh drops repositories the provider no longer returns | `RepositoryRepository` merge (`toDeleteIds`, then `WorkspaceRepositoryLinkCleanup.DeleteDependentsAsync`) | No |
| Delete Connector (cascades to its repositories in every Workspace, 05 U1) | `ConnectorRepository.DeleteAsync` | No |

The hook files stay in `<repo>/.git/hooks` with the old `workspaceId` and `repositoryId` baked into the payload. Every commit, checkout, merge or push the developer makes in that folder later, for example after keeping the clone for other work, then does all of this:

1. The hook runs `curl` to the Worker (`HookListenerHostedService`), which queues a `NotifySyncJob`.
2. The Worker does real work on a repository GrayMoon no longer manages:
   - `CommitHookSyncCommand` runs GitVersion (up to `GitVersionTimeoutSeconds`, 90 s by default) and `ls-remote`.
   - `CheckoutHookSyncCommand` runs a network fetch and a tag fetch.
   - Both first ask the App for a token through `/repos/{id}/connector`, which fails for a deleted repository and logs a warning.
3. The App receives the result and logs `SyncCommand: workspace {X} repo {Y} not found` (`SyncCommandHandler.HandleAsync`), then drops it.

The result is wasted CPU and network, surprise fetches in a folder the user thinks GrayMoon has let go of, and repeating warnings in both logs. Features make it worse: one hooks folder serves the primary checkout and all its worktrees, so every worktree of that repository pings too.

### 6.2 Design

- **G2: Worker command `UnhookRepository(repositoryPath, workspaceId)`.** It deletes only hook files that carry the GrayMoon marker **and** whose payload names that same `workspaceId`. This means a folder that has since been hooked by another Workspace is never touched. A missing folder, a folder that is not a Git repository, or `core.hooksPath` set is not an error: the result says what happened and the App logs a Warning.
- **G3: call it whenever a repository leaves a Workspace.** That covers removing one or several repositories, deleting a Workspace, and connector refresh or delete. The App computes the repository paths **before** deleting the rows (pure string building with `WorkspaceService.GetWorkspacePath`; the App does not touch the disk). It runs the unhook **after** the database change succeeds, best-effort, and only logs Warnings for failures. The user's action never fails or waits because of unhooking.
- **Re-adding a repository needs no new code.** A newly linked repository starts as `NeedsSync`, and Sync writes the hooks again (`SyncRepositoryCommand` calls `WriteSyncHooksAsync`). That is the natural path.
- **G4: self-healing for anything G3 missed.** If the Worker was offline during the removal, or the hooks were left behind by an older GrayMoon version, the next stale ping reaches `SyncCommandHandler` with a `RepositoryPath`. When the App confirms that the `(workspaceId, repositoryId)` link does not exist, it sends `UnhookRepository` for that path, at most once per path per hour. This also cleans up every stale hook left by removals before this release, which G3 alone cannot do because those paths are no longer in the database.
- **Workspace delete while Features exist is refused** with "Remove the Features first", matching the existing rule in `ReplaceRepositoriesAsync` ("Cannot change Workspace repository membership while Features exist"). Today `WorkspaceRepository.DeleteAsync` has no such guard, so deleting a Workspace that has Features would leave their worktrees, branches and folders on disk with no database rows pointing to them.

### 6.3 Why it belongs in v1, in lane G

- It edits the same hook code as G1 and reuses G1's marker recognition and hooks-folder resolution, so splitting it into a later release would mean reopening that code.
- It is small (one Agent command, three call sites, one self-heal branch) and every path is best-effort, so it cannot block a user action.
- Features multiply the number of checkouts that share a hooks folder, so stale hooks cost more after this release than before.
- It does not block any worktree gate. If time runs short, G4 is the part to defer: G2 and G3 stop new stale hooks, and G4 only cleans up old ones.

## 7. What happens if lane G is skipped

- Teams with their own hooks keep losing them on every Sync and Feature create, without notice.
- Husky and lefthook users get Features that never update live, without explanation.
- Neither shows up in tests or on the owner's machine unless a repository uses hooks, so the first report will come from users.
- Every repository ever removed from a Workspace keeps triggering GitVersion runs, network fetches and log warnings whenever the user works in it.
