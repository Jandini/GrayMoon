using System.Text.Json;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.Abstractions.Worker;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.Git;
using GrayMoon.App.Services.Worker;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application.Features;
using GrayMoon.Common.FileVersions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// Unit C: a Basic workspace never produces .NET dependency state - no levels, no dependency or
/// unmatched-dependency counts, no generated packages - while generic file versioning keeps working for it,
/// and a .NET Dependency workspace keeps today's behaviour. The seeded workspace starts on the model
/// defaults (Basic / None), and each test switches it where it needs the .NET triple.
/// </summary>
public sealed class WorkspaceDependencyGatingTests
{
    private const string ProducerName = "graymoon-api";
    private const string ConsumerName = "graymoon-web";

    [Fact]
    public async Task Basic_hook_sync_does_not_recompute_dependency_stats()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();

        await HandleHookAsync(ctx);

        var link = await ctx.ReadLinkAsync();
        Assert.Null(link.DependencyLevel);
        Assert.Null(await ReadContextDependencyLevelAsync(ctx));
    }

    [Fact]
    public async Task DotNet_hook_sync_still_recomputes_dependency_stats()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SetProfileAsync(ctx, WorkspaceType.DotNetDependency, WorkspaceVersioningMode.GitVersion);

        await HandleHookAsync(ctx);

        var link = await ctx.ReadLinkAsync();
        Assert.Equal(1, link.DependencyLevel);
        Assert.Equal(1, await ReadContextDependencyLevelAsync(ctx));
    }

    [Fact]
    public async Task Basic_recompute_runs_the_file_version_check_but_builds_no_levels_from_the_version_file()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var consumerId = await AddConsumerWithVersionFileAsync(ctx, "deploy/versions.env", $"API_BRANCH={{@{ProducerName}:branch}}");
        RespondCheckFileVersionsOutOfDate(ctx, $"@{ProducerName}:branch", "main", "feature/x");

        await RecomputeAsync(ctx);

        Assert.Contains(ctx.WorkerBridge.Calls, c => c.Command == "CheckFileVersions");
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var status = Assert.Single(await db.WorkspaceFileLineStatuses.AsNoTracking().ToListAsync());
        Assert.Equal(consumerId, status.RepositoryId);

        // The file token is a dependency edge in a .NET workspace; in a Basic one it must not become a level.
        var links = await db.WorkspaceRepositories.AsNoTracking().Where(l => l.WorkspaceId == ctx.WorkspaceId).ToListAsync();
        Assert.All(links, l =>
        {
            Assert.Null(l.DependencyLevel);
            Assert.Null(l.Dependencies);
            Assert.Null(l.UnmatchedDeps);
        });
    }

    [Fact]
    public async Task DotNet_recompute_still_orders_the_version_file_consumer_after_its_producer()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SetProfileAsync(ctx, WorkspaceType.DotNetDependency, WorkspaceVersioningMode.GitVersion);
        var consumerId = await AddConsumerWithVersionFileAsync(ctx, "deploy/versions.env", $"API_VERSION={{@{ProducerName}}}");
        RespondCheckFileVersionsOutOfDate(ctx, $"@{ProducerName}", "0.9.0", "1.0.0");

        await RecomputeAsync(ctx);

        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var links = await db.WorkspaceRepositories.AsNoTracking().Where(l => l.WorkspaceId == ctx.WorkspaceId).ToListAsync();
        Assert.Equal(1, links.Single(l => l.RepositoryId == ctx.RepositoryId).DependencyLevel);
        Assert.Equal(2, links.Single(l => l.RepositoryId == consumerId).DependencyLevel);
        Assert.Equal(1, links.Single(l => l.RepositoryId == consumerId).Dependencies);
    }

    [Fact]
    public async Task Basic_without_versioning_checks_branch_tokens_but_never_resolves_the_default_version_token()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await AddConsumerWithVersionFileAsync(
            ctx,
            "deploy/versions.env",
            $"API_VERSION={{@{ProducerName}}}\nAPI_BRANCH={{@{ProducerName}:branch}}");
        ctx.WorkerBridge.Respond("CheckFileVersions", new { files = Array.Empty<object>() });

        await RecomputeAsync(ctx);

        // The seeded link still carries GitVersion 1.0.0; with versioning off it is not applicable, so the
        // {@Repo} token is neither expected nor reported - only the branch token goes to the worker.
        var expected = ReadExpectedValues(ctx);
        Assert.Equal("feature/x", expected[$"@{ProducerName}:branch"]);
        Assert.False(expected.ContainsKey($"@{ProducerName}"));
    }

    [Fact]
    public async Task Basic_with_GitVersion_resolves_the_default_version_token_without_any_csproj()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SetProfileAsync(ctx, WorkspaceType.Basic, WorkspaceVersioningMode.GitVersion);
        await AddConsumerWithVersionFileAsync(ctx, "deploy/versions.env", $"API_VERSION={{@{ProducerName}}}");
        ctx.WorkerBridge.Respond("CheckFileVersions", new { files = Array.Empty<object>() });

        await RecomputeAsync(ctx);

        Assert.Equal("1.0.0", ReadExpectedValues(ctx)[$"@{ProducerName}"]);
        Assert.DoesNotContain(ctx.WorkerBridge.Calls, c => c.Command == WorkerHubMethods.ResolveGeneratedPackageReferences);
    }

    [Fact]
    public async Task Basic_csproj_version_file_never_becomes_a_generated_package()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SetProfileAsync(ctx, WorkspaceType.Basic, WorkspaceVersioningMode.GitVersion);
        await AddConsumerWithVersionFileAsync(ctx, "src/Web/Web.csproj", $"<PackageReference Include=\"Acme.Api\" Version=\"{{@{ProducerName}}}\" />");
        RespondGeneratedPackage(ctx);
        ctx.WorkerBridge.Respond("CheckFileVersions", new { files = Array.Empty<object>() });

        await RecomputeAsync(ctx);

        Assert.DoesNotContain(ctx.WorkerBridge.Calls, c => c.Command == WorkerHubMethods.ResolveGeneratedPackageReferences);
        Assert.Empty(await ReadAllProjectsAsync(ctx));
        await using var scope = ctx.CreateScope();
        var lines = await scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>()
            .GetPackageDependencyLinesByRepoAsync(ctx.WorkspaceId);
        Assert.Empty(lines);
    }

    [Fact]
    public async Task DotNet_csproj_version_file_still_becomes_a_generated_package()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        await SetProfileAsync(ctx, WorkspaceType.DotNetDependency, WorkspaceVersioningMode.GitVersion);
        await AddConsumerWithVersionFileAsync(ctx, "src/Web/Web.csproj", $"<PackageReference Include=\"Acme.Api\" Version=\"{{@{ProducerName}}}\" />");
        RespondGeneratedPackage(ctx);
        ctx.WorkerBridge.Respond("CheckFileVersions", new { files = Array.Empty<object>() });

        await RecomputeAsync(ctx);

        Assert.Contains(ctx.WorkerBridge.Calls, c => c.Command == WorkerHubMethods.ResolveGeneratedPackageReferences);
        Assert.Contains(await ReadAllProjectsAsync(ctx), p => p.IsGenerated && p.PackageId == "Acme.Api");
    }

    [Fact]
    public async Task Ok_badge_version_lines_are_not_applicable_without_versioning()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var consumerId = await AddConsumerWithVersionFileAsync(ctx, "deploy/versions.env", $"API_VERSION={{@{ProducerName}}}");
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [ProducerName] = "1.0.0" };
        var special = await ctx.GetSpecialContextIdAsync();

        await using (var scope = ctx.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<WorkspaceFileVersionService>();
            Assert.Empty(await service.GetAllFileVersionLinesForRepoAsync(ctx.WorkspaceId, special, consumerId, versions));
            Assert.Empty(await service.GetAllFileVersionLinesByRepoAsync(ctx.WorkspaceId, special, versions));
        }

        await SetProfileAsync(ctx, WorkspaceType.Basic, WorkspaceVersioningMode.GitVersion);
        await using (var scope = ctx.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<WorkspaceFileVersionService>();
            Assert.Single(await service.GetAllFileVersionLinesForRepoAsync(ctx.WorkspaceId, special, consumerId, versions));
            Assert.Single((await service.GetAllFileVersionLinesByRepoAsync(ctx.WorkspaceId, special, versions))[consumerId]);
        }
    }

    [Fact]
    public void Default_version_token_is_rejected_only_when_versioning_is_off()
    {
        var pattern = $"A={{@{ProducerName}}}\nB={{@{ProducerName}:branch}}\nC={{@{ProducerName}:commit}}";

        var rejected = WorkspaceFileVersionService.GetTokensRequiringRepositoryVersioning(pattern, usesRepositoryVersioning: false);
        var token = Assert.Single(rejected);
        Assert.Equal(FileVersionTokenKind.GitVersion, token.Kind);
        Assert.Equal($"@{ProducerName}", token.TokenKey);

        Assert.Empty(WorkspaceFileVersionService.GetTokensRequiringRepositoryVersioning(pattern, usesRepositoryVersioning: true));
        Assert.Empty(WorkspaceFileVersionService.GetTokensRequiringRepositoryVersioning(
            $"B={{@{ProducerName}:branch}}\nC={{@{ProducerName}:commit}}", usesRepositoryVersioning: false));
    }

    [Fact]
    public async Task Basic_project_refresh_asks_the_worker_for_nothing()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        var special = await ctx.GetSpecialContextIdAsync();

        await using var scope = ctx.CreateScope();
        var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
        await git.RefreshWorkspaceProjectsAsync(ctx.WorkspaceId, special);
        var refreshedSingle = await git.RefreshSingleRepositoryProjectsAsync(ctx.WorkspaceId, special, ctx.RepositoryId);

        Assert.False(refreshedSingle);
        Assert.DoesNotContain(ctx.WorkerBridge.Calls, c => c.Command == "RefreshRepositoryProjects");
        Assert.Null((await ctx.ReadLinkAsync()).DependencyLevel);
    }

    [Fact]
    public async Task Basic_sync_merges_no_dependency_edges_even_when_a_worker_reports_projects()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.WorkerBridge.Respond("SyncRepository", new
        {
            success = true,
            version = "-",
            branch = "main",
            defaultBranch = "main",
            localBranches = new[] { "main" },
            remoteBranches = new[] { "origin/main" },
            tags = Array.Empty<string>(),
            projects = new[]
            {
                new
                {
                    name = "Acme.Api",
                    projectType = 1,
                    projectPath = "src/Acme.Api/Acme.Api.csproj",
                    targetFramework = "net10.0",
                    packageId = (string?)null,
                    packageReferences = Array.Empty<object>(),
                },
            },
        });

        await using (var scope = ctx.CreateScope())
        {
            var git = scope.ServiceProvider.GetRequiredService<WorkspaceGitService>();
            await git.SyncAsync(ctx.WorkspaceId, await ctx.GetSpecialContextIdAsync());
        }

        var link = await ctx.ReadLinkAsync();
        Assert.Null(link.DependencyLevel);
        Assert.Null(link.UnmatchedDeps);
    }

    [Fact]
    public async Task Create_feature_sends_the_workspace_profile_on_every_worktree_request()
    {
        await using var ctx = await SyncStateTestContext.CreateAsync();
        ctx.WorkerBridge.Respond(WorkerHubMethods.GetHeadCommits, new
        {
            commits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ProducerName] = "abc123def456abc123def456abc123def456abc1",
            },
            branches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ProducerName] = "main",
            },
        });
        ctx.WorkerBridge.Respond(WorkerHubMethods.CreateGitWorktree, new { success = true, worktreePath = @"C:\wt" });

        await using (var scope = ctx.CreateScope())
        {
            var ops = scope.ServiceProvider.GetRequiredService<IWorkspaceFeatureOperations>();
            var result = await ops.CreateFeatureAsync(ctx.WorkspaceId, "feat-basic", WorkspaceFeatureBaseKindApplication.CurrentWorkspace);
            Assert.True(result.Success, result.Error);
        }

        var call = Assert.Single(ctx.WorkerBridge.Calls, c => c.Command == WorkerHubMethods.CreateGitWorktree);
        var capabilities = JsonSerializer.SerializeToElement(call.Args).GetProperty("capabilities");
        Assert.False(capabilities.GetProperty("calculateRepositoryVersion").GetBoolean());
        Assert.False(capabilities.GetProperty("discoverDotNetProjects").GetBoolean());
    }

    private static async Task HandleHookAsync(SyncStateTestContext ctx)
    {
        await using var scope = ctx.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<SyncCommandHandler>();
        await handler.HandleAsync(new RepositorySyncNotification
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = ctx.RepositoryId,
            Version = "-",
            Branch = "main",
            State = new RepositoryStateSnapshot { BranchName = "main", IdentityProbed = true },
        });
    }

    private static async Task RecomputeAsync(SyncStateTestContext ctx)
    {
        var special = await ctx.GetSpecialContextIdAsync();
        await using var scope = ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkspaceStateRecomputeScope>().RecomputeAsync(ctx.WorkspaceId, special);
    }

    private static async Task<int?> ReadContextDependencyLevelAsync(SyncStateTestContext ctx)
    {
        var special = await ctx.GetSpecialContextIdAsync();
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var state = await db.WorkspaceRepositoryContextStates.AsNoTracking()
            .FirstOrDefaultAsync(s => s.WorkspaceFeatureContextId == special.Value && s.WorkspaceRepositoryId == ctx.WorkspaceRepositoryId);
        return state?.DependencyLevel;
    }

    private static async Task<List<WorkspaceProject>> ReadAllProjectsAsync(SyncStateTestContext ctx)
    {
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.WorkspaceProjects.AsNoTracking().Where(p => p.WorkspaceId == ctx.WorkspaceId).ToListAsync();
    }

    /// <summary>Adds a second repository that pins a version file referencing the seeded one, and returns its id.</summary>
    private static async Task<int> AddConsumerWithVersionFileAsync(SyncStateTestContext ctx, string filePath, string pattern)
    {
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
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

        db.WorkspaceRepositories.Add(new WorkspaceRepositoryLink
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = consumer.RepositoryId,
            BranchName = "main",
            DefaultBranchName = "main",
            SyncStatus = RepoSyncStatus.InSync,
        });
        var file = new WorkspaceFile
        {
            WorkspaceId = ctx.WorkspaceId,
            RepositoryId = consumer.RepositoryId,
            FileName = Path.GetFileName(filePath),
            FilePath = filePath,
        };
        db.WorkspaceFiles.Add(file);
        await db.SaveChangesAsync();

        db.WorkspaceFileVersionConfigs.Add(new WorkspaceFileVersionConfig { FileId = file.FileId, VersionPattern = pattern });
        await db.SaveChangesAsync();
        return consumer.RepositoryId;
    }

    private static void RespondCheckFileVersionsOutOfDate(SyncStateTestContext ctx, string tokenName, string current, string expected)
        => ctx.WorkerBridge.Respond("CheckFileVersions", _ => new WorkerCommandResponse(true, new
        {
            files = new[]
            {
                new
                {
                    repositoryName = ConsumerName,
                    filePath = "deploy/versions.env",
                    fileName = "versions.env",
                    fileMissing = false,
                    outOfDateLines = new[] { new { tokenName, currentValue = current, expectedValue = expected } },
                },
            },
        }, null));

    private static void RespondGeneratedPackage(SyncStateTestContext ctx)
        => ctx.WorkerBridge.Respond(WorkerHubMethods.ResolveGeneratedPackageReferences, new
        {
            files = new[]
            {
                new
                {
                    repositoryName = ConsumerName,
                    filePath = "src/Web/Web.csproj",
                    packages = new[] { new { repoNameToken = ProducerName, packageName = "Acme.Api", version = "1.0.0" } },
                },
            },
        });

    private static Dictionary<string, string> ReadExpectedValues(SyncStateTestContext ctx)
    {
        var call = Assert.Single(ctx.WorkerBridge.Calls, c => c.Command == "CheckFileVersions");
        var file = Assert.Single(JsonSerializer.SerializeToElement(call.Args).GetProperty("files").EnumerateArray());
        return file.GetProperty("expectedValues").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
    }

    private static async Task SetProfileAsync(SyncStateTestContext ctx, WorkspaceType type, WorkspaceVersioningMode versioningMode)
    {
        var factory = ctx.Resolve<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var workspace = await db.Workspaces.FirstAsync(w => w.WorkspaceId == ctx.WorkspaceId);
        workspace.Type = type;
        workspace.VersioningMode = versioningMode;
        await db.SaveChangesAsync();
    }
}
