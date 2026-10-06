using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Worker.Abstractions;

namespace GrayMoon.Worker.Services;

/// <summary>GitVersion-backed version provider. The only thing in the worker that launches GitVersion.</summary>
public sealed class GitVersionRepositoryVersionProvider(IGitService git) : IRepositoryVersionProvider
{
    public async Task<RepositoryVersionResult> GetVersionAsync(
        string repoPath,
        RepositoryVersionOptions options,
        CancellationToken ct = default)
    {
        var (result, error) = await git.GetVersionAsync(repoPath, options.NonNormalize, options.CommitSha, ct);
        return new RepositoryVersionResult(Probed: true, result, error);
    }
}

/// <summary>
/// Version provider for a workspace that does not version its repositories. It reports "not probed" rather
/// than an empty version, so the app leaves the persisted version exactly as it is instead of clearing it.
/// </summary>
public sealed class NoRepositoryVersionProvider : IRepositoryVersionProvider
{
    public Task<RepositoryVersionResult> GetVersionAsync(
        string repoPath,
        RepositoryVersionOptions options,
        CancellationToken ct = default)
        => Task.FromResult(RepositoryVersionResult.NotProbed);
}

/// <inheritdoc cref="IRepositoryVersionProviderFactory" />
public sealed class RepositoryVersionProviderFactory(
    GitVersionRepositoryVersionProvider gitVersionProvider,
    NoRepositoryVersionProvider noVersionProvider) : IRepositoryVersionProviderFactory
{
    public IRepositoryVersionProvider Create(RepositoryOperationCapabilities? capabilities)
        => (capabilities ?? RepositoryOperationCapabilities.LegacyFullEnrichment).ShouldCalculateVersion
            ? gitVersionProvider
            : noVersionProvider;
}
