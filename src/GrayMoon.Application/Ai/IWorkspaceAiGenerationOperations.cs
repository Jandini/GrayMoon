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
    /// The providers for the menus: which CLIs the Worker can run, the model each uses (from <c>graymoon.json</c>), and the
    /// one pinned locally for this Workspace. <paramref name="refresh"/> checks the CLIs again instead of using the cache.
    /// </summary>
    Task<AiProviderMenu> GetProvidersAsync(int workspaceId, bool refresh, CancellationToken cancellationToken);

    /// <summary>Pins <paramref name="providerId"/> for this Workspace on this machine, or unpins with null. Never generates.</summary>
    Task PinProviderAsync(int workspaceId, string? providerId, CancellationToken cancellationToken);

    Task<AiGenerationResult<GeneratedCommitMessage>> GenerateCommitMessageAsync(
        AiCommitScope scope,
        string providerId,
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

/// <summary>The generated text, or why there is none. Never partial: a failure has no value.</summary>
public sealed record AiGenerationResult<T>(
    T? Value,
    AiGenerationFailure? Failure,
    string ProviderId,
    AiEvidenceCoverage? Coverage)
    where T : class
{
    public bool Success => Value is not null;

    public static AiGenerationResult<T> Fail(string providerId, AiGenerationErrorKind kind, string message, AiEvidenceCoverage? coverage = null) =>
        new(null, new AiGenerationFailure(kind, message), providerId, coverage);
}
