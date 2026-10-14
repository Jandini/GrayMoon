namespace GrayMoon.Common.FileVersions;
/// <summary>
/// Central parser for GrayMoon file-version <c>{...}</c> tokens.
/// Literal braces that are not a token (JSON objects, for example) are left alone.
/// </summary>
public static class FileVersionTokenParser
{
    /// <summary>
    /// Parses the content between <c>{</c> and <c>}</c> (e.g. <c>@Repo:commit</c> or legacy <c>Repo</c>).
    /// Returns false for empty content or an unsupported selector (not treated as a repository name).
    /// </summary>
    public static bool TryParse(string? inner, out FileVersionToken? token, out string? unsupportedSelector)
    {
        token = null;
        unsupportedSelector = null;
        if (string.IsNullOrWhiteSpace(inner))
            return false;
        var content = inner.Trim();
        if (content.Length == 0)
            return false;
        // Leading '@' is the GrayMoon token marker; optional for legacy {Repo} configs.
        if (content[0] == '@')
            content = content[1..];
        if (string.IsNullOrWhiteSpace(content))
            return false;
        var colon = content.IndexOf(':');
        if (colon < 0)
        {
            var repoOnly = content.Trim();
            if (repoOnly.Length == 0)
                return false;
            token = new FileVersionToken(repoOnly, FileVersionTokenKind.GitVersion);
            return true;
        }
        var repoName = content[..colon].Trim();
        var selector = content[(colon + 1)..].Trim();
        if (repoName.Length == 0)
            return false;
        if (selector.Equals("branch", StringComparison.OrdinalIgnoreCase))
        {
            token = new FileVersionToken(repoName, FileVersionTokenKind.Branch);
            return true;
        }
        if (selector.Equals("commit", StringComparison.OrdinalIgnoreCase))
        {
            token = new FileVersionToken(repoName, FileVersionTokenKind.Commit);
            return true;
        }
        unsupportedSelector = selector;
        return false;
    }
    /// <summary>Extracts successfully parsed tokens from each non-empty pattern line (one token per line).</summary>
    public static IReadOnlyList<FileVersionToken> ExtractTokens(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return [];
        var tokens = new List<FileVersionToken>();
        foreach (var raw in pattern.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (TryFindLineToken(line, out _, out _, out var token, out _) && token != null)
                tokens.Add(token);
        }
        return tokens;
    }
    /// <summary>
    /// Finds the first version token on <paramref name="line"/>.
    /// Brace pairs that contain whitespace, quotes, or nested braces are structural text, not tokens,
    /// so a JSON line such as <c>{ "commit": "{@Repo:commit}" }</c> resolves to <c>{@Repo:commit}</c>.
    /// Returns false when the line has no token. An unsupported selector still sets
    /// <paramref name="unsupportedSelector"/> and the token span.
    /// </summary>
    public static bool TryFindLineToken(
        string line,
        out int tokenStart,
        out int tokenEnd,
        out FileVersionToken? token,
        out string? unsupportedSelector)
    {
        token = null;
        unsupportedSelector = null;
        tokenStart = -1;
        tokenEnd = -1;
        var searchFrom = 0;
        while (searchFrom < line.Length)
        {
            var open = line.IndexOf('{', searchFrom);
            if (open < 0)
                return false;
            var close = line.IndexOf('}', open + 1);
            if (close < 0)
                return false;
            var inner = line[(open + 1)..close];
            if (!LooksLikeToken(inner))
            {
                searchFrom = open + 1;
                continue;
            }
            if (TryParse(inner, out token, out unsupportedSelector) && token != null)
            {
                tokenStart = open;
                tokenEnd = close;
                return true;
            }
            if (!string.IsNullOrEmpty(unsupportedSelector))
            {
                tokenStart = open;
                tokenEnd = close;
                return false;
            }
            token = null;
            unsupportedSelector = null;
            searchFrom = close + 1;
        }
        return false;
    }
    /// <summary>
    /// A token interior is <c>@Repo</c>, <c>@Repo:selector</c>, or the legacy form without <c>@</c>.
    /// Anything with whitespace, quotes, or braces is surrounding file text.
    /// </summary>
    private static bool LooksLikeToken(string inner)
    {
        var content = inner.Trim();
        if (content.Length == 0)
            return false;
        foreach (var c in content)
        {
            if (char.IsWhiteSpace(c) || c is '"' or '\'' or '{' or '}')
                return false;
        }
        return true;
    }
    /// <summary>
    /// Validates pattern lines against known repository names. Separates unknown repositories
    /// from unsupported selectors (e.g. <c>{@Repo:foo}</c>).
    /// When <paramref name="owningRepositoryName"/> is set, tokens that reference that same
    /// repository are reported as self-references (circular update risk).
    /// </summary>
    public static FileVersionTokenValidationResult Validate(
        string? pattern,
        IReadOnlySet<string> knownRepositoryNames,
        string? owningRepositoryName = null)
    {
        var valid = new List<FileVersionToken>();
        var unknownRepos = new List<string>();
        var unsupportedSelectors = new List<string>();
        var selfReferencing = new List<FileVersionToken>();
        if (string.IsNullOrWhiteSpace(pattern))
            return new FileVersionTokenValidationResult(valid, unknownRepos, unsupportedSelectors, selfReferencing);
        var hasOwning = !string.IsNullOrWhiteSpace(owningRepositoryName);
        foreach (var raw in pattern.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (string.IsNullOrEmpty(line))
                continue;
            if (!TryFindLineToken(line, out _, out _, out var token, out var unsupportedSelector) || token == null)
            {
                if (!string.IsNullOrEmpty(unsupportedSelector))
                    unsupportedSelectors.Add(unsupportedSelector);
                continue;
            }
            if (!knownRepositoryNames.Contains(token.RepositoryName))
            {
                unknownRepos.Add(token.RepositoryName);
                continue;
            }
            if (hasOwning &&
                token.RepositoryName.Equals(owningRepositoryName, StringComparison.OrdinalIgnoreCase))
            {
                selfReferencing.Add(token);
            }
            valid.Add(token);
        }
        return new FileVersionTokenValidationResult(valid, unknownRepos, unsupportedSelectors, selfReferencing);
    }
    /// <summary>
    /// Parses pattern text into prefix / token / suffix entries for Worker update and check commands.
    /// Lines with unparseable tokens (including unsupported selectors) are skipped.
    /// </summary>
    public static IReadOnlyList<FileVersionPatternEntry> ParsePatternLines(string? pattern)
    {
        var result = new List<FileVersionPatternEntry>();
        if (string.IsNullOrWhiteSpace(pattern))
            return result;
        foreach (var raw in pattern.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (string.IsNullOrEmpty(line))
                continue;
            if (!TryFindLineToken(line, out var start, out var end, out var token, out _) || token == null)
                continue;
            if (start < 1)
                continue;
            var prefix = line[..start];
            var suffix = end + 1 < line.Length ? line[(end + 1)..] : "";
            if (string.IsNullOrEmpty(prefix))
                continue;
            result.Add(new FileVersionPatternEntry(prefix, token, suffix));
        }
        return result;
    }
}
/// <summary>One version-pattern line broken into prefix, structured token, and suffix.</summary>
public sealed record FileVersionPatternEntry(string Prefix, FileVersionToken Token, string Suffix);
/// <summary>UI validation outcome for a version pattern.</summary>
public sealed record FileVersionTokenValidationResult(
    IReadOnlyList<FileVersionToken> ValidTokens,
    IReadOnlyList<string> UnknownRepositories,
    IReadOnlyList<string> UnsupportedSelectors,
    IReadOnlyList<FileVersionToken> SelfReferencingTokens);
