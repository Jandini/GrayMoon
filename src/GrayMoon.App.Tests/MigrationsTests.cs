using GrayMoon.App.Data;
using GrayMoon.App.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests;

public class MigrationsTests
{
    [Fact]
    public async Task SeedDefaultWorkspaceRootPathAsync_inserts_default_when_no_row_exists()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var dbContext = new AppDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();

        await Migrations.SeedDefaultWorkspaceRootPathAsync(dbContext);

        var setting = await dbContext.Settings.SingleAsync(s => s.Key == AppSettingRepository.WorkspaceRootPathKey);
        Assert.Equal(@"C:\Workspace", setting.Value);
    }

    [Fact]
    public async Task SeedDefaultWorkspaceRootPathAsync_does_not_override_an_existing_row()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var dbContext = new AppDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();

        dbContext.Settings.Add(new Models.Setting { Key = AppSettingRepository.WorkspaceRootPathKey, Value = @"D:\Custom" });
        await dbContext.SaveChangesAsync();

        await Migrations.SeedDefaultWorkspaceRootPathAsync(dbContext);

        var setting = await dbContext.Settings.SingleAsync(s => s.Key == AppSettingRepository.WorkspaceRootPathKey);
        Assert.Equal(@"D:\Custom", setting.Value);
    }

    [Fact]
    public async Task SeedDefaultWorkspaceRootPathAsync_is_idempotent_when_run_twice()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using var dbContext = new AppDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();

        await Migrations.SeedDefaultWorkspaceRootPathAsync(dbContext);
        await Migrations.SeedDefaultWorkspaceRootPathAsync(dbContext);

        var count = await dbContext.Settings.CountAsync(s => s.Key == AppSettingRepository.WorkspaceRootPathKey);
        Assert.Equal(1, count);
    }
}
