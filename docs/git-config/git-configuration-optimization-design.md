# Git configuration subprocess optimization - design and status

Scope: Windows `core.longpaths`, `remote.origin.url`, the conditional `core.hooksPath` read, and the `safe.directory`
guard. Nothing else was migrated. Task brief: `graymoon-git-configuration-optimization-prompt.md`.

## Lifecycle decision

`SyncRepositoryCommand` and `AttachWorkspaceRepositoryCommand` are the Worker-side onboarding points: a workspace
repository is cloned (Sync), restored/attached (Attach) or rediscovered (every later Sync) through them, and both already
call `AddSafeDirectoryAsync` at the same spot. `IRepositoryConfigurationInitializer.EnsureWindowsLongPaths` is called
there. Uncovered routes (a repository first touched by a Feature create/remove, and Features made by older builds) use the
same call as a first-use fallback in `GitWorktreeService`. The initializer remembers successes per path in a bounded set
(1024 entries, cleared on overflow, failures never stored), so repeat Feature operations cost a lookup and no process.
There is no "once ever" guarantee: after a Worker restart a repository is checked once more, which also reconciles a
setting that was unset externally. A setting unset while the Worker runs is not noticed until then.

- Clone: `-c core.longpaths=true` on the `git clone` invocation itself (Windows only, command-scoped), then the local
  setting is persisted after clone by the Sync/Attach seam.
- Attach of a non-empty root: configured right after `git init`, before the default-branch checkout.
- Remove: the setting is ensured before the exclusive removal claim (the write takes its own mutating lease on the main
  repository; never run while a claim covers it). Lock order: access lease, then a striped per-path lock; nothing is
  awaited while holding the stripe lock.

## Verified LibGit2Sharp 0.32.0 behavior

- `Config.Get<bool>(key, ConfigurationLevel.Local)` returns null when absent; `Set(..., Local)` writes the repository config.
- Opened at a **linked worktree path**, libgit2 does not see the shared config (reads return null, writes land outside the
  common config). The initializer therefore resolves the worktree's `commondir` and opens the main git directory.
- `includeIf gitdir:` conditions are not evaluated the way git does (a value reachable only through one is missing).
- `GIT_CONFIG_*` environment overrides are not honoured by libgit2.

## `remote.origin.url` and `core.hooksPath` reads (`LibGit2SharpConfigReader`)

In-process raw effective read; native `git config --get` is kept (same command as before) when libgit2 cannot be shown to
agree: environment-redirected config (`GIT_CONFIG`, `_COUNT`, `_PARAMETERS`, `_GLOBAL`, `_SYSTEM`, `_NOSYSTEM`), linked
worktrees, `config.worktree`, any `includeIf.*` entry, or a repository that cannot be opened. Plain `include`, global and
local precedence, relative values, and `url.*.insteadOf` (raw value returned, not expanded) match native git (tests).
Missing origin stays `null`. `git rev-parse --git-common-dir --git-path hooks` remains the authority for the hooks
location; `GitHooksLocationCache` is unchanged.

## `safe.directory`

`AddSafeDirectoryAsync` writes `git config --global --add safe.directory <path>` (native) only when the probe reports
dubious ownership. Any other failure logs a warning, changes nothing and is not cached, so it is probed again. Repeats
after a successful write are served from the existing per-process cache.

## Files

Added: `IRepositoryConfigurationInitializer.cs`, `RepositoryConfigurationInitializer.cs`, `LibGit2SharpConfigReader.cs`;
tests `RepositoryConfigurationInitializerTests`, `GitConfigReadTests`, `GitServiceSafeDirectoryTests`,
`GitConfigTestSupport`. Changed: `GitWorktreeService` (removed `EnsureLongPathsAsync`), `GitService`,
`GitCliRepositoryReader`, `SyncRepositoryCommand`, `AttachWorkspaceRepositoryCommand`, `RunCommandHandler` (DI), and
existing tests whose git-process counts or "older build" simulation changed.

## Evidence

`dotnet test src/GrayMoon.Worker.Tests`: 667 passed, 0 failed, 1 skipped (pre-existing). Repeat Feature create/remove
issue no `git config` process (`Repeated_Feature_create_and_remove_spawn_no_git_config_process_and_share_the_setting`).
Hook install with an outside `core.hooksPath` is now 1 git process instead of 2.
Not measured: the 27-repository wall-clock comparison against the 2026-10-09 baseline (needs the real workspace).

## Deferred

Wall-clock benchmark on the 27-repo workspace; a first-clone deep-path test against a real remote; reading
`remote.origin.url`/`core.hooksPath` in-process for linked worktrees (needs common-dir plus worktree-gitdir layering).
