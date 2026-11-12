using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Models;
using GrayMoon.App.Services;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.Application.Features;
using GrayMoon.Common.Git;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;

namespace GrayMoon.App.Components.Pages;

public sealed partial class WorkspaceGitChanges
{
    [Inject] private IOptions<GitChangesOptions> GitChangesOptions { get; set; } = default!;

    private string _workspaceCommitMessage = string.Empty;
    private bool _workspaceCommitMessageHasContent;
    private int _commitMessageBoxKey;
    private string? _ignoreStaleCommitMessage;

    /// <summary>
    /// Restores the in-progress commit message for the current Workspace and Feature context. Called once the
    /// context is known, including after a reload, so a Feature and the Workspace each show their own text.
    /// </summary>
    private void RestoreWorkspaceCommitMessage()
    {
        if (_selectedContextId is not { } contextId)
            return;

        ShowCommitMessage(CommitMessageMemory.Get(WorkspaceId, contextId.Value));
    }

    private void OnWorkspaceCommitMessageInput(ChangeEventArgs e)
    {
        var newValue = e.Value?.ToString() ?? string.Empty;
        if (_ignoreStaleCommitMessage != null)
        {
            if (newValue == _ignoreStaleCommitMessage)
                return;
            _ignoreStaleCommitMessage = null;
        }
        var hadContent = _workspaceCommitMessageHasContent;
        var hasContent = !string.IsNullOrWhiteSpace(newValue);
        _workspaceCommitMessage = newValue;
        _workspaceCommitMessageHasContent = hasContent;
        if (_selectedContextId is { } contextId)
            CommitMessageMemory.Set(WorkspaceId, contextId.Value, newValue);

        if (hadContent != hasContent)
        {
            StateHasChanged();
        }
    }

    /// <summary>Repositories a Commit Staged would commit. A read-only repository's staged files do not count.</summary>
    private int StagedRepositoryCount => _view?.Repositories.Count(r => r.StagedCount > 0 && !r.IsReference) ?? 0;
    private int ChangedRepositoryCount => _view?.Repositories.Count(r => r.StagedCount > 0 || r.ChangedCount > 0) ?? 0;

    private static readonly IReadOnlySet<string> EmptyPushPackageIds = new HashSet<string>();

    /// <summary>
    /// Mode offered/emphasized for the primary commit button on this render. The razor captures this
    /// into a local at render time so the click handler executes that exact mode even if <c>_view</c>
    /// mutates before the event runs (TOCTOU).
    /// </summary>
    private WorkspaceCommitMode OfferedCommitMode =>
        WorkspaceCommitModeHelper.ResolveOfferedMode(StagedRepositoryCount > 0);

    /// <summary>An empty message box blocks Commit unless the AI will write the message first (<see cref="CommitGeneratesMessage"/>).</summary>
    private bool IsCommitButtonDisabled =>
        IsJobRunning || (string.IsNullOrWhiteSpace(_workspaceCommitMessage) && !CommitGeneratesMessage);

    private string PrimaryCommitTitle(WorkspaceCommitMode mode)
    {
        var action = (mode, PushAfterCommit) switch
        {
            (WorkspaceCommitMode.Staged, true) => "Commits only already-staged changes, then pushes exactly the repositories that committed successfully.",
            (WorkspaceCommitMode.Staged, false) => "Commits only already-staged changes in each applicable repository.",
            (_, true) => "Stages all changes, commits, then pushes the repositories that committed successfully.",
            _ => "Stages all changes (git add --all) then commits in each repository with changes.",
        };
        var writer = string.IsNullOrWhiteSpace(_workspaceCommitMessage) && CommitMessageWriter is { } provider
            ? $"The message box is empty, so {provider.Label} writes the commit message first. "
            : string.Empty;
        return $"{writer}{action} Ctrl+Enter from the message box.";
    }

    /// <summary>
    /// While the message box is empty (nothing typed or written by the AI yet), the Commit control sits at the box's
    /// right edge as one control and saves a row; any message moves it back below the box.
    /// </summary>
    private bool CommitControlAttached => !_workspaceCommitMessageHasContent;

    private const string CommitMessagePlaceholder = "Commit message";

    /// <summary>"Push committed" checkbox state, remembered per workspace via <see cref="PushAfterCommitMemory"/>
    /// so it survives leaving Changes and a reload (F5 / Ctrl+F5).</summary>
    private bool PushAfterCommit
    {
        get => PushAfterCommitMemory.Get(WorkspaceId);
        set => PushAfterCommitMemory.Set(WorkspaceId, value);
    }

    private void OnPushAfterCommitChanged(bool value) => PushAfterCommit = value;

    /// <summary>
    /// Commits with one shared message across every applicable repository. Not atomic: each repository
    /// commits independently through the bounded fan-out below, one repository's failure never blocks
    /// or rolls back another's success. Mirrors the existing SemaphoreSlim + Task.WhenAll idiom used by
    /// PushOrchestrator/DependencyUpdateOrchestrator rather than introducing a new scheduler abstraction,
    /// bounded by the shared <see cref="GitChangesOptions.MaxParallelRepositoryOperations"/> - the same
    /// limit used by the workspace status scan - rather than a separately hard-coded value.
    /// Runs behind the page's LoadingOverlay/terminal job - this can touch many files across many
    /// repositories and run commit hooks, unlike the fast single-file/folder stage/unstage actions.
    /// When <paramref name="pushAfterCommit"/> is true, a push of exactly the repositories that
    /// committed successfully runs immediately afterward, inside the same job/terminal.
    /// With an empty message box and <see cref="CommitGeneratesMessage"/>, the job first writes the message with the
    /// pinned AI provider (same overlay/terminal), then commits and pushes exactly as with a typed message.
    /// <paramref name="mode"/> must be the value captured when the command was rendered/chosen - never
    /// recompute from mutable <c>_view</c> here.
    /// </summary>
    private void CommitWorkspaceAsync(WorkspaceCommitMode mode, bool pushAfterCommit)
    {
        if (string.IsNullOrWhiteSpace(_workspaceCommitMessage) && !CommitGeneratesMessage)
        {
            ToastService.ShowError("Enter a commit message.");
            return;
        }

        if (!WorkerBridge.IsWorkerConnected)
        {
            ToastService.ShowError("Worker not connected. Start the GrayMoon Worker and try again.");
            return;
        }

        if (IsJobRunning || _defaultBranchWarningModal.IsVisible)
        {
            return;
        }

        var targets = (_view?.Repositories ?? [])
            .Where(r => WorkspaceCommitModeHelper.IsRepositoryTarget(mode, r))
            .ToList();

        if (targets.Count == 0)
        {
            // Only read-only repositories have something to commit: say why nothing happens.
            var referenceCount = (_view?.Repositories ?? [])
                .Count(r => r.IsReference && WorkspaceCommitModeHelper.IsRepositoryTarget(mode, r.StagedCount, r.ChangedCount));
            if (referenceCount > 0)
            {
                ToastService.Show(referenceCount == 1
                    ? "Nothing to commit: the repository with changes is read only."
                    : $"Nothing to commit: the {referenceCount} repositories with changes are read only.");
            }

            return;
        }

        var reposOnDefaultBranch = targets.Where(WorkspaceCommitTargetPlanner.IsOnDefaultBranch).ToList();

        if (reposOnDefaultBranch.Count > 0)
        {
            var repoItems = reposOnDefaultBranch
                .Select(r => new DefaultBranchWarningItem(r.RepositoryName, r.DefaultBranchName!))
                .ToList();
            ShowDefaultBranchWarning(
                "The following repositories are on their default branch. Committing will write directly to the default (protected) branch.",
                repoItems,
                () =>
                {
                    CommitWorkspaceCoreAsync(mode, pushAfterCommit, targets);
                    return Task.CompletedTask;
                });
            return;
        }

        CommitWorkspaceCoreAsync(mode, pushAfterCommit, targets);
    }

    /// <param name="clickedTargets">
    /// The targets of the click: what the AI describes and what the user confirmed in the default-branch dialog. The
    /// job reads the repositories again right before it commits (<see cref="WorkspaceCommitTargetPlanner"/>).
    /// </param>
    private void CommitWorkspaceCoreAsync(WorkspaceCommitMode mode, bool pushAfterCommit, List<WorkspaceGitChangesRepositoryView> clickedTargets)
    {
        if (IsJobRunning)
        {
            return;
        }

        // Decided here, when the commit actually starts (after any default-branch confirmation), from the box as it is now:
        // a typed message is committed as typed; an empty one only when the AI writes it as the job's first step.
        var typedMessage = _workspaceCommitMessage;
        var committedContextId = RequireSelectedContextId();
        CommitMessageGeneration? generation = null;
        if (string.IsNullOrWhiteSpace(typedMessage))
        {
            if (CommitMessageWriter is not { } writer)
            {
                ToastService.ShowError("Enter a commit message.");
                return;
            }

            generation = new CommitMessageGeneration(writer, BuildAiCommitScope(mode, clickedTargets));
        }

        var stageAllFirst = WorkspaceCommitModeHelper.StageAllFirst(mode);
        var committingLabel = $"Committing in {clickedTargets.Count} repositor{(clickedTargets.Count == 1 ? "y" : "ies")}...";
        var label = generation is null ? committingLabel : $"Writing the commit message with {generation.Provider.Label}...";

        StartPageJob(label, async (job, ct) =>
        {
            var message = generation is null
                ? typedMessage
                : await GenerateCommitMessageInJobAsync(job, generation, ct);
            if (message is null)
            {
                return;
            }

            // The click is minutes old when the AI wrote the message, and older still after the default-branch
            // dialog: decide the targets now, from the persisted state of the context the job was queued with.
            var plan = await PlanCommitTargetsAsync(job, mode, clickedTargets, committedContextId, ct);
            var targets = plan.Targets;
            if (plan.AddedSinceClick.Count > 0)
            {
                job.Terminal.Append(
                    "commit",
                    WorkerCommandStreamKind.Stdout,
                    $"Also committing {string.Join(", ", plan.AddedSinceClick)}: changed after the commit was started."
                    + (generation is null ? "" : " The commit message was written before that and does not describe these changes."));
            }

            job.ReportProgress($"Committing in {targets.Count} repositor{(targets.Count == 1 ? "y" : "ies")}...");

            // One outcome per target repository. A repository that ends without one is reported as failed (see
            // RepositoryOutcomes.Complete), so a repository can never vanish from the result.
            var outcomes = new RepositoryOutcomes(
                targets.Select(t => (t.WorkspaceRepositoryId, t.RepositoryName))
                    .Concat(plan.LeftOut.Select(l => (l.Repository.WorkspaceRepositoryId, l.Repository.RepositoryName))));
            foreach (var (repository, reason) in plan.LeftOut)
            {
                outcomes.Skipped(repository.WorkspaceRepositoryId, repository.RepositoryName, reason);
            }
            var repositoryIds = new System.Collections.Concurrent.ConcurrentDictionary<int, int>();
            var completed = 0;

            using var semaphore = new SemaphoreSlim(Math.Max(1, GitChangesOptions.Value.MaxParallelRepositoryOperations));

            var tasks = targets.Select(async repo =>
            {
                try
                {
                    await semaphore.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    outcomes.Failed(repo.WorkspaceRepositoryId, repo.RepositoryName, "Cancelled before it started.");
                    throw;
                }

                try
                {
                    var resolved = await ResolveRepositoryAsync(repo.WorkspaceRepositoryId);
                    if (resolved == null)
                    {
                        outcomes.Failed(repo.WorkspaceRepositoryId, repo.RepositoryName, "Repository not found or workspace root is not configured.");
                        return;
                    }

                    repositoryIds[repo.WorkspaceRepositoryId] = resolved.Value.RepositoryId;

                    Logger.LogInformation(
                        "Workspace commit requested: Mode={Mode}, Repository={Repository}, StageAllFirst={StageAllFirst}",
                        mode, repo.RepositoryName, stageAllFirst);

                    var result = await GitChangesOperations.CommitAsync(
                        WorkspaceId, committedContextId, resolved.Value.RepositoryId, message, stageAllFirst, ct);

                    var nothingToCommit = !result.Success && string.Equals(result.ErrorCode, "NothingStaged", StringComparison.Ordinal);
                    await PersistMutationResultAsync(
                        repo.WorkspaceRepositoryId, resolved.Value.RepositoryId, result.Success, result.Snapshot, result.ErrorMessage,
                        reload: false, showError: false);

                    if (result.Success)
                    {
                        outcomes.Succeeded(repo.WorkspaceRepositoryId, repo.RepositoryName);
                    }
                    else if (nothingToCommit)
                    {
                        outcomes.Skipped(repo.WorkspaceRepositoryId, repo.RepositoryName, "no changes to commit");
                    }
                    else
                    {
                        outcomes.Failed(repo.WorkspaceRepositoryId, repo.RepositoryName, result.ErrorMessage ?? "Commit failed.");
                    }
                }
                catch (OperationCanceledException)
                {
                    outcomes.Failed(repo.WorkspaceRepositoryId, repo.RepositoryName, "Cancelled.");
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Workspace commit failed for repository {RepositoryName}", repo.RepositoryName);
                    outcomes.Failed(repo.WorkspaceRepositoryId, repo.RepositoryName, $"Unexpected error ({ex.GetType().Name}) - see logs.");
                }
                finally
                {
                    semaphore.Release();
                    var done = Interlocked.Increment(ref completed);
                    job.ReportProgress($"Committed {done} of {targets.Count} repositories...");
                }
            });

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
                // Tasks that never ran have no outcome; Complete() reports them as failed so the run still says what happened.
                var partial = outcomes.Complete();
                Logger.LogWarning("Workspace commit cancelled: {Summary}", RepositoryOutcomes.Summarize(partial, "Committed", "Not committed"));
                throw;
            }

            var results = outcomes.Complete();
            var succeeded = results
                .Where(r => r.Kind == RepositoryOutcomeKind.Succeeded && repositoryIds.ContainsKey(r.Key))
                .Select(r => (RepositoryName: r.Repository, RepositoryId: repositoryIds[r.Key]))
                .ToList();
            var failedCount = RepositoryOutcomes.Count(results, RepositoryOutcomeKind.Failed);
            var summary = RepositoryOutcomes.Summarize(results, "Committed", "Not committed");

            // One line per run in the log, so a missing repository is visible without reading stack traces.
            Logger.LogInformation(
                "Workspace commit finished: Mode={Mode}, Requested={Requested}, Committed={Committed}, Skipped={Skipped}, Failed={Failed}. {Summary}",
                mode, targets.Count, succeeded.Count, RepositoryOutcomes.Count(results, RepositoryOutcomeKind.Skipped), failedCount, summary);

            // The job terminal keeps the full per-repository result; the toast carries the same summary.
            foreach (var outcome in results.Where(r => r.Kind != RepositoryOutcomeKind.Succeeded))
            {
                job.Terminal.Append(
                    "commit",
                    outcome.Kind == RepositoryOutcomeKind.Failed ? WorkerCommandStreamKind.Stderr : WorkerCommandStreamKind.Stdout,
                    $"{outcome.Repository}: {(outcome.Kind == RepositoryOutcomeKind.Failed ? "NOT committed" : "skipped")} - {outcome.Detail}");
            }

            job.Terminal.Append("commit", failedCount > 0 ? WorkerCommandStreamKind.Stderr : WorkerCommandStreamKind.Stdout, summary);

            // The message is only consumed when every repository was handled. If any repository failed it stays in the
            // box and in memory, so the same message can be committed again after the cause is fixed.
            if (succeeded.Count > 0 && failedCount == 0)
            {
                // Clear this context's message even if the page was disposed (user navigated away mid-commit).
                // SafeInvoke no-ops when disposed, which previously left the message in memory so it
                // came back the next time Git Changes was opened. Another context's message stays.
                CommitMessageMemory.Clear(WorkspaceId, committedContextId.Value);
                _ignoreStaleCommitMessage = message;
                SafeInvoke(() =>
                {
                    if (_selectedContextId != committedContextId)
                        return;
                    _workspaceCommitMessage = string.Empty;
                    _workspaceCommitMessageHasContent = false;
                    _commitMessageBoxKey++;
                });
            }

            SafeInvoke(() =>
            {
                if (failedCount > 0)
                    ToastService.ShowError(summary + " The commit message was kept.");
                else
                    ToastService.Show(summary);
            });

            // Runs inside the same job/terminal as the commit above - the overlay is never hidden and
            // re-shown between the two phases. Only repositories that committed successfully are pushed;
            // a simple parallel push (no dependency-level ordering, no NuGet registry wait) since this
            // repo set doesn't necessarily line up with the workspace's dependency levels.
            if (pushAfterCommit && succeeded.Count > 0)
            {
                var pushRepoIds = succeeded.Select(s => s.RepositoryId).ToHashSet();
                job.ReportProgress("Preparing push...");

                var result = await ScopedExecutor.ExecuteAsync<IWorkspacePushOperations, OperationResult>(svc =>
                    svc.PushAsync(
                        WorkspaceId, committedContextId,
                        pushRepoIds,
                        synchronizedPush: false,
                        requiredPackageIds: EmptyPushPackageIds,
                        job.ToOperationProgress(),
                        cancellationToken: ct));
                result.ShowRepoErrors(
                    msg => SafeInvoke(() => ToastService.ShowError(msg)),
                    warning => SafeInvoke(() => ToastService.ShowWarning(warning)),
                    skipped => SafeInvoke(() => ToastService.Show(skipped)));
            }
        });
    }

    /// <summary>
    /// Reads the context's repositories again (off the dispatcher, into a local; never from <c>_view</c>) and plans the
    /// run. When the read fails the click's targets are used and the terminal says so.
    /// </summary>
    private async Task<WorkspaceCommitTargetPlan> PlanCommitTargetsAsync(
        BackgroundJobHandle job,
        WorkspaceCommitMode mode,
        List<WorkspaceGitChangesRepositoryView> clickedTargets,
        WorkspaceFeatureContextId contextId,
        CancellationToken cancellationToken)
    {
        try
        {
            var summaries = await ReadService.GetContextSummariesAsync(WorkspaceId, contextId, cancellationToken);
            return WorkspaceCommitTargetPlanner.Plan(mode, clickedTargets, GitChangesTreeBuilder.ViewFromSummaries(summaries).Repositories);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Could not re-read the Changes state before a workspace commit; committing the repositories of the click.");
            job.Terminal.Append("commit", WorkerCommandStreamKind.Stderr, "Could not re-read the repositories before committing; committing the ones selected when the commit was started.");
            return new WorkspaceCommitTargetPlan(clickedTargets, [], []);
        }
    }

    private void StageAllChangedAsync() => BulkSectionActionAsync(unstageStagedSection: false);

    private void UnstageAllStagedAsync() => BulkSectionActionAsync(unstageStagedSection: true);

    /// <summary>Stage all items in the Changed section, or unstage all items in the Staged section, across
    /// every repository represented in that section. Same bounded fan-out idiom as <see cref="CommitWorkspaceAsync"/>,
    /// sharing the same <see cref="GitChangesOptions.MaxParallelRepositoryOperations"/> limit, also behind
    /// the page's LoadingOverlay/terminal job since it spans every repository in the section.</summary>
    private void BulkSectionActionAsync(bool unstageStagedSection)
    {
        if (!WorkerBridge.IsWorkerConnected)
        {
            ToastService.ShowError("Worker not connected. Start the GrayMoon Worker and try again.");
            return;
        }

        var inSection = (_view?.Repositories ?? [])
            .Where(r => unstageStagedSection ? r.StagedCount > 0 : r.ChangedCount > 0)
            .ToList();

        // Across several repositories a read-only one is left out. Alone in the section it is the repository the
        // user means, and its own stage/unstage action does the same.
        var leftOut = inSection.Count > 1 ? inSection.Where(r => r.IsReference).ToList() : [];
        var targets = inSection.Except(leftOut).ToList();

        if (IsJobRunning)
        {
            return;
        }

        if (leftOut.Count > 0)
        {
            ToastService.Show(leftOut.Count == 1
                ? $"{leftOut[0].RepositoryName} is read only and was left out."
                : $"{leftOut.Count} read only repositories were left out.");
        }

        if (targets.Count == 0)
        {
            return;
        }

        var label = unstageStagedSection
            ? $"Unstaging {targets.Count} repositories..."
            : $"Staging {targets.Count} repositories...";

        var contextId = RequireSelectedContextId();
        StartPageJob(label, async (job, ct) =>
        {
            var outcomes = new RepositoryOutcomes(targets.Select(t => (t.WorkspaceRepositoryId, t.RepositoryName)));
            var completed = 0;
            using var semaphore = new SemaphoreSlim(Math.Max(1, GitChangesOptions.Value.MaxParallelRepositoryOperations));

            var tasks = targets.Select(async repo =>
            {
                try
                {
                    await semaphore.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    outcomes.Failed(repo.WorkspaceRepositoryId, repo.RepositoryName, "Cancelled before it started.");
                    throw;
                }

                try
                {
                    var resolved = await ResolveRepositoryAsync(repo.WorkspaceRepositoryId);
                    if (resolved == null)
                    {
                        outcomes.Failed(repo.WorkspaceRepositoryId, repo.RepositoryName, "Repository not found or workspace root is not configured.");
                        return;
                    }

                    var result = unstageStagedSection
                        ? await GitChangesOperations.UnstageAsync(WorkspaceId, contextId, resolved.Value.RepositoryId, GitChangeOperationScope.Repository, [], ct)
                        : await GitChangesOperations.StageAsync(WorkspaceId, contextId, resolved.Value.RepositoryId, GitChangeOperationScope.Repository, [], ct);

                    await PersistMutationResultAsync(repo.WorkspaceRepositoryId, resolved.Value.RepositoryId, result.Success, result.Snapshot, result.ErrorMessage, reload: false, showError: false);

                    if (result.Success)
                        outcomes.Succeeded(repo.WorkspaceRepositoryId, repo.RepositoryName);
                    else
                        outcomes.Failed(repo.WorkspaceRepositoryId, repo.RepositoryName, result.ErrorMessage ?? "Operation failed.");
                }
                catch (OperationCanceledException)
                {
                    outcomes.Failed(repo.WorkspaceRepositoryId, repo.RepositoryName, "Cancelled.");
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Bulk section {Action} failed for repository {RepositoryName}", unstageStagedSection ? "unstage" : "stage", repo.RepositoryName);
                    outcomes.Failed(repo.WorkspaceRepositoryId, repo.RepositoryName, $"Unexpected error ({ex.GetType().Name}) - see logs.");
                }
                finally
                {
                    semaphore.Release();
                    var done = Interlocked.Increment(ref completed);
                    job.ReportProgress(unstageStagedSection
                        ? $"Unstaged {done} of {targets.Count} repositories..."
                        : $"Staged {done} of {targets.Count} repositories...");
                }
            });

            await Task.WhenAll(tasks);

            ReportBulkOutcomes(job, outcomes, unstageStagedSection ? "Unstaged" : "Staged", unstageStagedSection ? "Not unstaged" : "Not staged");
        });
    }

    /// <summary>
    /// Names every repository a multi-repository stage/unstage did not handle, with the reason, in the log, the job
    /// terminal and an error toast. Success stays quiet (the lists update by themselves). Without this a repository whose
    /// call failed was only visible in the log, and a later Commit Staged then skipped it because nothing was staged there.
    /// </summary>
    private void ReportBulkOutcomes(BackgroundJobHandle job, RepositoryOutcomes outcomes, string pastVerb, string notVerb)
    {
        var results = outcomes.Complete();
        var failedCount = RepositoryOutcomes.Count(results, RepositoryOutcomeKind.Failed);
        var summary = RepositoryOutcomes.Summarize(results, pastVerb, notVerb);
        Logger.LogInformation(
            "Workspace {Verb} finished: Requested={Requested}, Succeeded={Succeeded}, Failed={Failed}. {Summary}",
            pastVerb, results.Count, RepositoryOutcomes.Count(results, RepositoryOutcomeKind.Succeeded), failedCount, summary);

        if (failedCount == 0)
        {
            return;
        }

        foreach (var outcome in results.Where(r => r.Kind == RepositoryOutcomeKind.Failed))
        {
            job.Terminal.Append("stage", WorkerCommandStreamKind.Stderr, $"{outcome.Repository}: {notVerb.ToLowerInvariant()} - {outcome.Detail}");
        }

        SafeInvoke(() => ToastService.ShowError(summary));
    }
}
