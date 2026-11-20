using System.Text.RegularExpressions;

namespace GrayMoon.App.Services.GitHub;

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

    /// <summary>The ticket id the default title would lead with (for example <c>ABC-123</c>), or null when the branch has none.</summary>
    public static string? FindTicketId(string? branchName)
    {
        var tokens = Tokenize(branchName);
        var ticketIndex = Array.FindIndex(tokens, TicketIdRegex.IsMatch);
        return ticketIndex < 0 ? null : tokens[ticketIndex];
    }

    /// <summary>
    /// Puts the branch's ticket id in front of a title written elsewhere (the AI title), so it keeps the ticket the
    /// default title had. A title that already names the ticket, an empty title and a branch without a ticket are
    /// returned unchanged.
    /// </summary>
    public static string PrefixWithTicket(string? title, string? branchName)
    {
        var trimmed = title?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || FindTicketId(branchName) is not { } ticket)
            return trimmed;

        var alreadyNamed = Regex.IsMatch(
            trimmed,
            $@"(?<![A-Za-z0-9]){Regex.Escape(ticket)}(?!\d)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        return alreadyNamed ? trimmed : $"{ticket} {trimmed}";
    }

    private static string[] Tokenize(string? branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            return [];

        var working = branchName.Trim();
        working = working.Replace('_', ' ').Replace('.', ' ').Replace('/', ' ');
        working = DashNotAfterUpperRegex.Replace(working, " ");
        working = MultipleWhitespaceRegex.Replace(working, " ").Trim();
        return working.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    public static string BuildDefaultTitle(string? branchName)
    {
        var tokens = Tokenize(branchName);
        if (tokens.Length == 0)
            return string.Empty;

        var ticketIndex = Array.FindIndex(tokens, TicketIdRegex.IsMatch);

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
