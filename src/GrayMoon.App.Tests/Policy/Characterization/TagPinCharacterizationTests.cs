using GrayMoon.App.Api.Endpoints;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using GrayMoon.Application.WorkspaceManifest;
using GrayMoon.Common.Manifest;
using Microsoft.Extensions.DependencyInjection;
using static GrayMoon.App.Tests.Policy.Characterization.PolicyHarness;

namespace GrayMoon.App.Tests.Policy.Characterization;

/// <summary>
/// What happens to the tag pin recorded in the Workspace definition when a Source repository
/// enters or leaves a tag through the app. The Workspace has a Workspace repository (the consumer of the seeded
/// graph is given that role), which is the condition for recording pins at all.
/// </summary>
public sealed class TagPinCharacterizationTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task Tag_checkout_in_the_special_Workspace_records_the_pin_in_the_Workspace_definition()
    {
        var pins = new PinRecorder();
        await using var ctx = await CreateWithWorkspaceRepositoryAsync(pins);
        ctx.WorkerBridge.Respond("CheckoutTag", new CheckoutTagResponse { Success = true, CurrentTag = Tag, Commit = Commit });

        await CheckoutAsync(ctx, Tag, isTag: true);

        var change = Assert.Single(pins.Changes);
        Assert.Equal(Tag, change.Tag);
        Assert.Equal(Commit, change.Commit);
    }

    [Fact]
    public async Task Branch_checkout_from_a_tag_in_the_special_Workspace_clears_the_pin()
    {
        var pins = new PinRecorder();
        await using var ctx = await CreateWithWorkspaceRepositoryAsync(pins);
        await PinWorkspaceAsync(ctx, ctx.RepositoryId);
        ctx.WorkerBridge.Respond("CheckoutBranch", new CheckoutBranchResponse { Success = true, CurrentBranch = "main" });

        await CheckoutAsync(ctx, "main", isTag: false);

        Assert.Null((await ctx.ReadLinkAsync()).CheckedOutTag);
        AssertPinCleared(pins);
    }

    [Fact]
    public async Task Branch_checkout_from_a_branch_does_not_touch_the_Workspace_definition()
    {
        var pins = new PinRecorder();
        await using var ctx = await CreateWithWorkspaceRepositoryAsync(pins);
        ctx.WorkerBridge.Respond("CheckoutBranch", new CheckoutBranchResponse { Success = true, CurrentBranch = "main" });

        await CheckoutAsync(ctx, "main", isTag: false);

        Assert.Empty(pins.Changes);
    }

    [Fact]
    public async Task Branch_checkout_that_the_Worker_refuses_keeps_the_pin()
    {
        var pins = new PinRecorder();
        await using var ctx = await CreateWithWorkspaceRepositoryAsync(pins);
        await PinWorkspaceAsync(ctx, ctx.RepositoryId);
        ctx.WorkerBridge.Respond("CheckoutBranch", new CheckoutBranchResponse { Success = false, ErrorMessage = "local changes" });

        var body = await CheckoutBodyAsync(ctx, "main", isTag: false);

        Assert.False(body.Success);
        Assert.Equal(Tag, (await ctx.ReadLinkAsync()).CheckedOutTag);
        Assert.Empty(pins.Changes);
    }

    [Fact]
    public async Task Branch_checkout_from_a_tag_reports_a_pin_that_could_not_be_cleared()
    {
        var pins = new PinRecorder { Failure = "The Workspace definition was not found." };
        await using var ctx = await CreateWithWorkspaceRepositoryAsync(pins);
        await PinWorkspaceAsync(ctx, ctx.RepositoryId);
        ctx.WorkerBridge.Respond("CheckoutBranch", new CheckoutBranchResponse { Success = true, CurrentBranch = "main" });

        var body = await CheckoutBodyAsync(ctx, "main", isTag: false);

        Assert.False(body.Success);
        Assert.Equal("The Workspace definition was not found.", body.ErrorMessage);
    }

    [Fact]
    public async Task Branch_checkout_from_a_tag_without_a_Workspace_repository_records_nothing()
    {
        var pins = new PinRecorder();
        await using var ctx = await CreateAsync(services => services.AddScoped<IWorkspaceManifestService>(_ => pins));
        await SeedGraphAsync(ctx);
        await PinWorkspaceAsync(ctx, ctx.RepositoryId);
        ctx.WorkerBridge.Respond("CheckoutBranch", new CheckoutBranchResponse { Success = true, CurrentBranch = "main" });

        await CheckoutAsync(ctx, "main", isTag: false);

        Assert.Empty(pins.Changes);
    }

    [Fact]
    public async Task Single_create_branch_from_a_tag_in_the_special_Workspace_clears_the_pin()
    {
        var pins = new PinRecorder();
        await using var ctx = await CreateWithWorkspaceRepositoryAsync(pins);
        await PinWorkspaceAsync(ctx, ctx.RepositoryId);
        ctx.WorkerBridge.Respond("CreateBranch", new CreateBranchResponse { Success = true, CurrentBranch = "topic", Branch = "topic" });
        var special = await ctx.GetSpecialContextIdAsync();

        await using (var scope = ctx.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>()
                .CreateBranchAsync(ctx.WorkspaceId, special, ctx.RepositoryId, "topic", "main");
        }

        // This path writes no repository state itself (the hook does), so the database still shows the tag.
        AssertPinCleared(pins);
        Assert.Equal(Tag, (await ctx.ReadLinkAsync()).CheckedOutTag);
    }

    [Fact]
    public async Task Bulk_create_branch_from_a_tag_in_the_special_Workspace_clears_the_pin()
    {
        var pins = new PinRecorder();
        await using var ctx = await CreateWithWorkspaceRepositoryAsync(pins);
        await PinWorkspaceAsync(ctx, ctx.RepositoryId);
        ctx.WorkerBridge.Respond("CreateBranch", new CreateBranchResponse { Success = true, CurrentBranch = "topic", Branch = "topic" });

        var outcome = await BulkCreateAsync(ctx, await ctx.GetSpecialContextIdAsync(), new HashSet<int> { ctx.RepositoryId });

        Assert.Empty(outcome.ErrorsByRepositoryId);
        Assert.Null((await ctx.ReadLinkAsync()).CheckedOutTag);
        AssertPinCleared(pins);
    }

    [Fact]
    public async Task Bulk_create_branch_for_the_whole_Workspace_clears_only_the_Source_that_left_a_tag()
    {
        var pins = new PinRecorder();
        await using var ctx = await CreateWithWorkspaceRepositoryAsync(pins);
        await PinWorkspaceAsync(ctx, ctx.RepositoryId);
        ctx.WorkerBridge.Respond("CreateBranch", new CreateBranchResponse { Success = true, CurrentBranch = "topic", Branch = "topic" });

        await BulkCreateAsync(ctx, await ctx.GetSpecialContextIdAsync(), repositoryIds: null);

        // The other repository is the Workspace repository and was on a branch: one change, for the Source.
        AssertPinCleared(pins);
    }

    [Fact]
    public async Task Bulk_create_branch_that_the_Worker_refuses_keeps_the_pin()
    {
        var pins = new PinRecorder();
        await using var ctx = await CreateWithWorkspaceRepositoryAsync(pins);
        await PinWorkspaceAsync(ctx, ctx.RepositoryId);
        ctx.WorkerBridge.Respond("CreateBranch", new CreateBranchResponse { Success = false, ErrorMessage = "exists" });

        var outcome = await BulkCreateAsync(ctx, await ctx.GetSpecialContextIdAsync(), new HashSet<int> { ctx.RepositoryId });

        Assert.Equal("exists", outcome.ErrorsByRepositoryId[ctx.RepositoryId]);
        Assert.Equal(Tag, (await ctx.ReadLinkAsync()).CheckedOutTag);
        Assert.Empty(pins.Changes);
    }

    [Fact]
    public async Task Bulk_create_branch_from_a_tag_reports_a_pin_that_could_not_be_cleared()
    {
        var pins = new PinRecorder { Failure = "The Workspace definition was not found." };
        await using var ctx = await CreateWithWorkspaceRepositoryAsync(pins);
        await PinWorkspaceAsync(ctx, ctx.RepositoryId);
        ctx.WorkerBridge.Respond("CreateBranch", new CreateBranchResponse { Success = true, CurrentBranch = "topic", Branch = "topic" });

        var outcome = await BulkCreateAsync(ctx, await ctx.GetSpecialContextIdAsync(), new HashSet<int> { ctx.RepositoryId });

        Assert.Equal("The Workspace definition was not found.", outcome.ErrorsByRepositoryId[ctx.RepositoryId]);
    }

    [Fact]
    public async Task Bulk_create_branch_in_a_Feature_leaves_the_Workspace_definition_alone()
    {
        var pins = new PinRecorder();
        await using var ctx = await CreateWithWorkspaceRepositoryAsync(pins);
        await PinWorkspaceAsync(ctx, ctx.RepositoryId);
        var feature = await SeedFeatureAsync(ctx, [new FeatureRepo(ctx.RepositoryId)]);
        ctx.WorkerBridge.Respond("CreateBranch", new CreateBranchResponse { Success = true, CurrentBranch = "topic", Branch = "topic" });

        await BulkCreateAsync(ctx, feature, new HashSet<int> { ctx.RepositoryId });

        // The pin describes the Workspace checkout, which a Feature does not move.
        Assert.Equal(Tag, (await ctx.ReadLinkAsync()).CheckedOutTag);
        Assert.Empty(pins.Changes);
    }

    [Fact]
    public async Task Workspace_wide_checkout_from_a_tag_in_the_special_Workspace_clears_the_pin()
    {
        var pins = new PinRecorder();
        await using var ctx = await CreateWithWorkspaceRepositoryAsync(pins);
        await PinWorkspaceAsync(ctx, ctx.RepositoryId);
        ctx.WorkerBridge.Respond("CheckoutBranch", new CheckoutBranchResponse { Success = true, CurrentBranch = "main" });
        var special = await ctx.GetSpecialContextIdAsync();

        await using (var scope = ctx.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WorkspaceBranchHandler>().CheckoutBranchForWorkspaceAsync(
                ctx.WorkspaceId,
                special,
                new Dictionary<int, int> { [ctx.RepositoryId] = ctx.WorkspaceRepositoryId },
                "main",
                reportProgress: null,
                CancellationToken.None);
        }

        Assert.Null((await ctx.ReadLinkAsync()).CheckedOutTag);
        AssertPinCleared(pins);
    }

    private static void AssertPinCleared(PinRecorder pins)
    {
        var change = Assert.Single(pins.Changes);
        Assert.Contains(ProducerName, change.RepositoryUrl);
        Assert.Null(change.Tag);
        Assert.Null(change.Commit);
    }

    private static async Task<BranchCreationOutcome> BulkCreateAsync(
        SyncStateTestContext ctx, WorkspaceFeatureContextId contextId, IReadOnlySet<int>? repositoryIds)
    {
        await using var scope = ctx.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkspaceGitService>()
            .CreateBranchesWithOutcomeAsync(ctx.WorkspaceId, contextId, "topic", "__default__", repositoryIds: repositoryIds);
    }

    private static async Task CheckoutAsync(SyncStateTestContext ctx, string name, bool isTag)
    {
        var body = await CheckoutBodyAsync(ctx, name, isTag);
        Assert.True(body.Success, body.ErrorMessage);
    }

    private static async Task<CheckoutBranchApiResult> CheckoutBodyAsync(SyncStateTestContext ctx, string name, bool isTag)
    {
        var special = await ctx.GetSpecialContextIdAsync();
        await using var scope = ctx.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>()
            .CheckoutAsync(ctx.WorkspaceId, special, ctx.RepositoryId, name, isTag);
        Assert.True(outcome.IsSuccessStatus);
        return Assert.IsType<CheckoutBranchApiResult>(outcome.Body);
    }

    private static async Task<SyncStateTestContext> CreateWithWorkspaceRepositoryAsync(PinRecorder pins)
    {
        var ctx = await CreateAsync(services => services.AddScoped<IWorkspaceManifestService>(_ => pins));
        var graph = await SeedGraphAsync(ctx);
        await MutateLinkAsync(ctx, graph.ConsumerId, link => link.Role = WorkspaceRepositoryRole.Workspace);
        return ctx;
    }

    /// <summary>Records tag pin changes; everything else the checkout paths may touch succeeds without doing anything.</summary>
    private sealed class PinRecorder : IWorkspaceManifestService
    {
        private readonly List<WorkspaceRepositoryTagPinChange> _changes = [];

        /// <summary>When set, every pin change fails with this message.</summary>
        public string? Failure { get; init; }

        public IReadOnlyList<WorkspaceRepositoryTagPinChange> Changes
        {
            get
            {
                lock (_changes)
                    return _changes.ToList();
            }
        }

        public Task<OperationResult> SetRepositoryTagPinsAsync(int workspaceId, IReadOnlyList<WorkspaceRepositoryTagPinChange> changes, CancellationToken cancellationToken = default)
        {
            lock (_changes)
                _changes.AddRange(changes);
            return Task.FromResult(Failure is null ? OperationResult.Ok() : OperationResult.Fail(Failure));
        }

        public Task<OperationResult> SetRepositoryReferenceRevisionsAsync(int workspaceId, IReadOnlyList<WorkspaceRepositoryReferenceRevisionChange> changes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkspaceManifestDrift> DetectDriftAsync(int workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkspaceManifestDrift(false, null, [], [], [], [], []));

        public Task<WorkspaceManifest> BuildFromDatabaseAsync(int workspaceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OperationResult> WriteAuthoritativeManifestAsync(int workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Ok());

        public Task<OperationResult> WriteManagedGitIgnoreAsync(int workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Ok());

        public Task<OperationResult> WriteVersionFilesAsync(int workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Ok());

        public Task<IReadOnlyList<string>> MergeVersionFilesIntoDatabaseAsync(int workspaceId, IReadOnlyList<WorkspaceManifestVersionFile>? versionFiles, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<OperationResult> MergeVersionFilesFromDefinitionAsync(int workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Ok());

        public Task<bool> IsCodeGraphEnabledAsync(int workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool?> GetCodeGraphSwitchAsync(int workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<bool?>(false);

        public Task<OperationResult> SetCodeGraphEnabledAsync(int workspaceId, bool enabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Ok());

        public Task<(WorkspaceManifestAi? Ai, string? Error)> ReadAiAsync(int workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<(WorkspaceManifestAi?, string?)>((null, null));

        public Task<OperationResult> WriteCodeGraphConfigAsync(int workspaceId, WorkspaceFeatureContextId? contextId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Ok());
    }
}
