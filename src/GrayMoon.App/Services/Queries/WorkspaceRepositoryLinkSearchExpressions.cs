using System.Linq.Expressions;
using GrayMoon.App.Models;
using GrayMoon.Common.Search;

namespace GrayMoon.App.Services.Queries;

internal static class WorkspaceRepositoryLinkSearchExpressions
{
    internal static int GetRepositoryTypeSortKey(ProjectType? type) => type switch
    {
        ProjectType.Service => 0,
        ProjectType.Package => 1,
        ProjectType.Executable => 2,
        ProjectType.Library => 3,
        ProjectType.Test => 4,
        _ => 5,
    };

    public static Expression<Func<WorkspaceRepositoryLink, bool>> BuildTermPredicate(FilterSearchTerm term)
    {
        if (!string.IsNullOrEmpty(term.Field))
        {
            return term.Field switch
            {
                "topic" => _ => false,
                _ => _ => true,
            };
        }

        var termValue = term.Value.ToLowerInvariant();
        return wr =>
            (wr.Repository != null ? wr.Repository.RepositoryName : string.Empty).ToLower().Contains(termValue)
            || (wr.BranchName ?? string.Empty).ToLower().Contains(termValue)
            || (wr.GitVersion ?? string.Empty).ToLower().Contains(termValue)
            || (wr.DependencyLevel == null
                ? "no dependencies"
                : ("level " + wr.DependencyLevel.ToString())).Contains(termValue)
            || (wr.SyncStatus == RepoSyncStatus.InSync ? "in sync"
                : wr.SyncStatus == RepoSyncStatus.NeedsSync ? "sync"
                : wr.SyncStatus == RepoSyncStatus.NotCloned ? "not cloned"
                : wr.SyncStatus == RepoSyncStatus.VersionMismatch ? "version"
                : wr.SyncStatus == RepoSyncStatus.Error ? "error"
                : "sync").Contains(termValue);
    }
}
