using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GrayMoon.Abstractions.Worker;

namespace GrayMoon.Worker.Services;

public static class WorkerJsonOptions
{
    /// <summary>Options of command requests (camelCase, case-insensitive). The types come from <see cref="WorkerJsonContext"/>.</summary>
    public static readonly JsonSerializerOptions SerializerOptions = WorkerJsonContext.Default.Options;

    /// <summary>
    /// The JSON style of the hub connection (camelCase, nulls written), with the types of <see cref="WorkerJsonContext"/>.
    /// Used to write a hub payload as text outside the connection: the size log and the trace log.
    /// </summary>
    public static readonly JsonSerializerOptions HubPayload = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        TypeInfoResolver = WorkerJsonContext.Default
    };

    /// <summary>Applies the hub's JSON style and the Worker's generated types to the connection's own options.</summary>
    public static void ConfigureHubPayload(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        options.TypeInfoResolverChain.Insert(0, WorkerJsonContext.Default);
    }

    /// <summary>The response as the JSON text the hub sends for it.</summary>
    public static string SerializeResponse(WorkerCommandResponse response) =>
        JsonSerializer.Serialize(response, (JsonTypeInfo<WorkerCommandResponse>)HubPayload.GetTypeInfo(typeof(WorkerCommandResponse)));
}
