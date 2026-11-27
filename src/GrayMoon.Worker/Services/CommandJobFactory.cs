using System.Text.Json;
using GrayMoon.Abstractions.Worker;
using GrayMoon.Worker.Jobs;
using GrayMoon.Worker.Jobs.Requests;

namespace GrayMoon.Worker.Services;

/// <summary>
/// Builds typed ICommandJob from RequestCommand (requestId, command, args). Deserializes JSON at the edge only.
/// </summary>
public sealed class CommandJobFactory
{
    public JobEnvelope CreateCommandJob(string requestId, string command, JsonElement? args, string? operationId = null)
    {
        var request = DeserializeRequest(command, args);
        var commandJob = new CommandJob
        {
            RequestId = requestId,
            Command = command,
            Request = request,
            OperationId = string.IsNullOrEmpty(operationId) ? null : operationId
        };
        return JobEnvelope.Command(commandJob);
    }

    private static object DeserializeRequest(string command, JsonElement? args)
    {
        if (args == null || args.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (command == "GetHostInfo")
                return new GetHostInfoRequest();
            if (command == WorkerHubMethods.SelfUpdate)
                return new SelfUpdateRequest();
            throw new ArgumentException($"Args required for {command}");
        }

        var json = args.Value.GetRawText();

        return command switch
        {
            "SyncRepository" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.SyncRepositoryRequest)
                ?? throw new ArgumentException("Invalid SyncRepository args"),
            "RefreshRepositoryVersion" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.RefreshRepositoryVersionRequest)
                ?? throw new ArgumentException("Invalid RefreshRepositoryVersion args"),
            "GetCommitCounts" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.GetCommitCountsRequest)
                ?? throw new ArgumentException("Invalid GetCommitCounts args"),
            "EnsureWorkspace" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.EnsureWorkspaceRequest)
                ?? throw new ArgumentException("Invalid EnsureWorkspace args"),
            "GetWorkspaceRepositories" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.GetWorkspaceRepositoriesRequest)
                ?? throw new ArgumentException("Invalid GetWorkspaceRepositories args"),
            "GetRepositoryVersion" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.GetRepositoryVersionRequest)
                ?? throw new ArgumentException("Invalid GetRepositoryVersion args"),
            WorkerHubMethods.GetGitVersionAtBranchPoint => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.GetGitVersionAtBranchPointRequest)
                ?? throw new ArgumentException("Invalid GetGitVersionAtBranchPoint args"),
            "GetGitVersionAtDefaultTip" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.GetGitVersionAtDefaultTipRequest)
                ?? throw new ArgumentException("Invalid GetGitVersionAtDefaultTip args"),
            "GetWorkspaceExists" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.GetWorkspaceExistsRequest)
                ?? throw new ArgumentException("Invalid GetWorkspaceExists args"),
            "GetHostInfo" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.GetHostInfoRequest) ?? new GetHostInfoRequest(),
            "SyncRepositoryDependencies" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.SyncRepositoryDependenciesRequest)
                ?? throw new ArgumentException("Invalid SyncRepositoryDependencies args"),
            "RefreshRepositoryProjects" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.RefreshRepositoryProjectsRequest)
                ?? throw new ArgumentException("Invalid RefreshRepositoryProjects args"),
            "CommitSyncRepository" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.CommitSyncRepositoryRequest)
                ?? throw new ArgumentException("Invalid CommitSyncRepository args"),
            "GetBranches" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.GetBranchesRequest)
                ?? throw new ArgumentException("Invalid GetBranches args"),
            "CheckoutBranch" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.CheckoutBranchRequest)
                ?? throw new ArgumentException("Invalid CheckoutBranch args"),
            "CheckoutTag" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.CheckoutTagRequest)
                ?? throw new ArgumentException("Invalid CheckoutTag args"),
            "ReturnToDefaultBranch" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.ReturnToDefaultBranchRequest)
                ?? throw new ArgumentException("Invalid ReturnToDefaultBranch args"),
            "RefreshBranches" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.RefreshBranchesRequest)
                ?? throw new ArgumentException("Invalid RefreshBranches args"),
            "CreateBranch" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.CreateBranchRequest)
                ?? throw new ArgumentException("Invalid CreateBranch args"),
            "SetUpstreamBranch" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.SetUpstreamBranchRequest)
                ?? throw new ArgumentException("Invalid SetUpstreamBranch args"),
            "DeleteBranch" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.DeleteBranchRequest)
                ?? throw new ArgumentException("Invalid DeleteBranch args"),
            "StageAndCommit" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.StageAndCommitRequest)
                ?? throw new ArgumentException("Invalid StageAndCommit args"),
            "PushRepository" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.PushRepositoryRequest)
                ?? throw new ArgumentException("Invalid PushRepository args"),
            "SearchFiles" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.SearchFilesRequest)
                ?? throw new ArgumentException("Invalid SearchFiles args"),
            "UpdateFileVersions" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.UpdateFileVersionsRequest)
                ?? throw new ArgumentException("Invalid UpdateFileVersions args"),
            "GetFileContents" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.GetFileContentsRequest)
                ?? throw new ArgumentException("Invalid GetFileContents args"),
            "ValidatePath" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.ValidatePathRequest)
                ?? throw new ArgumentException("Invalid ValidatePath args"),
            "DotnetRestore" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.DotnetRestoreRequest)
                ?? throw new ArgumentException("Invalid DotnetRestore args"),
            "UndoPush" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.UndoPushRequest)
                ?? throw new ArgumentException("Invalid UndoPush args"),
            WorkerHubMethods.SelfUpdate => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.SelfUpdateRequest)
                ?? throw new ArgumentException("Invalid SelfUpdate args"),
            WorkerHubMethods.CheckFileVersions => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.CheckFileVersionsRequest)
                ?? throw new ArgumentException("Invalid CheckFileVersions args"),
            WorkerHubMethods.GetHeadCommits => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.GetHeadCommitsRequest)
                ?? throw new ArgumentException("Invalid GetHeadCommits args"),
            WorkerHubMethods.ResolveGeneratedPackageReferences => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.ResolveGeneratedPackageReferencesRequest)
                ?? throw new ArgumentException("Invalid ResolveGeneratedPackageReferences args"),
            "UpdateBranchFromDefault" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.UpdateBranchFromDefaultRequest)
                ?? throw new ArgumentException("Invalid UpdateBranchFromDefault args"),
            "FetchCommits" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.FetchCommitsRequest)
                ?? throw new ArgumentException("Invalid FetchCommits args"),
"EnsureGitChangesWatchers" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.EnsureGitChangesWatchersRequest)
    ?? throw new ArgumentException("Invalid EnsureGitChangesWatchers args"),
            "GetGitChangeStatus" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.GetGitChangeStatusRequest)
                ?? throw new ArgumentException("Invalid GetGitChangeStatus args"),
            "GetGitFileDiff" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.GetGitFileDiffRequest)
                ?? throw new ArgumentException("Invalid GetGitFileDiff args"),
            "StageGitChanges" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.StageGitChangesRequest)
                ?? throw new ArgumentException("Invalid StageGitChanges args"),
            "UnstageGitChanges" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.UnstageGitChangesRequest)
                ?? throw new ArgumentException("Invalid UnstageGitChanges args"),
            "DiscardGitChanges" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.DiscardGitChangesRequest)
                ?? throw new ArgumentException("Invalid DiscardGitChanges args"),
            "CommitGitChanges" => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.CommitGitChangesRequest)
                ?? throw new ArgumentException("Invalid CommitGitChanges args"),
            WorkerHubMethods.ListGitWorktrees => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.ListGitWorktreesRequest)
                ?? throw new ArgumentException("Invalid ListGitWorktrees args"),
            WorkerHubMethods.CreateGitWorktree => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.CreateGitWorktreeRequest)
                ?? throw new ArgumentException("Invalid CreateGitWorktree args"),
            WorkerHubMethods.RemoveGitWorktree => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.RemoveGitWorktreeRequest)
                ?? throw new ArgumentException("Invalid RemoveGitWorktree args"),
            WorkerHubMethods.InspectWorktree => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.InspectWorktreeRequest)
                ?? throw new ArgumentException("Invalid InspectWorktree args"),
            WorkerHubMethods.CleanupFeatureFolder => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.CleanupFeatureFolderRequest)
                ?? throw new ArgumentException("Invalid CleanupFeatureFolder args"),
            WorkerHubMethods.SweepPendingFeatureFolders => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.SweepPendingFeatureFoldersRequest)
                ?? throw new ArgumentException("Invalid SweepPendingFeatureFolders args"),
            WorkerHubMethods.HoldWorktreeHookSyncs => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.HoldWorktreeHookSyncsRequest)
                ?? throw new ArgumentException("Invalid HoldWorktreeHookSyncs args"),
            WorkerHubMethods.AttachWorkspaceRepository => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.AttachWorkspaceRepositoryRequest)
                ?? throw new ArgumentException("Invalid AttachWorkspaceRepository args"),
            WorkerHubMethods.DiscardWorkspaceRoot => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.DiscardWorkspaceRootRequest)
                ?? throw new ArgumentException("Invalid DiscardWorkspaceRoot args"),
            WorkerHubMethods.WriteRepositoryFile => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.WriteRepositoryFileRequest)
                ?? throw new ArgumentException("Invalid WriteRepositoryFile args"),
            WorkerHubMethods.InitCodeGraph => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.InitCodeGraphRequest)
                ?? throw new ArgumentException("Invalid InitCodeGraph args"),
            WorkerHubMethods.UninitCodeGraph => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.UninitCodeGraphRequest)
                ?? throw new ArgumentException("Invalid UninitCodeGraph args"),
            WorkerHubMethods.WatchCodeGraphInit => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.WatchCodeGraphInitRequest)
                ?? throw new ArgumentException("Invalid WatchCodeGraphInit args"),
            WorkerHubMethods.CollectGitEvidence => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.CollectGitEvidenceRequest)
                ?? throw new ArgumentException("Invalid CollectGitEvidence args"),
            WorkerHubMethods.GetAiProviders => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.GetAiProvidersRequest)
                ?? throw new ArgumentException("Invalid GetAiProviders args"),
            WorkerHubMethods.GenerateAiText => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.GenerateAiTextRequest)
                ?? throw new ArgumentException("Invalid GenerateAiText args"),
            WorkerHubMethods.DetachRepositories => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.DetachRepositoriesRequest)
                ?? throw new ArgumentException("Invalid DetachRepositories args"),
            WorkerHubMethods.GetGitChangesMonitoring => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.GetGitChangesMonitoringRequest)
                ?? throw new ArgumentException("Invalid GetGitChangesMonitoring args"),
            WorkerHubMethods.ListWorkspaceRootFolders => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.ListWorkspaceRootFoldersRequest)
                ?? throw new ArgumentException("Invalid ListWorkspaceRootFolders args"),
            WorkerHubMethods.InspectWorkspaceFolder => JsonSerializer.Deserialize(json, WorkerJsonContext.Default.InspectWorkspaceFolderRequest)
                ?? throw new ArgumentException("Invalid InspectWorkspaceFolder args"),
            _ => throw new NotSupportedException($"Unknown command: {command}")
        };
    }
}
