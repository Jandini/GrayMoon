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
            HasSelfFileVersionToken = dto.HasSelfFileVersionToken,
            UncommittedChangedFileCount = dto.UncommittedChangedFileCount,
            HeadCommit = dto.HeadCommit,
            FeatureBaseCommitSha = dto.FeatureBaseCommitSha,
            ParentBranchName = dto.ParentBranchName,
            FeaturePinnedTag = dto.FeaturePinnedTag,
            Role = dto.Role,
            GitVersionPending = dto.GitVersionPending,
            Repository = new Repository
            {
                RepositoryId = dto.RepositoryId,
                RepositoryName = dto.RepositoryName,
                CloneUrl = dto.CloneUrl,
                Archived = dto.Archived,
                IsFork = dto.IsFork,
                ParentCloneUrl = dto.ParentCloneUrl,
            },
        };

        if (dto.PullRequestNumber.HasValue || !string.IsNullOrEmpty(dto.PullRequestState) || dto.ParentPullRequestNumber.HasValue)
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
                ParentPullRequestNumber = dto.ParentPullRequestNumber,
                ParentState = dto.ParentPullRequestState,
                ParentHtmlUrl = dto.ParentPullRequestHtmlUrl,
                ParentMergedAt = dto.ParentPullRequestMergedAt,
                ParentMergeable = dto.ParentPullRequestMergeable,
                ParentMergeableState = dto.ParentPullRequestMergeableState,
                ParentChangedFiles = dto.ParentPullRequestChangedFiles,
            };
        }

        return link;
    }

    public static PullRequestInfo? ToPullRequestInfo(WorkspaceRepositoryLinkListItemDto dto)
    {
        PullRequestInfo? own = null;
        if (dto.PullRequestNumber.HasValue || !string.IsNullOrEmpty(dto.PullRequestState))
        {
            own = new PullRequestInfo
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

        PullRequestInfo? parent = null;
        if (dto.ParentPullRequestNumber.HasValue)
        {
            parent = new PullRequestInfo
            {
                Number = dto.ParentPullRequestNumber.Value,
                State = dto.ParentPullRequestState ?? string.Empty,
                HtmlUrl = dto.ParentPullRequestHtmlUrl ?? string.Empty,
                MergedAt = dto.ParentPullRequestMergedAt,
                Mergeable = dto.ParentPullRequestMergeable,
                MergeableState = dto.ParentPullRequestMergeableState,
                ChangedFiles = dto.ParentPullRequestChangedFiles,
            };
        }

        return PullRequestInfo.Compose(own, parent);
    }
}
