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

    /// <summary>Pull request for the same branch in the repository this one was forked from (upstream). Null columns when there is none.</summary>
    public int? ParentPullRequestNumber { get; set; }
    public string? ParentState { get; set; }
    public bool? ParentMergeable { get; set; }
    public string? ParentMergeableState { get; set; }
    public string? ParentHtmlUrl { get; set; }
    public DateTimeOffset? ParentMergedAt { get; set; }
    public int? ParentChangedFiles { get; set; }

    /// <summary>The pull request in the cloned repository, or null when the row records none.</summary>
    public PullRequestInfo? ToOwnPullRequestInfo() => PullRequestNumber.HasValue
        ? new PullRequestInfo
        {
            Number = PullRequestNumber.Value,
            State = State ?? string.Empty,
            MergedAt = MergedAt,
            HtmlUrl = HtmlUrl ?? string.Empty,
            Mergeable = Mergeable,
            MergeableState = MergeableState,
            ChangedFiles = ChangedFiles
        }
        : null;

    /// <summary>The pull request in the upstream repository (fork only), or null when the row records none.</summary>
    public PullRequestInfo? ToParentPullRequestInfo() => ParentPullRequestNumber.HasValue
        ? new PullRequestInfo
        {
            Number = ParentPullRequestNumber.Value,
            State = ParentState ?? string.Empty,
            MergedAt = ParentMergedAt,
            HtmlUrl = ParentHtmlUrl ?? string.Empty,
            Mergeable = ParentMergeable,
            MergeableState = ParentMergeableState,
            ChangedFiles = ParentChangedFiles
        }
        : null;

    /// <summary>The single value the grid shows (see <see cref="PullRequestInfo.Compose"/>), or null when neither repository has a pull request.</summary>
    public PullRequestInfo? ToPullRequestInfo() => PullRequestInfo.Compose(ToOwnPullRequestInfo(), ToParentPullRequestInfo());
}