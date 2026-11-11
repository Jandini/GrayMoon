using System.Text.Json;
using GrayMoon.App.Services.Orchestration;
using GrayMoon.Application;
using GrayMoon.Application.Repositories.Policy;
using Microsoft.Extensions.DependencyInjection;
using static GrayMoon.App.Tests.Policy.Characterization.PolicyHarness;

namespace GrayMoon.App.Tests.Policy;

/// <summary>
/// A run records the repositories the policy left out. The record is for the log and for later use: it is not part of
/// what the REST interface returns.
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
    public void Skips_are_not_part_of_the_REST_response()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var update = DependencyUpdateRunResult.Ok() with { Skipped = [SampleSkip] };
        var push = OperationResult.Ok() with { Skipped = [SampleSkip] };

        foreach (var json in new[]
                 {
                     JsonSerializer.Serialize(push, web),
                     JsonSerializer.Serialize(update, web),
                     JsonSerializer.Serialize(new UpdateAndPushResult(true, update, push), web),
                     JsonSerializer.Serialize(new PrepareWorkspaceResult(true, new Dictionary<int, string>(), Update: update), web),
                 })
        {
            Assert.DoesNotContain("skipped", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("graymoon-api", json, StringComparison.Ordinal);
        }
    }
}
