using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Worker.Abstractions;

namespace GrayMoon.Worker.Services;

/// <summary>GitVersion-backed version provider. The only thing in the worker that launches GitVersion.</summary>
/// <remarks>
/// With a <paramref name="cache"/> a request whose version inputs are unchanged since GitVersion last succeeded
/// is answered without starting GitVersion. Without one (as in most tests) every request runs it.
/// </remarks>
public sealed class GitVersionRepositoryVersionProvider(IGitService git, GitVersionResultCache? cache = null) : IRepositoryVersionProvider
{
    public Task<RepositoryVersionResult> GetVersionAsync(
        string repoPath,
        RepositoryVersionOptions options,
        CancellationToken ct = default)
        => cache is null
            ? RunAsync(repoPath, options, ct)
            : cache.GetOrRunAsync(repoPath, options, token => RunAsync(repoPath, options, token), ct);

    private async Task<RepositoryVersionResult> RunAsync(string repoPath, RepositoryVersionOptions options, CancellationToken ct)
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
