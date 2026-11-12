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
            Path.Combine(workspace, "graymoon.json"),
            $$"""
            { "repositories": [ { "name": "repo", "repositoryUrl": "{{origin.Replace("\\", "\\\\")}}", "tag": "v1.0.0" } ] }
            """);

        var response = await SyncAsync(origin, applyRepositoryTagPin: true);

        Assert.False(response.Success);
        Assert.Contains("full commit hash", response.ErrorMessage);
        Assert.False(Directory.Exists(RepoPath()));
    }

    [Fact]
    public async Task Fresh_clone_of_a_read_only_repository_is_on_its_branch_at_the_recorded_commit_and_behind_origin()
    {
        var (origin, recorded, tip) = await CreateOriginAsync();
        await WriteReferenceDefinitionAsync(origin, "\"reference\": true, \"branch\": \"main\"", recorded);

        var response = await SyncAsync(origin, applyRepositoryTagPin: true);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal(recorded, await HeadAsync(RepoPath()));
        Assert.NotEqual(tip, recorded);
        Assert.Equal("main", await GitOutputAsync(RepoPath(), "rev-parse --abbrev-ref HEAD"));
        Assert.Equal("origin/main", await GitOutputAsync(RepoPath(), "rev-parse --abbrev-ref main@{upstream}"));
        // The commit the remote gained since shows as incoming; nothing is outgoing.
        Assert.Equal("1", await GitOutputAsync(RepoPath(), "rev-list --count HEAD..origin/main"));
        Assert.Equal("0", await GitOutputAsync(RepoPath(), "rev-list --count origin/main..HEAD"));
        Assert.Equal("main", response.Branch);
        Assert.Null(response.Tag);
    }

    [Fact]
    public async Task Fresh_clone_of_a_read_only_repository_takes_the_workspace_value_when_the_entry_has_none()
    {
        var (origin, recorded, _) = await CreateOriginAsync();
        await WriteReferenceDefinitionAsync(origin, "\"branch\": \"main\"", recorded, workspace: "\"reference\": true");

        var response = await SyncAsync(origin, applyRepositoryTagPin: true);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal(recorded, await HeadAsync(RepoPath()));
        Assert.Equal("main", await GitOutputAsync(RepoPath(), "rev-parse --abbrev-ref HEAD"));
    }

    [Fact]
    public async Task A_recorded_branch_the_remote_no_longer_has_is_created_without_an_upstream()
    {
        var (origin, recorded, _) = await CreateOriginAsync();
        await WriteReferenceDefinitionAsync(origin, "\"reference\": true, \"branch\": \"release/gone\"", recorded);

        var response = await SyncAsync(origin, applyRepositoryTagPin: true);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal(recorded, await HeadAsync(RepoPath()));
        Assert.Equal("release/gone", await GitOutputAsync(RepoPath(), "rev-parse --abbrev-ref HEAD"));
        var (exit, _, _) = await GitVersionParityTests.RunProcessAsync("git", "rev-parse --abbrev-ref release/gone@{upstream}", RepoPath());
        Assert.NotEqual(0, exit);
    }

    [Fact]
    public async Task Existing_checkout_of_a_read_only_repository_is_not_moved_to_the_recorded_commit()
    {
        var (origin, recorded, tip) = await CreateOriginAsync();
        await WriteReferenceDefinitionAsync(origin, "\"reference\": true, \"branch\": \"main\"", recorded);
        await RunGitAsync(Path.Combine(_root, "ws"), $"clone \"{origin}\" repo");

        var response = await SyncAsync(origin, applyRepositoryTagPin: true);

        Assert.True(response.Success, response.ErrorMessage);
        Assert.Equal(tip, await HeadAsync(RepoPath()));
    }

    [Fact]
    public async Task Missing_recorded_commit_of_a_read_only_repository_fails_the_sync_and_removes_the_clone()
    {
        var (origin, _, _) = await CreateOriginAsync();
        await WriteReferenceDefinitionAsync(origin, "\"reference\": true, \"branch\": \"main\"", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        var response = await SyncAsync(origin, applyRepositoryTagPin: true);

        Assert.False(response.Success);
        Assert.Contains("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", response.ErrorMessage);
        Assert.False(Directory.Exists(RepoPath()));
    }

    [Fact]
    public async Task A_branch_recorded_for_a_writable_repository_fails_the_sync_and_removes_the_clone()
    {
        var (origin, recorded, _) = await CreateOriginAsync();
        await WriteReferenceDefinitionAsync(origin, "\"reference\": false, \"branch\": \"main\"", recorded);

        var response = await SyncAsync(origin, applyRepositoryTagPin: true);

        Assert.False(response.Success);
        Assert.Contains("not read only", response.ErrorMessage);
        Assert.False(Directory.Exists(RepoPath()));
    }

    private async Task WriteReferenceDefinitionAsync(string origin, string revision, string commit, string workspace = "")
    {
        var workspacePath = Path.Combine(_root, "ws");
        Directory.CreateDirectory(workspacePath);
        var url = origin.Replace("\\", "\\\\");
        await File.WriteAllTextAsync(
            Path.Combine(workspacePath, "graymoon.json"),
            $$"""
            { "version": 2, "workspace": { {{workspace}} }, "repositories": [ { "name": "repo", "repositoryUrl": "{{url}}", {{revision}}, "commit": "{{commit}}" } ] }
            """);
    }

    private static async Task<string> GitOutputAsync(string repoPath, string args)
    {
        var (exit, stdout, stderr) = await GitVersionParityTests.RunProcessAsync("git", args, repoPath);
        Assert.True(exit == 0, stderr);
        return stdout.Trim();
    }

    private string RepoPath() => Path.Combine(_root, "ws", "repo");

    private Task<(string Origin, string TaggedCommit, string Tip)> CreateOriginAsync()
    {
        // The same two-commit history in every test: built once, with its two commit ids recorded beside it.
        GitTemplateCache.CopyTo("tag-pin-origin", _root, template =>
        {
            var templateOrigin = Path.Combine(template, "repo.git");
            Directory.CreateDirectory(templateOrigin);
            GitConfigTestSupport.Git(templateOrigin, "init --bare -b main");

            var seed = Path.Combine(template, "seed");
            GitConfigTestSupport.Git(template, $"clone \"{templateOrigin}\" seed");
            GitConfigTestSupport.Git(seed, "config user.email test@example.com");
            GitConfigTestSupport.Git(seed, "config user.name Test");
            File.WriteAllText(Path.Combine(seed, "a.txt"), "a\n");
            GitConfigTestSupport.Git(seed, "add -A");
            GitConfigTestSupport.Git(seed, "commit -m tagged");
            GitConfigTestSupport.Git(seed, "tag v1.0.0");
            var tagged = GitConfigTestSupport.Git(seed, "rev-parse HEAD").Trim().ToLowerInvariant();
            File.WriteAllText(Path.Combine(seed, "b.txt"), "b\n");
            GitConfigTestSupport.Git(seed, "add -A");
            GitConfigTestSupport.Git(seed, "commit -m tip");
            GitConfigTestSupport.Git(seed, "push origin main");
            GitConfigTestSupport.Git(seed, "push origin v1.0.0");
            var tip = GitConfigTestSupport.Git(seed, "rev-parse HEAD").Trim().ToLowerInvariant();
            File.WriteAllText(Path.Combine(template, "commits.txt"), tagged + "\n" + tip);
        });

        var commits = File.ReadAllText(Path.Combine(_root, "commits.txt")).Split('\n');
        return Task.FromResult((Path.Combine(_root, "repo.git"), commits[0], commits[1]));
    }

    private async Task WriteDefinitionAsync(string origin, string tag, string commit)
    {
        var workspace = Path.Combine(_root, "ws");
        Directory.CreateDirectory(workspace);
        var url = origin.Replace("\\", "\\\\");
        await File.WriteAllTextAsync(
            Path.Combine(workspace, "graymoon.json"),
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
