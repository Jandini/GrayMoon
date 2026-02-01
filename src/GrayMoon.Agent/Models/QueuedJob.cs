using System.Text.Json;

namespace GrayMoon.Agent.Models;

/// <summary>
/// A job enqueued from either SignalR RequestCommand or HTTP /notify.
/// </summary>
public sealed class QueuedJob
{
    /// <summary>Present for command jobs; null for notify jobs.</summary>
    public string? RequestId { get; init; }

    /// <summary>Command name (e.g. SyncRepository, RefreshRepositoryVersion). For notify jobs, use "NotifySync".</summary>
    public required string Command { get; init; }

    /// <summary>Command arguments (for command jobs).</summary>
    public JsonElement? Args { get; init; }

    /// <summary>For NotifySync: repository ID.</summary>
    public int? RepositoryId { get; init; }

    /// <summary>For NotifySync: workspace ID.</summary>
    public int? WorkspaceId { get; init; }

    /// <summary>For NotifySync: full path to the repository.</summary>
    public string? RepositoryPath { get; init; }

    public bool IsNotify => Command == "NotifySync";
}
