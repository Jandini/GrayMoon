using System.Text.Json;
using System.Text.Json.Serialization;
using GrayMoon.Abstractions.Notifications;
using GrayMoon.Abstractions.Worker;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;

namespace GrayMoon.Worker.Services;

/// <summary>
/// Every type the Worker reads or writes as JSON: command requests and responses, the hub payloads, the hook
/// payload, GitVersion output and the App's HTTP answers. The Worker is published as Native AOT, where JSON by
/// reflection does not exist, so a type that is missing here fails at run time with NotSupportedException.
/// A new command adds its request and response type; <c>WorkerJsonContextTests</c> fails when one is missing.
/// Metadata mode only: the same context serves the hub's options and <see cref="WorkerJsonOptions"/>.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = false,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
// Hub arguments
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(JsonElement?))]
[JsonSerializable(typeof(Dictionary<int, int>))]
[JsonSerializable(typeof(WorkerCommandResponse))]
[JsonSerializable(typeof(RepositorySyncNotification))]
[JsonSerializable(typeof(GitChangesSnapshotNotification))]
// GitVersion output, App HTTP answers (the hook payload is in WorkerStrictJsonContext)

[JsonSerializable(typeof(GitVersionResult))]
[JsonSerializable(typeof(WorkerTokenProvider.ConnectorTokenResponse))]
[JsonSerializable(typeof(RepositoryOperationCapabilities))]
// Command requests and responses, in the order of CommandDispatcher
[JsonSerializable(typeof(SyncRepositoryRequest))]
[JsonSerializable(typeof(SyncRepositoryResponse))]
[JsonSerializable(typeof(RefreshRepositoryVersionRequest))]
[JsonSerializable(typeof(RefreshRepositoryVersionResponse))]
[JsonSerializable(typeof(GetCommitCountsRequest))]
[JsonSerializable(typeof(GetCommitCountsResponse))]
[JsonSerializable(typeof(EnsureWorkspaceRequest))]
[JsonSerializable(typeof(EnsureWorkspaceResponse))]
[JsonSerializable(typeof(GetWorkspaceRepositoriesRequest))]
[JsonSerializable(typeof(GetWorkspaceRepositoriesResponse))]
[JsonSerializable(typeof(GetRepositoryVersionRequest))]
[JsonSerializable(typeof(GetRepositoryVersionResponse))]
[JsonSerializable(typeof(GetWorkspaceExistsRequest))]
[JsonSerializable(typeof(GetWorkspaceExistsResponse))]
[JsonSerializable(typeof(GetHostInfoRequest))]
[JsonSerializable(typeof(GetHostInfoResponse))]
[JsonSerializable(typeof(SyncRepositoryDependenciesRequest))]
[JsonSerializable(typeof(SyncRepositoryDependenciesResponse))]
[JsonSerializable(typeof(RefreshRepositoryProjectsRequest))]
[JsonSerializable(typeof(RefreshRepositoryProjectsResponse))]
[JsonSerializable(typeof(CommitSyncRepositoryRequest))]
[JsonSerializable(typeof(CommitSyncRepositoryResponse))]
[JsonSerializable(typeof(GetBranchesRequest))]
[JsonSerializable(typeof(GetBranchesResponse))]
[JsonSerializable(typeof(CheckoutBranchRequest))]
[JsonSerializable(typeof(CheckoutBranchResponse))]
[JsonSerializable(typeof(CheckoutTagRequest))]
[JsonSerializable(typeof(CheckoutTagResponse))]
[JsonSerializable(typeof(ReturnToDefaultBranchRequest))]
[JsonSerializable(typeof(ReturnToDefaultBranchResponse))]
[JsonSerializable(typeof(RefreshBranchesRequest))]
[JsonSerializable(typeof(RefreshBranchesResponse))]
[JsonSerializable(typeof(CreateBranchRequest))]
[JsonSerializable(typeof(CreateBranchResponse))]
[JsonSerializable(typeof(SetUpstreamBranchRequest))]
[JsonSerializable(typeof(SetUpstreamBranchResponse))]
[JsonSerializable(typeof(DeleteBranchRequest))]
[JsonSerializable(typeof(DeleteBranchResponse))]
[JsonSerializable(typeof(StageAndCommitRequest))]
[JsonSerializable(typeof(StageAndCommitResponse))]
[JsonSerializable(typeof(PushRepositoryRequest))]
[JsonSerializable(typeof(PushRepositoryResponse))]
[JsonSerializable(typeof(SearchFilesRequest))]
[JsonSerializable(typeof(SearchFilesResponse))]
[JsonSerializable(typeof(UpdateFileVersionsRequest))]
[JsonSerializable(typeof(UpdateFileVersionsResponse))]
[JsonSerializable(typeof(GetFileContentsRequest))]
[JsonSerializable(typeof(GetFileContentsResponse))]
[JsonSerializable(typeof(ValidatePathRequest))]
[JsonSerializable(typeof(ValidatePathResponse))]
[JsonSerializable(typeof(DotnetRestoreRequest))]
[JsonSerializable(typeof(DotnetRestoreResponse))]
[JsonSerializable(typeof(UndoPushRequest))]
[JsonSerializable(typeof(UndoPushResponse))]
[JsonSerializable(typeof(SelfUpdateRequest))]
[JsonSerializable(typeof(SelfUpdateResponse))]
[JsonSerializable(typeof(CheckFileVersionsRequest))]
[JsonSerializable(typeof(CheckFileVersionsResponse))]
[JsonSerializable(typeof(GetHeadCommitsRequest))]
[JsonSerializable(typeof(GetHeadCommitsResponse))]
[JsonSerializable(typeof(ResolveGeneratedPackageReferencesRequest))]
[JsonSerializable(typeof(ResolveGeneratedPackageReferencesResponse))]
[JsonSerializable(typeof(UpdateBranchFromDefaultRequest))]
[JsonSerializable(typeof(UpdateBranchFromDefaultResponse))]
[JsonSerializable(typeof(FetchCommitsRequest))]
[JsonSerializable(typeof(FetchCommitsResponse))]
[JsonSerializable(typeof(GetGitChangeStatusRequest))]
[JsonSerializable(typeof(GetGitChangeStatusResponse))]
[JsonSerializable(typeof(EnsureGitChangesWatchersRequest))]
[JsonSerializable(typeof(EnsureGitChangesWatchersResponse))]
[JsonSerializable(typeof(GetGitFileDiffRequest))]
[JsonSerializable(typeof(GetGitFileDiffResponse))]
[JsonSerializable(typeof(StageGitChangesRequest))]
[JsonSerializable(typeof(GitMutationResponse))]
[JsonSerializable(typeof(UnstageGitChangesRequest))]
[JsonSerializable(typeof(GitMutationResponse))]
[JsonSerializable(typeof(DiscardGitChangesRequest))]
[JsonSerializable(typeof(GitMutationResponse))]
[JsonSerializable(typeof(CommitGitChangesRequest))]
[JsonSerializable(typeof(CommitGitChangesResponse))]
[JsonSerializable(typeof(ListGitWorktreesRequest))]
[JsonSerializable(typeof(ListGitWorktreesResponse))]
[JsonSerializable(typeof(CreateGitWorktreeRequest))]
[JsonSerializable(typeof(CreateGitWorktreeResponse))]
[JsonSerializable(typeof(RemoveGitWorktreeRequest))]
[JsonSerializable(typeof(RemoveGitWorktreeResponse))]
[JsonSerializable(typeof(GetGitVersionAtDefaultTipRequest))]
[JsonSerializable(typeof(GetGitVersionAtDefaultTipResponse))]
[JsonSerializable(typeof(GetGitVersionAtBranchPointRequest))]
[JsonSerializable(typeof(GetGitVersionAtBranchPointResponse))]
[JsonSerializable(typeof(InspectWorktreeRequest))]
[JsonSerializable(typeof(InspectWorktreeResponse))]
[JsonSerializable(typeof(CleanupFeatureFolderRequest))]
[JsonSerializable(typeof(CleanupFeatureFolderResponse))]
[JsonSerializable(typeof(SweepPendingFeatureFoldersRequest))]
[JsonSerializable(typeof(SweepPendingFeatureFoldersResponse))]
[JsonSerializable(typeof(HoldWorktreeHookSyncsRequest))]
[JsonSerializable(typeof(HoldWorktreeHookSyncsResponse))]
[JsonSerializable(typeof(AttachWorkspaceRepositoryRequest))]
[JsonSerializable(typeof(AttachWorkspaceRepositoryResponse))]
[JsonSerializable(typeof(DiscardWorkspaceRootRequest))]
[JsonSerializable(typeof(DiscardWorkspaceRootResponse))]
[JsonSerializable(typeof(WriteRepositoryFileRequest))]
[JsonSerializable(typeof(WriteRepositoryFileResponse))]
[JsonSerializable(typeof(InitCodeGraphRequest))]
[JsonSerializable(typeof(InitCodeGraphResponse))]
[JsonSerializable(typeof(UninitCodeGraphRequest))]
[JsonSerializable(typeof(UninitCodeGraphResponse))]
[JsonSerializable(typeof(WatchCodeGraphInitRequest))]
[JsonSerializable(typeof(WatchCodeGraphInitResponse))]
[JsonSerializable(typeof(CollectGitEvidenceRequest))]
[JsonSerializable(typeof(CollectGitEvidenceResponse))]
[JsonSerializable(typeof(GetAiProvidersRequest))]
[JsonSerializable(typeof(GetAiProvidersResponse))]
[JsonSerializable(typeof(GenerateAiTextRequest))]
[JsonSerializable(typeof(GenerateAiTextResponse))]
[JsonSerializable(typeof(DetachRepositoriesRequest))]
[JsonSerializable(typeof(DetachRepositoriesResponse))]
[JsonSerializable(typeof(GetGitChangesMonitoringRequest))]
[JsonSerializable(typeof(GetGitChangesMonitoringResponse))]
[JsonSerializable(typeof(ListWorkspaceRootFoldersRequest))]
[JsonSerializable(typeof(ListWorkspaceRootFoldersResponse))]
[JsonSerializable(typeof(InspectWorkspaceFolderRequest))]
[JsonSerializable(typeof(InspectWorkspaceFolderResponse))]
internal sealed partial class WorkerJsonContext : JsonSerializerContext;

/// <summary>
/// The hook listener's payload, read with the serializer's default options (exact property names), as before the
/// move to generated JSON: the listener takes requests from any local process.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(NotifyPayload))]
internal sealed partial class WorkerStrictJsonContext : JsonSerializerContext;
