using System.Text.Json.Serialization;

namespace GrayMoon.Worker.Jobs.Requests;

/// <summary>
/// Background cleanup: deletes every Feature folder marked pending deletion directly under one Feature storage root.
/// </summary>
public sealed class SweepPendingFeatureFoldersRequest
{
    /// <summary>The Workspace's Feature storage root, ending in <c>&lt;Workspace&gt;\features</c>.</summary>
    [JsonPropertyName("featureStorageRoot")]
    public string? FeatureStorageRoot { get; set; }

    /// <summary>Workspace name, for logs and a rewritten marker; optional.</summary>
    [JsonPropertyName("workspaceName")]
    public string? WorkspaceName { get; set; }

    /// <summary>Names of the Features that still exist (as typed, with <c>/</c>); their folders are never touched even when marked.</summary>
    [JsonPropertyName("excludeFeatureNames")]
    public List<string>? ExcludeFeatureNames { get; set; }
}
