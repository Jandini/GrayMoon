using GrayMoon.App.Components.Pages;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services.Queries;
using GrayMoon.App.Services.Versions;
using GrayMoon.Application.Features;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>
/// A read only snapshot of a Feature is a detached worktree, and GitVersion on a detached HEAD does not give the version
/// its branch published: a commit three commits after tag 1.2.0 on <c>main</c> is <c>1.2.1-main.3</c> in the Workspace
/// and <c>1.2.0</c> in the snapshot. The Feature then rewrote its consumers to the tag version. The snapshot's effective
/// version is the version of the branch it was taken from at its commit.
/// </summary>
public sealed class EffectiveVersionReferenceSnapshotTests
{
    [Fact]
    public async Task A_snapshot_uses_the_version_of_its_source_branch_not_the_detached_GitVersion()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        var feature = await s.AddFeatureWithProducerSnapshotAsync();

        var resolution = await s.ResolveAsync(feature);

        var producer = resolution.Decisions.Single(d => d.RepositoryId == s.ProducerId);
        Assert.Equal(EffectiveVersionKind.InheritedUnverified, producer.Kind);
        Assert.Equal(EffectiveVersionReasonCodes.ReferenceSnapshot, producer.ReasonCode);
        Assert.Equal(EffectiveVersionScenario.ParentVersion, producer.EffectiveVersion);
        Assert.Null(resolution.StopMessage);

        // The Worker was asked for the source branch at the snapshot's commit.
        var call = Assert.Single(s.Ctx.WorkerBridge.Calls, c => c.Command == GrayMoon.Abstractions.Worker.WorkerHubMethods.GetGitVersionAtBranchPoint);
        var args = System.Text.Json.JsonSerializer.Serialize(call.Args);
        Assert.Contains("\"parentBranch\":\"release/2.4\"", args);
        Assert.Contains($"\"commitSha\":\"{EffectiveVersionScenario.BaseSha}\"", args);

        // The calculated GitVersion is kept; nothing is looked up, because a snapshot never publishes.
        var state = await s.ReadStateAsync(s.ProducerLinkId, feature);
        Assert.Equal(EffectiveVersionScenario.DetachedVersion, state.GitVersion);
        Assert.Equal(EffectiveVersionScenario.ParentVersion, state.EffectiveVersion);
        Assert.Equal(EffectiveVersionScenario.BaseSha, state.EffectiveVersionCommitSha);
        Assert.Equal(0, s.Registry.Lookups);
    }

    [Fact]
    public async Task Consumers_in_the_Feature_are_compared_with_the_source_branch_version()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        var feature = await s.AddFeatureWithProducerSnapshotAsync();
        await s.ResolveAsync(feature);

        await using var scope = s.Ctx.CreateScope();
        var (versionByRepo, _) = await scope.ServiceProvider.GetRequiredService<WorkspaceProjectRepository>()
            .GetContextVersionAndLevelByRepoAsync(s.Ctx.WorkspaceId, feature.Value, CancellationToken.None);

        // The consumer references ParentVersion: with the detached version it would be rewritten to the tag.
        Assert.Equal(EffectiveVersionScenario.ParentVersion, versionByRepo[s.ProducerId]);
    }

    [Fact]
    public async Task A_snapshot_nobody_consumes_still_gets_its_source_branch_version()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        var feature = await s.AddFeatureWithProducerSnapshotAsync();
        await s.WithDbAsync(db =>
        {
            db.ProjectDependencies.RemoveRange(db.ProjectDependencies);
            return Task.CompletedTask;
        });

        var producer = (await s.ResolveAsync(feature)).Decisions.Single(d => d.RepositoryId == s.ProducerId);

        Assert.Equal(EffectiveVersionKind.InheritedUnverified, producer.Kind);
        Assert.Equal(EffectiveVersionScenario.ParentVersion, producer.EffectiveVersion);
    }

    [Fact]
    public async Task A_background_refresh_and_an_operation_start_reuse_the_stored_snapshot_version()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        var feature = await s.AddFeatureWithProducerSnapshotAsync();
        await s.ResolveAsync(feature, bypassCache: true);
        Assert.Equal(1, s.BranchPointCalls);

        var refreshed = await s.ResolveAsync(feature, bypassCache: false);
        var atStart = await s.ResolveAsync(feature, bypassCache: true);

        Assert.Equal(1, s.BranchPointCalls);
        Assert.Equal(EffectiveVersionScenario.ParentVersion, refreshed.Decisions.Single(d => d.RepositoryId == s.ProducerId).EffectiveVersion);
        Assert.Equal(EffectiveVersionScenario.ParentVersion, atStart.Decisions.Single(d => d.RepositoryId == s.ProducerId).EffectiveVersion);
    }

    [Fact]
    public async Task A_decision_stored_before_snapshots_had_their_own_rule_is_replaced_on_the_next_refresh()
    {
        // An existing Feature has "Own, PolicyProtected" stored for the snapshot's HEAD; the refresh must not keep it.
        await using var s = await EffectiveVersionScenario.CreateAsync();
        var feature = await s.AddFeatureWithProducerSnapshotAsync();
        await s.MutateStateAsync(s.ProducerLinkId, state =>
        {
            state.EffectiveVersionKind = (int)EffectiveVersionKind.Own;
            state.EffectiveVersion = EffectiveVersionScenario.DetachedVersion;
            state.EffectiveVersionCommitSha = EffectiveVersionScenario.BaseSha;
            state.EffectiveVersionReasonCode = EffectiveVersionReasonCodes.PolicyProtected;
        }, feature);

        await s.ResolveAsync(feature, bypassCache: false);

        var state = await s.ReadStateAsync(s.ProducerLinkId, feature);
        Assert.Equal((int)EffectiveVersionKind.InheritedUnverified, state.EffectiveVersionKind);
        Assert.Equal(EffectiveVersionScenario.ParentVersion, state.EffectiveVersion);
    }

    [Fact]
    public async Task A_consumed_snapshot_whose_source_version_cannot_be_calculated_stops_operations()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        var feature = await s.AddFeatureWithProducerSnapshotAsync();
        s.RespondBranchPoint(success: false, version: null, onRemoteParent: true, error: "GitVersion versioned branch 'origin/release/2.4'");

        var resolution = await s.ResolveAsync(feature);

        var producer = resolution.Decisions.Single(d => d.RepositoryId == s.ProducerId);
        Assert.Equal(EffectiveVersionKind.Unresolved, producer.Kind);
        Assert.Equal(EffectiveVersionReasonCodes.ParentVersionUnavailable, producer.ReasonCode);
        Assert.Contains($"Cannot calculate the version of the read only snapshot {EffectiveVersionScenario.ProducerName}", resolution.StopMessage);
        Assert.Contains("release/2.4 at 1111111", resolution.StopMessage);
        Assert.Contains("Nothing was changed.", resolution.StopMessage);

        // Unresolved is never kept: the next refresh asks again.
        s.RespondBranchPoint(success: true, version: EffectiveVersionScenario.ParentVersion, onRemoteParent: true);
        var recovered = await s.ResolveAsync(feature, bypassCache: false);
        Assert.Equal(EffectiveVersionKind.InheritedUnverified, recovered.Decisions.Single(d => d.RepositoryId == s.ProducerId).Kind);
    }

    [Fact]
    public async Task A_consumed_snapshot_whose_commit_is_not_on_the_remote_source_branch_stops_operations()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        var feature = await s.AddFeatureWithProducerSnapshotAsync();
        s.RespondBranchPoint(success: true, version: null, onRemoteParent: false);

        var resolution = await s.ResolveAsync(feature);

        var producer = resolution.Decisions.Single(d => d.RepositoryId == s.ProducerId);
        Assert.Equal(EffectiveVersionKind.Unresolved, producer.Kind);
        Assert.Equal(EffectiveVersionReasonCodes.BaselineNotOnRemoteParent, producer.ReasonCode);
        Assert.Contains("its commit 1111111 is not on origin/release/2.4", resolution.StopMessage);
    }

    [Fact]
    public async Task An_unconsumed_snapshot_whose_source_version_cannot_be_calculated_stops_nothing()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        var feature = await s.AddFeatureWithProducerSnapshotAsync();
        await s.WithDbAsync(db =>
        {
            db.ProjectDependencies.RemoveRange(db.ProjectDependencies);
            return Task.CompletedTask;
        });
        s.RespondBranchPoint(success: false, version: null, onRemoteParent: true, error: "no GitVersion configuration");

        var resolution = await s.ResolveAsync(feature);

        var producer = resolution.Decisions.Single(d => d.RepositoryId == s.ProducerId);
        Assert.Equal(EffectiveVersionKind.Own, producer.Kind);
        Assert.Equal(EffectiveVersionReasonCodes.ParentVersionUnavailable, producer.ReasonCode);
        Assert.Null(resolution.StopMessage);

        // Deterministic and unused: the background refresh keeps it without another GitVersion run.
        await s.ResolveAsync(feature, bypassCache: false);
        Assert.Equal(1, s.BranchPointCalls);
    }

    [Fact]
    public async Task A_snapshot_taken_from_a_tag_or_a_detached_Source_keeps_its_calculated_version()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        var feature = await s.AddFeatureWithProducerSnapshotAsync(sourceBranch: null);

        var producer = (await s.ResolveAsync(feature)).Decisions.Single(d => d.RepositoryId == s.ProducerId);

        Assert.Equal(EffectiveVersionKind.Own, producer.Kind);
        Assert.Equal(EffectiveVersionReasonCodes.PolicyProtected, producer.ReasonCode);
        Assert.Equal(EffectiveVersionScenario.DetachedVersion, producer.EffectiveVersion);
        Assert.Equal(0, s.BranchPointCalls);
    }

    [Fact]
    public async Task The_grid_shows_the_source_branch_version_and_says_where_it_comes_from()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        var feature = await s.AddFeatureWithProducerSnapshotAsync();
        await s.ResolveAsync(feature);

        var link = await ReadLinkAsync(s, s.ProducerId, feature);

        Assert.True(link.IsVersionInherited);
        Assert.Equal(EffectiveVersionScenario.ParentVersion, link.DisplayVersion);
        Assert.Equal(
            $"Version {EffectiveVersionScenario.ParentVersion} of release/2.4 at 1111111, the commit this read only snapshot stays at. GitVersion of the detached checkout: {EffectiveVersionScenario.DetachedVersion}. Click to copy version.",
            WorkspaceRepositoriesRow.VersionTitle(link));
    }

    private static async Task<WorkspaceRepositoryLink> ReadLinkAsync(EffectiveVersionScenario s, int repositoryId, WorkspaceFeatureContextId contextId)
    {
        await using var scope = s.Ctx.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IWorkspaceRepositoryLinkListQueryService>()
            .GetAllSnapshotsAsync(s.Ctx.WorkspaceId, contextId, isSpecialWorkspace: false);
        var dto = Assert.Single(rows, r => r.RepositoryId == repositoryId);
        return WorkspaceRepositoryLinkListMapper.ToLink(dto);
    }
}
