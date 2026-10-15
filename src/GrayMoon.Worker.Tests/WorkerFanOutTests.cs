using GrayMoon.Common.Manifest;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// <see cref="CsProjFileService.UpdatePackageVersionsAsync"/> updates distinct .csproj files in parallel (bounded) and
/// <see cref="SweepPendingFeatureFoldersCommand"/> cleans distinct Feature folders in parallel (bounded); both keep
/// exact counts, and entries for the same file stay sequential.
/// </summary>
public sealed class WorkerFanOutTests : IDisposable
{
    private const int CsProjLimit = 8;
    private const int SweepLimit = 4;

    private readonly string _temp = Directory.CreateTempSubdirectory("graymoon-fanout-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_temp, true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task UpdatePackageVersions_fans_out_to_the_limit_and_counts_only_modified_files()
    {
        var repo = Path.Combine(_temp, "repo");
        const int total = 20;
        var updates = new List<(string ProjectPath, IReadOnlyDictionary<string, string> PackageUpdates)>();
        for (var i = 0; i < total; i++)
        {
            var rel = $"src/P{i:D2}/P{i:D2}.csproj";
            WriteFile(repo, rel);
            updates.Add((rel, new Dictionary<string, string> { ["Pkg"] = "1.0.0" }));
        }

        updates.Add(("src/Missing/Missing.csproj", new Dictionary<string, string> { ["Pkg"] = "1.0.0" })); // not on disk
        updates.Add(("", new Dictionary<string, string> { ["Pkg"] = "1.0.0" })); // blank path
        updates.Add(("src/P00/P00.csproj", new Dictionary<string, string>())); // nothing to update

        // Projects whose name ends in 0 (P00, P10) report "not modified".
        var probe = new CsProjUpdateFanOutProbe(path => !Path.GetFileNameWithoutExtension(path).EndsWith('0'));
        var service = CreateService(probe);

        var count = await service.UpdatePackageVersionsAsync(repo, updates);

        Assert.Equal(probe.ModifiedCount, count);
        Assert.Equal(total, probe.Calls);
        Assert.Equal(CsProjLimit, probe.MaxActive);
        Assert.True(probe.ModifiedCount is > 0 and < total);
    }

    [Fact]
    public async Task UpdatePackageVersions_never_runs_two_updates_of_the_same_file_at_once()
    {
        var repo = Path.Combine(_temp, "repo-same");
        var updates = new List<(string ProjectPath, IReadOnlyDictionary<string, string> PackageUpdates)>();
        for (var i = 0; i < 6; i++)
        {
            var rel = $"src/P{i}/P{i}.csproj";
            WriteFile(repo, rel);
            // Same file three times, spelled differently.
            updates.Add((rel, new Dictionary<string, string> { ["A"] = "1.0.0" }));
            updates.Add(($"src/P{i}/./P{i}.csproj", new Dictionary<string, string> { ["B"] = "1.0.0" }));
            updates.Add(($"src/P{i}/../P{i}/P{i}.csproj", new Dictionary<string, string> { ["C"] = "1.0.0" }));
        }

        var probe = new CsProjUpdateFanOutProbe(_ => true);
        var service = CreateService(probe);

        var count = await service.UpdatePackageVersionsAsync(repo, updates);

        Assert.Equal(18, count);
        Assert.Equal(18, probe.Calls);
        Assert.Equal(1, probe.MaxActivePerFile);
        Assert.True(probe.MaxActive > 1);
    }

    [Fact]
    public async Task UpdatePackageVersions_of_nothing_returns_zero()
    {
        var service = CreateService(new CsProjUpdateFanOutProbe(_ => true));

        Assert.Equal(0, await service.UpdatePackageVersionsAsync(Path.Combine(_temp, "nope"), [("a.csproj", new Dictionary<string, string> { ["A"] = "1" })]));
        Assert.Equal(0, await service.UpdatePackageVersionsAsync(_temp, []));
    }

    [Fact]
    public async Task Sweep_cleans_distinct_folders_in_parallel_up_to_the_limit_with_exact_counters()
    {
        var storageRoot = Path.Combine(_temp, "Shop", "features");
        const int removable = 12;
        for (var i = 0; i < removable; i++)
            CreateMarkedFeature(storageRoot, $"old-{i:D2}", withGitRepo: false);
        // A marked folder holding a Git repository is refused; it must not disturb the others.
        CreateMarkedFeature(storageRoot, "cloned", withGitRepo: true);
        // An unmarked folder is not even considered.
        Directory.CreateDirectory(Path.Combine(storageRoot, "unmarked"));

        var access = new SweepAccessProbe();
        var command = new SweepPendingFeatureFoldersCommand(new FeatureFolderCleaner(NullLogger<FeatureFolderCleaner>.Instance, access));

        var response = await command.ExecuteAsync(new SweepPendingFeatureFoldersRequest { FeatureStorageRoot = storageRoot });

        Assert.Equal(removable, response.Removed);
        Assert.Equal(0, response.StillPending);
        Assert.Equal(1, response.Refused);
        Assert.Equal(removable, access.Calls);
        Assert.Equal(SweepLimit, access.MaxActive);
        for (var i = 0; i < removable; i++)
            Assert.False(Directory.Exists(Path.Combine(storageRoot, $"old-{i:D2}")));
        Assert.True(Directory.Exists(Path.Combine(storageRoot, "cloned")));
        Assert.True(Directory.Exists(Path.Combine(storageRoot, "unmarked")));
    }

    [Fact]
    public async Task Sweep_removes_each_empty_grouping_parent_once_nested_folders_are_gone()
    {
        var storageRoot = Path.Combine(_temp, "Shop", "features");
        for (var i = 0; i < 6; i++)
            CreateMarkedFeature(storageRoot, $"team/old-{i}", withGitRepo: false);

        var command = new SweepPendingFeatureFoldersCommand(
            new FeatureFolderCleaner(NullLogger<FeatureFolderCleaner>.Instance, new SweepAccessProbe()));

        var response = await command.ExecuteAsync(new SweepPendingFeatureFoldersRequest { FeatureStorageRoot = storageRoot });

        Assert.Equal(6, response.Removed);
        Assert.False(Directory.Exists(Path.Combine(storageRoot, "team")));
    }

    private static CsProjFileService CreateService(ICsProjFileParser parser) =>
        new(parser, new LibGit2SharpGitIgnoreService(), new WorkspaceManifestFile(), NullLogger<CsProjFileService>.Instance);

    private static void WriteFile(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project />");
    }

    private static void CreateMarkedFeature(string storageRoot, string name, bool withGitRepo)
    {
        var root = Path.Combine(storageRoot, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.Combine(root, "Api"));
        File.WriteAllText(Path.Combine(root, "Api", "a.txt"), "x");
        if (withGitRepo)
            Directory.CreateDirectory(Path.Combine(root, "Api", ".git"));
        File.WriteAllText(
            Path.Combine(root, PendingDeleteMarker.FileName),
            new PendingDeleteMarker(root, "Shop", name, DateTime.UtcNow).Render());
    }

    /// <summary>Parser stand-in whose UpdateAsync tracks how many calls overlap, overall and per file.</summary>
    private sealed class CsProjUpdateFanOutProbe(Func<string, bool> modifies) : ICsProjFileParser
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _activePerFile = new(StringComparer.OrdinalIgnoreCase);
        private int _active;
        private int _modified;

        public int Calls { get; private set; }
        public int MaxActive { get; private set; }
        public int MaxActivePerFile { get; private set; }
        public int ModifiedCount => Volatile.Read(ref _modified);

        public async Task<bool> UpdateAsync(string csprojPath, IReadOnlyDictionary<string, string> packageIdToNewVersion, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                Calls++;
                _active++;
                MaxActive = Math.Max(MaxActive, _active);
                _activePerFile[csprojPath] = _activePerFile.GetValueOrDefault(csprojPath) + 1;
                MaxActivePerFile = Math.Max(MaxActivePerFile, _activePerFile[csprojPath]);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(60), cancellationToken);
            }
            finally
            {
                lock (_gate)
                {
                    _active--;
                    _activePerFile[csprojPath]--;
                }
            }

            var modified = modifies(csprojPath);
            if (modified)
                Interlocked.Increment(ref _modified);
            return modified;
        }

        public Task<CsProjFileInfo?> ParseAsync(string csprojPath, CancellationToken cancellationToken = default, string? repositoryRoot = null) =>
            throw new NotSupportedException();

        public Task<PackageReferenceMatch?> FindPackageReferenceForVersionPatternAsync(string csprojPath, string prefix, string suffix, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Access broker stand-in: tracks how many folder removals hold an exclusive claim at once.</summary>
    private sealed class SweepAccessProbe : IRepositoryAccess
    {
        private readonly object _gate = new();
        private int _active;

        public int Calls { get; private set; }
        public int MaxActive { get; private set; }

        public async Task<IRepositoryExclusiveScope> AcquireExclusiveAsync(
            IReadOnlyCollection<string> paths, CancellationToken cancellationToken, RepositoryAccessTimings? timings = null)
        {
            lock (_gate)
            {
                Calls++;
                _active++;
                MaxActive = Math.Max(MaxActive, _active);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(80), cancellationToken);
            return new Scope(this);
        }

        public IRepositoryAccessLease? TryAcquireShared(string path, RepositoryAccessKind kind, Action? forceTerminate = null, IRepositoryExclusiveScope? owner = null) =>
            throw new NotSupportedException();

        public IDisposable RegisterReleasable(string path, IReleasable releasable) => throw new NotSupportedException();

        public IDisposable RegisterAncestorObserver(string path, IRepositoryAccessObserver observer) => throw new NotSupportedException();

        public bool IsUnderRemoval(string path) => false;

        private void Release()
        {
            lock (_gate)
            {
                _active--;
            }
        }

        private sealed class Scope(SweepAccessProbe owner) : IRepositoryExclusiveScope
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    owner.Release();
            }
        }
    }
}
