using System.Text.RegularExpressions;

namespace GrayMoon.App.Tests;

/// <summary>
/// Every page that shows one context of a Workspace must follow its <c>?context=</c> value while it stays open.
/// Four pages read it once: browser back and forward then left the other context's data on screen. This test
/// reads the page sources, so a new context page that forgets the rule fails here and not in front of a user.
/// </summary>
public sealed class ContextPageBindingDisciplineTests
{
    /// <summary>Pages that carry <c>ContextQuery</c> and follow it with their own, older mechanism.</summary>
    private static readonly Dictionary<string, string> OwnMechanism = new(StringComparer.Ordinal)
    {
        ["WorkspaceRepositories"] = "_boundContextQuery != ContextQuery",
        ["WorkspaceGitChanges"] = "_loadedContextQuery != ContextQuery",
        ["WorkspaceProjects"] = "_loadedContextId != ContextQuery",
    };

    public static TheoryData<string> ContextPages()
    {
        var data = new TheoryData<string>();
        foreach (var page in FindContextPages())
            data.Add(page);
        return data;
    }

    [Fact]
    public void The_pages_this_test_knows_are_the_pages_that_take_a_context()
        => Assert.Equal(
            ["WorkspaceActions", "WorkspaceDependencies", "WorkspaceFiles", "WorkspaceGitChanges", "WorkspacePackages", "WorkspaceProjects", "WorkspaceRepositories"],
            FindContextPages());

    [Theory]
    [MemberData(nameof(ContextPages))]
    public void A_context_page_reacts_to_a_changed_context_query_in_its_parameter_lifecycle(string page)
    {
        var source = ReadPage(page);
        var parametersSet = MethodBody(source, "OnParametersSetAsync");
        Assert.True(parametersSet is not null, $"{page} takes ?context= but has no OnParametersSetAsync: back and forward would not change what it shows.");

        if (OwnMechanism.TryGetValue(page, out var marker))
        {
            Assert.Contains(marker, parametersSet);
            return;
        }

        Assert.Contains("_contextBinding.QueryChanged(ContextQuery)", parametersSet);
        Assert.Contains("ResolveQueryChangeAsync(WorkspaceId, ContextQuery)", parametersSet);
    }

    [Theory]
    [InlineData("WorkspaceActions", "LoadWorkspaceAsync")]
    [InlineData("WorkspaceDependencies", "LoadAsync")]
    [InlineData("WorkspaceFiles", "LoadAsync")]
    [InlineData("WorkspacePackages", "LoadAsync")]
    public void A_bound_page_loads_one_at_a_time_and_drops_a_load_that_was_overtaken(string page, string loadMethod)
    {
        var source = ReadPage(page);
        var load = MethodBody(source, loadMethod);
        Assert.NotNull(load);

        // The generation and the context are read once, before the first await.
        var firstAwait = load!.IndexOf("await ", StringComparison.Ordinal);
        var capture = load.IndexOf("var generation = _contextBinding.Generation;", StringComparison.Ordinal);
        Assert.True(capture >= 0 && capture < firstAwait, $"{page}.{loadMethod} must capture the generation before its first await.");
        // Nothing read after an await may be assigned without asking, and the selection is not read again.
        Assert.True(Regex.Matches(load, @"_contextBinding\.IsCurrent\(generation").Count >= 3, $"{page}.{loadMethod} must check the generation before it assigns and in its failure path.");
        Assert.DoesNotMatch(@"await [^;]*\b_selectedContextId\b", load);

        // Every context switch goes through the one place that binds and serializes.
        var select = MethodBody(source, "SelectContextAsync");
        Assert.NotNull(select);
        Assert.Contains("_contextBinding.Bind(info.ContextId, info.IsSpecialWorkspace)", select);
        Assert.Contains($"_contextBinding.RunLatestAsync({loadMethod})", select);
        Assert.Contains("SelectContextAsync(await ContextNavigation.ResolveSelectionAsync(WorkspaceId, contextId))", MethodBody(source, "OnSelectedContextChangedAsync"));
    }

    private static List<string> FindContextPages()
        => Directory.EnumerateFiles(PagesDirectory(), "*.*")
            .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"public int\? ContextQuery \{ get; set; \}"))
            .Select(f => Path.GetFileName(f).Split('.')[0])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>All source of one page: its .razor and every partial .cs file.</summary>
    private static string ReadPage(string page)
        => string.Join(
            "\n",
            Directory.EnumerateFiles(PagesDirectory(), page + ".*")
                .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .Select(File.ReadAllText));

    /// <summary>The text of a method from its name to the brace that closes it, or to the end of an expression body.</summary>
    private static string? MethodBody(string source, string name)
    {
        var match = Regex.Match(source, @"\b(?:Task|void)\s+" + Regex.Escape(name) + @"\s*\(");
        if (!match.Success)
            return null;

        var open = source.IndexOf('{', match.Index);
        var arrow = source.IndexOf("=>", match.Index, StringComparison.Ordinal);
        if (arrow >= 0 && (open < 0 || arrow < open))
            return source[match.Index..(source.IndexOf(';', arrow) + 1)];

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[match.Index..(i + 1)];
        }

        return null;
    }

    private static string PagesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GrayMoon.slnx")))
            directory = directory.Parent;
        Assert.True(directory is not null, "GrayMoon.slnx was not found above the test output directory.");
        return Path.Combine(directory!.FullName, "src", "GrayMoon.App", "Components", "Pages");
    }
}
