using GrayMoon.Application.Features;
using GrayMoon.Common.Ai;

namespace GrayMoon.Application.Ai;

/// <summary>
/// AI-written text for the existing Git workflows: one shared commit message for a workspace commit, or one shared title
/// and description for a set of pull requests. GrayMoon collects bounded Git evidence itself and makes exactly one
/// provider call per request; the provider only writes text. Nothing here stages, commits, pushes, fetches or creates.
/// </summary>
public interface IWorkspaceAiGenerationOperations
{
    /// <summary>
    /// The providers for the menus: which CLIs the startup host check found, the model each uses (from <c>graymoon.json</c>),
    /// and the one pinned locally for this Workspace. Does not probe the CLIs.
    /// </summary>
    Task<AiProviderMenu> GetProvidersAsync(int workspaceId, CancellationToken cancellationToken);

    /// <summary>Pins <paramref name="providerId"/> for this Workspace on this machine, or clears the pin when it is null. Never generates.</summary>
    Task PinProviderAsync(int workspaceId, string? providerId, CancellationToken cancellationToken);

    /// <summary>
    /// <paramref name="progress"/> receives each step (collecting the changes, calling the provider) so a caller running
    /// inside an overlay job can show what is happening; the Worker streams the provider CLI call itself to the job terminal.
    /// </summary>
    Task<AiGenerationResult<GeneratedCommitMessage>> GenerateCommitMessageAsync(
        AiCommitScope scope,
        string providerId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);

    Task<AiGenerationResult<GeneratedPullRequestText>> GeneratePullRequestTextAsync(
        AiPullRequestScope scope,
        string providerId,
        CancellationToken cancellationToken);
}

public sealed record AiProviderOption(
    string Id,
    string Label,
    string IconSrc,
    bool IsAvailable,
    string? Version,
    string? Model,
    bool HasNoTools,
    string? Problem);

public sealed record AiProviderMenu(IReadOnlyList<AiProviderOption> Options, string? PinnedProviderId, string? Notice)
{
    public static AiProviderMenu Empty { get; } = new([], null, null);

    public AiProviderOption? Pinned => Options.FirstOrDefault(o => string.Equals(o.Id, PinnedProviderId, StringComparison.Ordinal));
}

/// <summary>
/// The exact repositories a workspace commit will touch, captured when the AI segment is clicked: the mode the Commit
/// button offered and the repositories it targets under that mode.
/// </summary>
public sealed record AiCommitScope(
    int WorkspaceId,
    WorkspaceFeatureContextId ContextId,
    GitCommitEvidenceMode Mode,
    IReadOnlyList<AiCommitRepository> Repositories);

public sealed record AiCommitRepository(int RepositoryId, string? BranchName);

/// <summary>
/// The pull requests the dialog will create: per repository the source branch and the selected target branch, exactly as
/// the create request will send them. The target is never inferred.
/// </summary>
public sealed record AiPullRequestScope(
    int WorkspaceId,
    WorkspaceFeatureContextId ContextId,
    IReadOnlyList<AiPullRequestRepository> Repositories)
{
    /// <summary>Identity of the comparison: changes when a repository, its source or its selected target changes.</summary>
    public string Fingerprint() => string.Join(
        "\n",
        Repositories
            .OrderBy(r => r.RepositoryId)
            .Select(r => $"{r.RepositoryId}\t{r.HeadBranch}\t{r.BaseBranch}"));
}

public sealed record AiPullRequestRepository(int RepositoryId, string HeadBranch, string BaseBranch);

public sealed record AiGenerationFailure(AiGenerationErrorKind Kind, string Message);

/// <summary>
/// The generated text, or why there is none. Never partial: a failure has no value.
/// <see cref="Warning"/> is set only with a validated <see cref="Value"/> (success with attention), never with a failure.
/// </summary>
public sealed record AiGenerationResult<T>(
    T? Value,
    AiGenerationFailure? Failure,
    string ProviderId,
    AiEvidenceCoverage? Coverage,
    AiGenerationWarning? Warning = null)
    where T : class
{
    public bool Success => Value is not null;

    public static AiGenerationResult<T> Fail(string providerId, AiGenerationErrorKind kind, string message, AiEvidenceCoverage? coverage = null) =>
        new(null, new AiGenerationFailure(kind, message), providerId, coverage);
}
