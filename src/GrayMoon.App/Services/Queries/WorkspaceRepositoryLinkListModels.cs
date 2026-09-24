using GrayMoon.App.Models;

namespace GrayMoon.App.Services.Queries;

public sealed record WorkspaceRepositoryLinkListCursor(
    int DependencyLevelSortKey,
    int RepositoryTypeSortKey,
    int DependenciesSortKey,
    int WorkspaceRepositoryId);

public sealed record WorkspaceRepositoryLinkListRequest(
    int WorkspaceId,
    string? Search,
    int PageSize,
    WorkspaceRepositoryLinkListCursor? Cursor);

public sealed record WorkspaceRepositoryLinkListItemDto(
    int WorkspaceRepositoryId,
    int WorkspaceId,
    int RepositoryId,
    string RepositoryName,
    string CloneUrl,
    string? GitVersion,
    string? BranchName,
    string? CheckedOutTag,
    string? DefaultBranchName,
    int? OutgoingCommits,
    int? IncomingCommits,
    int? DefaultBranchBehindCommits,
    int? DefaultBranchAheadCommits,
    bool? BranchHasUpstream,
    RepoSyncStatus SyncStatus,
    int? DependencyLevel,
    int? Dependencies,
    int? UnmatchedDeps,
    int? OutOfDateFileRepos,
    ProjectType? RepositoryType,
    bool? HasNewerTag,
    bool? HasSelfFileVersionToken,
    string? PullRequestState,
    int? PullRequestNumber,
    string? PullRequestHtmlUrl,
    DateTimeOffset? PullRequestMergedAt,
    bool? PullRequestMergeable,
    string? PullRequestMergeableState,
    int? PullRequestChangedFiles,
    bool Archived,
    int UncommittedChangedFileCount);

public sealed record WorkspaceRepositoryLinkListPageResult(
    IReadOnlyList<WorkspaceRepositoryLinkListItemDto> Items,
    WorkspaceRepositoryLinkListCursor? NextCursor,
    bool HasMore);

public sealed record WorkspaceRepositoryLinkListFilter(int WorkspaceId, string? Search);

public sealed record WorkspaceRepositoryHeaderStateDto(
    int TotalCount,
    bool HasUnmatchedDependencies,
    bool IsPushRecommended,
    bool HasIncomingCommits,
    bool HasTaggedRepos,
    bool IsOutOfSync,
    int? LowestLevelNeedingWork,
    /// <summary>True when at least one repository would show the yellow "create" PR badge (ahead of default, no open/merged/closed PR).</summary>
    bool HasCreatablePr,
    /// <summary>True when at least one repository has an open pull request in the selected context.</summary>
    bool HasOpenPr);

/// <summary>Lightweight row for virtual-scroll index (no PR/join payload).</summary>
public sealed record WorkspaceRepositoryLinkIndexEntry(
    int WorkspaceRepositoryId,
    int RepositoryId,
    int? DependencyLevel);
