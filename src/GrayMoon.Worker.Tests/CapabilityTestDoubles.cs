using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Hand-written doubles for the capability seams. There is no mocking library in this repository, and the
/// point of these two is to count: the capability work is only meaningful if a skipped group launches no
/// process and reads no file at all, which is an assertion about calls not happening.
/// </summary>
internal static class CapabilityTestDoubles
{
    /// <summary>The production factory over a real <see cref="GitService"/>, wrapped so calls can be counted.</summary>
    public static CountingVersionProviderFactory RealFactory(IGitService git)
        => new(new RepositoryVersionProviderFactory(
            new GitVersionRepositoryVersionProvider(git),
            new NoRepositoryVersionProvider()));
}

/// <summary>
/// Delegates to the real factory but records how many times a version was actually asked for. Because
/// <see cref="GitVersionRepositoryVersionProvider"/> is the only thing in the worker that launches
/// GitVersion (and therefore the only thing that can trigger <c>dotnet tool restore</c>), a count of zero
/// here is a count of zero GitVersion processes and zero tool restores.
/// </summary>
internal sealed class CountingVersionProviderFactory(IRepositoryVersionProviderFactory inner)
    : IRepositoryVersionProviderFactory
{
    private int _versionCalls;

    public int VersionCalls => Volatile.Read(ref _versionCalls);

    public IRepositoryVersionProvider Create(RepositoryOperationCapabilities? capabilities)
        => new CountingProvider(inner.Create(capabilities), this);

    private void Record() => Interlocked.Increment(ref _versionCalls);

    private sealed class CountingProvider(IRepositoryVersionProvider inner, CountingVersionProviderFactory owner)
        : IRepositoryVersionProvider
    {
        public Task<RepositoryVersionResult> GetVersionAsync(
            string repoPath,
            RepositoryVersionOptions options,
            CancellationToken ct = default)
        {
            if (inner is GitVersionRepositoryVersionProvider)
                owner.Record();
            return inner.GetVersionAsync(repoPath, options, ct);
        }
    }
}

/// <summary>
/// Stand-in for the csproj scanner that reports nothing and counts how often it was asked. Same shape as
/// the <c>NoProjects</c> fakes the neighbouring tests already use, plus the call counter.
/// </summary>
internal sealed class CountingCsProjFileService(IReadOnlyList<CsProjFileInfo>? projects = null, Exception? failWith = null) : ICsProjFileService
{
    private readonly IReadOnlyList<CsProjFileInfo> _projects = projects ?? [];
    private int _findCalls;

    public int FindCalls => Volatile.Read(ref _findCalls);

    public Task<IReadOnlyList<CsProjFileInfo>> FindAsync(string repoPath, CancellationToken cancellationToken = default, int? maxParallel = null)
    {
        Interlocked.Increment(ref _findCalls);
        if (failWith is not null)
            throw failWith;
        return Task.FromResult(_projects);
    }

    public Task<IReadOnlyList<string>> GetProjectPathsAsync(string repoPath, CancellationToken cancellationToken = default, int? maxParallel = null)
        => Task.FromResult<IReadOnlyList<string>>([]);

    public Task<CsProjFileInfo?> ParseAsync(string csprojPath, CancellationToken cancellationToken = default)
        => Task.FromResult<CsProjFileInfo?>(null);

    public Task<int> UpdatePackageVersionsAsync(
        string repoPath,
        IReadOnlyList<(string ProjectPath, IReadOnlyDictionary<string, string> PackageUpdates)> projectUpdates,
        CancellationToken cancellationToken = default)
        => Task.FromResult(0);
}
