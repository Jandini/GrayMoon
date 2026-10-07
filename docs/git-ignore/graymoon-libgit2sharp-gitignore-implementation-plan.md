# GrayMoon LibGit2Sharp Git Ignore Implementation Plan

**Companion design:** `graymoon-libgit2sharp-gitignore-design.md` (read its sections 4-6 first: the no-fallback rules, the spike results and the single exclusion predicate)
**Goal:** One LibGit2Sharp-backed ignore session used by every GrayMoon-owned ignore decision, with the old `check-ignore` machinery deleted, not wrapped.

---

## 1. Delivery strategy

Small, testable units, each leaving the build green. Do not combine with the broader LibGit2Sharp sync/ref/status migration.

Order: baseline, dependency, service, parity gate, discovery, caller failure semantics, staging (Git Changes then Stage-and-Commit), file search (decision gate), delete the old helper, lifetime review, instrumentation, regression, VDI benchmark, docs.

Parity (Unit 3) is a hard gate: nothing that mutates or prunes migrates until it passes.

Repository conventions that apply to every unit: CRLF line endings, ASCII hyphens only (no en/em dashes), `sealed` classes, primary constructors for capture-only classes.

---

# Unit 0 - Baseline

Work:

- Run the Worker and Common test projects and record the green baseline.
- Record the production `check-ignore` call sites: `GitIgnoredPathFilter.KeepNonIgnoredAsync` (called from `CsProjFileService`) and `AddWithIgnoredFallbackAsync` (called from `GitService.StageAndCommitAsync` and `GitCliRepositoryGitChangesService.StageAsync`).
- Record non-Git ignore approximations: `WorkspaceFileSearchService` (`bin`/`obj`/`.git`).
- List tests that encode the old mechanism and must change: `GitIgnoredPathFilterTests` (delete), `GitServiceStageAndCommitTests` (`Ignored_path_add_failure_drops_ignored_and_commits_the_rest`, `All_ignored_paths_is_nothing_staged` assert `check-ignore` is invoked; `Tracked_then_gitignored_file_still_stages_without_check_ignore`), `GitCliRepositoryGitChangesServiceTests.Stage_explicit_ignored_path_drops_it_and_stages_the_rest`, `CsProjFileServiceIgnoredDirTests` (constructor takes `GitProcessRunner`).
- Capture the VDI baseline numbers from the design (39 `check-ignore` calls, about 180 s aggregate, 82.1 s wall).

Acceptance: baseline recorded; tests green before any edit.

---

# Unit 1 - LibGit2Sharp dependency

Files: `src/GrayMoon.Worker/GrayMoon.Worker.csproj`.

Work:

- Add `<PackageReference Include="LibGit2Sharp" Version="0.32.0" />` (exact version; this is the version spiked on `net10.0`). Do not reference it from App, Common or Abstractions.
- Verify the native assets: `dotnet publish` output contains `runtimes/<rid>/native/git2-*.dll`/`.so`; `dotnet pack` produces a tool package that contains them; `dotnet tool install` of the packed tool on win-x64 and linux-x64 can open a repository.
- Confirm the Windows Service and Linux systemd hosting paths load the native library (service account search path).
- Record the version, the bundled libgit2 version (printed by `GlobalSettings.Version`) and the reason for pinning in the living docs.

Acceptance: existing tests pass with the package referenced but unused; packed tool contains the native binaries.

---

# Unit 2 - `IGitIgnoreService` and session

Files (new): `src/GrayMoon.Worker/Abstractions/IGitIgnoreService.cs` (interfaces, `GitPathKind`, `GitStageSelection`, `GitIgnoreException`), `src/GrayMoon.Worker/Services/LibGit2SharpGitIgnoreService.cs`.
Edit: `Cli/Handlers/RunCommandHandler.cs` (register `IGitIgnoreService` as a singleton next to `ICsProjFileService`).

Work (design section 7):

- `Open(repositoryPath)`: `new Repository(path)`; wrap `LibGit2SharpException`/`RepositoryNotFoundException` into `GitIgnoreException` carrying the repository path and the ownership hint when applicable. No fallback to the CLI.
- `IsExcluded(path, kind)` implements the single predicate (design section 6). Directory paths are passed to `IsPathIgnored` with a trailing `/`.
- Tracked sets: lazily built once per session from `repo.Index`: tracked files and every ancestor directory of a tracked file. Comparer chosen from `core.ignorecase`.
- Input validation: reject empty, absolute, rooted, backslash-containing, `.`/`..`-segment, leading `/`, and the `.git` segment with `ArgumentException`. The session does not normalize (callers use `GitRepositoryPathValidator`).
- `SelectStageable(paths)`: classifies each path as File vs Directory from the work tree (a missing path is a File) and applies the predicate; returns `Stageable` and `ExcludedUntracked` preserving input order.
- Dispose releases the `Repository`.

Tests (unit, no oracle): lifetime (disposed session throws), validation matrix, tracked set including a deleted tracked file, `core.ignorecase` both values, `SelectStageable` ordering.

Acceptance: unit tests pass; no consumer migrated yet.

---

# Unit 3 - Parity harness (gate)

Files (new, test project): `src/GrayMoon.Worker.Tests/GitIgnoreParityTests.cs`, using `TempGitRepositoryFixture`.

Work: the oracle is `git check-ignore` run **only from tests**, compared under Git's definition (tracked paths are not ignored). Cases are listed in design section 15, seeded from the spike matrix: root/nested rules, `info/exclude`, global excludes in an isolated config, anchored, `**`, wildcards, negation and nested negation, tracked-matching-rule, deleted-tracked, tracked-in-ignored-directory, spaces, Unicode, `core.ignorecase` both ways, linked worktree opened from the worktree path, nested repo and submodule, ownership failure surfaces `GitIgnoreException`.

Include the divergence test explicitly: raw `IsPathIgnored` differs from the oracle for a tracked file, and `IsExcluded` agrees with the oracle. This documents why the index is consulted.

Gate: all parity tests pass on Windows (and Linux CI if available) before Units 4 onward merge. A parity failure is fixed in the session, never by special-casing in a consumer.

---

# Unit 4 - Project discovery

Files: `Services/CsProjFileService.cs`, `Abstractions/ICsProjFileService.cs` (update the XML docs that say "gitignored top-level directories"), `Worker.Tests/CsProjFileServiceIgnoredDirTests.cs`.

Work:

- Constructor becomes `CsProjFileService(ICsProjFileParser parser, IGitIgnoreService ignore, ILogger<CsProjFileService> logger)`; remove `GitProcessRunner`.
- Replace `GetProjectPathsAsync` with the sequential walk of design section 8: one session, per directory check files then recurse, skip `.git`, skip directories that contain their own `.git`, prune `IsExcluded(dir)`, check each `*.csproj` with `IsExcluded(file)`, ordinal sort, cancellation per directory.
- Delete the `catch { return []; }` blocks and `EnumerateCsprojInDirectory`'s swallow. Unreadable directory or unopenable repository throws with repository and path in the message. `OperationCanceledException` propagates untouched.
- Keep `FindAsync`'s parallel parse. A single file that fails to parse is still skipped (that is a per-file content decision, not an ignore or discovery failure); log it at Debug with the path.
- Keep the `Directory.Exists(repoPath)` early return only if callers rely on it; otherwise remove (Unit 5 decides).

Tests:

- Existing two tests updated to the new constructor.
- `Repo/{root.csproj, src/A/A.csproj, src/Generated/B.csproj}` with `src/Generated/` ignored: `B.csproj` excluded (the current defect).
- Negation: `generated/*`, `!generated/keep/`; `generated/keep/D.csproj` found, `generated/other/C.csproj` not.
- Tracked `.csproj` inside an ignore-matching directory is returned.
- Nested repository directory (has `.git`) not descended; submodule likewise.
- Unreadable directory fails the scan (does not return a partial list).
- Process recorder shows no `check-ignore`.

Acceptance: `CsProjFileService` has no reference to `GitProcessRunner` or `check-ignore`.

---

# Unit 5 - Caller failure semantics

Verify and, where needed, change each project-discovery caller so a thrown discovery error is never read as "zero projects" (design section 12):

- `SyncRepositoryCommand.ScanProjectsAsync` and `RepositoryStateProbe.CaptureAsync`: confirm how a faulted task is handled today; a failed probe must leave persisted projects untouched (use the existing "probed" flag mechanism, confirmed against the code before editing).
- `RefreshRepositoryProjectsCommand`: surface the error; do not return `Projects = []` for it.
- `PushRepositoryCommand.BuildPostOperationNotificationAsync`: a discovery failure after a successful push reports projects as not probed and logs; the push result stays successful.
- `SyncRepositoryDependenciesCommand`: confirm behavior and apply the same rule.

Tests: per caller, a `ICsProjFileService` double that throws; assert no empty-projects persistence and, for push, a successful result.

Acceptance: no caller contains its own ignore logic or an `catch -> []` around discovery.

---

# Unit 6 - Git Changes selective stage

File: `Services/GitChanges/GitCliRepositoryGitChangesService.cs` (`StageAsync`, line ~314).

Work:

- After `ValidateAndNormalizePaths`, open one session and call `SelectStageable`. Constructor gains `IGitIgnoreService`.
- `Stageable` empty: return `MutationSuccessAsync` (current "nothing to stage" UX), log excluded count at Information.
- Otherwise run exactly one `git --literal-pathspecs add --pathspec-from-file=- --pathspec-file-nul`. Confirm the global option precedes the subcommand in both `RunPathspecOperationAsync` and the bounded-batch fallback for Git older than 2.25; unit-test both argument builders.
- No retry, no `IsIgnoredPathsAddError` parsing. Non-zero exit returns `StageFailed` with Git's text as today.
- Whole-repository scope (`git add --all`) untouched.

Tests: update `Stage_explicit_ignored_path_drops_it_and_stages_the_rest`; add mixed ignored/non-ignored, all-ignored, tracked-matching-rule, deleted tracked, literal path containing `[`/`*`, and a recorder assertion of exactly one `add` and zero `check-ignore`.

---

# Unit 7 - `StageAndCommitAsync`

File: `Services/GitService.cs` (line ~874).

Work:

- Replace the `Replace('\\','/')` normalization with `GitRepositoryPathValidator.Validate` for every path; an invalid path returns `(false, false, <validator message>)`. (Closes design defect 6.)
- Same session/`SelectStageable`/single `git --literal-pathspecs add` flow as Unit 6, sharing the same session method. No second filter.
- All excluded: return `(true, false, null)` as the existing `All_ignored_paths_is_nothing_staged` expects, without running `git add`.
- Keep `git diff --cached --quiet` then `git commit` unchanged.
- Classification and mutation both happen inside this method with no await between them other than the add itself; no extra locking (design section 9).

Tests: update the three tests named in Unit 0 to assert zero `check-ignore`; add invalid path (`../x`, absolute) rejected; hooks-config prefix still honored with `--literal-pathspecs`.

---

# Unit 8 - Workspace file search (decision gate)

File: `Services/WorkspaceFileSearchService.cs`.

**Gate:** confirm with the product owner before starting. The change (design section 10): drop hard-coded `bin`/`obj`, use the session predicate per repository, restrict to directories with Git metadata. Observable effects: ignored non-build files stop appearing; tracked files under `bin`/`obj` start appearing.

- If approved: inject `IGitIgnoreService`, open one session per repository directory, prune with `IsExcluded(dir)`, filter files with `IsExcluded(file)` after the name match, keep nested-repo skipping, surface failures. Tests for the above plus nested ignore rules.
- If rejected: leave the service as is and add a short comment and a design note stating it is an accepted non-Git file-picker rule. Do not half-migrate.

---

# Unit 9 - Delete the old machinery

Work:

- Delete `Services/GitIgnoredPathFilter.cs` and `Worker.Tests/GitIgnoredPathFilterTests.cs`.
- Remove stale text mentioning `check-ignore` in production comments (for example the note in `GrayMoon.Common/CommandLineService.cs` near line 177; keep the behavior it describes only if still true for another command).
- Update `ICsProjFileService` and any other XML docs.

Code-search gates (run and record):

- `check-ignore` appears only in tests (oracle), docs and history.
- `GitIgnoredPathFilter`, `IsIgnoredPathsAddError`, `AddWithIgnoredFallbackAsync`, `KeepNonIgnoredAsync` return nothing.
- No `catch` returning an empty discovery list in `CsProjFileService`.

---

# Unit 10 - Worktree, lifetime and concurrency review

- Primary checkout, linked worktree (`.git` file), and a workspace/feature worktree: same ignore results; opened from the worktree path with no manual `.git` resolution.
- Every `Open` is in a `using`; no `Repository` field on any singleton; no session crosses an `await` boundary shared by concurrent operations.
- Create-then-remove a worktree immediately after a scan and a stage classification: removal must not fail on a leaked handle (add a test around the existing `RemoveGitWorktree` path).
- Concurrent discovery of different repositories (sync fan-out) is safe; confirm no lock inversion with the process runner (classification takes no runner lock).

---

# Unit 11 - Instrumentation

Debug logs as in design section 14 for discovery and staging. No per-path Information logging. Add an elapsed-time field to the existing sync timing breakdown only if the sync log already carries the project scan time (do not invent a new reporting channel).

---

# Unit 12 - Regression suite

Run all three test projects. Explicitly verify: sync discovery, explicit refresh, post-push notification, Git Changes selective stage, Stage-and-Commit, ignored-only, mixed, tracked-matching-rule, deleted tracked, nested ignored project, negated project rule, nested repository, worktree, discovery-failure semantics per caller. No UX regression beyond the intentional behavior changes listed in the design (nested ignored projects now excluded, tracked projects under ignored directories now included, invalid stage paths now rejected, literal pathspecs).

---

# Unit 13 - VDI validation

Re-run the 39-repository / 16-worker benchmark, at least three quiet syncs, and the post-push flow separately. Compare wall clock, `SyncRepository` p50/p90, project discovery time, Git process count, `check-ignore` count (must be 0), GitVersion duration, status/ref timings. Record the results next to the baseline.

---

# Unit 14 - Living documentation

Update the existing Git service design docs: ignore decisions on LibGit2Sharp, mutations/network on the Git CLI. Record the exact LibGit2Sharp version and bundled libgit2 version, parity coverage, worktree behavior, the tracked-file divergence between libgit2 and `git check-ignore`, the validation requirement, the ownership/`safe.directory` behavior, benchmark deltas, and the known out-of-scope item (watcher noise). Keep the checklist below current.

---

# Implementation checklist

- [ ] Baseline and VDI numbers recorded (Unit 0)
- [ ] LibGit2Sharp 0.32.0 added; native assets verified in publish and packed tool (win-x64, linux-x64)
- [ ] `IGitIgnoreService` / `IGitIgnoreSession` / `GitIgnoreException` implemented and registered
- [ ] Single exclusion predicate with index-backed tracked check
- [ ] Parity harness and gate passed (including tracked divergence, negation walk, worktree, nested repo, ownership)
- [ ] `CsProjFileService` migrated; no `GitProcessRunner`; no swallow-to-empty
- [ ] Caller failure semantics verified: sync, probe, refresh, push post-op, dependencies sync
- [ ] Git Changes `StageAsync` migrated; `--literal-pathspecs`; single `git add`
- [ ] `StageAndCommitAsync` validates paths and migrated; single `git add`
- [ ] Whole-repo `git add --all` reviewed and unchanged
- [ ] `WorkspaceFileSearchService` decision made and implemented (or documented as accepted non-Git rule)
- [ ] `GitIgnoredPathFilter` and its tests deleted; code-search gates recorded
- [ ] Worktree, handle-release and concurrency review complete
- [ ] Instrumentation added
- [ ] Full regression passes
- [ ] VDI benchmark rerun and documented
- [ ] Living docs updated

---

# Final acceptance criteria

1. No Worker production path launches `git check-ignore`; the process recorder proves it for sync, refresh, post-push, Stage-and-Commit and Git Changes stage.
2. Every GrayMoon-owned ignore decision uses the one `IGitIgnoreSession` predicate (or Unit 8 documents the accepted exception).
3. Project discovery excludes nested ignored `.csproj` files, includes tracked ones, honors negation, and never descends into nested repositories.
4. Parity tests cover rules, `info/exclude`, global excludes, case, Unicode, spaces, worktrees, and the tracked-file divergence.
5. Selective staging skips only excluded untracked paths, uses one `git add --literal-pathspecs` call, and never retries or force-adds.
6. Stage-and-Commit validates paths with `GitRepositoryPathValidator` and shares the classification with Git Changes.
7. No fail-open, swallow-to-empty, CLI fallback or hard-coded ignore list remains in ignore or discovery code.
8. Discovery failures surface per caller (design section 12) and never replace persisted projects with an empty list.
9. No ignore dependency is added to fetch, push, merge or branch-update operations.
10. Git mutations and network operations remain on the Git CLI.
11. The VDI benchmark shows zero `check-ignore` processes and records the performance delta.
