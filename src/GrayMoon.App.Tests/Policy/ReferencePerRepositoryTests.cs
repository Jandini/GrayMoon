using GrayMoon.App.Components.Modals;
using GrayMoon.App.Components.Pages;
using GrayMoon.App.Models;
using GrayMoon.App.Services.Queries;
using GrayMoon.Application.WorkspaceManifest;
using GrayMoon.Application.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Tests.Policy;

/// <summary>
/// Phase 2 of read-only repositories: the <c>ro:</c> search field, the none / some / all text, the text of the drift
/// banner and of the Restore preview, and where the per-repository control is offered.
/// </summary>
public sealed class ReferencePerRepositoryTests
{
    [Fact]
    public async Task Search_ro_yes_and_ro_no_split_the_repositories_by_their_read_only_state()
    {
        var (ctx, workspaceId) = await ListQueryTestContext.CreateWithWorkspaceLinksAsync(30);
        await using (ctx)
        {
            var referenceIds = await ctx.DbContext.WorkspaceRepositories
                .Where(link => link.WorkspaceId == workspaceId)
                .OrderBy(link => link.WorkspaceRepositoryId)
                .Take(7)
                .Select(link => link.WorkspaceRepositoryId)
                .ToListAsync();
            await ctx.DbContext.WorkspaceRepositories
                .Where(link => referenceIds.Contains(link.WorkspaceRepositoryId))
                .ExecuteUpdateAsync(s => s.SetProperty(link => link.IsReference, true));

            var yes = await ctx.WorkspaceRepoLinkQuery.GetPageAsync(new WorkspaceRepositoryLinkListRequest(workspaceId, "ro:yes", 50, null));
            var no = await ctx.WorkspaceRepoLinkQuery.GetPageAsync(new WorkspaceRepositoryLinkListRequest(workspaceId, "ro:no", 50, null));

            Assert.Equal(referenceIds.Order(), yes.Items.Select(item => item.WorkspaceRepositoryId).Order());
            Assert.All(yes.Items, item => Assert.True(item.IsReference));
            Assert.Equal(23, no.Items.Count);
            Assert.All(no.Items, item => Assert.False(item.IsReference));
            Assert.Equal(7, await ctx.WorkspaceRepoLinkQuery.CountAsync(new WorkspaceRepositoryLinkListFilter(workspaceId, "RO:YES")));
        }
    }

    [Fact]
    public async Task Search_ro_with_another_value_matches_nothing_and_an_unknown_field_still_matches_everything()
    {
        var (ctx, workspaceId) = await ListQueryTestContext.CreateWithWorkspaceLinksAsync(12);
        await using (ctx)
        {
            Assert.Equal(0, await ctx.WorkspaceRepoLinkQuery.CountAsync(new WorkspaceRepositoryLinkListFilter(workspaceId, "ro:maybe")));
            Assert.Equal(12, await ctx.WorkspaceRepoLinkQuery.CountAsync(new WorkspaceRepositoryLinkListFilter(workspaceId, "nosuchfield:x")));
        }
    }

    [Fact]
    public async Task Search_ro_combines_with_a_text_term()
    {
        var (ctx, workspaceId) = await ListQueryTestContext.CreateWithWorkspaceLinksAsync(30);
        await using (ctx)
        {
            await ctx.DbContext.WorkspaceRepositories
                .Where(link => link.WorkspaceId == workspaceId)
                .ExecuteUpdateAsync(s => s.SetProperty(link => link.IsReference, true));
            var all = await ctx.WorkspaceRepoLinkQuery.CountAsync(new WorkspaceRepositoryLinkListFilter(workspaceId, "main"));

            Assert.True(all > 0);
            Assert.Equal(all, await ctx.WorkspaceRepoLinkQuery.CountAsync(new WorkspaceRepositoryLinkListFilter(workspaceId, "main ro:yes")));
            Assert.Equal(0, await ctx.WorkspaceRepoLinkQuery.CountAsync(new WorkspaceRepositoryLinkListFilter(workspaceId, "main ro:no")));
        }
    }

    [Theory]
    [InlineData(0, 0, WorkspaceReferenceAggregate.NoSources, "-")]
    [InlineData(20, 0, WorkspaceReferenceAggregate.None, "None")]
    [InlineData(20, 1, WorkspaceReferenceAggregate.Mixed, "Some")]
    [InlineData(20, 19, WorkspaceReferenceAggregate.Mixed, "Some")]
    [InlineData(20, 20, WorkspaceReferenceAggregate.All, "All")]
    public void The_summary_is_none_some_or_all_and_never_a_count(int sources, int reference, WorkspaceReferenceAggregate expected, string label)
    {
        var aggregate = WorkspaceReferenceAggregates.Of(sources, reference);

        Assert.Equal(expected, aggregate);
        Assert.Equal(label, WorkspaceReferenceAggregates.Label(aggregate));
    }

    [Fact]
    public void The_Workspaces_list_does_not_count_the_Workspace_repository()
    {
        static WorkspaceRepositoryLink Link(bool reference, WorkspaceRepositoryRole role = WorkspaceRepositoryRole.Source) =>
            new() { IsReference = reference, Role = role };

        Assert.Equal("All", Workspaces.ReferenceLabel(new Workspace { Repositories = [Link(true), Link(true), Link(false, WorkspaceRepositoryRole.Workspace)] }));
        Assert.Equal("Some", Workspaces.ReferenceLabel(new Workspace { Repositories = [Link(true), Link(false)] }));
        Assert.Equal("None", Workspaces.ReferenceLabel(new Workspace { Repositories = [Link(false)] }));
        Assert.Equal("-", Workspaces.ReferenceLabel(new Workspace { Repositories = [Link(false, WorkspaceRepositoryRole.Workspace)] }));
    }

    [Fact]
    public void The_drift_text_names_the_repositories_and_the_default()
    {
        var none = new WorkspaceManifestDrift(true, null, ["X"], [], [], [], []);
        var names = new WorkspaceManifestDrift(true, null, [], [], [], [], []) { ChangedReference = ["Api", "Web"] };
        var both = new WorkspaceManifestDrift(true, null, [], [], [], [], []) { ChangedReference = ["Api"], ReferenceDefaultChanged = true };
        var many = new WorkspaceManifestDrift(true, null, [], [], [], [], []) { ChangedReference = ["A", "B", "C", "D", "E", "F", "G"] };

        Assert.Null(none.DescribeReferenceDifferences());
        Assert.Equal("The read-only state differs for Api, Web.", names.DescribeReferenceDifferences());
        Assert.Equal(
            "The read-only state differs for Api. The read-only default for new repositories differs.",
            both.DescribeReferenceDifferences());
        Assert.Equal("The read-only state differs for A, B, C, D, E and 2 more.", many.DescribeReferenceDifferences());
    }

    [Theory]
    [InlineData(WorkspaceReferenceAggregate.None, false, null)]
    [InlineData(WorkspaceReferenceAggregate.NoSources, false, null)]
    [InlineData(WorkspaceReferenceAggregate.None, true, "New repositories only")]
    [InlineData(WorkspaceReferenceAggregate.Mixed, false, "Some repositories")]
    [InlineData(WorkspaceReferenceAggregate.Mixed, true, "Some repositories, and new repositories")]
    [InlineData(WorkspaceReferenceAggregate.All, true, "All repositories, and new repositories")]
    public void The_Restore_preview_names_read_only_state_only_when_the_definition_has_some(
        WorkspaceReferenceAggregate aggregate, bool referenceDefault, string? expected)
    {
        var preview = new RestoreWorkspacePreflight { Success = true, ReferenceAggregate = aggregate, ReferenceDefault = referenceDefault };

        Assert.Equal(expected, RestoreWorkspaceFlow.ReferenceSummary(preview));
    }

    [Theory]
    [InlineData(false, false, 5, true)]
    [InlineData(true, false, 5, false)]
    [InlineData(false, true, 5, false)]
    [InlineData(false, false, 0, false)]
    public void The_per_repository_control_is_offered_only_for_a_Source_in_the_Workspace_view(
        bool isFeatureContext, bool isWorkspaceRepository, int repositoryId, bool expected)
    {
        Assert.Equal(expected, SwitchBranchModal.ShouldShowReferenceToggle(isFeatureContext, isWorkspaceRepository, repositoryId));
    }
}
