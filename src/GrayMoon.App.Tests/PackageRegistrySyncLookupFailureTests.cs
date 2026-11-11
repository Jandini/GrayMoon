using System.Net;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.Connectors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Tests;

/// <summary>
/// A registry lookup that threw used to count as "this registry does not have the package". With every other
/// registry answering no, the package's known registry match was cleared, and the two-lane push pre-check then
/// reported a package without a registry mapping.
/// </summary>
public sealed class PackageRegistrySyncLookupFailureTests
{
    private const string PackageId = "Acme.Widgets";

    [Fact]
    public async Task A_failed_lookup_leaves_the_stored_match_and_is_logged()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);
        var (downId, _, projectId) = await SeedAsync(ctx, db);
        var logger = new CapturingLogger<PackageRegistrySyncService>();

        await Service(ctx, scope, db, logger, downFails: true).SyncWorkspacePackageRegistriesAsync(ctx.WorkspaceId);

        Assert.Equal(downId, (await db.WorkspaceProjects.AsNoTracking().SingleAsync(p => p.ProjectId == projectId)).MatchedConnectorId);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(PackageId) && e.Message.Contains("nuget-down"));
    }

    [Fact]
    public async Task A_failed_lookup_for_selected_packages_writes_no_miss()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);
        var (_, _, projectId) = await SeedAsync(ctx, db, matched: false);
        var logger = new CapturingLogger<PackageRegistrySyncService>();

        await Service(ctx, scope, db, logger, downFails: true)
            .SyncRegistriesForPackageIdsAsync(ctx.WorkspaceId, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PackageId });

        Assert.Null((await db.WorkspaceProjects.AsNoTracking().SingleAsync(p => p.ProjectId == projectId)).MatchedConnectorId);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(PackageId));
    }

    [Fact]
    public async Task When_every_registry_answers_no_the_match_is_cleared()
    {
        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
        await using var scope = ctx.CreateScope();
        var db = ctx.Db(scope);
        var (_, _, projectId) = await SeedAsync(ctx, db);
        var logger = new CapturingLogger<PackageRegistrySyncService>();

        await Service(ctx, scope, db, logger, downFails: false).SyncWorkspacePackageRegistriesAsync(ctx.WorkspaceId);

        Assert.Null((await db.WorkspaceProjects.AsNoTracking().SingleAsync(p => p.ProjectId == projectId)).MatchedConnectorId);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    private static PackageRegistrySyncService Service(
        GeneratedPackageTestContext ctx, AsyncServiceScope scope, Data.AppDbContext db, ILogger<PackageRegistrySyncService> logger, bool downFails) =>
        new(
            new ConnectorRepository(db, NullLogger<ConnectorRepository>.Instance),
            ctx.Repo(scope),
            new NuGetService(new HttpClient(new TwoRegistryHandler(downFails)), NullLogger<NuGetService>.Instance),
            Options.Create(new WorkspaceOptions()),
            logger);

    private static async Task<(int DownConnectorId, int OkConnectorId, int ProjectId)> SeedAsync(
        GeneratedPackageTestContext ctx, Data.AppDbContext db, bool matched = true)
    {
        var down = new Connector { ConnectorName = "nuget-down", ConnectorType = ConnectorType.NuGet, ApiBaseUrl = "https://down.registry.test/v3/index.json", IsActive = true, IsHealthy = true };
        var ok = new Connector { ConnectorName = "nuget-ok", ConnectorType = ConnectorType.NuGet, ApiBaseUrl = "https://ok.registry.test/v3/index.json", IsActive = true, IsHealthy = true };
        db.Connectors.AddRange(down, ok);
        await db.SaveChangesAsync();

        var package = new WorkspaceProject
        {
            WorkspaceId = ctx.WorkspaceId,
            WorkspaceFeatureContextId = ctx.SpecialContextId,
            RepositoryId = ctx.ProducerRepositoryId,
            ProjectName = PackageId,
            ProjectType = ProjectType.Package,
            ProjectFilePath = "src/Widgets/Widgets.csproj",
            TargetFramework = "net10.0",
            PackageId = PackageId,
            MatchedConnectorId = matched ? down.ConnectorId : null
        };
        package.RefreshIdentityKey();
        db.WorkspaceProjects.Add(package);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (down.ConnectorId, ok.ConnectorId, package.ProjectId);
    }

    /// <summary>"ok" answers its index and 404 for every package; "down" does the same or fails every request.</summary>
    private sealed class TwoRegistryHandler(bool downFails) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            if (downFails && host.StartsWith("down.", StringComparison.Ordinal))
                throw new HttpRequestException("No such host is known.");

            if (request.RequestUri.AbsolutePath.EndsWith("/v3/index.json", StringComparison.Ordinal))
            {
                var index = $$"""{"version":"3.0.0","resources":[{"@id":"https://{{host}}/flat/","@type":"PackageBaseAddress/3.0.0"}]}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(index, System.Text.Encoding.UTF8, "application/json") });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}