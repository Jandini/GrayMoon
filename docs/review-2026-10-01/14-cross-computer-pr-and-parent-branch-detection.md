# Creating/merging a PR from a second computer's plain checkout: detection and parent-branch defaulting

Code references name symbols and give line numbers at the time of writing; line numbers drift.

## 1. The two scenarios and the short answer

**Setup:** the same feature branch exists on two computers:
- **Computer 1:** the branch was created through GrayMoon as a **Feature** (`WorkspaceFeatureContext`, kind `Feature`). GrayMoon has its own row for it.
- **Computer 2:** the same branch is simply checked out in the special **Workspace** context (`IsSpecialWorkspace == true`) — a plain `git checkout`, not a Feature GrayMoon created.

**Question 1 — if the PR is created and merged from computer 2 as an ordinary branch, will the Feature on computer 1 notice and let you remove it?**

**Yes, for the "is it merged" part.** GrayMoon never asks "which GrayMoon instance created this PR" — it asks GitHub "is there a pull request whose head branch is `<feature-branch-name>`, and what is its state". That question is answered the same way no matter which computer, which tool, or which UI created or merged the PR. See §2.

**But GrayMoon does not verify the PR merged into the *right* base.** The safety check that lets the toolbar show a one-click "Remove" is only `PullRequestMerged == true` (and no local drift). It never compares the PR's base branch to the Feature's own `ParentBranchName`. If computer 2's PR defaulted to `main` instead of the real parent (see Question 2) and nobody changed it, GrayMoon will still say "Completed, safe to remove" even though the change landed in the wrong branch. See §3.

**Question 2 — why does GrayMoon default the PR's base to `main` instead of the parent branch, and does git not record the parent branch?**

**Git itself has no persistent "parent branch" relationship.** A branch is just a movable pointer to a commit; there is no ref, no config, and no object in git that says "this branch was forked from `develop`". The only concern git has is which *commit* a branch points at, not which branch it was conceptually derived from. (The closest things — the local reflog, or guessing from merge-base — are local, best-effort heuristics, not a recorded fact; see §4.)

Because of that, **GrayMoon captures the parent branch itself, once, at Feature-creation time**, as a plain row in its own SQLite database (`WorkspaceFeatureRepository.ParentBranchName`). That capture only happens for a Feature. The second computer never ran "Create Feature" for this branch — it just checked it out under the special Workspace context — so there is no `WorkspaceFeatureRepository` row and no `ParentBranchName` for it there. GrayMoon's "New Pull Request" dialog falls back to the repository's configured default branch (`main`), which is exactly the behavior observed. See §5.

## 2. Why PR-merge detection does not care who created the PR

`WorkspaceFeatureOperations.AnalyzeRemoveFeatureAsync` (the analysis behind the Remove dialog) does this, every time it runs:

```2192:2206:GrayMoon/src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs
    private static RemoveFeatureClassification Classify(IReadOnlyList<RemoveFeatureRepositoryPlan> plans)
    {
        if (plans.Any(p => !p.WorktreeExists))
            return RemoveFeatureClassification.NeedsRepair;
        if (plans.All(IsPrMergedOrNeverCreated))
            return RemoveFeatureClassification.Completed;
        if (plans.Any(p => string.Equals(p.PullRequestState, "closed", StringComparison.OrdinalIgnoreCase)
                           && p.PullRequestMerged != true))
            return RemoveFeatureClassification.Abandoned;
        return RemoveFeatureClassification.Active;
    }

    private static bool IsPrMergedOrNeverCreated(RemoveFeatureRepositoryPlan p) =>
        p.PullRequestMerged == true
        || (p.EffectiveAheadOfDefault == 0 && p.PullRequestNumber is null or 0 && string.IsNullOrWhiteSpace(p.PullRequestState));
```

`p.PullRequestMerged` is populated by refreshing the Feature's own PR projection (`WorkspacePullRequestService.RefreshContextPullRequestsAsync`), which calls:

```250:250:GrayMoon/src/GrayMoon.App/Services/Workspaces/WorkspacePullRequestService.cs
                var pr = await gitHubPullRequestService.GetPullRequestForBranchAsync(wr.Repository!, wr.Repository!.Connector, branch, cancellationToken);
```

This is a **GitHub API call keyed only by `branch`** — "list pull requests whose head is this branch name". GitHub's PR object is a server-side resource; it exists and reports `merged: true` regardless of:
- which machine opened the PR (`gh pr create`, the GitHub web UI, or GrayMoon's own New Pull Request dialog all produce the same kind of PR object),
- which machine clicked "Merge",
- whether GrayMoon's own merge button was ever used.

So: as long as the branch name GrayMoon is polling for (the Feature's own branch — `WorkspaceFeatureRepository`'s implicit branch name, i.e. the Feature name itself) matches the head branch of the PR that computer 2 opened and merged, computer 1's next poll (or the next time you open the Remove dialog, which forces a live refresh — see `RefreshPullRequestsForRemoveAnalysisAsync` inside `AnalyzeRemoveFeatureAsync`) will see `State = "closed"`, `MergedAt = <timestamp>`. `IsPrMergedOrNeverCreated` becomes true, `Classify` returns `Completed`, and — if the worktree is otherwise clean — `IsAutomaticallySafe` is true:

```1220:1238:GrayMoon/src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs
    private static bool IsAutomaticallySafe(
        RemoveFeatureClassification classification,
        bool pullRequestStatusUnknown,
        IReadOnlyList<RemoveFeatureRepositoryPlan> plans) =>
        classification is RemoveFeatureClassification.Completed
        && !pullRequestStatusUnknown
        && plans.All(p =>
            !p.WorktreeStatusUnknown
            && p.LiveStatusEstablished
            && p.EffectiveOutgoingCommits == 0
            && !p.HasUncommittedChanges
            && !p.HasStagedChanges
            && !p.HasConflicts
            && !p.IsLocked);
```

**Practical effect:** open the Remove-Feature dialog on computer 1 after the merge on computer 2, and (worktree permitting) it will show the Feature as "Completed" with the one-click Remove enabled — the same as if you had merged it from computer 1 itself.

One mechanical note: GitHub auto-deletes the head branch on merge by default for most repos, which makes the "list PRs by head branch" lookup return nothing afterward even though the PR *is* merged. `RefreshPullRequestsAsync`/`WorkspacePullRequestService` already handles this by falling back to `GetPullRequestByNumberAsync` once a PR number is known, and the merge path persists the PR number/`MergedAt` locally (`PersistMergedAsync`) before the next refresh, specifically so this doesn't regress a Feature from "Completed" back to "unknown".

## 3. What GrayMoon does *not* check: did the PR merge into the right base?

`IsPrMergedOrNeverCreated` only reads `p.PullRequestMerged`. Nowhere in `Classify`/`IsAutomaticallySafe` is the PR's **base branch** compared against the Feature's stored `ParentBranchName`. The plan model (`RemoveFeatureRepositoryPlan`) does not even carry the PR's base branch — only `PullRequestNumber`, `PullRequestState`, `PullRequestMerged`.

Consequence: if computer 2's PR defaulted to `main` (see §5) and was merged there instead of the real parent (e.g. `develop` or `release/3.0`), GrayMoon on computer 1 will still classify the Feature as **Completed / automatically safe**, because "merged" is true — it merged, just into the wrong place. GrayMoon has no way today to tell "merged where it should have gone" apart from "merged somewhere". This is a real risk specifically because Question 2 shows the base is easy to get wrong from a plain checkout.

## 4. Does git record a branch's parent? No — and what the near-misses are

Git branches are refs (`refs/heads/<name>`) pointing at a commit. There is no persisted "this branch's parent/upstream-concept branch is X" field anywhere in the object model. What people reach for instead, and why none of it is authoritative:

- **Upstream tracking ref** (`branch.<name>.remote` / `.merge` in `.git/config`, shown by `git status` as "tracking origin/X"): this is set by `git push -u` / `git branch --set-upstream-to`, and it is almost always the *remote copy of the same branch*, not the branch it was forked from. It answers "where do I push/pull", not "what was my parent".
- **Reflog** (`git reflog show <branch>`): on the machine that ran `git checkout -b feature main`, the reflog records the checkout event and could be grepped for the base at creation time — but it is purely **local**, never pushed, pruned over time (`gc.reflogExpire`, default 90 days), and gone the moment the branch is cloned or checked out fresh on another machine (exactly what happened on computer 2).
- **Merge-base guessing** (`git merge-base feature main`, or `git merge-base --fork-point`): finds the most recent common ancestor commit with a candidate branch, which is a reasonable *heuristic* for "where did this probably fork from" when you already have a candidate — but it requires guessing the candidate first, breaks down with multiple plausible candidates (`develop`, `release/3.0`, `main` could all share history), and says nothing when the base branch has since been fast-forwarded, rebased, or deleted.
- **GitHub's own PR object**: once a PR exists, GitHub does store `base.ref` on it — but that is PR metadata created at PR-creation time (by whoever opened it, defaulting to the repo's default branch unless changed), not a git-level fact, and it does not exist before a PR is opened.

So the honest answer is: **no git-level source of truth exists**, and GrayMoon's `ParentBranchName` is a snapshot GrayMoon takes itself, once, because git cannot answer that question later.

## 5. How GrayMoon actually captures and uses `ParentBranchName`

**At Feature creation** (`WorkspaceFeatureOperations.CreateFeatureAsync`), GrayMoon asks the Agent for the HEAD snapshot of every repo in the *current* checkout (`GetHeadSnapshotAsync` → Agent command `GetHeadCommits`, which returns the current branch name per repo), and persists whatever branch was checked out at that moment as the parent, per repository, in the new `WorkspaceFeatureRepository` row:

```220:228:GrayMoon/src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs
                        WorkspaceFeatureContextId = context.WorkspaceFeatureContextId,
                        WorkspaceRepositoryId = link.WorkspaceRepositoryId,
                        WorktreePath = AgentPath.Combine(featureRootPath, repoName),
                        BaseCommitSha = sha,
                        ParentBranchName = pinnedTag == null ? parentBranch : null,
                        PinnedTag = pinnedTag,
                        CreatedAt = now,
                        State = WorkspaceFeatureRepositoryState.Pending
```

If the repo was detached (no branch checked out) when the Feature was created, `parentBranch` is `null` and stays `null` forever (`CreateFeatureParentBranchTests.Create_persists_null_ParentBranchName_when_detached_HEAD`). There is no later re-derivation; it is captured once and never touched again by anything that watches git.

**Reading it back** is scoped to a Feature context and returns nothing for the special Workspace context:

```2116:2135:GrayMoon/src/GrayMoon.App/Services/Features/WorkspaceFeatureOperations.cs
    public async Task<IReadOnlyDictionary<int, string?>> GetParentBranchNamesByRepositoryIdAsync(
        WorkspaceFeatureContextId featureContextId,
        CancellationToken cancellationToken = default)
    {
        var info = await contextResolver.GetRequiredAsync(featureContextId, cancellationToken: cancellationToken);
        if (info.IsSpecialWorkspace)
            return new Dictionary<int, string?>();
        ...
```

**New Pull Request dialog** only fetches parent branches when the current view is a Feature (`_isFeatureContext`):

```33:46:GrayMoon/src/GrayMoon.App/Components/Pages/WorkspaceRepositories.PullRequests.cs
        IReadOnlyDictionary<int, string?> parentByRepoId = new Dictionary<int, string?>();
        if (_isFeatureContext && _selectedContextId is { } featureContextId)
        {
            try
            {
                parentByRepoId = await FeatureOperations.GetParentBranchNamesByRepositoryIdAsync(featureContextId);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Failed to load Feature parent branches for New PR");
            }
        }
```

and the resolved base branch candidate falls back in this order — stored parent, then the repository's own configured default branch, then any other remote branch, never inventing one:

```29:51:GrayMoon/src/GrayMoon.App/Components/Modals/NewPullRequestTargetBranch.cs
    public static string ResolveInitialBase(
        string? parentBranchName,
        string defaultBranch,
        string headBranch,
        IReadOnlyList<string> candidates)
    {
        ...
        if (Exists(candidates, parentBranchName)
            && !string.Equals(parentBranchName, headBranch, StringComparison.OrdinalIgnoreCase))
            return parentBranchName!;

        if (Exists(candidates, defaultBranch)
            && !string.Equals(defaultBranch, headBranch, StringComparison.OrdinalIgnoreCase))
            return defaultBranch;
        ...
```

On computer 2, `_isFeatureContext` is `false` (it is the special Workspace), so `parentByRepoId` is never populated, `parentBranchName` is `null` for every target, and `ResolveInitialBase` falls straight through to `defaultBranch` — the repository's default branch, `main`. This matches exactly what was observed: GrayMoon's own "Create PR" on the second computer offers `main` as the base, not the real parent, because **the only place that fact was ever recorded is a database row that belongs to the Feature context on computer 1, and that row never existed on computer 2.**

Two separate reasons compound here, worth keeping distinct:
1. **It is a Feature-only concept.** Even on the *same* computer, opening "New Pull Request" from the Workspace view (any branch, not a Feature) never looks up a parent — there is nothing to look up for a plain checkout.
2. **It is local-database-only, never synced.** Even if computer 2 had created its own Feature for this branch, GrayMoon has no cross-computer sync of `WorkspaceFeatureRepository` rows; each computer's SQLite database is independent. A parent captured on computer 1 is invisible to GrayMoon running on computer 2 regardless of context.

## 6. Practical takeaways

- **Removal detection is safe to rely on for "was it merged".** You do not need to merge through GrayMoon, or even through the same computer, for the Feature's Remove analysis to eventually see `Completed`/safe-to-remove — it is driven entirely by GitHub's PR state for that branch name.
- **Removal detection is *not* a check that the merge went to the right place.** If you create the PR from the second computer, double-check (and correct, if needed) the base branch before merging — GrayMoon will not catch a merge into `main` instead of the real parent, and will offer one-click Remove just as happily afterward.
- **The `main`-by-default behavior on the second computer is expected, not a bug**, given today's design: parent branch is Feature-scoped, local-database-only, captured once at creation. The only way to get the correct base pre-filled is to open the PR dialog from a GrayMoon Feature context on the computer that created that Feature (or set it manually elsewhere).
- **Possible future fix** (not implemented): either (a) have `RemoveFeaturePlan`/`Classify` also surface the PR's base branch and warn when it differs from `ParentBranchName`, or (b) sync `ParentBranchName` (and other Feature metadata) somewhere both computers can read, or (c) let a Workspace-context PR dialog take an optional "treat this branch as a Feature's child of —" hint. None of these exist today.
