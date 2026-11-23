using System.Text.Json;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application.Features;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// One accessor (plan package E): the update plan, the unmatched-dependency badge and <c>{@Repo}</c> tokens all use the
/// effective version. On the old code a consumer pinned to the parent's published <c>2.4.0-beta.2</c> got an update to the
/// producer's Feature GitVersion <c>2.3.1-abc.3</c> (red test 4); now it is left alone while the inheritance holds.
/// </summary>
public sealed class EffectiveVersionDependencyTests
{
    [Fact]
    public async Task A_consumer_on_the_inherited_version_gets_no_update_and_no_unmatched_badge()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await s.ResolveAsync();

        var plan = await UpdatePlanAsync(s);
        await RecomputeStatsAsync(s);

        Assert.DoesNotContain(plan, p => p.RepoId == s.ConsumerId);
        Assert.Equal(0, (await s.ReadStateAsync(s.ConsumerLinkId)).UnmatchedDeps);
    }

    [Fact]
    public async Task Without_a_resolution_the_consumer_is_updated_to_the_calculated_version()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();

        var plan = await UpdatePlanAsync(s);

        var update = Assert.Single(plan, p => p.RepoId == s.ConsumerId);
        var package = Assert.Single(Assert.Single(update.ProjectUpdates).PackageUpdates);
        Assert.Equal(EffectiveVersionScenario.CalculatedVersion, package.NewVersion);
    }

    [Fact]
    public async Task A_consumer_pinned_to_an_older_version_is_rewritten_to_the_inherited_version()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync(consumerReferenceVersion: "2.4.0-beta.1");
        await s.ResolveAsync();

        var plan = await UpdatePlanAsync(s);
        await RecomputeStatsAsync(s);

        var update = Assert.Single(plan, p => p.RepoId == s.ConsumerId);
        var package = Assert.Single(Assert.Single(update.ProjectUpdates).PackageUpdates);
        Assert.Equal(EffectiveVersionScenario.ParentVersion, package.NewVersion);
        Assert.Equal(1, (await s.ReadStateAsync(s.ConsumerLinkId)).UnmatchedDeps);
    }

    [Fact]
    public async Task A_stored_inheritance_for_another_HEAD_reads_as_the_calculated_version()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await s.ResolveAsync();
        await s.MutateStateAsync(s.ProducerLinkId, state => state.HeadCommit = EffectiveVersionScenario.MovedSha);

        var plan = await UpdatePlanAsync(s);

        var package = Assert.Single(Assert.Single(Assert.Single(plan, p => p.RepoId == s.ConsumerId).ProjectUpdates).PackageUpdates);
        Assert.Equal(EffectiveVersionScenario.CalculatedVersion, package.NewVersion);
    }

    [Fact]
    public async Task A_generated_package_is_required_and_its_consumer_compares_against_the_effective_version()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await s.WithDbAsync(db =>
        {
            foreach (var project in db.WorkspaceProjects.Where(p => p.ProjectId == s.ProducerProjectId))
            {
                project.IsGenerated = true;
                project.WorkspaceFeatureContextId = null;
            }

            return Task.CompletedTask;
        });

        await s.ResolveAsync();

        Assert.Equal((int)EffectiveVersionKind.InheritedVerified, (await s.ReadStateAsync(s.ProducerLinkId)).EffectiveVersionKind);
        Assert.Equal(1, s.Registry.Lookups);
        Assert.DoesNotContain(await UpdatePlanAsync(s), p => p.RepoId == s.ConsumerId);
    }

    [Fact]
    public async Task Repo_token_resolves_to_the_inherited_version()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await AddVersionFileAsync(s);
        await s.ResolveAsync();

        var expected = await CheckFileVersionsAsync(s, s.Special);

        Assert.Contains(EffectiveVersionScenario.ParentVersion, expected);
        Assert.DoesNotContain(EffectiveVersionScenario.CalculatedVersion, expected);
    }

    [Fact]
    public async Task A_Feature_without_its_own_state_row_gets_no_token_value_instead_of_the_Workspace_value()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await AddVersionFileAsync(s);
        var feature = await s.AddFeatureAsync(withStateRows: false);

        var expected = await CheckFileVersionsAsync(s, feature);

        // The old code borrowed the Workspace link's version for the Feature; now the token is skipped (I6).
        Assert.Null(expected);
    }

    [Fact]
    public async Task A_Feature_with_its_own_state_row_uses_its_own_effective_version()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await AddVersionFileAsync(s);
        var feature = await s.AddFeatureAsync();
        await s.MutateStateAsync(s.ProducerLinkId, state => { state.GitVersion = "9.9.9-feature.1"; state.HeadCommit = EffectiveVersionScenario.MovedSha; }, feature);
        await s.ResolveAsync(feature);

        var expected = await CheckFileVersionsAsync(s, feature);

        Assert.Contains("9.9.9-feature.1", expected);
    }

    [Fact]
    public async Task A_file_that_stamps_its_own_repository_compares_against_the_effective_version_then_the_calculated_one()
    {
        // Characterization (red test 5, T18): no special rule for a self-stamping file. While the producer inherits, its
        // own {@Repo} expects the parent version; once a commit moves HEAD, it expects the calculated GitVersion.
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await AddVersionFileAsync(s, s.ProducerId);
        await s.ResolveAsync();

        var whileInherited = await CheckFileVersionsAsync(s, s.Special);
        Assert.Contains(EffectiveVersionScenario.ParentVersion, whileInherited);

        await s.MutateStateAsync(s.ProducerLinkId, state => { state.HeadCommit = EffectiveVersionScenario.MovedSha; state.GitVersion = "2.3.1-abc.4"; });
        await s.ResolveAsync();

        var afterCommit = await CheckFileVersionsAsync(s, s.Special);
        Assert.Contains("2.3.1-abc.4", afterCommit);
        Assert.DoesNotContain(EffectiveVersionScenario.ParentVersion, afterCommit);
    }

    private static async Task AddVersionFileAsync(EffectiveVersionScenario s, int? repositoryId = null)
        => await s.WithDbAsync(async db =>
        {
            var file = new WorkspaceFile { WorkspaceId = s.Ctx.WorkspaceId, RepositoryId = repositoryId ?? s.ConsumerId, FilePath = "version.props", FileName = "version.props" };
            db.WorkspaceFiles.Add(file);
            await db.SaveChangesAsync();
            db.WorkspaceFileVersionConfigs.Add(new WorkspaceFileVersionConfig { FileId = file.FileId, VersionPattern = $"API={{@{EffectiveVersionScenario.ProducerName}}}" });
        });

    /// <summary>Runs the file-version check and returns the expected values it sent to the Worker, or null when it sent nothing.</summary>
    private static async Task<string?> CheckFileVersionsAsync(EffectiveVersionScenario s, WorkspaceFeatureContextId contextId)
    {
        s.Ctx.WorkerBridge.Respond("CheckFileVersions", new { files = Array.Empty<object>() });
        s.Ctx.WorkerBridge.Calls.Clear();
        await using var scope = s.Ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkspaceFileVersionService>()
            .CheckAndPersistFileVersionStatusAsync(s.Ctx.WorkspaceId, contextId, CancellationToken.None);
        var call = s.Ctx.WorkerBridge.Calls.Where(c => c.Command == "CheckFileVersions").Select(c => c.Args).FirstOrDefault();
        return call is null ? null : JsonSerializer.Serialize(call);
    }

    private static async Task<List<SyncDependenciesRepoPayload>> UpdatePlanAsync(EffectiveVersionScenario s)
    {
        await using var scope = s.Ctx.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>()
            .GetSyncDependenciesPayloadAsync(s.Ctx.WorkspaceId, s.Special.Value);
    }

    private static async Task RecomputeStatsAsync(EffectiveVersionScenario s)
    {
        await using var scope = s.Ctx.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>()
            .RecomputeAndPersistRepositoryDependencyStatsAsync(s.Ctx.WorkspaceId, s.Special.Value);
    }
}
