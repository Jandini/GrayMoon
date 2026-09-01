using System.Text.Json;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceGitServiceStageAndCommitTests
{
    [Fact]
    public async Task CommitDependencyUpdatesAsync_sends_skipHooks_when_requested()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.AgentBridge.Respond("StageAndCommit", new StageAndCommitResponse { Success = true, Committed = true });

        await using var scope = ctx.CreateScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        var payload = new[]
        {
            new SyncDependenciesRepoPayload(
                ctx.RepositoryId,
                "graymoon-api",
                1,
                [new SyncDependenciesProjectUpdate("src/A.csproj", [("Pkg", "1.0.0", "1.1.0")])])
        };

        var results = await git.CommitDependencyUpdatesAsync(ctx.WorkspaceId, payload, skipHooks: true);

        var result = Assert.Single(results);
        Assert.True(result.Committed);
        Assert.Null(result.ErrorMessage);

        var call = Assert.Single(ctx.AgentBridge.Calls, c => c.Command == "StageAndCommit");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(call.Args));
        Assert.True(doc.RootElement.GetProperty("skipHooks").GetBoolean());
    }
}
