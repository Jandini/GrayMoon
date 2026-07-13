namespace GrayMoon.Common.Git;

/// <summary>
/// Pure parser for the output of <c>git status --porcelain=v2 -z --branch --untracked-files=all</c>.
/// With <c>-z</c>, every record (including the <c>#</c> branch headers) is NUL-terminated instead of
/// newline-terminated, and paths are never C-style quoted - so splitting the raw output on NUL and
/// taking fields verbatim is sufficient. No process invocation or filesystem access happens here.
/// </summary>
public static class GitPorcelainV2Parser
{
    public static GitPorcelainV2ParseResult Parse(string? output)
    {
        var branchName = "HEAD";
        string? headCommit = null;
        var isDetachedHead = false;
        var isUnbornBranch = false;
        var changes = new List<GitChangeEntry>();

        if (string.IsNullOrEmpty(output))
        {
            return new GitPorcelainV2ParseResult
            {
                BranchName = branchName,
                HeadCommit = headCommit,
                IsDetachedHead = isDetachedHead,
                IsUnbornBranch = isUnbornBranch,
                Changes = changes,
            };
        }

        var records = SplitRecords(output);

        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];

            switch (record[0])
            {
                case '#':
                    ParseHeader(record, ref branchName, ref headCommit, ref isDetachedHead, ref isUnbornBranch);
                    break;

                case '1':
                    changes.Add(ParseOrdinary(record));
                    break;

                case '2':
                    var originalPath = i + 1 < records.Count ? records[i + 1] : string.Empty;
                    changes.Add(ParseRenameOrCopy(record, originalPath));
                    i++;
                    break;

                case 'u':
                    changes.Add(ParseUnmerged(record));
                    break;

                case '?':
                    changes.Add(ParseUntracked(record));
                    break;

                case '!':
                    // Ignored entries only appear when --ignored is passed; the standard scan omits it.
                    break;
            }
        }

        return new GitPorcelainV2ParseResult
        {
            BranchName = branchName,
            HeadCommit = headCommit,
            IsDetachedHead = isDetachedHead,
            IsUnbornBranch = isUnbornBranch,
            Changes = changes,
        };
    }

    private static List<string> SplitRecords(string output)
    {
        var raw = output.Split('\0');
        var records = new List<string>(raw.Length);
        foreach (var record in raw)
        {
            if (record.Length > 0)
            {
                records.Add(record);
            }
        }

        return records;
    }

    private static void ParseHeader(
        string record,
        ref string branchName,
        ref string? headCommit,
        ref bool isDetachedHead,
        ref bool isUnbornBranch)
    {
        if (record.Length <= 2)
        {
            return;
        }

        var content = record[2..];

        if (content.StartsWith("branch.oid ", StringComparison.Ordinal))
        {
            var oid = content["branch.oid ".Length..];
            if (oid == "(initial)")
            {
                isUnbornBranch = true;
                headCommit = null;
            }
            else
            {
                headCommit = oid;
            }
        }
        else if (content.StartsWith("branch.head ", StringComparison.Ordinal))
        {
            var head = content["branch.head ".Length..];
            if (head == "(detached)")
            {
                isDetachedHead = true;
                branchName = "HEAD";
            }
            else
            {
                branchName = head;
            }
        }
    }

    private static GitChangeEntry ParseOrdinary(string record)
    {
        var parts = record.Split(' ', 9);
        var xy = parts[1];
        var sub = parts[2];
        var path = parts[8];

        return new GitChangeEntry
        {
            Path = path,
            IndexChange = MapChangeChar(xy[0]),
            WorktreeChange = MapChangeChar(xy[1]),
            IsTracked = true,
            IsConflicted = false,
            IsSubmodule = IsSubmoduleField(sub),
        };
    }

    private static GitChangeEntry ParseRenameOrCopy(string record, string originalPath)
    {
        var parts = record.Split(' ', 10);
        var xy = parts[1];
        var sub = parts[2];
        var path = parts[9];

        return new GitChangeEntry
        {
            Path = path,
            OriginalPath = originalPath,
            IndexChange = MapChangeChar(xy[0]),
            WorktreeChange = MapChangeChar(xy[1]),
            IsTracked = true,
            IsConflicted = false,
            IsSubmodule = IsSubmoduleField(sub),
        };
    }

    private static GitChangeEntry ParseUnmerged(string record)
    {
        var parts = record.Split(' ', 11);
        var xy = parts[1];
        var sub = parts[2];
        var path = parts[10];

        return new GitChangeEntry
        {
            Path = path,
            IndexChange = MapChangeChar(xy[0]),
            WorktreeChange = MapChangeChar(xy[1]),
            IsTracked = true,
            IsConflicted = true,
            IsSubmodule = IsSubmoduleField(sub),
        };
    }

    private static GitChangeEntry ParseUntracked(string record)
    {
        var parts = record.Split(' ', 2);
        var path = parts.Length > 1 ? parts[1] : string.Empty;

        return new GitChangeEntry
        {
            Path = path,
            IndexChange = GitChangeKind.None,
            WorktreeChange = GitChangeKind.Untracked,
            IsTracked = false,
            IsConflicted = false,
            IsSubmodule = false,
        };
    }

    private static bool IsSubmoduleField(string sub) => sub.Length > 0 && sub[0] == 'S';

    private static GitChangeKind MapChangeChar(char c) => c switch
    {
        '.' => GitChangeKind.None,
        'M' => GitChangeKind.Modified,
        'A' => GitChangeKind.Added,
        'D' => GitChangeKind.Deleted,
        'R' => GitChangeKind.Renamed,
        'C' => GitChangeKind.Copied,
        'T' => GitChangeKind.TypeChanged,
        'U' => GitChangeKind.Unmerged,
        _ => GitChangeKind.None,
    };
}

/// <summary>Result of parsing one porcelain v2 status scan - branch/head state plus the flat change list.</summary>
public sealed record GitPorcelainV2ParseResult
{
    public required string BranchName { get; init; }
    public string? HeadCommit { get; init; }
    public bool IsDetachedHead { get; init; }
    public bool IsUnbornBranch { get; init; }
    public required IReadOnlyList<GitChangeEntry> Changes { get; init; }
}
