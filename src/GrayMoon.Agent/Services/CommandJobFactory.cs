using System.Text.Json;
using GrayMoon.Agent.Jobs;
using GrayMoon.Agent.Jobs.Requests;

namespace GrayMoon.Agent.Services;

/// <summary>
/// Builds typed ICommandJob from RequestCommand (requestId, command, args). Deserializes JSON at the edge only.
/// </summary>
public sealed class CommandJobFactory
{
    public JobEnvelope CreateCommandJob(string requestId, string command, JsonElement? args)
    {
        var request = DeserializeRequest(command, args);
        var commandJob = new CommandJob { RequestId = requestId, Command = command, Request = request };
        return JobEnvelope.Command(commandJob);
    }

    private static object DeserializeRequest(string command, JsonElement? args)
    {
        if (args == null || args.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (command == "GetHostInfo")
                return new GetHostInfoRequest();
            throw new ArgumentException($"Args required for {command}");
        }

        var json = args.Value.GetRawText();
        var options = AgentJsonOptions.SerializerOptions;

        return command switch
        {
            "SyncRepository" => JsonSerializer.Deserialize<SyncRepositoryRequest>(json, options)
                ?? throw new ArgumentException("Invalid SyncRepository args"),
            "RefreshRepositoryVersion" => JsonSerializer.Deserialize<RefreshRepositoryVersionRequest>(json, options)
                ?? throw new ArgumentException("Invalid RefreshRepositoryVersion args"),
            "EnsureWorkspace" => JsonSerializer.Deserialize<EnsureWorkspaceRequest>(json, options)
                ?? throw new ArgumentException("Invalid EnsureWorkspace args"),
            "GetWorkspaceRepositories" => JsonSerializer.Deserialize<GetWorkspaceRepositoriesRequest>(json, options)
                ?? throw new ArgumentException("Invalid GetWorkspaceRepositories args"),
            "GetRepositoryVersion" => JsonSerializer.Deserialize<GetRepositoryVersionRequest>(json, options)
                ?? throw new ArgumentException("Invalid GetRepositoryVersion args"),
            "GetWorkspaceExists" => JsonSerializer.Deserialize<GetWorkspaceExistsRequest>(json, options)
                ?? throw new ArgumentException("Invalid GetWorkspaceExists args"),
            "GetHostInfo" => JsonSerializer.Deserialize<GetHostInfoRequest>(json, options) ?? new GetHostInfoRequest(),
            "SyncRepositoryDependencies" => JsonSerializer.Deserialize<SyncRepositoryDependenciesRequest>(json, options)
                ?? throw new ArgumentException("Invalid SyncRepositoryDependencies args"),
            "RefreshRepositoryProjects" => JsonSerializer.Deserialize<RefreshRepositoryProjectsRequest>(json, options)
                ?? throw new ArgumentException("Invalid RefreshRepositoryProjects args"),
            "CommitSyncRepository" => JsonSerializer.Deserialize<CommitSyncRepositoryRequest>(json, options)
                ?? throw new ArgumentException("Invalid CommitSyncRepository args"),
            "GetBranches" => JsonSerializer.Deserialize<GetBranchesRequest>(json, options)
                ?? throw new ArgumentException("Invalid GetBranches args"),
            "CheckoutBranch" => JsonSerializer.Deserialize<CheckoutBranchRequest>(json, options)
                ?? throw new ArgumentException("Invalid CheckoutBranch args"),
            "SyncToDefaultBranch" => JsonSerializer.Deserialize<SyncToDefaultBranchRequest>(json, options)
                ?? throw new ArgumentException("Invalid SyncToDefaultBranch args"),
            "RefreshBranches" => JsonSerializer.Deserialize<RefreshBranchesRequest>(json, options)
                ?? throw new ArgumentException("Invalid RefreshBranches args"),
            "CreateBranch" => JsonSerializer.Deserialize<CreateBranchRequest>(json, options)
                ?? throw new ArgumentException("Invalid CreateBranch args"),
            "StageAndCommit" => JsonSerializer.Deserialize<StageAndCommitRequest>(json, options)
                ?? throw new ArgumentException("Invalid StageAndCommit args"),
            "PushRepository" => JsonSerializer.Deserialize<PushRepositoryRequest>(json, options)
                ?? throw new ArgumentException("Invalid PushRepository args"),
            "SearchFiles" => JsonSerializer.Deserialize<SearchFilesRequest>(json, options)
                ?? throw new ArgumentException("Invalid SearchFiles args"),
            "UpdateFileVersions" => JsonSerializer.Deserialize<UpdateFileVersionsRequest>(json, options)
                ?? throw new ArgumentException("Invalid UpdateFileVersions args"),
            "GetFileContents" => JsonSerializer.Deserialize<GetFileContentsRequest>(json, options)
                ?? throw new ArgumentException("Invalid GetFileContents args"),
            _ => throw new NotSupportedException($"Unknown command: {command}")
        };
    }
}
