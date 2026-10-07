# GrayMoon LibGit2Sharp Git Ignore Design

**Status:** Revised after code review and a LibGit2Sharp behavior spike (branch `gitignore-gitlib`)
**Scope:** Every GrayMoon-owned Git-ignore decision in the Worker
**Primary goal:** Replace subprocess-based `git check-ignore` usage with one correct, fast, LibGit2Sharp-backed implementation that is the single source of truth for ignore decisions. No workarounds and no fallbacks.

---

## 1. Motivation

Enterprise VDI measurements show `git check-ignore` is disproportionately expensive. In the measured 39-repository workspace:

- `git check-ignore` ran once per repository during project discovery (39 calls on a clean sync);
- aggregate time was about 180 seconds, about 4.6 seconds per repository;
- the clean workspace sync took 82.1 seconds wall clock.

Each call starts `git.exe`, re-reads Git configuration and ignore files, and is exposed to EDR/antivirus process inspection.

The design removes `git check-ignore` from every GrayMoon-owned decision path and, in the same change, fixes correctness defects in the current implementation (section 3).

---

## 2. Code audit (verified against the branch)

### 2.1 Direct consumers of ignore logic

| # | Location | Current behavior |
|---|---|---|
| 1 | `Services/CsProjFileService.cs` `GetProjectPathsAsync` | Asks `check-ignore` about top-level directory **names** only, then `SearchOption.AllDirectories` below survivors. |
| 2 | `Services/GitService.cs` `StageAndCommitAsync` (line ~889) | `git add`, and on the "ignored by one of your .gitignore files" error runs `check-ignore`, drops ignored paths, retries. |
| 3 | `Services/GitChanges/GitCliRepositoryGitChangesService.cs` `StageAsync` (line ~314) | Same `AddWithIgnoredFallbackAsync` behavior. |
| 4 | `Services/WorkspaceFileSearchService.cs` | **Not in the previous design.** Hard-codes `.git`, `bin`, `obj` skipping at any depth. This is a hand-written approximation of ignore rules and must not survive "all ignore decisions use one implementation". |
| - | `Services/GitIgnoredPathFilter.cs` | Owns the `check-ignore` process, NUL parsing, and the add-retry orchestration. To be deleted. |

All `*.csproj` discovery goes through `ICsProjFileService.FindAsync`/`GetProjectPathsAsync`. Callers, all of which inherit the new behavior: `SyncRepositoryCommand` (via `ScanProjectsAsync`), `RepositoryStateProbe.CaptureAsync`, `RefreshRepositoryProjectsCommand`, `PushRepositoryCommand` post-operation notification (after the network push), `SyncRepositoryDependenciesCommand`.

### 2.2 Deliberately out of scope

- `GitRepositoryWatcher` does not filter ignored paths (it only drops `.git` and nested repositories), so build output churn triggers status refreshes. That is a separate watcher-noise problem: events arrive on FS callback threads at high rate and need a long-lived rule cache with invalidation. It must not be solved by opening a `Repository` per event. Track separately.
- `git add --all` (whole-repository stage) and `git status` already honor ignore rules natively inside the mutation/read that needs them. They are not "GrayMoon ignore decisions" and stay on the Git CLI.
- `ManagedGitIgnoreSection` (App side) writes the managed `.gitignore` block; it makes no ignore decisions.

### 2.3 Existing helper to reuse, not duplicate

`GrayMoon.Common.Git.GitRepositoryPathValidator.Validate(repoRoot, path)` already rejects absolute paths and `.`/`..` traversal, normalizes separators to `/`, and confirms the path stays inside the repository. The Git Changes path already uses it (`ValidateAndNormalizePaths`). The previous design proposed a new normalization helper; that would be a second implementation. Use the existing validator.

`StageAndCommitAsync` currently does **not** use it (only `Replace('\\','/')` and `Trim`), so it accepts `../x` and absolute paths from the caller. This change closes that gap.

---

## 3. Defects in the current behavior

1. **Top-level-only ignore check.** `src/Generated/` ignored by rule is still scanned because only `src` is checked. Nested ignored `.csproj` files are returned.
2. **Fail-open in `KeepNonIgnoredAsync`.** When `check-ignore` fails (exit other than 0/1) it returns the original, unfiltered list. An ignore failure silently becomes "nothing is ignored".
3. **Swallowed discovery failure.** `GetProjectPathsAsync` ends in `catch { return []; }` and `EnumerateCsprojInDirectory` returns `[]` on any exception. A transient IO problem produces an empty project list that sync then reports as "this repository has zero projects".
4. **Nested repositories are not excluded.** Git never tracks the contents of a nested repository or submodule, but discovery will walk into one unless a rule happens to ignore it.
5. **Pathspec/classification mismatch.** `git add --pathspec-from-file` interprets entries as pathspecs (glob magic: `[`, `*`, `?`). Any classifier that treats the same string as a literal path can disagree with what `git add` stages.
6. **Unvalidated paths in `StageAndCommitAsync`** (section 2.3).
7. **Tests assert the old mechanism.** `GitServiceStageAndCommitTests` asserts that `check-ignore` *was* invoked (lines ~146, ~177); `GitIgnoredPathFilterTests` tests the retry helper.

---

## 4. No workarounds, no fallbacks (explicit rules)

- No `git check-ignore` in production code. It is allowed only as the oracle in parity tests.
- No fail-open: if ignore state cannot be determined, the operation fails with a clear error. It never proceeds as if nothing is ignored.
- No swallow-to-empty: discovery that cannot complete returns a failure, never a partial or empty list presented as a result.
- No CLI fallback when LibGit2Sharp cannot open a repository (ownership/`safe.directory`, corrupt repo, missing native library). The error is surfaced.
- No hard-coded directory names (`bin`, `obj`, `node_modules`) standing in for ignore rules.
- No GrayMoon-written pattern matching. Rule evaluation belongs to libgit2 only.
- No add-then-retry: staging classifies first and runs `git add` once.

---

## 5. Verified LibGit2Sharp behavior (spike results)

Spike: LibGit2Sharp 0.32.0 (restores and runs on `net10.0`, Windows x64) compared against `git check-ignore` from Git for Windows 2.53.0, on a repository with root and nested `.gitignore`, `.git/info/exclude`, anchored, `**`, wildcard, negation, spaces/Unicode, a linked worktree and a tracked-but-ignored file.

| Behavior | Result |
|---|---|
| Root/nested `.gitignore`, `.git/info/exclude`, `/anchored/`, `**/gen/`, `*.Generated.csproj`, `generated/*` + `!generated/keep/` | Identical to `git check-ignore` for files **and** directories. |
| Case (`core.ignorecase=true` on Windows) | Identical (`OBJ`, `Obj/h.csproj`, `SRC/Local` all match). libgit2 reads the repo's own setting. |
| Linked worktree (`.git` is a file) | `info/exclude` and `.gitignore` rules identical to CLI. |
| Directory vs file | libgit2 stats the work tree to decide "directory", so `obj` is ignored by `obj/` when the directory exists. `nonexistent/obj` is not ignored, same as Git. |
| **Tracked file matching a rule** | **Diverges.** libgit2 `IsPathIgnored` = `true`; `git check-ignore` = `false` (Git never reports tracked paths). `repo.Index[path]` correctly reports it tracked, including for a file deleted from disk. |
| **Invalid inputs** | `IsPathIgnored` does **not** validate: `../x` -> `true`, `.` -> `true`, `.git` -> `true`, `obj\h.csproj` (backslash) -> `false` (wrong), absolute path -> `false` (wrong), `""` -> `ArgumentException`. Callers must pass only validated `/`-separated repository-relative paths. |
| `RetrieveStatus` with `PathSpec` as a classifier | Reports ignored directories as one entry and **omits** a path inside an ignored directory. Unsuitable as the classifier; use `IsPathIgnored` + index. |
| Cost | `Repository` open about 1.9 ms. `IsPathIgnored` about 0.2-0.35 ms per deep path (20,000 checks 4.4 s; 6,000 directory checks 2.1 s, versus 0.9 s to merely enumerate them). Pruning matters; check directories and candidate files only, never every file. |

Conclusions that drive the design: libgit2 rule evaluation is trustworthy for untracked paths; tracked state must come from the index; input validation is mandatory and must precede every call.

---

## 6. The single exclusion predicate

Git's own definition: a path is *excluded* when it is untracked **and** matches an ignore rule. Tracked paths are never excluded.

```text
IsExcluded(path, kind):
    kind = File:       !tracked(path) && IsPathIgnored(path)
    kind = Directory:  !containsTrackedEntries(path) && IsPathIgnored(path + "/")
```

Every consumer uses this one predicate:

- discovery prunes a directory iff it is excluded, and accepts a candidate file iff it is not excluded;
- staging drops a path iff it is excluded.

Tracked paths therefore stay stageable (including deleted tracked files) and tracked `.csproj` files under an ignore-matching directory are still discovered (a deliberate improvement over the current top-level check, and consistent with Git's definition).

---

## 7. Service contract

Ignore evaluation needs one open `Repository` for a logical operation, and `Repository` is not thread-safe. The contract is therefore a short-lived session, not a bag of path-in/path-out methods (this replaces the previous `FindNonIgnoredFiles` on the service: walking the file system is project discovery's job, not the ignore service's).

```csharp
public interface IGitIgnoreService
{
    // Opens the repository once. Throws GitIgnoreException (typed, actionable) when it cannot be opened.
    IGitIgnoreSession Open(string repositoryPath);
}

public interface IGitIgnoreSession : IDisposable
{
    // path: validated, repository-relative, '/'-separated. Anything else throws ArgumentException.
    bool IsExcluded(string relativePath, GitPathKind kind);

    // Filters requested paths with the same predicate; files vs directories are decided from the work tree.
    GitStageSelection SelectStageable(IReadOnlyList<string> relativePaths);
}

public enum GitPathKind { File, Directory }

public sealed record GitStageSelection(
    IReadOnlyList<string> Stageable,
    IReadOnlyList<string> ExcludedUntracked);
```

Implementation: `LibGit2SharpGitIgnoreService` (sealed, primary constructor, registered as a singleton in `RunCommandHandler`; the singleton holds no `Repository`).

Session internals:

- Opens `new Repository(repositoryPath)` once; libgit2 resolves linked worktrees and `.git` files itself. GrayMoon never derives Git directory paths for ignore evaluation.
- Builds the tracked-file set and the set of directories that contain tracked entries lazily on the first query (one pass over `repo.Index`), using ordinal-ignore-case comparison when `core.ignorecase` is true and ordinal otherwise.
- Rejects invalid input with `ArgumentException` rather than passing it to libgit2 (section 5).
- No cross-session caching; `.gitignore`, `info/exclude`, global excludes and index state change at any time.

The session never normalizes. Callers validate with `GitRepositoryPathValidator` first (section 2.3), so there is exactly one normalization implementation.

---

## 8. Project discovery

Algorithm (sequential walk on one thread, one session; `.csproj` parsing stays parallel afterwards as today):

```text
open session
visit(root):
    for each *.csproj file in dir:  include iff !IsExcluded(file, File)
    for each subdirectory d (ordinal order):
        skip d if d is ".git"
        skip d if d has its own .git (nested repository or submodule: never part of this repository)
        skip d if IsExcluded(d, Directory)
        visit(d)
```

Rules:

- **Pruning is exact, not "conservative vs aggressive".** Git cannot re-include anything below an excluded directory, so pruning an excluded directory is precisely Git's semantics. Negation such as `generated/*` + `!generated/keep/` works because `generated` itself is not excluded (only its children are), so the walk reaches `generated/keep` and prunes its ignored siblings. The spike confirmed parity for exactly this case. The previous design's "Option A/B" is removed.
- Candidate files are checked individually.
- Results are sorted ordinally so persistence sees a stable order.
- Cancellation is checked per directory.
- **Failure policy (no fallback):** an unopenable repository or an unreadable directory fails the scan with an exception that names the repository and path. It never returns a partial or empty list. Callers decide how a failed probe is reported (section 12); none may treat it as "zero projects".
- `CsProjFileService` stops depending on `GitProcessRunner`; it depends on `IGitIgnoreService`.
- A missing repository directory keeps its existing meaning at the call sites (they already guard `Directory.Exists`).

---

## 9. Selective staging

```text
validate paths (GitRepositoryPathValidator, both call sites)
    |
open session; SelectStageable(paths)        one Repository open
    |
Stageable empty  -> success, nothing staged (log count of excluded)
    |
git --literal-pathspecs add --pathspec-from-file=- ...     exactly one invocation, no retry
    |
(StageAndCommit only) git diff --cached --quiet -> git commit
```

- Both `StageAndCommitAsync` and Git Changes `StageAsync` call the same session method. There is no second filter.
- `--literal-pathspecs` makes `git add` interpret each entry exactly as the classifier did, removing the glob-magic mismatch (defect 5). Verified at implementation time against the batched fallback path used on Git older than 2.25.
- Tracked and deleted-tracked paths are stageable; untracked non-ignored paths are stageable (a nonexistent untracked path still reaches `git add`, which reports it as Git does); only excluded untracked paths are dropped.
- Never `git add -f`.
- If `.gitignore` changes between classification and `git add` and Git rejects a path, that error is returned to the caller. There is no retry loop. The window is milliseconds and an explicit error is the correct outcome.
- Classification takes no `GitProcessRunner` lock: it is a read; libgit2 reads the index atomically, and `git` itself writes the index via `index.lock` + rename.

Whole-repository staging (`git add --all`) is unchanged (section 2.2).

---

## 10. Workspace file search

`WorkspaceFileSearchService` currently skips `bin`/`obj`/`.git` by name. Under the single-source rule it must use the session predicate per repository instead, which changes observable behavior in two ways:

- ignored files that are not build output (for example `.env`, `*.user`) no longer appear in the file picker;
- tracked files under a directory named `bin`/`obj` now appear.

Directories without Git metadata (the unscoped search enumerates every child of the workspace folder) have no Git ignore semantics; the search must be restricted to directories with Git metadata, consistent with `GetWorkspaceRepositoriesCommand`. **This is a product-visible change and is gated on confirmation** (plan Unit 8). If it is rejected, the hard-coded skip stays and is documented as an accepted non-Git rule; it must not be partially migrated.

---

## 11. Native packaging, ownership and environment

- The Worker is a framework-dependent dotnet tool (`PackAsTool`) that also runs as a Windows Service or Linux systemd unit. `LibGit2Sharp.NativeBinaries` must be present under `runtimes/<rid>/native` in the packed tool and in `dotnet publish` output; verify `dotnet pack` + `dotnet tool install` on win-x64 and linux-x64, and that GitVersion.MsBuild/CI are unaffected.
- Pin an exact version (`0.32.0` is the version spiked, with libgit2 shipped inside `NativeBinaries`); do not use a wildcard for a native dependency. Record the reason and the bundled libgit2 version in the docs.
- libgit2 enforces repository ownership (`safe.directory`) like Git does. When the Worker runs as a service account that does not own a repository, `Open` fails. That is parity with the Git CLI, which fails the same way; it is surfaced as a `GitIgnoreException` with the repository path and the ownership message. No CLI fallback. Parity test required.
- libgit2 reads system/global/XDG config for `core.excludesFile`; global-excludes parity is tested in an isolated `HOME`/`XDG_CONFIG_HOME`.

---

## 12. Failure reporting by caller

| Caller | Required behavior on `GitIgnoreException` / incomplete discovery |
|---|---|
| `SyncRepositoryCommand`, `RepositoryStateProbe` | Report the repository's project probe as failed (existing `projectsProbed` style semantics so persisted projects survive), never persist `[]`. |
| `RefreshRepositoryProjectsCommand` | Return the command error; do not return `Projects = []`. |
| `PushRepositoryCommand` post-operation | The push already succeeded; report projects as not probed and log. A discovery failure must not turn a successful push into a failed one. |
| Staging (both) | Return a stage failure with the error text; stage nothing. |

Each is verified in the plan; the exact existing flag names are confirmed against the current code before editing.

---

## 13. Lifetime and concurrency

- One `Repository` per logical operation (one discovery scan, one stage classification), disposed deterministically with `using`. Not shared across threads or operations; no pool; no singleton `Repository`.
- Disposal is correctness-critical on Windows: an undisposed handle can block worktree removal (`RemoveGitWorktree`) and branch operations.
- Different repositories may be scanned concurrently (sync workers); each owns its own session.

---

## 14. Instrumentation

Debug-level structured timings, no per-path logging at Information.

Discovery: `repo`, `elapsedMs`, `directoriesVisited`, `directoriesPruned`, `nestedReposSkipped`, `candidateFiles`, `excludedCandidates`, `returnedFiles`.
Staging: `repo`, `elapsedMs`, `requested`, `trackedPaths`, `excludedUntracked`, `stageable`.

---

## 15. Parity strategy

`git check-ignore` is the oracle in tests only. The fixture builds a temporary repository, writes rules and files, asks both `git check-ignore` and the session, and asserts equal classification **under Git's definition** (tracked paths are not ignored). The test project must never launch `check-ignore` from production types; a process-recorder assertion proves the production paths launch none.

Required cases (the spike matrix is the starting point):

- root and nested `.gitignore`; `.git/info/exclude`; global excludes (isolated config)
- directory-only patterns, anchored, `**`, wildcards, `*.Generated.csproj`
- negation and nested negation (`generated/*`, `!generated/keep/`) including walk-level assertions that `generated/keep` is reached and `generated/other` pruned
- tracked file matching a rule; deleted tracked file matching a rule; tracked file inside an ignored directory
- untracked ignored / untracked non-ignored
- spaces, Unicode, `core.ignorecase` true and false (platform-appropriate, no hard-coded cross-platform assumptions)
- linked worktree opened from the worktree path
- nested repository and submodule directories skipped
- invalid input rejected: `../x`, absolute, backslash, empty, `.git`
- ownership/`safe.directory` failure surfaces a typed error

---

## 16. Performance acceptance

- Production `git check-ignore` process count is 0 for sync, explicit refresh, post-push refresh, Stage-and-Commit, and Git Changes stage (asserted by the process recorder in tests and by the VDI run).
- One `Repository` open per discovery or classification operation.
- Re-run the 39-repository / 16-worker VDI benchmark and compare wall time, `SyncRepository` p50/p90, per-repo project scan time, total Git subprocess count, `check-ignore` count (target 0), GitVersion duration.

---

## 17. Architectural boundary

```text
LibGit2Sharp : ignore decisions, tracked/untracked classification needed for them
Git CLI      : add, commit, fetch, pull, push, merge, checkout, reset, clean, status
```

This change does not convert mutations or network operations to LibGit2Sharp and does not combine with the broader sync/ref/status migration.

---

## 18. Definition of done

- Every GrayMoon-owned ignore decision goes through `IGitIgnoreSession.IsExcluded`/`SelectStageable`; `WorkspaceFileSearchService` is migrated or its non-Git rule is explicitly documented per section 10.
- No production `git check-ignore`; `GitIgnoredPathFilter` and its tests deleted.
- Nested ignored `.csproj` files excluded; tracked files stay stageable and discoverable; nested repositories skipped.
- No fail-open, swallow-to-empty, or retry path remains; failures surface per section 12.
- `StageAndCommitAsync` validates paths; both staging paths use `--literal-pathspecs`.
- Parity tests (section 15) pass; native packaging verified on win-x64 and linux-x64.
- VDI benchmark rerun and documented.
