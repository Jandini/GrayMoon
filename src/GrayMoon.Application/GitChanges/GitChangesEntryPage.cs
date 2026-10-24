namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// One page of direct children under a path prefix for a Staged or Changed section.
/// Children are folders (next path segment) and files at this level, ordered to match
/// <see cref="GitChangesTreeBuilder"/> (folders first, then files; ordinal-ignore-case name).
/// </summary>
public sealed record GitChangesEntryPage
{
    public required long SnapshotVersion { get; init; }

    /// <summary>Total direct children under the prefix (folders + files), before paging.</summary>
    public required int TotalDirectChildCount { get; init; }

    public required IReadOnlyList<GitChangesTreeChild> Children { get; init; }

    /// <summary>True when more children exist after this page (keyset continue with <see cref="NextAfterChildKey"/>).</summary>
    public bool HasMore => !string.IsNullOrEmpty(NextAfterChildKey);

    /// <summary>Pass as <c>afterChildKey</c> on the next request; null when this page exhausted the prefix.</summary>
    public string? NextAfterChildKey { get; init; }
}

/// <summary>One direct child of a folder/repo prefix: either a subfolder or a file (or directory entry).</summary>
public sealed record GitChangesTreeChild
{
    public required bool IsFolder { get; init; }
    public required string Name { get; init; }

    /// <summary>Repo-relative path for files (and directory entries with trailing '/'); folder prefix without trailing slash.</summary>
    public required string Path { get; init; }

    /// <summary>Set for file and directory-entry children; null for ordinary folders.</summary>
    public WorkspaceGitChangeEntryView? Entry { get; init; }

    /// <summary>Untracked directory entry (path ends with '/'); never expandable.</summary>
    public bool IsDirectoryEntry { get; init; }

    /// <summary>Stable keyset cursor: <c>f:name</c> for folders/directory entries, <c>p:name</c> for files.</summary>
    public string ChildKey => IsFolder || IsDirectoryEntry ? $"f:{Name}" : $"p:{Name}";
}

public enum GitChangesHydrationMode
{
    /// <summary>Single-shot full entry list in the circuit (today's behavior for small inventories).</summary>
    Full,

    /// <summary>Summary-first open; folder children come from <see cref="GitChangesEntryPage"/> into a node cache.</summary>
    Summary,
}
