using GrayMoon.Abstractions.Notifications;
using GrayMoon.Agent.Models;

namespace GrayMoon.Agent.Commands;

internal static class RepositorySyncProjectMapper
{
    public static List<RepositorySyncProjectNotification>? ToNotifications(IReadOnlyList<CsProjFileInfo>? projects)
    {
        if (projects == null)
            return null;

        return projects
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => new RepositorySyncProjectNotification
            {
                Name = p.Name.Trim(),
                ProjectType = (int)p.ProjectType,
                ProjectPath = p.ProjectPath ?? "",
                TargetFramework = p.TargetFramework ?? "",
                PackageId = p.PackageId,
                PackageReferences = p.PackageReferences
                    .Where(pr => !string.IsNullOrWhiteSpace(pr.Name))
                    .Select(pr => new RepositorySyncPackageReferenceNotification
                    {
                        Name = pr.Name.Trim(),
                        Version = pr.Version ?? ""
                    })
                    .ToList()
            })
            .ToList();
    }
}
