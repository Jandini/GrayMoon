using System.Linq.Expressions;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Policy;
using GrayMoon.App.Services.Queries;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using GrayMoon.Application.Repositories.Policy;
using GrayMoon.Application.Workspaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static GrayMoon.App.Tests.Policy.Characterization.PolicyHarness;

namespace GrayMoon.App.Tests.Policy;

/// <summary>
/// A repository that is archived on the provider: GrayMoon does not push it, and it asks the provider again before it
/// keeps treating a repository as archived.
/// </summary>
public sealed class ArchivedRepositoryPolicyTests
{
    private static RepositoryPolicySubject Archived(
        string? tag = null, bool isWorkspaceRepository = false, bool isReference = false) =>
        RepositoryPolicySubject.ForCheckout("main", tag, isWorkspaceRepository, isReference: isReference, isArchived: true);

    [Fact]
    public void A_push_of_an_archived_repository_is_skipped_in_a_bulk_action_and_refused_for_one_repository()
    {
        var bulk = RepositoryOperationPolicy.Evaluate(RepositoryOperation.Push, RepositoryOperationScope.Bulk, Archived());
        var single = RepositoryOperationPolicy.Evaluate(RepositoryOperation.Push, RepositoryOperationScope.Individual, Archived());

        Assert.Equal(RepositoryOperationOutcome.Skipped, bulk.Outcome);
        Assert.Equal(RepositoryOperationReason.Archived, bulk.Reason);
        Assert.Equal(RepositoryOperationOutcome.Refused, single.Outcome);
        Assert.Equal(RepositoryOperationReason.Archived, single.Reason);
        Assert.Equal(RepositoryOperationPolicy.ArchivedMessage, single.Message);
    }

    [Fact]
    public void Only_push_reads_the_archived_state()
    {
        var withRule = RepositoryOperationPolicy.Rules.Where(rule => !rule.Value.AllowedWhenArchived).Select(rule => rule.Key).ToArray();

        Assert.Equal([RepositoryOperation.Push], withRule);
        foreach (var operation in Enum.GetValues<RepositoryOperation>().Where(operation => operation != RepositoryOperation.Push))
        {
            var notArchived = RepositoryPolicySubject.ForCheckout("main", null);
            Assert.Equal(
                RepositoryOperationPolicy.Evaluate(operation, RepositoryOperationScope.Bulk, notArchived),
                RepositoryOperationPolicy.Evaluate(operation, RepositoryOperationScope.Bulk, Archived()));
        }
    }

    [Fact]
    public void The_archived_rule_also_holds_for_the_Workspace_repository_and_a_tag_is_reported_first()
    {
        var workspaceRepository = RepositoryOperationPolicy.Evaluate(
            RepositoryOperation.Push, RepositoryOperationScope.Bulk, Archived(isWorkspaceRepository: true));
        var onTag = RepositoryOperationPolicy.Evaluate(RepositoryOperation.Push, RepositoryOperationScope.Bulk, Archived(tag: "v1.0.0"));
        var readOnly = RepositoryOperationPolicy.Evaluate(RepositoryOperation.Push, RepositoryOperationScope.Bulk, Archived(isReference: true));

        Assert.Equal(RepositoryOperationReason.Archived, workspaceRepository.Reason);
        Assert.Equal(RepositoryOperationReason.OnTag, onTag.Reason);
        Assert.Equal(RepositoryOperationReason.Archived, readOnly.Reason);
    }

    public static TheoryData<RepositoryOperation, string?, bool, bool> ArchivedSubjects()
    {
        var data = new TheoryData<RepositoryOperation, string?, bool, bool>();
        foreach (var operation in Enum.GetValues<RepositoryOperation>())
        foreach (var tag in new[] { null, "v1.0.0" })
        foreach (var isWorkspaceRepository in new[] { false, true })
        foreach (var isReference in new[] { false, true })
            data.Add(operation, tag, isWorkspaceRepository, isReference);
        return data;
    }

    [Theory]
    [MemberData(nameof(ArchivedSubjects))]
    public void The_query_form_agrees_with_the_policy_for_an_archived_repository(
        RepositoryOperation operation, string? storedTag, bool isWorkspaceRepository, bool isReference)
    {
        var role = isWorkspaceRepository ? WorkspaceRepositoryRole.Workspace : WorkspaceRepositoryRole.Source;
        Expression<Func<string?, WorkspaceRepositoryRole, bool, bool, bool>> query = (tag, r, reference, archived) =>
            RepositoryOperationPolicyExpressions.Allows(operation, tag, r, reference, false, archived);
        var expanded = (Expression<Func<string?, WorkspaceRepositoryRole, bool, bool, bool>>)RepositoryOperationPolicyExpressions.Expand(query);

        var expected = RepositoryOperationPolicy.IsAllowed(operation, Archived(storedTag, isWorkspaceRepository, isReference));

        Assert.Equal(expected, expanded.Compile()(storedTag, role, isReference, true));
        Assert.Equal(expected, RepositoryOperationPolicyExpressions.Allows(operation, storedTag, role, isReference, false, true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_header_does_not_offer_a_push_for_an_archived_repository_and_keeps_the_other_counters(bool dependencyGraph)
    {
        await using var ctx = await CreateAsync();
        await ctx.MutateLinkAsync(link =>
        {
            link.SyncStatus = RepoSyncStatus.InSync;
            link.BranchName = "feature/x";
            link.OutgoingCommits = 3;
            link.IncomingCommits = 2;
            link.BranchHasUpstream = true;
        });
        var capabilities = dependencyGraph
            ? WorkspaceCapabilities.Legacy
            : new WorkspaceCapabilities(WorkspaceType.Basic, WorkspaceVersioningMode.None, WorkspaceCiProvider.None);
        var special = await ctx.GetSpecialContextIdAsync();

        var before = await HeaderAsync(ctx, special, capabilities);
        await SetArchivedAsync(ctx, ctx.RepositoryId, true);
        var after = await HeaderAsync(ctx, special, capabilities);

        Assert.True(before.IsPushRecommended);
        Assert.False(after.IsPushRecommended);
        Assert.True(after.HasIncomingCommits);
        Assert.Equal(1, after.TotalCount);
    }

    [Fact]
    public async Task A_bulk_push_leaves_the_archived_repository_out_and_names_it()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await SetArchivedAsync(ctx, graph.ProducerId, true);
        var special = await ctx.GetSpecialContextIdAsync();
        await using var scope = ctx.CreateScope();
        var policy = scope.ServiceProvider.GetRequiredService<IWorkspaceRepositoryOperationPolicy>();

        var push = await policy.PartitionAsync(ctx.WorkspaceId, special, RepositoryOperation.Push);
        var pull = await policy.PartitionAsync(ctx.WorkspaceId, special, RepositoryOperation.Pull);
        var single = await policy.EvaluateAsync(ctx.WorkspaceId, special, RepositoryOperation.Push, graph.ProducerId);

        Assert.Equal([graph.ConsumerId], push.Eligible.ToArray());
        var skip = Assert.Single(push.Skipped);
        Assert.Equal(graph.ProducerId, skip.RepositoryId);
        Assert.Equal(RepositoryOperationReason.Archived, skip.Reason);
        Assert.Equal($"1 archived repository skipped: {ProducerName}.", RepositorySkipSummary.Describe(push.Skipped));
        Assert.Empty(pull.Skipped);
        Assert.Equal(RepositoryOperationOutcome.Refused, single.Outcome);
        Assert.Equal(RepositoryOperationPolicy.ArchivedMessage, single.Message);
    }

    [Fact]
    public async Task The_row_and_the_list_item_carry_the_archived_state_into_the_policy()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await SetArchivedAsync(ctx, graph.ProducerId, true);
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();

        var withRepository = await db.WorkspaceRepositories.AsNoTracking().Include(link => link.Repository).SingleAsync(link => link.RepositoryId == graph.ProducerId);
        var item = Dto(1, "a", branch: "main", outgoing: 1) with { Archived = true };

        Assert.False(withRepository.Allows(RepositoryOperation.Push));
        Assert.False(withRepository.Allows(RepositoryOperation.Push, RepositoryOperationScope.Individual));
        Assert.True(withRepository.Allows(RepositoryOperation.Pull));
        Assert.True(withRepository.Allows(RepositoryOperation.Commit));
        Assert.False(item.Allows(RepositoryOperation.Push));
        Assert.True(item.Allows(RepositoryOperation.Pull));
    }

    [Fact]
    public async Task A_repository_the_provider_reports_as_not_archived_is_unmarked_and_can_be_pushed_again()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await SetArchivedAsync(ctx, graph.ProducerId, true);
        await SetArchivedAsync(ctx, graph.ConsumerId, true);
        var probe = new FakeProbe { Answers = { [ProducerName] = false, [ConsumerName] = true } };
        var special = await ctx.GetSpecialContextIdAsync();

        var cleared = await VerifierFor(ctx, probe).VerifyAsync(ctx.WorkspaceId);

        Assert.Equal([ProducerName], cleared.ToArray());
        Assert.False(await IsArchivedAsync(ctx, graph.ProducerId));
        Assert.True(await IsArchivedAsync(ctx, graph.ConsumerId));
        Assert.Equal(2, probe.Asked.Count);
        await using var scope = ctx.CreateScope();
        var push = await scope.ServiceProvider.GetRequiredService<IWorkspaceRepositoryOperationPolicy>()
            .PartitionAsync(ctx.WorkspaceId, special, RepositoryOperation.Push);
        Assert.Equal([graph.ProducerId], push.Eligible.ToArray());
    }

    [Fact]
    public async Task No_answer_or_a_failed_request_keeps_the_archived_mark()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await SetArchivedAsync(ctx, graph.ProducerId, true);
        await SetArchivedAsync(ctx, graph.ConsumerId, true);
        var probe = new FakeProbe { Answers = { [ProducerName] = null }, Throws = { ConsumerName } };

        var cleared = await VerifierFor(ctx, probe).VerifyAsync(ctx.WorkspaceId);

        Assert.Empty(cleared);
        Assert.True(await IsArchivedAsync(ctx, graph.ProducerId));
        Assert.True(await IsArchivedAsync(ctx, graph.ConsumerId));
    }

    [Fact]
    public async Task The_provider_is_not_asked_when_no_repository_is_marked_and_a_repository_is_never_marked_here()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        var probe = new FakeProbe { Answers = { [ProducerName] = true, [ConsumerName] = true } };

        var cleared = await VerifierFor(ctx, probe).VerifyAsync(ctx.WorkspaceId);

        Assert.Empty(cleared);
        Assert.Empty(probe.Asked);
        Assert.False(await IsArchivedAsync(ctx, graph.ProducerId));
    }

    private static async Task<WorkspaceRepositoryHeaderStateDto> HeaderAsync(
        SyncStateTestContext ctx, WorkspaceFeatureContextId contextId, WorkspaceCapabilities capabilities)
    {
        await using var scope = ctx.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkspaceRepositoryLinkListQueryService>()
            .GetHeaderStateAsync(ctx.WorkspaceId, contextId, true, capabilities);
    }

    private static ArchivedRepositoryVerifier VerifierFor(
        SyncStateTestContext ctx, IRepositoryArchiveProbe probe) =>
        new(
            ctx.Resolve<IDbContextFactory<AppDbContext>>(),
            probe,
            ctx.Resolve<IHubContext<WorkspaceSyncHub>>(),
            NullLogger<ArchivedRepositoryVerifier>.Instance);

    private static async Task SetArchivedAsync(SyncStateTestContext ctx, int repositoryId, bool archived)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        await db.Repositories.Where(r => r.RepositoryId == repositoryId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Archived, archived));
    }

    private static async Task<bool> IsArchivedAsync(SyncStateTestContext ctx, int repositoryId)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        return await db.Repositories.AsNoTracking().Where(r => r.RepositoryId == repositoryId).Select(r => r.Archived).SingleAsync();
    }

    private sealed class FakeProbe : IRepositoryArchiveProbe
    {
        public Dictionary<string, bool?> Answers { get; } = [];
        public HashSet<string> Throws { get; } = [];
        public List<string> Asked { get; } = [];

        public Task<bool?> IsArchivedAsync(Repository repository, CancellationToken cancellationToken = default)
        {
            Asked.Add(repository.RepositoryName);
            if (Throws.Contains(repository.RepositoryName))
                throw new HttpRequestException("provider not reachable");
            return Task.FromResult(Answers.GetValueOrDefault(repository.RepositoryName));
        }
    }
}
