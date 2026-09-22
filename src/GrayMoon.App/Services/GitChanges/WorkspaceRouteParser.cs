namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// Parses a GrayMoon relative URI for a workspace route (<c>workspaces/{id}/...</c>).
/// Shared by layout activity binding so Git Changes monitoring does not depend on NavMenu presentation.
/// </summary>
public static class WorkspaceRouteParser
{
    /// <summary>
    /// Returns the workspace id when <paramref name="relativeUri"/> is under
    /// <c>workspaces/{id}</c> (with or without a trailing page segment); otherwise null.
    /// </summary>
    public static int? TryGetWorkspaceId(string? relativeUri)
    {
        if (string.IsNullOrWhiteSpace(relativeUri))
        {
            return null;
        }

        var pathAndQuery = relativeUri.Split('#')[0];
        var path = pathAndQuery.Split('?')[0];
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length >= 2 &&
            string.Equals(segments[0], "workspaces", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(segments[1], out var parsedId))
        {
            return parsedId;
        }

        return null;
    }
}
