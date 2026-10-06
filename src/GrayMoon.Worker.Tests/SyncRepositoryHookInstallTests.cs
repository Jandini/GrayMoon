using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Hook installation depends on the repository being a valid checkout, nothing else. It used to also require
/// a resolvable version, which no workspace could fail before versioning became optional - and which a Basic
/// workspace now fails always, leaving it with no managed hooks and therefore no live updates at all.
/// </summary>
public sealed class SyncRepositoryHookInstallTests : IDisposable
{
    private static readonly string[] ManagedHooks = ["post-commit", "post-checkout", "post-merge", "pre-push"];

    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-hook-install-").FullName;
    private readonly GitService _git;

    public SyncRepositoryHookInstallTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner);
    }

    public void Dispose()
    {
        var root = OperatingSystem.IsWindows() ? @"\\?\" + _root : _root;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Hooks_are_installed_for_a_repository_with_no_resolvable_version()
    {
        var repoPath = await CloneCommittedRepositoryAsync();

        var response = await SyncAsync(RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: false));

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("-", response.Version);
        Assert.NotEqual("-", response.Branch);

        var hooksDir = Path.Combine(repoPath, ".git", "hooks");
        foreach (var hook in ManagedHooks)
        {
            var path = Path.Combine(hooksDir, hook);
            Assert.True(File.Exists(path), $"{hook} was not installed.");
            Assert.Contains("# Created by GrayMoon.Agent", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_installed_hook_payload_stays_context_agnostic()
    {
        var repoPath = await CloneCommittedRepositoryAsync();

        await SyncAsync(RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: false));

        // The script resolves the executing worktree at runtime and sends only identity plus that path, so
        // the app attributes the context. No capability or other decision state belongs on disk.
        var script = await File.ReadAllTextAsync(Path.Combine(repoPath, ".git", "hooks", "post-commit"));
        Assert.Contains("git rev-parse --show-toplevel", script, StringComparison.Ordinal);
        Assert.Contains("\\\"repositoryPath\\\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("calculateRepositoryVersion", script, StringComparison.Ordinal);
        Assert.DoesNotContain("discoverDotNetProjects", script, StringComparison.Ordinal);
    }

    private Task<SyncRepositoryResponse> SyncAsync(RepositoryOperationCapabilities capabilities)
        => new SyncRepositoryCommand(_git, new CountingCsProjFileService(), CapabilityTestDoubles.RealFactory(_git))
            .ExecuteAsync(new SyncRepositoryRequest
            {
                WorkspaceRoot = _root,
                WorkspaceName = "ws",
                RepositoryName = "repo",
                RepositoryId = 2,
                WorkspaceId = 1,
                Capabilities = capabilities,
            });

    private async Task<string> CloneCommittedRepositoryAsync()
    {
        var origin = Path.Combine(_root, "origin.git");
        Directory.CreateDirectory(origin);
        await RunGitAsync(origin, "init --bare -b main");

        var workspace = Path.Combine(_root, "ws");
        Directory.CreateDirectory(workspace);
        await RunGitAsync(workspace, $"clone \"{origin}\" repo");

        var repoPath = Path.Combine(workspace, "repo");
        await RunGitAsync(repoPath, "config user.email test@example.com");
        await RunGitAsync(repoPath, "config user.name Test");
        await File.WriteAllTextAsync(Path.Combine(repoPath, "README.md"), "hello\n");
        await RunGitAsync(repoPath, "add -A");
        await RunGitAsync(repoPath, "commit -m init");
        return repoPath;
    }

    private static async Task RunGitAsync(string workingDirectory, string args)
    {
        var (exit, _, stderr) = await GitVersionParityTests.RunProcessAsync("git", args, workingDirectory);
        if (exit != 0)
            throw new InvalidOperationException($"git {args} failed: {stderr}");
    }
}
