using GrayMoon.App.Data;
using GrayMoon.App.Hubs;
using GrayMoon.App.Desktop;
using GrayMoon.App.Models;
using GrayMoon.App.Repositories;
using GrayMoon.App.Services;
using GrayMoon.App.Services.Ai;
using GrayMoon.App.Services.Ci;
using GrayMoon.App.Services.Worker;
using GrayMoon.App.Services.Connectors;
using GrayMoon.App.Services.Git;
using GrayMoon.App.Services.GitChanges;
using GrayMoon.App.Services.GitHub;
using GrayMoon.App.Services.Jobs;
using GrayMoon.App.Services.Orchestration;
using GrayMoon.App.Services.Queries;
using GrayMoon.App.Services.Security;
using GrayMoon.App.Services.Ui;
using GrayMoon.App.Services.Features;
using GrayMoon.App.Services.Workspaces;
using GrayMoon.Application.Ai;
using GrayMoon.Application.Features;
using GrayMoon.Application.Workspaces;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Common.Manifest;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace GrayMoon.App;

/// <summary>
/// Every service registration of the App, kept out of Program.cs so a test can build the same container
/// with scope and build validation on (see ServiceContainerConventionTests).
/// </summary>
public static class GrayMoonServiceRegistration
{
    /// <param name="connectionString">The SQLite connection string after Program.cs added its defaults.</param>
    public static IServiceCollection AddGrayMoonServices(this IServiceCollection services, IConfiguration configuration, string connectionString)
    {
        services.Configure<WorkspaceOptions>(configuration.GetSection("Workspace"));
        services.Configure<GitChangesOptions>(configuration.GetSection("GitChanges"));
        // Legacy section name (before the Agent -> Worker rename) is applied first so an existing override keeps
        // working; the current section wins when both are set.
        services.Configure<WorkerBridgeOptions>(configuration.GetSection(WorkerBridgeOptions.LegacySectionName));
        services.Configure<WorkerBridgeOptions>(configuration.GetSection(WorkerBridgeOptions.SectionName));
        services.Configure<SecurityOptions>(configuration.GetSection(SecurityOptions.SectionName));
        services.Configure<AiGenerationOptions>(configuration.GetSection(AiGenerationOptions.SectionName));

        // Add services to the container.
        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // Default SignalR incoming message limit is 32KB. SyncRepository ResponseCommand carries branch lists
        // plus full .csproj/package graphs; larger repos exceed that and the server closes the connection,
        // which surfaces on the worker as HubException during InvokeAsync (not a git failure).
        // SyncCommand no longer awaits worker responses inline - it enqueues to WorkerSyncNotificationQueue and
        // returns immediately - so the default MaximumParallelInvocationsPerClient = 1 is sufficient.
        services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = 10 * 1024 * 1024;
        });

        // AddDbContext + AddDbContextFactory together fails on EF Core 10 (scoped
        // IDbContextOptionsConfiguration resolved from the root provider). Register the
        // factory only, then supply scoped AppDbContext instances from it.
        // busy_timeout and foreign_keys are per connection; the interceptor sets them on every open.
        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseSqlite(connectionString).AddInterceptors(SqliteConnectionPragmasInterceptor.Instance));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());
        services.AddScoped<ConnectorRepository>();
        services.AddScoped<GitHubRepositoryRepository>();
        services.AddScoped<WorkspaceProjectRepository>();
        services.AddScoped<WorkspaceFileRepository>();
        services.AddScoped<WorkspaceFileVersionConfigRepository>();
        services.AddScoped<WorkspaceRepositoryCustomDependencyRepository>();
        services.AddScoped<WorkspaceRepository>();
        services.AddScoped<AppSettingRepository>();
        services.AddScoped<NavbarCollapseService>();
        services.AddSingleton<DesktopTopBarState>();
        services.AddSingleton<WorkerConnectionTracker>();
        services.AddSingleton<WorkerUpgradeStartupPrompt>();
        services.AddScoped<HostPrerequisiteInstallService>();
        services.AddScoped<HostPrerequisiteRequirementsProvider>();
        services.AddScoped<WorkerInstallService>();
        services.AddScoped<WorkerRestartService>();
        services.AddHostedService<WorkerUpdateDesktopNotifier>();
        services.AddSingleton<WorkerQueueStateService>();
        services.AddSingleton<WorkerCommandCancelSender>();
        services.AddSingleton<OverlayCommandTerminalService>();
        services.AddScoped<IToastService, ToastService>();
        services.AddSingleton<MatrixOverlayPreferenceService>();
        services.AddSingleton<CommandTerminalOverlayPreferenceService>();
        services.AddSingleton<MergePullRequestReturnToDefaultPreferenceService>();
        services.AddSingleton<LoadingOverlayUiSettingsService>();
        services.AddSingleton<IGitHubRateLimitTracker, GitHubRateLimitTracker>();
        services.AddSingleton<IGitHubETagCache, GitHubETagCache>();
        services.AddSingleton<IGhaLiveFeedJobsCache, GhaLiveFeedJobsCache>();
        services.AddSingleton<IGitHubApiUsageRecorder, GitHubApiUsageRecorder>();
        services.AddHostedService<GitHubApiUsageLoggerBackgroundService>();
        services.AddScoped<SyncCommandHandler>();
        services.AddScoped<IWorkerBridge, WorkerBridge>();
        services.AddScoped<WorkspaceService>();
        services.AddScoped<IWorkspaceFeatureContextResolver, WorkspaceFeatureContextResolver>();
        services.AddScoped<IWorkspaceCapabilitiesResolver, WorkspaceCapabilitiesResolver>();
        services.AddScoped<IWorkspacePageAccessResolver, WorkspacePageAccessResolver>();
        services.AddScoped<IWorkspaceContextPathResolver, WorkspaceContextPathResolver>();
        services.AddScoped<IWorkspaceSelectedFeatureContextService, WorkspaceSelectedFeatureContextService>();
        services.AddScoped<IWorkspaceHookContextAttributor, WorkspaceHookContextAttributor>();
        services.AddScoped<FeatureCodeGraphService>();
        services.AddScoped<WorkspaceCodeGraphActivation>();
        services.AddScoped<IWorkspaceFeatureOperations, WorkspaceFeatureOperations>();
        services.AddSingleton<IWorkspaceFeatureReconciler, WorkspaceFeatureReconciler>();
        services.AddHostedService(sp => (WorkspaceFeatureReconciler)sp.GetRequiredService<IWorkspaceFeatureReconciler>());
        services.AddSingleton<FeatureRemovalQueue>();
        services.AddSingleton<IFeatureRemovalQueue>(sp => sp.GetRequiredService<FeatureRemovalQueue>());
        services.AddHostedService(sp => sp.GetRequiredService<FeatureRemovalQueue>());
        services.AddSingleton<IWorkspaceCheckoutRefresh, WorkspaceCheckoutRefresh>();
        services.AddScoped<IFeatureRemovalDeparture, FeatureRemovalDeparture>();
        services.AddSingleton<FeatureFolderCleanupService>();
        services.AddSingleton<IFeatureFolderCleanupService>(sp => sp.GetRequiredService<FeatureFolderCleanupService>());
        services.AddHostedService(sp => sp.GetRequiredService<FeatureFolderCleanupService>());
        services.AddScoped<WorkspaceContextNavigationService>();
        services.AddScoped<IWorkspaceExternalWorktreeOperations, WorkspaceExternalWorktreeOperations>();
        services.AddScoped<IWorkspaceNativeLaunchService, WorkspaceNativeLaunchService>();
        services.AddHttpContextAccessor();
        services.AddScoped<WorkspaceOpenInRecentTools>();
        services.AddScoped<IWorkspaceBranchOccupancyService, WorkspaceBranchOccupancyService>();
        services.AddScoped<IFeatureBranchGuard, FeatureBranchGuard>();
        services.AddScoped<WorkspaceGitService>();
        services.AddScoped<ConnectorHealthService>();
        services.AddScoped<HomeNavAttentionMonitor>();
        services.AddScoped<GitHubRepositoryService>();
        services.AddScoped<IRepositoryListQueryService, RepositoryListQueryService>();
        services.AddScoped<IWorkspaceProjectListQueryService, WorkspaceProjectListQueryService>();
        services.AddScoped<IWorkspaceRepositoryLinkListQueryService, WorkspaceRepositoryLinkListQueryService>();
        services.AddMemoryCache();
        services.AddScoped<GitHubActionsService>();
        services.AddScoped<GhaWorkflowLiveFeedService>();
        services.AddScoped<GitHubPullRequestService>();
        services.AddScoped<GrayMoon.App.Services.GitProviders.GitHubGitProvider>();
        services.AddScoped<GrayMoon.App.Services.GitProviders.IGitProviderResolver, GrayMoon.App.Services.GitProviders.GitProviderResolver>();
        services.AddScoped<GitHubPullRequestMergeService>();
        services.AddScoped<WorkspacePullRequestRepository>();
        services.AddScoped<WorkspacePullRequestService>();
        services.AddScoped<RepositoryBranchWriter>();
        services.AddScoped<WorkspaceRepositoryStateWriter>();
        services.AddSingleton<GrayMoon.Application.Repositories.Policy.IWorkspaceRepositoryOperationPolicy, GrayMoon.App.Services.Policy.WorkspaceRepositoryOperationPolicyService>();
        services.AddScoped<GrayMoon.App.Services.Policy.ReferenceActivationChecker>();
        services.AddScoped<GrayMoon.App.Services.Policy.ReferenceImportGuard>();
        services.AddSingleton<GrayMoon.App.Services.Policy.ReferenceRevisionRecorder>();
        services.AddScoped<GrayMoon.App.Services.Policy.IRepositoryArchiveProbe, GrayMoon.App.Services.Policy.GitHubRepositoryArchiveProbe>();
        services.AddScoped<GrayMoon.App.Services.Policy.ArchivedRepositoryVerifier>();
        services.AddScoped<WorkspaceStateRecomputeScope>();
        services.AddSingleton<SyncRecomputeCoalescer>();
        services.AddSingleton<FileVersionCheckCoalescer>();
        services.AddSingleton<FeatureFinalizationCoordinator>();
        services.AddScoped<WorkspaceActionRepository>();
        services.AddScoped<WorkspaceActionService>();
        services.AddScoped<GitHubActionsCiProvider>();
        services.AddScoped<IWorkspaceCiProviderResolver, WorkspaceCiProviderResolver>();
        services.AddScoped<PackageRegistrySyncService>();
        services.AddScoped<IWorkspaceFileSearchService, WorkspaceFileSearchService>();
        services.AddScoped<WorkspaceFileVersionService>();
        services.AddScoped<WorkspaceCommitSyncHandler>();
        services.AddScoped<WorkspaceBranchUpdateHandler>();
        services.AddScoped<WorkspaceSyncHandler>();
        services.AddScoped<DependencyUpdateOrchestrator>();
        services.AddScoped<IDependencyUpdateLane>(sp => sp.GetRequiredService<DependencyUpdateOrchestrator>());
        services.AddScoped<UpdateAndPushOrchestrator>();
        services.AddScoped<WorkspaceUpdateHandler>();
        services.AddScoped<PushOrchestrator>();
        services.AddScoped<WorkspacePushService>();
        services.AddScoped<IPushLane>(sp => sp.GetRequiredService<WorkspacePushService>());
        services.AddScoped<WorkspacePushHandler>();
        services.AddScoped<BasicGitPushStrategy>();
        services.AddScoped<DotNetDependencyPushStrategy>();
        services.AddScoped<WorkspacePushStrategySelector>();
        services.AddScoped<WorkspaceUndoPushHandler>();
        services.AddScoped<WorkspaceDependencyService>();
        services.AddScoped<WorkspacePendingActionsService>();
        services.AddScoped<AppActivityStateService>();
        services.AddScoped<IWorkspaceBranchOperations, WorkspaceBranchOperations>();
        services.AddScoped<IWorkspaceCatalogOperations, WorkspaceCatalogOperations>();
        services.AddScoped<IWorkspacePushOperations, WorkspacePushOperations>();
        services.AddScoped<IWorkspaceUpdateOperations, WorkspaceUpdateOperations>();
        services.AddScoped<IWorkspaceUpdateAndPushOperations, WorkspaceUpdateAndPushOperations>();
        services.AddScoped<IWorkspacePreparationOperations, WorkspacePreparationOperations>();
        services.AddScoped<IWorkspaceSyncOperations, WorkspaceSyncOperations>();
        services.AddScoped<IWorkspacePullRequestOperations, WorkspacePullRequestOperations>();
        services.AddScoped<IWorkspaceFileOperations, WorkspaceFileOperations>();
        services.AddScoped<IWorkspaceGitChangesOperations, WorkspaceGitChangesOperations>();
        services.AddSingleton<HostInfoCache>();
        services.AddSingleton<AiProviderAvailabilityCache>();
        services.AddScoped<WorkspaceAiProviderPins>();
        services.AddScoped<IWorkspaceAiGenerationOperations, WorkspaceAiGenerationOperations>();
        services.AddSingleton<IWorkspaceManifestFile, WorkspaceManifestFile>();
        services.AddScoped<GrayMoon.Application.WorkspaceManifest.IWorkspaceManifestService, GrayMoon.App.Services.WorkspaceManifest.WorkspaceManifestService>();
        services.AddScoped<GrayMoon.App.Services.WorkspaceManifest.IRemoteWorkspaceManifestReader, GrayMoon.App.Services.WorkspaceManifest.GitHubRemoteWorkspaceManifestReader>();
        services.AddScoped<IWorkspaceRepositoryOperations, GrayMoon.App.Services.Application.WorkspaceRepositoryOperations>();
        services.AddScoped<IWorkspaceReferenceOperations, GrayMoon.App.Services.Application.WorkspaceReferenceOperations>();
        services.AddScoped<WorkspaceBranchHandler>();
        services.AddScoped<IPrepareWorkspacePhases, PrepareWorkspacePhases>();
        services.AddScoped<PrepareWorkspaceOrchestrator>();
        services.AddSingleton<IWorkspaceOperationRunner, WorkspaceOperationRunner>();
        services.AddSingleton<IWorkspaceOperationLock>(sp => (IWorkspaceOperationLock)sp.GetRequiredService<IWorkspaceOperationRunner>());
        services.AddScoped<IBackgroundJobService, BackgroundJobService>();
        services.AddScoped<IWorkspacePageService, WorkspacePageService>();
        services.AddScoped<IWorkspaceTopBarService, WorkspaceTopBarService>();
        services.AddSingleton<DesktopWorkspaceContextTracker>();
        services.AddSingleton<WorkspaceOperationOutcomeStore>();
        services.AddSingleton<UpdateAndPushApprovalStore>();

        services.AddScoped<IWorkspaceGitChangesReadService, WorkspaceGitChangesReadService>();
        services.AddScoped<IGitChangesWorkerClient, GitChangesWorkerClient>();
        services.AddSingleton<MarkdownProseDiffService>();
        services.AddScoped<MarkdownImageEmbedder>();
        services.AddHttpClient(nameof(MarkdownImageEmbedder))
            .ConfigureHttpClient(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("GrayMoon-MarkdownPreview/1.0");
            });

        services.AddScoped<GitChangesSnapshotPushHandler>();
        services.AddSingleton<IWorkspaceGitChangesNotifier, WorkspaceGitChangesNotifier>();
        services.AddScoped<WorkspaceGitChangesSelectionMemory>();
        services.AddSingleton<IGitChangesFileOrdering, TreeGitChangesFileOrdering>();
        services.AddSingleton<WorkspaceGitChangesCommitMessageMemory>();
        services.AddSingleton<WorkspaceGitChangesPushAfterCommitMemory>();
        services.AddSingleton<WorkspaceGitChangesGenerateMessageOnCommitMemory>();
        services.AddSingleton<WorkspaceGitChangesCommitGeneration>();
        services.AddSingleton<WorkspacePullRequestDraftMemory>();
        services.AddSingleton<WorkspacePullRequestTextGeneration>();
        services.AddSingleton<IWorkspaceGitChangesActivityTracker, WorkspaceGitChangesActivityTracker>();
        services.AddSingleton<IWorkspaceGitChangesMonitoringPause, WorkspaceGitChangesMonitoringPause>();
        services.AddSingleton<IGitChangesWorkspaceScanner, GitChangesWorkspaceScanner>();
        services.AddSingleton<IGitChangesLineStatsRefresh, GitChangesLineStatsRefresh>();
        services.AddScoped<IWorkspaceGitChangesActivation, WorkspaceGitChangesActivation>();
        services.AddScoped<WorkspaceGitChangesRouteActivity>();

        services.AddSingleton<ICommandLineService, CommandLineService>();
        services.AddSingleton<IScopedServiceExecutor, ScopedServiceExecutor>();

        // Token protection
        services.AddSingleton<ITokenEncryptionKeyProvider, TokenEncryptionKeyProvider>();
        services.AddSingleton<ITokenProtector, AesGcmTokenProtector>();

        // Worker secret (F2) and one-time pairing codes
        services.AddSingleton<IWorkerSecretSeenStore, DbWorkerSecretSeenStore>();
        services.AddSingleton<WorkerSecretService>();
        services.AddSingleton<WorkerPairingService>();

        // Background services
        services.AddSingleton<SyncBackgroundService>();
        services.AddHostedService(sp => sp.GetRequiredService<SyncBackgroundService>());
        services.AddSingleton<WorkerSyncNotificationQueue>();
        services.AddHostedService(sp => sp.GetRequiredService<WorkerSyncNotificationQueue>());
        services.AddSingleton<IWorkerSyncNotificationBarrier>(sp => sp.GetRequiredService<WorkerSyncNotificationQueue>());
        services.AddHostedService<TokenHealthBackgroundService>();
        services.AddSingleton<WorkspaceGitChangesWriteQueue>();
        services.AddHostedService(sp => sp.GetRequiredService<WorkspaceGitChangesWriteQueue>());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IGitChangesContextEnsurer, GitChangesContextEnsurer>();
        services.AddSingleton<GitChangesAuditScheduler>();
        services.AddSingleton<IGitChangesAuditScheduler>(sp => sp.GetRequiredService<GitChangesAuditScheduler>());
        services.AddHostedService(sp => sp.GetRequiredService<GitChangesAuditScheduler>());
        services.AddHostedService<GitChangesWatcherKeeper>();

        // Connector services
        services.AddTransient<GitHubOverlayLoggingHandler>();
        services.AddHttpClient<GitHubService>()
            .AddHttpMessageHandler<GitHubOverlayLoggingHandler>();
        services.AddHttpClient<NuGetService>();
        services.AddScoped<ConnectorServiceFactory>();
        services.AddScoped<IPullRequestService, PullRequestService>();

        // Persist Data Protection keys to db volume so antiforgery and other protected data survive container restarts
        var keyRingDir = Path.Combine(Path.GetDirectoryName(DatabasePathResolver.GetDatabasePath(connectionString) ?? "db") ?? "db", "DataProtection-Keys");
        services.AddDataProtection()
            .SetApplicationName("GrayMoon")
            .PersistKeysToFileSystem(new DirectoryInfo(keyRingDir));

        return services;
    }
}
