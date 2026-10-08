using System.Collections.Concurrent;
using GrayMoon.Abstractions.Worker;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Unit 3 of the Sync critical path work: GrayMoon does not start GitVersion when the version inputs are
/// provably what they were at the last successful run. The cache tests use a counting stand-in for GitVersion
/// against real repositories (a tool manifest in the repository makes the tool identity resolvable without
/// GitVersion installed); the parity and process-count tests use the real tool and skip without it.
/// </summary>
public sealed class GitVersionResultCacheTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-gvcache-").FullName;
    private readonly CapturingLogger<GitVersionResultCache> _log = new();
    private readonly GitVersionResultCache _cache;
    private int _runs;

    public GitVersionResultCacheTests()
    {
        _cache = new GitVersionResultCache(_log);
    }

    public void Dispose()
    {
        try { ForceDelete(_root); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task An_unchanged_repository_is_answered_without_running_GitVersion()
    {
        var repo = await InitAsync("repo");

        var first = await GetAsync(repo);
        var second = await GetAsync(repo);

        Assert.Equal(1, _runs);
        Assert.Equal("v1", first.InformationalVersion);
        Assert.Equal("v1", second.InformationalVersion);
        Assert.Equal("NO_PREVIOUS_SUCCESS", LastMissReason());
        Assert.Contains(_log.Entries, e => e.Message.StartsWith("GitVersion cache hit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_new_commit_runs_GitVersion_again_and_no_stale_result_is_returned()
    {
        var repo = await InitAsync("repo");
        await GetAsync(repo);

        await CommitAsync(repo, "b.txt", "second");
        var after = await GetAsync(repo);

        Assert.Equal(2, _runs);
        Assert.Equal("v2", after.InformationalVersion);
        Assert.Equal("HEAD_CHANGED", LastMissReason());
        await GetAsync(repo);
        Assert.Equal(2, _runs);
    }

    [Fact]
    public async Task Switching_branch_at_the_same_commit_runs_GitVersion_again()
    {
        var repo = await InitAsync("repo");
        await GetAsync(repo);

        await GitAsync(repo, "checkout -b other");
        await GetAsync(repo);

        Assert.Equal(2, _runs);
        Assert.Equal("HEAD_IDENTITY_CHANGED", LastMissReason());
    }

    [Fact]
    public async Task A_new_tag_and_a_moved_tag_run_GitVersion_again()
    {
        var repo = await InitAsync("repo");
        await GetAsync(repo);

        await GitAsync(repo, "tag v1.0.0");
        await GetAsync(repo);
        Assert.Equal(2, _runs);
        Assert.Equal("REFS_CHANGED", LastMissReason());

        await CommitAsync(repo, "b.txt", "second");
        await GetAsync(repo);
        await GitAsync(repo, "tag -f v1.0.0");
        await GetAsync(repo);
        Assert.Equal(4, _runs);
        Assert.Equal("REFS_CHANGED", LastMissReason());
    }

    [Fact]
    public async Task A_changed_local_or_remote_ref_runs_GitVersion_again()
    {
        var repo = await InitAsync("repo");
        await GetAsync(repo);

        await GitAsync(repo, "branch release/1.0");
        await GetAsync(repo);
        Assert.Equal(2, _runs);

        var sha = (await GitAsync(repo, "rev-parse HEAD")).Trim();
        await GitAsync(repo, $"update-ref refs/remotes/origin/main {sha}");
        await GetAsync(repo);
        Assert.Equal(3, _runs);
        Assert.Equal("REFS_CHANGED", LastMissReason());

        await GetAsync(repo);
        Assert.Equal(3, _runs);
    }

    [Fact]
    public async Task Changes_to_the_GitVersion_config_run_GitVersion_again()
    {
        var repo = await InitAsync("repo");
        await GetAsync(repo);

        await File.WriteAllTextAsync(Path.Combine(repo, "GitVersion.yml"), "mode: ContinuousDelivery\n");
        await GetAsync(repo);
        Assert.Equal(2, _runs);
        Assert.Equal("CONFIG_CHANGED", LastMissReason());

        await File.WriteAllTextAsync(Path.Combine(repo, "GitVersion.yml"), "mode: Mainline\n");
        await GetAsync(repo);
        Assert.Equal(3, _runs);
        Assert.Equal("CONFIG_CHANGED", LastMissReason());
    }

    [Fact]
    public async Task A_config_that_formats_with_UncommittedChanges_is_never_cached()
    {
        var repo = await InitAsync("repo");
        await File.WriteAllTextAsync(Path.Combine(repo, "GitVersion.yml"), "assembly-informational-format: '{FullSemVer}.{UncommittedChanges}'\n");

        await GetAsync(repo);
        await GetAsync(repo);

        Assert.Equal(2, _runs);
    }

    [Fact]
    public async Task A_changed_tool_identity_runs_GitVersion_again()
    {
        var repo = await InitAsync("repo");
        await GetAsync(repo);

        await File.WriteAllTextAsync(Path.Combine(repo, "dotnet-tools.json"), ToolManifest("5.12.1"));
        await GetAsync(repo);

        Assert.Equal(2, _runs);
        Assert.Equal("TOOL_CHANGED", LastMissReason());
    }

    [Fact]
    public async Task NonNormalize_and_CommitSha_are_part_of_the_request_and_do_not_evict_each_other()
    {
        var repo = await InitAsync("repo");
        var sha = (await GitAsync(repo, "rev-parse HEAD")).Trim();
        var plain = new RepositoryVersionOptions();
        var nonNormalize = new RepositoryVersionOptions { NonNormalize = true };
        var atSha = new RepositoryVersionOptions { CommitSha = sha };

        await GetAsync(repo, plain);
        await GetAsync(repo, nonNormalize);
        Assert.Equal(2, _runs);
        Assert.Equal("INVOCATION_CHANGED", LastMissReason());
        await GetAsync(repo, atSha);
        Assert.Equal(3, _runs);

        // Alternating between the three does not thrash: each is remembered on its own.
        await GetAsync(repo, plain);
        await GetAsync(repo, nonNormalize);
        await GetAsync(repo, atSha);
        Assert.Equal(3, _runs);

        // A different commit sha is a different request.
        await CommitAsync(repo, "b.txt", "second");
        var newSha = (await GitAsync(repo, "rev-parse HEAD")).Trim();
        await GetAsync(repo, new RepositoryVersionOptions { CommitSha = newSha });
        Assert.Equal(4, _runs);
    }

    [Fact]
    public async Task A_detached_HEAD_is_cached_and_distinct_from_the_branch_it_came_from()
    {
        var repo = await InitAsync("repo");
        await GetAsync(repo);

        await GitAsync(repo, "checkout --detach");
        await GetAsync(repo);
        Assert.Equal(2, _runs);
        Assert.Equal("HEAD_IDENTITY_CHANGED", LastMissReason());

        await GetAsync(repo);
        Assert.Equal(2, _runs);

        await GitAsync(repo, "checkout main");
        await GetAsync(repo);
        Assert.Equal(3, _runs);
    }

    [Fact]
    public async Task A_linked_worktree_has_its_own_entry_and_sees_shared_ref_changes()
    {
        var repo = await InitAsync("repo");
        var worktree = Path.Combine(_root, "wt");
        await GitAsync(repo, $"worktree add \"{worktree}\" -b feat");
        await File.WriteAllTextAsync(Path.Combine(worktree, "dotnet-tools.json"), ToolManifest("5.12.0"));

        await GetAsync(repo);
        await GetAsync(worktree);
        Assert.Equal(2, _runs);
        await GetAsync(repo);
        await GetAsync(worktree);
        Assert.Equal(2, _runs);

        // A tag made from the main checkout is a ref of the worktree's repository as well.
        await GitAsync(repo, "tag v2.0.0");
        await GetAsync(worktree);
        Assert.Equal(3, _runs);
        Assert.Equal("REFS_CHANGED", LastMissReason());

        // A commit on the worktree's own branch changes only its HEAD.
        await CommitAsync(worktree, "w.txt", "worktree work");
        await GetAsync(worktree);
        Assert.Equal(4, _runs);
        Assert.Equal("HEAD_CHANGED", LastMissReason());
    }

    [Fact]
    public async Task A_failed_run_is_not_remembered_and_the_next_success_is()
    {
        var repo = await InitAsync("repo");
        var fail = true;
        Task<RepositoryVersionResult> Run(CancellationToken _)
        {
            Interlocked.Increment(ref _runs);
            return Task.FromResult(fail
                ? new RepositoryVersionResult(true, null, "dotnet-gitversion exited with code 1")
                : new RepositoryVersionResult(true, new GitVersionResult { InformationalVersion = "ok", BranchName = "main" }, null));
        }

        var failed = await _cache.GetOrRunAsync(repo, new RepositoryVersionOptions(), Run, CancellationToken.None);
        Assert.NotNull(failed.Error);
        var failedAgain = await _cache.GetOrRunAsync(repo, new RepositoryVersionOptions(), Run, CancellationToken.None);
        Assert.NotNull(failedAgain.Error);
        Assert.Equal(2, _runs);

        fail = false;
        var ok = await _cache.GetOrRunAsync(repo, new RepositoryVersionOptions(), Run, CancellationToken.None);
        Assert.Equal("ok", ok.InformationalVersion);
        await _cache.GetOrRunAsync(repo, new RepositoryVersionOptions(), Run, CancellationToken.None);
        Assert.Equal(3, _runs);
    }

    [Fact]
    public async Task A_run_that_throws_is_not_remembered_and_does_not_poison_later_requests()
    {
        var repo = await InitAsync("repo");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _cache.GetOrRunAsync(
            repo, new RepositoryVersionOptions(), _ => throw new InvalidOperationException("boom"), CancellationToken.None));

        var ok = await GetAsync(repo);
        Assert.Equal("v1", ok.InformationalVersion);
    }

    [Fact]
    public async Task Concurrent_identical_requests_share_one_GitVersion_run()
    {
        var repo = await InitAsync("repo");
        var gate = new TaskCompletionSource();
        Task<RepositoryVersionResult> Run(CancellationToken _) => RunSlowAsync(gate.Task);

        var calls = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => _cache.GetOrRunAsync(repo, new RepositoryVersionOptions(), Run, CancellationToken.None)))
            .ToList();
        await Task.Delay(300);
        gate.SetResult();
        var results = await Task.WhenAll(calls);

        Assert.Equal(1, _runs);
        Assert.All(results, r => Assert.Equal("slow", r.InformationalVersion));
        Assert.Equal(results.Length, results.Select(r => r.Result).Distinct().Count()); // each caller owns its copy
    }

    [Fact]
    public async Task A_cancelled_first_request_does_not_fail_the_request_waiting_on_it()
    {
        var repo = await InitAsync("repo");
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();

        async Task<RepositoryVersionResult> Cancellable(CancellationToken ct)
        {
            Interlocked.Increment(ref _runs);
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new RepositoryVersionResult(true, null, null);
        }

        var first = _cache.GetOrRunAsync(repo, new RepositoryVersionOptions(), Cancellable, cts.Token);
        await started.Task;
        var second = _cache.GetOrRunAsync(repo, new RepositoryVersionOptions(), _ => Task.FromResult(Success("second")), CancellationToken.None);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal("second", (await second).InformationalVersion);
    }

    [Fact]
    public async Task The_cache_is_bounded()
    {
        var small = new GitVersionResultCache(_log, maxEntries: 2);
        for (var i = 0; i < 4; i++)
        {
            var repo = await InitAsync($"repo{i}");
            await small.GetOrRunAsync(repo, new RepositoryVersionOptions(), _ => Task.FromResult(Success("v")), CancellationToken.None);
        }

        Assert.Equal(2, small.Count);
    }

    [Fact]
    public async Task A_repository_that_cannot_be_fingerprinted_always_runs_GitVersion()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_root, "unborn")).FullName;
        await GitAsync(empty, "init -b main"); // no commit yet: GitVersion fails there, nothing to key on

        await GetAsync(empty);
        await GetAsync(empty);

        Assert.Equal(2, _runs);
    }

    [Fact]
    public async Task Working_tree_edits_alone_do_not_invalidate_the_cache()
    {
        var repo = await InitAsync("repo");
        await GetAsync(repo);

        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "edited\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "untracked.txt"), "new\n");
        await GetAsync(repo);

        Assert.Equal(1, _runs);
    }

    // ---------------------------------------------------------------- real GitVersion

    /// <summary>
    /// The premise behind leaving the working tree out of the fingerprint: ordinary uncommitted edits move
    /// UncommittedChanges (which GrayMoon does not consume), not InformationalVersion or the branch name.
    /// </summary>
    [FactIfGitVersion]
    public async Task Real_GitVersion_reports_the_same_consumed_fields_for_a_clean_and_a_dirty_working_tree()
    {
        var repo = await InitAsync("gv", withManifest: false);
        await GitAsync(repo, "tag v1.2.0");
        await CommitAsync(repo, "b.txt", "after tag");

        var clean = await RunRealGitVersionAsync(repo);
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "edited\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "untracked.txt"), "new\n");
        await GitAsync(repo, "add README.md");
        var dirty = await RunRealGitVersionAsync(repo);

        Assert.Equal(clean.InformationalVersion, dirty.InformationalVersion);
        Assert.Equal(clean.BranchName, dirty.BranchName);
        Assert.Equal(clean.EscapedBranchName, dirty.EscapedBranchName);
    }

    [FactIfGitVersion]
    public async Task Real_provider_starts_GitVersion_once_for_an_unchanged_repository_and_again_after_a_commit()
    {
        var repo = await InitAsync("gv", withManifest: false);
        var provider = new GitVersionRepositoryVersionProvider(NewGitService(), _cache);

        var events = new ConcurrentQueue<CommandLineStreamEvent>();
        RepositoryVersionResult first, second, third;
        using (new CommandLineStreamScope(events.Enqueue))
        {
            first = await provider.GetVersionAsync(repo, new RepositoryVersionOptions());
            second = await provider.GetVersionAsync(repo, new RepositoryVersionOptions());
        }

        Assert.Null(first.Error);
        Assert.NotNull(first.InformationalVersion);
        Assert.Equal(first.InformationalVersion, second.InformationalVersion);
        Assert.Equal(first.Result!.BranchName, second.Result!.BranchName);
        Assert.Equal(1, events.Count(IsGitVersion));

        await CommitAsync(repo, "b.txt", "second");
        events.Clear();
        using (new CommandLineStreamScope(events.Enqueue))
            third = await provider.GetVersionAsync(repo, new RepositoryVersionOptions());

        Assert.Equal(1, events.Count(IsGitVersion));
        Assert.NotEqual(first.InformationalVersion, third.InformationalVersion);
    }

    // ---------------------------------------------------------------- helpers

    private static bool IsGitVersion(CommandLineStreamEvent e)
        => e.Kind == WorkerCommandStreamKind.CommandLine && e.Text.Contains("gitversion", StringComparison.OrdinalIgnoreCase);

    private async Task<GitVersionResult> RunRealGitVersionAsync(string repo)
    {
        var result = await new GitVersionRepositoryVersionProvider(NewGitService()).GetVersionAsync(repo, new RepositoryVersionOptions());
        Assert.Null(result.Error);
        return result.Result!;
    }

    private static GitService NewGitService()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        return new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, reader, new LibGit2SharpGitIgnoreService());
    }

    private static RepositoryVersionResult Success(string version)
        => new(true, new GitVersionResult { InformationalVersion = version, BranchName = "main", EscapedBranchName = "main" }, null);

    private Task<RepositoryVersionResult> RunSlowAsync(Task gate)
    {
        Interlocked.Increment(ref _runs);
        return gate.ContinueWith(_ => Success("slow"), TaskScheduler.Default);
    }

    /// <summary>Runs through the cache with a counting stand-in for GitVersion; versions are v1, v2, ...</summary>
    private async Task<GitVersionResult> GetAsync(string repo, RepositoryVersionOptions? options = null)
    {
        var result = await _cache.GetOrRunAsync(repo, options ?? new RepositoryVersionOptions(),
            _ => Task.FromResult(Success($"v{Interlocked.Increment(ref _runs)}")), CancellationToken.None);
        Assert.Null(result.Error);
        return result.Result!;
    }

    private string? LastMissReason()
    {
        const string prefix = "GitVersion cache miss for ";
        var line = _log.Entries.LastOrDefault(e => e.Message.StartsWith(prefix, StringComparison.Ordinal)).Message;
        return line?[(line.LastIndexOf(": ", StringComparison.Ordinal) + 2)..];
    }

    private static string ToolManifest(string version)
        => "{\"version\":1,\"isRoot\":true,\"tools\":{\"gitversion.tool\":{\"version\":\"" + version + "\",\"commands\":[\"dotnet-gitversion\"]}}}\n";

    private async Task<string> InitAsync(string name, bool withManifest = true)
    {
        var repo = Path.Combine(_root, name);
        Directory.CreateDirectory(repo);
        await GitAsync(repo, "init -b main");
        await GitAsync(repo, "config user.email t@example.com");
        await GitAsync(repo, "config user.name T");
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "init\n");
        await GitAsync(repo, "add -A");
        await GitAsync(repo, "commit -m init");
        // Untracked, so it does not move HEAD; it only makes the tool identity resolvable without GitVersion installed.
        if (withManifest)
            await File.WriteAllTextAsync(Path.Combine(repo, "dotnet-tools.json"), ToolManifest("5.12.0"));
        return repo;
    }

    private static async Task CommitAsync(string repo, string file, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(repo, file), message + "\n");
        await GitAsync(repo, $"add {file}");
        await GitAsync(repo, $"commit -m \"{message}\"");
    }

    private static void ForceDelete(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, true);
    }

    private static async Task<string> GitAsync(string workingDirectory, string args)
    {
        var (exit, stdout, stderr) = await GitVersionParityTests.RunProcessAsync("git", args, workingDirectory);
        if (exit != 0)
            throw new InvalidOperationException($"git {args} failed in {workingDirectory}: {stderr}");
        return stdout;
    }
}
