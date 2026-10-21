using System.Diagnostics;
using System.Text.Json.Serialization;
using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Data;
using GrayMoon.App.Services.Worker;
using GrayMoon.Application.Ai;
using GrayMoon.Application.Features;
using GrayMoon.Application.WorkspaceManifest;
using GrayMoon.Common.Ai;
using GrayMoon.Common.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Services.Ai;

/// <summary>
/// One click, one generation: resolve the provider and its model, collect bounded evidence for the captured scope in a
/// single Worker request, pack one prompt, make one provider call, and validate the answer. Every failure is specific and
/// local to the AI controls; nothing here touches staging, commits, pushes or pull request creation.
/// </summary>
public sealed class WorkspaceAiGenerationOperations(
    IWorkerBridge workerBridge,
    IDbContextFactory<AppDbContext> dbContextFactory,
    IWorkspaceContextPathResolver pathResolver,
    IWorkspaceManifestService manifestService,
    AiProviderAvailabilityCache availability,
    WorkspaceAiProviderPins pins,
    IOptions<GitChangesOptions> gitChangesOptions,
    IOptions<AiGenerationOptions> aiOptions,
    ILogger<WorkspaceAiGenerationOperations> logger) : IWorkspaceAiGenerationOperations
{
    public async Task<AiProviderMenu> GetProvidersAsync(int workspaceId, CancellationToken cancellationToken)
    {
        var installed = await availability.GetAsync(workerBridge, cancellationToken);
        var pinned = await pins.GetAsync(workspaceId, cancellationToken);

        WorkspaceManifestAiRead ai;
        try
        {
            ai = await ReadAiAsync(workspaceId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Reading the ai section of the Workspace definition failed. WorkspaceId={WorkspaceId}", workspaceId);
            ai = new WorkspaceManifestAiRead(null, "graymoon.json could not be read.");
        }

        var options = AiProviderIds.All.Select(id =>
        {
            var cli = installed.First(p => p.ProviderId == id);
            var choice = AiProviderConfiguration.Resolve(ai.Section, id);
            var problem = cli.IsInstalled ? choice.Error : cli.Problem;
            return new AiProviderOption(
                id,
                AiProviderIds.DisplayName(id),
                IconFor(id),
                cli.IsInstalled && choice.IsValid,
                cli.Version,
                choice.Model,
                cli.HasNoTools,
                problem);
        }).ToList();

        var unknown = AiProviderConfiguration.UnknownProviderIds(ai.Section);
        var notice = ai.Error
                     ?? (unknown.Count > 0 ? $"graymoon.json lists AI providers this GrayMoon does not know: {string.Join(", ", unknown)}. They are ignored." : null);
        return new AiProviderMenu(options, pinned, notice);
    }

    public Task PinProviderAsync(int workspaceId, string? providerId, CancellationToken cancellationToken) =>
        pins.SetAsync(workspaceId, providerId, cancellationToken);

    public async Task<AiGenerationResult<GeneratedCommitMessage>> GenerateCommitMessageAsync(
        AiCommitScope scope,
        string providerId,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (scope.Repositories.Count == 0)
            return AiGenerationResult<GeneratedCommitMessage>.Fail(providerId, AiGenerationErrorKind.NothingToDescribe, "There is nothing to commit.");

        var repositories = scope.Repositories
            .Select(r => new GitEvidenceRepositoryScope { RepositoryId = r.RepositoryId })
            .ToList();
        var branches = scope.Repositories.ToDictionary(r => r.RepositoryId, r => r.BranchName);

        var run = await RunAsync(
            GitEvidenceKind.Commit,
            scope.WorkspaceId,
            scope.ContextId,
            scope.Mode,
            repositories,
            providerId,
            AiPromptBudget.Commit,
            perFileBytes: 1_500,
            "the commit message",
            progress,
            evidence =>
            {
                foreach (var repository in evidence)
                    repository.HeadRef = branches.GetValueOrDefault(repository.RepositoryId);
                return AiPromptBuilder.BuildCommit(evidence, scope.Mode, AiPromptBudget.Commit);
            },
            cancellationToken);
        if (run.Failure is not null)
            return AiGenerationResult<GeneratedCommitMessage>.Fail(providerId, run.Failure.Kind, run.Failure.Message, run.Coverage);

        return AiGeneratedTextParser.TryParseCommit(run.Json, out var message, out var error)
            ? new AiGenerationResult<GeneratedCommitMessage>(message, null, providerId, run.Coverage)
            : AiGenerationResult<GeneratedCommitMessage>.Fail(providerId, AiGenerationErrorKind.InvalidOutput, error!, run.Coverage);
    }

    public async Task<AiGenerationResult<GeneratedPullRequestText>> GeneratePullRequestTextAsync(
        AiPullRequestScope scope,
        string providerId,
        CancellationToken cancellationToken)
    {
        if (scope.Repositories.Count == 0)
            return AiGenerationResult<GeneratedPullRequestText>.Fail(providerId, AiGenerationErrorKind.NothingToDescribe, "There are no pull requests to describe.");
        if (scope.Repositories.GroupBy(r => r.RepositoryId).Any(g => g.Count() > 1))
            return AiGenerationResult<GeneratedPullRequestText>.Fail(providerId, AiGenerationErrorKind.Evidence, "A repository is listed twice.");

        // The selected target is the only base: a missing or identical one is an error, never a default branch.
        var invalid = scope.Repositories
            .Where(r => string.IsNullOrWhiteSpace(r.HeadBranch)
                        || string.IsNullOrWhiteSpace(r.BaseBranch)
                        || string.Equals(r.HeadBranch, r.BaseBranch, StringComparison.Ordinal))
            .ToList();
        if (invalid.Count > 0)
            return AiGenerationResult<GeneratedPullRequestText>.Fail(providerId, AiGenerationErrorKind.Evidence,
                "Choose a target branch (different from the source) for every repository first.");

        var repositories = scope.Repositories
            .Select(r => new GitEvidenceRepositoryScope { RepositoryId = r.RepositoryId, HeadBranch = r.HeadBranch, BaseBranch = r.BaseBranch })
            .ToList();

        var run = await RunAsync(
            GitEvidenceKind.PullRequest,
            scope.WorkspaceId,
            scope.ContextId,
            GitCommitEvidenceMode.Staged,
            repositories,
            providerId,
            AiPromptBudget.PullRequest,
            perFileBytes: 1_200,
            "the pull request text",
            progress: null,
            evidence => AiPromptBuilder.BuildPullRequest(evidence, AiPromptBudget.PullRequest),
            cancellationToken);
        if (run.Failure is not null)
            return AiGenerationResult<GeneratedPullRequestText>.Fail(providerId, run.Failure.Kind, run.Failure.Message, run.Coverage);

        return AiGeneratedTextParser.TryParsePullRequest(run.Json, out var text, out var error)
            ? new AiGenerationResult<GeneratedPullRequestText>(text, null, providerId, run.Coverage)
            : AiGenerationResult<GeneratedPullRequestText>.Fail(providerId, AiGenerationErrorKind.InvalidOutput, error!, run.Coverage);
    }

    private async Task<GenerationRun> RunAsync(
        GitEvidenceKind kind,
        int workspaceId,
        WorkspaceFeatureContextId contextId,
        GitCommitEvidenceMode mode,
        List<GitEvidenceRepositoryScope> repositories,
        string providerId,
        AiPromptBudget budget,
        int perFileBytes,
        string subject,
        IProgress<OperationProgress>? progress,
        Func<IReadOnlyList<RepositoryGitEvidence>, PreparedAiPrompt> buildPrompt,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        if (!AiProviderIds.IsKnown(providerId))
            return GenerationRun.Fail(AiGenerationErrorKind.Configuration, $"Unknown AI provider \"{providerId}\".");
        if (workerBridge.GetUnavailableReason() is { } unavailable)
            return GenerationRun.Fail(AiGenerationErrorKind.WorkerUnavailable, unavailable);

        // Provider and model first: a CLI that is missing or a broken configuration fails before any Git work.
        var ai = await ReadAiAsync(workspaceId, cancellationToken);
        if (ai.Error is not null)
            return GenerationRun.Fail(AiGenerationErrorKind.Configuration, ai.Error);
        var choice = AiProviderConfiguration.Resolve(ai.Section, providerId);
        if (!choice.IsValid)
            return GenerationRun.Fail(AiGenerationErrorKind.Configuration, choice.Error!);
        var cli = (await availability.GetAsync(workerBridge, cancellationToken)).First(p => p.ProviderId == providerId);
        if (!cli.IsInstalled)
            return GenerationRun.Fail(AiGenerationErrorKind.NotInstalled, cli.Problem ?? $"{AiProviderIds.DisplayName(providerId)} CLI is not available on the Worker.");

        // Repository names come from this Workspace's links, never from the caller.
        var names = await RepositoryNamesAsync(workspaceId, repositories.Select(r => r.RepositoryId).ToList(), cancellationToken);
        var missing = repositories.Where(r => !names.ContainsKey(r.RepositoryId)).ToList();
        if (missing.Count > 0)
            return GenerationRun.Fail(AiGenerationErrorKind.Evidence, "A repository is not part of this Workspace.");
        foreach (var repository in repositories)
            repository.RepositoryName = names[repository.RepositoryId];

        var args = await pathResolver.GetWorkerArgsAsync(contextId, cancellationToken);
        if (string.IsNullOrWhiteSpace(args.WorkspaceRoot))
            return GenerationRun.Fail(AiGenerationErrorKind.Evidence, "Workspace root is not configured.");

        // Collection budget per repository: an even share of the evidence budget, never below a useful minimum, so a
        // large selection cannot read more than the prompt could ever use.
        var evidenceChars = (int)(budget.MaxChars * 0.6);
        var limits = new GitEvidenceLimits
        {
            MaxPatchBytesPerFile = perFileBytes,
            MaxPatchBytesPerRepository = Math.Clamp(evidenceChars / repositories.Count, 1_200, 8_000),
            MaxInventoryFiles = repositories.Count > 10 ? 60 : 200,
            MaxHistoryCommits = repositories.Count > 10 ? 60 : 200,
        };

        progress.Report($"Collecting the changes of {repositories.Count} repositor{(repositories.Count == 1 ? "y" : "ies")}...");
        var evidenceResponse = await workerBridge.SendCommandAsync(
            WorkerHubMethods.CollectGitEvidence,
            new
            {
                workspaceRoot = args.WorkspaceRoot,
                workspaceRepositoryName = args.WorkspaceRepositoryName,
                workspaceName = args.WorkspaceFolderName,
                maxParallelOperations = Math.Max(1, gitChangesOptions.Value.MaxParallelRepositoryOperations),
                kind,
                commitMode = mode,
                repositories,
                limits,
            },
            cancellationToken);
        var evidence = evidenceResponse.Success
            ? WorkerResponseJson.DeserializeWorkerResponse<CollectGitEvidenceWorkerResponse>(evidenceResponse.Data)
            : null;
        if (evidence is null)
            return GenerationRun.Fail(AiGenerationErrorKind.Evidence, evidenceResponse.Error ?? "The Worker could not collect the Git changes.");
        var evidenceMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        // All or nothing: a commit message or pull request description that silently leaves a repository out would be
        // wrong for that repository, so any failed comparison stops here with the reason per repository.
        var failed = evidence.Repositories.Where(r => !r.Success).ToList();
        if (failed.Count > 0)
            return GenerationRun.Fail(AiGenerationErrorKind.Evidence, Describe(failed));

        var conflicted = evidence.Repositories.Where(r => r.ConflictedPaths.Count > 0).ToList();
        if (conflicted.Count > 0)
            return GenerationRun.Fail(AiGenerationErrorKind.Evidence,
                $"Resolve the merge conflicts first ({string.Join(", ", conflicted.Select(r => r.RepositoryName))}).");

        if (evidence.Repositories.Sum(r => r.TotalFiles) == 0)
            return GenerationRun.Fail(AiGenerationErrorKind.NothingToDescribe,
                kind == GitEvidenceKind.Commit ? "There are no changes to describe in the selected scope." : "The branches have no file changes to describe.");

        var prompt = buildPrompt(evidence.Repositories);
        var promptMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        // The commit overlay names the provider only. The model is still sent to the CLI.
        var model = kind == GitEvidenceKind.Commit || choice.Model is null ? string.Empty : $" ({choice.Model})";
        progress.Report($"Writing {subject} with {AiProviderIds.DisplayName(providerId)}{model}...");
        var generation = await workerBridge.SendCommandAsync(
            WorkerHubMethods.GenerateAiText,
            new
            {
                providerId,
                model = choice.Model,
                systemPrompt = prompt.SystemPrompt,
                userPrompt = prompt.UserPrompt,
                jsonSchema = prompt.JsonSchema,
                timeoutSeconds = Math.Clamp(aiOptions.Value.TimeoutSeconds, 10, 600),
            },
            cancellationToken);
        var answer = generation.Success
            ? WorkerResponseJson.DeserializeWorkerResponse<GenerateAiTextWorkerResponse>(generation.Data)
            : null;

        var outcome = answer switch
        {
            null => GenerationRun.Fail(AiGenerationErrorKind.ProviderFailed, generation.Error ?? "The Worker did not return an answer.", prompt.Coverage),
            { Success: true, Json: not null } => new GenerationRun(answer.Json, null, prompt.Coverage),
            _ => GenerationRun.Fail(answer.ErrorKind == AiGenerationErrorKind.None ? AiGenerationErrorKind.ProviderFailed : answer.ErrorKind,
                answer.ErrorMessage ?? "The AI provider did not answer.", prompt.Coverage),
        };

        logger.LogInformation(
            "AI generation. Kind={Kind} Provider={Provider} ModelSet={ModelSet} Repositories={Repositories} ChangedFiles={ChangedFiles} ExcerptFiles={ExcerptFiles} HistoryCommits={HistoryCommits} HistoryShown={HistoryShown} RedactedLines={RedactedLines} Partial={Partial} EstimatedTokens={EstimatedTokens} EvidenceMs={EvidenceMs} WorkerEvidenceMs={WorkerEvidenceMs} PromptMs={PromptMs} ProviderMs={ProviderMs} TotalMs={TotalMs} Outcome={Outcome}",
            kind, providerId, choice.Model is not null, prompt.Coverage.Repositories, prompt.Coverage.ChangedFiles, prompt.Coverage.FilesWithExcerpts,
            prompt.Coverage.HistoryCommits, prompt.Coverage.HistoryShown, prompt.Coverage.RedactedLines, prompt.Coverage.IsPartial, prompt.EstimatedTokens, evidenceMs, evidence.ElapsedMs, promptMs - evidenceMs,
            answer?.ElapsedMs, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, outcome.Failure?.Kind ?? AiGenerationErrorKind.None);
        return outcome;
    }

    private static string Describe(IReadOnlyList<RepositoryGitEvidence> failed)
    {
        var shown = failed.Take(3).Select(r => $"{r.RepositoryName}: {r.ErrorMessage}");
        var more = failed.Count > 3 ? $" (and {failed.Count - 3} more)" : string.Empty;
        return $"Cannot describe every repository, so nothing was generated. {string.Join(" ", shown)}{more}";
    }

    private async Task<WorkspaceManifestAiRead> ReadAiAsync(int workspaceId, CancellationToken cancellationToken)
    {
        var (section, error) = await manifestService.ReadAiAsync(workspaceId, cancellationToken);
        return new WorkspaceManifestAiRead(section, error);
    }

    private async Task<Dictionary<int, string>> RepositoryNamesAsync(int workspaceId, IReadOnlyList<int> repositoryIds, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.WorkspaceRepositories.AsNoTracking()
            .Where(l => l.WorkspaceId == workspaceId && repositoryIds.Contains(l.RepositoryId) && l.Repository != null)
            .Select(l => new { l.RepositoryId, l.Repository!.RepositoryName })
            .ToDictionaryAsync(l => l.RepositoryId, l => l.RepositoryName, cancellationToken);
    }

    private static string IconFor(string providerId) => providerId switch
    {
        AiProviderIds.Claude => "icons/claude.svg",
        AiProviderIds.Codex => "icons/codex.svg",
        AiProviderIds.Cursor => "icons/cursor.svg",
        _ => string.Empty,
    };

    private sealed record WorkspaceManifestAiRead(GrayMoon.Common.Manifest.WorkspaceManifestAi? Section, string? Error);

    private sealed record GenerationRun(string? Json, AiGenerationFailure? Failure, AiEvidenceCoverage? Coverage)
    {
        public static GenerationRun Fail(AiGenerationErrorKind kind, string message, AiEvidenceCoverage? coverage = null) =>
            new(null, new AiGenerationFailure(kind, message), coverage);
    }

    internal sealed class CollectGitEvidenceWorkerResponse
    {
        [JsonPropertyName("repositories")]
        public List<RepositoryGitEvidence> Repositories { get; set; } = [];

        [JsonPropertyName("elapsedMs")]
        public long ElapsedMs { get; set; }
    }

    internal sealed class GenerateAiTextWorkerResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("json")]
        public string? Json { get; set; }

        [JsonPropertyName("errorKind")]
        public AiGenerationErrorKind ErrorKind { get; set; }

        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }

        [JsonPropertyName("elapsedMs")]
        public long ElapsedMs { get; set; }
    }
}
