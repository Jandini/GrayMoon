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
}
