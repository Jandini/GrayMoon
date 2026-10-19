using GrayMoon.App.Services.Ai;
using GrayMoon.App.Services.GitChanges;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

public sealed class WorkspaceGitChangesCommitGenerationTests
{
    private static readonly WorkspaceGitChangesCommitGeneration.Key Feature = new(1, 10);
    private static readonly WorkspaceGitChangesCommitGeneration.Key Workspace = new(1, 20);

    [Fact]
    public async Task A_feature_and_the_workspace_can_finish_separately()
    {
        var memory = new WorkspaceGitChangesCommitMessageMemory();
        var generation = NewGeneration(memory);
        var feature = new TaskCompletionSource<WorkspaceGitChangesCommitGeneration.Outcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workspace = new TaskCompletionSource<WorkspaceGitChangesCommitGeneration.Outcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(generation.TryStart(Feature, _ => feature.Task, consult: null));
        Assert.True(generation.TryStart(Workspace, _ => workspace.Task, consult: null));
        Assert.False(generation.TryStart(Feature, _ => Task.FromResult(WorkspaceGitChangesCommitGeneration.Outcome.CancelledResult), consult: null));
        Assert.True(generation.IsRunning(Feature));
        Assert.True(generation.IsRunning(Workspace));

        feature.SetResult(WorkspaceGitChangesCommitGeneration.Outcome.Succeeded("feature message", "Claude", ""));
        await WaitUntilAsync(() => !generation.IsRunning(Feature));

        Assert.Equal("feature message", memory.Get(1, 10));
        Assert.Equal(string.Empty, memory.Get(1, 20));
        Assert.True(generation.IsRunning(Workspace));

        generation.Cancel(Workspace);
        workspace.SetResult(WorkspaceGitChangesCommitGeneration.Outcome.Succeeded("should not land", "Claude", ""));
        await WaitUntilAsync(() => !generation.IsRunning(Workspace));

        Assert.Equal(string.Empty, memory.Get(1, 20));
        Assert.Null(generation.OfferFor(Workspace));
        Assert.Equal("feature message", memory.Get(1, 10));
    }

    [Fact]
    public async Task An_edited_message_is_kept_and_the_result_is_a_suggestion()
    {
        var memory = new WorkspaceGitChangesCommitMessageMemory();
        memory.Set(1, 10, "I typed this");
        var generation = NewGeneration(memory);

        generation.TryStart(
            Feature,
            _ => Task.FromResult(WorkspaceGitChangesCommitGeneration.Outcome.Succeeded("ai text", "Claude", "")),
            consult: null);
        await WaitUntilAsync(() => !generation.IsRunning(Feature));

        Assert.Equal("I typed this", memory.Get(1, 10));
        var offer = generation.OfferFor(Feature);
        Assert.NotNull(offer);
        Assert.Equal("ai text", offer.Text);
        Assert.Equal(AiTextApplyPolicy.EditedReason, offer.Reason);
    }

    [Fact]
    public async Task A_page_that_still_shows_the_context_can_keep_the_result_as_a_suggestion()
    {
        var memory = new WorkspaceGitChangesCommitMessageMemory();
        var generation = NewGeneration(memory);

        generation.TryStart(
            Feature,
            _ => Task.FromResult(WorkspaceGitChangesCommitGeneration.Outcome.Succeeded("ai text", "Codex", "")),
            _ => Task.FromResult<AiApplyDecision?>(new AiApplyDecision(false, AiTextApplyPolicy.StaleChangesReason)));
        await WaitUntilAsync(() => !generation.IsRunning(Feature));

        Assert.Equal(string.Empty, memory.Get(1, 10));
        Assert.Equal(AiTextApplyPolicy.StaleChangesReason, generation.OfferFor(Feature)?.Reason);
    }

    [Fact]
    public async Task A_page_that_applies_the_message_is_not_written_again()
    {
        var memory = new WorkspaceGitChangesCommitMessageMemory();
        var generation = NewGeneration(memory);

        generation.TryStart(
            Feature,
            _ => Task.FromResult(WorkspaceGitChangesCommitGeneration.Outcome.Succeeded("ai text", "Claude", "")),
            _ =>
            {
                memory.Set(1, 10, "page applied");
                return Task.FromResult<AiApplyDecision?>(new AiApplyDecision(true, null));
            });
        await WaitUntilAsync(() => !generation.IsRunning(Feature));

        Assert.Equal("page applied", memory.Get(1, 10));
        Assert.Null(generation.OfferFor(Feature));
        Assert.Null(generation.Error(Feature));
    }

    [Fact]
    public async Task A_failure_is_remembered_for_that_context_only()
    {
        var memory = new WorkspaceGitChangesCommitMessageMemory();
        memory.Set(1, 20, "workspace text");
        var generation = NewGeneration(memory);

        generation.TryStart(
            Feature,
            _ => Task.FromResult(WorkspaceGitChangesCommitGeneration.Outcome.Failed("Claude is not signed in.", "Claude", "")),
            consult: null);
        await WaitUntilAsync(() => !generation.IsRunning(Feature));

        Assert.Equal("Claude is not signed in.", generation.Error(Feature));
        Assert.Equal(string.Empty, memory.Get(1, 10));
        Assert.Equal("workspace text", memory.Get(1, 20));
        Assert.Null(generation.Error(Workspace));
    }

    [Fact]
    public void Generate_commit_message_starts_ticked_until_changed_and_push_committed_starts_unticked_per_workspace()
    {
        var generate = new WorkspaceGitChangesGenerateMessageOnCommitMemory();
        var push = new WorkspaceGitChangesPushAfterCommitMemory();

        Assert.True(generate.Get(1));
        Assert.False(push.Get(1));

        generate.Set(1, false);
        push.Set(1, true);

        Assert.False(generate.Get(1));
        Assert.True(push.Get(1));
        Assert.True(generate.Get(2));
        Assert.False(push.Get(2));
    }

    private static WorkspaceGitChangesCommitGeneration NewGeneration(WorkspaceGitChangesCommitMessageMemory memory) =>
        new(memory, NullLogger<WorkspaceGitChangesCommitGeneration>.Instance);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Commit generation did not finish.");
            await Task.Delay(10);
        }
    }
}
