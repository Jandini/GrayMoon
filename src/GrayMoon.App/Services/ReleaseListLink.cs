namespace GrayMoon.App.Services;

/// <summary>
/// The top-bar version opens the GrayMoon.Release releases page already filtered.
/// A release version (no branch in the number) filters to <c>release</c>. A prerelease
/// version from GitVersion is <c>{major}.{minor}.{patch}-{branch}.{height}</c>, and the
/// filter is that branch name.
/// </summary>
internal static class ReleaseListLink
{
    public const string ReleasesPage = "https://github.com/Jandini/GrayMoon.Release/releases";

    public static string ForVersion(string? version)
    {
        var branch = PrereleaseBranch(version);
        var query = string.IsNullOrEmpty(branch) ? "release" : branch;
        return $"{ReleasesPage}?q={Uri.EscapeDataString(query)}&expanded=true";
    }

    /// <summary>
    /// The branch label inside a GitVersion prerelease, or null when the version is a release.
    /// Build metadata (after <c>+</c>) and a leading <c>v</c> are ignored. The trailing
    /// <c>.{height}</c> commit count is not part of the branch name.
    /// </summary>
    public static string? PrereleaseBranch(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var normalized = version.Trim();
        if (normalized.Length > 1 && (normalized[0] == 'v' || normalized[0] == 'V') && char.IsDigit(normalized[1]))
            normalized = normalized[1..];

        var plus = normalized.IndexOf('+');
        if (plus >= 0)
            normalized = normalized[..plus];

        var dash = normalized.IndexOf('-');
        if (dash < 0 || dash >= normalized.Length - 1)
            return null;

        var core = normalized[..dash];
        if (!IsMajorMinorPatch(core))
            return null;

        var pre = normalized[(dash + 1)..];
        var lastDot = pre.LastIndexOf('.');
        if (lastDot > 0 && lastDot < pre.Length - 1 && IsAllDigits(pre[(lastDot + 1)..]))
            pre = pre[..lastDot];

        return pre.Length == 0 ? null : pre;
    }

    private static bool IsMajorMinorPatch(string core)
    {
        var parts = core.Split('.');
        if (parts.Length != 3)
            return false;

        foreach (var part in parts)
        {
            if (!IsAllDigits(part))
                return false;
        }

        return true;
    }

    private static bool IsAllDigits(string value)
    {
        if (value.Length == 0)
            return false;

        foreach (var c in value)
        {
            if (!char.IsDigit(c))
                return false;
        }

        return true;
    }
}
