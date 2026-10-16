using GrayMoon.Abstractions.Worker;
using GrayMoon.Worker.Hosted;
using GrayMoon.Worker.Jobs.Requests;

namespace GrayMoon.Worker.Tests;

public sealed class CommandJobStreamLabelTests
{
    [Fact]
    public void An_ai_text_generation_is_labelled_with_its_provider()
    {
        var label = CommandJobStreamLabel.Resolve(WorkerHubMethods.GenerateAiText, new GenerateAiTextRequest { ProviderId = "claude" });

        Assert.Equal("claude", label);
    }
}
