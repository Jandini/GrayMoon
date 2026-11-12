using System.Text.Json;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.Policy;
using GrayMoon.Common.Manifest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GrayMoon.App.Tests.Policy.Characterization.PolicyHarness;

namespace GrayMoon.App.Tests;

/// <summary>
/// Restore of a Workspace definition that holds read-only state, and the check on repositories imported from folders
/// that already exist.
/// </summary>
public sealed class ReferenceRestoreTests
{
    private const string Sha = "3f2a9c0d5e7b1a4c6d8e0f1a2b3c4d5e6f7a8b9c";

    private static string Definition(bool? referenceDefault, params WorkspaceManifestRepository[] repositories) =>
        new WorkspaceManifestFile().Serialize(new WorkspaceManifest(
            1,
            new WorkspaceManifestWorkspace(new WorkspaceManifestProfile("basic", "none", "none"), referenceDefault),
            [new WorkspaceManifestConnector("github", "https://github.com")],
            repositories));

    private static WorkspaceManifestRepository Entry(string name, bool? reference = null, string? branch = null, string? commit = null) =>
        new(name, $"https://github.com/acme/{name}.git", "https://github.com", null, commit, reference, branch);

    private static async Task<(OperationsFixture Fixture, Repository Root, Repository Billing, Repository Platform)> SeedAsync()
    {
        var fixture = await OperationsFixture.CreateAsync();
        var root = await fixture.SeedRootAsync();
        var github = await fixture.GetGitHubConnectorAsync();
        var billing = await fixture.Seed.AddRepositoryAsync(github, "Billing", "https://github.com/acme/Billing.git");
        var platform = await fixture.Seed.AddRepositoryAsync(github, "Platform", "https://github.com/acme/Platform.git");
        return (fixture, root, billing, platform);
    }

    [Fact]
    public async Task The_mixed_sample_restores_each_repository_with_its_own_state_and_leaves_the_file_alone()
    {
        var (fixture, root, billing, platform) = await SeedAsync();
        await using var _ = fixture;
        fixture.UseDefinition(Definition(
            true,
            Entry("Billing", reference: true, branch: "main", commit: Sha),
            Entry("Platform", reference: false)));

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored");

        Assert.True(result.Success, result.Error);
        Assert.Null(result.Warning);
        var links = await fixture.GetLinksAsync(result.WorkspaceId!.Value);
        Assert.True(links.Single(link => link.RepositoryId == billing.RepositoryId).IsReference);
        Assert.False(links.Single(link => link.RepositoryId == platform.RepositoryId).IsReference);
        Assert.False(links.Single(link => link.RepositoryId == root.RepositoryId).IsReference);
        Assert.True(await ReferenceDefaultAsync(fixture, result.WorkspaceId.Value));
        // What the database writes equals the file, branch and commit included: nothing to rewrite.
        Assert.Equal(["gitignore"], fixture.Manifest.Calls.ToArray());
    }

    [Fact]
    public async Task A_default_of_true_with_one_writable_repository_is_previewed_and_restored_as_mixed()
    {
        var (fixture, root, billing, platform) = await SeedAsync();
        await using var _ = fixture;
        fixture.UseDefinition(Definition(
            true,
            Entry("Billing"),
            Entry("Platform", reference: false)));

        // What the Restore dialog shows before the user confirms.
        var preview = await fixture.Operations.PreflightRestoreAsync(root.RepositoryId);

        Assert.True(preview.Success, preview.Error);
        Assert.Equal(GrayMoon.Application.Workspaces.WorkspaceReferenceAggregate.Mixed, preview.ReferenceAggregate);
        Assert.True(preview.ReferenceDefault);
        Assert.Equal(
            "Some repositories, and new repositories",
            GrayMoon.App.Components.Modals.RestoreWorkspaceFlow.ReferenceSummary(preview));

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored");

        // The default does not override the one repository the definition keeps writable.
        Assert.True(result.Success, result.Error);
        var links = await fixture.GetLinksAsync(result.WorkspaceId!.Value);
        Assert.True(links.Single(link => link.RepositoryId == billing.RepositoryId).IsReference);
        Assert.False(links.Single(link => link.RepositoryId == platform.RepositoryId).IsReference);
        Assert.True(await ReferenceDefaultAsync(fixture, result.WorkspaceId.Value));
        // What the Workspaces list shows for it.
        Assert.Equal("Some", GrayMoon.App.Components.Pages.Workspaces.ReferenceLabel(new Workspace { Repositories = [.. links] }));
    }

    [Fact]
    public async Task A_definition_with_only_the_workspace_value_makes_every_repository_read_only()
    {
        var (fixture, root, _, _) = await SeedAsync();
        await using var __ = fixture;
        fixture.UseDefinition(Definition(true, Entry("Billing"), Entry("Platform")));

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored");

        Assert.True(result.Success, result.Error);
        var sources = await fixture.GetLinksAsync(result.WorkspaceId!.Value, WorkspaceRepositoryRole.Source);
        Assert.Equal(2, sources.Count);
        Assert.All(sources, link => Assert.True(link.IsReference));
        Assert.True(await ReferenceDefaultAsync(fixture, result.WorkspaceId.Value));
        // The file is rewritten so that every entry carries its own state.
        Assert.Equal(["gitignore", "manifest"], fixture.Manifest.Calls.ToArray());
    }

    [Fact]
    public async Task A_default_of_false_with_one_read_only_repository_restores_only_that_one()
    {
        var (fixture, root, billing, platform) = await SeedAsync();
        await using var _ = fixture;
        fixture.UseDefinition(Definition(false, Entry("Billing", reference: false), Entry("Platform", reference: true)));

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored");

        Assert.True(result.Success, result.Error);
        var links = await fixture.GetLinksAsync(result.WorkspaceId!.Value);
        Assert.False(links.Single(link => link.RepositoryId == billing.RepositoryId).IsReference);
        Assert.True(links.Single(link => link.RepositoryId == platform.RepositoryId).IsReference);
        Assert.False(await ReferenceDefaultAsync(fixture, result.WorkspaceId.Value));
    }

    [Fact]
    public async Task A_version_1_definition_restores_no_read_only_state()
    {
        var (fixture, root, _, _) = await SeedAsync();
        await using var __ = fixture;
        fixture.UseDefinition(Definition(null, Entry("Billing"), Entry("Platform")));

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored");

        Assert.True(result.Success, result.Error);
        Assert.All(await fixture.GetLinksAsync(result.WorkspaceId!.Value), link => Assert.False(link.IsReference));
        Assert.False(await ReferenceDefaultAsync(fixture, result.WorkspaceId.Value));
        Assert.Equal(["gitignore"], fixture.Manifest.Calls.ToArray());
    }

    [Fact]
    public async Task A_definition_with_reference_data_but_version_1_is_refused_and_creates_nothing()
    {
        var (fixture, root, _, _) = await SeedAsync();
        await using var __ = fixture;
        fixture.UseDefinition(Definition(true, Entry("Billing", reference: true)).Replace("\"version\": 2", "\"version\": 1", StringComparison.Ordinal));

        var result = await fixture.Operations.RestoreFromRepositoryAsync(root.RepositoryId, "restored");

        Assert.False(result.Success);
        Assert.Contains("\"version\": 2", result.Error);
        await fixture.AssertNoWorkspaceAsync();
    }

    [Fact]
    public async Task An_imported_folder_with_unpushed_commits_is_kept_writable_and_the_other_stays_read_only()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await MutateLinkAsync(ctx, graph.ProducerId, link => link.IsReference = true);
        await MutateLinkAsync(ctx, graph.ConsumerId, link => link.IsReference = true);
        RespondInspect(ctx, path => path.Contains(ProducerName, StringComparison.OrdinalIgnoreCase) ? Unpushed(3) : Clean());

        var warnings = await ApplyGuardAsync(ctx, graph.ProducerId, graph.ConsumerId);

        var warning = Assert.Single(warnings);
        Assert.Equal($"Repository {ProducerName}: it has 3 unpushed commits. It was added as writable, not read-only.", warning);
        Assert.False((await ReadLinkAsync(ctx, graph.ProducerId)).IsReference);
        Assert.True((await ReadLinkAsync(ctx, graph.ConsumerId)).IsReference);
    }

    [Fact]
    public async Task Imported_writable_repositories_are_not_inspected()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        RespondInspect(ctx, _ => Unpushed(1));

        var warnings = await ApplyGuardAsync(ctx, graph.ProducerId, graph.ConsumerId);

        Assert.Empty(warnings);
        Assert.DoesNotContain(ctx.WorkerBridge.Calls, call => call.Command == "InspectWorktree");
        Assert.False((await ReadLinkAsync(ctx, graph.ProducerId)).IsReference);
    }

    [Fact]
    public async Task An_imported_folder_with_only_uncommitted_changes_stays_read_only()
    {
        await using var ctx = await CreateAsync();
        var graph = await SeedGraphAsync(ctx);
        await MutateLinkAsync(ctx, graph.ProducerId, link => link.IsReference = true);
        RespondInspect(ctx, _ =>
        {
            var response = Clean();
            response.IsDirty = true;
            return response;
        });

        var warnings = await ApplyGuardAsync(ctx, graph.ProducerId);

        Assert.Empty(warnings);
        Assert.True((await ReadLinkAsync(ctx, graph.ProducerId)).IsReference);
    }

    private static InspectWorktreeWorkerResponse Clean() => new()
    {
        Exists = true,
        Branch = "main",
        HeadSha = "1111111111111111111111111111111111111111",
        IsDirty = false,
        HasUpstream = true,
        AheadOfUpstream = 0,
    };

    private static InspectWorktreeWorkerResponse Unpushed(int count)
    {
        var response = Clean();
        response.AheadOfUpstream = count;
        return response;
    }

    private static void RespondInspect(SyncStateTestContext ctx, Func<string, InspectWorktreeWorkerResponse> answer) =>
        ctx.WorkerBridge.Respond("InspectWorktree", args =>
            new WorkerCommandResponse(true, answer(JsonSerializer.SerializeToElement(args).GetProperty("worktreePath").GetString()!), null));

    private static async Task<IReadOnlyList<string>> ApplyGuardAsync(SyncStateTestContext ctx, params int[] repositoryIds)
    {
        await using var scope = ctx.CreateScope();
        // The test container registers its services by hand; the guard and its checker are built from them.
        var checker = ActivatorUtilities.CreateInstance<ReferenceActivationChecker>(scope.ServiceProvider);
        return await ActivatorUtilities.CreateInstance<ReferenceImportGuard>(scope.ServiceProvider, checker).ApplyAsync(ctx.WorkspaceId, repositoryIds);
    }

    private static async Task<bool> ReferenceDefaultAsync(OperationsFixture fixture, int workspaceId)
    {
        await using var db = new AppDbContext(fixture.Seed.Options);
        return await db.Workspaces.AsNoTracking().Where(w => w.WorkspaceId == workspaceId).Select(w => w.ReferenceDefault).SingleAsync();
    }
}
