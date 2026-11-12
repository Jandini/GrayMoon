using System.Text.Json;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Api.Endpoints;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Services.Application;
using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.Policy;
using GrayMoon.Application;
using GrayMoon.Application.Features;
using GrayMoon.Application.WorkspaceManifest;
using GrayMoon.Application.Workspaces;
using GrayMoon.Common.Manifest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GrayMoon.App.Tests.Policy.Characterization.PolicyHarness;

namespace GrayMoon.App.Tests.Policy;

/// <summary>
/// Changing which repositories are read-only (the facade), and recording the branch and commit of a read-only
/// repository in the Workspace definition. The Worker and the Workspace definition are fakes.
/// </summary>
public sealed class WorkspaceReferenceOperationsTests
{
    private const string Head = "1111111111111111111111111111111111111111";
    private const string Pulled = "2222222222222222222222222222222222222222";

    private static InspectWorktreeWorkerResponse Clean() => new()
    {
        Exists = true,
        Branch = "main",
        HeadSha = Head,
        IsDirty = false,
        HasUpstream = true,
        AheadOfUpstream = 0,
    };

    [Fact]
    public async Task State_reports_none_mixed_and_all_and_leaves_the_Workspace_repository_out()
    {
        var manifest = new FakeManifest();
        await using var ctx = await CreateAsync(manifest);
        var graph = await SeedGraphAsync(ctx);

        Assert.Equal(WorkspaceReferenceAggregate.None, (await StateAsync(ctx)).Aggregate);

        await MutateLinkAsync(ctx, graph.ProducerId, link => link.IsReference = true);
        var mixed = await StateAsync(ctx);
        Assert.Equal(WorkspaceReferenceAggregate.Mixed, mixed.Aggregate);
        Assert.Equal(2, mixed.Repositories.Count);
        Assert.False(mixed.HasWorkspaceRepository);

        await MutateLinkAsync(ctx, graph.ConsumerId, link => link.Role = WorkspaceRepositoryRole.Workspace);
        var all = await StateAsync(ctx);
        Assert.Equal(WorkspaceReferenceAggregate.All, all.Aggregate);
        Assert.Equal(graph.ProducerId, Assert.Single(all.Repositories).RepositoryId);
        Assert.True(all.HasWorkspaceRepository);

        await using var scope = ctx.CreateScope();
        var empty = await scope.ServiceProvider.GetRequiredService<IWorkspaceReferenceOperations>().GetStateAsync(ctx.WorkspaceId + 1000);
        Assert.Equal(WorkspaceReferenceAggregate.NoSources, empty.Aggregate);
    }

    [Fact]
    public async Task One_blocked_repository_refuses_the_whole_change_and_nothing_is_written()
    {
        var manifest = new FakeManifest();
        await using var ctx = await CreateAsync(manifest);
        var graph = await SeedGraphAsync(ctx);
        RespondInspect(ctx, path =>
        {
            var response = Clean();
            if (path.Contains(ConsumerName, StringComparison.Ordinal))
                response.AheadOfUpstream = 2;
            else
                response.IsDirty = true;
            return response;
        });

        var result = await RunAsync(ctx, operations => operations.SetAllAsync(ctx.WorkspaceId, reference: true, alsoSetDefault: true));

        Assert.False(result.Success);
        Assert.Equal(WorkspaceReferenceOperations.BlockedMessage, result.Error);
        var block = Assert.Single(result.Blocks);
        Assert.Equal(graph.ConsumerId, block.RepositoryId);
        Assert.Equal($"Repository {ConsumerName}: it has 2 unpushed commits.", block.Message);
        // The warning of the repository that was fine is still reported.
        Assert.Contains(ProducerName, Assert.Single(result.Warnings));
        Assert.False((await ReadLinkAsync(ctx, graph.ProducerId)).IsReference);
        Assert.False((await ReadLinkAsync(ctx, graph.ConsumerId)).IsReference);
        Assert.False(await ReadDefaultAsync(ctx));
        Assert.Equal(0, manifest.DefinitionWrites);
        Assert.Empty(manifest.Revisions);
        Assert.DoesNotContain(ctx.Broadcasts, broadcast => broadcast.Method == "WorkspaceSynced");
    }

    [Fact]
    public async Task Making_all_read_only_sets_the_flags_writes_the_definition_then_records_the_checked_HEAD()
    {
        var manifest = new FakeManifest();
        await using var ctx = await CreateAsync(manifest);
        var graph = await SeedGraphAsync(ctx);
        await MutateLinkAsync(ctx, graph.ConsumerId, link => link.Role = WorkspaceRepositoryRole.Workspace);
        RespondInspect(ctx, _ => Clean());

        var result = await RunAsync(ctx, operations => operations.SetAllAsync(ctx.WorkspaceId, reference: true, alsoSetDefault: false));

        Assert.True(result.Success, result.Error);
        Assert.Empty(result.Warnings);
        Assert.Null(result.Notice);
        Assert.Equal(1, result.ChangedRepositoryCount);
        Assert.True((await ReadLinkAsync(ctx, graph.ProducerId)).IsReference);
        // The Workspace repository is never read-only and was not inspected.
        Assert.False((await ReadLinkAsync(ctx, graph.ConsumerId)).IsReference);
        Assert.Single(InspectedPaths(ctx));
        Assert.False(await ReadDefaultAsync(ctx));
        var revision = Assert.Single(manifest.Revisions);
        Assert.Contains(ProducerName, revision.RepositoryUrl);
        Assert.Equal("main", revision.Branch);
        Assert.Equal(Head, revision.Commit);
        Assert.Equal(["definition", "revisions"], manifest.Log);
        Assert.Contains(ctx.Broadcasts, broadcast => broadcast.Method == "WorkspaceSynced");
    }

    [Fact]
    public async Task Also_set_default_changes_the_default_in_the_same_call()
    {
        var manifest = new FakeManifest();
        await using var ctx = await CreateAsync(manifest);
        await SeedGraphAsync(ctx);
        RespondInspect(ctx, _ => Clean());

        var result = await RunAsync(ctx, operations => operations.SetAllAsync(ctx.WorkspaceId, reference: true, alsoSetDefault: true));

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.ChangedRepositoryCount);
        Assert.True(await ReadDefaultAsync(ctx));
        // No Workspace repository: the flags are saved, nothing is written and the limitation is stated.
        Assert.Equal(WorkspaceReferenceOperations.NoWorkspaceRepositoryNotice, result.Notice);
        Assert.Equal(0, manifest.DefinitionWrites);
        Assert.Empty(manifest.Revisions);
    }

    [Fact]
    public async Task Making_all_writable_runs_no_check_and_records_nothing()
    {
        var manifest = new FakeManifest();
        await using var ctx = await CreateAsync(manifest);
        var graph = await SeedGraphAsync(ctx);
        await MutateLinkAsync(ctx, graph.ConsumerId, link => link.Role = WorkspaceRepositoryRole.Workspace);
        await MutateLinkAsync(ctx, graph.ProducerId, link => link.IsReference = true);

        var result = await RunAsync(ctx, operations => operations.SetAllAsync(ctx.WorkspaceId, reference: false, alsoSetDefault: false));

        Assert.True(result.Success, result.Error);
        Assert.False((await ReadLinkAsync(ctx, graph.ProducerId)).IsReference);
        Assert.Empty(InspectedPaths(ctx));
        // The definition is rewritten from the database, which drops the recorded branch and commit.
        Assert.Equal(1, manifest.DefinitionWrites);
        Assert.Empty(manifest.Revisions);
    }

    [Fact]
    public async Task A_default_only_change_touches_no_repository_and_asks_the_Worker_nothing()
    {
        var manifest = new FakeManifest();
        await using var ctx = await CreateAsync(manifest);
        var graph = await SeedGraphAsync(ctx);
        await MutateLinkAsync(ctx, graph.ConsumerId, link => link.Role = WorkspaceRepositoryRole.Workspace);

        var result = await RunAsync(ctx, operations => operations.SetDefaultAsync(ctx.WorkspaceId, true));

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, result.ChangedRepositoryCount);
        Assert.True(await ReadDefaultAsync(ctx));
        Assert.False((await ReadLinkAsync(ctx, graph.ProducerId)).IsReference);
        Assert.Empty(InspectedPaths(ctx));
        Assert.Equal(1, manifest.DefinitionWrites);
        Assert.Empty(manifest.Revisions);
    }

    [Fact]
    public async Task One_repository_can_be_changed_alone_and_the_Workspace_repository_is_refused()
    {
        var manifest = new FakeManifest();
        await using var ctx = await CreateAsync(manifest);
        var graph = await SeedGraphAsync(ctx);
        RespondInspect(ctx, _ => Clean());

        var one = await RunAsync(ctx, operations => operations.SetRepositoryAsync(ctx.WorkspaceId, graph.ProducerId, true));
        await MutateLinkAsync(ctx, graph.ConsumerId, link => link.Role = WorkspaceRepositoryRole.Workspace);
        var workspaceRepository = await RunAsync(ctx, operations => operations.SetRepositoryAsync(ctx.WorkspaceId, graph.ConsumerId, true));

        Assert.True(one.Success, one.Error);
        Assert.True((await ReadLinkAsync(ctx, graph.ProducerId)).IsReference);
        Assert.False(workspaceRepository.Success);
        Assert.False((await ReadLinkAsync(ctx, graph.ConsumerId)).IsReference);
        Assert.False(await ReadDefaultAsync(ctx));
    }

    [Fact]
    public async Task Turning_read_only_on_without_a_Worker_is_refused_and_nothing_changes()
    {
        var manifest = new FakeManifest();
        await using var ctx = await CreateAsync(manifest);
        var graph = await SeedGraphAsync(ctx);
        ctx.WorkerBridge.IsWorkerConnected = false;

        var result = await RunAsync(ctx, operations => operations.SetAllAsync(ctx.WorkspaceId, reference: true, alsoSetDefault: true));

        Assert.False(result.Success);
        Assert.Equal(WorkspaceReferenceOperations.WorkerNotConnectedMessage, result.Error);
        Assert.False((await ReadLinkAsync(ctx, graph.ProducerId)).IsReference);
        Assert.False(await ReadDefaultAsync(ctx));
    }

    [Fact]
    public async Task A_definition_that_cannot_be_written_is_a_warning_and_the_flags_stay()
    {
        var manifest = new FakeManifest { DefinitionFailure = "disk full" };
        await using var ctx = await CreateAsync(manifest);
        var graph = await SeedGraphAsync(ctx);
        await MutateLinkAsync(ctx, graph.ConsumerId, link => link.Role = WorkspaceRepositoryRole.Workspace);
        RespondInspect(ctx, _ => Clean());

        var result = await RunAsync(ctx, operations => operations.SetAllAsync(ctx.WorkspaceId, reference: true, alsoSetDefault: false));

        Assert.True(result.Success, result.Error);
        Assert.Contains("disk full", Assert.Single(result.Warnings));
        Assert.True((await ReadLinkAsync(ctx, graph.ProducerId)).IsReference);
        // The flags are not in the file, so a branch cannot be recorded there either.
        Assert.Empty(manifest.Revisions);
    }

    [Fact]
    public async Task The_change_is_refused_while_an_operation_holds_the_lock_of_a_context()
    {
        var manifest = new FakeManifest();
        await using var ctx = await CreateAsync(manifest);
        var graph = await SeedGraphAsync(ctx);
        var special = await ctx.GetSpecialContextIdAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(ctx.Resolve<IWorkspaceOperationLock>().TryStartContext(
            special,
            ctx.WorkspaceId,
            "push",
            WorkspaceJobKeys.ContextOverlayKey(ctx.WorkspaceId, special.Value),
            "Push...",
            async (_, _) =>
            {
                entered.SetResult();
                await release.Task;
            },
            out var running));
        await entered.Task;

        var result = await RunAsync(ctx, operations => operations.SetAllAsync(ctx.WorkspaceId, reference: true, alsoSetDefault: true));

        Assert.False(result.Success);
        Assert.Equal(WorkspaceReferenceOperations.BusyMessage, result.Error);
        Assert.False((await ReadLinkAsync(ctx, graph.ProducerId)).IsReference);
        Assert.False(await ReadDefaultAsync(ctx));
        Assert.Empty(InspectedPaths(ctx));
        release.SetResult();
        await running.WhenCompleted;
    }

    [Fact]
    public async Task No_operation_can_start_in_a_context_while_the_change_runs()
    {
        var manifest = new FakeManifest();
        await using var ctx = await CreateAsync(manifest);
        var graph = await SeedGraphAsync(ctx);
        var special = await ctx.GetSpecialContextIdAsync();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        RespondInspect(ctx, _ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            return Clean();
        });

        var change = Task.Run(() => RunAsync(ctx, operations => operations.SetAllAsync(ctx.WorkspaceId, reference: true, alsoSetDefault: false)));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));

        var ran = false;
        var started = ctx.Resolve<IWorkspaceOperationLock>().TryStartContext(
            special,
            ctx.WorkspaceId,
            "push",
            WorkspaceJobKeys.ContextOverlayKey(ctx.WorkspaceId, special.Value),
            "Push...",
            (_, _) =>
            {
                ran = true;
                return Task.CompletedTask;
            },
            out _);
        release.Set();
        var result = await change;

        Assert.False(started);
        Assert.False(ran);
        Assert.True(result.Success, result.Error);
        Assert.True((await ReadLinkAsync(ctx, graph.ProducerId)).IsReference);
    }

    [Fact]
    public async Task A_pull_of_a_read_only_repository_records_the_new_commit()
    {
        var manifest = new FakeManifest();
        await using var ctx = await CreateWithReferenceProducerAsync(manifest);
        ctx.WorkerBridge.Respond("CommitSyncRepository", PulledResponse());

        await PullAsync(ctx, await ctx.GetSpecialContextIdAsync());

        var revision = Assert.Single(manifest.Revisions);
        Assert.Contains(ProducerName, revision.RepositoryUrl);
        Assert.Equal("main", revision.Branch);
        Assert.Equal(Pulled, revision.Commit);
    }

    [Fact]
    public async Task A_pull_of_a_writable_repository_records_nothing()
    {
        var manifest = new FakeManifest();
        await using var ctx = await CreateWithReferenceProducerAsync(manifest);
        await MutateLinkAsync(ctx, ctx.RepositoryId, link => link.IsReference = false);
        ctx.WorkerBridge.Respond("CommitSyncRepository", PulledResponse());

        await PullAsync(ctx, await ctx.GetSpecialContextIdAsync());

        Assert.Empty(manifest.Revisions);
    }

    [Fact]
    public async Task A_pull_that_the_Worker_reports_as_failed_records_nothing()
    {
        var manifest = new FakeManifest();
        await using var ctx = await CreateWithReferenceProducerAsync(manifest);
        ctx.WorkerBridge.Respond("CommitSyncRepository", new { success = false, branch = "main", commit = Pulled, errorMessage = "The branch has local commits." });

        await PullAsync(ctx, await ctx.GetSpecialContextIdAsync());

        Assert.Empty(manifest.Revisions);
    }

    [Fact]
    public async Task A_definition_write_that_fails_does_not_fail_the_pull()
    {
        var manifest = new FakeManifest { RevisionFailure = "The Workspace definition was not found." };
        await using var ctx = await CreateWithReferenceProducerAsync(manifest);
        ctx.WorkerBridge.Respond("CommitSyncRepository", PulledResponse());

        var result = await PullAsync(ctx, await ctx.GetSpecialContextIdAsync());

        Assert.True(result.Success, result.Error);
        Assert.Single(manifest.Revisions);
    }

    [Fact]
    public async Task A_manual_switch_of_a_read_only_repository_records_the_branch_and_commit()
    {
        var manifest = new FakeManifest();
        await using var ctx = await CreateWithReferenceProducerAsync(manifest);
        ctx.WorkerBridge.Respond("CheckoutBranch", new CheckoutBranchResponse { Success = true, CurrentBranch = "release/1", Commit = Pulled });

        var body = await CheckoutAsync(ctx, await ctx.GetSpecialContextIdAsync(), "release/1");

        Assert.True(body.Success, body.ErrorMessage);
        Assert.Null(body.Warning);
        var revision = Assert.Single(manifest.Revisions);
        Assert.Equal("release/1", revision.Branch);
        Assert.Equal(Pulled, revision.Commit);
    }

    [Fact]
    public async Task A_manual_switch_of_a_writable_repository_records_nothing()
    {
        var manifest = new FakeManifest();
        await using var ctx = await CreateWithReferenceProducerAsync(manifest);
        await MutateLinkAsync(ctx, ctx.RepositoryId, link => link.IsReference = false);
        ctx.WorkerBridge.Respond("CheckoutBranch", new CheckoutBranchResponse { Success = true, CurrentBranch = "release/1", Commit = Pulled });

        var body = await CheckoutAsync(ctx, await ctx.GetSpecialContextIdAsync(), "release/1");

        Assert.True(body.Success, body.ErrorMessage);
        Assert.Empty(manifest.Revisions);
    }

    [Fact]
    public async Task A_manual_switch_succeeds_with_a_warning_when_the_definition_cannot_be_written()
    {
        var manifest = new FakeManifest { RevisionFailure = "The Workspace definition was not found." };
        await using var ctx = await CreateWithReferenceProducerAsync(manifest);
        ctx.WorkerBridge.Respond("CheckoutBranch", new CheckoutBranchResponse { Success = true, CurrentBranch = "release/1", Commit = Pulled });

        var body = await CheckoutAsync(ctx, await ctx.GetSpecialContextIdAsync(), "release/1");

        Assert.True(body.Success, body.ErrorMessage);
        Assert.Contains("The Workspace definition was not found", body.Warning);
    }

    [Fact]
    public async Task A_manual_switch_answered_without_a_commit_warns_and_records_nothing()
    {
        var manifest = new FakeManifest();
        await using var ctx = await CreateWithReferenceProducerAsync(manifest);
        ctx.WorkerBridge.Respond("CheckoutBranch", new CheckoutBranchResponse { Success = true, CurrentBranch = "release/1" });

        var body = await CheckoutAsync(ctx, await ctx.GetSpecialContextIdAsync(), "release/1");

        Assert.True(body.Success, body.ErrorMessage);
        Assert.NotNull(body.Warning);
        Assert.Empty(manifest.Revisions);
    }

    [Fact]
    public async Task Nothing_is_recorded_from_a_Feature()
    {
        var manifest = new FakeManifest();
        await using var ctx = await CreateWithReferenceProducerAsync(manifest);
        var feature = await SeedFeatureAsync(ctx, [new FeatureRepo(ctx.RepositoryId)]);

        var warning = await ctx.Resolve<ReferenceRevisionRecorder>()
            .RecordAsync(ctx.WorkspaceId, feature, ctx.RepositoryId, "feature/y", Pulled);

        Assert.Null(warning);
        Assert.Empty(manifest.Revisions);
    }

    [Fact]
    public async Task Nothing_is_recorded_without_a_Workspace_repository()
    {
        var manifest = new FakeManifest();
        await using var ctx = await CreateAsync(manifest);
        var graph = await SeedGraphAsync(ctx);
        await MutateLinkAsync(ctx, graph.ProducerId, link => link.IsReference = true);

        var warning = await ctx.Resolve<ReferenceRevisionRecorder>()
            .RecordAsync(ctx.WorkspaceId, [new ReferenceRevision(graph.ProducerId, "main", Pulled)]);

        Assert.Null(warning);
        Assert.Empty(manifest.Revisions);
    }

    [Fact]
    public async Task A_hook_report_with_a_new_head_does_not_touch_the_Workspace_definition()
    {
        var manifest = new FakeManifest();
        await using var ctx = await CreateWithReferenceProducerAsync(manifest);

        await using (var scope = ctx.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<SyncCommandHandler>().HandleAsync(new RepositorySyncNotification
            {
                WorkspaceId = ctx.WorkspaceId,
                RepositoryId = ctx.RepositoryId,
                Version = "2.0.0",
                Branch = "release/1",
                RemoteBranches = ["main", "release/1"],
            });
        }

        Assert.Empty(manifest.Revisions);
        Assert.Equal(0, manifest.DefinitionWrites);
    }

    [Fact]
    public async Task A_Sync_that_finds_a_new_head_does_not_touch_the_Workspace_definition()
    {
        var manifest = new FakeManifest();
        await using var ctx = await CreateWithReferenceProducerAsync(manifest);
        ctx.WorkerBridge.Respond("SyncRepository", new
        {
            version = "2.0.0",
            branch = "release/1",
            defaultBranch = "main",
            outgoingCommits = 0,
            incomingCommits = 0,
            hasUpstream = true,
            upstreamProbed = true,
            headCommit = Pulled,
        });

        await using (var scope = ctx.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WorkspaceGitService>()
                .SyncAsync(ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync());
        }

        Assert.Empty(manifest.Revisions);
    }

    private static object PulledResponse() => new
    {
        success = true,
        mergeConflict = false,
        version = "2.0.0",
        branch = "main",
        commit = Pulled,
        outgoingCommits = 0,
        incomingCommits = 0,
        defaultBranchBehind = 0,
        defaultBranchAhead = 0,
        hasUpstream = true,
    };

    [Fact]
    public async Task Toggling_one_of_twenty_repositories_changes_no_sibling_and_not_the_default()
    {
        var manifest = new FakeManifest();
        await using var ctx = await CreateAsync(manifest);
        var graph = await SeedGraphAsync(ctx);
        var siblings = await AddSourcesAsync(ctx, 18);
        RespondInspect(ctx, _ => Clean());

        var on = await RunAsync(ctx, operations => operations.SetRepositoryAsync(ctx.WorkspaceId, graph.ConsumerId, true));
        var afterOn = await StateAsync(ctx);

        Assert.True(on.Success, on.Error);
        Assert.Equal(1, on.ChangedRepositoryCount);
        Assert.Equal(20, afterOn.Repositories.Count);
        Assert.Equal(WorkspaceReferenceAggregate.Mixed, afterOn.Aggregate);
        Assert.Equal([graph.ConsumerId], afterOn.Repositories.Where(repository => repository.IsReference).Select(repository => repository.RepositoryId).ToArray());
        Assert.False(afterOn.ReferenceDefault);
        // Only the changed repository was checked.
        Assert.Single(InspectedPaths(ctx));

        // The other direction: all twenty read-only and the default on, then one made writable.
        var all = await RunAsync(ctx, operations => operations.SetAllAsync(ctx.WorkspaceId, true, alsoSetDefault: true));
        var off = await RunAsync(ctx, operations => operations.SetRepositoryAsync(ctx.WorkspaceId, siblings[3], false));
        var afterOff = await StateAsync(ctx);

        Assert.True(all.Success, all.Error);
        Assert.True(off.Success, off.Error);
        Assert.Equal(1, off.ChangedRepositoryCount);
        Assert.Equal(WorkspaceReferenceAggregate.Mixed, afterOff.Aggregate);
        Assert.Equal([siblings[3]], afterOff.Repositories.Where(repository => !repository.IsReference).Select(repository => repository.RepositoryId).ToArray());
        Assert.True(afterOff.ReferenceDefault);
    }

    [Fact]
    public async Task The_state_suggests_read_only_for_an_archived_repository_until_it_is_read_only()
    {
        var manifest = new FakeManifest();
        await using var ctx = await CreateAsync(manifest);
        var graph = await SeedGraphAsync(ctx);
        RespondInspect(ctx, _ => Clean());
        await using (var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
        {
            await db.Repositories.Where(r => r.RepositoryId == graph.ProducerId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Archived, true));
        }

        var before = await StateAsync(ctx);
        var change = await RunAsync(ctx, operations => operations.SetRepositoryAsync(ctx.WorkspaceId, graph.ProducerId, true));
        var after = await StateAsync(ctx);

        var archived = before.Repositories.Single(repository => repository.RepositoryId == graph.ProducerId);
        var other = before.Repositories.Single(repository => repository.RepositoryId == graph.ConsumerId);
        Assert.True(archived.Archived);
        Assert.True(archived.SuggestReference);
        Assert.False(other.Archived);
        Assert.False(other.SuggestReference);
        // Archived alone changes nothing: the repository was still writable, and the usual check ran when it was changed.
        Assert.False(archived.IsReference);
        Assert.True(change.Success, change.Error);
        Assert.Single(InspectedPaths(ctx));
        var archivedAfter = after.Repositories.Single(repository => repository.RepositoryId == graph.ProducerId);
        Assert.True(archivedAfter.Archived);
        Assert.False(archivedAfter.SuggestReference);
    }

    [Fact]
    public async Task An_archived_repository_with_unpushed_commits_is_refused_like_any_other()
    {
        var manifest = new FakeManifest();
        await using var ctx = await CreateAsync(manifest);
        var graph = await SeedGraphAsync(ctx);
        var unpushed = Clean();
        unpushed.AheadOfUpstream = 2;
        RespondInspect(ctx, _ => unpushed);
        await using (var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
        {
            await db.Repositories.Where(r => r.RepositoryId == graph.ProducerId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Archived, true));
        }

        var change = await RunAsync(ctx, operations => operations.SetRepositoryAsync(ctx.WorkspaceId, graph.ProducerId, true));

        Assert.False(change.Success);
        Assert.NotEmpty(change.Blocks);
        Assert.False((await ReadLinkAsync(ctx, graph.ProducerId)).IsReference);
        Assert.True((await StateAsync(ctx)).Repositories.Single(repository => repository.RepositoryId == graph.ProducerId).SuggestReference);
    }

    [Fact]
    public async Task Setting_a_repository_to_the_state_it_already_has_changes_nothing()
    {
        var manifest = new FakeManifest();
        await using var ctx = await CreateAsync(manifest);
        var graph = await SeedGraphAsync(ctx);
        RespondInspect(ctx, _ => Clean());

        var result = await RunAsync(ctx, operations => operations.SetRepositoryAsync(ctx.WorkspaceId, graph.ProducerId, false));

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, result.ChangedRepositoryCount);
        Assert.Empty(InspectedPaths(ctx));
        Assert.False((await ReadLinkAsync(ctx, graph.ProducerId)).IsReference);
    }

    /// <summary>Adds writable Source repositories to the Workspace and returns their ids.</summary>
    private static async Task<List<int>> AddSourcesAsync(SyncStateTestContext ctx, int count)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var connectorId = await db.Repositories.Where(r => r.RepositoryId == ctx.RepositoryId).Select(r => r.ConnectorId).SingleAsync();
        var repositories = Enumerable.Range(1, count)
            .Select(i => new Repository
            {
                ConnectorId = connectorId,
                RepositoryName = $"Sibling{i:00}",
                OrgName = "acme",
                Visibility = "Public",
                CloneUrl = $"https://github.com/acme/Sibling{i:00}.git",
            })
            .ToList();
        db.Repositories.AddRange(repositories);
        await db.SaveChangesAsync();
        db.WorkspaceRepositories.AddRange(repositories.Select(repository => new WorkspaceRepositoryLink
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = repository.RepositoryId,
            BranchName = "main",
            DefaultBranchName = "main",
            BranchHasUpstream = true,
        }));
        await db.SaveChangesAsync();
        return repositories.Select(repository => repository.RepositoryId).ToList();
    }

    private static Task<SyncStateTestContext> CreateAsync(FakeManifest manifest) =>
        Characterization.PolicyHarness.CreateAsync(services => services.AddScoped<IWorkspaceManifestService>(_ => manifest));

    /// <summary>The producer is a read-only Source on <c>main</c>; the consumer is the Workspace repository.</summary>
    private static async Task<SyncStateTestContext> CreateWithReferenceProducerAsync(FakeManifest manifest)
    {
        var ctx = await CreateAsync(manifest);
        var graph = await SeedGraphAsync(ctx);
        await MutateLinkAsync(ctx, graph.ConsumerId, link => link.Role = WorkspaceRepositoryRole.Workspace);
        await MutateLinkAsync(ctx, graph.ProducerId, link =>
        {
            link.IsReference = true;
            link.BranchName = "main";
            link.DefaultBranchName = "main";
        });
        return ctx;
    }

    private static async Task<WorkspaceReferenceChangeResult> RunAsync(
        SyncStateTestContext ctx, Func<IWorkspaceReferenceOperations, Task<WorkspaceReferenceChangeResult>> run)
    {
        await using var scope = ctx.CreateScope();
        return await run(scope.ServiceProvider.GetRequiredService<IWorkspaceReferenceOperations>());
    }

    private static async Task<WorkspaceReferenceState> StateAsync(SyncStateTestContext ctx)
    {
        await using var scope = ctx.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkspaceReferenceOperations>().GetStateAsync(ctx.WorkspaceId);
    }

    private static async Task<OperationResult> PullAsync(SyncStateTestContext ctx, WorkspaceFeatureContextId contextId)
    {
        await using var scope = ctx.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkspaceSyncOperations>()
            .PullAsync(ctx.WorkspaceId, contextId, ctx.RepositoryId, null, CancellationToken.None);
    }

    private static async Task<CheckoutBranchApiResult> CheckoutAsync(SyncStateTestContext ctx, WorkspaceFeatureContextId contextId, string branch)
    {
        await using var scope = ctx.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<IWorkspaceBranchOperations>()
            .CheckoutAsync(ctx.WorkspaceId, contextId, ctx.RepositoryId, branch, isTag: false);
        Assert.True(outcome.IsSuccessStatus);
        return Assert.IsType<CheckoutBranchApiResult>(outcome.Body);
    }

    private static async Task<bool> ReadDefaultAsync(SyncStateTestContext ctx)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        return await db.Workspaces.AsNoTracking().Where(w => w.WorkspaceId == ctx.WorkspaceId).Select(w => w.ReferenceDefault).SingleAsync();
    }

    private static void RespondInspect(SyncStateTestContext ctx, Func<string, InspectWorktreeWorkerResponse> answer) =>
        ctx.WorkerBridge.Respond("InspectWorktree", args =>
            new WorkerCommandResponse(true, answer(JsonSerializer.SerializeToElement(args).GetProperty("worktreePath").GetString()!), null));

    private static List<string> InspectedPaths(SyncStateTestContext ctx) =>
        [.. ctx.WorkerBridge.Calls
            .Where(call => call.Command == "InspectWorktree")
            .Select(call => JsonSerializer.SerializeToElement(call.Args).GetProperty("worktreePath").GetString()!)];

    /// <summary>Records what would be written to the Workspace definition; nothing is read or written.</summary>
    private sealed class FakeManifest : IWorkspaceManifestService
    {
        private readonly List<WorkspaceRepositoryReferenceRevisionChange> _revisions = [];
        private readonly List<string> _log = [];

        /// <summary>When set, recording a branch and commit fails with this message.</summary>
        public string? RevisionFailure { get; init; }

        /// <summary>When set, writing the definition from the database fails with this message.</summary>
        public string? DefinitionFailure { get; init; }

        public IReadOnlyList<WorkspaceRepositoryReferenceRevisionChange> Revisions
        {
            get
            {
                lock (_log)
                    return _revisions.ToList();
            }
        }

        /// <summary>"definition" and "revisions", in the order they were written.</summary>
        public IReadOnlyList<string> Log
        {
            get
            {
                lock (_log)
                    return _log.ToList();
            }
        }

        public int DefinitionWrites => Log.Count(entry => entry == "definition");

        public Task<OperationResult> WriteAuthoritativeManifestAsync(int workspaceId, CancellationToken cancellationToken = default)
        {
            lock (_log)
                _log.Add("definition");
            return Task.FromResult(DefinitionFailure is null ? OperationResult.Ok() : OperationResult.Fail(DefinitionFailure));
        }

        public Task<OperationResult> SetRepositoryReferenceRevisionsAsync(int workspaceId, IReadOnlyList<WorkspaceRepositoryReferenceRevisionChange> changes, CancellationToken cancellationToken = default)
        {
            lock (_log)
            {
                _log.Add("revisions");
                _revisions.AddRange(changes);
            }
            return Task.FromResult(RevisionFailure is null ? OperationResult.Ok() : OperationResult.Fail(RevisionFailure));
        }

        public Task<OperationResult> SetRepositoryTagPinsAsync(int workspaceId, IReadOnlyList<WorkspaceRepositoryTagPinChange> changes, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Ok());

        public Task<WorkspaceManifestDrift> DetectDriftAsync(int workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkspaceManifestDrift(false, null, [], [], [], [], []));

        public Task<WorkspaceManifest> BuildFromDatabaseAsync(int workspaceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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

        public Task<(WorkspaceManifestAi? Ai, string? Error)> ReadAiAsync(int workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<(WorkspaceManifestAi?, string?)>((null, null));

        public Task<OperationResult> WriteCodeGraphConfigAsync(int workspaceId, WorkspaceFeatureContextId? contextId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Ok());
    }
}
