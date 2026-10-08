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

    /// <summary><see cref="BlockingProcessReason"/> name: OpenFile, WorkingDirectory or LoadedModule.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>When the process started (UTC); sent back unchanged with a kill request so a reused process id is never ended.</summary>
    [JsonPropertyName("startTimeUtc")]
    public DateTime? StartTimeUtc { get; set; }

    /// <summary>True when GrayMoon may end the process after the user confirms.</summary>
    [JsonPropertyName("canTerminate")]
    public bool CanTerminate { get; set; }

    /// <summary><see cref="BlockingProcessProtectedReason"/> value when <see cref="CanTerminate"/> is false.</summary>
    [JsonPropertyName("protectedReason")]
    public string? ProtectedReason { get; set; }

    public static List<BlockingProcessResponse> From(IReadOnlyList<BlockingProcessInfo> processes) =>
        processes.Select(p => new BlockingProcessResponse
        {
            ProcessId = p.ProcessId,
            ProcessName = p.ProcessName,
            ExecutablePath = p.ExecutablePath,
            ServiceName = p.ServiceName,
            Kind = p.Kind.ToString(),
            Reason = p.Reason.ToString(),
            StartTimeUtc = p.StartTimeUtc,
            CanTerminate = p.CanTerminate,
            ProtectedReason = p.ProtectedReason,
        }).ToList();

    public BlockingProcessInfo ToInfo() => new(
        ProcessId,
        ProcessName,
        ExecutablePath,
        ServiceName,
        Enum.TryParse<BlockingProcessKind>(Kind, out var kind) ? kind : BlockingProcessKind.Unknown,
        Enum.TryParse<BlockingProcessReason>(Reason, out var reason) ? reason : BlockingProcessReason.OpenFile,
        StartTimeUtc,
        CanTerminate,
        ProtectedReason);
}
