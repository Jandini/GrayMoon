# GrayMoon LibGit2Sharp Git Ignore Implementation Plan

**Companion design:** `graymoon-libgit2sharp-gitignore-design.md`  
**Goal:** Implement one LibGit2Sharp-backed ignore service and migrate every current GrayMoon ignore decision to it.

---

## 1. Delivery strategy

Implement this in small, testable units.

Do not combine this with the broader LibGit2Sharp sync/ref/status migration.

This change is specifically about Git-ignore semantics and the operations that depend on them.

Recommended order:

1. baseline and audit;
2. add library dependency;
3. implement normalization and ignore service;
4. build Git parity tests;
5. migrate project discovery;
6. migrate selective staging in Git Changes;
7. migrate Stage-and-Commit;
8. remove production `check-ignore`;
9. regression/performance validation;
10. update living documentation.

---

# Unit 0 - Baseline and safety net

## Owner

Performance / integration agent.

## Work

Capture current behavior before modifying code.

Record:

- current `git check-ignore` production call sites;
- project-discovery test behavior;
- Stage-and-Commit ignored-path behavior;
- Git Changes selective staging behavior;
- worktree behavior;
- current performance measurement from enterprise VDI.

Add a code-search guard document listing these current direct consumers:

```text
src/GrayMoon.Worker/Services/CsProjFileService.cs
src/GrayMoon.Worker/Services/GitService.cs
src/GrayMoon.Worker/Services/GitChanges/GitCliRepositoryGitChangesService.cs
```

Also record indirect project-discovery callers including:

```text
SyncRepositoryCommand
RefreshRepositoryProjectsCommand
PushRepositoryCommand
```

## Acceptance

Baseline tests pass before implementation begins.

---

# Unit 1 - Add LibGit2Sharp dependency

## Owner

Infrastructure agent.

## Work

Add an explicit LibGit2Sharp package reference to the appropriate Worker project.

Do not introduce it into projects that do not require it.

Verify:

- Windows build;
- worker publish;
- self-contained/normal deployment as applicable;
- native libgit2 assets are included correctly;
- existing CI succeeds.

Pin a specific version according to GrayMoon dependency conventions.

Document the chosen version and why.

## Acceptance

Worker starts and existing tests pass with LibGit2Sharp referenced but not yet used.

---

# Unit 2 - Introduce `IGitIgnoreService`

## Owner

Git/local-read agent.

## Files

Add appropriate abstraction and implementation, for example:

```text
src/GrayMoon.Worker/Abstractions/IGitIgnoreService.cs
src/GrayMoon.Worker/Services/LibGit2SharpGitIgnoreService.cs
```

Names may be adjusted to repository conventions.

## Required operations

The service needs to support:

1. classify a single path;
2. classify/filter many paths with one repository open;
3. discover non-ignored files through a Git-aware traversal.

Suggested API:

```csharp
public interface IGitIgnoreService
{
    GitIgnorePathClassification Classify(
        string repositoryPath,
        string path);

    IReadOnlyList<GitIgnorePathClassification> Classify(
        string repositoryPath,
        IReadOnlyList<string> paths);

    IReadOnlyList<string> FindNonIgnoredFiles(
        string repositoryPath,
        string searchPattern,
        CancellationToken cancellationToken = default);
}
```

Recommended model:

```csharp
public sealed record GitIgnorePathClassification(
    string Path,
    bool IsIgnored,
    bool IsTracked)
{
    public bool ShouldStage => IsTracked || !IsIgnored;
}
```

Exact model names are flexible.

The semantics are not.

## Acceptance

Unit tests cover repository lifetime, path validation and normalization.

---

# Unit 3 - Central path normalization

## Owner

Git/local-read agent.

## Work

Implement one tested helper that converts:

- absolute repository-contained paths;
- repository-relative Windows paths;
- repository-relative slash paths;

into safe Git-relative paths.

Required behavior:

```text
C:\Repo\src\App\App.csproj -> src/App/App.csproj
src\App\App.csproj         -> src/App/App.csproj
./src/App/App.csproj       -> src/App/App.csproj
```

Reject:

```text
..\OtherRepo\File.cs
C:\OtherRepo\File.cs
```

Do not duplicate normalization in consumers.

## Tests

- root file;
- nested file;
- spaces;
- Unicode;
- separators;
- `.` segments;
- escape attempt;
- different drive on Windows where relevant.

---

# Unit 4 - Git parity test harness

## Owner

Test agent.

## Purpose

Prove LibGit2Sharp behavior matches Git for the semantics GrayMoon requires.

The test suite may invoke:

```text
git check-ignore
```

as the oracle.

Production code may not.

## Test matrix

Implement parity tests for:

- root `.gitignore`;
- nested `.gitignore`;
- directory ignore;
- file ignore;
- wildcard;
- `**`;
- root-anchored patterns;
- negation;
- nested negation;
- `.git/info/exclude`;
- global exclude when practical and isolated;
- filenames with spaces;
- Unicode;
- Windows separators;
- ignored `.csproj`;
- non-ignored `.csproj`;
- linked worktree.

Also add explicit tests for:

```text
tracked file + matching ignore rule
deleted tracked file + matching ignore rule
untracked ignored file
untracked non-ignored file
```

## Gate

Do not migrate mutation behavior until these staging semantics are proven.

---

# Unit 5 - Implement correct project traversal

## Owner

Project-discovery agent.

## Replace

Current logic in:

```text
src/GrayMoon.Worker/Services/CsProjFileService.cs
```

that uses:

```text
GitIgnoredPathFilter.KeepNonIgnoredAsync
```

and top-level-only ignore checks.

## New behavior

`CsProjFileService` depends on:

```text
IGitIgnoreService
```

instead of `GitProcessRunner` for discovery.

Flow:

```text
IGitIgnoreService.FindNonIgnoredFiles(repoPath, "*.csproj")
        |
parse returned csproj files
```

## Traversal requirements

- one LibGit2Sharp repository open;
- skip Git metadata;
- cancellation during traversal;
- candidate `.csproj` evaluated individually;
- ignored nested `.csproj` excluded;
- inaccessible directory does not kill entire repository scan;
- preserve deterministic enough results for existing persistence behavior.

## Directory pruning

Start conservatively.

Enable aggressive pruning only for semantics proven by the parity test suite.

Correctness is more important than shaving the final few milliseconds.

## Tests

Add:

```text
Repo/
  root.csproj
  src/
    A/A.csproj
    Generated/B.csproj
```

with nested rules proving `B.csproj` is excluded.

Test negation/re-inclusion.

## Acceptance

`CsProjFileService` contains no production call to `git check-ignore`.

---

# Unit 6 - Validate all project-discovery callers

## Owner

Integration agent.

## Flows

Validate:

### Sync

```text
SyncRepositoryCommand
  -> CsProjFileService.FindAsync
  -> new ignore service
```

### Explicit refresh

```text
RefreshRepositoryProjectsCommand
  -> CsProjFileService.FindAsync
  -> new ignore service
```

### Push post-operation refresh

```text
PushRepositoryCommand.BuildPostOperationNotificationAsync
  -> CsProjFileService.FindAsync
  -> new ignore service
```

### Other project/package flows

Search all `ICsProjFileService.FindAsync` and `GetProjectPathsAsync` callers and verify they use the same implementation automatically.

## Acceptance

No caller introduces its own ignore logic.

---

# Unit 7 - Migrate Git Changes selective Stage

## Owner

Git Changes agent.

## File

```text
src/GrayMoon.Worker/Services/GitChanges/GitCliRepositoryGitChangesService.cs
```

## Replace

```text
GitIgnoredPathFilter.AddWithIgnoredFallbackAsync(...)
```

with pre-classification:

```text
requested paths
  -> IGitIgnoreService.Classify(...)
  -> ShouldStage paths
  -> one git add operation
```

## Critical semantics

Do not suppress tracked files merely because `IsPathIgnored` reports a matching ignore rule.

Stage when:

```text
IsTracked == true
```

or:

```text
IsTracked == false && IsIgnored == false
```

Skip only:

```text
IsTracked == false && IsIgnored == true
```

## Behavior when every requested path is ignored

Return successful/no-op behavior consistent with current UX unless existing tests establish another required response.

Do not force add.

## Acceptance

Normal successful selective staging has:

```text
0 git check-ignore processes
```

and does not require a failing first `git add`.

---

# Unit 8 - Migrate `GitService.StageAndCommitAsync`

## Owner

Git mutation agent.

## File

```text
src/GrayMoon.Worker/Services/GitService.cs
```

## Replace

`GitIgnoredPathFilter.AddWithIgnoredFallbackAsync`.

Use the same classification service and the same `ShouldStage` semantics as Git Changes.

Do not create a separate filter implementation.

## Flow

```text
normalize requested paths
        |
classify with IGitIgnoreService
        |
select stageable paths
        |
git add
        |
git diff --cached --quiet
        |
git commit
```

If no path is stageable, preserve appropriate current "nothing to commit/stage" behavior.

## Locking

Review whether classification belongs inside an existing repository write lock in the caller.

The classification and mutation should be close enough to avoid a large race window.

Do not share `Repository` instances between unrelated operations.

---

# Unit 9 - Whole-repository staging review

## Owner

Git mutation agent.

## Current behavior

Whole-repository Git Changes staging uses:

```text
git add --all
```

Do not replace this with a GrayMoon filesystem walk.

Git already honors ignore semantics.

The objective is to remove GrayMoon-owned `git check-ignore`, not to duplicate Git's own correct mutation behavior.

## Acceptance

`git add --all` remains unless a separate performance measurement proves it should change.

---

# Unit 10 - Remove production `git check-ignore`

## Owner

Cleanup agent.

## File

```text
src/GrayMoon.Worker/Services/GitIgnoredPathFilter.cs
```

After all consumers migrate:

Remove production responsibilities that are no longer needed:

- `KeepNonIgnoredAsync`;
- `AddWithIgnoredFallbackAsync`;
- `check-ignore` process execution;
- NUL-delimited ignore-output parsing;
- ignored-error parsing if no longer referenced.

Delete the class completely if it has no remaining responsibility.

If a compatibility fallback remains temporarily, it must be explicit, logged, test-covered and scheduled for removal.

## Code-search gate

Repository search for:

```text
check-ignore
```

should return only:

- tests;
- historical docs;
- performance/design documentation;

not Worker production execution.

Search for:

```text
GitIgnoredPathFilter
```

should return nothing in production if the class is removed.

---

# Unit 11 - Worktree validation

## Owner

Worktree agent.

## Cases

Test ignore classification from:

- primary checkout;
- linked feature worktree;
- repository with `.git` directory;
- worktree with `.git` indirection file.

Do not manually derive Git metadata paths for ignore evaluation.

Let LibGit2Sharp open the repository from the worktree path.

## Acceptance

Same ignore rules produce the correct result in main checkout and linked worktree.

---

# Unit 12 - Concurrency and lifetime review

## Owner

Architecture agent.

## Verify

- repository object created per logical operation;
- no singleton `Repository`;
- no sharing across concurrent repository jobs;
- disposal occurs deterministically;
- project discovery can run concurrently for different repositories;
- classification does not violate existing Worker repo lock behavior;
- no new lock inversion is introduced.

Add comments only where behavior is non-obvious.

Do not add a complex repository-object pool.

---

# Unit 13 - Instrumentation

## Owner

Performance agent.

## Project discovery metrics

Log at Debug:

```text
repo
elapsedMs
directoriesVisited
directoriesPruned
candidateFiles
ignoredCandidates
returnedFiles
```

## Stage classification metrics

Log at Debug:

```text
repo
elapsedMs
requested
tracked
ignoredUntracked
stageable
```

Do not log every path by default.

---

# Unit 14 - Regression test suite

## Owner

Test agent.

Run all existing Worker tests plus new ignore tests.

Explicitly verify:

- sync project discovery;
- refresh projects;
- post-push project notification;
- selective Git Changes stage;
- Stage-and-Commit;
- stage ignored path only;
- mixed ignored/non-ignored paths;
- tracked file matching ignore pattern;
- deleted tracked file;
- nested ignored project;
- negated project rule;
- worktree.

No UX regression is acceptable.

---

# Unit 15 - Enterprise VDI performance validation

## Owner

Performance agent.

Re-run the same AVR workspace benchmark:

```text
39 repositories
16 sync workers
same enterprise VDI
```

Collect at least three quiet syncs.

Compare:

- wall clock;
- `SyncRepository` p50 / p90;
- project discovery timing;
- Git process count;
- `git check-ignore` process count;
- GitVersion duration;
- status/ref command timings;
- CPU / contention observations where available.

## Required result

```text
git check-ignore calls during normal sync = 0
```

Also validate post-push flow separately.

---

# Unit 16 - Living documentation

## Owner

Documentation agent.

Update the existing GrayMoon Git service design documents with the final architecture.

Document:

```text
Ignore decisions       -> LibGit2Sharp
Git mutations/network  -> Git CLI
```

Record:

- exact LibGit2Sharp version;
- parity test coverage;
- worktree behavior;
- performance before/after;
- any known libgit2/Git semantic differences.

Keep implementation progress checkboxes in this plan current during development.

---

# Implementation checklist

- [ ] Baseline captured
- [ ] LibGit2Sharp dependency added
- [ ] `IGitIgnoreService` created
- [ ] path normalization implemented
- [ ] Git CLI parity harness created
- [ ] nested/negation/exclude tests pass
- [ ] tracked-file staging semantics proven
- [ ] project discovery migrated
- [ ] sync path validated
- [ ] explicit project refresh validated
- [ ] push post-operation project refresh validated
- [ ] Git Changes selective stage migrated
- [ ] `GitService.StageAndCommitAsync` migrated
- [ ] whole-repo stage behavior reviewed
- [ ] production `git check-ignore` removed
- [ ] worktree tests pass
- [ ] concurrency/lifetime review complete
- [ ] performance instrumentation added
- [ ] full regression suite passes
- [ ] enterprise VDI benchmark rerun
- [ ] living architecture docs updated

---

# Final acceptance criteria

The work is complete only when all of the following are true:

1. No normal Worker production path launches `git check-ignore`.
2. Every GrayMoon-owned ignore decision uses the same LibGit2Sharp-backed service.
3. Project discovery correctly excludes nested ignored `.csproj` files.
4. Negation/re-inclusion behavior has Git parity tests.
5. `.git/info/exclude` behavior has parity coverage.
6. Worktree behavior is verified.
7. Selective staging skips ignored untracked paths without dropping tracked paths.
8. Stage-and-Commit and Git Changes use the same classification implementation.
9. Push post-operation project refresh automatically uses the new project-discovery path.
10. No unnecessary ignore dependency is added to operations such as fetch, push, merge or branch update that do not themselves need ignore classification.
11. Git mutation/network behavior stays on Git CLI in this change.
12. The enterprise VDI benchmark shows zero `check-ignore` processes and records the performance delta.
