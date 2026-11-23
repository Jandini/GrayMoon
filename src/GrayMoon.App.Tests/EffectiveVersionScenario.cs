using System.Collections.Concurrent;
using System.Net;
using System.Text;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Versions;
using GrayMoon.Application.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GrayMoon.App.Tests;

/// <summary>
/// Effective versions test bed: a .NET Workspace whose producer (<c>graymoon-api</c>, package <see cref="PackageId"/>) and
/// consumer (<c>graymoon-web</c>) sit in the special Workspace on <c>feature/x</c>. The producer's branch was created by
/// GrayMoon at <see cref="BaseSha"/> from <c>release/2.4</c> and has not moved; its calculated GitVersion is
/// <see cref="CalculatedVersion"/>, the parent's version at that commit is <see cref="ParentVersion"/>. Nothing is pushed
/// yet (no upstream). The registry is a fake (<see cref="FakeRegistry"/>); the Worker answers the branch point command.
/// </summary>
internal sealed class EffectiveVersionScenario : IAsyncDisposable
{
    public const string PackageId = "Acme.Api";
    public const string SecondPackageId = "Acme.Api.Client";
    public const string ParentVersion = "2.4.0-beta.2";
    public const string CalculatedVersion = "2.3.1-abc.3";
    public const string ConsumerCalculatedVersion = "2.3.1-abc.7";
    public const string BaseSha = "1111111111111111111111111111111111111111";
    public const string ConsumerSha = "2222222222222222222222222222222222222222";
    public const string MovedSha = "3333333333333333333333333333333333333333";
    public const string NuGetIndexUrl = "https://nuget.test/v3/index.json";
    public const string ConsumerName = "graymoon-web";
    public const string ProducerName = "graymoon-api";

    private EffectiveVersionScenario(SyncStateTestContext ctx, FakeRegistry registry, ManualClock clock)
    {
        Ctx = ctx;
        Registry = registry;
        Clock = clock;
    }

    public SyncStateTestContext Ctx { get; }
    public FakeRegistry Registry { get; }
    public ManualClock Clock { get; }
    public WorkspaceFeatureContextId Special { get; private set; }
    public int ProducerId => Ctx.RepositoryId;
    public int ProducerLinkId => Ctx.WorkspaceRepositoryId;
    public int ConsumerId { get; private set; }
    public int ConsumerLinkId { get; private set; }
    public int ConnectorId { get; private set; }
    public int ProducerProjectId { get; private set; }
    public int ConsumerProjectId { get; private set; }

    /// <param name="secondPackage">The producer has a second packable project the consumer also references.</param>
    /// <param name="consumerReferenceVersion">The version the consumer's csproj references.</param>
    /// <param name="mapConnector">Whether the producer's packages are already mapped to the NuGet connector.</param>
    public static async Task<EffectiveVersionScenario> CreateAsync(
        bool secondPackage = false,
        string consumerReferenceVersion = ParentVersion,
        bool mapConnector = true,
        bool dotNet = true,
        Action<IServiceCollection>? configureServices = null)
    {
        var registry = new FakeRegistry();
        var clock = new ManualClock();
        var ctx = await SyncStateTestContext.CreateAsync(configureServices: services =>
        {
            services.AddSingleton(new HttpClient(registry));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            configureServices?.Invoke(services);
        });
        var scenario = new EffectiveVersionScenario(ctx, registry, clock);
        if (dotNet)
            await ctx.UseDotNetDependencyProfileAsync();
        await scenario.SeedAsync(secondPackage, consumerReferenceVersion, mapConnector);
        scenario.RespondBranchPoint(success: true, version: ParentVersion, onRemoteParent: true);
        return scenario;
    }

    public ValueTask DisposeAsync() => Ctx.DisposeAsync();

    /// <summary>The Worker's answer to <c>GetGitVersionAtBranchPoint</c>.</summary>
    public void RespondBranchPoint(bool success, string? version, bool? onRemoteParent, string? error = null)
        => Ctx.WorkerBridge.Respond(
            GrayMoon.Abstractions.Worker.WorkerHubMethods.GetGitVersionAtBranchPoint,
            new { success, version, baselineOnRemoteParent = onRemoteParent, errorMessage = error });

    public int BranchPointCalls
        => Ctx.WorkerBridge.Calls.Count(c => c.Command == GrayMoon.Abstractions.Worker.WorkerHubMethods.GetGitVersionAtBranchPoint);

    public async Task<EffectiveVersionResolution> ResolveAsync(WorkspaceFeatureContextId? contextId = null, bool bypassCache = true, CancellationToken cancellationToken = default)
    {
        await using var scope = Ctx.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<EffectiveVersionResolver>()
            .ResolveAsync(Ctx.WorkspaceId, contextId ?? Special, repositoryIds: null, bypassCache, cancellationToken);
    }

    public async Task<WorkspaceRepositoryContextState> ReadStateAsync(int linkId, WorkspaceFeatureContextId? contextId = null)
    {
        await using var db = await Ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var cid = (contextId ?? Special).Value;
        return await db.WorkspaceRepositoryContextStates.AsNoTracking()
            .SingleAsync(s => s.WorkspaceFeatureContextId == cid && s.WorkspaceRepositoryId == linkId);
    }

    public async Task MutateStateAsync(int linkId, Action<WorkspaceRepositoryContextState> mutate, WorkspaceFeatureContextId? contextId = null)
    {
        await using var db = await Ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var cid = (contextId ?? Special).Value;
        var state = await db.WorkspaceRepositoryContextStates.SingleAsync(s => s.WorkspaceFeatureContextId == cid && s.WorkspaceRepositoryId == linkId);
        mutate(state);
        await db.SaveChangesAsync();
    }

    public async Task WithDbAsync(Func<AppDbContext, Task> action)
    {
        await using var db = await Ctx.Resolve<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        await action(db);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Adds a Feature whose worktrees were created at <see cref="BaseSha"/> (producer) and <see cref="ConsumerSha"/>
    /// (consumer) from <c>release/2.4</c>, with state rows on those commits and no upstream. Returns its context.
    /// </summary>
    public async Task<WorkspaceFeatureContextId> AddFeatureAsync(string name = "feat-a", bool withStateRows = true)
    {
        var contextId = 0;
        await WithDbAsync(async db =>
        {
            var feature = new WorkspaceFeature
            {
                WorkspaceId = Ctx.WorkspaceId,
                Name = name,
                LifecycleState = WorkspaceFeatureLifecycleState.Ready,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.WorkspaceFeatures.Add(feature);
            await db.SaveChangesAsync();
            var context = new WorkspaceFeatureContext
            {
                WorkspaceId = Ctx.WorkspaceId,
                Kind = WorkspaceFeatureContextKind.Feature,
                WorkspaceFeatureId = feature.WorkspaceFeatureId,
                CreatedAt = DateTime.UtcNow,
            };
            db.WorkspaceFeatureContexts.Add(context);
            await db.SaveChangesAsync();
            contextId = context.WorkspaceFeatureContextId;

            foreach (var (linkId, sha, version) in new[] { (ProducerLinkId, BaseSha, CalculatedVersion), (ConsumerLinkId, ConsumerSha, ConsumerCalculatedVersion) })
            {
                db.WorkspaceFeatureRepositories.Add(new WorkspaceFeatureRepository
                {
                    WorkspaceFeatureContextId = contextId,
                    WorkspaceRepositoryId = linkId,
                    WorktreePath = $@"C:\features\{name}\{linkId}",
                    BaseCommitSha = sha,
                    ParentBranchName = "release/2.4",
                    CreatedAt = DateTime.UtcNow,
                    State = WorkspaceFeatureRepositoryState.Ready,
                });
                if (withStateRows)
                {
                    db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
                    {
                        WorkspaceFeatureContextId = contextId,
                        WorkspaceRepositoryId = linkId,
                        BranchName = $"feature/{name}",
                        HeadCommit = sha,
                        GitVersion = version,
                        OutgoingCommits = 0,
                        BranchHasUpstream = false,
                        DependencyLevel = linkId == ProducerLinkId ? 1 : 2,
                        SyncStatus = RepoSyncStatus.InSync,
                    });
                }
            }

            // The Feature's own project rows (the generated ones are shared).
            var producerProject = await db.WorkspaceProjects.AsNoTracking().SingleAsync(p => p.ProjectId == ProducerProjectId);
            var consumerProject = await db.WorkspaceProjects.AsNoTracking().SingleAsync(p => p.ProjectId == ConsumerProjectId);
            var featureProducer = Copy(producerProject, contextId);
            var featureConsumer = Copy(consumerProject, contextId);
            db.WorkspaceProjects.AddRange(featureProducer, featureConsumer);
            await db.SaveChangesAsync();
            db.ProjectDependencies.Add(new ProjectDependency
            {
                DependentProjectId = featureConsumer.ProjectId,
                ReferencedProjectId = featureProducer.ProjectId,
                Version = ParentVersion,
            });
        });

        return new WorkspaceFeatureContextId(contextId);
    }

    private static WorkspaceProject Copy(WorkspaceProject project, int contextId) => new()
    {
        WorkspaceId = project.WorkspaceId,
        WorkspaceFeatureContextId = contextId,
        RepositoryId = project.RepositoryId,
        ProjectName = project.ProjectName,
        PackageId = project.PackageId,
        ProjectType = project.ProjectType,
        ProjectFilePath = project.ProjectFilePath,
        TargetFramework = project.TargetFramework,
        MatchedConnectorId = project.MatchedConnectorId,
        ProjectIdentityKey = project.ProjectIdentityKey is null ? null : project.ProjectIdentityKey + "|" + contextId,
    };

    private async Task SeedAsync(bool secondPackage, string consumerReferenceVersion, bool mapConnector)
    {
        Special = await Ctx.GetSpecialContextIdAsync();
        await WithDbAsync(async db =>
        {
            var producerLink = await db.WorkspaceRepositories.Include(l => l.Repository).SingleAsync(l => l.WorkspaceRepositoryId == ProducerLinkId);
            producerLink.GitVersion = CalculatedVersion;
            producerLink.OutgoingCommits = 0;
            producerLink.BranchHasUpstream = false;
            producerLink.DependencyLevel = 1;

            var connector = new Connector
            {
                ConnectorName = "nuget-test",
                ConnectorType = ConnectorType.NuGet,
                ApiBaseUrl = NuGetIndexUrl,
                IsActive = true,
                IsHealthy = true,
            };
            db.Connectors.Add(connector);
            var consumer = new Repository
            {
                ConnectorId = producerLink.Repository!.ConnectorId,
                RepositoryName = ConsumerName,
                OrgName = "acme",
                Visibility = "Public",
                CloneUrl = $"https://github.com/acme/{ConsumerName}.git",
            };
            db.Repositories.Add(consumer);
            await db.SaveChangesAsync();
            ConnectorId = connector.ConnectorId;
            ConsumerId = consumer.RepositoryId;

            var consumerLink = new WorkspaceRepositoryLink
            {
                WorkspaceId = Ctx.WorkspaceId,
                RepositoryId = consumer.RepositoryId,
                GitVersion = ConsumerCalculatedVersion,
                BranchName = "feature/x",
                DefaultBranchName = "main",
                OutgoingCommits = 0,
                BranchHasUpstream = false,
                DependencyLevel = 2,
                SyncStatus = RepoSyncStatus.InSync,
            };
            db.WorkspaceRepositories.Add(consumerLink);
            await db.SaveChangesAsync();
            ConsumerLinkId = consumerLink.WorkspaceRepositoryId;

            // State rows of the special Workspace: both branches are new, on their branch points, never pushed.
            db.WorkspaceRepositoryContextStates.RemoveRange(db.WorkspaceRepositoryContextStates.Where(s => s.WorkspaceFeatureContextId == Special.Value));
            await db.SaveChangesAsync();
            foreach (var (linkId, sha, version, level) in new[] { (ProducerLinkId, BaseSha, CalculatedVersion, 1), (ConsumerLinkId, ConsumerSha, ConsumerCalculatedVersion, 2) })
            {
                db.WorkspaceRepositoryContextStates.Add(new WorkspaceRepositoryContextState
                {
                    WorkspaceFeatureContextId = Special.Value,
                    WorkspaceRepositoryId = linkId,
                    BranchName = "feature/x",
                    HeadCommit = sha,
                    GitVersion = version,
                    OutgoingCommits = 0,
                    BranchHasUpstream = false,
                    DependencyLevel = level,
                    SyncStatus = RepoSyncStatus.InSync,
                });
                db.WorkspaceRepositoryBranchBaselines.Add(new WorkspaceRepositoryBranchBaseline
                {
                    WorkspaceFeatureContextId = Special.Value,
                    WorkspaceRepositoryId = linkId,
                    BranchName = "feature/x",
                    ParentBranchName = "release/2.4",
                    BaseCommitSha = sha,
                    ResolvedStartRef = "origin/release/2.4",
                    CreatedAtUtc = DateTime.UtcNow,
                });
            }

            var producerProject = Project(ProducerId, "Acme.Api", PackageId, "src/Api/Acme.Api.csproj", mapConnector ? ConnectorId : null);
            var consumerProject = Project(ConsumerId, "Acme.Web", null, "src/Web/Acme.Web.csproj", null);
            db.WorkspaceProjects.AddRange(producerProject, consumerProject);
            WorkspaceProject? secondProject = null;
            if (secondPackage)
            {
                secondProject = Project(ProducerId, "Acme.Api.Client", SecondPackageId, "src/Client/Acme.Api.Client.csproj", mapConnector ? ConnectorId : null);
                db.WorkspaceProjects.Add(secondProject);
            }

            await db.SaveChangesAsync();
            ProducerProjectId = producerProject.ProjectId;
            ConsumerProjectId = consumerProject.ProjectId;
            db.ProjectDependencies.Add(new ProjectDependency { DependentProjectId = consumerProject.ProjectId, ReferencedProjectId = producerProject.ProjectId, Version = consumerReferenceVersion });
            if (secondProject != null)
                db.ProjectDependencies.Add(new ProjectDependency { DependentProjectId = consumerProject.ProjectId, ReferencedProjectId = secondProject.ProjectId, Version = consumerReferenceVersion });
        });
    }

    private WorkspaceProject Project(int repositoryId, string name, string? packageId, string path, int? connectorId) => new()
    {
        WorkspaceId = Ctx.WorkspaceId,
        WorkspaceFeatureContextId = Special.Value,
        RepositoryId = repositoryId,
        ProjectName = name,
        PackageId = packageId,
        ProjectType = ProjectType.Library,
        ProjectFilePath = path,
        TargetFramework = "net10.0",
        MatchedConnectorId = connectorId,
    };

    /// <summary>
    /// A NuGet v3 registry. Each package answers as configured: the versions it lists, an HTTP status, a timeout, or a
    /// malformed body. A package without a configuration lists <see cref="ParentVersion"/>.
    /// </summary>
    internal sealed class FakeRegistry : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, Func<HttpResponseMessage>> _byPackage = new(StringComparer.OrdinalIgnoreCase);
        private int _lookups;

        public int Lookups => Volatile.Read(ref _lookups);

        public void Lists(string packageId, params string[] versions)
            => _byPackage[packageId] = () => Json("{\"versions\":[" + string.Join(",", versions.Select(v => "\"" + v + "\"")) + "]}");

        public void Answers(string packageId, HttpStatusCode status)
            => _byPackage[packageId] = () => new HttpResponseMessage(status);

        public void Malformed(string packageId)
            => _byPackage[packageId] = () => Json("{\"items\":[]}");

        public void TimesOut(string packageId)
            => _byPackage[packageId] = () => throw new TaskCanceledException("The request timed out.");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url.Equals(NuGetIndexUrl, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Json("""{"resources":[{"@type":"PackageBaseAddress/3.0.0","@id":"https://nuget.test/flat/"}]}"""));

            const string flat = "https://nuget.test/flat/";
            if (url.StartsWith(flat, StringComparison.OrdinalIgnoreCase))
            {
                var packageId = url[flat.Length..].Split('/')[0];
                if (!url.EndsWith("/index.json", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                Interlocked.Increment(ref _lookups);
                return Task.FromResult(_byPackage.TryGetValue(packageId, out var answer)
                    ? answer()
                    : Json("{\"versions\":[\"" + ParentVersion + "\"]}"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
