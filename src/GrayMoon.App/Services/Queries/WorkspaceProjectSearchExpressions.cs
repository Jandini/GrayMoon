using System.Linq.Expressions;
using GrayMoon.App.Models;
using GrayMoon.Common.Search;

namespace GrayMoon.App.Services.Queries;

internal static class WorkspaceProjectSearchExpressions
{
    public static Expression<Func<WorkspaceProject, bool>> BuildTermPredicate(FilterSearchTerm term)
    {
        if (!string.IsNullOrEmpty(term.Field))
        {
            var value = term.Value.ToLowerInvariant();
            return term.Field switch
            {
                "type" => p => p.ProjectType.ToString().ToLower().Contains(value),
                "framework" => p => p.TargetFramework.ToLower().Contains(value),
                _ => _ => false,
            };
        }

        var termValue = term.Value.ToLowerInvariant();
        return p => p.ProjectName.ToLower().Contains(termValue)
                    || p.ProjectFilePath.ToLower().Contains(termValue)
                    || p.ProjectType.ToString().ToLower().Contains(termValue)
                    || p.TargetFramework.ToLower().Contains(termValue);
    }

    internal static int GetProjectTypeSortKey(ProjectType type) => type switch
    {
        ProjectType.Service => 0,
        ProjectType.Library => 1,
        ProjectType.Package => 2,
        ProjectType.Test => 3,
        _ => 4,
    };
}
