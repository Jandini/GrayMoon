using GrayMoon.App.Components.Pages;
using GrayMoon.Application.Features;

namespace GrayMoon.App.Tests;

/// <summary>
/// The rules a context page follows for its <c>?context=</c> value. Actions, Dependencies, Files and Packages read
/// the value once, when the page opened: browser back and forward left the data of the context that was left on
/// screen, and of two loads started by quick switches the slower one won.
/// </summary>
public sealed class PageContextBindingTests
{
    private static readonly WorkspaceFeatureContextId Workspace = new(33);
    private static readonly WorkspaceFeatureContextId Feature = new(34);

    [Fact]
    public void Nothing_counts_as_a_change_before_the_page_has_resolved_its_context()
    {
        var binding = new PageContextBinding();

        Assert.False(binding.QueryChanged(null));
        Assert.False(binding.QueryChanged(34));
    }

    [Fact]
    public void Back_and_forward_between_a_Feature_and_the_Workspace_are_changes_and_the_selectors_own_navigation_is_not()
    {
        var binding = new PageContextBinding();
        binding.Bind(Feature, isSpecialWorkspace: false);

        // The selector switched to the Feature and then navigated to ?context=34: the same context.
        Assert.False(binding.QueryChanged(34));
        // Back to the URL without the value: the Workspace.
        Assert.True(binding.QueryChanged(null));
        Assert.True(binding.QueryChanged(35));

        binding.Bind(Workspace, isSpecialWorkspace: true);

        // The Workspace's URL has no ?context=. Its own context id in the URL is not what the page is bound to.
        Assert.False(binding.QueryChanged(null));
        Assert.False(binding.QueryChanged(0));
        Assert.True(binding.QueryChanged(34));
    }

    [Fact]
    public void A_load_that_began_under_an_older_context_is_not_current()
    {
        var binding = new PageContextBinding();
        var first = binding.Bind(Feature, isSpecialWorkspace: false);
        Assert.True(binding.IsCurrent(first));

        var second = binding.Bind(Workspace, isSpecialWorkspace: true);

        Assert.False(binding.IsCurrent(first));
        Assert.True(binding.IsCurrent(second));
        Assert.False(binding.IsCurrent(second, disposed: true));
    }

    [Fact]
    public async Task Loads_never_overlap_and_the_last_one_begins_after_the_latest_request()
    {
        var binding = new PageContextBinding();
        var gate = new TaskCompletionSource();
        var running = 0;
        var maxRunning = 0;
        var began = new List<int>();
        var latest = 1;

        async Task Load()
        {
            maxRunning = Math.Max(maxRunning, ++running);
            began.Add(latest);
            if (began.Count == 1)
                await gate.Task;
            running--;
        }

        // The first load is waiting for its data when two more switches arrive.
        var first = binding.RunLatestAsync(Load);
        latest = 2;
        var second = binding.RunLatestAsync(Load);
        latest = 3;
        var third = binding.RunLatestAsync(Load);
        Assert.False(first.IsCompleted || second.IsCompleted || third.IsCompleted);

        gate.SetResult();
        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, maxRunning);
        // One more run for both later requests, and it saw the latest selection.
        Assert.Equal([1, 3], began);
    }

    [Fact]
    public async Task A_load_that_throws_does_not_block_the_next_one()
    {
        var binding = new PageContextBinding();

        await Assert.ThrowsAsync<InvalidOperationException>(() => binding.RunLatestAsync(() => throw new InvalidOperationException("boom")));

        var ran = false;
        await binding.RunLatestAsync(() =>
        {
            ran = true;
            return Task.CompletedTask;
        });
        Assert.True(ran);
    }
}
