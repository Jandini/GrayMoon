using System.Text.Json;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// The completion of a hook batch ran the whole context recompute (effective versions, the file-version check, the
/// dependency stats) for every notification that was not refs-only, whether it changed anything or not. It now
/// runs when a notification changed something the recompute reads: the checked-out commit, the version or the
/// projects. The full recompute is the reference: after any sequence, running it must change nothing.
/// </summary>
public sealed class ScopedRecomputeTests
{
    private const string ConsumerName = "graymoon-web";
    private const string PackageName = "Acme.Api";
    private const string Head1 = "1111111111111111111111111111111111111111";
    private const string Head2 = "2222222222222222222222222222222222222222";

    /// <summary>A value no recompute writes: when it is still there, no recompute ran.</summary>
    private const int Untouched = 77;

    // ---------------------------------------------------------------- counts: did the recompute run

    [Fact]
    public async Task A_notification_that_confirms_the_stored_state_does_not_recompute_and_is_still_announced()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await HandleAsync(ctx, Producer(ctx, Head1, "1.0.0"));
        await MarkDerivedStateAsync(ctx, graph);
        var announced = ctx.Broadcasts.Count(b => b.Method == "ContextSynced");

        await HandleAsync(ctx, Producer(ctx, Head1, "1.0.0"));

        Assert.Equal(Untouched, await ReadUnmatchedAsync(ctx, graph));
        // The page is still told: a pull request or a sync flag may have changed with the notification.
        Assert.Equal(announced + 1, ctx.Broadcasts.Count(b => b.Method == "ContextSynced"));
    }

    [Fact]
    public async Task A_notification_that_changes_commit_counts_only_stores_them_and_does_not_recompute()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await HandleAsync(ctx, Producer(ctx, Head1, "1.0.0"));
        await MarkDerivedStateAsync(ctx, graph);

        // A fetch brought three commits: nothing that is checked out, versioned or scanned changed.
        await HandleAsync(ctx, Producer(ctx, Head1, "1.0.0", incoming: 3));

        Assert.Equal(3, (await ctx.ReadLinkAsync()).IncomingCommits);
        Assert.Equal(Untouched, await ReadUnmatchedAsync(ctx, graph));
    }

    [Fact]
    public async Task A_branch_created_outside_GrayMoon_does_not_recompute()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await HandleAsync(ctx, Producer(ctx, Head1, "1.0.0"));
        await MarkDerivedStateAsync(ctx, graph);

        await HandleAsync(ctx, new RepositorySyncNotification
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepositoryId,
            State = new RepositoryStateSnapshot { LocalBranches = ["main", "topic"], RemoteBranches = ["main"], BranchesProbed = true },
        });

        Assert.Contains(await ctx.ReadBranchesAsync(), b => b.BranchName == "topic");
        Assert.Equal(Untouched, await ReadUnmatchedAsync(ctx, graph));
    }

    [Fact]
    public async Task A_commit_that_changes_the_version_recomputes_its_consumers()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await HandleAsync(ctx, Producer(ctx, Head1, "1.0.0"));
        await MarkDerivedStateAsync(ctx, graph);

        await HandleAsync(ctx, Producer(ctx, Head2, "2.0.0"));

        // The consumer still references 1.0.0 of a package that is now 2.0.0.
        Assert.Equal(1, await ReadUnmatchedAsync(ctx, graph));
    }

    [Fact]
    public async Task An_edited_project_file_recomputes_although_commit_and_version_are_the_same()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await HandleAsync(ctx, Consumer(ctx, graph, Head1, references: "1.0.0"));
        await MarkDerivedStateAsync(ctx, graph);

        // The scan found another referenced version: the edge changed, nothing else did.
        await HandleAsync(ctx, Consumer(ctx, graph, Head1, references: "0.9.0"));

        Assert.Equal(1, await ReadUnmatchedAsync(ctx, graph));
        Assert.Equal("0.9.0", Assert.Single(await ctx.ReadDependenciesAsync()).Version);
    }

    [Fact]
    public async Task A_recompute_that_is_owed_runs_with_the_next_notification_whatever_it_changed()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await HandleAsync(ctx, Producer(ctx, Head1, "1.0.0"));
        await MarkDerivedStateAsync(ctx, graph);
        var special = await ctx.GetSpecialContextIdAsync();
        // What a failed recompute leaves behind.
        ctx.Resolve<SyncRecomputeCoalescer>().MarkOwed($"{ctx.WorkspaceId}:{special.Value}");

        await HandleAsync(ctx, Producer(ctx, Head1, "1.0.0"));

        Assert.Equal(0, await ReadUnmatchedAsync(ctx, graph));
        // Paid once: the next confirming notification does not recompute again.
        await MarkDerivedStateAsync(ctx, graph);
        await HandleAsync(ctx, Producer(ctx, Head1, "1.0.0"));
        Assert.Equal(Untouched, await ReadUnmatchedAsync(ctx, graph));
    }

    [Fact]
    public async Task A_Workspace_profile_that_changed_recomputes_with_the_next_notification_whatever_it_changed()
    {
        // Versions, version file values and dependency state are derived under the profile. A profile change
        // writes no repository state, so no notification reports it as a change.
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await HandleAsync(ctx, Producer(ctx, Head1, "1.0.0"));
        await MarkDerivedStateAsync(ctx, graph);
        await using (var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
        {
            var workspace = await db.Workspaces.SingleAsync(w => w.WorkspaceId == ctx.WorkspaceId);
            workspace.CiProvider = WorkspaceCiProvider.None;
            await db.SaveChangesAsync();
        }

        await HandleAsync(ctx, Producer(ctx, Head1, "1.0.0"));

        Assert.Equal(0, await ReadUnmatchedAsync(ctx, graph));
        // Once: the profile is the same again at the next notification.
        await MarkDerivedStateAsync(ctx, graph);
        await HandleAsync(ctx, Producer(ctx, Head1, "1.0.0"));
        Assert.Equal(Untouched, await ReadUnmatchedAsync(ctx, graph));
    }

    [Fact]
    public void Forgetting_a_key_removes_what_was_owed_and_the_remembered_profile()
    {
        var coalescer = new SyncRecomputeCoalescer();
        coalescer.MarkOwed("7:11");
        Assert.False(coalescer.ObserveProfile("7:11", "A"));
        Assert.True(coalescer.ObserveProfile("7:11", "B"));

        coalescer.Forget("7:11");

        Assert.False(coalescer.TakeOwed("7:11"));
        // A context id that is used again starts without the old context's profile.
        Assert.False(coalescer.ObserveProfile("7:11", "A"));
    }

    [Theory]
    [InlineData(RepositoryFactGroups.None, false)]
    [InlineData(RepositoryFactGroups.CommitCounts | RepositoryFactGroups.Upstream | RepositoryFactGroups.Branches, false)]
    [InlineData(RepositoryFactGroups.Identity, true)]
    [InlineData(RepositoryFactGroups.GitVersion, true)]
    [InlineData(RepositoryFactGroups.Projects | RepositoryFactGroups.CommitCounts, true)]
    public void Only_a_change_the_recompute_reads_asks_for_it(RepositoryFactGroups changed, bool expected)
        => Assert.Equal(expected, WorkspaceStateRecomputeScope.NeedsRecompute(changed));

    [Fact]
    public void A_write_that_did_not_say_what_changed_asks_for_the_recompute()
        => Assert.True(WorkspaceStateRecomputeScope.NeedsRecompute(null));

    // ---------------------------------------------------------------- equivalence with the full recompute

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task After_any_sequence_of_notifications_the_full_recompute_changes_nothing(bool dotNetDependencyProfile)
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var graph = await SeedGraphAsync(ctx, dotNetDependencyProfile);
        var special = await ctx.GetSpecialContextIdAsync();

        // Changes the recompute reads, each followed by notifications it does not read. After every one of them
        // the derived state must already be what a full recompute of the stored rows gives.
        RepositorySyncNotification[] sequence =
        [
            Producer(ctx, Head1, "1.0.0"),
            Producer(ctx, Head1, "1.0.0"),
            Consumer(ctx, graph, Head1, references: "1.0.0"),
            Producer(ctx, Head1, "1.0.0", incoming: 4),
            Producer(ctx, Head2, "2.0.0"),
            Producer(ctx, Head2, "2.0.0", incoming: 0, outgoing: 2),
            Consumer(ctx, graph, Head1, references: "1.0.0"),
            Consumer(ctx, graph, Head2, references: "2.0.0"),
            Consumer(ctx, graph, Head2, references: "2.0.0", incoming: 1),
            Producer(ctx, Head1, "1.5.0"),
            Producer(ctx, Head1, "1.5.0"),
        ];

        for (var i = 0; i < sequence.Length; i++)
        {
            await HandleAsync(ctx, sequence[i]);
            var incremental = await ReadDerivedStateAsync(ctx);

            await using (var scope = ctx.CreateScope())
                await scope.ServiceProvider.GetRequiredService<WorkspaceStateRecomputeScope>().RecomputeAsync(ctx.WorkspaceId, special);

            Assert.True(
                incremental == await ReadDerivedStateAsync(ctx),
                $"After notification {i} the stored derived state differs from the full recompute.\nstored: {incremental}\nfull:   {await ReadDerivedStateAsync(ctx)}");
        }

        // The sequence is not vacuous: the dependency profile ends with a consumer behind its package.
        if (dotNetDependencyProfile)
            Assert.Equal(1, await ReadUnmatchedAsync(ctx, graph));
        else
            Assert.Null(await ReadUnmatchedAsync(ctx, graph));
    }

    // ---------------------------------------------------------------- what the writer reports for projects

    [Fact]
    public async Task A_project_scan_that_finds_the_stored_projects_is_not_reported_as_a_change()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        var special = await ctx.GetSpecialContextIdAsync();
        await using var scope = ctx.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>();

        var same = await writer.ApplyAsync(special, ctx.WorkspaceId, graph.ConsumerId, Consumer(ctx, graph, null, "1.0.0").State!);
        var edge = await writer.ApplyAsync(special, ctx.WorkspaceId, graph.ConsumerId, Consumer(ctx, graph, null, "3.0.0").State!);
        var none = await writer.ApplyAsync(special, ctx.WorkspaceId, graph.ConsumerId, new RepositoryStateSnapshot { Projects = [], ProjectsProbed = true });

        Assert.True(same.Probed.HasFlag(RepositoryFactGroups.Projects));
        Assert.False(same.Changed.HasFlag(RepositoryFactGroups.Projects));
        Assert.True(edge.Changed.HasFlag(RepositoryFactGroups.Projects));
        Assert.True(none.Changed.HasFlag(RepositoryFactGroups.Projects));
    }

    // ---------------------------------------------------------------- fixture

    private sealed record Graph(int ConsumerId, int ConsumerLinkId);

    private static RepositorySyncProjectNotification ProducerProject() => new()
    {
        Name = PackageName,
        ProjectType = 3,
        ProjectPath = "src/Acme.Api/Acme.Api.csproj",
        TargetFramework = "net10.0",
        PackageId = PackageName,
        PackageReferences = [],
    };

    private static RepositorySyncProjectNotification ConsumerProject(string references) => new()
    {
        Name = "Acme.Web",
        ProjectType = 2,
        ProjectPath = "src/Acme.Web/Acme.Web.csproj",
        TargetFramework = "net10.0",
        PackageReferences = [new RepositorySyncPackageReferenceNotification { Name = PackageName, Version = references }],
    };

    private static RepositoryStateSnapshot Snapshot(string? head, string version, RepositorySyncProjectNotification project, int incoming, int outgoing) => new()
    {
        BranchName = "main",
        HeadCommit = head,
        GitVersion = version,
        DefaultBranchName = "main",
        OutgoingCommits = outgoing,
        IncomingCommits = incoming,
        DefaultBranchBehind = 0,
        DefaultBranchAhead = 0,
        HasUpstream = true,
        Projects = [project],
        IdentityProbed = true,
        GitVersionProbed = true,
        CommitCountsProbed = true,
        UpstreamProbed = true,
        ProjectsProbed = true,
    };

    private static RepositorySyncNotification Producer(SyncStateTestContext ctx, string head, string version, int incoming = 0, int outgoing = 0) => new()
    {
        WorkspaceId = ctx.WorkspaceId,
        RepositoryId = ctx.RepositoryId,
        Version = version,
        Branch = "main",
        State = Snapshot(head, version, ProducerProject(), incoming, outgoing),
    };

    private static RepositorySyncNotification Consumer(SyncStateTestContext ctx, Graph graph, string? head, string references, int incoming = 0) => new()
    {
        WorkspaceId = ctx.WorkspaceId,
        RepositoryId = graph.ConsumerId,
        Version = "1.0.0",
        Branch = "main",
        State = Snapshot(head, "1.0.0", ConsumerProject(references), incoming, 0),
    };

    private static async Task HandleAsync(SyncStateTestContext ctx, RepositorySyncNotification notification)
    {
        await using var scope = ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SyncCommandHandler>().HandleAsync(notification, RepositoryStateFreshness.Now());
    }

    /// <summary>A package producer (the seeded repository) and a consumer that references the package at its current version, recomputed.</summary>
    private static async Task<Graph> SeedGraphAsync(SyncStateTestContext ctx, bool dotNetDependencyProfile = true)
    {
        if (dotNetDependencyProfile)
            await ctx.UseDotNetDependencyProfileAsync();

        int consumerId, consumerLinkId;
        await using (var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
        {
            var producer = await db.Repositories.AsNoTracking().FirstAsync(r => r.RepositoryId == ctx.RepositoryId);
            var consumer = new Repository
            {
                ConnectorId = producer.ConnectorId,
                RepositoryName = ConsumerName,
                OrgName = "acme",
                Visibility = "Public",
                CloneUrl = $"https://github.com/acme/{ConsumerName}.git",
            };
            db.Repositories.Add(consumer);
            await db.SaveChangesAsync();
            var link = new WorkspaceRepositoryLink
            {
                WorkspaceId = ctx.WorkspaceId,
                RepositoryId = consumer.RepositoryId,
                BranchName = "main",
                DefaultBranchName = "main",
                SyncStatus = RepoSyncStatus.InSync,
            };
            db.WorkspaceRepositories.Add(link);
            await db.SaveChangesAsync();
            (consumerId, consumerLinkId) = (consumer.RepositoryId, link.WorkspaceRepositoryId);
        }

        var graph = new Graph(consumerId, consumerLinkId);
        var special = await ctx.GetSpecialContextIdAsync();
        await using var scope = ctx.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<WorkspaceRepositoryStateWriter>();
        await writer.ApplyAsync(special, ctx.WorkspaceId, ctx.RepositoryId, Snapshot(null, "1.0.0", ProducerProject(), 0, 0));
        await writer.ApplyAsync(special, ctx.WorkspaceId, consumerId, Snapshot(null, "1.0.0", ConsumerProject("1.0.0"), 0, 0));
        await scope.ServiceProvider.GetRequiredService<WorkspaceStateRecomputeScope>().RecomputeAsync(ctx.WorkspaceId, special);
        return graph;
    }

    /// <summary>Overwrites the consumer's unmatched-dependency count with a value the recompute never produces.</summary>
    private static async Task MarkDerivedStateAsync(SyncStateTestContext ctx, Graph graph)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        await db.WorkspaceRepositoryContextStates
            .Where(s => s.WorkspaceRepositoryId == graph.ConsumerLinkId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UnmatchedDeps, Untouched));
        await db.WorkspaceRepositories
            .Where(l => l.WorkspaceRepositoryId == graph.ConsumerLinkId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UnmatchedDeps, Untouched));
    }

    private static async Task<int?> ReadUnmatchedAsync(SyncStateTestContext ctx, Graph graph)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var state = await db.WorkspaceRepositoryContextStates.AsNoTracking().SingleAsync(s => s.WorkspaceRepositoryId == graph.ConsumerLinkId);
        var link = await db.WorkspaceRepositories.AsNoTracking().SingleAsync(l => l.WorkspaceRepositoryId == graph.ConsumerLinkId);
        // The special Workspace keeps both in step.
        Assert.Equal(state.UnmatchedDeps, link.UnmatchedDeps);
        return state.UnmatchedDeps;
    }

    /// <summary>Everything the recompute derives, for every repository of the Workspace, as one comparable text.</summary>
    private static async Task<string> ReadDerivedStateAsync(SyncStateTestContext ctx)
    {
        await using var db = await ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var states = await db.WorkspaceRepositoryContextStates.AsNoTracking()
            .OrderBy(s => s.WorkspaceRepositoryId)
            .Select(s => new
            {
                s.WorkspaceRepositoryId, s.DependencyLevel, s.Dependencies, s.UnmatchedDeps, s.OutOfDateFileLines, s.OutOfDateFileRepos,
                s.TotalFileConfigRepos, s.HasSelfFileVersionToken, s.TotalFileLines, s.EffectiveVersion, s.EffectiveVersionKind,
                s.EffectiveVersionCommitSha, s.EffectiveVersionReasonCode,
            })
            .ToListAsync();
        var links = await db.WorkspaceRepositories.AsNoTracking()
            .OrderBy(l => l.WorkspaceRepositoryId)
            .Select(l => new { l.WorkspaceRepositoryId, l.DependencyLevel, l.Dependencies, l.UnmatchedDeps, l.OutOfDateFileRepos, l.TotalFileConfigRepos })
            .ToListAsync();
        var fileLines = await db.WorkspaceFileLineStatuses.AsNoTracking().CountAsync();
        return JsonSerializer.Serialize(new { states, links, fileLines });
    }
}
