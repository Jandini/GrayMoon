using GrayMoon.App.Services.Ai;
using GrayMoon.Common.Ai;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.App.Tests;

public sealed class WorkspacePullRequestTextGenerationTests
{
    private static readonly WorkspacePullRequestTextGeneration.Key Feature = new(1, 10);
    private static readonly WorkspacePullRequestTextGeneration.Key Workspace = new(1, 20);

    private static PullRequestAiText Text(string title) =>
        new(new GeneratedPullRequestText(title, "description"), "fingerprint");

    [Fact]
    public async Task A_run_finishing_with_the_dialog_closed_is_remembered_for_that_context()
    {
        var drafts = new WorkspacePullRequestDraftMemory();
        var generation = NewGeneration(drafts);
        var feature = new TaskCompletionSource<WorkspacePullRequestTextGeneration.Outcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(generation.TryStart(Feature, _ => feature.Task, consult: null));
        Assert.False(generation.TryStart(Feature, _ => feature.Task, consult: null));
        Assert.True(generation.IsRunning(Feature));

        feature.SetResult(WorkspacePullRequestTextGeneration.Outcome.Succeeded(Text("Feature title"), "Claude", ""));
        await WaitUntilAsync(() => !generation.IsRunning(Feature));

        var draft = drafts.Get(1, 10);
        Assert.NotNull(draft);
        Assert.Equal("Feature title", draft.Title);
        Assert.Equal("description", draft.Body);
        Assert.False(draft.TitleIsDefault);
        Assert.Equal("fingerprint", draft.AiFingerprint);
        Assert.Null(drafts.Get(1, 20));
        Assert.Null(generation.OfferFor(Feature));
    }

    [Fact]
    public async Task Text_written_meanwhile_is_kept_and_the_result_is_a_suggestion()
    {
        var drafts = new WorkspacePullRequestDraftMemory();
        var generation = NewGeneration(drafts);
        var run = new TaskCompletionSource<WorkspacePullRequestTextGeneration.Outcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        generation.TryStart(Feature, _ => run.Task, consult: null);
        drafts.Set(1, 10, new PullRequestDraft("Default", "I typed this", TitleIsDefault: true, AiFingerprint: null));
        run.SetResult(WorkspacePullRequestTextGeneration.Outcome.Succeeded(Text("AI title"), "Codex", ""));
        await WaitUntilAsync(() => !generation.IsRunning(Feature));

        Assert.Equal("I typed this", drafts.Get(1, 10)?.Body);
        var offer = generation.OfferFor(Feature);
        Assert.NotNull(offer);
        Assert.Equal("AI title", offer.Text.Text.Title);
        Assert.Equal(AiTextApplyPolicy.EditedReason, offer.Reason);
    }

    [Fact]
    public async Task A_cancelled_run_leaves_nothing_and_a_failure_stays_for_that_context()
    {
        var drafts = new WorkspacePullRequestDraftMemory();
        var generation = NewGeneration(drafts);
        var run = new TaskCompletionSource<WorkspacePullRequestTextGeneration.Outcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        generation.TryStart(Workspace, _ => run.Task, consult: null);
        generation.Cancel(Workspace);
        run.SetResult(WorkspacePullRequestTextGeneration.Outcome.Succeeded(Text("too late"), "Claude", ""));
        await WaitUntilAsync(() => !generation.IsRunning(Workspace));

        Assert.Null(drafts.Get(1, 20));
        Assert.Null(generation.OfferFor(Workspace));

        generation.TryStart(
            Feature,
            _ => Task.FromResult(WorkspacePullRequestTextGeneration.Outcome.Failed("Codex is not signed in.", "Codex", "")),
            consult: null);
        await WaitUntilAsync(() => !generation.IsRunning(Feature));

        Assert.Equal("Codex is not signed in.", generation.Error(Feature));
        Assert.Null(generation.Error(Workspace));
    }

    [Fact]
    public void A_draft_with_only_the_default_title_is_not_kept()
    {
        var drafts = new WorkspacePullRequestDraftMemory();

        drafts.Set(1, 10, new PullRequestDraft("Feature/abc", "", TitleIsDefault: true, AiFingerprint: null));
        Assert.Null(drafts.Get(1, 10));

        drafts.Set(1, 10, new PullRequestDraft("Typed title", "", TitleIsDefault: false, AiFingerprint: null));
        Assert.Equal("Typed title", drafts.Get(1, 10)?.Title);

        drafts.Clear(1, 10);
        Assert.Null(drafts.Get(1, 10));
    }

    private static WorkspacePullRequestTextGeneration NewGeneration(WorkspacePullRequestDraftMemory drafts) =>
        new(drafts, NullLogger<WorkspacePullRequestTextGeneration>.Instance);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Pull request text generation did not finish.");
            await Task.Delay(10);
        }
    }
}
