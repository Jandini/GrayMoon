using GrayMoon.App.Models;

namespace GrayMoon.App.Services.Queries;

internal static class WorkspaceRepositoryLinkListMapper
{
    public static WorkspaceRepositoryLink ToLink(WorkspaceRepositoryLinkListItemDto dto)
    {
        var link = new WorkspaceRepositoryLink
        {
            WorkspaceRepositoryId = dto.WorkspaceRepositoryId,
            WorkspaceId = dto.WorkspaceId,
            RepositoryId = dto.RepositoryId,
            GitVersion = dto.GitVersion,
            BranchName = dto.BranchName,
            CheckedOutTag = dto.CheckedOutTag,
            DefaultBranchName = dto.DefaultBranchName,
            OutgoingCommits = dto.OutgoingCommits,
            IncomingCommits = dto.IncomingCommits,
            DefaultBranchBehindCommits = dto.DefaultBranchBehindCommits,
            DefaultBranchAheadCommits = dto.DefaultBranchAheadCommits,
            BranchHasUpstream = dto.BranchHasUpstream,
            SyncStatus = dto.SyncStatus,
            DependencyLevel = dto.DependencyLevel,
            Dependencies = dto.Dependencies,
            UnmatchedDeps = dto.UnmatchedDeps,
            OutOfDateFileRepos = dto.OutOfDateFileRepos,
            RepositoryType = dto.RepositoryType,
            HasNewerTag = dto.HasNewerTag,
            Repository = new Repository
            {
                RepositoryId = dto.RepositoryId,
                RepositoryName = dto.RepositoryName,
                CloneUrl = dto.CloneUrl,
            },
        };

        if (dto.PullRequestNumber.HasValue || !string.IsNullOrEmpty(dto.PullRequestState))
        {
            link.PullRequest = new WorkspaceRepositoryPullRequest
            {
                WorkspaceRepositoryId = dto.WorkspaceRepositoryId,
                PullRequestNumber = dto.PullRequestNumber,
                State = dto.PullRequestState,
                HtmlUrl = dto.PullRequestHtmlUrl,
                MergedAt = dto.PullRequestMergedAt,
                Mergeable = dto.PullRequestMergeable,
                MergeableState = dto.PullRequestMergeableState,
                ChangedFiles = dto.PullRequestChangedFiles,
            };
        }

        return link;
    }

    public static PullRequestInfo? ToPullRequestInfo(WorkspaceRepositoryLinkListItemDto dto)
    {
        if (!dto.PullRequestNumber.HasValue && string.IsNullOrEmpty(dto.PullRequestState))
        {
            return null;
        }

        return new PullRequestInfo
        {
            Number = dto.PullRequestNumber ?? 0,
            State = dto.PullRequestState ?? string.Empty,
            HtmlUrl = dto.PullRequestHtmlUrl ?? string.Empty,
            MergedAt = dto.PullRequestMergedAt,
            Mergeable = dto.PullRequestMergeable,
            MergeableState = dto.PullRequestMergeableState,
            ChangedFiles = dto.PullRequestChangedFiles,
        };
    }
}
