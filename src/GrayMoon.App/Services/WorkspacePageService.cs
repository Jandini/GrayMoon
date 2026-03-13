using System.Net.Http;
using GrayMoon.App.Repositories;

namespace GrayMoon.App.Services;

/// <summary>
/// Facade for services used by the workspace repositories page and its handlers.
/// Aggregates the underlying services into a single injected dependency.
/// </summary>
public interface IWorkspacePageService
{
    WorkspaceRepository WorkspaceRepository { get; }
    WorkspaceGitService WorkspaceGitService { get; }
    WorkspaceFileVersionService FileVersionService { get; }
    WorkspacePullRequestService WorkspacePullRequestService { get; }
    GitHubPullRequestService GitHubPullRequestService { get; }
    ConnectorRepository ConnectorRepository { get; }
    GitHubRepositoryService RepositoryService { get; }
    IHttpClientFactory HttpClientFactory { get; }
}

public sealed class WorkspacePageService(
    WorkspaceRepository workspaceRepository,
    WorkspaceGitService workspaceGitService,
    WorkspaceFileVersionService fileVersionService,
    WorkspacePullRequestService workspacePullRequestService,
    GitHubPullRequestService gitHubPullRequestService,
    ConnectorRepository connectorRepository,
    GitHubRepositoryService repositoryService,
    IHttpClientFactory httpClientFactory) : IWorkspacePageService
{
    public WorkspaceRepository WorkspaceRepository { get; } = workspaceRepository;
    public WorkspaceGitService WorkspaceGitService { get; } = workspaceGitService;
    public WorkspaceFileVersionService FileVersionService { get; } = fileVersionService;
    public WorkspacePullRequestService WorkspacePullRequestService { get; } = workspacePullRequestService;
    public GitHubPullRequestService GitHubPullRequestService { get; } = gitHubPullRequestService;
    public ConnectorRepository ConnectorRepository { get; } = connectorRepository;
    public GitHubRepositoryService RepositoryService { get; } = repositoryService;
    public IHttpClientFactory HttpClientFactory { get; } = httpClientFactory;
}

