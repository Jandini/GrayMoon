using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static GrayMoon.Worker.Tests.GitConfigTestSupport;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// The in-process reads of <c>remote.origin.url</c> and <c>core.hooksPath</c> must give the answer native
/// <c>git config --get</c> gives (raw, effective, layered), or hand over to native git. Native git is the oracle.
/// </summary>
public sealed class GitConfigReadTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-cfgread-").FullName;
    private readonly CountingCommandLine _commandLine;
    private readonly GitProcessRunner _runner;
    private readonly GitCliRepositoryReader _reader;

    public GitConfigReadTests()
    {
        _commandLine = new CountingCommandLine(new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions())));
        _runner = new GitProcessRunner(_commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _reader = new GitCliRepositoryReader(_runner, NullLogger<GitCliRepositoryReader>.Instance,
            new RepositoryAccess(NullLogger<RepositoryAccess>.Instance));
    }

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

    private async Task AssertOriginMatchesNativeAsync(string repo, string? expected)
    {
        var native = NativeGet(repo, "remote.origin.url");
        Assert.Equal(expected, native);
        Assert.Equal(native, await _reader.GetRemoteOriginUrlAsync(repo, CancellationToken.None));
    }

    [Fact]
    public async Task Origin_url_is_read_without_a_git_config_process()
    {
        var repo = CreateRepo(_root, "origin");
        Git(repo, "remote add origin https://example.com/a/b.git");
        _commandLine.Calls.Clear();

        Assert.Equal("https://example.com/a/b.git", await _reader.GetRemoteOriginUrlAsync(repo, CancellationToken.None));
        Assert.DoesNotContain(_commandLine.Calls, c => c.Contains("config", StringComparison.Ordinal));
        Assert.Empty(_commandLine.Calls);
    }

    [Fact]
    public async Task Origin_url_is_the_raw_value_not_the_insteadOf_expansion()
    {
        var repo = CreateRepo(_root, "instead");
        Git(repo, "remote add origin gh:a/b.git");
        Git(repo, "config --local url.https://github.com/.insteadOf gh:");

        await AssertOriginMatchesNativeAsync(repo, "gh:a/b.git");
    }

    [Fact]
    public async Task Missing_origin_is_null_like_native()
    {
        var repo = CreateRepo(_root, "noorigin");

        await AssertOriginMatchesNativeAsync(repo, null);
        Assert.Null(await _reader.GetRemoteOriginUrlAsync(repo, CancellationToken.None));
    }

    [Fact]
    public async Task Origin_url_from_an_include_and_from_an_includeIf_matches_native()
    {
        var repo = CreateRepo(_root, "included");
        var include = Path.Combine(_root, "origin.cfg");
        File.WriteAllText(include, "[remote \"origin\"]\n\turl = https://example.com/included.git\n");
        Git(repo, $"config --local include.path \"{ForGitConfig(include)}\"");
        await AssertOriginMatchesNativeAsync(repo, "https://example.com/included.git");

        var repo2 = CreateRepo(_root, "included-if");
        Git(repo2, $"config --local includeIf.gitdir:{ForGitConfig(repo2)}/.path \"{ForGitConfig(include)}\"");
        await AssertOriginMatchesNativeAsync(repo2, "https://example.com/included.git");
    }

    [Fact]
    public async Task Local_origin_wins_over_an_included_one_as_in_native()
    {
        var repo = CreateRepo(_root, "precedence");
        var include = Path.Combine(_root, "prec.cfg");
        File.WriteAllText(include, "[remote \"origin\"]\n\turl = https://example.com/included.git\n");
        Git(repo, $"config --local include.path \"{ForGitConfig(include)}\"");
        Git(repo, "config --local remote.origin.url https://example.com/local.git");

        await AssertOriginMatchesNativeAsync(repo, NativeGet(repo, "remote.origin.url"));
    }

    [Fact]
    public async Task Origin_url_of_a_linked_worktree_equals_the_source_repository()
    {
        var main = CreateRepo(_root, "wt-main");
        Git(main, "remote add origin https://example.com/wt.git");
        var worktree = Path.Combine(_root, "wt-linked");
        Git(main, $"worktree add -b wt \"{worktree}\"");

        // Source repository: in-process. Linked worktree: libgit2 does not layer the shared config under it, so it is
        // answered by native git; both give the same value.
        _commandLine.Calls.Clear();
        Assert.Equal("https://example.com/wt.git", await _reader.GetRemoteOriginUrlAsync(main, CancellationToken.None));
        Assert.Empty(_commandLine.Calls);
        Assert.Equal("https://example.com/wt.git", await _reader.GetRemoteOriginUrlAsync(worktree, CancellationToken.None));
        Assert.Equal(NativeGet(worktree, "remote.origin.url"), await _reader.GetRemoteOriginUrlAsync(worktree, CancellationToken.None));
    }

    [Fact]
    public async Task Per_worktree_config_is_left_to_native_git()
    {
        var main = CreateRepo(_root, "pwc-main");
        Git(main, "remote add origin https://example.com/common.git");
        Git(main, "config extensions.worktreeConfig true");
        var worktree = Path.Combine(_root, "pwc-linked");
        Git(main, $"worktree add -b pwc \"{worktree}\"");
        Git(worktree, "config --worktree remote.origin.url https://example.com/worktree-only.git");

        Assert.Equal("https://example.com/worktree-only.git", NativeGet(worktree, "remote.origin.url"));
        _commandLine.Calls.Clear();
        Assert.Equal("https://example.com/worktree-only.git", await _reader.GetRemoteOriginUrlAsync(worktree, CancellationToken.None));
        Assert.Contains(_commandLine.Calls, c => c.Contains("config --get remote.origin.url", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unopenable_folder_falls_back_to_native_and_returns_null()
    {
        var folder = Path.Combine(_root, "plain");
        Directory.CreateDirectory(folder);

        Assert.Null(await _reader.GetRemoteOriginUrlAsync(folder, CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(".githooks")]
    [InlineData("../shared-hooks")]
    [InlineData("C:/outside/hooks")]
    [InlineData("~/hooks")]
    public void Hooks_path_matches_native_for_normal_and_unusual_values(string? value)
    {
        var repo = CreateRepo(_root, "hooks-" + Guid.NewGuid().ToString("N")[..6]);
        if (value is not null)
            Git(repo, $"config --local core.hooksPath \"{value}\"");

        var result = LibGit2SharpConfigReader.TryGetString(repo, "core.hooksPath", null, CancellationToken.None);

        Assert.True(result.Handled);
        Assert.Equal(NativeGet(repo, "core.hooksPath"), result.Value);
    }

    [Fact]
    public void Hooks_path_from_include_includeIf_and_a_worktree_matches_native()
    {
        var repo = CreateRepo(_root, "hooks-inc");
        var include = Path.Combine(_root, "hooks.cfg");
        File.WriteAllText(include, "[core]\n\thooksPath = /shared/hooks\n");
        Git(repo, $"config --local includeIf.gitdir:{ForGitConfig(repo)}/.path \"{ForGitConfig(include)}\"");
        var worktree = Path.Combine(_root, "hooks-inc-wt");
        Git(repo, $"worktree add -b hk \"{worktree}\"");

        // libgit2 does not evaluate includeIf gitdir: like git, and does not layer the common config under a linked
        // worktree, so neither case may be answered in-process: native git keeps them.
        Assert.Equal("/shared/hooks", NativeGet(repo, "core.hooksPath"));
        foreach (var path in new[] { repo, worktree })
            Assert.False(LibGit2SharpConfigReader.TryGetString(path, "core.hooksPath", null, CancellationToken.None).Handled);
    }

    [Fact]
    public void Hooks_path_is_handed_to_native_when_the_environment_redirects_config()
    {
        var repo = CreateRepo(_root, "hooks-env");
        var previous = Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT");
        try
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
            Assert.False(LibGit2SharpConfigReader.TryGetString(repo, "core.hooksPath", null, CancellationToken.None).Handled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", previous);
        }
    }
}
