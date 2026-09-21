using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Orchestration;
using GrayMoon.App.Services.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

/// <summary>
/// Shared Return-to-Default analyze/execute/unattended behaviour (MCP readiness pre-start supplement Â§8).
/// </summary>
public sealed class ReturnToDefaultAnalyzeTests
{
    private static BranchesResponse RefreshBranchesOk(bool hasUpstream = true) => new()
    {
        Success = true,
        LocalBranches = ["main", "feature/x"],
        RemoteBranches = ["origin/main", "origin/feature/x"],
        CurrentBranch = "feature/x",
        DefaultBranch = "main",
        Tags = [],
        HasUpstream = hasUpstream,
        UpstreamProbed = true,
    };

    private static WorkspaceRepositoryLinkListItemDto Dto(
        int repositoryId = 1,
        string name = "repo",
        string? branch = "feature/x",
        string? defaultBranch = "main",
        string? tag = null,
        int ahead = 0,
        bool? hasUpstream = true,
        string? prState = null,
        int? prNumber = null,
        DateTimeOffset? mergedAt = null) => new(
        WorkspaceRepositoryId: 10,
        WorkspaceId: 1,
        RepositoryId: repositoryId,
        RepositoryName: name,
        CloneUrl: "https://example/repo.git",
        GitVersion: "1.0.0",
        BranchName: branch,
        CheckedOutTag: tag,
        DefaultBranchName: defaultBranch,
        OutgoingCommits: 0,
        IncomingCommits: 0,
        DefaultBranchBehindCommits: 0,
        DefaultBranchAheadCommits: ahead,
        BranchHasUpstream: hasUpstream,
        SyncStatus: RepoSyncStatus.InSync,
        DependencyLevel: 0,
        Dependencies: 0,
        UnmatchedDeps: 0,
        OutOfDateFileRepos: 0,
        RepositoryType: ProjectType.Library,
        HasNewerTag: null,
        HasSelfFileVersionToken: null,
        PullRequestState: prState,
        PullRequestNumber: prNumber,
        PullRequestHtmlUrl: null,
        PullRequestMergedAt: mergedAt,
        PullRequestMergeable: null,
        PullRequestMergeableState: null,
        PullRequestChangedFiles: null,
        Archived: false,
        UncommittedChangedFileCount: 0);

    [Fact]
    public void Classify_already_on_default_is_skipped()
    {
        var plan = WorkspaceSyncHandler.ClassifyRepository(Dto(branch: "main", ahead: 0));
        Assert.True(plan.IsAlreadyOnDefault);
        Assert.False(plan.RequiresExplicitDiscardConfirmation);
        Assert.Null(plan.BlockingReason);
    }

    [Fact]
    public void Classify_ahead_without_merged_or_closed_pr_requires_explicit_discard()
    {
        var plan = WorkspaceSyncHandler.ClassifyRepository(Dto(ahead: 3, prState: "open", prNumber: 7));
        Assert.True(plan.RequiresExplicitDiscardConfirmation);
        Assert.False(plan.CanDiscardLocalBranchSafely);
        Assert.False(plan.IsAlreadyOnDefault);
        Assert.Contains("not safe", plan.BlockingReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classify_ahead_with_merged_pr_is_eligible()
    {
        var plan = WorkspaceSyncHandler.ClassifyRepository(Dto(
            ahead: 3,
            prState: "closed",
            prNumber: 7,
            mergedAt: DateTimeOffset.UtcNow));
        Assert.False(plan.RequiresExplicitDiscardConfirmation);
        Assert.True(plan.CanDiscardLocalBranchSafely);
        Assert.Equal("merged", plan.PullRequestState);
    }

    [Fact]
    public void Classify_ahead_with_closed_pr_is_eligible()
    {
        var plan = WorkspaceSyncHandler.ClassifyRepository(Dto(ahead: 2, prState: "closed", prNumber: 9));
        Assert.False(plan.RequiresExplicitDiscardConfirmation);
        Assert.True(plan.CanDiscardLocalBranchSafely);
        Assert.Equal("closed", plan.PullRequestState);
    }

    [Fact]
    public void Classify_upstream_reports_remote_branch_can_be_deleted()
    {
        var withUpstream = WorkspaceSyncHandler.ClassifyRepository(Dto(hasUpstream: true, ahead: 0));
        Assert.True(withUpstream.RemoteBranchCanBeDeleted);
        Assert.True(withUpstream.HasUpstream);

        var without = WorkspaceSyncHandler.ClassifyRepository(Dto(hasUpstream: false, ahead: 0));
        Assert.False(without.RemoteBranchCanBeDeleted);
        Assert.False(without.HasUpstream);
    }

    [Fact]
    public void Classify_tag_is_blocking()
    {
        var plan = WorkspaceSyncHandler.ClassifyRepository(Dto(tag: "v1.0.0", branch: null));
        Assert.True(plan.IsOnTag);
        Assert.False(plan.CanDiscardLocalBranchSafely);
        Assert.Contains("tag", plan.BlockingReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classify_ahead_without_pr_requires_explicit_force_authorization()
    {
        var plan = WorkspaceSyncHandler.ClassifyRepository(Dto(ahead: 5, prState: null, prNumber: null));
        Assert.True(plan.RequiresExplicitDiscardConfirmation);
        Assert.False(plan.CanDiscardLocalBranchSafely);
    }

    [Fact]
    public async Task Analyze_already_on_default_reports_skipped()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.AgentBridge.Respond("RefreshBranches", RefreshBranchesOk());
        await ctx.MutateLinkAsync(link =>
        {
            link.BranchName = "main";
            link.DefaultBranchName = "main";
            link.DefaultBranchAheadCommits = 0;
            link.CheckedOutTag = null;
        });

        var handler = CreateHandler(ctx);
        var plan = await handler.AnalyzeReturnToDefaultAsync(
            ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync(), [ctx.RepositoryId], null, CancellationToken.None);

        Assert.False(plan.AnalysisFailed);
        var repo = Assert.Single(plan.Repositories);
        Assert.True(repo.IsAlreadyOnDefault);
        Assert.True(plan.CanProceedAutomatically);
        Assert.False(plan.HasBlockingRepositories);
    }

    [Fact]
    public async Task Analyze_ahead_without_safe_pr_is_not_automatic()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.AgentBridge.Respond("RefreshBranches", RefreshBranchesOk());
        // Seed leaves ahead=4; PR refresh without a token clears any PR row, so analysis must not treat cleanup as automatic.
        var handler = CreateHandler(ctx);
        var plan = await handler.AnalyzeReturnToDefaultAsync(
            ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync(), [ctx.RepositoryId], null, CancellationToken.None);

        Assert.False(plan.AnalysisFailed);
        Assert.False(plan.CanProceedAutomatically);
        Assert.True(plan.HasBlockingRepositories);
        var repo = Assert.Single(plan.Repositories);
        Assert.True(repo.RequiresExplicitDiscardConfirmation);
    }

    [Fact]
    public async Task Analyze_tag_blocks_automatic_cleanup()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.AgentBridge.Respond("RefreshBranches", new BranchesResponse
        {
            Success = true,
            LocalBranches = ["main"],
            RemoteBranches = ["origin/main"],
            CurrentBranch = null,
            DefaultBranch = "main",
            Tags = ["v1.0.0"],
            CurrentTag = "v1.0.0",
            UpstreamProbed = false,
        });
        await ctx.MutateLinkAsync(link =>
        {
            link.CheckedOutTag = "v1.0.0";
            link.BranchName = null;
            link.DefaultBranchAheadCommits = 0;
        });

        var handler = CreateHandler(ctx);
        var plan = await handler.AnalyzeReturnToDefaultAsync(
            ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync(), [ctx.RepositoryId], null, CancellationToken.None);

        Assert.False(plan.CanProceedAutomatically);
        var repo = Assert.Single(plan.Repositories);
        Assert.True(repo.IsOnTag);
    }

    [Fact]
    public async Task Analyze_fetch_failure_fails_safely()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        // No RefreshBranches canned response â†’ agent reports failure â†’ analysis fails.
        var handler = CreateHandler(ctx);
        var plan = await handler.AnalyzeReturnToDefaultAsync(
            ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync(), [ctx.RepositoryId], null, CancellationToken.None);

        Assert.True(plan.AnalysisFailed);
        Assert.False(plan.CanProceedAutomatically);
        Assert.Contains("Fetch failed", plan.AnalysisError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unattended_aborts_when_ahead_without_safe_pr()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.AgentBridge.Respond("RefreshBranches", RefreshBranchesOk());

        var handler = CreateHandler(ctx);
        var result = await handler.ReturnToDefaultUnattendedAsync(
            ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync(), [ctx.RepositoryId], null, CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Contains("not safe", result.AbortReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ctx.AgentBridge.Calls, c => c.Command == "ReturnToDefaultBranch");
    }

    [Fact]
    public async Task Unattended_aborts_on_tag()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.AgentBridge.Respond("RefreshBranches", new BranchesResponse
        {
            Success = true,
            LocalBranches = ["main"],
            RemoteBranches = ["origin/main"],
            DefaultBranch = "main",
            Tags = ["v1"],
            CurrentTag = "v1",
            UpstreamProbed = false,
        });
        await ctx.MutateLinkAsync(link =>
        {
            link.CheckedOutTag = "v1";
            link.BranchName = null;
            link.DefaultBranchAheadCommits = 0;
        });

        var handler = CreateHandler(ctx);
        var result = await handler.ReturnToDefaultUnattendedAsync(
            ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync(), [ctx.RepositoryId], null, CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Contains("tag", result.AbortReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unattended_succeeds_when_already_on_default()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.AgentBridge.Respond("RefreshBranches", new BranchesResponse
        {
            Success = true,
            LocalBranches = ["main"],
            RemoteBranches = ["origin/main"],
            CurrentBranch = "main",
            DefaultBranch = "main",
            Tags = [],
            HasUpstream = true,
            UpstreamProbed = true,
        });
        await ctx.MutateLinkAsync(link =>
        {
            link.BranchName = "main";
            link.DefaultBranchName = "main";
            link.DefaultBranchAheadCommits = 0;
            link.CheckedOutTag = null;
        });

        var handler = CreateHandler(ctx);
        var result = await handler.ReturnToDefaultUnattendedAsync(
            ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync(), [ctx.RepositoryId], null, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Null(result.AbortReason);
        Assert.DoesNotContain(ctx.AgentBridge.Calls, c => c.Command == "ReturnToDefaultBranch");
    }

    [Fact]
    public async Task Execute_deletes_remote_only_when_option_requests_it()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.AgentBridge.Respond("ReturnToDefaultBranch", new ReturnToDefaultBranchResponse
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
        });
        await ctx.MutateLinkAsync(link =>
        {
            link.BranchName = "feature/x";
            link.DefaultBranchName = "main";
            link.DefaultBranchAheadCommits = 0;
            link.BranchHasUpstream = true;
            link.CheckedOutTag = null;
        });

        var handler = CreateHandler(ctx);

        await handler.ExecuteReturnToDefaultAsync(
            ctx.WorkspaceId,
            await ctx.GetSpecialContextIdAsync(),
            [ctx.RepositoryId],
            new ReturnToDefaultOptions(DeleteRemoteBranch: false, AllowForceDeleteLocalBranch: true, CloseOpenPullRequest: false),
            null,
            CancellationToken.None);

        var args = ctx.AgentBridge.Calls.Single(c => c.Command == "ReturnToDefaultBranch").Args;
        var deleteRemote = args.GetType().GetProperty("deleteRemoteBranch")!.GetValue(args);
        Assert.Equal(false, deleteRemote);

        ctx.AgentBridge.Calls.Clear();
        await ctx.MutateLinkAsync(link =>
        {
            link.BranchName = "feature/x";
            link.DefaultBranchName = "main";
            link.DefaultBranchAheadCommits = 0;
            link.BranchHasUpstream = true;
        });

        await handler.ExecuteReturnToDefaultAsync(
            ctx.WorkspaceId,
            await ctx.GetSpecialContextIdAsync(),
            [ctx.RepositoryId],
            new ReturnToDefaultOptions(DeleteRemoteBranch: true, AllowForceDeleteLocalBranch: true, CloseOpenPullRequest: false),
            null,
            CancellationToken.None);

        var args2 = ctx.AgentBridge.Calls.Single(c => c.Command == "ReturnToDefaultBranch").Args;
        var deleteRemote2 = args2.GetType().GetProperty("deleteRemoteBranch")!.GetValue(args2);
        Assert.Equal(true, deleteRemote2);
    }

    [Fact]
    public async Task Execute_does_not_request_remote_delete_when_no_upstream()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.AgentBridge.Respond("ReturnToDefaultBranch", new ReturnToDefaultBranchResponse
        {
            Success = true,
            CurrentBranch = "main",
            DefaultBranch = "main",
            LocalBranches = ["main"],
            RemoteBranches = ["origin/main"],
            Tags = [],
            OutgoingCommits = 0,
            IncomingCommits = 0,
            HasUpstream = false,
            DefaultBranchBehind = 0,
            DefaultBranchAhead = 0,
            GitVersion = "2.0.0",
        });
        await ctx.MutateLinkAsync(link =>
        {
            link.BranchName = "feature/x";
            link.DefaultBranchName = "main";
            link.DefaultBranchAheadCommits = 0;
            link.BranchHasUpstream = false;
            link.CheckedOutTag = null;
        });

        var handler = CreateHandler(ctx);
        await handler.ExecuteReturnToDefaultAsync(
            ctx.WorkspaceId,
            await ctx.GetSpecialContextIdAsync(),
            [ctx.RepositoryId],
            new ReturnToDefaultOptions(DeleteRemoteBranch: true, AllowForceDeleteLocalBranch: true, CloseOpenPullRequest: false),
            null,
            CancellationToken.None);

        var args = ctx.AgentBridge.Calls.Single(c => c.Command == "ReturnToDefaultBranch").Args;
        var deleteRemote = args.GetType().GetProperty("deleteRemoteBranch")!.GetValue(args);
        Assert.Equal(false, deleteRemote);
    }

    [Fact]
    public async Task Unattended_eligible_repo_executes_with_force_local_and_remote_when_upstream()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.AgentBridge.Respond("RefreshBranches", RefreshBranchesOk(hasUpstream: true));
        ctx.AgentBridge.Respond("ReturnToDefaultBranch", new ReturnToDefaultBranchResponse
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
        });
        await ctx.MutateLinkAsync(link =>
        {
            link.BranchName = "feature/x";
            link.DefaultBranchName = "main";
            link.DefaultBranchAheadCommits = 0;
            link.BranchHasUpstream = true;
            link.CheckedOutTag = null;
        });

        var handler = CreateHandler(ctx);
        var result = await handler.ReturnToDefaultUnattendedAsync(
            ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync(), [ctx.RepositoryId], null, CancellationToken.None);

        Assert.True(result.Completed);
        var args = ctx.AgentBridge.Calls.Single(c => c.Command == "ReturnToDefaultBranch").Args;
        Assert.Equal(true, args.GetType().GetProperty("deleteRemoteBranch")!.GetValue(args));
        Assert.Equal(true, args.GetType().GetProperty("forceDeleteLocalBranch")!.GetValue(args));
    }

    [Fact]
    public async Task Multi_repo_plan_represents_per_repository_safety()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        int secondRepoId;
        await using (var scope = ctx.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GrayMoon.App.Data.AppDbContext>();
            var connectorId = await db.Connectors.Select(c => c.ConnectorId).FirstAsync();
            var repo = new Repository
            {
                ConnectorId = connectorId,
                RepositoryName = "second-repo",
                OrgName = "acme",
                Visibility = "Public",
                CloneUrl = "https://github.com/acme/second-repo.git",
            };
            db.Repositories.Add(repo);
            await db.SaveChangesAsync();
            secondRepoId = repo.RepositoryId;
            db.WorkspaceRepositories.Add(new WorkspaceRepositoryLink
            {
                WorkspaceId = ctx.WorkspaceId,
                RepositoryId = secondRepoId,
                BranchName = "main",
                DefaultBranchName = "main",
                DefaultBranchAheadCommits = 0,
                BranchHasUpstream = true,
                SyncStatus = RepoSyncStatus.InSync,
            });
            await db.SaveChangesAsync();
        }

        ctx.AgentBridge.Respond("RefreshBranches", RefreshBranchesOk());
        // First repo stays ahead of default (unsafe); second is on default.
        await ctx.MutateLinkAsync(link =>
        {
            link.BranchName = "feature/x";
            link.DefaultBranchAheadCommits = 4;
        });

        var handler = CreateHandler(ctx);
        var plan = await handler.AnalyzeReturnToDefaultAsync(
            ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync(), [ctx.RepositoryId, secondRepoId], null, CancellationToken.None);

        Assert.False(plan.CanProceedAutomatically);
        Assert.Equal(2, plan.Repositories.Count);
        var first = plan.Repositories.Single(r => r.RepositoryId == ctx.RepositoryId);
        var second = plan.Repositories.Single(r => r.RepositoryId == secondRepoId);
        Assert.True(first.RequiresExplicitDiscardConfirmation);
        Assert.True(second.IsAlreadyOnDefault);
    }

    private static WorkspaceSyncHandler CreateHandler(SyncStateTestContext ctx)
        => new(NullLogger<WorkspaceSyncHandler>.Instance, ctx.Resolve<IServiceScopeFactory>(), ctx.Resolve<IOptions<WorkspaceOptions>>());
}
