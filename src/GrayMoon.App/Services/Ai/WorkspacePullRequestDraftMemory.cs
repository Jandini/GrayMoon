namespace GrayMoon.App.Services.Ai;

/// <summary>
/// The New Pull Request dialog's title and description for one Workspace or Feature context, typed or AI-written.
/// <see cref="TitleIsDefault"/> marks the prefilled title from the source branch, which is recomputed for the
/// repositories the dialog opens with. <see cref="AiFingerprint"/> is the branch selection the AI text was written for.
/// </summary>
public sealed record PullRequestDraft(string Title, string Body, bool TitleIsDefault, string? AiFingerprint)
{
    /// <summary>What the user wrote: the prefilled default title alone counts as nothing written yet.</summary>
    public string UserWrittenText => UserWritten(TitleIsDefault ? string.Empty : Title, Body);

    public bool IsEmpty => UserWrittenText.Length == 0;

    public static string UserWritten(string title, string body) => (title + "\n" + body).Trim();
}

/// <summary>
/// App-wide (singleton) memory of the New Pull Request dialog's title and description per Workspace and Feature
/// context. Survives closing the dialog, leaving the page and a browser reload (F5 / Ctrl+F5 starts a new circuit).
/// Kept until every pull request of a Create succeeds or the user empties both fields; not persisted across App
/// restarts. Thread-safe, since every circuit and background AI run shares the one instance.
/// </summary>
public sealed class WorkspacePullRequestDraftMemory
{
    private readonly Dictionary<(int WorkspaceId, int ContextId), PullRequestDraft> _byContext = new();
    private readonly object _gate = new();

    public void Set(int workspaceId, int contextId, PullRequestDraft draft)
    {
        lock (_gate)
        {
            var key = (workspaceId, contextId);
            if (draft.IsEmpty)
            {
                _byContext.Remove(key);
                return;
            }

            _byContext[key] = draft;
        }
    }

    public PullRequestDraft? Get(int workspaceId, int contextId)
    {
        lock (_gate)
            return _byContext.GetValueOrDefault((workspaceId, contextId));
    }

    public void Clear(int workspaceId, int contextId)
    {
        lock (_gate)
            _byContext.Remove((workspaceId, contextId));
    }
}
