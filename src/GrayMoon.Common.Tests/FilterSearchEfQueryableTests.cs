using System.Linq.Expressions;
using GrayMoon.Common.Search;

namespace GrayMoon.Common.Tests;

public class FilterSearchEfQueryableTests
{
    private readonly TestRepository _repo = new(
        Name: "graymoon-api",
        Owner: "acme",
        Topics: "blazor,angular",
        Connector: "github-prod");

    private readonly TestProject _project = new(
        Name: "GrayMoon.App",
        File: "src/GrayMoon.App/GrayMoon.App.csproj",
        Type: "Service",
        Framework: "net10.0");

    private readonly TestWorkspaceLink _workspaceLink = new(
        RepositoryName: "graymoon-api",
        BranchName: "main",
        GitVersion: "1.2.3",
        DependencyLevel: 2,
        SyncStatus: "in sync");

    [Fact]
    public void Repository_filter_parity_empty_query()
    {
        AssertRepositoryParity(null);
        AssertRepositoryParity("   ");
    }

    [Fact]
    public void Repository_filter_parity_implicit_and()
    {
        AssertRepositoryParity("gray api");
        AssertRepositoryParity("gray zz");
    }

    [Fact]
    public void Repository_filter_parity_or_and_parens()
    {
        AssertRepositoryParity("zz or api");
        AssertRepositoryParity("(zz or api) and gray");
    }

    [Fact]
    public void Repository_filter_parity_topic_and_unknown_field()
    {
        AssertRepositoryParity("topic:blazor");
        AssertRepositoryParity("topic:zz");
        AssertRepositoryParity("status:open");
    }

    [Fact]
    public void Repository_filter_parity_legacy_invalid_syntax()
    {
        AssertRepositoryParity("(api");
        AssertRepositoryParity("  gray   api  ");
    }

    [Fact]
    public void Project_filter_parity_type_and_framework()
    {
        AssertProjectParity("type:service");
        AssertProjectParity("framework:net10");
        AssertProjectParity("status:open");
    }

    [Fact]
    public void Project_filter_parity_unqualified_haystack()
    {
        AssertProjectParity("GrayMoon.App");
        AssertProjectParity("csproj");
        AssertProjectParity("missing");
    }

    [Fact]
    public void Workspace_link_filter_parity_empty_query()
    {
        AssertWorkspaceLinkParity(null);
        AssertWorkspaceLinkParity("   ");
    }

    [Fact]
    public void Workspace_link_filter_parity_implicit_and()
    {
        AssertWorkspaceLinkParity("graymoon main");
        AssertWorkspaceLinkParity("graymoon zz");
    }

    [Fact]
    public void Workspace_link_filter_parity_or_and_parens()
    {
        AssertWorkspaceLinkParity("zz or main");
        AssertWorkspaceLinkParity("(zz or main) and graymoon");
    }

    [Fact]
    public void Workspace_link_filter_parity_topic_and_unknown_field()
    {
        AssertWorkspaceLinkParity("topic:blazor");
        AssertWorkspaceLinkParity("status:open");
    }

    [Fact]
    public void Workspace_link_filter_parity_sync_and_level_text()
    {
        AssertWorkspaceLinkParity("in sync");
        AssertWorkspaceLinkParity("level 2");
        AssertWorkspaceLinkParity("no dependencies");
    }

    [Fact]
    public void Workspace_link_filter_parity_legacy_invalid_syntax()
    {
        AssertWorkspaceLinkParity("(main");
        AssertWorkspaceLinkParity("  graymoon   main  ");
    }

    private void AssertWorkspaceLinkParity(string? query)
    {
        var expected = FilterSearchExpression.Matches(query, _workspaceLink.Matches);
        var filter = FilterSearchEfQueryable.BuildFilter(query, BuildWorkspaceLinkTermPredicate);
        var actual = filter is null || filter.Compile()(_workspaceLink);
        Assert.Equal(expected, actual);
    }

    private void AssertRepositoryParity(string? query)
    {
        var expected = FilterSearchExpression.Matches(query, _repo.Matches);
        var filter = FilterSearchEfQueryable.BuildFilter(query, BuildRepositoryTermPredicate);
        var actual = filter is null || filter.Compile()(_repo);
        Assert.Equal(expected, actual);
    }

    private void AssertProjectParity(string? query)
    {
        var expected = FilterSearchExpression.Matches(query, _project.Matches);
        var filter = FilterSearchEfQueryable.BuildFilter(query, BuildProjectTermPredicate);
        var actual = filter is null || filter.Compile()(_project);
        Assert.Equal(expected, actual);
    }

    private static Expression<Func<TestRepository, bool>> BuildRepositoryTermPredicate(FilterSearchTerm term)
    {
        if (!string.IsNullOrEmpty(term.Field))
        {
            if (term.Field == "topic")
            {
                var value = term.Value;
                return r => r.Topics.Contains(value, StringComparison.OrdinalIgnoreCase);
            }

            return _ => true;
        }

        var termValue = term.Value;
        return r => r.Name.Contains(termValue, StringComparison.OrdinalIgnoreCase)
                    || r.Owner.Contains(termValue, StringComparison.OrdinalIgnoreCase)
                    || r.Topics.Contains(termValue, StringComparison.OrdinalIgnoreCase)
                    || r.Connector.Contains(termValue, StringComparison.OrdinalIgnoreCase);
    }

    private static Expression<Func<TestProject, bool>> BuildProjectTermPredicate(FilterSearchTerm term)
    {
        if (!string.IsNullOrEmpty(term.Field))
        {
            var value = term.Value;
            return term.Field switch
            {
                "type" => p => p.Type.Contains(value, StringComparison.OrdinalIgnoreCase),
                "framework" => p => p.Framework.Contains(value, StringComparison.OrdinalIgnoreCase),
                _ => _ => false,
            };
        }

        var termValue = term.Value;
        return p => p.Name.Contains(termValue, StringComparison.OrdinalIgnoreCase)
                    || p.File.Contains(termValue, StringComparison.OrdinalIgnoreCase)
                    || p.Type.Contains(termValue, StringComparison.OrdinalIgnoreCase)
                    || p.Framework.Contains(termValue, StringComparison.OrdinalIgnoreCase);
    }

    private static Expression<Func<TestWorkspaceLink, bool>> BuildWorkspaceLinkTermPredicate(FilterSearchTerm term)
    {
        if (!string.IsNullOrEmpty(term.Field))
        {
            return term.Field switch
            {
                "topic" => _ => false,
                _ => _ => true,
            };
        }

        var termValue = term.Value;
        return link => link.RepositoryName.Contains(termValue, StringComparison.OrdinalIgnoreCase)
                       || link.BranchName.Contains(termValue, StringComparison.OrdinalIgnoreCase)
                       || link.GitVersion.Contains(termValue, StringComparison.OrdinalIgnoreCase)
                       || link.LevelTitle.Contains(termValue, StringComparison.OrdinalIgnoreCase)
                       || link.SyncStatus.Contains(termValue, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record TestRepository(string Name, string Owner, string Topics, string Connector)
    {
        public bool Matches(FilterSearchTerm term)
        {
            if (!string.IsNullOrEmpty(term.Field))
            {
                return term.Field switch
                {
                    "topic" => Topics.Contains(term.Value, StringComparison.OrdinalIgnoreCase),
                    _ => true,
                };
            }

            return Name.Contains(term.Value, StringComparison.OrdinalIgnoreCase)
                   || Owner.Contains(term.Value, StringComparison.OrdinalIgnoreCase)
                   || Topics.Contains(term.Value, StringComparison.OrdinalIgnoreCase)
                   || Connector.Contains(term.Value, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed record TestProject(string Name, string File, string Type, string Framework)
    {
        public bool Matches(FilterSearchTerm term)
        {
            if (!string.IsNullOrEmpty(term.Field))
            {
                return term.Field switch
                {
                    "type" => Type.Contains(term.Value, StringComparison.OrdinalIgnoreCase),
                    "framework" => Framework.Contains(term.Value, StringComparison.OrdinalIgnoreCase),
                    _ => false,
                };
            }

            var haystack = $"{Name} {File} {Type} {Framework}";
            return haystack.Contains(term.Value, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed record TestWorkspaceLink(
        string RepositoryName,
        string BranchName,
        string GitVersion,
        int? DependencyLevel,
        string SyncStatus)
    {
        public string LevelTitle => DependencyLevel == null ? "No dependencies" : $"Level {DependencyLevel}";

        public bool Matches(FilterSearchTerm term)
        {
            if (!string.IsNullOrEmpty(term.Field))
            {
                return term.Field switch
                {
                    "topic" => false,
                    _ => true,
                };
            }

            var searchable = $"{RepositoryName} {BranchName} {GitVersion} {LevelTitle} {SyncStatus}";
            return searchable.Contains(term.Value, StringComparison.OrdinalIgnoreCase);
        }
    }
}
