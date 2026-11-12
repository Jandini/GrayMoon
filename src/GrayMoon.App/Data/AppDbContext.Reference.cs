using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Data;

/// <summary>
/// The Workspace repository is never a reference repository: it is where GrayMoon writes the Workspace definition.
/// Every save of this context enforces that, so no writer has to remember it.
/// </summary>
public partial class AppDbContext
{
    private void KeepWorkspaceRepositoryWritable()
    {
        foreach (var entry in ChangeTracker.Entries<WorkspaceRepositoryLink>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
                continue;

            if (entry.Entity.Role == WorkspaceRepositoryRole.Workspace && entry.Entity.IsReference)
                entry.Entity.IsReference = false;
        }
    }
}
