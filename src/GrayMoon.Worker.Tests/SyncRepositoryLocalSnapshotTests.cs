using System.Collections.Concurrent;
using GrayMoon.Abstractions.Worker;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// How sync uses the in-process local snapshot: it is the read lane; when it cannot read the repository the git CLI
/// reads answer instead (logged, and with the same response); a folder without its own <c>.git</c> is not answered
/// for by an enclosing repository; and counts for a branch that is not checked out still come from git against
/// HEAD. Real git against a bare remote.
/// </summary>
public sealed class SyncRepositoryLocalSnapshotTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-sync-snapshot-").FullName;
    private readonly GitService _git;
    private readonly GitCliRepositoryReader _reader;

    public SyncRepositoryLocalSnapshotTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, _reader, new LibGit2SharpGitIgnoreService());
    }

    public void Dispose()
    {
        var root = OperatingSystem.IsWindows() ? @"\\?\" + _root : _root;
        try
        {
            // Git writes its object files read-only, which blocks the delete.
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task A_snapshot_that_cannot_read_the_repository_falls_back_to_git_with_the_same_answers()
    {
        var repo = await CloneAsync();
        await GitAsync(repo, "tag v1.0.0");
        await GitAsync(repo, "checkout -q -b feature");
        await CommitAsync(repo, "f.txt", "f");
        var request = NewRequest();
        var expected = await SyncAsync(request, new LibGit2SharpLocalGitSnapshotReader());

        var log = new CapturingLogger();
        var (actual, commands) = await RecordAsync(() => SyncAsync(request, new FailingSnapshotReader(), log));

        Assert.True(actual.Success, actual.ErrorMessage);
        Assert.Equal(expected.Branch, actual.Branch);
        Assert.Equal(expected.Tag, actual.Tag);
        Assert.Equal(expected.Tags, actual.Tags);
        Assert.Equal(expected.LocalBranches, actual.LocalBranches);
        Assert.Equal(expected.RemoteBranches, actual.RemoteBranches);
        Assert.Equal(expected.DefaultBranch, actual.DefaultBranch);
        Assert.Equal(expected.DefaultBranchAhead, actual.DefaultBranchAhead);
        Assert.Equal(expected.DefaultBranchBehind, actual.DefaultBranchBehind);
        Assert.Equal(expected.OutgoingCommits, actual.OutgoingCommits);
        Assert.Equal(expected.IncomingCommits, actual.IncomingCommits);
        Assert.Equal(expected.HasUpstream, actual.HasUpstream);
        Assert.Equal(expected.UpstreamProbed, actual.UpstreamProbed);

        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("falling back to git CLI reads", StringComparison.Ordinal));
        Assert.Contains(log.Entries, e => e.Message.Contains("[GitCli]", StringComparison.Ordinal) && e.Message.Contains("snapshot(failed)", StringComparison.Ordinal));
        Assert.Contains(commands, c => c.Contains("for-each-ref", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_folder_without_its_own_git_is_not_answered_for_by_the_enclosing_repository()
    {
        // The workspace folder is itself a clone (as the Workspace repository is), and "repo" is a plain folder in
        // it: the fetch succeeds against the enclosing repository, and git would report its branch for "repo".
        var origin = await SeedOriginAsync();
        await GitAsync(_root, $"clone -q \"{origin}\" ws");
        Directory.CreateDirectory(Path.Combine(_root, "ws", "repo"));

        var log = new CapturingLogger();
        var response = await SyncAsync(NewRequest(), new LibGit2SharpLocalGitSnapshotReader(), log);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("-", response.Branch);
        Assert.Null(response.LocalBranches);
        Assert.Null(response.RemoteBranches);
        Assert.Null(response.DefaultBranch);
        Assert.Null(response.OutgoingCommits);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("has no .git of its own", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_version_provider_branch_that_is_not_checked_out_is_counted_by_git_against_HEAD()
    {
        var repo = await CloneAsync();
        await GitAsync(repo, "branch other");
        await CommitAsync(repo, "a.txt", "a");
        var factory = new FixedVersionProviderFactory(new RepositoryVersionResult(
            Probed: true,
            new GitVersionResult { InformationalVersion = "1.0.0", BranchName = "other" },
            Error: null));
        var request = NewRequest(RepositoryOperationCapabilities.For(calculateVersion: true, discoverProjects: false));

        var (response, commands) = await RecordAsync(() => SyncAsync(request, new LibGit2SharpLocalGitSnapshotReader(), versionProviders: factory));

        var expected = await _reader.ProbeCommitCountsAsync(repo, "other", "origin/main", CancellationToken.None);
        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("other", response.Branch);
        Assert.Equal(expected.Outgoing, response.OutgoingCommits);
        Assert.Equal(expected.Incoming, response.IncomingCommits);
        Assert.Equal(expected.HasUpstream, response.HasUpstream);
        Assert.Contains(commands, c => c.Contains("refs/heads/other", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tag_checkout_reports_the_tag_from_the_snapshot_without_a_read_process()
    {
        var repo = await CloneAsync();
        await GitAsync(repo, "tag -a v2.0.0 -m v2");
        await GitAsync(repo, "checkout -q --detach v2.0.0");

        var (response, commands) = await RecordAsync(() => SyncAsync(NewRequest(), new LibGit2SharpLocalGitSnapshotReader()));

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("-", response.Branch);
        Assert.Equal("v2.0.0", response.Tag);
        Assert.Null(response.OutgoingCommits);
        Assert.DoesNotContain(commands, c => c.Contains("describe", StringComparison.Ordinal) || c.Contains("symbolic-ref", StringComparison.Ordinal));
    }

    private Task<SyncRepositoryResponse> SyncAsync(
        SyncRepositoryRequest request,
        ILocalGitSnapshotReader snapshots,
        ILogger<SyncRepositoryCommand>? logger = null,
        IRepositoryVersionProviderFactory? versionProviders = null)
        => new SyncRepositoryCommand(_git, _reader, snapshots, new CountingCsProjFileService(), versionProviders ?? CapabilityTestDoubles.RealFactory(_git), logger)
            .ExecuteAsync(request);

    private SyncRepositoryRequest NewRequest(RepositoryOperationCapabilities? capabilities = null) => new()
    {
        WorkspaceRoot = _root,
        WorkspaceName = "ws",
        RepositoryName = "repo",
        RepositoryId = 1,
        WorkspaceId = 1,
        Capabilities = capabilities ?? RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: false),
    };

    private static async Task<(T Result, List<string> Commands)> RecordAsync<T>(Func<Task<T>> action)
    {
        var events = new ConcurrentQueue<CommandLineStreamEvent>();
        T result;
        using (new CommandLineStreamScope(events.Enqueue))
            result = await action();
        return (result, events
            .Where(e => e.Kind == WorkerCommandStreamKind.CommandLine && e.Text.StartsWith("$ git", StringComparison.Ordinal))
            .Select(e => e.Text)
            .ToList());
    }

    private async Task<string> SeedOriginAsync()
    {
        var origin = Path.Combine(_root, "origin.git");
        Directory.CreateDirectory(origin);
        await GitAsync(origin, "init --bare -b main");

        var seed = Path.Combine(_root, "seed");
        Directory.CreateDirectory(seed);
        await GitAsync(seed, "init -b main");
        await ConfigureUserAsync(seed);
        await GitAsync(seed, $"remote add origin \"{origin}\"");
        await CommitAsync(seed, "README.md", "init");
        await GitAsync(seed, "push origin main");
        return origin;
    }

    /// <summary>Clones to <c>ws/repo</c>, the layout <see cref="NewRequest"/> points at.</summary>
    private async Task<string> CloneAsync()
    {
        var origin = await SeedOriginAsync();
        var workspace = Path.Combine(_root, "ws");
        Directory.CreateDirectory(workspace);
        await GitAsync(workspace, $"clone -q \"{origin}\" repo");
        var repo = Path.Combine(workspace, "repo");
        await ConfigureUserAsync(repo);
        return repo;
    }

    private static async Task ConfigureUserAsync(string repo)
    {
        await GitAsync(repo, "config user.email t@example.com");
        await GitAsync(repo, "config user.name T");
    }

    private static async Task CommitAsync(string repoPath, string fileName, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(repoPath, fileName), message + "\n");
        await GitAsync(repoPath, "add -A");
        await GitAsync(repoPath, $"commit -q -m \"{message}\"");
    }

    private static async Task<string> GitAsync(string workingDirectory, string args)
    {
        var (exit, stdout, stderr) = await GitVersionParityTests.RunProcessAsync("git", args, workingDirectory);
        if (exit != 0)
            throw new InvalidOperationException($"git {args} failed in {workingDirectory}: {stderr}");
        return stdout;
    }

    private sealed class FailingSnapshotReader : ILocalGitSnapshotReader
    {
        public LocalGitSnapshot Read(string repositoryPath, LocalGitSnapshotRequest request, CancellationToken ct)
            => throw new LocalGitReadException(repositoryPath, "simulated: repository format not supported");
    }

    private sealed class FixedVersionProviderFactory(RepositoryVersionResult result) : IRepositoryVersionProviderFactory
    {
        public IRepositoryVersionProvider Create(RepositoryOperationCapabilities? capabilities) => new Provider(result);

        private sealed class Provider(RepositoryVersionResult result) : IRepositoryVersionProvider
        {
            public Task<RepositoryVersionResult> GetVersionAsync(string repoPath, RepositoryVersionOptions options, CancellationToken ct = default)
                => Task.FromResult(result);
        }
    }

    private sealed class CapturingLogger : ILogger<SyncRepositoryCommand>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
                Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
