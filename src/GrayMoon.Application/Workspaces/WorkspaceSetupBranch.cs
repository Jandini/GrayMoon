namespace GrayMoon.Application.Workspaces;

/// <summary>
/// The new branch a repository gets when it becomes the Workspace repository: GrayMoon creates it, checks it out and
/// writes <c>graymoon.json</c> and the managed <c>.gitignore</c> section there, so nothing is written on the default
/// branch. GrayMoon never commits or pushes it.
/// </summary>
public static class WorkspaceSetupBranch
{
    public const string DefaultName = "graymoon/workspace-setup";

    public const string RequiredMessage = "Enter a name for the new branch.";

    public static string ExistsMessage(string branch) =>
        $"Branch '{branch}' already exists in this repository. Choose another name.";

    public static string InvalidMessage(string branch) => $"'{branch}' is not a valid branch name.";

    private static readonly char[] ForbiddenChars = ['~', '^', ':', '?', '*', '[', '\\', '"', '\''];

    /// <summary>
    /// Null when <paramref name="name"/> is a branch name git accepts and that is safe as one argument; otherwise the
    /// message to show. Follows <c>git check-ref-format</c>, and also refuses a leading dash, quotes and <c>refs/</c>.
    /// </summary>
    public static string? Validate(string? name)
    {
        var branch = name?.Trim() ?? string.Empty;
        if (branch.Length == 0)
            return RequiredMessage;

        var valid = branch[0] != '-'
            && branch != "@"
            && !string.Equals(branch, "HEAD", StringComparison.Ordinal)
            && !branch.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            && branch.IndexOfAny(ForbiddenChars) < 0
            && !branch.Contains("..", StringComparison.Ordinal)
            && !branch.Contains("@{", StringComparison.Ordinal)
            && !branch.Contains("//", StringComparison.Ordinal)
            && !branch.StartsWith('/')
            && !branch.EndsWith('/')
            && !branch.EndsWith('.')
            && !branch.StartsWith("refs/", StringComparison.Ordinal)
            && branch.Split('/').All(part => !part.StartsWith('.') && !part.EndsWith(".lock", StringComparison.Ordinal));
        return valid ? null : InvalidMessage(branch);
    }
}
