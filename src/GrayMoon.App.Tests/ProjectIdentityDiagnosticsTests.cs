using System.Collections.Concurrent;
using System.Diagnostics;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GrayMoon.App.Tests;

/// <summary>Collision diagnostics are observable, bounded and silent on clean scans; large scans stay fast.</summary>
public sealed class ProjectIdentityDiagnosticsTests
{
    private sealed class CollectingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CollectingLogger(Entries);

        public void Dispose() { }

        private sealed class CollectingLogger(ConcurrentQueue<(LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }

    private static SyncProjectInfo Project(string name, string path, string? package = null, params SyncPackageReference[] refs) =>
        new(name, package is null ? ProjectType.Library : ProjectType.Package, path, "net10.0", package, refs);

    [Fact]
    public async Task Duplicate_names_log_one_bounded_warning_with_the_paths()
    {
        var logs = new CollectingLoggerProvider();
        await using var ctx = await GeneratedPackageTestContext.CreateAsync(logProvider: logs);
        await using var scope = ctx.CreateScope();

        var projects = Enumerable.Range(0, 12).Select(i => Project("Project", $"p{i}/Project.csproj")).ToList();
        await ctx.Repo(scope).MergeWorkspaceProjectsAsync(ctx.WorkspaceId, ctx.ProducerRepositoryId, projects);

        var warning = Assert.Single(logs.Entries, e => e.Message.Contains("Category=DuplicateProjectName"));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("DuplicateNames=1", warning.Message);
        Assert.Contains("Project x12", warning.Message);
        Assert.Contains("p0/Project.csproj", warning.Message);
        Assert.DoesNotContain("p11/Project.csproj", warning.Message);
    }

    [Fact]
    public async Task Clean_unchanged_scans_log_no_warnings()
    {
        var logs = new CollectingLoggerProvider();
        await using var ctx = await GeneratedPackageTestContext.CreateAsync(logProvider: logs);
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);

        IReadOnlyList<SyncProjectInfo> producer = [Project("Lib", "lib/Lib.csproj", "Lib.Pkg")];
        IReadOnlyList<SyncProjectInfo> consumer = [Project("App", "app/App.csproj", null, new SyncPackageReference("Lib.Pkg", "1.0.0"))];
        for (var i = 0; i < 3; i++)
        {
            await repo.ReconcileProjectSnapshotAsync(ctx.WorkspaceId, ctx.SpecialContextId,
                [ProjectScanOutcome.Scanned(ctx.ProducerRepositoryId, producer), ProjectScanOutcome.Scanned(ctx.ConsumerRepositoryId, consumer)],
                mergeEdges: true, persistDependencyLevel: true);
        }

        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Contains(logs.Entries, e => e.Message.StartsWith("Project reconciliation:") && e.Message.Contains("ElapsedMs="));
    }

    [Fact]
    public async Task Ambiguous_producers_log_a_categorised_warning_with_count_and_resolution()
    {
        var logs = new CollectingLoggerProvider();
        await using var ctx = await GeneratedPackageTestContext.CreateAsync(logProvider: logs);
        await using var scope = ctx.CreateScope();

        IReadOnlyList<SyncProjectInfo> producer = [Project("X", "x/X.csproj", "Dup.Pkg"), Project("Y", "y/Y.csproj", "Dup.Pkg")];
        IReadOnlyList<SyncProjectInfo> consumer = [Project("App", "app/App.csproj", null, new SyncPackageReference("Dup.Pkg", "1.0.0"))];
        await ctx.Repo(scope).ReconcileProjectSnapshotAsync(ctx.WorkspaceId, ctx.SpecialContextId,
            [ProjectScanOutcome.Scanned(ctx.ProducerRepositoryId, producer), ProjectScanOutcome.Scanned(ctx.ConsumerRepositoryId, consumer)],
            mergeEdges: true, persistDependencyLevel: true);

        var warning = Assert.Single(logs.Entries, e => e.Message.Contains("Category=AmbiguousPackageProducer"));
        Assert.Contains("PackageId=Dup.Pkg", warning.Message);
        Assert.Contains("ProducerCount=2", warning.Message);
        Assert.Contains("Resolution=NoEdge", warning.Message);
        Assert.Contains("x/X.csproj", warning.Message);
    }

    [Fact]
    public async Task A_failed_scan_is_logged_as_skipped()
    {
        var logs = new CollectingLoggerProvider();
        await using var ctx = await GeneratedPackageTestContext.CreateAsync(logProvider: logs);
        await using var scope = ctx.CreateScope();

        await ctx.Repo(scope).ReconcileProjectSnapshotAsync(ctx.WorkspaceId, ctx.SpecialContextId,
            [ProjectScanOutcome.Scanned(ctx.ProducerRepositoryId, [Project("Lib", "lib/Lib.csproj")]), ProjectScanOutcome.Failed(ctx.ConsumerRepositoryId)],
            mergeEdges: true, persistDependencyLevel: false);

        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("no authoritative scan"));
        Assert.Contains(logs.Entries, e => e.Message.Contains("Skipped=1"));
    }

    [Fact]

    [Trait("Category", "Long")]
    public async Task A_workspace_of_39_repositories_reconciles_and_rescans_with_work_proportional_to_what_changed()
    {
        const int repositoryCount = 39;
        const int projectsPerRepository = 25;

        var commands = new SqlCommandCounter();
        await using var ctx = await GeneratedPackageTestContext.CreateAsync(commands);
        await using var scope = ctx.CreateScope();
        var repo = ctx.Repo(scope);
        var db = ctx.Db(scope);

        var connectorId = (await db.Connectors.AsNoTracking().FirstAsync()).ConnectorId;
        var repoIds = new List<int>();
        for (var i = 0; i < repositoryCount; i++)
        {
            var r = new Repository { ConnectorId = connectorId, RepositoryName = $"perf-{i}", OrgName = "acme", Visibility = "Public", CloneUrl = $"https://example/perf-{i}.git" };
            db.Repositories.Add(r);
            await db.SaveChangesAsync();
            db.WorkspaceRepositories.Add(new WorkspaceRepositoryLink { WorkspaceId = ctx.WorkspaceId, RepositoryId = r.RepositoryId });
            repoIds.Add(r.RepositoryId);
        }
        await db.SaveChangesAsync();

        // Every repository publishes one package and its projects reference the previous repository's package.
        List<ProjectScanOutcome> Outcomes() => repoIds.Select((id, i) =>
        {
            var refs = i == 0 ? [] : new[] { new SyncPackageReference($"Perf.Pkg{i - 1}", "1.0.0") };
            var projects = Enumerable.Range(0, projectsPerRepository)
                .Select(p => p == 0
                    ? Project("Core", $"src/Core/Core.csproj", $"Perf.Pkg{i}", refs)
                    : Project("Project", $"src/m{p}/Project.csproj", null, refs))
                .ToList();
            return ProjectScanOutcome.Scanned(id, projects);
        }).ToList();

        commands.Reset();
        await repo.ReconcileProjectSnapshotAsync(ctx.WorkspaceId, ctx.SpecialContextId, Outcomes(), mergeEdges: true, persistDependencyLevel: true);
        var firstCommands = commands.Count;

        Assert.Equal(repositoryCount * projectsPerRepository, await db.WorkspaceProjects.CountAsync(p => repoIds.Contains(p.RepositoryId)));
        var edgesAfterFirst = await db.ProjectDependencies.CountAsync();
        Assert.True(edgesAfterFirst >= (repositoryCount - 1) * projectsPerRepository);

        commands.Reset();
        await repo.ReconcileProjectSnapshotAsync(ctx.WorkspaceId, ctx.SpecialContextId, Outcomes(), mergeEdges: true, persistDependencyLevel: true);
        var repeatCommands = commands.Count;

        Assert.Equal(edgesAfterFirst, await db.ProjectDependencies.CountAsync());

        // A partial scan (one repository) must not cost a full-workspace rewrite.
        commands.Reset();
        await repo.ReconcileProjectSnapshotAsync(ctx.WorkspaceId, ctx.SpecialContextId, [Outcomes()[5]], mergeEdges: true, persistDependencyLevel: true);
        var partialCommands = commands.Count;

        // Work, not elapsed time: the number of SQL commands is the same on any machine, and it is what grows when
        // reconciliation starts doing something per row it used to do once per batch. A repeat of an unchanged scan
        // writes nothing new, and a one-repository scan must not cost a full-workspace rewrite.
        var projectCount = repositoryCount * projectsPerRepository;
        Assert.True(firstCommands <= 3 * projectCount + 20 * repositoryCount, $"first reconcile ran {firstCommands} commands for {projectCount} projects");
        Assert.True(repeatCommands <= firstCommands, $"repeat reconcile ran {repeatCommands} commands, more than the first ({firstCommands})");
        Assert.True(partialCommands * 10 <= repeatCommands, $"partial reconcile ran {partialCommands} commands vs {repeatCommands} for a full repeat");
    }
}
