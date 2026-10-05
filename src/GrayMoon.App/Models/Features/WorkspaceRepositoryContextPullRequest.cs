using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GrayMoon.App.Models;

[Table("WorkspaceRepositoryContextPullRequests")]
public sealed class WorkspaceRepositoryContextPullRequest
{
    public int WorkspaceRepositoryContextPullRequestId { get; set; }

    [Required]
    public int WorkspaceFeatureContextId { get; set; }

    [ForeignKey(nameof(WorkspaceFeatureContextId))]
    public WorkspaceFeatureContext? WorkspaceFeatureContext { get; set; }

    [Required]
    public int WorkspaceRepositoryId { get; set; }

    [ForeignKey(nameof(WorkspaceRepositoryId))]
    public WorkspaceRepositoryLink? WorkspaceRepository { get; set; }

    public int? PullRequestNumber { get; set; }
    public string? State { get; set; }
    public bool? Mergeable { get; set; }
    public string? MergeableState { get; set; }
    public string? HtmlUrl { get; set; }
    public DateTimeOffset? MergedAt { get; set; }
    public int? ChangedFiles { get; set; }
    public DateTime LastCheckedAt { get; set; }

    public PullRequestInfo ToPullRequestInfo() => new()
    {
        Number = PullRequestNumber ?? 0,
        State = State ?? string.Empty,
        MergedAt = MergedAt,
        HtmlUrl = HtmlUrl ?? string.Empty,
        Mergeable = Mergeable,
        MergeableState = MergeableState,
        ChangedFiles = ChangedFiles
    };
}
