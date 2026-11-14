namespace GrayMoon.App.Services.WorkspaceManifest;

// Inside the namespace, so WorkspaceManifest binds to the type and not to this namespace.
using GrayMoon.Common.Manifest;

/// <summary>
/// The schema level of a Workspace definition is sticky: version 1 stays 1 until read-only data needs version 2, and
/// version 2 never goes back to 1, even when every repository is writable again.
/// </summary>
public static class WorkspaceManifestSchemaPolicy
{
    /// <summary>
    /// <paramref name="built"/> is the definition derived from the database, which omits read-only state while there is
    /// none. When <paramref name="existing"/> (the file on disk) is version 2, the result is version 2 and every
    /// <c>reference</c> is explicit: the default and each Source say true or false. Effective states do not change: an
    /// absent value in <paramref name="built"/> already means false. Otherwise <paramref name="built"/> is returned as is.
    /// </summary>
    public static WorkspaceManifest KeepSchema(WorkspaceManifest built, WorkspaceManifest existing)
    {
        if (existing.SchemaVersion < WorkspaceManifestFile.MaxSchemaVersion)
            return built;

        return built with
        {
            SchemaVersion = WorkspaceManifestFile.MaxSchemaVersion,
            Workspace = built.Workspace with { Reference = built.Workspace.Reference ?? false },
            Repositories = (built.Repositories ?? [])
                .Select(repository => repository with { Reference = repository.Reference ?? false })
                .ToList(),
        };
    }
}
