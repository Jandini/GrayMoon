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
    int UncommittedChangedFileCount,
    /// <summary>HEAD SHA for the selected context (Feature Create PR compares to <see cref="FeatureBaseCommitSha"/>).</summary>
    string? HeadCommit = null,
    /// <summary>Feature creation tip SHA when viewing a Feature; null for Workspace.</summary>
    string? FeatureBaseCommitSha = null,
    /// <summary>Feature parent / PR-base branch when viewing a Feature; null for Workspace or when unknown.</summary>
    string? ParentBranchName = null,
    /// <summary>Tag the Feature repository is pinned to when viewing a Feature; null for Workspace or an unpinned repository.</summary>
    string? FeaturePinnedTag = null);

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
    bool HasOpenPr,
    /// <summary>
    /// True when the Feature has at least one pull request, every pull request is merged or closed (none open),
    /// and no repository has commits outside a pull request (not <see cref="HasCreatablePr"/>). Always false for
    /// the special Workspace, where "Remove" is never the header's primary action (E2).
    /// </summary>
    bool AllFeaturePrsCompleted = false,
    /// <summary>
    /// True when at least one repository not on a tag has an out-of-date version file. Only computed for a
    /// workspace without the dependency graph, which offers file-version updating as its own action; a
    /// dependency workspace folds out-of-date files into <see cref="HasUnmatchedDependencies"/> instead.
    /// </summary>
    bool HasOutOfDateFiles = false);

/// <summary>Lightweight row for virtual-scroll index (no PR/join payload).</summary>
public sealed record WorkspaceRepositoryLinkIndexEntry(
    int WorkspaceRepositoryId,
    int RepositoryId,
    int? DependencyLevel);
