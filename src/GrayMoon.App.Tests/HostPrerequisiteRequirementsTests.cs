using GrayMoon.Abstractions.Workspaces;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Ui;
using GrayMoon.App.Services.Worker;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

public sealed class HostPrerequisiteRequirementsTests
{
    private static readonly WorkspaceCapabilities BasicNone =
        new(WorkspaceType.Basic, WorkspaceVersioningMode.None, WorkspaceCiProvider.None);

    private static readonly WorkspaceCapabilities BasicGitVersion =
        new(WorkspaceType.Basic, WorkspaceVersioningMode.GitVersion, WorkspaceCiProvider.None);

    private static readonly WorkspaceCapabilities DotNetNone =
        new(WorkspaceType.DotNetDependency, WorkspaceVersioningMode.None, WorkspaceCiProvider.None);

    private static readonly HostPrerequisiteVersions GitOnlyInstalled = new(null, "2.47.0", null);

    [Fact]
    public void Basic_without_versioning_requires_only_git()
    {
        var requirements = HostPrerequisiteRequirements.For(BasicNone);

        Assert.Equal(HostPrerequisiteRequirements.GitOnly, requirements);
        Assert.True(requirements.IsRequired(HostPrerequisiteIds.Git));
        Assert.False(requirements.IsRequired(HostPrerequisiteIds.DotnetSdk));
        Assert.False(requirements.IsRequired(HostPrerequisiteIds.GitVersion));
    }

    /// <summary>GitVersion only runs through the dotnet host, so it pulls in the .NET SDK.</summary>
    [Fact]
    public void Basic_with_GitVersion_requires_git_GitVersion_and_the_dotnet_sdk_it_runs_on()
    {
        var requirements = HostPrerequisiteRequirements.For(BasicGitVersion);

        Assert.True(requirements.IsRequired(HostPrerequisiteIds.Git));
        Assert.True(requirements.IsRequired(HostPrerequisiteIds.GitVersion));
        Assert.True(requirements.IsRequired(HostPrerequisiteIds.DotnetSdk));
    }

    [Fact]
    public void DotNet_dependency_with_default_versioning_requires_everything()
    {
        var requirements = HostPrerequisiteRequirements.For(WorkspaceCapabilities.Legacy);

        Assert.Equal(HostPrerequisiteRequirements.All, requirements);
    }

    [Fact]
    public void DotNet_dependency_without_versioning_requires_the_sdk_for_restore_but_not_GitVersion()
    {
        var requirements = HostPrerequisiteRequirements.For(DotNetNone);

        Assert.True(requirements.DotnetSdkRequired);
        Assert.False(requirements.GitVersionRequired);
    }

    [Fact]
    public void Requirements_are_the_union_over_every_workspace()
    {
        Assert.Equal(
            HostPrerequisiteRequirements.GitOnly,
            HostPrerequisiteRequirements.For([BasicNone, BasicNone]));
        Assert.Equal(
            HostPrerequisiteRequirements.All,
            HostPrerequisiteRequirements.For([BasicNone, WorkspaceCapabilities.Legacy]));
        Assert.Equal(
            HostPrerequisiteRequirements.All,
            HostPrerequisiteRequirements.For([DotNetNone, BasicGitVersion]));
        Assert.Equal(
            new HostPrerequisiteRequirements(DotnetSdkRequired: true, GitVersionRequired: false),
            HostPrerequisiteRequirements.For([BasicNone, DotNetNone]));
    }

    [Fact]
    public void No_workspaces_requires_only_git()
    {
        Assert.Equal(HostPrerequisiteRequirements.GitOnly, HostPrerequisiteRequirements.For([]));
    }

    [Fact]
    public void Missing_dotnet_and_GitVersion_is_not_attention_when_only_basic_workspaces_exist()
    {
        var requirements = HostPrerequisiteRequirements.For([BasicNone]);

        Assert.True(HostPrerequisiteState.AnyMissing(GitOnlyInstalled));
        Assert.False(HostPrerequisiteState.AnyRequiredMissing(GitOnlyInstalled, requirements));
        Assert.False(HomeNavNotification.WorkerRequiresAttention(
            WorkerConnectionState.Online,
            selfUpdateInProgress: false,
            hostPrerequisitesMissing: HostPrerequisiteState.AnyRequiredMissing(GitOnlyInstalled, requirements)));
        Assert.Equal(
            [HostPrerequisiteIds.DotnetSdk, HostPrerequisiteIds.GitVersion],
            HostPrerequisiteState.GetMissingOptionalIds(GitOnlyInstalled, requirements));
        Assert.Equal(
            "All required prerequisites are installed. .NET SDK and GitVersion are optional: no workspace uses them.",
            HostPrerequisiteState.Note(GitOnlyInstalled, requirements));
    }

    [Fact]
    public void The_same_host_needs_attention_once_a_dotnet_workspace_exists()
    {
        var requirements = HostPrerequisiteRequirements.For([BasicNone, WorkspaceCapabilities.Legacy]);

        Assert.True(HostPrerequisiteState.AnyRequiredMissing(GitOnlyInstalled, requirements));
        Assert.True(HomeNavNotification.WorkerRequiresAttention(
            WorkerConnectionState.Online,
            selfUpdateInProgress: false,
            hostPrerequisitesMissing: HostPrerequisiteState.AnyRequiredMissing(GitOnlyInstalled, requirements)));
        Assert.Empty(HostPrerequisiteState.GetMissingOptionalIds(GitOnlyInstalled, requirements));
        Assert.Equal("Install the missing prerequisites above.", HostPrerequisiteState.Note(GitOnlyInstalled, requirements));
    }

    [Fact]
    public void Missing_git_is_always_attention_even_with_no_workspaces()
    {
        var versions = new HostPrerequisiteVersions("10.0.100", null, "5.12.0");

        Assert.True(HostPrerequisiteState.AnyRequiredMissing(versions, HostPrerequisiteRequirements.GitOnly));
    }

    [Fact]
    public void A_single_optional_missing_tool_reads_in_the_singular()
    {
        var versions = new HostPrerequisiteVersions("10.0.100", "2.47.0", null);

        Assert.Equal(
            "All required prerequisites are installed. GitVersion is optional: no workspace uses it.",
            HostPrerequisiteState.Note(versions, new HostPrerequisiteRequirements(true, false)));
    }

    [Fact]
    public void Install_outcome_is_judged_against_the_requested_tools_only()
    {
        var versions = new HostPrerequisiteVersions(null, "2.47.0", null);

        Assert.Empty(HostPrerequisiteInstallService.StillMissing(versions, [HostPrerequisiteIds.Git]));
        Assert.Equal(
            [HostPrerequisiteIds.GitVersion],
            HostPrerequisiteInstallService.StillMissing(versions, [HostPrerequisiteIds.Git, HostPrerequisiteIds.GitVersion]));
        Assert.Equal(
            "Still missing: GitVersion.",
            HostPrerequisiteInstallService.BuildFailureDetail(
                versions,
                [HostPrerequisiteIds.GitVersion],
                failedPrerequisiteIds: null,
                message: null));
    }

    [Fact]
    public async Task Provider_returns_git_only_when_there_are_no_workspaces()
    {
        await using var context = await ProviderTestContext.CreateAsync();

        Assert.Equal(HostPrerequisiteRequirements.GitOnly, await context.Provider.GetAsync());
    }

    [Fact]
    public async Task Provider_unions_the_persisted_workspace_profiles()
    {
        await using var context = await ProviderTestContext.CreateAsync();
        await context.AddWorkspaceAsync("basic", WorkspaceType.Basic, WorkspaceVersioningMode.None);

        Assert.Equal(HostPrerequisiteRequirements.GitOnly, await context.Provider.GetAsync());

        await context.AddWorkspaceAsync("dotnet", WorkspaceType.DotNetDependency, WorkspaceVersioningMode.GitVersion);

        Assert.Equal(HostPrerequisiteRequirements.All, await context.Provider.GetAsync());
    }

    [Fact]
    public async Task Provider_falls_back_to_every_prerequisite_when_workspaces_cannot_be_read()
    {
        var context = await ProviderTestContext.CreateAsync();
        await context.DisposeAsync();

        Assert.Equal(HostPrerequisiteRequirements.All, await context.Provider.GetAsync());
    }

    private sealed class ProviderTestContext : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly AppDbContext _dbContext;

        public HostPrerequisiteRequirementsProvider Provider { get; }

        private ProviderTestContext(SqliteConnection connection, DbContextOptions<AppDbContext> options)
        {
            _connection = connection;
            _dbContext = new AppDbContext(options);
            var factory = new TestDbContextFactory(options);
            Provider = new HostPrerequisiteRequirementsProvider(
                factory,
                new WorkspaceCapabilitiesResolver(factory),
                NullLogger<HostPrerequisiteRequirementsProvider>.Instance);
        }

        public static async Task<ProviderTestContext> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            var context = new ProviderTestContext(connection, options);
            await context._dbContext.Database.EnsureCreatedAsync();
            return context;
        }

        public async Task AddWorkspaceAsync(string name, WorkspaceType type, WorkspaceVersioningMode versioningMode)
        {
            _dbContext.Workspaces.Add(new Workspace
            {
                Name = name,
                Type = type,
                VersioningMode = versioningMode,
                CiProvider = WorkspaceCiProvider.None
            });
            await _dbContext.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _dbContext.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
        {
            public AppDbContext CreateDbContext() => new(options);
        }
    }
}
