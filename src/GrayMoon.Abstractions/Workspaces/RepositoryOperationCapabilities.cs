using System.Text.Json.Serialization;

namespace GrayMoon.Abstractions.Workspaces;

/// <summary>
/// The subset of a workspace's capabilities the Worker needs in order to run a repository operation:
/// which optional enrichment steps are active on top of pure Git synchronization.
/// </summary>
/// <remarks>
/// <para>
/// The Worker is told what to do; it never reads App persistence. On App-initiated commands this
/// travels on <c>WorkspaceCommandRequest</c>. On the one Worker-initiated path - git hooks, where the
/// developer's own git invocation is the trigger - the Worker resolves it through
/// <c>IWorkspaceCapabilityProvider</c>, which asks the App's API exactly as
/// <c>WorkerTokenProvider</c> already does for connector tokens.
/// </para>
/// <para>
/// Both flags are nullable so a payload from an App that predates this type deserialises to "not
/// stated", and the Worker then keeps its current full-enrichment behaviour rather than silently
/// skipping work for an existing .NET workspace. Use <see cref="LegacyFullEnrichment"/> for that
/// fallback.
/// </para>
/// </remarks>
public sealed class RepositoryOperationCapabilities
{
    /// <summary>Run the repository version provider (GitVersion) as part of this operation.</summary>
    [JsonPropertyName("calculateRepositoryVersion")]
    public bool? CalculateRepositoryVersion { get; set; }

    /// <summary>Scan the working tree for .NET projects and their package references.</summary>
    [JsonPropertyName("discoverDotNetProjects")]
    public bool? DiscoverDotNetProjects { get; set; }

    /// <summary>True when the version provider should run. Unstated means yes, preserving pre-profile behaviour.</summary>
    [JsonIgnore]
    public bool ShouldCalculateVersion => CalculateRepositoryVersion ?? true;

    /// <summary>True when the working tree should be scanned for projects. Unstated means yes, preserving pre-profile behaviour.</summary>
    [JsonIgnore]
    public bool ShouldDiscoverProjects => DiscoverDotNetProjects ?? true;

    /// <summary>
    /// What the Worker assumes when it cannot learn a workspace's capabilities: everything on, which is
    /// today's behaviour. Losing a known version because the App was briefly unreachable is worse than
    /// one wasted probe in a Basic workspace.
    /// </summary>
    public static RepositoryOperationCapabilities LegacyFullEnrichment => new()
    {
        CalculateRepositoryVersion = true,
        DiscoverDotNetProjects = true
    };

    public static RepositoryOperationCapabilities For(bool calculateVersion, bool discoverProjects) => new()
    {
        CalculateRepositoryVersion = calculateVersion,
        DiscoverDotNetProjects = discoverProjects
    };
}
