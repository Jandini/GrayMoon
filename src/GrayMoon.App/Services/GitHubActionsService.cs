using System.Text.RegularExpressions;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;

namespace GrayMoon.App.Services;

public class GitHubActionsService(
    ConnectorRepository connectorRepository,
    GitHubRepositoryService repositoryService,
    GitHubService gitHubService,
    ILogger<GitHubActionsService> logger)
{
    public async Task<List<GitHubActionEntry>> GetLatestActionsAsync()
    {
        var results = new List<GitHubActionEntry>();
        var connectors = await connectorRepository.GetAllAsync();
        var repositories = await repositoryService.GetRepositoriesAsync();

        foreach (var connector in connectors)
        {
            var connectorRepos = repositories
                .Where(repo => repo.ConnectorName == connector.ConnectorName)
                .ToList();

            foreach (var repository in connectorRepos)
            {
                if (string.IsNullOrWhiteSpace(repository.OrgName) || string.IsNullOrWhiteSpace(repository.RepositoryName))
                {
                    continue;
                }

                try
                {
                    var run = await gitHubService.GetLatestWorkflowRunAsync(
                        connector,
                        repository.OrgName,
                        repository.RepositoryName);

                    if (run == null)
                    {
                        continue;
                    }

                    results.Add(new GitHubActionEntry
                    {
                        RunId = run.Id,
                        WorkflowId = run.WorkflowId,
                        ConnectorName = connector.ConnectorName,
                        Owner = repository.OrgName ?? string.Empty,
                        RepositoryName = repository.RepositoryName,
                        WorkflowName = run.Name,
                        Event = run.Event,
                        Status = run.Status,
                        Conclusion = run.Conclusion,
                        UpdatedAt = run.UpdatedAt,
                        HtmlUrl = run.HtmlUrl,
                        HeadBranch = run.HeadBranch
                    });
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error loading latest action for {Owner}/{Repo}", repository.OrgName, repository.RepositoryName);
                }
            }
        }

        return results;
    }

    public async Task<GitHubActionEntry?> GetLatestActionAsync(GitHubRepositoryEntry repository)
    {
        if (string.IsNullOrWhiteSpace(repository.RepositoryName) || string.IsNullOrWhiteSpace(repository.OrgName))
        {
            return null;
        }

        var connector = await connectorRepository.GetByNameAsync(repository.ConnectorName);
        if (connector == null)
        {
            logger.LogWarning("Connector {ConnectorName} not found for actions.", repository.ConnectorName);
            return null;
        }

        try
        {
            var run = await gitHubService.GetLatestWorkflowRunAsync(
                connector,
                repository.OrgName,
                repository.RepositoryName);

            if (run == null)
            {
                return null;
            }

            return new GitHubActionEntry
            {
                RunId = run.Id,
                WorkflowId = run.WorkflowId,
                ConnectorName = connector.ConnectorName,
                Owner = repository.OrgName ?? string.Empty,
                RepositoryName = repository.RepositoryName,
                WorkflowName = run.Name,
                Event = run.Event,
                Status = run.Status,
                Conclusion = run.Conclusion,
                UpdatedAt = run.UpdatedAt,
                HtmlUrl = run.HtmlUrl,
                HeadBranch = run.HeadBranch
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading latest action for {Owner}/{Repo}", repository.OrgName, repository.RepositoryName);
            return null;
        }
    }

    /// <summary>
    /// Fetches active workflows and the latest run per workflow for <paramref name="branch"/>.
    /// Each entry is independent (not cross-workflow aggregate).
    /// </summary>
    public async Task<IReadOnlyList<ActionStatusInfo>?> GetWorkflowStatusesForBranchAsync(GitHubRepositoryEntry repository, string branch, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository.OrgName) || string.IsNullOrWhiteSpace(repository.RepositoryName))
            return null;

        if (string.IsNullOrWhiteSpace(branch))
            return [new ActionStatusInfo { Status = "none", BranchName = branch }];

        var connector = await connectorRepository.GetByNameAsync(repository.ConnectorName);
        if (connector == null)
        {
            logger.LogWarning("Connector {ConnectorName} not found for workflow statuses.", repository.ConnectorName);
            return null;
        }

        var owner = repository.OrgName!;
        var repoName = repository.RepositoryName;

        var workflows = (await gitHubService.GetWorkflowsAsync(connector, owner, repoName, cancellationToken))
            .Where(w => string.Equals(w.State, "active", StringComparison.OrdinalIgnoreCase))
            .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var runs = await gitHubService.GetWorkflowRunsForBranchAsync(
            connector, owner, repoName, branch, perPage: 100);
        var latestByWorkflowId = runs
            .GroupBy(r => r.WorkflowId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.UpdatedAt).First());

        var result = new List<ActionStatusInfo>();
        if (workflows.Count > 0)
        {
            var dispatchPairs = await Task.WhenAll(workflows.Select(async wf =>
            {
                var yaml = await gitHubService.GetRepositoryFileUtf8TextAsync(connector, owner, repoName, wf.Path, cancellationToken);
                return (wf.Id, Supports: YamlAppearsToHaveWorkflowDispatch(yaml));
            }));
            var dispatchById = dispatchPairs.ToDictionary(x => x.Id, x => x.Supports);

            foreach (var wf in workflows)
            {
                latestByWorkflowId.TryGetValue(wf.Id, out var run);
                var supportsDispatch = dispatchById.GetValueOrDefault(wf.Id, false);
                result.Add(ToPerWorkflowActionStatus(branch, wf.Id, wf.Name, run, supportsDispatch));
            }
        }
        else
        {
            foreach (var run in latestByWorkflowId.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                var meta = await gitHubService.GetWorkflowByIdAsync(connector, owner, repoName, run.WorkflowId, cancellationToken);
                var yaml = string.IsNullOrWhiteSpace(meta?.Path)
                    ? null
                    : await gitHubService.GetRepositoryFileUtf8TextAsync(connector, owner, repoName, meta.Path, cancellationToken);
                var supportsDispatch = YamlAppearsToHaveWorkflowDispatch(yaml);
                result.Add(ToPerWorkflowActionStatus(branch, run.WorkflowId, run.Name, run, supportsDispatch));
            }
        }

        return result;
    }

    /// <summary>Detects a <c>workflow_dispatch</c> trigger in workflow YAML without a full parser.</summary>
    private static bool YamlAppearsToHaveWorkflowDispatch(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return false;
        return WorkflowDispatchTriggerRegex.IsMatch(yaml);
    }

    private static readonly Regex WorkflowDispatchTriggerRegex = new(@"\bworkflow_dispatch\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static ActionStatusInfo ToPerWorkflowActionStatus(string branch, long workflowId, string workflowName, GitHubWorkflowRunDto? run, bool supportsWorkflowDispatch)
    {
        if (run == null)
        {
            return new ActionStatusInfo
            {
                Status = "none",
                BranchName = branch,
                WorkflowId = workflowId,
                WorkflowName = workflowName,
                SupportsWorkflowDispatch = supportsWorkflowDispatch
            };
        }

        string status;
        if (IsFailureConclusion(run.Conclusion))
            status = "failed";
        else if (!string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase))
            status = "running";
        else
            status = "success";

        var displayName = string.IsNullOrWhiteSpace(run.Name) ? workflowName : run.Name;
        return new ActionStatusInfo
        {
            Status = status,
            HtmlUrl = run.HtmlUrl,
            UpdatedAt = run.UpdatedAt,
            BranchName = branch,
            RunId = run.Id,
            WorkflowId = workflowId,
            WorkflowName = displayName,
            SupportsWorkflowDispatch = supportsWorkflowDispatch
        };
    }

    private static bool IsFailureConclusion(string? conclusion) =>
        string.Equals(conclusion, "failure", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(conclusion, "timed_out", StringComparison.OrdinalIgnoreCase);

    public async Task RerunWorkflowAsync(GitHubActionEntry action)
    {
        if (action.RunId <= 0)
        {
            throw new InvalidOperationException("Workflow run id is not available.");
        }

        if (string.IsNullOrWhiteSpace(action.Owner) || string.IsNullOrWhiteSpace(action.RepositoryName))
        {
            throw new InvalidOperationException("Workflow owner and repository are required.");
        }

        var connector = await connectorRepository.GetByNameAsync(action.ConnectorName);
        if (connector == null)
        {
            logger.LogWarning("Connector {ConnectorName} not found for rerun.", action.ConnectorName);
            throw new InvalidOperationException("Connector not found for this action.");
        }

        await gitHubService.RerunWorkflowRunAsync(connector, action.Owner, action.RepositoryName, action.RunId);
    }

    public async Task RunWorkflowAsync(GitHubActionEntry action)
    {
        if (action.WorkflowId <= 0)
        {
            throw new InvalidOperationException("Workflow id is not available.");
        }

        if (string.IsNullOrWhiteSpace(action.Owner) || string.IsNullOrWhiteSpace(action.RepositoryName))
        {
            throw new InvalidOperationException("Workflow owner and repository are required.");
        }

        if (string.IsNullOrWhiteSpace(action.HeadBranch))
        {
            throw new InvalidOperationException("Workflow branch is not available.");
        }

        var connector = await connectorRepository.GetByNameAsync(action.ConnectorName);
        if (connector == null)
        {
            logger.LogWarning("Connector {ConnectorName} not found for run.", action.ConnectorName);
            throw new InvalidOperationException("Connector not found for this action.");
        }

        await gitHubService.DispatchWorkflowAsync(
            connector,
            action.Owner,
            action.RepositoryName,
            action.WorkflowId,
            action.HeadBranch);
    }
}
