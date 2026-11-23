using GrayMoon.App.Components.Pages;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace GrayMoon.App.Tests;

/// <summary>The Version column (design section 10, plan package G, T34): value, class, title and the copied text.</summary>
public sealed class EffectiveVersionGridTests
{
    [Fact]
    public async Task An_inherited_row_shows_and_copies_the_effective_version_with_its_provenance()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await s.ResolveAsync();

        var link = await ReadLinkAsync(s, s.ProducerId);

        Assert.True(link.IsVersionInherited);
        Assert.Equal(EffectiveVersionScenario.ParentVersion, link.DisplayVersion);
        Assert.Equal(EffectiveVersionScenario.CalculatedVersion, link.GitVersion);
        Assert.Equal(
            $"Inherited published version {EffectiveVersionScenario.ParentVersion} from release/2.4 at 1111111. Calculated GitVersion: {EffectiveVersionScenario.CalculatedVersion}. Click to copy effective version.",
            WorkspaceRepositoriesRow.VersionTitle(link));
    }

    [Fact]
    public async Task An_own_row_shows_and_copies_the_calculated_version()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        await s.ResolveAsync();

        var link = await ReadLinkAsync(s, s.ConsumerId);

        Assert.False(link.IsVersionInherited);
        Assert.Equal(EffectiveVersionScenario.ConsumerCalculatedVersion, link.DisplayVersion);
        Assert.Equal("Click to copy version", WorkspaceRepositoriesRow.VersionTitle(link));
    }

    [Fact]
    public async Task An_unresolved_row_shows_the_calculated_version_and_names_the_reason()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        s.Registry.Answers(EffectiveVersionScenario.PackageId, System.Net.HttpStatusCode.Unauthorized);
        await s.ResolveAsync();

        var link = await ReadLinkAsync(s, s.ProducerId);

        Assert.False(link.IsVersionInherited);
        Assert.Equal(EffectiveVersionScenario.CalculatedVersion, link.DisplayVersion);
        Assert.Contains("the package registry refused the credentials", WorkspaceRepositoriesRow.VersionTitle(link));
    }

    [Fact]
    public async Task A_Feature_row_reads_its_own_decision_and_branch_point()
    {
        await using var s = await EffectiveVersionScenario.CreateAsync();
        var feature = await s.AddFeatureAsync();
        await s.ResolveAsync(feature);

        var featureLink = await ReadLinkAsync(s, s.ProducerId, feature);
        var specialLink = await ReadLinkAsync(s, s.ProducerId);

        Assert.True(featureLink.IsVersionInherited);
        Assert.Equal("release/2.4", featureLink.BaselineParentBranchName);
        Assert.False(specialLink.IsVersionInherited);
    }

    [Fact]
    public void The_clicked_state_key_is_the_repository_and_the_displayed_value()
    {
        Assert.NotEqual(WorkspaceRepositories.VersionClickKey(1, "2.4.0"), WorkspaceRepositories.VersionClickKey(2, "2.4.0"));
        Assert.NotEqual(WorkspaceRepositories.VersionClickKey(1, "2.4.0"), WorkspaceRepositories.VersionClickKey(1, "2.3.1"));
    }

    private static async Task<WorkspaceRepositoryLink> ReadLinkAsync(EffectiveVersionScenario s, int repositoryId, GrayMoon.Application.Features.WorkspaceFeatureContextId? contextId = null)
    {
        var cid = contextId ?? s.Special;
        await using var scope = s.Ctx.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IWorkspaceRepositoryLinkListQueryService>()
            .GetAllSnapshotsAsync(s.Ctx.WorkspaceId, cid, isSpecialWorkspace: cid == s.Special);
        var dto = Assert.Single(rows, r => r.RepositoryId == repositoryId);
        return WorkspaceRepositoryLinkListMapper.ToLink(dto);
    }
}
