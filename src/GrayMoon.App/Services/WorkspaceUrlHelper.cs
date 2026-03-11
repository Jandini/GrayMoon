namespace GrayMoon.App.Services;

/// <summary>Builds workspace-related URLs for navigation.</summary>
public static class WorkspaceUrlHelper
{
    /// <summary>
    /// Builds the dependency graph URL for a workspace, either for a specific repository or a dependency level.
    /// Exactly one of <paramref name="repositoryId"/> or <paramref name="level"/> should be non-null.
    /// </summary>
    public static string GetDependencyGraphUrl(int workspaceId, int? repositoryId = null, int? level = null)
    {
        if (repositoryId is not null)
        {
            return $"/workspaces/{workspaceId}/dependencies?repo={repositoryId.Value}";
        }

        if (level is not null)
        {
            return $"/workspaces/{workspaceId}/dependencies?level={level.Value}";
        }

        return $"/workspaces/{workspaceId}/dependencies";
    }
}

