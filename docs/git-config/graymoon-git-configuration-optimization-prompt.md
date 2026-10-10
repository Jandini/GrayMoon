# GrayMoon — Git Configuration Subprocess Optimization: Full Implementation Prompt

## Mission

Implement a narrowly scoped, production-ready improvement to GrayMoon's Git configuration access: Windows `core.longpaths`, `remote.origin.url`, and the conditional `core.hooksPath` read, plus a separate correctness guard for `safe.directory`. Replace the repeated `git config` subprocess check/set with **LibGit2Sharp configuration APIs** and move responsibility to an appropriate **repository initialization/management lifecycle**, without weakening Feature create/remove, legacy compatibility, error handling, or concurrent repository access. **Review current `main` before making changes; current code is authoritative.** Complete the implementation and tests, rather than delivering a plan only.

## Verified facts and starting points

- GrayMoon's Worker already references `LibGit2Sharp` **0.32.0** (`src/GrayMoon.Worker/GrayMoon.Worker.csproj`). Do **not** add a dependency or upgrade it merely for this task.
- LibGit2Sharp exposes `Repository.Config.Get<bool>("core.longpaths", ConfigurationLevel.Local)` and `Repository.Config.Set("core.longpaths", true, ConfigurationLevel.Local)`; `Get` returns `null` when absent. The no-level `Get` reads effective layered configuration, whereas an explicit `Local` read inspects the repository-local config. Verify these calls compile against the *actual pinned version* before finalizing. Example only:

  ```csharp
  using var repository = new LibGit2Sharp.Repository(repositoryPath);
  var local = repository.Config.Get<bool>("core.longpaths", ConfigurationLevel.Local);
  if (local?.Value != true)
      repository.Config.Set("core.longpaths", true, ConfigurationLevel.Local);
  ```

- `src/GrayMoon.Worker/Services/GitWorktreeService.cs` currently calls `EnsureLongPathsAsync(mainRepositoryPath, ct)` in `CreateWorktreeAsync` **and** `RemoveWorktreeAsync`. That helper runs `git config --local --get core.longpaths` every time on Windows and runs `git config --local core.longpaths true` when not already true. It catches most errors and logs warnings; cancellation propagates.
- `src/GrayMoon.Worker.Tests/GitServiceLongPathsTests.cs` contains real-Git coverage for Windows-only creation, existing-true behavior, paths exceeding 260 characters, and removal of legacy Features after the setting is unset. Preserve the guarantees of these tests while adjusting their assumptions about *when* setup occurs.
- LibGit2Sharp is already used by `LibGit2SharpGitIgnoreService`, `LibGit2SharpLocalGitSnapshotReader`, and `GitVersionInputFingerprint`. Their repository handles are short-lived and respect GrayMoon's `IRepositoryAccess` removal/lease coordination. **Don't hold repository handles indefinitely.**
- GrayMoon uses `GitProcessRunner` for native Git mutations and has a repository-access coordination system (`RepositoryAccess` / `IRepositoryAccess`) to prevent live operations blocking worktree deletion. Do not evade existing mutating operation synchronization.
- Existing measurements (`docs/worktree/GrayMoon-Create-Feature-Performance-Measurements-2026-10-09.md`) recorded mean `core.longpaths` get/set durations of **1741 ms** on the first Feature and **838 ms** on the second for 27 repos. These are per-repository durations, not total wall clock added to Create.

## Additional recommended changes — include these, and no other Git-config migrations

The following are the **only** additions to the original long-path task. Do not turn this into a generic Git CLI-to-LibGit2Sharp migration.

### A. Replace the `remote.origin.url` configuration subprocess

Current site: `src/GrayMoon.Worker/Services/GitCliRepositoryReader.cs`, `GetRemoteOriginUrlAsync`, which calls `git config --get remote.origin.url`.

- Replace this subprocess with a short-lived LibGit2Sharp repository and a config read such as `repository.Config.Get<string>("remote.origin.url")?.Value`, using the pinned version's supported API.
- Preserve the existing public method contract, missing-origin behavior (`null`), error semantics, cancellation boundaries, and `IRepositoryAccess` lease/handle-disposal discipline.
- Pay close attention to semantic equivalence: this existing command reads the **raw effective configuration value**, so do not silently substitute `repository.Network.Remotes["origin"]?.Url` if it expands `url.*.insteadOf`, differs under includes, or produces a different result. Test `includeIf`, global/local precedence, and a remote with no origin URL. Preserve Git behavior or retain a narrowly justified native fallback for configurations LibGit2Sharp cannot reproduce.
- Cover source repositories and linked Feature worktrees. Prove no `git config --get remote.origin.url` child process is spawned in the normal successful path.

### B. Carefully optimize the conditional `core.hooksPath` config read

Current site: `src/GrayMoon.Worker/Services/GitService.cs`, `ResolveGitHooksLocationAsync`.

- Keep `git rev-parse --git-common-dir --git-path hooks` as the **authoritative effective hooks-location resolver**. Do not replace it with path reconstruction or change GrayMoon's policy for external hooks directories.
- Investigate replacing **only** the conditional `git config --get core.hooksPath` read with `Repository.Config.Get<string>("core.hooksPath")` using a short-lived LibGit2Sharp repository.
- Preserve `GitHooksLocationCache`, its invalidation/fingerprint rules, worktree/common-dir handling, `include`/`includeIf`, environment-based `GIT_CONFIG_*` overrides, relative paths, global/system configuration, and external hooks safety.
- First write parity tests comparing native Git and the candidate LibGit2Sharp read for normal and unusual configurations. **If parity is not demonstrable for a case, retain the native command for that case**, rather than silently changing behavior. Document precisely when a fallback remains.
- Do not alter Git hooks installation, checkout hook suppression, or the cache's native-resolution fallback.

### C. Fix the `safe.directory` correctness guard, but keep native Git configuration

Current site: `src/GrayMoon.Worker/Services/GitService.cs`, `AddSafeDirectoryAsync` / `CheckRepoSafeAsync`.

- `CheckRepoSafeAsync` already returns both `IsSafe` and `IsDubiousOwnership`. Today the caller appears to add `safe.directory` globally for **any** failed `git rev-parse --is-inside-work-tree`, including errors unrelated to dubious ownership.
- Change the guard so GM calls `git config --global --add safe.directory ...` **only for a verified dubious-ownership failure**. For other errors, retain appropriate diagnostic handling and do not expand the user's trusted directories.
- Keep native Git for the `safe.directory` write and for the probe. Do **not** migrate protected configuration to LibGit2Sharp, alter global trust semantics, or automatically add an entire wildcard directory.
- Add tests for (1) a safe repository, (2) dubious ownership, (3) an unrelated Git failure, and (4) repeated calls not producing unnecessary duplicate writes.
- Keep this as an isolated, reviewable correctness correction. Do not redesign safe-directory policy, introduce user-facing dialogs, or generalize repository trust management.

## Required review before implementation

1. Read the current main-branch implementations and call graph of:
   - `GitWorktreeService.CreateWorktreeAsync`, `RemoveWorktreeAsync`, `EnsureLongPathsAsync`;
   - `CreateGitWorktreeCommand`, `RemoveGitWorktreeCommand`, `GitProcessRunner`;
   - repository **clone, attach, workspace restore/import, existing repository discovery/reconciliation**, and first-use paths; search exhaustively rather than assuming filenames;
   - `IRepositoryAccess`, `RepositoryAccessKind`, and existing LibGit2Sharp services;
   - dependency registration and Worker startup if a lifecycle hook is warranted;
   - `GitServiceLongPathsTests` and adjacent worktree/legacy/removal tests.
2. Identify the narrowest **central Worker-side repository-onboarding boundary** that covers ordinary clone, attach and restore. Document any uncovered route and implement a minimal shared call rather than placing copies into each UI handler. Do not add a new Worker command just for this unless existing architecture demands one.
3. Verify whether `Repository.Config` on the pinned version writes the **shared** `$GIT_COMMON_DIR/config` when the path supplied is a linked worktree. Prefer opening the **main repository** and specifying `ConfigurationLevel.Local`; use a real-Git integration test to establish the behavior.
4. Confirm interaction with `extensions.worktreeConfig`, `includeIf`/conditional includes, and layered global/system configuration. The setting GM persists should live in the common repository's local config, **never** `config.worktree`, global, or system. Test `git config --local --get core.longpaths` from the source and from a linked worktree.
5. Identify how clone can checkout paths exceeding 260 characters **before** repository initialization runs. Preserve existing working clone behavior and, if needed, use an initial native Git `-c core.longpaths=true` on the **clone operation itself** (not a persistent global write) and persist the local config once the repository exists. Do not assume post-clone setup can rescue a failed initial checkout.
6. Summarize the actual lifecycle/call-site decision in a short design note **before editing**. If the ideal centralized onboarding path cannot cover all cases, choose a robust low-overhead compatibility strategy, explain it, and proceed.

## Target behavior and design constraints

- On **Windows**, each managed repository is configured with shared repository-local `core.longpaths=true` when initially cloned, attached, or restored, **before** any operation needing deep paths. On other operating systems, no read/write is made for this Windows-specific policy.
- No `git config --get` or `git config set` subprocesses in normal Feature creation/removal. **Do not merely replace per-Feature process calls with repeated LibGit2Sharp checks**: the lifecycle optimization matters.
- No global/system Git configuration changes, user-wide side effects, schema changes, arbitrary file rewrites, or new persistent database status flags.
- Existing configuration already `true` is not rewritten. A missing or false local setting is enabled in the local repository config, consistent with the existing GM policy. If effective `true` is inherited from global config but local is absent, decide explicitly whether to persist `true` locally; prefer deterministic local policy for managed repositories and test/document it.
- Avoid redundant config writes, including when multiple workspaces/features refer to the same underlying repository. Use the existing per-repository synchronization conventions; avoid a race between simultaneous onboarding, worktree create/remove and repository disposal. Do **not** implement an unbounded permanent `ConcurrentDictionary` cache: external Git config may change and paths may be reused/recloned.
- **Legacy repositories** created/attached before this change must remain safe. Provide a one-time first-use/migration fallback for unmanaged initialization state, preferably at Worker repository discovery or the earliest managed-repository usage boundary. If no reliable central early path exists, a cheap LibGit2Sharp-based fallback on the legacy operation is acceptable only if clearly justified, with a deliberate strategy for eliminating repeated normal-path checks. Do not claim a guarantee of 'once ever' without persistent state or a secure lifecycle anchor.
- A configuration error must be logged with a useful reason and never silently marked successful. Distinguish cancellation, repository missing/inaccessible, config locked/read-only, and library errors as existing architecture allows. Maintain current user-facing failure semantics unless an unsafe Git operation requires refusing to continue. On failure, allow a later retry rather than poisoning a cache.
- Short-lived `using`/`Dispose` for `LibGit2Sharp.Repository` on all paths. Respect `IRepositoryAccess` claims and lease lifetimes, including `PathUnderRemoval` behavior. Do not hold a process-wide LibGit2Sharp Repository instance.
- Cancellation must still propagate where supported; handle LibGit2Sharp's synchronous operations with checks before and after and do not wrap synchronous work in `Task.Run` just to present a fake asynchronous interface.
- Leave **native Git** as authority for `git worktree add/remove`, hook-directory resolution, fetch, GitVersion and other unrelated mutations. Scope this effort only to the recommended Git configuration reads/writes and associated lifecycle wiring.
- Avoid changing Feature UX, unrelated Sync behavior, ignore evaluation, worktree listing or checkout-hook handling in this PR. The hooks-path read change must preserve `GitHooksLocationCache` correctness.

## Implementation outline (adapt after code review)

1. Add a small Worker-owned, clearly named **repository configuration initializer** or extend an appropriate existing Git repository service. It should have a single operation analogous to `EnsureWindowsLongPaths(repositoryPath, ct)` which validates the repo, opens it, reads the local setting, conditionally writes `true`, closes it, and reports whether configuration was already correct, was changed, or failed. Avoid an over-engineered abstraction hierarchy.
2. Acquire appropriate repository access/synchronization for **a local config write**, not a read-only lease falsely labeled as mutation; use the current existing mechanism where possible and document lock ordering so parallel Feature operations do not deadlock. Never mutate config while deletion has an exclusive claim on the path.
3. Wire this initializer into the central repository onboarding path(s) proven by code review. Ensure creation of the repository actually precedes local config writes. Cover workspace restore and migration of existing managed repositories with the smallest possible compatibility mechanism.
4. Remove `EnsureLongPathsAsync` (and its timers, obsolete logs and any now-unused parameters) from hot worktree paths when the new coverage is proven. Retain meaningful instrumentation for initialization work and Feature timing, but show zero Git-config child processes in normal repeat Feature operations.
5. If clone requires an early `-c core.longpaths=true`, apply it **only to that Git invocation**. Do not globally toggle users' Git.
6. Keep the diff small and conform to current Worker services, DI and tests. Any new service should have one owner and testable behavior; avoid speculative generalized Git-configuration framework.

## Test matrix — required

**Unit/component tests** (as appropriate):
- Windows missing local setting -> exactly one successful write, `true` visible afterward;
- already true -> no rewrite or duplication;
- explicit local false -> reconciled to true (existing GM behavior);
- inherited global true with local missing -> deliberate documented behavior;
- non-Windows -> no mutation;
- repository invalid/unavailable, permission/config lock failure -> useful warning/error and retry is possible;
- cancellation preserved;
- simultaneous initialization requests -> safe and idempotent, no competing writes;
- disposed repository handles do not interfere with subsequent worktree removal.

**Real Git integration tests**:
- clone/attach/restore code paths initialize a repository; check shared repository `.git/config` with native `git config --local --get` **as independent validation**, not production behavior;
- Feature 1 and Feature 2 using the same source repository create successfully and do not reinitialize or spawn `git config` commands;
- linked worktree has the same shared effective `core.longpaths=true` as source;
- paths >260 characters on Windows can be checked out and removed;
- legacy Feature removal when initial `core.longpaths` was absent still succeeds, including the existing `Remove_sets_core_longpaths_for_a_Feature_created_before_the_setting_existed` scenario (update expected mechanism, not safety requirement);
- branch-name conflicts, existing worktree/idempotent retry, detached HEAD and worktree repair unaffected;
- config externally unset and then repository reopened/reconciled -> supported behavior as documented;
- worktree removal and repository initialization concurrency does not leave pinned handles or permit unsafe mutations;
- where realistic, verify the first clone with a deep path succeeds and no global git config is modified.

**Performance and regression evidence**:
- instrument/log counts of Git child processes attributable to `core.longpaths` during first and subsequent Feature creations: expected **zero** for normal repeat Creates and Removes;
- record per-repository initialization duration separately from Feature creation duration;
- compare the new Create timing against the October 9 baseline, distinguishing wall-clock critical path from cumulative per-repo milliseconds; avoid attributing all remaining time to longpaths (hook-triggered sync is a separate known issue);
- run affected Worker tests, the Worktree tests, then available relevant solution tests/builds. Report exact commands, pass/fail/skips.

## Additional acceptance criteria for the recommended changes

- [ ] `GetRemoteOriginUrlAsync` uses LibGit2Sharp in the normal case with correct raw-config semantics and no extra Git child process.
- [ ] The conditional `core.hooksPath` read is in-process **where proven equivalent**, while Git remains authoritative for actual hook location and non-equivalent cases retain the native fallback.
- [ ] `GitHooksLocationCache` correctness and invalidation behavior are unchanged.
- [ ] `AddSafeDirectoryAsync` writes global trust configuration only when the probe identifies dubious ownership; native Git remains responsible for that write.
- [ ] Tests independently cover origin URL semantics, hook overrides/includes/worktrees, and safe-directory unrelated failures.
- [ ] No other Git config calls or command-scoped `-c` / `GIT_CONFIG_*` authentication settings are migrated as part of this work.

## Acceptance criteria

- [ ] LibGit2Sharp 0.32.0 configuration API verified and used for the normal local get/set path.
- [ ] No new dependency, global Git write, schema migration or unrelated redesign.
- [ ] All supported clone/attach/restore/onboarding routes are identified and handled.
- [ ] A fresh Windows repository is locally configured before deep-path Feature operations.
- [ ] Existing repositories and legacy Feature removal remain safe.
- [ ] Normal repeat Feature create/remove issues **no `git config` subprocess**, and avoids repeated config reads.
- [ ] Configuration scope is the **shared repository-local** config, not worktree-specific config.
- [ ] Concurrency, cancellation, logging, error retry, and repository handle disposal are correct.
- [ ] Real Git long-path, multi-worktree, idempotency and removal tests pass.
- [ ] A concise living design/implementation document captures decisions, files changed, completed steps and benchmark evidence.

## Delivery format

After reviewing the current `main`, present a short **verified call graph and chosen lifecycle seam**, plus an audit of the three additional recommended call sites, then **implement** the changes. At the end, provide: (1) concise change summary and files modified; (2) important design decisions and trade-offs; (3) test commands/results and any limitations; (4) before/after process counts and timings if measured; (5) any intentionally deferred follow-ups. Do not stop at a proposal or make unsupported claims of tests run. Keep this a tight, reviewable change suitable for cherry-picking.
