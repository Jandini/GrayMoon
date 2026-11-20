using GrayMoon.App.Models;

namespace GrayMoon.App.Services.Connectors;

/// <summary>
/// What the connector dialog fills in by itself. A value follows the connector type only while the user has not
/// chosen it: the name while the name box still holds the name the dialog put there, the address while it is blank
/// or still the previous type's default.
/// </summary>
public static class ConnectorFormAutoFill
{
    /// <summary>
    /// Renames the connector to <paramref name="name"/> when the name box still holds <paramref name="autoName"/>.
    /// Returns the name the dialog now owns: <paramref name="name"/> after a rename, else <paramref name="autoName"/>
    /// unchanged. A null <paramref name="autoName"/> (Edit mode) never renames.
    /// </summary>
    public static string? ApplyName(ConnectorForm form, string? autoName, string name)
    {
        if (autoName == null)
            return null;
        if (!string.Equals((form.ConnectorName ?? string.Empty).Trim(), autoName, StringComparison.Ordinal))
            return autoName;

        form.ConnectorName = name;
        return name;
    }

    /// <summary>
    /// Call after <see cref="ConnectorForm.ConnectorType"/> changed from <paramref name="previousType"/>. Replaces the
    /// address only when the user has not chosen one, and drops the token for NuGet.org, which takes none.
    /// </summary>
    public static void ApplyTypeChange(ConnectorForm form, ConnectorType previousType)
    {
        var current = (form.ApiBaseUrl ?? string.Empty).Trim();
        if (current.Length == 0 || IsSameUrl(current, ConnectorUrlResolver.DefaultUrl(previousType)))
            form.ApiBaseUrl = ConnectorUrlResolver.DefaultUrl(form.ConnectorType);

        if (ConnectorHelpers.IsNuGetOrg(form.ApiBaseUrl ?? string.Empty))
            form.UserToken = null;

        if (form.ConnectorType != ConnectorType.ProGet)
            form.FeedName = null;
        else
            form.UserName = null; // ProGet has no User Name box; a leftover name would replace the "api" sign-in unseen
    }

    /// <summary>
    /// ProGet: shows a saved feed endpoint as its two parts, the server in the address box and the feed in the Feed
    /// box. Does nothing for another type or an address that names no feed.
    /// </summary>
    public static void SplitProGetAddress(ConnectorForm form)
    {
        if (form.ConnectorType != ConnectorType.ProGet)
            return;

        var resolution = ConnectorUrlResolver.Resolve(ConnectorType.ProGet, form.ApiBaseUrl);
        if (resolution.ServerUrl == null || resolution.Feed == null)
            return;

        form.ApiBaseUrl = resolution.ServerUrl;
        form.FeedName = resolution.Feed;
    }

    private static bool IsSameUrl(string left, string right)
        => string.Equals(left.TrimEnd('/'), right.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
