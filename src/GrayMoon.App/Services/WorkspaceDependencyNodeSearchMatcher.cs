using GrayMoon.App.Models;
using GrayMoon.Common.Search;

namespace GrayMoon.App.Services;

public static class WorkspaceDependencyNodeSearchMatcher
{
    public static bool Matches(RepositoryDependencyNode node, string? query) =>
        FilterSearchMatcher.MatchesHaystack(query, node.RepositoryName ?? string.Empty);
}
