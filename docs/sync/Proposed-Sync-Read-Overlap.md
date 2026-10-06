# Proposed: overlap read-only work inside one repository sync

**Status:** proposal with an implementation plan. No code change is described as done.  
**Date:** 2026-10-06 (revised the same day after a code review)

Workspace sync already runs repositories side by side, up to `Workspace:MaxParallelOperations` (16). Each repository is one `SyncRepository` command (`src/GrayMoon.Worker/Commands/SyncRepositoryCommand.cs`). Inside that command every step is awaited one after another, and the per-repository write lock in `GitProcessRunner` keeps even the two pairs that are started together from overlapping.

The long part of one repository is `git fetch origin --prune --tags`, then GitVersion (`/nofetch`). Roughly fifteen to twenty short git processes then run in a line behind GitVersion, although none of them needs GitVersion's output, and only one group needs the branch name GitVersion produces.

## Why

A sync feels slow because each repository pays for its fetch and its version calculation, and then pays again for a queue of short git processes. On Windows each process costs tens of milliseconds just to start, so the queue adds up to somewhere around half a second to a second and a half per repository.

This is everything that runs after GitVersion today, in order, with the git commands each helper actually spawns:

| Step | Helper | Git commands | Needs branch name |
| --- | --- | --- | --- |
| Branch fallback | `ResolveBranchAsync` → `GetCurrentBranchNameAsync` | `branch --show-current` (only when GitVersion gave no branch) | no |
| Checked-out tag | `GetCheckedOutTagAsync` | `symbolic-ref -q HEAD`, then `describe --tags --exact-match` | no |
| Tag list | `GetTagsAsync` | `tag --sort=-creatordate` | no |
| Hooks | `WriteSyncHooksAsync` | 3 to 4 read-intent `rev-parse`/`config` calls, then file writes under the common `hooks` dir | needs branch **or** tag |
| Default branch | `GetDefaultBranchOriginRefAsync` | `symbolic-ref -q refs/remotes/origin/HEAD`, 1 to 3 `rev-parse --verify` | no |
| Divergence base | `SetDivergenceBaseBranchAsync` | `rev-parse --git-dir` (already read intent), then writes or deletes `<git-dir>/graymoon-divergence-base` | no |
| Upstream counts | `ProbeCommitCountsAsync` | `for-each-ref %(upstream:short)`, `rev-parse --verify`, maybe a divergence-base read, `rev-list --left-right --count` | **yes** |
| Default-branch counts | `GetCommitCountsVsDefaultAsync` | `rev-list --left-right --count <divergenceRef>...HEAD` | no (uses `HEAD`) |
| Branch lists | `GetLocalBranchesAsync`, `GetRemoteBranchesFromRefsAsync` | two `for-each-ref` | no |
| Projects | `ICsProjFileService.FindAsync` | none; file system only | no |

None of the git commands above update refs or the index. Two steps write files, and neither file is read by GitVersion or by the ref reads: the divergence-base file, which `ProbeCommitCountsAsync` reads back, and the hook scripts.

Only `ProbeCommitCountsAsync` needs the branch name. The default-branch counts compare `HEAD` against the divergence ref, so they do not need it either. (The first draft of this proposal grouped both count calls as waiting on the branch, and it left out the default-branch lookup and the divergence-base write.)

`SyncRepositoryCommand` already starts the two count calls together and the two branch-list calls together. Every helper uses the default `GitLockIntent.Write`, and the lock is a `SemaphoreSlim(1, 1)`, so both pairs still run one at a time.

Running these reads while GitVersion runs shortens the repository without adding a second writer on the same repository. The fetch and GitVersion take as long as they do today.

## How

Change `SyncRepositoryCommand`. Give the helpers it calls an optional `GitLockIntent intent = GitLockIntent.Write` parameter, so the sync path can ask for `Read` while every other caller keeps the write lock it has today.

### Order that stays

1. Clone, when the directory is missing, still finishes before anything else.
2. `git fetch origin --prune --tags` still finishes before GitVersion and before any ref read. GitVersion stays `/nofetch`, so it must see the fetch. Ordering here comes from `await`, not from the lock, because read-intent calls never touch the lock.
3. A failed fetch still returns immediately, with no version and with projects left unset. Nothing else starts before the fetch succeeds, so a failed fetch starts no GitVersion, no reads, and no project scan.
4. The branch name is still chosen after GitVersion. GitVersion's branch wins when it returns one; otherwise the name comes from `git branch --show-current`. That command now runs early, alongside GitVersion, and its answer is used only as the fallback.
5. A tag checkout still clears the branch and skips the upstream counts. The default-branch counts still run for a tag checkout when a divergence ref exists, as they do today.
6. `SetDivergenceBaseBranchAsync` still finishes before `ProbeCommitCountsAsync`, because the no-upstream path reads that file back.
7. Hook install still waits until the checkout is known to be a real branch or a tag.
8. Project discovery still runs only when the workspace profile asks for it. A skipped discovery still leaves projects unset, so the app does not treat "nobody looked" as "this repository has no projects." An exception from the scan still fails the command, as it does today.

### What overlaps

After the fetch succeeds, three things start together:

- GitVersion, which stays on the write lock (the runner locks `dotnet-gitversion` and `dotnet gitversion` by file name).
- One **read lane**: a single async method that runs, one after another and all as read intent, the current-branch read, the tag check, the tag list, both branch lists, the default-branch lookup, the divergence-base write, and the default-branch counts.
- The `.csproj` scan, when the profile discovers projects, wrapped in `Task.Run` so its directory walk does not run on the calling thread.

The read lane is one sequence, not a fan-out. GitVersion usually takes seconds and the lane is well under a second, so one lane already hides almost all of it while adding only one extra process per repository. With 16 repositories in flight, fanning out every read would add around a hundred concurrent git processes, which on Windows (process start cost, antivirus scanning) can make the whole sync slower. Split the lane only if measurement says so (see the last step of the plan).

When GitVersion and the read lane have both finished, the branch name is chosen, and then the upstream counts (read intent) and hook install run together. The project scan is awaited last, when the response is built.

Read-intent calls add `--no-optional-locks` in front of the subcommand. That is the contract written on `GitLockIntent`, and status and diff already follow it. None of these subcommands refresh the index, so the flag changes nothing about their output.

### What stays on the write lock

Fetch, clone, checkout, merge, commit, push, safe-directory setup, and GitVersion stay one writer per repository. Every other caller of the changed helpers keeps the write intent, because the new parameter defaults to `Write`.

## Risks and how they are handled

**Reads can now run alongside another command's writer on the same repository.** Read intent skips the lock entirely, so a sync read can run while, say, a checkout started from the UI holds the lock. This is smaller than it sounds. The lock is taken per git process, not per command, so today a checkout can already slip in between two of sync's reads and give it a mixed picture. What is new is a read running during the write itself. Git updates each ref by writing a lock file and renaming it, so a read sees either the old value or the new one, never a half-written ref. The post-checkout hook and the next sync correct a mixed picture, which is the same trade status and diff already make.

**GitVersion might write refs.** Sync calls GitVersion without `/nonormalize`. GitVersion can normalize a repository (create local branches from remote ones, or move a detached `HEAD`) when it thinks it is running on a build server. If that happens on a developer machine, a branch list or `HEAD` read taken during GitVersion could differ from one taken after it. Step 0 of the plan checks this before any code changes, and says what to do if it fails.

**Overlay output interleaves.** Live command output goes into an unbounded `Channel` in `JobBackgroundService`, which is safe to write from several processes at once. Lines from GitVersion and the read lane will interleave in the overlay terminal. That is cosmetic.

**Orphaned project scan on an exception.** If GitVersion or the read lane throws, the command fails and the scan task is never awaited. An unobserved task exception does not crash a .NET process, and the command's result is discarded anyway. No extra handling is needed.

## Why this does not change existing behavior

The response shape stays the same. Success still carries version, branch, tag, tags, both branch lists, both count pairs, default branch, upstream flags, and projects under the same rules. Failure on fetch still carries the fetch error and leaves the optional groups absent. The path with no repository directory does not change.

Cross-repository fan-out stays capped by `Workspace:MaxParallelOperations`. This proposal does not raise that cap and does not turn the batch into an unbounded `Task.WhenAll`.

Persistence stays a sequential pass over one `DbContext` after the worker results return. That ordering is for EF, and this proposal does not change it.

---

## Implementation plan

Written so each step can be done and checked on its own. Do the steps in order. Do not skip step 0 or step 1.

### Rules for the whole change

- Do **not** change the default intent of any method. Every new `intent` parameter defaults to `GitLockIntent.Write`.
- Do **not** edit `GitProcessRunner.cs`, `CommandLineService.cs`, `CsProjFileService.cs`, or any command other than `SyncRepositoryCommand`. `RefreshRepositoryVersionCommand`, `CommitSyncRepositoryCommand`, `CheckoutHookSyncCommand` and the others keep calling the helpers with no intent argument.
- Do **not** change `FetchAsync`, `CloneAsync`, `AddSafeDirectoryAsync`, `WriteSyncHooksAsync`, or `GetVersionAsync`.
- Do **not** change `Workspace:MaxParallelOperations` or the app-side persistence.
- Do **not** split the read lane into several parallel tasks in the first pass.
- Keep the `try { ... } catch { }` around the two branch-list calls. Branch lists are non-critical today and must stay that way.

### Step 0: check that GitVersion does not write refs (no code)

Run this once on the machine that runs the Worker, with the GitVersion version the Worker uses. Use a clone that has several remote branches with no local branch, and at least one tag.

```powershell
function Snap { git for-each-ref --format='%(refname) %(objectname)'; git symbolic-ref -q HEAD; git rev-parse HEAD }
Snap > before.txt
dotnet-gitversion /output json /nofetch /verbosity quiet | Out-Null   # or: dotnet gitversion ... when the repo has a tool manifest
Snap > after.txt
Compare-Object (Get-Content before.txt) (Get-Content after.txt)
```

Run it twice: once on a branch, and once after `git checkout --detach <some-tag>`.

- No differences in either run: continue with the plan as written.
- `refs/heads/*` changed: in step 3, move `GetLocalBranchesAsync` out of the read lane and run it after GitVersion.
- `HEAD` changed: in step 3, move everything that reads `HEAD` (current branch, checked-out tag, default-branch counts) after GitVersion as well. The tag list, remote branch list, default-branch lookup, and divergence-base write can stay in the lane.

Write the result into the PR description.

### Step 1: baseline timing (no code)

Pick a workspace with at least ten repositories and versioning switched on. Sync it three times and record, from the Worker log, the Information line `ResponseCommand {RequestId} completed (SyncRepository) in {ElapsedMs}ms` for every repository, plus the total time the sync took in the UI. Keep the numbers for step 5.

### Step 2: add the `intent` parameter to the helpers

Files: `src/GrayMoon.Worker/Abstractions/IGitService.cs`, `src/GrayMoon.Worker/Services/GitService.cs`, `src/GrayMoon.Worker/Services/GitVersionBranch.cs`, and the two test fakes listed below.

2a. Add one private helper to `GitService`. Pick a name that is not already used in the file:

```csharp
private Task<(int ExitCode, string? Stdout, string? Stderr)> RunGitWithIntentAsync(
    string arguments,
    string repoPath,
    CancellationToken ct,
    GitLockIntent intent,
    bool? streamStderrAsStdout = null,
    bool? mirrorFailureOutputAsStderr = null)
    => runner.RunAsync(
        "git",
        intent == GitLockIntent.Read ? "--no-optional-locks " + arguments : arguments,
        repoPath,
        ct,
        streamStderrAsStdout,
        mirrorFailureOutputAsStderr,
        intent);
```

The runner's timeout and logging code already skip leading global flags such as `--no-optional-locks`, and none of these calls are network calls, so they keep the default timeout.

2b. Add `GitLockIntent intent = GitLockIntent.Write` as the **last** parameter of these public methods, on both `IGitService` and `GitService`. Inside each one, replace `runner.RunAsync("git", "<args>", repoPath, ct, ...)` with `RunGitWithIntentAsync("<args>", repoPath, ct, intent, ...)`, keeping any `streamStderrAsStdout` and `mirrorFailureOutputAsStderr` arguments exactly as they are:

- `GetCurrentBranchNameAsync`
- `GetCheckedOutTagAsync` (both git calls)
- `GetTagsAsync`
- `GetLocalBranchesAsync`
- `GetRemoteBranchesFromRefsAsync`
- `GetDefaultBranchOriginRefAsync` (pass `intent` through to `GetDefaultBranchAsync`)
- `GetCommitCountsVsDefaultAsync` (also pass `intent` to its `GetDefaultBranchAsync` fallback)
- `ProbeCommitCountsAsync` (new parameter goes after `skipUpstreamCheck`)

`IGitService.cs` needs `using GrayMoon.Worker.Services;` for `GitLockIntent`.

2c. Thread `intent` through the private helpers those methods call. Add `GitLockIntent intent = GitLockIntent.Write` as the last parameter and pass it down:

- `GetDefaultBranchAsync` (its `symbolic-ref` call and its `RefExistsAsync` calls)
- `RefExistsAsync`
- `GetUpstreamRefAsync`
- `ResolveNoUpstreamCompareRefAsync` (its `RefExistsAsync` and `GetDefaultBranchAsync` calls)
- `CountAheadOfCompareRefAsync` (its `rev-list` call). It already takes several parameters; put `intent` last and pass it by name at the call sites in `ProbeCommitCountsAsync`.

Leave `GetDivergenceBaseBranchAsync` and `ResolveDivergenceBaseFilePathAsync` alone; they already use read intent.

2d. In `GitVersionBranch.cs`, pull the choice into a pure function and make `ResolveBranchAsync` use it, so behavior stays identical (an empty `BranchName` still falls through to git, not to `EscapedBranchName`):

```csharp
public static string? Choose(GitVersionResult? versionResult, string? gitBranch)
{
    var fromGitVersion = versionResult?.BranchName ?? versionResult?.EscapedBranchName;
    return !string.IsNullOrWhiteSpace(fromGitVersion) ? fromGitVersion : gitBranch;
}
```

`ResolveBranchAsync` keeps its signature. It returns the GitVersion branch when there is one, and otherwise `Choose(null, await git.GetCurrentBranchNameAsync(repoPath, cancellationToken))`. It must still skip the git call when GitVersion gave a branch.

2e. Two test fakes implement `IGitService` and will stop compiling: `src/GrayMoon.Worker.Tests/ReturnToDefaultBranchCommandTests.cs` and `src/GrayMoon.Worker.Tests/DeleteBranchCommandTests.cs`. Add the same trailing `GitLockIntent intent = GitLockIntent.Write` parameter to each changed member there. Do not change what the fakes do.

Check: `dotnet build` succeeds, and `dotnet test src/GrayMoon.Worker.Tests` passes with no change to `SyncRepositoryCommand` yet.

### Step 3: restructure `SyncRepositoryCommand`

File: `src/GrayMoon.Worker/Commands/SyncRepositoryCommand.cs`. Change only the part after `if (!fetchOk) { return ...; }` and before `return new SyncRepositoryResponse { Success = true, ... }` inside the `if (git.DirectoryExists(repoPath))` block. Everything before it (clone, safe directory, fetch, fetch-failure return) and the final no-directory return stay as they are.

3a. Add a private record and a private method to the class. Apply the step 0 result here if it moved anything out of the lane.

```csharp
private sealed record SyncRefs(
    string? CurrentBranch,
    string? CurrentTag,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string>? LocalBranches,
    IReadOnlyList<string>? RemoteBranches,
    string? DefaultRef,
    int? DefaultBehind,
    int? DefaultAhead);

/// <summary>
/// Ref reads that need neither GitVersion's output nor the branch name, run as read intent so they can
/// overlap GitVersion, which holds the repository write lock. Must start only after fetch has finished.
/// </summary>
private async Task<SyncRefs> ReadRefsAsync(string repoPath, string? divergenceBaseBranch, CancellationToken ct)
{
    const GitLockIntent read = GitLockIntent.Read;

    var currentBranch = await git.GetCurrentBranchNameAsync(repoPath, ct, read);
    var currentTag = await git.GetCheckedOutTagAsync(repoPath, ct, read);
    var tags = await git.GetTagsAsync(repoPath, ct, read);

    IReadOnlyList<string>? localBranches = null;
    IReadOnlyList<string>? remoteBranches = null;
    try
    {
        localBranches = await git.GetLocalBranchesAsync(repoPath, ct, read);
        remoteBranches = await git.GetRemoteBranchesFromRefsAsync(repoPath, ct, read);
    }
    catch
    {
        // If branch fetching fails, continue without branches (non-critical)
    }

    var defaultRef = await git.GetDefaultBranchOriginRefAsync(repoPath, ct, read);
    await git.SetDivergenceBaseBranchAsync(repoPath, divergenceBaseBranch, ct);
    var divergenceRef = git.ToOriginBranchRef(divergenceBaseBranch) ?? defaultRef;

    int? defaultBehind = null;
    int? defaultAhead = null;
    if (divergenceRef != null)
        (defaultBehind, defaultAhead, _) = await git.GetCommitCountsVsDefaultAsync(repoPath, divergenceRef, ct, read);

    return new SyncRefs(currentBranch, currentTag, tags, localBranches, remoteBranches, defaultRef, defaultBehind, defaultAhead);
}
```

Calling `GetCommitCountsVsDefaultAsync` only when `divergenceRef` is not null matches today: today's branch path passes a null ref, the helper then looks up the default branch itself, finds none (the lookup above already returned null), and returns nulls.

3b. Replace the middle of `ExecuteAsync` with this shape:

```csharp
var versionTask = versionProviderFactory
    .Create(capabilities)
    .GetVersionAsync(repoPath, RepositoryVersionOptions.Default, cancellationToken);
var refsTask = ReadRefsAsync(repoPath, request.DivergenceBaseBranch, cancellationToken);
var projectsTask = capabilities.ShouldDiscoverProjects
    ? ScanProjectsAsync(repoPath, cancellationToken)
    : Task.FromResult<IReadOnlyList<CsProjFileInfo>?>(null);

await Task.WhenAll(versionTask, refsTask);
var versionResult = await versionTask;
var refs = await refsTask;

versionError = versionResult.Error;
if (versionResult.Probed)
    version = versionResult.VersionOrPlaceholder;

branch = GitVersionBranch.Choose(versionResult.Result, refs.CurrentBranch) ?? "-";
if (refs.CurrentTag != null)
    branch = "-";

var hooksTask = branch != "-" || refs.CurrentTag != null
    ? git.WriteSyncHooksAsync(repoPath, workspaceId, repositoryId, cancellationToken)
    : Task.CompletedTask;
var countsTask = branch != "-"
    ? git.ProbeCommitCountsAsync(repoPath, branch, refs.DefaultRef, cancellationToken, intent: GitLockIntent.Read)
    : null;

await hooksTask;
if (countsTask != null)
{
    var counts = await countsTask;
    outgoingCommits = counts.Outgoing;
    incomingCommits = counts.Incoming;
    hasUpstream = counts.HasUpstream;
    upstreamProbed = counts.UpstreamProbed;
}

projects = await projectsTask;

string? defaultBranch = refs.DefaultRef != null
    ? (refs.DefaultRef.StartsWith("origin/", StringComparison.Ordinal)
        ? refs.DefaultRef["origin/".Length..]
        : refs.DefaultRef)
    : null;
```

and add:

```csharp
private Task<IReadOnlyList<CsProjFileInfo>?> ScanProjectsAsync(string repoPath, CancellationToken ct)
    => Task.Run<IReadOnlyList<CsProjFileInfo>?>(async () => await csProjFileService.FindAsync(repoPath, ct), ct);
```

3c. Build the success response from `refs` (`Tag = refs.CurrentTag`, `Tags = refs.Tags`, `LocalBranches = refs.LocalBranches`, `RemoteBranches = refs.RemoteBranches`, `DefaultBranchBehind = refs.DefaultBehind`, `DefaultBranchAhead = refs.DefaultAhead`) and the locals above. Every property that is set today must still be set, and no new property is added.

3d. Keep the useful comments from the old code (why fetch comes first, why GitVersion's branch wins, why the branch is a git fact, why tag checkouts need hooks, why projects stay null when not discovered, why sync reports the upstream flag), moved next to the line they explain. Delete the comment that says counts and divergence "run in parallel", because it was never true. Update the class `<summary>` if it no longer matches.

Check: `dotnet test src/GrayMoon.Worker.Tests` passes, including `SyncRepositoryCapabilitiesTests`, `SyncRepositoryGitVersionFailureTests`, `SyncRepositoryHookInstallTests`, and `GitServiceCommitCountProbeTests`.

### Step 4: add tests

New file `src/GrayMoon.Worker.Tests/SyncRepositoryReadOverlapTests.cs`. Copy the setup, temp-directory cleanup, and `RunGitAsync` helper from `SyncRepositoryCapabilitiesTests` (real git, a bare remote, a clone). Keep the `GitProcessRunner` in a field; the test project can see internals.

1. **Read intent ignores the write lock.** Inside `runner.WithRepoWriteLockAsync(repoPath, ...)`, call `GetTagsAsync`, `GetLocalBranchesAsync`, `GetCheckedOutTagAsync`, and `ProbeCommitCountsAsync` with `GitLockIntent.Read`, and assert each finishes within 10 seconds using `Task.WhenAny(task, Task.Delay(...))`. This proves the parameter actually reaches the runner. (The write-intent versions would hang here, so do not call them inside the lock.)
2. **Read and write intent agree.** For each changed helper, call it once with no intent and once with `GitLockIntent.Read` on the same repository, and assert equal results.
3. **Tag checkout.** Tag a commit, push the tag, `git checkout --detach <tag>`, sync. Assert `Success`, `Branch == "-"`, `Tag == <tag>`, `OutgoingCommits` and `IncomingCommits` null, and the `post-checkout` hook file exists.
4. **Failed fetch.** Point `origin` at a path that does not exist, sync with both capabilities on. Assert `Success == false`, `GitFetchError` not null, `Version == "-"`, `Projects == null`, and that the version and project doubles were each called zero times. (The fetch retry pipeline may make this test take a few seconds.)
5. **Divergence base is written before the upstream counts.** On a local branch with no upstream that is one commit ahead of a second local branch, sync with `DivergenceBaseBranch` set to that second branch. Assert `OutgoingCommits == 1`. Without the ordering in step 3 this would count against the default branch instead.

Check: the new tests pass, and the full `dotnet test src/GrayMoon.Worker.Tests` run passes.

### Step 5: measure again

Repeat step 1 on the same workspace. Report both sets of numbers in the PR.

- The median per-repository `SyncRepository` time should drop by roughly the length of the read queue (several hundred milliseconds or more on Windows), and the total sync time should not get worse.
- If the total sync time gets worse, the extra processes are the likely cause. Look for CPU at 100% or antivirus activity during the sync before changing anything else.
- Only if GitVersion is often faster than the read lane (check the DEBUG `completed in {ElapsedMs}ms` lines), or many workspaces sync without versioning (there is nothing to overlap with then), consider splitting the read lane into two lanes: tags and branch lists in one, default branch, divergence base, and default-branch counts in the other. Do that as a separate change with its own measurement.

### Done when

- Steps 0 and 1 results are in the PR description.
- All existing Worker tests and the five new tests pass.
- Step 5 shows a lower median per-repository time and no worse total time.
- `git diff` touches only `IGitService.cs`, `GitService.cs`, `GitVersionBranch.cs`, `SyncRepositoryCommand.cs`, the two test fakes, the new test file, and this document.
