using System.Text.Json;
using GrayMoon.App.Services.Orchestration;
using GrayMoon.Application;
using GrayMoon.Application.Repositories.Policy;
using Microsoft.Extensions.DependencyInjection;
using static GrayMoon.App.Tests.Policy.Characterization.PolicyHarness;

namespace GrayMoon.App.Tests.Policy;

/// <summary>
/// A run records the repositories the policy left out. The record is logged, returned by the REST interface and shown
/// to the user as information.
/// </summary>
public sealed class RepositorySkipReportingTests
{
    private static readonly RepositorySkip SampleSkip =
        new(7, "graymoon-api", RepositoryOperationReason.OnTag, RepositoryOperationPolicy.OnTagMessage);

    [Fact]
    public async Task A_push_names_the_requested_repository_it_left_out()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await PinWorkspaceAsync(ctx, graph.ProducerId);
        RespondPushCommands(ctx);
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IWorkspacePushOperations>().PushAsync(
            ctx.WorkspaceId,
            special,
            new HashSet<int> { graph.ProducerId, graph.ConsumerId },
            synchronizedPush: false,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            restorePackages: false);

        var skip = Assert.Single(result.Skipped);
        Assert.Equal(graph.ProducerId, skip.RepositoryId);
        Assert.Equal(ProducerName, skip.RepositoryName);
        Assert.Equal(RepositoryOperationReason.OnTag, skip.Reason);
    }

    [Fact]
    public async Task A_push_of_branch_repositories_leaves_nothing_out()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        RespondPushCommands(ctx);
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IWorkspacePushOperations>().PushAsync(
            ctx.WorkspaceId,
            special,
            new HashSet<int> { graph.ProducerId, graph.ConsumerId },
            synchronizedPush: false,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            restorePackages: false);

        Assert.Empty(result.Skipped);
    }

    [Fact]
    public async Task An_update_names_the_repository_it_left_out()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await PinWorkspaceAsync(ctx, graph.ProducerId);
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<DependencyUpdateOrchestrator>().RunAsync(
            ctx.WorkspaceId, special, CancellationToken.None, progress: null, onRepoError: (_, _) => { }, onLevelError: (_, _) => { });

        var skip = Assert.Single(result.Skipped);
        Assert.Equal(graph.ProducerId, skip.RepositoryId);
        Assert.Equal(RepositoryOperationReason.OnTag, skip.Reason);
    }

    [Fact]
    public void The_combined_results_carry_the_skips_of_their_parts()
    {
        var update = DependencyUpdateRunResult.Ok() with { Skipped = [SampleSkip] };
        var push = OperationResult.Ok() with { Skipped = [SampleSkip with { RepositoryId = 8 }] };

        var combined = new UpdateAndPushResult(true, update, push);
        var prepared = new PrepareWorkspaceResult(true, new Dictionary<int, string>(), Update: update);

        Assert.Equal([7, 8], combined.Skipped.Select(skip => skip.RepositoryId));
        Assert.Equal([7], prepared.Skipped.Select(skip => skip.RepositoryId));
        Assert.Empty(new UpdateAndPushResult(true, DependencyUpdateRunResult.Ok(), null).Skipped);
        Assert.Empty(new PrepareWorkspaceResult(false, new Dictionary<int, string>()).Skipped);
    }

    [Fact]
    public void Skips_are_part_of_the_REST_response_with_the_reason_by_name()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var reference = SampleSkip with { Reason = RepositoryOperationReason.Reference, Message = RepositoryOperationPolicy.ReferenceMessage };
        var update = DependencyUpdateRunResult.Ok() with { Skipped = [reference] };
        var push = OperationResult.Ok() with { Skipped = [reference] };

        foreach (var json in new[]
                 {
                     JsonSerializer.Serialize(push, web),
                     JsonSerializer.Serialize(update, web),
                     JsonSerializer.Serialize(new UpdateAndPushResult(true, update, push), web),
                     JsonSerializer.Serialize(new PrepareWorkspaceResult(true, new Dictionary<int, string>(), Update: update), web),
                 })
        {
            Assert.Contains("\"skipped\":[{\"repositoryId\":7,\"repositoryName\":\"graymoon-api\",\"reason\":\"Reference\"", json, StringComparison.Ordinal);
            Assert.Contains(RepositoryOperationPolicy.ReferenceMessage, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_summary_counts_read_only_repositories_and_tag_repositories_apart()
    {
        var reference = SampleSkip with { Reason = RepositoryOperationReason.Reference };
        var snapshot = SampleSkip with { RepositoryId = 8, RepositoryName = "graymoon-web", Reason = RepositoryOperationReason.ReferenceSnapshot };
        var tag = SampleSkip with { RepositoryId = 9, RepositoryName = "graymoon-docs" };

        Assert.Null(RepositorySkipSummary.Describe([]));
        Assert.Null(RepositorySkipSummary.Describe(null));
        Assert.Equal("1 read only repository skipped: graymoon-api.", RepositorySkipSummary.Describe([reference, reference]));
        Assert.Equal(
            "2 read only repositories skipped: graymoon-api, graymoon-web. 1 repository on a tag skipped: graymoon-docs.",
            RepositorySkipSummary.Describe([reference, tag, snapshot]));
    }

    [Fact]
    public void The_summary_names_five_repositories_and_counts_the_rest()
    {
        var skips = Enumerable.Range(1, 7)
            .Select(i => new RepositorySkip(i, "repo-" + i, RepositoryOperationReason.Reference, RepositoryOperationPolicy.ReferenceMessage))
            .ToList();

        Assert.Equal(
            "7 read only repositories skipped: repo-1, repo-2, repo-3, repo-4, repo-5, and 2 more.",
            RepositorySkipSummary.Describe(skips));
    }

    [Fact]
    public void Skips_are_shown_as_information_and_never_in_place_of_an_error_or_a_warning()
    {
        var skipped = OperationResult.Ok() with { Skipped = [SampleSkip] };
        var failed = OperationResult.Fail("boom") with { Skipped = [SampleSkip] };
        var warned = OperationResult.Ok() with { Skipped = [SampleSkip], Warnings = ["restore failed"], WarningSummary = "restore failed" };

        Assert.Equal(["info:1 repository on a tag skipped: graymoon-api."], Shown(skipped));
        Assert.Equal(["error:boom"], Shown(failed));
        Assert.Equal(["warning:restore failed"], Shown(warned));
        Assert.Empty(Shown(OperationResult.Ok()));

        // A caller without an information surface shows nothing for a skip.
        var shown = new List<string>();
        skipped.ShowRepoErrors(message => shown.Add(message));
        Assert.Empty(shown);
    }

    private static List<string> Shown(OperationResult result)
    {
        var shown = new List<string>();
        result.ShowRepoErrors(
            message => shown.Add("error:" + message),
            message => shown.Add("warning:" + message),
            message => shown.Add("info:" + message));
        return shown;
    }
}
