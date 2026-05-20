using System.Text.RegularExpressions;

namespace GrayMoon.App.Services;

/// <summary>Builds a default PR title from a branch name.</summary>
/// <remarks>
/// Replaces <c>-</c>, <c>_</c>, <c>.</c>, <c>/</c> with spaces, except a <c>-</c> immediately following an uppercase letter
/// (so ticket identifiers like <c>ABC-123</c> are kept together). Capitalization is preserved. Duplicate whitespace
/// is collapsed. If a ticket-id token (one or more uppercase letters followed by <c>-</c> and digits) is present,
/// it is moved to the front.
/// </remarks>
public static class PullRequestTitleHelper
{
    private static readonly Regex DashNotAfterUpperRegex = new(
        @"(?<![A-Z])-",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MultipleWhitespaceRegex = new(
        @"\s+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TicketIdRegex = new(
        @"^[A-Z]+-\d+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string BuildDefaultTitle(string? branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            return string.Empty;

        var working = branchName.Trim();
        working = working.Replace('_', ' ').Replace('.', ' ').Replace('/', ' ');
        working = DashNotAfterUpperRegex.Replace(working, " ");
        working = MultipleWhitespaceRegex.Replace(working, " ").Trim();

        if (working.Length == 0)
            return string.Empty;

        var tokens = working.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var ticketIndex = -1;
        for (var i = 0; i < tokens.Length; i++)
        {
            if (TicketIdRegex.IsMatch(tokens[i]))
            {
                ticketIndex = i;
                break;
            }
        }

        if (ticketIndex > 0)
        {
            var ticket = tokens[ticketIndex];
            var reordered = new List<string>(tokens.Length) { ticket };
            for (var i = 0; i < tokens.Length; i++)
            {
                if (i == ticketIndex) continue;
                reordered.Add(tokens[i]);
            }
            return string.Join(' ', reordered);
        }

        return string.Join(' ', tokens);
    }
}
