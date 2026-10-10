using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// A fresh clone in a Workspace that has a Workspace repository is checked out to the commit recorded
/// for that repository when the definition has it on a tag. An existing checkout is left where it is.
/// </summary>
public sealed class SyncRepositoryTagPinTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-tag-pin-").FullName;
    private readonly GitService _git;
    private readonly GitCliRepositoryReader _reader;

    public SyncRepositoryTagPinTests()
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
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
        catch
        {
            // The temp folder is best-effort.
        }
    }

    [Fact]
    public async Task Fresh_clone_of_a_pinned_repository_checks_out_the_recorded_commit()
    {
        var (origin, taggedCommit, tip) = await CreateOriginAsync();
        await WriteDefinitionAsync(origin, "v1.0.0", taggedCommit);

        var response = await SyncAsync(origin, applyRepositoryTagPin: true);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal("v1.0.0", response.Tag);
        Assert.Equal(taggedCommit, response.Commit);
        Assert.Equal(taggedCommit, await HeadAsync(RepoPath()));
        Assert.NotEqual(tip, taggedCommit);
    }

    [Fact]
    public async Task Existing_checkout_is_not_moved_to_the_pinned_commit()
    {
        var (origin, taggedCommit, tip) = await CreateOriginAsync();
        await WriteDefinitionAsync(origin, "v1.0.0", taggedCommit);
        var workspace = Path.Combine(_root, "ws");
        Directory.CreateDirectory(workspace);
        await RunGitAsync(workspace, $"clone \"{origin}\" repo");

        var response = await SyncAsync(origin, applyRepositoryTagPin: true);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Null(response.Tag);
        Assert.Equal(tip, await HeadAsync(RepoPath()));
    }

    [Fact]
    public async Task Missing_pinned_commit_fails_the_sync_and_removes_the_clone()
    {
        var (origin, _, _) = await CreateOriginAsync();
        await WriteDefinitionAsync(origin, "v1.0.0", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        var response = await SyncAsync(origin, applyRepositoryTagPin: true);

        Assert.False(response.Success);
        Assert.Contains("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", response.ErrorMessage);
        Assert.False(Directory.Exists(RepoPath()));
    }

    [Fact]
    public async Task Invalid_pin_fails_the_sync_and_removes_the_clone()
    {
        var (origin, _, _) = await CreateOriginAsync();
        var workspace = Path.Combine(_root, "ws");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(
            Path.Combine(workspace, ".graymoon.json"),
            $$"""
            { "repositories": [ { "name": "repo", "repositoryUrl": "{{origin.Replace("\\", "\\\\")}}", "tag": "v1.0.0" } ] }
            """);

        var response = await SyncAsync(origin, applyRepositoryTagPin: true);

        Assert.False(response.Success);
        Assert.Contains("full commit hash", response.ErrorMessage);
        Assert.False(Directory.Exists(RepoPath()));
    }

    private string RepoPath() => Path.Combine(_root, "ws", "repo");

    private async Task<(string Origin, string TaggedCommit, string Tip)> CreateOriginAsync()
    {
        var origin = Path.Combine(_root, "repo.git");
        Directory.CreateDirectory(origin);
        await RunGitAsync(origin, "init --bare -b main");

        var seed = Path.Combine(_root, "seed");
        await RunGitAsync(_root, $"clone \"{origin}\" seed");
        await RunGitAsync(seed, "config user.email test@example.com");
        await RunGitAsync(seed, "config user.name Test");
        await File.WriteAllTextAsync(Path.Combine(seed, "a.txt"), "a\n");
        await RunGitAsync(seed, "add -A");
        await RunGitAsync(seed, "commit -m tagged");
        await RunGitAsync(seed, "tag v1.0.0");
        var tagged = await HeadAsync(seed);
        await File.WriteAllTextAsync(Path.Combine(seed, "b.txt"), "b\n");
        await RunGitAsync(seed, "add -A");
        await RunGitAsync(seed, "commit -m tip");
        await RunGitAsync(seed, "push origin main");
        await RunGitAsync(seed, "push origin v1.0.0");
        return (origin, tagged, await HeadAsync(seed));
    }

    private async Task WriteDefinitionAsync(string origin, string tag, string commit)
    {
        var workspace = Path.Combine(_root, "ws");
        Directory.CreateDirectory(workspace);
        var url = origin.Replace("\\", "\\\\");
        await File.WriteAllTextAsync(
            Path.Combine(workspace, ".graymoon.json"),
            $$"""
            { "repositories": [ { "name": "repo", "repositoryUrl": "{{url}}", "tag": "{{tag}}", "commit": "{{commit}}" } ] }
            """);
    }

    private Task<GrayMoon.Worker.Jobs.Response.SyncRepositoryResponse> SyncAsync(string origin, bool applyRepositoryTagPin)
        => new SyncRepositoryCommand(
            _git,
            _reader,
            new LibGit2SharpLocalGitSnapshotReader(),
            new CountingCsProjFileService(),
            CapabilityTestDoubles.RealFactory(_git))
            .ExecuteAsync(new SyncRepositoryRequest
            {
                WorkspaceRoot = _root,
                WorkspaceName = "ws",
                RepositoryName = "repo",
                RepositoryId = 1,
                WorkspaceId = 1,
                CloneUrl = origin,
                ApplyRepositoryTagPin = applyRepositoryTagPin,
                Capabilities = RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: false),
            });

    private static async Task<string> HeadAsync(string repoPath)
    {
        var (exit, stdout, stderr) = await GitVersionParityTests.RunProcessAsync("git", "rev-parse HEAD", repoPath);
        Assert.True(exit == 0, stderr);
        return stdout.Trim().ToLowerInvariant();
    }

    private static async Task RunGitAsync(string workingDirectory, string args)
    {
        var (exit, _, stderr) = await GitVersionParityTests.RunProcessAsync("git", args, workingDirectory);
        if (exit != 0)
            throw new InvalidOperationException($"git {args} failed: {stderr}");
    }
}
