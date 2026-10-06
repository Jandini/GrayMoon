using System.ComponentModel.DataAnnotations;
using GrayMoon.Abstractions.Workspaces;

namespace GrayMoon.App.Models;

public class Workspace
{
    public int WorkspaceId { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public bool IsDefault { get; set; }

    [MaxLength(500)]
    public string? RootPath { get; set; }

    public DateTime? LastSyncedAt { get; set; }

    public bool IsInSync { get; set; }

    public bool ExcludeAiWorkflows { get; set; } = true;

    /// <summary>
    /// One of the three independent profile axes: whether this Workspace does .NET project and NuGet
    /// package discovery, dependency levels and dependency-aware push, or is a plain multi-repository
    /// Git Workspace. It says nothing about versioning or CI, which are their own axes.
    /// </summary>
    /// <remarks>
    /// The model defaults are deliberately Basic/None/None so a brand-new database created by
    /// EnsureCreated() starts clean; <c>MigrateWorkspaceProfileColumnsAsync</c> backfills the rows of an
    /// existing database to the .NET triple instead, because every pre-profile Workspace must keep
    /// behaving exactly as it does today.
    /// </remarks>
    public WorkspaceType Type { get; set; } = WorkspaceType.Basic;

    /// <summary>
    /// One of the three independent profile axes: how repository versions are calculated. Independent of
    /// <see cref="Type"/> - a Basic Workspace may still use GitVersion.
    /// </summary>
    public WorkspaceVersioningMode VersioningMode { get; set; } = WorkspaceVersioningMode.None;

    /// <summary>
    /// One of the three independent profile axes: which CI system this Workspace is integrated with.
    /// Independent of <see cref="Type"/> and of GitHub source control, which stays available with no CI.
    /// </summary>
    public WorkspaceCiProvider CiProvider { get; set; } = WorkspaceCiProvider.None;

    /// <summary>Persisted GrayMoon-managed Feature storage root for this Workspace (e.g. C:\Users\name\.graymoon\AVR\features). Set on first Feature creation from the global Feature storage setting; not relocated when that setting or the Workspace name changes.</summary>
    [MaxLength(1000)]
    public string? ManagedFeatureStorageRoot { get; set; }

    public ICollection<WorkspaceRepositoryLink> Repositories { get; set; } = new List<WorkspaceRepositoryLink>();
}
