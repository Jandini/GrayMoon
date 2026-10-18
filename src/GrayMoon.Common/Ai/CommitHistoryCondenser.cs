namespace GrayMoon.Common.Ai;

/// <summary>One line of a pull request's history: one commit, or the same commit subject made in several repositories.</summary>
public sealed record CommitHistoryEntry(
    string Subject,
    IReadOnlyList<string> Repositories,
    int FilesChanged,
    int Added,
    int Deleted,
    bool IsLowSignal)
{
    public int Churn => Added + Deleted;
}

/// <summary>The condensed history of every repository in one pull request, oldest first.</summary>
public sealed record CommitHistory(
    IReadOnlyList<CommitHistoryEntry> Entries,
    int Commits,
    int NotRead,
    int RedactedLines)
{
    public static CommitHistory Empty { get; } = new([], 0, 0, 0);
}

/// <summary>
/// Turns the commits of every repository into one short, deterministic history. Within a repository, <c>fixup!</c>,
/// <c>squash!</c> and <c>amend!</c> commits fold into the commit they fix, and a commit reverted on the same branch is
/// dropped with its revert, since the diff shows neither. Across repositories, a workspace commit (the same subject
/// committed in several repositories) becomes one entry naming them all. Subjects are redacted like every other
/// evidence text. Commits are ordered by committer time.
/// </summary>
public static class CommitHistoryCondenser
{
    /// <summary>A commit whose subject says little is low signal only while it is also small.</summary>
    internal const int LowSignalMaxChurn = 50;

    private static readonly string[] FixupPrefixes = ["fixup! ", "squash! ", "amend! "];

    private static readonly HashSet<string> LowSignalSubjects = new(StringComparer.OrdinalIgnoreCase)
    {
        "wip", "tmp", "temp", "misc", "minor", "oops", "fix", "fixes", "update", "updates", "changes",
        "typo", "typos", "fix typo", "fix typos",
        "fix build", "fix tests", "fix test", "fix lint", "lint",
        "format", "formatting", "format code", "cleanup", "clean up", "minor fix", "minor fixes", "small fix", "small fixes",
        "review", "review comments", "review feedback", "address review", "address review comments", "address review feedback",
        "address comments", "address feedback", "pr comments", "pr feedback",
    };

    private const string RevertPrefix = "Revert \"";

    public static CommitHistory Condense(IReadOnlyList<RepositoryGitEvidence> repositories)
    {
        var commits = 0;
        var notRead = 0;
        var redacted = 0;
        var items = new List<Item>();
        for (var r = 0; r < repositories.Count; r++)
        {
            var repository = repositories[r];
            commits += repository.Commits.Count + repository.CommitsOmitted;
            notRead += repository.CommitsOmitted;

            var own = new List<Item>();
            for (var i = 0; i < repository.Commits.Count; i++)
            {
                var commit = repository.Commits[i];
                var subject = EvidenceRedactor.Redact(OneLine(commit.Subject), out var redactedHere);
                redacted += redactedHere;
                own.Add(new Item(repository.RepositoryName, r, i, commit.CommittedAt, subject, commit.FilesChanged, commit.Added, commit.Deleted));
            }

            FoldFixups(own);
            DropReverts(own);
            items.AddRange(own.Where(item => !item.Removed));
        }

        var entries = new List<Item>();
        var bySubject = new Dictionary<string, List<Item>>(StringComparer.Ordinal);
        foreach (var item in items.OrderBy(i => i.CommittedAt).ThenBy(i => i.RepositoryOrder).ThenBy(i => i.Index))
        {
            if (!bySubject.TryGetValue(item.Subject, out var same))
                bySubject[item.Subject] = same = [];

            // The same subject in another repository is the same workspace commit; in the same repository it is not.
            var match = same.FirstOrDefault(e => !e.Repositories.Contains(item.RepositoryName, StringComparer.OrdinalIgnoreCase));
            if (match is null)
            {
                same.Add(item);
                entries.Add(item);
                continue;
            }

            match.Repositories.Add(item.RepositoryName);
            match.FilesChanged += item.FilesChanged;
            match.Added += item.Added;
            match.Deleted += item.Deleted;
        }

        return new CommitHistory(
            entries.Select(e => new CommitHistoryEntry(
                    e.Subject,
                    e.Repositories,
                    e.FilesChanged,
                    e.Added,
                    e.Deleted,
                    (e.WasUnmatchedFixup || IsLowSignalSubject(e.Subject)) && e.Added + e.Deleted <= LowSignalMaxChurn))
                .ToList(),
            commits,
            notRead,
            redacted);
    }

    internal static bool IsLowSignalSubject(string subject)
    {
        var normalized = subject.Trim().TrimEnd('.', '!').Trim();
        return normalized.Length == 0
               || LowSignalSubjects.Contains(normalized)
               || normalized.StartsWith("wip:", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("wip ", StringComparison.OrdinalIgnoreCase);
    }

    private static void FoldFixups(List<Item> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var subject = items[i].Subject;
            var stripped = StripFixupPrefixes(subject);
            if (ReferenceEquals(stripped, subject))
                continue;

            var target = LastBefore(items, i, stripped);
            if (target is null)
            {
                // It fixes a commit that is not on this branch: keep it under the subject it refers to.
                items[i].Subject = stripped;
                items[i].WasUnmatchedFixup = true;
                continue;
            }

            target.FilesChanged = Math.Max(target.FilesChanged, items[i].FilesChanged);
            target.Added += items[i].Added;
            target.Deleted += items[i].Deleted;
            items[i].Removed = true;
        }
    }

    private static void DropReverts(List<Item> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Removed)
                continue;
            // git's own revert subject: Revert "<subject of the reverted commit>"
            var subject = items[i].Subject;
            if (subject.Length <= RevertPrefix.Length + 1
                || !subject.StartsWith(RevertPrefix, StringComparison.Ordinal)
                || !subject.EndsWith('"'))
                continue;

            var target = LastBefore(items, i, subject[RevertPrefix.Length..^1]);
            if (target is null)
                continue;

            target.Removed = true;
            items[i].Removed = true;
        }
    }

    private static Item? LastBefore(List<Item> items, int index, string subject)
    {
        for (var j = index - 1; j >= 0; j--)
        {
            if (!items[j].Removed && string.Equals(items[j].Subject, subject, StringComparison.Ordinal))
                return items[j];
        }

        return null;
    }

    private static string StripFixupPrefixes(string subject)
    {
        var result = subject;
        var found = true;
        while (found)
        {
            found = false;
            foreach (var prefix in FixupPrefixes)
            {
                if (result.StartsWith(prefix, StringComparison.Ordinal))
                {
                    result = result[prefix.Length..].TrimStart();
                    found = true;
                }
            }
        }

        return result;
    }

    private static string OneLine(string text)
    {
        var newline = text.IndexOfAny(['\r', '\n']);
        return (newline < 0 ? text : text[..newline]).Trim();
    }

    private sealed class Item(string repositoryName, int repositoryOrder, int index, long committedAt, string subject, int filesChanged, int added, int deleted)
    {
        public string RepositoryName { get; } = repositoryName;
        public int RepositoryOrder { get; } = repositoryOrder;
        public int Index { get; } = index;
        public long CommittedAt { get; } = committedAt;
        public string Subject { get; set; } = subject;
        public int FilesChanged { get; set; } = filesChanged;
        public int Added { get; set; } = added;
        public int Deleted { get; set; } = deleted;
        public bool Removed { get; set; }
        public bool WasUnmatchedFixup { get; set; }
        public List<string> Repositories { get; } = [repositoryName];
    }
}
