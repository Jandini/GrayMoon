using GrayMoon.App.Services.Ai;
using Microsoft.Extensions.Logging;

namespace GrayMoon.App.Services.GitChanges;

/// <summary>
/// App-wide runs of "write the commit message" started from the provider button on Changes (see
/// <see cref="AiTextGenerationRuns{TText}"/>). The written message is stored in that context's
/// <see cref="WorkspaceGitChangesCommitMessageMemory"/> even when no page is showing it.
/// </summary>
public sealed class WorkspaceGitChangesCommitGeneration(
    WorkspaceGitChangesCommitMessageMemory messages,
    ILogger<WorkspaceGitChangesCommitGeneration> logger)
    : AiTextGenerationRuns<string>(logger, "Writing the commit message failed - see logs.")
{
    protected override string CurrentText(Key key) => messages.Get(key.WorkspaceId, key.ContextId);

    protected override void Store(Key key, string text) => messages.Set(key.WorkspaceId, key.ContextId, text);
}
