using System.Collections.Concurrent;
using GrayMoon.Abstractions.Agent;
using GrayMoon.Abstractions.Exceptions;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Models;
using GrayMoon.App.Models.Api;
using GrayMoon.App.Repositories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App.Services.Git;

public sealed partial class WorkspaceGitService(
    IAgentBridge agentBridge,
    WorkspaceService workspaceService,
    WorkspaceRepository workspaceRepository,
    GitHubRepositoryRepository repositoryRepository,
    WorkspaceProjectRepository workspaceProjectRepository,
    WorkspaceDependencyService workspaceDependencyService,
    WorkspacePullRequestService workspacePullRequestService,
    RepositoryBranchWriter branchWriter,
    WorkspaceRepositoryStateWriter stateWriter,
    WorkspaceStateRecomputeScope recomputeScope,
    AppDbContext dbContext,
    Microsoft.Extensions.Options.IOptions<WorkspaceOptions> workspaceOptions,
    ILogger<WorkspaceGitService> logger,
    IHubContext<WorkspaceSyncHub>? hubContext = null,
    PackageRegistrySyncService? packageRegistrySyncService = null,
    NuGetService? nuGetService = null,
    ConnectorRepository? connectorRepository = null,
    ConnectorHealthService? connectorHealthService = null,
    WorkspaceFileVersionService? fileVersionService = null)
{
    private readonly IAgentBridge _agentBridge = agentBridge ?? throw new ArgumentNullException(nameof(agentBridge));
    private readonly WorkspaceService _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
    private readonly WorkspaceRepository _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
    private readonly GitHubRepositoryRepository _repositoryRepository = repositoryRepository ?? throw new ArgumentNullException(nameof(repositoryRepository));
    private readonly WorkspaceProjectRepository _workspaceProjectRepository = workspaceProjectRepository ?? throw new ArgumentNullException(nameof(workspaceProjectRepository));
    private readonly WorkspaceDependencyService _workspaceDependencyService = workspaceDependencyService ?? throw new ArgumentNullException(nameof(workspaceDependencyService));
    private readonly WorkspacePullRequestService _workspacePullRequestService = workspacePullRequestService ?? throw new ArgumentNullException(nameof(workspacePullRequestService));
    private readonly RepositoryBranchWriter _branchWriter = branchWriter ?? throw new ArgumentNullException(nameof(branchWriter));
    private readonly WorkspaceRepositoryStateWriter _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
    private readonly WorkspaceStateRecomputeScope _recomputeScope = recomputeScope ?? throw new ArgumentNullException(nameof(recomputeScope));
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly ILogger<WorkspaceGitService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly int _maxConcurrent = Math.Max(1, workspaceOptions?.Value?.MaxParallelOperations ?? 16);
    private readonly IHubContext<WorkspaceSyncHub>? _hubContext = hubContext;
    private readonly PackageRegistrySyncService? _packageRegistrySyncService = packageRegistrySyncService;
    private readonly NuGetService? _nuGetService = nuGetService;
    private readonly ConnectorRepository? _connectorRepository = connectorRepository;
    private readonly ConnectorHealthService? _connectorHealthService = connectorHealthService;
    private readonly WorkspaceFileVersionService? _fileVersionService = fileVersionService;
}
