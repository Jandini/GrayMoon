using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// Characterisation tests for <c>WorkspaceGitService.SyncToDefaultDirectAsync</c>: after the agent
/// reports a successful switch to the default branch, the link row and the branch rows must reflect
/// the branch that is now checked out.
/// </summary>
public sealed class SyncToDefaultPersistenceTests
{
    private static SyncToDefaultBranchResponse SuccessfulResponse() => new()
    {
        Success = true,
        CurrentBranch = "main",
        DefaultBranch = "main",
        LocalBranches = ["main"],
        RemoteBranches = ["origin/main"],
        Tags = [],
        OutgoingCommits = 0,
        IncomingCommits = 0,
        HasUpstream = true,
        DefaultBranchBehind = 0,
        DefaultBranchAhead = 0,
        GitVersion = "2.0.0",
        Projects =
        [
            new AgentProjectDto
            {
                Name = "Acme.Api",
                ProjectType = (int)ProjectType.Service,
                ProjectPath = "src/Acme.Api/Acme.Api.csproj",
                TargetFramework = "net10.0",
            }
        ],
    };

    [Fact]
    public async Task Successful_sync_persists_branch_version_counts_and_upstream()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.AgentBridge.Respond("SyncToDefaultBranch", SuccessfulResponse());

        await using var scope = ctx.CreateScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();

        var (success, error) = await git.SyncToDefaultDirectAsync(
            ctx.WorkspaceId, ctx.RepositoryId, "feature/x",
            deleteRemoteBranch: false, allowForceDeleteLocalBranch: true, CancellationToken.None);

        Assert.True(success);
        Assert.Null(error);

        var link = await ctx.ReadLinkAsync();
        Assert.Equal("main", link.BranchName);
        Assert.Equal("2.0.0", link.GitVersion);
        Assert.Equal(0, link.OutgoingCommits);
        Assert.Equal(0, link.IncomingCommits);
        Assert.Equal(0, link.DefaultBranchAheadCommits);
        Assert.Equal(0, link.DefaultBranchBehindCommits);
        Assert.True(link.BranchHasUpstream);
        Assert.Null(link.CheckedOutTag);
    }

    [Fact]
    public async Task Successful_sync_replaces_the_branch_rows_with_the_agent_list()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.AgentBridge.Respond("SyncToDefaultBranch", SuccessfulResponse());

        await using (var seedScope = ctx.CreateScope())
        {
            var seedGit = seedScope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
            await seedGit.PersistBranchesAsync(
                ctx.WorkspaceRepositoryId,
                localBranches: ["main", "feature/x"],
                remoteBranches: ["origin/main", "origin/feature/x"],
                defaultBranchName: "main");
        }

        await using var scope = ctx.CreateScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        await git.SyncToDefaultDirectAsync(
            ctx.WorkspaceId, ctx.RepositoryId, "feature/x",
            deleteRemoteBranch: false, allowForceDeleteLocalBranch: true, CancellationToken.None);

        var branches = await ctx.ReadBranchesAsync();
        Assert.DoesNotContain(branches, b => b.BranchName == "feature/x");
        Assert.DoesNotContain(branches, b => b.BranchName == "origin/feature/x");
        Assert.Contains(branches, b => b is { BranchName: "main", IsRemote: false, IsDefault: true });
    }

    [Fact]
    public async Task Successful_sync_persists_the_projects_found_on_the_default_branch()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.AgentBridge.Respond("SyncToDefaultBranch", SuccessfulResponse());

        await using var scope = ctx.CreateScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        await git.SyncToDefaultDirectAsync(
            ctx.WorkspaceId, ctx.RepositoryId, "feature/x",
            deleteRemoteBranch: false, allowForceDeleteLocalBranch: true, CancellationToken.None);

        var projects = await ctx.ReadProjectsAsync();
        Assert.Single(projects);
        Assert.Equal("Acme.Api", projects[0].ProjectName);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Local_branch_force_delete_follows_the_users_confirmation_without_a_pull_request(
        bool allowForceDeleteLocalBranch,
        bool expectedForceDelete)
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.AgentBridge.Respond("SyncToDefaultBranch", SuccessfulResponse());

        await using var scope = ctx.CreateScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        await git.SyncToDefaultDirectAsync(
            ctx.WorkspaceId, ctx.RepositoryId, "feature/x",
            deleteRemoteBranch: false, allowForceDeleteLocalBranch, CancellationToken.None);

        var args = ctx.AgentBridge.Calls.Single(c => c.Command == "SyncToDefaultBranch").Args;
        var force = args.GetType().GetProperty("forceDeleteLocalBranch")!.GetValue(args);
        Assert.Equal(expectedForceDelete, force);
    }

    [Fact]
    public async Task Failed_agent_command_reports_the_inner_error_and_leaves_the_row_untouched()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.AgentBridge.Respond("SyncToDefaultBranch", new SyncToDefaultBranchResponse
        {
            Success = false,
            ErrorMessage = "Could not determine default branch",
        });

        await using var scope = ctx.CreateScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();

        var (success, error) = await git.SyncToDefaultDirectAsync(
            ctx.WorkspaceId, ctx.RepositoryId, "feature/x",
            deleteRemoteBranch: false, allowForceDeleteLocalBranch: true, CancellationToken.None);

        Assert.False(success);
        Assert.Equal("Could not determine default branch", error);

        var link = await ctx.ReadLinkAsync();
        Assert.Equal("feature/x", link.BranchName);
        Assert.Equal(3, link.OutgoingCommits);
    }

    [Fact]
    public async Task GetBranches_after_sync_in_another_scope_returns_main_not_stale_tracked_branch()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.AgentBridge.Respond("SyncToDefaultBranch", SuccessfulResponse());

        await using var circuitScope = ctx.CreateScope();
        var circuitDb = circuitScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tracked = await circuitDb.WorkspaceRepositories
            .FirstAsync(wr => wr.WorkspaceId == ctx.WorkspaceId && wr.RepositoryId == ctx.RepositoryId);
        Assert.Equal("feature/x", tracked.BranchName);

        await using (var jobScope = ctx.CreateScope())
        {
            var git = jobScope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
            var (success, error) = await git.SyncToDefaultDirectAsync(
                ctx.WorkspaceId, ctx.RepositoryId, "feature/x",
                deleteRemoteBranch: false, allowForceDeleteLocalBranch: true, CancellationToken.None);
            Assert.True(success);
            Assert.Null(error);
        }

        Assert.Equal("feature/x", tracked.BranchName);

        var ops = circuitScope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>();
        var outcome = await ops.GetBranchesAsync(ctx.WorkspaceId, ctx.RepositoryId);
        Assert.True(outcome.IsSuccessStatus);
        var snapshot = Assert.IsType<WorkspaceBranchesSnapshot>(outcome.Body);
        Assert.Equal("main", snapshot.CurrentBranch);
        Assert.Equal("main", snapshot.DefaultBranch);
        Assert.Contains("origin/main", snapshot.RemoteBranches);
        Assert.DoesNotContain(snapshot.LocalBranches, b => b == "feature/x");
    }
}
