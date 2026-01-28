using GrayMoon.App.Models;
using GrayMoon.App.Repositories;

namespace GrayMoon.App.Services;

public class GitHubActionsService
{
    private readonly GitHubConnectorRepository _connectorRepository;
    private readonly GitHubRepositoryService _repositoryService;
    private readonly GitHubService _gitHubService;
    private readonly ILogger<GitHubActionsService> _logger;

    public GitHubActionsService(
        GitHubConnectorRepository connectorRepository,
        GitHubRepositoryService repositoryService,
        GitHubService gitHubService,
        ILogger<GitHubActionsService> logger)
    {
        _connectorRepository = connectorRepository;
        _repositoryService = repositoryService;
        _gitHubService = gitHubService;
        _logger = logger;
    }

    public async Task<List<GitHubActionEntry>> GetLatestActionsAsync()
    {
        var results = new List<GitHubActionEntry>();
        var connectors = await _connectorRepository.GetAllAsync();
        var repositories = await _repositoryService.GetRepositoriesAsync();

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
                    var run = await _gitHubService.GetLatestWorkflowRunAsync(
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
                    _logger.LogError(ex, "Error loading latest action for {Owner}/{Repo}", repository.OrgName, repository.RepositoryName);
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

        var connector = await _connectorRepository.GetByNameAsync(repository.ConnectorName);
        if (connector == null)
        {
            _logger.LogWarning("Connector {ConnectorName} not found for actions.", repository.ConnectorName);
            return null;
        }

        try
        {
            var run = await _gitHubService.GetLatestWorkflowRunAsync(
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
            _logger.LogError(ex, "Error loading latest action for {Owner}/{Repo}", repository.OrgName, repository.RepositoryName);
            return null;
        }
    }

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

        var connector = await _connectorRepository.GetByNameAsync(action.ConnectorName);
        if (connector == null)
        {
            _logger.LogWarning("Connector {ConnectorName} not found for rerun.", action.ConnectorName);
            throw new InvalidOperationException("Connector not found for this action.");
        }

        await _gitHubService.RerunWorkflowRunAsync(connector, action.Owner, action.RepositoryName, action.RunId);
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

        var connector = await _connectorRepository.GetByNameAsync(action.ConnectorName);
        if (connector == null)
        {
            _logger.LogWarning("Connector {ConnectorName} not found for run.", action.ConnectorName);
            throw new InvalidOperationException("Connector not found for this action.");
        }

        await _gitHubService.DispatchWorkflowAsync(
            connector,
            action.Owner,
            action.RepositoryName,
            action.WorkflowId,
            action.HeadBranch);
    }
}
