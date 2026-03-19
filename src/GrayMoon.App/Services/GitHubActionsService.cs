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
    /// Fetches all recent workflow runs for the given branch and returns an aggregated status.
    /// Groups by workflow, takes the latest run per workflow, then returns:
    /// "failed" if any has a failure conclusion, "running" if any is still in progress,
    /// "success" if all completed successfully, or "none" if no runs found.
    /// </summary>
    public async Task<ActionStatusInfo?> GetAggregateActionStatusForBranchAsync(GitHubRepositoryEntry repository, string branch)
    {
        if (string.IsNullOrWhiteSpace(repository.OrgName) || string.IsNullOrWhiteSpace(repository.RepositoryName))
            return null;

        if (string.IsNullOrWhiteSpace(branch))
            return new ActionStatusInfo { Status = "none" };

        var connector = await connectorRepository.GetByNameAsync(repository.ConnectorName);
        if (connector == null)
        {
            logger.LogWarning("Connector {ConnectorName} not found for aggregate actions.", repository.ConnectorName);
            return null;
        }

        var runs = await gitHubService.GetWorkflowRunsForBranchAsync(connector, repository.OrgName, repository.RepositoryName, branch);
        if (runs.Count == 0)
            return new ActionStatusInfo { Status = "none", BranchName = branch };

        // Take the latest run per workflow to avoid stale duplicates counting against aggregate
        var latestPerWorkflow = runs
            .GroupBy(r => r.WorkflowId)
            .Select(g => g.OrderByDescending(r => r.UpdatedAt).First())
            .ToList();

        var failedRun = latestPerWorkflow
            .Where(r => IsFailureConclusion(r.Conclusion))
            .OrderByDescending(r => r.UpdatedAt)
            .FirstOrDefault();

        var runningRun = latestPerWorkflow
            .Where(r => !string.Equals(r.Status, "completed", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.UpdatedAt)
            .FirstOrDefault();

        var latestRun = latestPerWorkflow.OrderByDescending(r => r.UpdatedAt).First();

        string status;
        GitHubWorkflowRunDto primaryRun;

        if (failedRun != null)
        {
            status = "failed";
            primaryRun = failedRun;
        }
        else if (runningRun != null)
        {
            status = "running";
            primaryRun = runningRun;
        }
        else
        {
            status = "success";
            primaryRun = latestRun;
        }

        return new ActionStatusInfo
        {
            Status = status,
            HtmlUrl = primaryRun.HtmlUrl,
            UpdatedAt = primaryRun.UpdatedAt,
            BranchName = branch,
            RunId = primaryRun.Id,
            WorkflowId = primaryRun.WorkflowId,
            WorkflowName = primaryRun.Name
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
