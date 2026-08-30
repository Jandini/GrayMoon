using GrayMoon.Common.Git;

namespace GrayMoon.App.Services.GitChanges;

public sealed class GitChangesMutationResult
{
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public GitChangeSnapshot? Snapshot { get; set; }
}

public sealed class GitChangesCommitResult
{
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? CommitSha { get; set; }
    public GitChangeSnapshot? Snapshot { get; set; }
}
