namespace GrayMoon.App.Services.Jobs;

/// <summary>
/// Parses circuit job keys that belong to a workspace so the process-wide runner
/// can lock one mutation per workspace and attach overlays by page family.
/// </summary>
public static class WorkspaceJobKeys
{
    public const string WorkspaceFamily = "workspace";
    public const string ChangesFamily = "changes";

    public static bool IsScanKey(string jobKey)
        => jobKey.Contains(":scan", StringComparison.OrdinalIgnoreCase);

    public static bool TryGetWorkspaceId(string jobKey, out int workspaceId)
    {
        workspaceId = 0;
        if (string.IsNullOrWhiteSpace(jobKey) || IsScanKey(jobKey))
            return false;

        var parts = jobKey.Trim().Trim('/').ToLowerInvariant().Split('/');
        if (parts.Length < 2 || parts[0] != "workspaces")
            return false;

        return int.TryParse(parts[1], out workspaceId) && workspaceId > 0;
    }

    public static bool IsMutationKey(string jobKey, out int workspaceId)
        => TryGetWorkspaceId(jobKey, out workspaceId);

    public static string OverlayFamily(string jobKey)
    {
        var parts = jobKey.Trim().Trim('/').ToLowerInvariant().Split('/');
        if (parts.Length >= 3 && parts[0] == "workspaces" && parts[2] == "changes")
            return ChangesFamily;
        return WorkspaceFamily;
    }

    public static bool OverlayMatches(string overlayKey, WorkspaceOperation operation)
        => TryGetWorkspaceId(overlayKey, out var workspaceId)
           && workspaceId == operation.WorkspaceId
           && string.Equals(OverlayFamily(overlayKey), operation.OverlayFamily, StringComparison.Ordinal);
}
