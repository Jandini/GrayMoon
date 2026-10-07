using System.Text.Json.Serialization;
using GrayMoon.Worker.Models;

namespace GrayMoon.Worker.Jobs.Response;

/// <summary>One process keeping a path in use (wire shape of <see cref="BlockingProcessInfo"/>).</summary>
public sealed class BlockingProcessResponse
{
    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("processName")]
    public string? ProcessName { get; set; }

    [JsonPropertyName("executablePath")]
    public string? ExecutablePath { get; set; }

    [JsonPropertyName("serviceName")]
    public string? ServiceName { get; set; }

    /// <summary><see cref="BlockingProcessKind"/> name: Unknown, Application, Service, Explorer, Console or Critical.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    /// <summary><see cref="BlockingProcessReason"/> name: OpenFile or WorkingDirectory.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    public static List<BlockingProcessResponse> From(IReadOnlyList<BlockingProcessInfo> processes) =>
        processes.Select(p => new BlockingProcessResponse
        {
            ProcessId = p.ProcessId,
            ProcessName = p.ProcessName,
            ExecutablePath = p.ExecutablePath,
            ServiceName = p.ServiceName,
            Kind = p.Kind.ToString(),
            Reason = p.Reason.ToString(),
        }).ToList();
}
