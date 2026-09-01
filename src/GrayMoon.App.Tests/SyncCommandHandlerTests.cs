using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Models;
using GrayMoon.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// Characterisation tests for the agent hook flow. These pin the semantics the grid depends on:
/// tag pinning wipes the branch-scoped badge fields, a real branch clears the tag fields, and
/// both the per-repository and workspace-level broadcasts fire.
/// </summary>
public sealed class SyncCommandHandlerTests
{
    private static RepositorySyncNotification Notification(SyncStateTestContext ctx, Action<RepositorySyncNotificationBuilder> configure)
    {
        var builder = new RepositorySyncNotificationBuilder(ctx.WorkspaceId, ctx.RepositoryId);
        configure(builder);
        return builder.Build();
    }

    [Fact]
    public async Task Tag_checkout_clears_branch_scoped_badge_fields()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<SyncCommandHandler>();

        await handler.HandleAsync(Notification(ctx, n =>
        {
            n.Version = "1.2.3";
            n.Branch = "-";
            n.Tag = "v1.2.3";
        }));

        var link = await ctx.ReadLinkAsync();
        Assert.Equal("v1.2.3", link.CheckedOutTag);
        Assert.Null(link.BranchName);
        Assert.Null(link.BranchHasUpstream);
        Assert.Null(link.OutgoingCommits);
        Assert.Null(link.IncomingCommits);
        Assert.Null(link.DefaultBranchAheadCommits);
        Assert.Null(link.DefaultBranchBehindCommits);
    }

    [Fact]
    public async Task Real_branch_clears_persisted_tag_state()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await ctx.MutateLinkAsync(l =>
        {
            l.CheckedOutTag = "v1.0.0";
            l.HasNewerTag = true;
            l.BranchName = null;
        });

        await using var scope = ctx.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<SyncCommandHandler>();

        await handler.HandleAsync(Notification(ctx, n =>
        {
            n.Version = "2.0.0";
            n.Branch = "main";
            n.OutgoingCommits = 0;
            n.IncomingCommits = 0;
            n.HasUpstream = true;
        }));

        var link = await ctx.ReadLinkAsync();
        Assert.Null(link.CheckedOutTag);
        Assert.Null(link.HasNewerTag);
        Assert.Equal("main", link.BranchName);
        Assert.Equal("2.0.0", link.GitVersion);
        Assert.Equal(0, link.OutgoingCommits);
        Assert.True(link.BranchHasUpstream);
    }

    [Fact]
    public async Task Error_message_keeps_the_row_in_sync_so_the_grid_shows_the_error_badge_not_a_retry_chip()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<SyncCommandHandler>();

        await handler.HandleAsync(Notification(ctx, n =>
        {
            n.Version = "-";
            n.Branch = "-";
            n.ErrorMessage = "fetch failed";
        }));

        var link = await ctx.ReadLinkAsync();
        Assert.Equal(RepoSyncStatus.InSync, link.SyncStatus);

        Assert.Contains(ctx.Broadcasts, b => b.Method == "RepositoryError");
    }

    [Fact]
    public async Task Missing_default_branch_downgrades_status_to_needs_sync()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await ctx.MutateLinkAsync(l => l.DefaultBranchName = null);

        await using var scope = ctx.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<SyncCommandHandler>();

        await handler.HandleAsync(Notification(ctx, n =>
        {
            n.Version = "1.0.1";
            n.Branch = "main";
        }));

        var link = await ctx.ReadLinkAsync();
        Assert.Equal(RepoSyncStatus.NeedsSync, link.SyncStatus);
    }

    [Fact]
    public async Task Remote_branch_list_prunes_rows_that_no_longer_exist_in_git()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using (var seedScope = ctx.CreateScope())
        {
            var git = seedScope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
            await git.PersistBranchesAsync(
                ctx.WorkspaceRepositoryId,
                localBranches: ["main", "feature/x"],
                remoteBranches: ["origin/main", "origin/feature/x"],
                defaultBranchName: "main");
        }

        await using var scope = ctx.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<SyncCommandHandler>();

        await handler.HandleAsync(Notification(ctx, n =>
        {
            n.Version = "1.0.1";
            n.Branch = "main";
            n.RemoteBranches = ["origin/main"];
        }));

        var branches = await ctx.ReadBranchesAsync();
        Assert.Contains(branches, b => b is { IsRemote: true, BranchName: "origin/main" });
        Assert.DoesNotContain(branches, b => b is { IsRemote: true, BranchName: "origin/feature/x" });
        Assert.Contains(branches, b => b is { IsRemote: false, BranchName: "feature/x" });
    }

    [Fact]
    public async Task Both_repository_and_workspace_broadcasts_fire()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<SyncCommandHandler>();

        await handler.HandleAsync(Notification(ctx, n =>
        {
            n.Version = "1.0.1";
            n.Branch = "main";
        }));

        Assert.Contains(ctx.Broadcasts, b => b.Method == "RepositorySynced");
        Assert.Contains(ctx.Broadcasts, b => b.Method == "WorkspaceSynced");
        Assert.DoesNotContain(ctx.Broadcasts, b => b.Method == "RepositoryError");
    }

    [Fact]
    public async Task Workspace_sync_metadata_is_updated()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<SyncCommandHandler>();

        await handler.HandleAsync(Notification(ctx, n =>
        {
            n.Version = "1.0.1";
            n.Branch = "main";
        }));

        await using var readScope = ctx.CreateScope();
        var db = readScope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
        var workspace = await db.Workspaces.FindAsync(ctx.WorkspaceId);
        Assert.NotNull(workspace);
        Assert.True(workspace!.IsInSync);
        Assert.NotNull(workspace.LastSyncedAt);
    }

    [Fact]
    public async Task Projects_in_the_notification_are_merged_into_workspace_projects()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<SyncCommandHandler>();

        await handler.HandleAsync(Notification(ctx, n =>
        {
            n.Version = "1.0.1";
            n.Branch = "main";
            n.Projects =
            [
                new RepositorySyncProjectNotification
                {
                    Name = "Acme.Api",
                    ProjectType = (int)ProjectType.Service,
                    ProjectPath = "src/Acme.Api/Acme.Api.csproj",
                    TargetFramework = "net10.0",
                }
            ];
        }));

        var projects = await ctx.ReadProjectsAsync();
        Assert.Single(projects);
        Assert.Equal("Acme.Api", projects[0].ProjectName);
    }

    [Fact]
    public async Task Projects_with_package_references_update_project_dependencies_before_recompute()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<SyncCommandHandler>();

        await handler.HandleAsync(Notification(ctx, n =>
        {
            n.Version = "2.0.0";
            n.Branch = "main";
            n.Projects =
            [
                new RepositorySyncProjectNotification
                {
                    Name = "Acme.Lib",
                    ProjectType = (int)ProjectType.Package,
                    ProjectPath = "src/Acme.Lib/Acme.Lib.csproj",
                    TargetFramework = "net10.0",
                    PackageId = "Acme.Lib",
                },
                new RepositorySyncProjectNotification
                {
                    Name = "Acme.Api",
                    ProjectType = (int)ProjectType.Service,
                    ProjectPath = "src/Acme.Api/Acme.Api.csproj",
                    TargetFramework = "net10.0",
                    PackageReferences =
                    [
                        new RepositorySyncPackageReferenceNotification { Name = "Acme.Lib", Version = "2.0.0" }
                    ]
                }
            ];
        }));

        var deps = await ctx.ReadDependenciesAsync();
        var edge = Assert.Single(deps);
        Assert.Equal("Acme.Api", edge.DependentProject!.ProjectName);
        Assert.Equal("Acme.Lib", edge.ReferencedProject!.ProjectName);
        Assert.Equal("2.0.0", edge.Version);
    }
}

/// <summary>Mutable builder so tests can set only the fields they care about on an init-only notification.</summary>
public sealed class RepositorySyncNotificationBuilder(int workspaceId, int repositoryId)
{
    public string Version { get; set; } = "-";
    public string Branch { get; set; } = "-";
    public string? Tag { get; set; }
    public int? OutgoingCommits { get; set; }
    public int? IncomingCommits { get; set; }
    public bool? HasUpstream { get; set; }
    public int? DefaultBranchBehind { get; set; }
    public int? DefaultBranchAhead { get; set; }
    public List<RepositorySyncProjectNotification>? Projects { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string>? RemoteBranches { get; set; }
    public List<string>? RemoteTags { get; set; }

    public RepositorySyncNotification Build() => new()
    {
        WorkspaceId = workspaceId,
        RepositoryId = repositoryId,
        Version = Version,
        Branch = Branch,
        Tag = Tag,
        OutgoingCommits = OutgoingCommits,
        IncomingCommits = IncomingCommits,
        HasUpstream = HasUpstream,
        DefaultBranchBehind = DefaultBranchBehind,
        DefaultBranchAhead = DefaultBranchAhead,
        Projects = Projects,
        ErrorMessage = ErrorMessage,
        RemoteBranches = RemoteBranches,
        RemoteTags = RemoteTags,
    };
}
