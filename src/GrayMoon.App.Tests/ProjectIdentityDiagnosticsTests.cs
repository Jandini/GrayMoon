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
    public async Task A_workspace_of_39_repositories_and_thousands_of_projects_reconciles_and_rescans_quickly()
    {
        const int repositoryCount = 39;
        const int projectsPerRepository = 100;

        await using var ctx = await GeneratedPackageTestContext.CreateAsync();
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

        var first = Stopwatch.StartNew();
        await repo.ReconcileProjectSnapshotAsync(ctx.WorkspaceId, ctx.SpecialContextId, Outcomes(), mergeEdges: true, persistDependencyLevel: true);
        first.Stop();

        Assert.Equal(repositoryCount * projectsPerRepository, await db.WorkspaceProjects.CountAsync(p => repoIds.Contains(p.RepositoryId)));
        var edgesAfterFirst = await db.ProjectDependencies.CountAsync();
        Assert.True(edgesAfterFirst >= (repositoryCount - 1) * projectsPerRepository);

        var repeat = Stopwatch.StartNew();
        await repo.ReconcileProjectSnapshotAsync(ctx.WorkspaceId, ctx.SpecialContextId, Outcomes(), mergeEdges: true, persistDependencyLevel: true);
        repeat.Stop();

        Assert.Equal(edgesAfterFirst, await db.ProjectDependencies.CountAsync());

        // A partial scan (one repository) must not cost a full-workspace rewrite.
        var partial = Stopwatch.StartNew();
        await repo.ReconcileProjectSnapshotAsync(ctx.WorkspaceId, ctx.SpecialContextId, [Outcomes()[5]], mergeEdges: true, persistDependencyLevel: true);
        partial.Stop();

        Console.WriteLine($"PERF 39 repos x 100 projects: first={first.ElapsedMilliseconds}ms repeat={repeat.ElapsedMilliseconds}ms partial={partial.ElapsedMilliseconds}ms");
        Assert.True(first.Elapsed < TimeSpan.FromSeconds(60), $"first reconcile took {first.Elapsed}");
        Assert.True(repeat.Elapsed < TimeSpan.FromSeconds(60), $"repeat reconcile took {repeat.Elapsed}");
        Assert.True(partial.Elapsed < repeat.Elapsed + TimeSpan.FromSeconds(1), $"partial {partial.Elapsed} vs full repeat {repeat.Elapsed}");
    }
}
