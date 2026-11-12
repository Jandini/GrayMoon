using GrayMoon.App.Components.GitChanges;
using GrayMoon.App.Components.Pages;
using GrayMoon.App.Components.Shared;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.App.Services.Ui;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace GrayMoon.App.Tests.Policy;

/// <summary>
/// The Razor markup of the read only indicator, rendered to HTML by the framework's <see cref="HtmlRenderer"/>.
/// This is the first render only: no browser, no CSS and no click is involved.
/// </summary>
public sealed class ReferenceMarkupRenderTests
{
    private const string LockClass = "reference-lock-icon";

    private static async Task<string> RenderAsync<TComponent>(
        Dictionary<string, object?> parameters, Action<IServiceCollection>? configure = null)
        where TComponent : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Script calls happen after the first render, which this renderer never reaches.
        services.AddSingleton<IJSRuntime>(new NoScript());
        configure?.Invoke(services);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<TComponent>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }

    private sealed class NoScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException($"Script call '{identifier}' during the first render.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new InvalidOperationException($"Script call '{identifier}' during the first render.");
    }

    private static string Encoded(string text) => System.Net.WebUtility.HtmlEncode(text);

    // ---- the icon

    [Fact]
    public async Task The_padlock_with_a_title_is_an_image_named_Read_only()
    {
        var html = await RenderAsync<ReferenceLockIcon>(new() { ["Title"] = "Why it is read only", ["AdditionalClass"] = "text-muted" });

        Assert.Contains(LockClass, html);
        Assert.Contains("text-muted", html);
        Assert.Contains("title=\"Why it is read only\"", html);
        Assert.Contains("role=\"img\"", html);
        Assert.Contains("aria-label=\"Read only\"", html);
        Assert.DoesNotContain("aria-hidden=\"true\"", html.Split("<svg")[0]);
    }

    [Fact]
    public async Task The_padlock_without_a_title_is_hidden_from_assistive_technology()
    {
        var html = await RenderAsync<ReferenceLockIcon>([]);
        var span = html.Split("<svg")[0];

        Assert.Contains("aria-hidden=\"true\"", span);
        Assert.DoesNotContain("role=", span);
        Assert.DoesNotContain("title=", span);
    }

    [Fact]
    public async Task The_padlock_is_square_keeps_its_width_and_has_a_keyhole()
    {
        var html = await RenderAsync<ReferenceLockIcon>([]);

        Assert.Contains("viewBox=\"0 0 16 16\"", html);
        Assert.Contains("width=\"1em\"", html);
        Assert.Contains("height=\"1em\"", html);
        Assert.Contains("flex-shrink-0", html);
        // The keyhole is a second outline in the body, cut out by the even-odd rule.
        Assert.Contains("fill-rule=\"evenodd\"", html);
    }

    [Fact]
    public async Task The_open_padlock_has_another_shackle_than_the_closed_one()
    {
        var closed = await RenderAsync<ReferenceLockIcon>([]);
        var open = await RenderAsync<ReferenceLockIcon>(new() { ["Open"] = true });

        Assert.NotEqual(closed, open);
        Assert.Contains("a3 3 0 0 1 6 0v2.9", closed);
        Assert.DoesNotContain("a3 3 0 0 1 6 0v2.9", open);
    }

    // ---- the row of Workspace repositories

    private static WorkspaceRepositoryLink Link(bool reference = false, bool snapshot = false, string? cloneUrl = "https://github.com/acme/widgets.git") => new()
    {
        WorkspaceRepositoryId = 11,
        WorkspaceId = 1,
        RepositoryId = 7,
        BranchName = "main",
        DefaultBranchName = "main",
        IsReference = reference,
        IsReferenceSnapshot = snapshot,
        Repository = new Repository { RepositoryId = 7, RepositoryName = "widgets", CloneUrl = cloneUrl! },
    };

    private static Task<string> RenderRowAsync(WorkspaceRepositoryLink link) =>
        RenderAsync<WorkspaceRepositoriesRow>(
            new() { ["WorkspaceId"] = 1, ["Link"] = link },
            // The row only uses it on a click.
            services => services.AddSingleton(new WorkspaceContextNavigationService(null!, null!, null!, null!)));

    [Fact]
    public async Task A_read_only_repository_shows_the_padlock_after_its_name_and_no_readonly_badge()
    {
        var html = await RenderRowAsync(Link(reference: true));

        Assert.Contains(LockClass, html);
        Assert.Contains($"title=\"{Encoded(RepositoryBadgeTitles.Reference)}\"", html);
        Assert.True(html.IndexOf("widgets", StringComparison.Ordinal) < html.IndexOf(LockClass, StringComparison.Ordinal));
        Assert.DoesNotContain(">readonly<", html);
        Assert.DoesNotContain(">read only<", html);
    }

    [Fact]
    public async Task A_read_only_repository_without_a_web_address_shows_the_padlock_too()
    {
        var html = await RenderRowAsync(Link(reference: true, cloneUrl: ""));

        Assert.Contains(LockClass, html);
        Assert.DoesNotContain("class=\"repo-link\"", html);
    }

    [Fact]
    public async Task A_snapshot_in_a_Feature_shows_the_padlock_with_the_Feature_text()
    {
        var html = await RenderRowAsync(Link(snapshot: true));

        Assert.Contains($"title=\"{Encoded(RepositoryBadgeTitles.ReferenceSnapshot)}\"", html);
        Assert.DoesNotContain($"title=\"{Encoded(RepositoryBadgeTitles.Reference)}\"", html);
    }

    [Fact]
    public async Task A_writable_repository_shows_no_padlock()
    {
        var html = await RenderRowAsync(Link());

        Assert.Contains("widgets", html);
        Assert.DoesNotContain(LockClass, html);
    }

    // ---- the tree of Git Changes

    // The tree draws its rows in a virtualized list, which draws nothing before the browser reports its size. So the
    // label of the repository row is rendered on its own, from the row the real builder makes.
    private static Task<string> RenderTreeAsync(bool reference)
    {
        var rows = GitChangesTreeBuilder.Build(
            new WorkspaceGitChangesView
            {
                WorkspaceId = 1,
                Repositories =
                [
                    new WorkspaceGitChangesRepositoryView
                    {
                        WorkspaceRepositoryId = 11,
                        RepositoryId = 7,
                        RepositoryName = "widgets",
                        IsReference = reference,
                        ChangedCount = 1,
                        Changes =
                        [
                            new WorkspaceGitChangeEntryView { Path = "src/a.cs", WorktreeChange = GrayMoon.Common.Git.GitChangeKind.Modified, IsTracked = true },
                        ],
                    },
                ],
            },
            filterQuery: null);

        var repositoryRow = rows.First(row => row.Kind == GitChangesTreeRowKind.Repository);
        return RenderAsync<GitChangesRepositoryLabel>(new() { ["Row"] = repositoryRow });
    }

    [Fact]
    public async Task The_Git_Changes_tree_draws_the_repository_label_through_the_label_component()
    {
        // Guards the one line that ties the rendered label to the tree.
        var source = await File.ReadAllTextAsync(Path.Combine(
            GrayMoon.App.Tests.Conventions.ConventionSource.AppDirectory, "Components", "GitChanges", "GitChangesTree.razor"));

        Assert.Contains("<GitChangesRepositoryLabel Row=\"row\" />", source);
        Assert.DoesNotContain("ReferenceLockIcon", source);
    }

    [Fact]
    public async Task Git_Changes_shows_the_padlock_after_the_name_of_a_read_only_repository()
    {
        var html = await RenderTreeAsync(reference: true);

        Assert.Contains("git-changes-tree__reference-lock", html);
        Assert.Contains($"title=\"{Encoded(RepositoryBadgeTitles.ReferenceInGitChanges)}\"", html);
        Assert.True(html.IndexOf("git-changes-tree__repo-name", StringComparison.Ordinal) < html.IndexOf("git-changes-tree__reference-lock", StringComparison.Ordinal));
        Assert.DoesNotContain(">readonly<", html);
    }

    [Fact]
    public async Task Git_Changes_shows_no_padlock_for_a_writable_repository()
    {
        var html = await RenderTreeAsync(reference: false);

        Assert.Contains("git-changes-tree__repo-name", html);
        Assert.DoesNotContain(LockClass, html);
    }
}
