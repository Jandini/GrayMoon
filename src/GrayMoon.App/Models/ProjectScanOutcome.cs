namespace GrayMoon.App.Models;

/// <summary>How a repository's project scan ended. Only <see cref="Success"/> and <see cref="EmptySuccess"/> are authoritative.</summary>
public enum ProjectScanStatus
{
    /// <summary>The scan ran and found projects.</summary>
    Success,

    /// <summary>The scan ran and found no projects: the repository's physical projects are removed.</summary>
    EmptySuccess,

    /// <summary>The scan failed, was cancelled, or did not run (project discovery off, repository not cloned). Persisted state is left alone.</summary>
    Failure
}

/// <summary>One repository's project scan result for <c>WorkspaceProjectRepository.ReconcileProjectSnapshotAsync</c>.</summary>
public sealed record ProjectScanOutcome
{
    public required int RepositoryId { get; init; }
    public required ProjectScanStatus Status { get; init; }
    public IReadOnlyList<SyncProjectInfo> Projects { get; init; } = [];

    public bool IsAuthoritative => Status != ProjectScanStatus.Failure;

    public static ProjectScanOutcome Scanned(int repositoryId, IReadOnlyList<SyncProjectInfo> projects) => new()
    {
        RepositoryId = repositoryId,
        Status = projects.Count == 0 ? ProjectScanStatus.EmptySuccess : ProjectScanStatus.Success,
        Projects = projects
    };

    public static ProjectScanOutcome Failed(int repositoryId) => new() { RepositoryId = repositoryId, Status = ProjectScanStatus.Failure };

    /// <summary>Null means there was no authoritative project list (failed or not scanned); an empty list is an authoritative empty scan.</summary>
    public static ProjectScanOutcome FromProjectsDetail(int repositoryId, IReadOnlyList<SyncProjectInfo>? projectsDetail) =>
        projectsDetail is null ? Failed(repositoryId) : Scanned(repositoryId, projectsDetail);
}
