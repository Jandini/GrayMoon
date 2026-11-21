using System.Text.RegularExpressions;

namespace GrayMoon.Worker.Services;

/// <summary>
/// Turns the output of a failed GitVersion run into the one line a user needs. GitVersion prints its log lines and a
/// full stack trace; the row of a repository only has room for the reason.
/// </summary>
internal static partial class GitVersionFailureText
{
    /// <summary>The message for a repository whose current branch has no commits, the usual state of a new repository.</summary>
    public const string NoCommits = "Repository has no commits yet, so it has no version.";

    private const string NoCommitsMarker = "No commits found on the current branch";

    /// <summary>True when GitVersion failed only because the current branch has no commits.</summary>
    public static bool IsNoCommits(string? stderr, string? stdout) =>
        (stderr?.Contains(NoCommitsMarker, StringComparison.OrdinalIgnoreCase) ?? false)
        || (stdout?.Contains(NoCommitsMarker, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// The short reason of a failed run: <see cref="NoCommits"/>, or the message of the first exception GitVersion
    /// printed. Null when the output names no exception, so the caller keeps the output as it is.
    /// </summary>
    public static string? Summarize(string? stderr, string? stdout)
    {
        if (IsNoCommits(stderr, stdout))
            return NoCommits;

        foreach (var text in new[] { stderr, stdout })
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;
            var match = ExceptionLine().Match(text);
            if (match.Success)
                return "GitVersion: " + match.Groups["message"].Value.Trim();
        }

        return null;
    }

    [GeneratedRegex(@"^[^\r\n]*?\b[\w.]+Exception: (?<message>[^\r\n]+)", RegexOptions.Multiline)]
    private static partial Regex ExceptionLine();
}
