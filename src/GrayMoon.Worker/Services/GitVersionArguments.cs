namespace GrayMoon.Worker.Services;

/// <summary>
/// Checks for the values GrayMoon puts into GitVersion's argument string (<c>/c</c>, <c>/b</c>). GitVersion is started
/// with one argument string, not an argument list, so only plain values pass: a full commit SHA and a branch name made
/// of letters, digits and <c>/ . _ - +</c>, not starting with <c>-</c>.
/// </summary>
public static class GitVersionArguments
{
    /// <summary>A full SHA-1 (40) or SHA-256 (64) object name in hex.</summary>
    public static bool IsCommitSha(string? value)
    {
        var sha = value?.Trim();
        if (sha is null || (sha.Length != 40 && sha.Length != 64))
            return false;
        foreach (var c in sha)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }

        return true;
    }

    /// <summary>A branch name GitVersion can take as one argument. Stricter than <c>git check-ref-format</c> on purpose.</summary>
    public static bool IsBranchName(string? value)
    {
        var name = value?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200)
            return false;
        if (name[0] is '-' or '/' or '.' || name[^1] is '/' or '.')
            return false;
        if (name.Contains("..", StringComparison.Ordinal) || name.Contains("//", StringComparison.Ordinal)
            || name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or '+'))
                return false;
        }

        return true;
    }
}
