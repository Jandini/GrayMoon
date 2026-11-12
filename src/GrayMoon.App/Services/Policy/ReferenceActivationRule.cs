using GrayMoon.App.Services.Features;

namespace GrayMoon.App.Services.Policy;

/// <summary>What the activation check found in one checkout of a repository.</summary>
public enum ReferenceActivationSeverity
{
    /// <summary>Nothing to report: the checkout holds nothing that is not on the remote.</summary>
    Pass,

    /// <summary>Reported to the user; the repository may still become read-only.</summary>
    Warning,

    /// <summary>The repository must not become read-only: something here exists only on this machine, or could not be read.</summary>
    Block,
}

/// <summary>
/// One finding of the activation check. <paramref name="FeatureName"/> is null for the Workspace checkout.
/// </summary>
public sealed record ReferenceActivationFinding(
    int RepositoryId,
    string RepositoryName,
    string? FeatureName,
    ReferenceActivationSeverity Severity,
    string Message)
{
    public bool Blocks => Severity == ReferenceActivationSeverity.Block;
}

/// <summary>
/// May a checkout be handed over to read-only mode? Read-only means GrayMoon never commits or pushes there again, so
/// a commit that exists only on this machine would be stranded. Pure: it judges one InspectWorktree answer.
/// Ahead of default, behind upstream and pull request state are not read.
/// </summary>
internal static class ReferenceActivationRule
{
    internal const string UnreadableMessage = "its state could not be read.";
    internal const string NoUpstreamMessage = "its branch has no upstream and its commit is not on the remote.";
    internal const string DirtyMessage = "it has uncommitted changes. They stay as they are; GrayMoon will not commit them.";
    internal const string MissingFolderMessage = "its folder is missing, so it was not checked.";

    internal static string UnpushedMessage(int count) =>
        count == 1 ? "it has 1 unpushed commit." : $"it has {count} unpushed commits.";

    /// <summary>
    /// The rule for a checkout whose folder exists. <paramref name="response"/> is null when the Worker gave no
    /// usable answer.
    /// </summary>
    internal static (ReferenceActivationSeverity Severity, string? Message) Evaluate(InspectWorktreeWorkerResponse? response)
    {
        if (response == null || !string.IsNullOrWhiteSpace(response.Error) || response.IsDirty == null)
            return (ReferenceActivationSeverity.Block, UnreadableMessage);

        var dirty = response.IsDirty == true
            ? (ReferenceActivationSeverity.Warning, DirtyMessage)
            : (ReferenceActivationSeverity.Pass, (string?)null);

        // A tag or a detached HEAD: no branch that could be ahead of anything.
        if (string.IsNullOrWhiteSpace(response.Branch))
            return dirty;

        switch (response.HasUpstream)
        {
            case null:
                return (ReferenceActivationSeverity.Block, UnreadableMessage);
            case false:
                // A new branch with no commits of its own (a fresh Feature branch) is on the remote through its parent.
                return response.HeadOnRemote == true ? dirty : (ReferenceActivationSeverity.Block, NoUpstreamMessage);
        }

        return response.AheadOfUpstream switch
        {
            null => (ReferenceActivationSeverity.Block, UnreadableMessage),
            > 0 => (ReferenceActivationSeverity.Block, UnpushedMessage(response.AheadOfUpstream.Value)),
            _ => dirty,
        };
    }
}
