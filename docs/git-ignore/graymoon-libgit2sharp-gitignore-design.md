# GrayMoon LibGit2Sharp Git Ignore Design

**Status:** Proposed  
**Scope:** All GrayMoon Git-ignore decisions in the Worker  
**Primary goal:** Replace subprocess-based `git check-ignore` usage with one correct, robust, fast LibGit2Sharp-backed ignore implementation and make it the single source of truth for ignore decisions.

---

## 1. Motivation

Real enterprise VDI measurements show that `git check-ignore` is disproportionately expensive.

In the measured 39-repository workspace:

- `git check-ignore` ran once per repository during project discovery;
- the clean new sync performed 39 `check-ignore` calls;
- aggregate `check-ignore` time was about 180 seconds;
- average cost was about 4.6 seconds per repository;
- the clean workspace sync still took 82.1 seconds wall clock.

This is especially expensive because every invocation starts `git.exe`, causes Git configuration and ignore files to be read, and can trigger enterprise EDR/antivirus/process inspection.

The new design removes `git check-ignore` from GrayMoon's normal ignore-decision paths.

---

## 2. Code audit

The current `main` branch has three direct production consumers of GrayMoon ignore logic.

### 2.1 Project discovery

File:

```text
src/GrayMoon.Worker/Services/CsProjFileService.cs
```

Current flow:

```text
enumerate repo-root *.csproj
        |
enumerate top-level directories
        |
GitIgnoredPathFilter.KeepNonIgnoredAsync(...)
        |
git check-ignore -z --stdin
        |
recursively enumerate *.csproj under surviving top-level directories
```

This is used directly or indirectly by:

- `SyncRepositoryCommand`;
- `RefreshRepositoryProjectsCommand`;
- `PushRepositoryCommand.BuildPostOperationNotificationAsync`;
- other flows that refresh project/package metadata through `ICsProjFileService`.

Therefore improving project discovery also removes the ignore subprocess from post-push project refresh and other project-discovery operations.

### 2.2 `GitService.StageAndCommitAsync`

File:

```text
src/GrayMoon.Worker/Services/GitService.cs
```

Current selective-stage flow:

```text
git add requested paths
        |
if Git reports "ignored by one of your .gitignore files"
        |
GitIgnoredPathFilter.AddWithIgnoredFallbackAsync(...)
        |
git check-ignore -z --stdin
        |
remove ignored paths
        |
retry git add
```

### 2.3 Git Changes selective staging

File:

```text
src/GrayMoon.Worker/Services/GitChanges/GitCliRepositoryGitChangesService.cs
```

`StageAsync` uses the same `GitIgnoredPathFilter.AddWithIgnoredFallbackAsync(...)` behavior for path-scoped staging.

### 2.4 Existing helper

File:

```text
src/GrayMoon.Worker/Services/GitIgnoredPathFilter.cs
```

It currently owns:

- parsing the `git add` ignored-path error;
- launching `git check-ignore`;
- parsing NUL-delimited Git output;
- filtering ignored paths;
- retry orchestration.

This class should no longer own ignore evaluation after this work.

---

## 3. Important indirect flows

Some operations do not directly call `check-ignore` but still pay for it through project discovery.

### Push

`PushRepositoryCommand` performs its network push and then builds a post-operation sync notification.

When project discovery is enabled:

```csharp
var projects = await csProjFileService.FindAsync(repoPath, CancellationToken.None);
```

Therefore the current push flow can trigger `git check-ignore` after the push.

The new shared ignore implementation removes this cost automatically.

### Sync

`SyncRepositoryCommand` starts project discovery concurrently with GitVersion and ref reads.

Replacing project-discovery `check-ignore` removes one Git process per repository and reduces filesystem/process contention during the sync critical section.

### Explicit project refresh

`RefreshRepositoryProjectsCommand` calls the same `CsProjFileService` and therefore benefits directly.

### Update / merge operations

`UpdateBranchFromDefaultCommand` does not directly perform an ignore decision.

It may cause later hooks/sync/project refresh behavior that reaches project discovery. That downstream path must use the new shared implementation.

No fake ignore dependency should be introduced into operations that do not need ignore semantics.

---

## 4. Current correctness problem

The existing project-discovery implementation checks only top-level directories.

Example:

```text
Repo/
  src/
    Product/
      Product.csproj
    Generated/
      Generated.csproj
```

With:

```gitignore
src/Generated/
```

GrayMoon asks Git whether:

```text
src
```

is ignored.

It is not.

GrayMoon then recursively scans all descendants of `src`, which can discover:

```text
src/Generated/Generated.csproj
```

even though Git considers it ignored.

The intended rule is:

> GrayMoon project discovery must include only `.csproj` files that Git considers non-ignored.

The new implementation must apply ignore semantics at every directory boundary and to candidate files.

---

## 5. Design decision

Introduce one shared abstraction:

```csharp
IGitIgnoreService
```

Recommended contract:

```csharp
public interface IGitIgnoreService
{
    bool IsIgnored(string repositoryPath, string path);

    IReadOnlyList<string> KeepNonIgnored(
        string repositoryPath,
        IReadOnlyList<string> paths);

    IReadOnlyList<string> FindNonIgnoredFiles(
        string repositoryPath,
        string searchPattern,
        CancellationToken cancellationToken = default);
}
```

If asynchronous API shape is preferred for consistency, the public interface may expose `Task`, but ignore evaluation itself is local and synchronous in LibGit2Sharp.

The essential design requirement is not the exact signatures. It is:

> Every GrayMoon ignore decision goes through the same service.

Implementation:

```text
LibGit2SharpGitIgnoreService
```

---

## 6. LibGit2Sharp behavior

LibGit2Sharp exposes:

```csharp
repository.Ignore.IsPathIgnored(relativePath)
```

The API accepts repository-relative paths and delegates ignore evaluation to libgit2.

The upstream LibGit2Sharp test suite includes tests for:

- direct ignored-path checks;
- nested `.gitignore`;
- normal repository ignore rules;
- slash-separated relative paths.

GrayMoon must validate the exact semantics it relies upon with its own parity tests before removing the CLI implementation.

---

## 7. Repository lifetime

A key performance rule:

> Open a LibGit2Sharp `Repository` once for a logical ignore operation, not once per path.

Bad:

```text
path A -> open repo -> check -> dispose
path B -> open repo -> check -> dispose
path C -> open repo -> check -> dispose
```

Correct:

```text
open repo
  check A
  check B
  check C
dispose repo
```

For tree discovery:

```text
open repo
  walk complete project tree
dispose repo
```

For selective staging:

```text
open repo
  classify all requested paths
dispose repo
```

---

## 8. Path normalization

All ignore decisions must use one normalization helper.

Recommended semantics:

```csharp
string ToGitRelativePath(string repositoryRoot, string path)
```

Rules:

1. accept either absolute paths under the repository or repository-relative paths;
2. reject paths that escape the repository root;
3. normalize separators to `/`;
4. remove harmless leading `./`;
5. never pass an empty repository-root path into `IsPathIgnored`;
6. preserve path content otherwise;
7. do not invent GrayMoon-specific wildcard logic.

Example:

```text
C:\Work\Repo\src\App\App.csproj
```

becomes:

```text
src/App/App.csproj
```

---

## 9. Project discovery design

Replace the existing top-level `check-ignore` plus `SearchOption.AllDirectories` algorithm.

New algorithm:

```text
open Repository once
        |
walk repository tree
        |
for each directory
    |
    +-- skip .git metadata
    |
    +-- evaluate ignore state
    |
    +-- prune when safe
        |
for each *.csproj candidate
    |
    +-- evaluate candidate path
    |
    +-- include only when non-ignored
```

Candidate files must always be checked individually.

### Why explicit traversal is better

The current code recursively enumerates an entire surviving top-level subtree.

The new approach can avoid walking ignored output trees entirely.

That reduces:

- directory enumeration;
- file metadata reads;
- EDR/antivirus inspection;
- useless `.csproj` parsing work;
- total VDI filesystem pressure.

---

## 10. Negation and safe pruning

Git ignore negation makes directory pruning subtle.

Example:

```gitignore
generated/*
!generated/important/
```

A naive implementation that prunes `generated` too early could miss a re-included descendant.

Therefore directory pruning must be verified against Git/libgit2 semantics.

Implementation rule:

> Do not assume that `IsPathIgnored(directory)` alone is sufficient evidence that every descendant can be skipped unless parity tests prove this for the exact traversal representation.

Two acceptable implementations:

### Option A: Conservative first implementation

- enumerate directories;
- evaluate candidate `.csproj` files individually;
- only prune directory patterns whose semantics are proven safe by parity tests.

This favors correctness.

### Option B: Libgit2-proven pruning

If tests demonstrate that `IsPathIgnored(relativeDirectoryPath)` correctly represents whether traversal of that path can be omitted under Git's parent-ignore/negation semantics, prune ignored directories.

This favors maximum performance.

The implementation plan must start with parity tests before enabling aggressive pruning.

---

## 11. Selective staging design

Current behavior first lets `git add` fail, then starts a second Git process to discover ignored paths, then retries.

The new flow should pre-filter with LibGit2Sharp:

```text
requested paths
        |
IGitIgnoreService.KeepNonIgnored(...)
        |
   +----+----+
 ignored    allowed
               |
            git add
```

This removes the normal `git add failure -> check-ignore -> git add retry` sequence.

### Required semantics

For selective staging:

- ignored untracked paths should be dropped;
- non-ignored paths continue to `git add`;
- tracked files must not be accidentally suppressed because an ignore rule now matches them;
- deleted tracked files must remain stageable;
- force-add behavior must not be introduced;
- the service must preserve the current GrayMoon intent: skip truly ignored paths rather than use `git add -f`.

This tracked-file point is critical.

Git ignore rules primarily affect untracked files. A file already tracked by Git can match an ignore pattern and still require staging.

Therefore `KeepNonIgnored` used for staging cannot be a simplistic:

```csharp
paths.Where(p => !repo.Ignore.IsPathIgnored(p))
```

The staging classifier must distinguish tracked/indexed paths from untracked ignored paths.

Recommended result model:

```csharp
public sealed record GitIgnoreClassification(
    string Path,
    bool IsIgnored,
    bool IsTracked,
    bool ShouldStage);
```

For staging:

```text
tracked -> stage
untracked + non-ignored -> stage
untracked + ignored -> skip
```

LibGit2Sharp index/status information may be used for this classification if needed.

---

## 12. Whole-repository staging

Current whole-repository staging uses:

```text
git add --all
```

Git already applies ignore semantics correctly.

Do not add a pre-scan of every repository file merely to route it through `IGitIgnoreService`.

The principle "all ignore implementations use the new approach" means:

- GrayMoon code must not independently run `git check-ignore` or implement separate ignore parsing;
- native Git operations that inherently honor ignores, such as `git add --all`, may continue to let Git do so.

Do not replace a single efficient Git mutation with an expensive whole-tree GrayMoon pre-classification.

---

## 13. Stage-and-commit behavior

`GitService.StageAndCommitAsync` currently delegates selective add retry behavior to `GitIgnoredPathFilter`.

Change it to:

```text
normalize requested paths
        |
IGitIgnoreService classify/filter once
        |
if no stageable paths:
    return successful "nothing staged" behavior as appropriate
        |
git add only stageable paths
        |
verify staged diff
        |
commit
```

The Git mutation remains Git CLI.

Only ignore classification moves to LibGit2Sharp.

This keeps the broader architecture conservative:

```text
local metadata/read classification -> LibGit2Sharp
mutating operation                -> git.exe
```

---

## 14. Git Changes staging behavior

`GitCliRepositoryGitChangesService.StageAsync` must use exactly the same classification path as `GitService.StageAndCommitAsync`.

There must not be:

- one ignore implementation for Git Changes;
- another for Stage-and-Commit;
- another for project discovery.

All three depend on `IGitIgnoreService`.

---

## 15. `GitIgnoredPathFilter` disposition

After migration, the existing helper should no longer be the normal ignore engine.

Recommended end state:

- remove `KeepNonIgnoredAsync`;
- remove direct `git check-ignore` invocation;
- remove NUL-delimited `check-ignore` parsing;
- remove `AddWithIgnoredFallbackAsync` once both staging paths use the new service;
- remove `IgnoredByGitignoreMarker` parsing if no longer required.

A temporary diagnostic fallback may be retained during rollout, but it must not silently become a permanent second implementation.

If retained temporarily, it should:

- be clearly marked compatibility/diagnostic only;
- log when reached;
- not run during successful normal flows;
- have a TODO/removal gate tied to parity tests.

Final target:

```text
zero normal GrayMoon git check-ignore processes
```

---

## 16. Error handling

The ignore service is local infrastructure and must fail predictably.

### Invalid repository

Return a controlled error/result rather than allowing arbitrary LibGit2Sharp exceptions to leak into UI code.

### Path outside repository

Reject it.

Never classify:

```text
..\OtherRepo\file.csproj
```

against the current repository.

### Repository unavailable during operation

Operations must tolerate expected races such as repository removal or worktree cleanup.

Behavior should match the caller:

- discovery can fail safely and return no discovered files while logging;
- staging should fail explicitly rather than silently stage an unknown subset.

### Inaccessible directories

Discovery should skip inaccessible subtrees, log at debug/warning as appropriate, and continue with other accessible paths.

### Cancellation

Long tree discovery must check cancellation while traversing.

---

## 17. Worktrees

The service must be tested with Git worktrees.

Do not assume:

```text
repoPath/.git
```

is always a directory.

In linked worktrees `.git` can be a file pointing to the common Git directory.

LibGit2Sharp repository discovery/open behavior should be used rather than manually assuming Git directory structure.

Tests must cover:

- normal repository;
- linked worktree;
- workspace/feature worktree where applicable.

---

## 18. Threading and concurrency

Do not share one LibGit2Sharp `Repository` instance across unrelated concurrent repository operations.

Recommended lifetime:

```text
one logical operation
    ->
one Repository instance
    ->
dispose
```

Different repositories may be evaluated concurrently.

For the same repository, existing GrayMoon repository locking rules must be respected when an ignore classification is part of a mutation sequence.

Selective stage/commit should keep classification sufficiently close to the Git mutation to minimize races.

Where the caller already holds a repository write lock, classification should run inside that lock.

---

## 19. Caching

Do not introduce cross-operation ignore-result caching in the first implementation.

Ignore state can change when:

- `.gitignore` changes;
- nested `.gitignore` changes;
- `.git/info/exclude` changes;
- user/global ignore configuration changes;
- tracked/untracked state changes.

The first implementation should prefer correctness and still gain most of the performance benefit from eliminating process launches.

Possible future optimization:

- cache only within a single logical discovery/staging operation;
- later investigate invalidation-aware caching if profiling proves necessary.

---

## 20. Logging and instrumentation

Add structured timings around the new service.

For project discovery:

```text
Git ignore/project discovery:
repo
elapsed
directoriesVisited
directoriesPruned
candidateFiles
ignoredCandidates
projectsReturned
```

For selective staging:

```text
Git ignore classification:
repo
requestedPaths
trackedPaths
ignoredUntrackedPaths
stageablePaths
elapsed
```

Do not log excessive individual file paths at information level.

---

## 21. Correctness parity strategy

Before deleting `git check-ignore`, create integration tests comparing LibGit2Sharp results to real Git.

Test fixture should:

1. create temporary repository;
2. write ignore rules;
3. create files/directories;
4. ask:
   ```text
   git check-ignore
   ```
5. ask:
   ```csharp
   IGitIgnoreService
   ```
6. assert equivalent classification.

This CLI use is test-only.

The production Worker must not launch `git check-ignore`.

---

## 22. Required parity cases

At minimum:

### Basic

```gitignore
bin/
obj/
*.tmp
```

### Nested `.gitignore`

```text
src/.gitignore
generated/
```

### Root anchored

```gitignore
/generated/
```

### Wildcards

```gitignore
**/generated/
*.generated.csproj
```

### Negation

```gitignore
generated/*
!generated/keep/
```

### Nested negation

Test parent-directory ignore behavior carefully.

### `.git/info/exclude`

Must match Git behavior.

### Global excludes

If supported by the current libgit2/LibGit2Sharp environment, verify parity.

### Spaces

```text
Folder With Spaces/App.csproj
```

### Unicode

Include non-ASCII repository-relative paths.

### Windows separators

Input `\` must normalize correctly.

### Case behavior

Run platform-appropriate tests without hard-coding incorrect cross-platform assumptions.

### Tracked file matching ignore pattern

A tracked file that now matches `.gitignore` must still be stageable.

### Deleted tracked file

Must remain stageable.

### Worktree

Ignore behavior must work from linked worktree paths.

---

## 23. Performance acceptance criteria

Primary:

```text
production git check-ignore process count = 0
```

for:

- workspace sync/project discovery;
- explicit project refresh;
- post-push project discovery;
- selective Stage-and-Commit;
- Git Changes selective Stage.

Project discovery should perform one LibGit2Sharp repository open per repository discovery operation.

Selective staging should perform one LibGit2Sharp repository open per classification operation.

On the enterprise VDI, re-run the same 39-repository benchmark and compare:

- workspace wall time;
- per-repository project scan time;
- total Git subprocess count;
- `git check-ignore` count;
- GitVersion duration;
- process contention.

Expected `git check-ignore` count:

```text
0
```

---

## 24. Architectural boundary

This work does not convert Git mutations or network operations to LibGit2Sharp.

Target boundary:

```text
LibGit2Sharp
  |
  +-- ignore classification
  +-- project-discovery ignore decisions
  +-- tracked/untracked classification where needed for ignore correctness

Git CLI
  |
  +-- add
  +-- commit
  +-- fetch
  +-- pull
  +-- push
  +-- merge
  +-- checkout
  +-- reset
  +-- clean
```

The goal is a focused, low-risk performance improvement with one authoritative ignore implementation.

---

## 25. Definition of done

The design is complete when:

- every GrayMoon-owned ignore decision uses `IGitIgnoreService`;
- project discovery no longer launches `git check-ignore`;
- selective staging no longer launches `git check-ignore`;
- post-push project discovery inherits the new path;
- `GitIgnoredPathFilter` is removed or reduced to no production ignore evaluation;
- nested ignored `.csproj` files are correctly excluded;
- tracked files remain stageable even when matching ignore patterns;
- worktrees behave correctly;
- parity tests pass against Git CLI;
- production `git check-ignore` count is zero;
- the enterprise VDI benchmark is rerun and documented.
