namespace GrayMoon.Common.Git;

/// <summary>
/// The revision a repository entry in <c>graymoon.json</c> records. There are three valid forms: nothing, a tag
/// pin (<c>tag</c> + <c>commit</c>, see <see cref="WorkspaceDefinitionTagPin"/>), or the revision of a read-only
/// repository on a branch (<c>branch</c> + <c>commit</c>, only with <c>reference: true</c>). Restore checks a fresh
/// clone out at that commit; with a branch it creates the local branch there so the repository can still pull.
/// </summary>
public static class WorkspaceDefinitionRevisionPin
{
    /// <summary>
    /// Normalizes the three values and rejects every other combination with a message that names the repository
    /// and the property. <paramref name="isReference"/> is the entry's effective read-only state.
    /// </summary>
    public static bool TryNormalize(
        string? tag,
        string? branch,
        string? commit,
        bool isReference,
        string? repositoryName,
        out string? normalizedTag,
        out string? normalizedBranch,
        out string? normalizedCommit,
        out string? error)
    {
        normalizedBranch = string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();

        if (normalizedBranch is null)
        {
            // No branch: the tag rules decide, with their own messages.
            return WorkspaceDefinitionTagPin.TryNormalize(tag, commit, repositoryName, out normalizedTag, out normalizedCommit, out error);
        }

        normalizedTag = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();
        normalizedCommit = string.IsNullOrWhiteSpace(commit) ? null : commit.Trim().ToLowerInvariant();
        error = null;

        var label = string.IsNullOrWhiteSpace(repositoryName)
            ? "A repository"
            : $"Repository '{repositoryName.Trim()}'";

        if (normalizedTag is not null)
        {
            error = $"{label} has both tag and branch. An entry records a tag or a branch, not both.";
            return false;
        }

        if (!isReference)
        {
            error = $"{label} has a branch but is not read only. branch is recorded only for a repository with reference set to true.";
            return false;
        }

        if (!IsPlainBranchName(normalizedBranch))
        {
            error = $"{label} branch is not a valid branch name.";
            return false;
        }

        if (normalizedCommit is null)
        {
            error = $"{label} has a branch but is missing the full commit hash in commit.";
            return false;
        }

        if (!WorkspaceDefinitionTagPin.IsFullCommitHash(normalizedCommit))
        {
            error = $"{label} commit must be the full commit hash.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// A branch name that is safe to hand to git as one argument: it cannot be read as an option and holds no
    /// whitespace or control character. Git itself decides whether the branch exists.
    /// </summary>
    public static bool IsPlainBranchName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 255 || name[0] is '-' or '/')
            return false;
        if (name.EndsWith('/') || name.EndsWith('.') || name.EndsWith(".lock", StringComparison.Ordinal))
            return false;
        if (name.Contains("..", StringComparison.Ordinal) || name.Contains("@{", StringComparison.Ordinal) || name.Contains("//", StringComparison.Ordinal))
            return false;

        foreach (var character in name)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character) || character is '~' or '^' or ':' or '?' or '*' or '[' or '\\')
                return false;
        }

        return true;
    }
}
