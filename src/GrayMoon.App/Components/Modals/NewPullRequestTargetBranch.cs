using GrayMoon.Application;

namespace GrayMoon.App.Components.Modals;

/// <summary>Pure helpers for New PR target/base branch resolution (unit-tested without the Blazor host).</summary>
public static class NewPullRequestTargetBranch
{
    /// <summary>Remote short branch names suitable as GitHub PR bases (no local checkout mutation).</summary>
    public static IReadOnlyList<string> BuildBaseCandidates(WorkspaceBranchesSnapshot snapshot)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var remote in snapshot.RemoteBranches)
        {
            if (string.IsNullOrWhiteSpace(remote)) continue;
            var shortName = remote.StartsWith("origin/", StringComparison.OrdinalIgnoreCase)
                ? remote["origin/".Length..]
                : remote;
            if (!string.IsNullOrWhiteSpace(shortName))
                set.Add(shortName);
        }

        if (!string.IsNullOrWhiteSpace(snapshot.DefaultBranch))
            set.Add(snapshot.DefaultBranch);

        return set.OrderBy(b => b, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Feature parent when it still exists remotely; otherwise repository default; never invents a branch.
    /// </summary>
    public static string ResolveInitialBase(
        string? parentBranchName,
        string defaultBranch,
        string headBranch,
        IReadOnlyList<string> candidates)
    {
        static bool Exists(IReadOnlyList<string> list, string? name) =>
            !string.IsNullOrWhiteSpace(name)
            && list.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));

        if (Exists(candidates, parentBranchName)
            && !string.Equals(parentBranchName, headBranch, StringComparison.OrdinalIgnoreCase))
            return parentBranchName!;

        if (Exists(candidates, defaultBranch)
            && !string.Equals(defaultBranch, headBranch, StringComparison.OrdinalIgnoreCase))
            return defaultBranch;

        var other = candidates.FirstOrDefault(c => !string.Equals(c, headBranch, StringComparison.OrdinalIgnoreCase));
        return other ?? defaultBranch;
    }

    /// <summary>
    /// Target-branch state for one repository from persisted (cached) branch data. <paramref name="currentSelection"/> is the
    /// user's choice before a refresh; it is kept while it is still a valid base. <see cref="NewPrBranchResolution.NeedsRefresh"/>
    /// is true when no remote branches are persisted yet (never synced or refreshed), so the dialog fetches that repository in the
    /// background instead of every repository on open.
    /// </summary>
    public static NewPrBranchResolution Resolve(NewPrTargetRepo target, WorkspaceBranchesSnapshot? snapshot, string? currentSelection = null)
    {
        if (snapshot is null)
            return new NewPrBranchResolution([], target.DefaultBranch, NeedsRefresh: true);

        var candidates = BuildBaseCandidates(snapshot);
        var keep = !string.IsNullOrWhiteSpace(currentSelection)
                   && candidates.Any(c => string.Equals(c, currentSelection, StringComparison.OrdinalIgnoreCase))
                   && !string.Equals(currentSelection, target.HeadBranch, StringComparison.OrdinalIgnoreCase);
        var selected = keep
            ? currentSelection!
            : ResolveInitialBase(target.ParentBranchName, target.DefaultBranch, target.HeadBranch, candidates);

        return new NewPrBranchResolution(candidates, selected, NeedsRefresh: snapshot.RemoteBranches.Count == 0);
    }

    /// <summary>
    /// The candidate to select when <paramref name="branch"/> is applied as the base, using that repository's own
    /// spelling. Null when it is missing, blank, or the head branch (a pull request cannot target its own head).
    /// </summary>
    public static string? BaseToApply(string? branch, string? headBranch, IReadOnlyList<string>? candidates)
    {
        if (string.IsNullOrWhiteSpace(branch) || candidates is null || candidates.Count == 0)
            return null;

        var wanted = branch.Trim();
        var match = candidates.FirstOrDefault(c => string.Equals(c, wanted, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(match))
            return null;
        if (string.Equals(match, headBranch, StringComparison.OrdinalIgnoreCase))
            return null;
        return match;
    }
}

/// <summary>Result of <see cref="NewPullRequestTargetBranch.Resolve"/>.</summary>
public sealed record NewPrBranchResolution(IReadOnlyList<string> Candidates, string SelectedBase, bool NeedsRefresh);
