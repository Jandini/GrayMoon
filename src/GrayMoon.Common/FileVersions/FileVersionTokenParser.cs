namespace GrayMoon.Common.FileVersions;
/// <summary>Central parser for GrayMoon file-version <c>{...}</c> tokens.</summary>
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
            var start = line.IndexOf('{');
            var end = start >= 0 ? line.IndexOf('}', start) : -1;
            if (start < 0 || end <= start)
                continue;
            var inner = line[(start + 1)..end];
            if (TryParse(inner, out var token, out _) && token != null)
                tokens.Add(token);
        }
        return tokens;
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
            var start = line.IndexOf('{');
            var end = start >= 0 ? line.IndexOf('}', start) : -1;
            if (start < 0 || end <= start)
                continue;
            var inner = line[(start + 1)..end];
            if (!TryParse(inner, out var token, out var unsupportedSelector) || token == null)
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
    /// Parses pattern text into prefix / token / suffix entries for Agent update and check commands.
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
            var start = line.IndexOf('{');
            var end = line.IndexOf('}', start >= 0 ? start : 0);
            if (start < 1 || end <= start)
                continue;
            var prefix = line[..start];
            var inner = line[(start + 1)..end];
            var suffix = end + 1 < line.Length ? line[(end + 1)..] : "";
            if (string.IsNullOrEmpty(prefix))
                continue;
            if (!TryParse(inner, out var token, out _) || token == null)
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
