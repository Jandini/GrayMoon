using GrayMoon.App.Models;
using GrayMoon.Common.Search;

namespace GrayMoon.App.Services;

public static class WorkspaceProjectSearchMatcher
{
    public static bool Matches(WorkspaceProject project, string? query) =>
        FilterSearchMatcher.Matches(query, term => MatchesTerm(project, term));

    private static bool MatchesTerm(WorkspaceProject project, FilterSearchTerm term)
    {
        if (!string.IsNullOrEmpty(term.Field))
        {
            return term.Field switch
            {
                "type" => project.ProjectType.ToString()
                    .Contains(term.Value, StringComparison.OrdinalIgnoreCase),
                "framework" => (project.TargetFramework ?? string.Empty)
                    .Contains(term.Value, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }

        var name = project.ProjectName ?? string.Empty;
        var file = project.ProjectFilePath ?? string.Empty;
        var haystack = $"{name} {file} {project.ProjectType} {project.TargetFramework}";
        return haystack.Contains(term.Value, StringComparison.OrdinalIgnoreCase);
    }
}
