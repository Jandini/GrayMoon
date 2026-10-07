using System.Diagnostics;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// R3: the version GrayMoon shows for a merged Feature repo (<c>GetGitVersionAtDefaultTip</c>, which runs
/// GitVersion with <c>/c &lt;origin/default sha&gt;</c> from whatever branch the repo has checked out) must be the
/// same version GitVersion reports in a fresh checkout of the default branch. Real git, real GitVersion.
/// Skipped, with a reason, on machines where <c>dotnet-gitversion</c> is not installed.
/// </summary>
public sealed class GitVersionParityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-gv-").FullName;
    private readonly GitService _git;
    private GitCliRepositoryReader _reader = null!;
    private readonly GetGitVersionAtDefaultTipCommand _command;

    public GitVersionParityTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, _reader);
        _command = new GetGitVersionAtDefaultTipCommand(_reader, new GitVersionRepositoryVersionProvider(_git));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best-effort; git marks some files read-only */ }
    }

    [FactIfGitVersion]
    public async Task Default_tip_version_matches_a_fresh_checkout_when_the_repo_is_on_the_default_branch()
    {
        var (origin, seed) = await CreateOriginWithHistoryAsync();
        var repoPath = await CloneIntoWorkspaceAsync(origin);

        await AssertParityAsync(origin, repoPath);
    }

    // Known mismatch found by R3, reported in Part F of the release plan (06). `dotnet-gitversion /c <sha>` is
    // ignored for a local repository (it only applies to a remote /url checkout), so the Worker reports the
    // version of the CHECKED-OUT branch, not of origin/<default>. Remove Skip when the fix lands.
    [FactIfGitVersion(Skip = "Known mismatch (R3, plan 06 Part F): GitVersion ignores /c for a local repo, so GetGitVersionAtDefaultTip returns the Feature branch version. Un-skip when fixed.")]
    public async Task Default_tip_version_matches_a_fresh_checkout_when_the_repo_is_on_a_Feature_branch_and_origin_moved_on()
    {
        var (origin, seed) = await CreateOriginWithHistoryAsync();
        var repoPath = await CloneIntoWorkspaceAsync(origin);

        // The Feature worktree is on its own branch with local commits...
        await RunGitAsync(repoPath, "checkout -b feat-x");
        await CommitAsync(repoPath, "feature-1.txt", "feature work 1");
        await CommitAsync(repoPath, "feature-2.txt", "feature work 2");

        // ...while the default branch kept moving upstream (a merge, then a release tag) and was fetched.
        await CommitAsync(seed, "upstream-1.txt", "upstream work 1");
        await CommitAsync(seed, "upstream-2.txt", "upstream work 2");
        await RunGitAsync(seed, "tag v1.3.0");
        await RunGitAsync(seed, "push origin main --tags");
        await CommitAsync(seed, "upstream-3.txt", "upstream work 3");
        await RunGitAsync(seed, "push origin main");
        await RunGitAsync(repoPath, "fetch origin --tags");

        await AssertParityAsync(origin, repoPath);
    }

    private async Task AssertParityAsync(string origin, string repoPath)
    {
        var expected = await RunGitVersionInFreshCheckoutAsync(origin);

        var response = await _command.ExecuteAsync(new GetGitVersionAtDefaultTipRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = "ws",
            RepositoryName = "repo",
        });

        Assert.True(response.Success, response.ErrorMessage);
        Assert.False(string.IsNullOrWhiteSpace(expected));
        Assert.Equal(expected, response.Version);
    }

    // ---- fixtures -----------------------------------------------------------------------------------

    /// <summary>A bare origin plus a seed clone used to publish history, with a GitVersion config and tags.</summary>
    private async Task<(string Origin, string Seed)> CreateOriginWithHistoryAsync()
    {
        var origin = Path.Combine(_root, "origin.git");
        Directory.CreateDirectory(origin);
        await RunGitAsync(origin, "init --bare -b main");

        var seed = Path.Combine(_root, "seed");
        Directory.CreateDirectory(seed);
        await RunGitAsync(seed, "init -b main");
        await ConfigureUserAsync(seed);
        await RunGitAsync(seed, $"remote add origin \"{origin}\"");

        await File.WriteAllTextAsync(Path.Combine(seed, "GitVersion.yml"), "next-version: 1.0.0\n");
        await CommitAsync(seed, "README.md", "init");
        await CommitAsync(seed, "a.txt", "second");
        await RunGitAsync(seed, "tag v1.2.0");
        await CommitAsync(seed, "b.txt", "third");
        await CommitAsync(seed, "c.txt", "fourth");
        await RunGitAsync(seed, "push -u origin main --tags");

        // Make `origin/HEAD` resolvable for clones the way a real hosted remote does.
        await RunGitAsync(origin, "symbolic-ref HEAD refs/heads/main");
        return (origin, seed);
    }

    private async Task<string> CloneIntoWorkspaceAsync(string origin)
    {
        var workspace = Path.Combine(_root, "ws");
        Directory.CreateDirectory(workspace);
        await RunGitAsync(workspace, $"clone \"{origin}\" repo");
        var repoPath = Path.Combine(workspace, "repo");
        await ConfigureUserAsync(repoPath);
        return repoPath;
    }

    private async Task<string> RunGitVersionInFreshCheckoutAsync(string origin)
    {
        var fresh = Path.Combine(_root, "fresh");
        Directory.CreateDirectory(fresh);
        await RunGitAsync(fresh, $"clone \"{origin}\" checkout");
        var checkout = Path.Combine(fresh, "checkout");

        var (exit, stdout, stderr) = await RunProcessAsync("dotnet-gitversion", "/output json /nofetch /verbosity quiet /nonormalize", checkout);
        Assert.True(exit == 0, $"dotnet-gitversion failed in the fresh checkout: {stderr}{stdout}");
        var result = System.Text.Json.JsonSerializer.Deserialize<GitVersionResult>(stdout);
        return result?.InformationalVersion ?? "";
    }

    private static async Task ConfigureUserAsync(string repoPath)
    {
        await RunGitAsync(repoPath, "config user.email test@example.com");
        await RunGitAsync(repoPath, "config user.name Test");
    }

    private static async Task CommitAsync(string repoPath, string file, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(repoPath, file), message + "\n");
        await RunGitAsync(repoPath, "add -A");
        await RunGitAsync(repoPath, $"commit -m \"{message}\"");
    }

    private static async Task RunGitAsync(string workingDirectory, string args)
    {
        var (exit, _, stderr) = await RunProcessAsync("git", args, workingDirectory);
        if (exit != 0)
            throw new InvalidOperationException($"git {args} failed: {stderr}");
    }

    internal static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName, string args, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}");
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await stdoutTask, await stderrTask);
    }
}

/// <summary>A <see cref="FactAttribute"/> that is skipped, with a reason, when <c>dotnet-gitversion</c> is not installed.</summary>
public sealed class FactIfGitVersionAttribute : FactAttribute
{
    private static readonly Lazy<bool> Installed = new(() =>
    {
        try
        {
            var (exit, _, _) = GitVersionParityTests.RunProcessAsync("dotnet-gitversion", "/version", Path.GetTempPath())
                .GetAwaiter().GetResult();
            return exit == 0;
        }
        catch
        {
            return false;
        }
    });

    public FactIfGitVersionAttribute()
    {
        if (!Installed.Value)
            Skip = "dotnet-gitversion is not installed. Install it with 'dotnet tool install -g GitVersion.Tool' to run the GitVersion parity check (R3).";
    }
}
