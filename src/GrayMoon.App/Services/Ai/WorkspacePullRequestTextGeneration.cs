using GrayMoon.Common.Ai;
using Microsoft.Extensions.Logging;

namespace GrayMoon.App.Services.Ai;

/// <summary>A generated pull request title and description with the branch selection (<c>AiPullRequestScope.Fingerprint</c>) it describes.</summary>
public sealed record PullRequestAiText(GeneratedPullRequestText Text, string Fingerprint);

/// <summary>
/// App-wide runs of "write the title and description" started from the New Pull Request dialog's provider button
/// (see <see cref="AiTextGenerationRuns{TText}"/>). Closing the dialog does not cancel a run; the written text is
/// stored in that context's <see cref="WorkspacePullRequestDraftMemory"/> and shows the next time the dialog opens.
/// </summary>
public sealed class WorkspacePullRequestTextGeneration(
    WorkspacePullRequestDraftMemory drafts,
    ILogger<WorkspacePullRequestTextGeneration> logger)
    : AiTextGenerationRuns<PullRequestAiText>(logger, "Writing the title and description failed - see logs.")
{
    protected override string CurrentText(Key key) =>
        drafts.Get(key.WorkspaceId, key.ContextId)?.UserWrittenText ?? string.Empty;

    protected override void Store(Key key, PullRequestAiText text) =>
        drafts.Set(key.WorkspaceId, key.ContextId, new PullRequestDraft(text.Text.Title, text.Text.Description, TitleIsDefault: false, text.Fingerprint));
}
