using GrayMoon.App.Data;
using GrayMoon.App.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

public sealed class GitHubApiUsageRecorderTests
{
    /// <summary>Hands out fresh <see cref="AppDbContext"/> instances against one shared open in-memory SQLite connection,
    /// so data survives across DbContext instances the way it would across real app restarts against a real file.</summary>
    private sealed class SharedConnectionDbContextFactory(SqliteConnection connection) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    [Fact]
    public async Task FlushAsync_AggregatesMultipleRecordsForSameConnectorCategoryHour_IntoOneRow()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new SharedConnectionDbContextFactory(connection);
        await using (var setup = factory.CreateDbContext())
            await setup.Database.EnsureCreatedAsync();

        var recorder = new GitHubApiUsageRecorder(factory, NullLogger<GitHubApiUsageRecorder>.Instance);

        recorder.Record(1, "repos/acme/widgets/actions/runs/1/jobs", isNotModified: false, isError: false);
        recorder.Record(1, "repos/acme/widgets/actions/runs/1/jobs", isNotModified: true, isError: false);
        recorder.Record(1, "repos/acme/widgets/actions/runs/2/jobs", isNotModified: false, isError: true);

        await recorder.FlushAsync();

        await using var dbContext = factory.CreateDbContext();
        var row = await dbContext.GitHubApiUsageHourly.SingleAsync(u => u.ConnectorId == 1 && u.Category == GitHubApiUsageCategoryMapper.Actions);

        Assert.Equal(3, row.RequestCount);
        Assert.Equal(1, row.NotModifiedCount);
        Assert.Equal(1, row.ErrorCount);
    }

    [Fact]
    public async Task FlushAsync_DifferentCategories_ProduceSeparateRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new SharedConnectionDbContextFactory(connection);
        await using (var setup = factory.CreateDbContext())
            await setup.Database.EnsureCreatedAsync();

        var recorder = new GitHubApiUsageRecorder(factory, NullLogger<GitHubApiUsageRecorder>.Instance);
        recorder.Record(1, "repos/acme/widgets/actions/runs/1/jobs", isNotModified: false, isError: false);
        recorder.Record(1, "repos/acme/widgets/pulls?state=open", isNotModified: false, isError: false);

        await recorder.FlushAsync();

        await using var dbContext = factory.CreateDbContext();
        var rows = await dbContext.GitHubApiUsageHourly.ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Category == GitHubApiUsageCategoryMapper.Actions && r.RequestCount == 1);
        Assert.Contains(rows, r => r.Category == GitHubApiUsageCategoryMapper.PullRequests && r.RequestCount == 1);
    }

    [Fact]
    public async Task FlushAsync_CalledAgainAfterNewRecordsInSameHour_AccumulatesOnExistingRowRatherThanDuplicating()
    {
        // Simulates a restart: a brand-new recorder instance (fresh in-memory counters) flushing into a
        // database that already has a row for this connector/category/hour from before the "restart".
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new SharedConnectionDbContextFactory(connection);
        await using (var setup = factory.CreateDbContext())
            await setup.Database.EnsureCreatedAsync();

        var firstRecorder = new GitHubApiUsageRecorder(factory, NullLogger<GitHubApiUsageRecorder>.Instance);
        firstRecorder.Record(1, "repos/acme/widgets/actions/runs/1/jobs", isNotModified: false, isError: false);
        await firstRecorder.FlushAsync();

        // "Restart": a new recorder instance with empty in-memory counters, pointed at the same persisted DB.
        var secondRecorder = new GitHubApiUsageRecorder(factory, NullLogger<GitHubApiUsageRecorder>.Instance);
        secondRecorder.Record(1, "repos/acme/widgets/actions/runs/1/jobs", isNotModified: false, isError: false);
        secondRecorder.Record(1, "repos/acme/widgets/actions/runs/1/jobs", isNotModified: false, isError: false);
        await secondRecorder.FlushAsync();

        await using var dbContext = factory.CreateDbContext();
        var rows = await dbContext.GitHubApiUsageHourly.ToListAsync();

        Assert.Single(rows);
        Assert.Equal(3, rows[0].RequestCount);
    }

    [Fact]
    public async Task FlushAsync_WithNoRecordedCalls_DoesNotWriteAnyRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new SharedConnectionDbContextFactory(connection);
        await using (var setup = factory.CreateDbContext())
            await setup.Database.EnsureCreatedAsync();

        var recorder = new GitHubApiUsageRecorder(factory, NullLogger<GitHubApiUsageRecorder>.Instance);
        await recorder.FlushAsync();

        await using var dbContext = factory.CreateDbContext();
        Assert.Empty(await dbContext.GitHubApiUsageHourly.ToListAsync());
    }
}
