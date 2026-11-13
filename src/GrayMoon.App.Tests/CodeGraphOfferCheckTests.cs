using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.Worker;

namespace GrayMoon.App.Tests;

public sealed class CodeGraphOfferCheckTests
{
    private static HostInfoQuery Answer(string? codeGraphVersion) =>
        new(new HostInfoRead(new HostPrerequisiteVersions(null, null, null, null, null, null), codeGraphVersion, null, []), null);

    private static readonly HostInfoQuery NoAnswer = new(null, "Worker is not connected.");

    [Fact]
    public void A_host_check_without_an_answer_is_still_checking()
    {
        Assert.Equal(CodeGraphOffer.Checking, CodeGraphOfferCheck.From(NoAnswer));
        Assert.Equal(CodeGraphOffer.Offered, CodeGraphOfferCheck.From(Answer("1.2.3")));
        Assert.Equal(CodeGraphOffer.NotInstalled, CodeGraphOfferCheck.From(Answer(null)));
    }

    [Fact]
    public async Task Use_CodeGraph_is_offered_once_the_Worker_answers_after_the_dialog_opened()
    {
        var answers = new Queue<HostInfoQuery>([NoAnswer, NoAnswer, Answer("1.2.3")]);
        var pauses = 0;

        var offer = await CodeGraphOfferCheck.WaitForAnswerAsync(
            _ => Task.FromResult(answers.Dequeue()),
            _ =>
            {
                pauses++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(CodeGraphOffer.Offered, offer);
        Assert.Equal(2, pauses);
        Assert.Empty(answers);
    }

    [Fact]
    public async Task Closing_the_dialog_ends_the_wait()
    {
        using var closed = new CancellationTokenSource();

        var wait = CodeGraphOfferCheck.WaitForAnswerAsync(
            _ => Task.FromResult(NoAnswer),
            token =>
            {
                closed.Cancel();
                return Task.FromCanceled(token);
            },
            closed.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }
}
