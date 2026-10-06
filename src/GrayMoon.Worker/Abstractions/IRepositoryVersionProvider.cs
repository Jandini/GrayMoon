using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Worker.Models;

namespace GrayMoon.Worker.Abstractions;

/// <summary>
/// The one place the worker asks for a repository's version. Every GitVersion call site goes through an
/// implementation of this, so no command has to know what a workspace profile is.
/// </summary>
public interface IRepositoryVersionProvider
{
    /// <summary>
    /// Resolves the version of <paramref name="repoPath"/>. An implementation that does not version
    /// anything returns <see cref="RepositoryVersionResult.NotProbed"/> rather than a fake version, so the
    /// app leaves whatever version it already has alone.
    /// </summary>
    Task<RepositoryVersionResult> GetVersionAsync(
        string repoPath,
        RepositoryVersionOptions options,
        CancellationToken ct = default);
}

/// <summary>Per-call knobs for <see cref="IRepositoryVersionProvider.GetVersionAsync"/>.</summary>
public sealed class RepositoryVersionOptions
{
    /// <summary>Run the provider without normalizing the repository, for flows that already ensured fetch ordering.</summary>
    public bool NonNormalize { get; init; }

    /// <summary>Resolve the version at a specific commit rather than at HEAD.</summary>
    public string? CommitSha { get; init; }

    /// <summary>What every call looked like before workspace profiles existed.</summary>
    public static RepositoryVersionOptions Default { get; } = new();
}

/// <summary>
/// What a version provider produced. <see cref="Probed"/> is the distinction the app's state writer needs:
/// a provider that ran and failed clears the stored version, a provider that never ran must not.
/// </summary>
public sealed record RepositoryVersionResult(bool Probed, GitVersionResult? Result, string? Error)
{
    /// <summary>No version provider ran for this repository, so nothing is known either way.</summary>
    public static RepositoryVersionResult NotProbed { get; } = new(false, null, null);

    public string? InformationalVersion => Result?.InformationalVersion;

    /// <summary>The worker's wire placeholder for "no version". It is not the domain model; the app maps it back to null.</summary>
    public string VersionOrPlaceholder => InformationalVersion ?? "-";
}

/// <summary>Picks the version provider a request's capabilities ask for.</summary>
public interface IRepositoryVersionProviderFactory
{
    /// <summary>
    /// Returns the GitVersion-backed provider when versioning is active, otherwise the no-op one. A null
    /// <paramref name="capabilities"/> means "not stated" and keeps the pre-profile GitVersion behaviour.
    /// </summary>
    IRepositoryVersionProvider Create(RepositoryOperationCapabilities? capabilities);
}
