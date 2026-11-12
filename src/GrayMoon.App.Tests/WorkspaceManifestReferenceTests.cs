using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.Common.Manifest;
using Microsoft.EntityFrameworkCore;
using WorkspaceRepositoryReferenceRevisionChange = GrayMoon.Application.WorkspaceManifest.WorkspaceRepositoryReferenceRevisionChange;

namespace GrayMoon.App.Tests;

/// <summary>
/// Read-only state in the Workspace definition: what is written from the database, what is kept from the file, how a
/// revision is recorded and what counts as drift.
/// </summary>
public sealed class WorkspaceManifestReferenceTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string ApiUrl = "https://github.com/acme/Api.git";

    private static readonly WorkspaceManifestFile File = new();

    [Fact]
    public async Task Nothing_is_written_when_no_repository_is_read_only_and_the_default_is_off()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();

        var manifest = await fixture.CreateService().BuildFromDatabaseAsync(workspace.WorkspaceId);

        Assert.Null(manifest.Workspace.Reference);
        Assert.All(manifest.Repositories, repository => Assert.Null(repository.Reference));
        Assert.Contains("\"version\": 1", File.Serialize(manifest));
    }

    [Fact]
    public async Task One_read_only_repository_makes_every_entry_say_true_or_false()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        await SetReferenceAsync(fixture, workspace, "Api", true);

        var manifest = await fixture.CreateService().BuildFromDatabaseAsync(workspace.WorkspaceId);

        Assert.False(manifest.Workspace.Reference);
        Assert.True(manifest.Repositories.Single(repository => repository.Name == "Api").Reference);
        Assert.False(manifest.Repositories.Single(repository => repository.Name == "Web").Reference);
        Assert.Contains("\"version\": 2", File.Serialize(manifest));
    }

    [Fact]
    public async Task A_default_of_true_is_written_with_every_entry_explicit_and_changes_no_repository()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        await SetReferenceDefaultAsync(fixture, workspace, true);

        var manifest = await fixture.CreateService().BuildFromDatabaseAsync(workspace.WorkspaceId);

        Assert.True(manifest.Workspace.Reference);
        Assert.All(manifest.Repositories, repository => Assert.False(repository.Reference));
    }

    [Fact]
    public async Task Writing_the_definition_keeps_the_recorded_branch_and_commit_of_a_read_only_repository()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        await SetReferenceAsync(fixture, workspace, "Api", true);
        var service = fixture.CreateService();
        fixture.Bridge.RespondWithFile(File.Serialize(WithApi(
            await service.BuildFromDatabaseAsync(workspace.WorkspaceId), api => api with { Branch = "main", Commit = Sha })));

        var result = await service.WriteAuthoritativeManifestAsync(workspace.WorkspaceId);

        Assert.True(result.Success, result.Error);
        var api = Parse(WrittenContent(fixture)).Repositories.Single(repository => repository.Name == "Api");
        Assert.True(api.Reference);
        Assert.Equal("main", api.Branch);
        Assert.Equal(Sha, api.Commit);
    }

    [Fact]
    public async Task Writing_the_definition_drops_the_branch_and_commit_of_a_repository_that_is_writable_again()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        var service = fixture.CreateService();
        await SetReferenceAsync(fixture, workspace, "Api", true);
        var file = File.Serialize(WithApi(
            await service.BuildFromDatabaseAsync(workspace.WorkspaceId), api => api with { Branch = "main", Commit = Sha }));
        await SetReferenceAsync(fixture, workspace, "Api", false);
        fixture.Bridge.RespondWithFile(file);

        var result = await service.WriteAuthoritativeManifestAsync(workspace.WorkspaceId);

        Assert.True(result.Success, result.Error);
        var content = WrittenContent(fixture);
        Assert.Contains("\"version\": 1", content);
        Assert.DoesNotContain("branch", content);
        Assert.DoesNotContain("commit", content);
        Assert.DoesNotContain("reference", content);
    }

    [Fact]
    public async Task A_tag_pin_survives_turning_read_only_off()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        var service = fixture.CreateService();
        await SetReferenceAsync(fixture, workspace, "Api", true);
        var file = File.Serialize(WithApi(
            await service.BuildFromDatabaseAsync(workspace.WorkspaceId), api => api with { Tag = "v1.0.0", Commit = Sha }));
        await SetReferenceAsync(fixture, workspace, "Api", false);
        fixture.Bridge.RespondWithFile(file);

        var result = await service.WriteAuthoritativeManifestAsync(workspace.WorkspaceId);

        Assert.True(result.Success, result.Error);
        var api = Parse(WrittenContent(fixture)).Repositories.Single(repository => repository.Name == "Api");
        Assert.Null(api.Reference);
        Assert.Equal("v1.0.0", api.Tag);
        Assert.Equal(Sha, api.Commit);
    }

    [Fact]
    public async Task Recording_a_revision_writes_the_branch_and_commit_and_a_repeat_does_not_write_again()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        await SetReferenceAsync(fixture, workspace, "Api", true);
        var service = fixture.CreateService();
        fixture.Bridge.RespondWithFile(File.Serialize(await service.BuildFromDatabaseAsync(workspace.WorkspaceId)));
        var change = new WorkspaceRepositoryReferenceRevisionChange(ApiUrl, "main", Sha.ToUpperInvariant());

        var result = await service.SetRepositoryReferenceRevisionsAsync(workspace.WorkspaceId, [change]);

        Assert.True(result.Success, result.Error);
        var content = WrittenContent(fixture);
        var api = Parse(content).Repositories.Single(repository => repository.Name == "Api");
        Assert.Equal("main", api.Branch);
        Assert.Equal(Sha, api.Commit);
        Assert.Null(Parse(content).Repositories.Single(repository => repository.Name == "Web").Branch);

        fixture.Bridge.Sent.Clear();
        fixture.Bridge.RespondWithFile(content);
        var repeat = await service.SetRepositoryReferenceRevisionsAsync(workspace.WorkspaceId, [change]);

        Assert.True(repeat.Success, repeat.Error);
        Assert.DoesNotContain(fixture.Bridge.Sent, sent => sent.Command == "WriteRepositoryFile");
    }

    [Fact]
    public async Task Recording_a_revision_replaces_a_tag_pin_and_clearing_removes_the_branch()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        await SetReferenceAsync(fixture, workspace, "Api", true);
        var service = fixture.CreateService();
        fixture.Bridge.RespondWithFile(File.Serialize(WithApi(
            await service.BuildFromDatabaseAsync(workspace.WorkspaceId), api => api with { Tag = "v1.0.0", Commit = Sha })));

        var recorded = await service.SetRepositoryReferenceRevisionsAsync(
            workspace.WorkspaceId, [new WorkspaceRepositoryReferenceRevisionChange(ApiUrl, "main", Sha)]);

        Assert.True(recorded.Success, recorded.Error);
        var content = WrittenContent(fixture);
        var api = Parse(content).Repositories.Single(repository => repository.Name == "Api");
        Assert.Null(api.Tag);
        Assert.Equal("main", api.Branch);

        fixture.Bridge.Sent.Clear();
        fixture.Bridge.RespondWithFile(content);
        var cleared = await service.SetRepositoryReferenceRevisionsAsync(
            workspace.WorkspaceId, [new WorkspaceRepositoryReferenceRevisionChange(ApiUrl, null, null)]);

        Assert.True(cleared.Success, cleared.Error);
        api = Parse(WrittenContent(fixture)).Repositories.Single(repository => repository.Name == "Api");
        Assert.True(api.Reference);
        Assert.Null(api.Branch);
        Assert.Null(api.Commit);
    }

    [Fact]
    public async Task Recording_a_revision_for_a_writable_repository_fails_without_throwing_and_writes_nothing()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        await SetReferenceAsync(fixture, workspace, "Web", true);
        var service = fixture.CreateService();
        fixture.Bridge.RespondWithFile(File.Serialize(await service.BuildFromDatabaseAsync(workspace.WorkspaceId)));

        var result = await service.SetRepositoryReferenceRevisionsAsync(
            workspace.WorkspaceId, [new WorkspaceRepositoryReferenceRevisionChange(ApiUrl, "main", Sha)]);

        Assert.False(result.Success);
        Assert.Contains("not read-only", result.Error);
        Assert.DoesNotContain(fixture.Bridge.Sent, sent => sent.Command == "WriteRepositoryFile");
    }

    [Fact]
    public async Task Clearing_a_tag_pin_leaves_the_revision_of_a_read_only_repository_on_a_branch()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        await SetReferenceAsync(fixture, workspace, "Api", true);
        var service = fixture.CreateService();
        fixture.Bridge.RespondWithFile(File.Serialize(WithApi(
            await service.BuildFromDatabaseAsync(workspace.WorkspaceId), api => api with { Branch = "main", Commit = Sha })));

        var result = await service.SetRepositoryTagPinsAsync(
            workspace.WorkspaceId, [new GrayMoon.Application.WorkspaceManifest.WorkspaceRepositoryTagPinChange(ApiUrl, null, null)]);

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain(fixture.Bridge.Sent, sent => sent.Command == "WriteRepositoryFile");
    }

    [Fact]
    public async Task Drift_names_a_repository_whose_read_only_state_differs()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        var service = fixture.CreateService();
        await SetReferenceAsync(fixture, workspace, "Api", true);
        var file = File.Serialize(await service.BuildFromDatabaseAsync(workspace.WorkspaceId));
        await SetReferenceAsync(fixture, workspace, "Api", false);
        await SetReferenceAsync(fixture, workspace, "Web", true);
        fixture.Bridge.RespondWithFile(file);

        var drift = await service.DetectDriftAsync(workspace.WorkspaceId);

        Assert.True(drift.HasDrift);
        Assert.Equal(["Api", "Web"], drift.ChangedReference.ToArray());
        Assert.False(drift.ReferenceDefaultChanged);
        Assert.Empty(drift.AddedRepositories);
        Assert.Empty(drift.RemovedRepositories);
    }

    [Fact]
    public async Task Drift_reports_a_different_default_and_no_repository()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        var service = fixture.CreateService();
        var file = File.Serialize(await service.BuildFromDatabaseAsync(workspace.WorkspaceId));
        await SetReferenceDefaultAsync(fixture, workspace, true);
        fixture.Bridge.RespondWithFile(file);

        var drift = await service.DetectDriftAsync(workspace.WorkspaceId);

        Assert.True(drift.HasDrift);
        Assert.True(drift.ReferenceDefaultChanged);
        Assert.Empty(drift.ChangedReference);
    }

    [Fact]
    public async Task Drift_ignores_the_recorded_branch_and_commit()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        await SetReferenceAsync(fixture, workspace, "Api", true);
        var service = fixture.CreateService();
        fixture.Bridge.RespondWithFile(File.Serialize(WithApi(
            await service.BuildFromDatabaseAsync(workspace.WorkspaceId), api => api with { Branch = "main", Commit = Sha })));

        var drift = await service.DetectDriftAsync(workspace.WorkspaceId);

        Assert.False(drift.HasDrift);
        Assert.Empty(drift.ChangedReference);
    }

    [Fact]
    public async Task A_file_that_says_false_everywhere_equals_a_Workspace_with_no_read_only_state()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        var service = fixture.CreateService();
        var database = await service.BuildFromDatabaseAsync(workspace.WorkspaceId);
        fixture.Bridge.RespondWithFile(File.Serialize(database with
        {
            Workspace = database.Workspace with { Reference = false },
            Repositories = database.Repositories.Select(repository => repository with { Reference = false }).ToList(),
        }));

        Assert.False((await service.DetectDriftAsync(workspace.WorkspaceId)).HasDrift);
    }

    private static WorkspaceManifest WithApi(WorkspaceManifest manifest, Func<WorkspaceManifestRepository, WorkspaceManifestRepository> change) =>
        manifest with
        {
            Repositories = manifest.Repositories.Select(repository => repository.Name == "Api" ? change(repository) : repository).ToList(),
        };

    private static WorkspaceManifest Parse(string content)
    {
        Assert.True(File.TryParse(content, out var parsed, out var error), error);
        return parsed!;
    }

    private static string WrittenContent(ManifestTestFixture fixture) =>
        ManifestTestFixture.ToJson(Assert.Single(fixture.Bridge.Sent, sent => sent.Command == "WriteRepositoryFile").Args)
            .GetProperty("content").GetString()!;

    internal static async Task SetReferenceAsync(ManifestTestFixture fixture, Workspace workspace, string repositoryName, bool isReference)
    {
        await using var db = new AppDbContext(fixture.Options);
        var link = await db.WorkspaceRepositories.SingleAsync(l =>
            l.WorkspaceId == workspace.WorkspaceId && l.Repository!.RepositoryName == repositoryName);
        link.IsReference = isReference;
        await db.SaveChangesAsync();
    }

    internal static async Task SetReferenceDefaultAsync(ManifestTestFixture fixture, Workspace workspace, bool referenceDefault)
    {
        await using var db = new AppDbContext(fixture.Options);
        (await db.Workspaces.SingleAsync(w => w.WorkspaceId == workspace.WorkspaceId)).ReferenceDefault = referenceDefault;
        await db.SaveChangesAsync();
    }
}
