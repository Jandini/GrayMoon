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
            throw new ArgumentException($"Args required for {command}");

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
            "GetWorkspaceRoot" => JsonSerializer.Deserialize<GetWorkspaceRootRequest>(json, options)
                ?? throw new ArgumentException("Invalid GetWorkspaceRoot args"),
            "SyncRepositoryDependencies" => JsonSerializer.Deserialize<SyncRepositoryDependenciesRequest>(json, options)
                ?? throw new ArgumentException("Invalid SyncRepositoryDependencies args"),
            "RefreshRepositoryProjects" => JsonSerializer.Deserialize<RefreshRepositoryProjectsRequest>(json, options)
                ?? throw new ArgumentException("Invalid RefreshRepositoryProjects args"),
            _ => throw new NotSupportedException($"Unknown command: {command}")
        };
    }
}
