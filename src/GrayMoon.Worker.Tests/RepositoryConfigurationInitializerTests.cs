using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using static GrayMoon.Worker.Tests.GitConfigTestSupport;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// The Windows <c>core.longpaths</c> policy through LibGit2Sharp. The Windows gate is injected so the policy itself is
/// exercised on every OS; native git only verifies the result independently.
/// </summary>
public sealed class RepositoryConfigurationInitializerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-rci-").FullName;

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch
        {
            // best-effort
        }
    }

    private static RepositoryConfigurationInitializer Create(IRepositoryAccess? access = null, bool windows = true)
        => new(access, NullLogger<RepositoryConfigurationInitializer>.Instance, windows);

    private static string? Local(string repo) =>
        Git(repo, "config --local --get core.longpaths", allowFailure: true).Trim() is { Length: > 0 } v ? v : null;

    private static int LocalEntryCount(string repo) =>
        Git(repo, "config --local --get-all core.longpaths", allowFailure: true)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

    [Fact]
    public void Missing_setting_is_written_once_to_the_local_config()
    {
        var repo = CreateRepo(_root, "missing");
        var initializer = Create();

        Assert.Equal(RepositoryConfigurationResult.Configured, initializer.EnsureWindowsLongPaths(repo, CancellationToken.None));
        Assert.Equal("true", Local(repo));

        Assert.Equal(RepositoryConfigurationResult.AlreadyConfigured, initializer.EnsureWindowsLongPaths(repo, CancellationToken.None));
        Assert.Equal(1, LocalEntryCount(repo));
    }

    [Fact]
    public void Existing_true_is_not_rewritten()
    {
        var repo = CreateRepo(_root, "already");
        Git(repo, "config --local core.longpaths true");
        var before = File.GetLastWriteTimeUtc(Path.Combine(repo, ".git", "config"));
        Thread.Sleep(30);

        Assert.Equal(RepositoryConfigurationResult.AlreadyConfigured, Create().EnsureWindowsLongPaths(repo, CancellationToken.None));
        Assert.Equal(before, File.GetLastWriteTimeUtc(Path.Combine(repo, ".git", "config")));
        Assert.Equal(1, LocalEntryCount(repo));
    }

    [Fact]
    public void Explicit_local_false_is_reconciled_to_true()
    {
        var repo = CreateRepo(_root, "false");
        Git(repo, "config --local core.longpaths false");

        Assert.Equal(RepositoryConfigurationResult.Configured, Create().EnsureWindowsLongPaths(repo, CancellationToken.None));
        Assert.Equal("true", Local(repo));
        Assert.Equal(1, LocalEntryCount(repo));
    }

    [Fact]
    public void Not_applicable_off_Windows_and_nothing_is_touched()
    {
        var repo = CreateRepo(_root, "other-os");

        Assert.Equal(RepositoryConfigurationResult.NotApplicable, Create(windows: false).EnsureWindowsLongPaths(repo, CancellationToken.None));
        Assert.Null(Local(repo));
    }

    [Fact]
    public void Missing_repository_fails_and_a_later_call_retries()
    {
        var path = Path.Combine(_root, "later");
        var initializer = Create();

        Assert.Equal(RepositoryConfigurationResult.Failed, initializer.EnsureWindowsLongPaths(path, CancellationToken.None));

        var repo = CreateRepo(_root, "later");
        Assert.Equal(RepositoryConfigurationResult.Configured, initializer.EnsureWindowsLongPaths(repo, CancellationToken.None));
        Assert.Equal("true", Local(repo));
    }

    [Fact]
    public void A_folder_that_is_not_a_repository_fails()
    {
        var folder = Path.Combine(_root, "plain");
        Directory.CreateDirectory(folder);

        Assert.Equal(RepositoryConfigurationResult.Failed, Create().EnsureWindowsLongPaths(folder, CancellationToken.None));
    }

    [Fact]
    public void A_locked_config_fails_without_being_remembered_and_is_retried()
    {
        var repo = CreateRepo(_root, "locked");
        var lockFile = Path.Combine(repo, ".git", "config.lock");
        File.WriteAllText(lockFile, "");
        var initializer = Create();

        Assert.Equal(RepositoryConfigurationResult.Failed, initializer.EnsureWindowsLongPaths(repo, CancellationToken.None));
        Assert.Null(Local(repo));

        File.Delete(lockFile);
        Assert.Equal(RepositoryConfigurationResult.Configured, initializer.EnsureWindowsLongPaths(repo, CancellationToken.None));
        Assert.Equal("true", Local(repo));
    }

    [Fact]
    public void Cancellation_propagates_and_writes_nothing()
    {
        var repo = CreateRepo(_root, "cancel");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => Create().EnsureWindowsLongPaths(repo, cts.Token));
        Assert.Null(Local(repo));
    }

    [Fact]
    public void Simultaneous_requests_are_idempotent_with_a_single_entry()
    {
        var repo = CreateRepo(_root, "parallel");
        var initializer = Create();

        var results = Enumerable.Range(0, 24).AsParallel().WithDegreeOfParallelism(12)
            .Select(_ => initializer.EnsureWindowsLongPaths(repo, CancellationToken.None))
            .ToList();

        Assert.DoesNotContain(RepositoryConfigurationResult.Failed, results);
        Assert.Equal(1, results.Count(r => r == RepositoryConfigurationResult.Configured));
        Assert.Equal(1, LocalEntryCount(repo));
    }

    [Fact]
    public void Linked_worktree_path_writes_the_shared_config_not_a_per_worktree_one()
    {
        var main = CreateRepo(_root, "shared");
        var worktree = Path.Combine(_root, "shared-wt");
        Git(main, $"worktree add -b wt \"{worktree}\"");

        Assert.Equal(RepositoryConfigurationResult.Configured, Create().EnsureWindowsLongPaths(worktree, CancellationToken.None));

        Assert.Equal("true", Local(main));
        Assert.Equal("true", Local(worktree));
        Assert.False(File.Exists(Path.Combine(main, ".git", "config.worktree")));
        Assert.Empty(Directory.GetFiles(Path.Combine(main, ".git", "worktrees"), "config.worktree", SearchOption.AllDirectories));
        Assert.Equal(1, LocalEntryCount(main));
    }

    [Fact]
    public void Local_policy_is_written_even_when_a_global_style_value_is_effective()
    {
        // An included file stands in for inherited configuration; the setting is read per level, so a value that is
        // only inherited does not stop the repository from recording its own explicit local policy.
        var repo = CreateRepo(_root, "inherited");
        var include = Path.Combine(_root, "inherited.cfg");
        File.WriteAllText(include, "[core]\n\tlongpaths = true\n");
        Git(repo, $"config --local include.path \"{ForGitConfig(include)}\"");

        var result = Create().EnsureWindowsLongPaths(repo, CancellationToken.None);

        // Documented behaviour: whichever way libgit2 layers includes, the effective value stays true and a repeat is a no-op.
        Assert.Contains(result, new[] { RepositoryConfigurationResult.Configured, RepositoryConfigurationResult.AlreadyConfigured });
        Assert.Equal("true", NativeGet(repo, "core.longpaths"));
        Assert.Equal(RepositoryConfigurationResult.AlreadyConfigured, Create().EnsureWindowsLongPaths(repo, CancellationToken.None));
    }

    [Fact]
    public async Task A_folder_under_removal_is_refused_and_released_handles_do_not_block_deletion()
    {
        var repo = CreateRepo(_root, "removing");
        var access = new RepositoryAccess(NullLogger<RepositoryAccess>.Instance);
        var initializer = Create(access);

        using (await access.AcquireExclusiveAsync([repo], CancellationToken.None))
            Assert.Throws<PathUnderRemovalException>(() => initializer.EnsureWindowsLongPaths(repo, CancellationToken.None));
        Assert.Null(Local(repo));

        Assert.Equal(RepositoryConfigurationResult.Configured, initializer.EnsureWindowsLongPaths(repo, CancellationToken.None));

        // No repository handle is left open: the whole folder can be deleted straight away.
        foreach (var file in Directory.EnumerateFiles(repo, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(repo, true);
        Assert.False(Directory.Exists(repo));
    }
}
