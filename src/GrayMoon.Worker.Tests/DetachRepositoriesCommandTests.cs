using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;
using GrayMoon.Worker.Services.GitChanges;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// "These repositories leave this Workspace": the Worker lets go of its file watchers on the folders and removes
/// GrayMoon's hooks, in one command, and touches nothing else.
/// </summary>
[Trait("Category", "PullRequest")]
public sealed class DetachRepositoriesCommandTests : IDisposable
{
    private const string Marker = "# Created by GrayMoon.Agent";
    private const string WorkspaceName = "ws";
    private const int WorkspaceId = 7;

    private readonly string _root = TestDirectory.Create("detach");
    private readonly RepositoryAccess _access = new(NullLogger<RepositoryAccess>.Instance);
    private readonly GitService _git;
    private readonly GitChangesRepositoryRegistry _registry = new();
    private readonly GitStatusRefreshCoordinator _coordinator;
    private readonly GitRepositoryWatcherManager _watchers;
    private readonly FakeWorkspaceCapabilityProvider _capabilities = new(RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: false));
    private readonly DetachRepositoriesCommand _command;

    public DetachRepositoriesCommandTests()
    {
        var inner = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var commandLine = new RepositoryAccessCommandLineService(inner, _access);
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, reader, new LibGit2SharpGitIgnoreService(), _access);

        var options = new GitChangesOptions { WatcherDebounceMilliseconds = 50 };
        var cache = new GitChangesSnapshotCache();
        _coordinator = new GitStatusRefreshCoordinator(
            new FakeRepositoryGitChangesService { Delay = TimeSpan.Zero }, cache,
            Options.Create(options), NullLogger<GitStatusRefreshCoordinator>.Instance);
        _watchers = new GitRepositoryWatcherManager(
            _coordinator, cache, _registry,
            Options.Create(options), NullLoggerFactory.Instance, NullLogger<GitRepositoryWatcherManager>.Instance, _access);
        _command = new DetachRepositoriesCommand(_git, _access, _watchers, _capabilities, NullLogger<DetachRepositoriesCommand>.Instance);
    }

    public void Dispose()
    {
        _watchers.Dispose();
        _coordinator.Dispose();
        TestDirectory.Delete(_root);
    }

    private string WorkspacePath => Path.Combine(_root, WorkspaceName);

    private string RepoPath(string name) => Path.Combine(WorkspacePath, name);

    private async Task<string> NewRepositoryAsync(string name, int repositoryId, bool watch = true, int workspaceId = WorkspaceId)
    {
        var path = RepoPath(name);
        GitScenarios.CommittedRepository(path);
        await _git.WriteSyncHooksAsync(path, workspaceId, repositoryId, CancellationToken.None);
        if (watch)
        {
            _registry.Register(path, workspaceId, repositoryId);
            _watchers.Touch(path);
        }

        return path;
    }

    private DetachRepositoriesRequest Request(bool workspaceRemoved, params (int Id, string Name)[] repositories) => new()
    {
        WorkspaceRoot = _root,
        WorkspaceName = WorkspaceName,
        WorkspaceId = WorkspaceId,
        WorkspaceRemoved = workspaceRemoved,
        Repositories = [.. repositories.Select(r => new DetachRepositoriesRepository { RepositoryId = r.Id, RepositoryName = r.Name })],
    };

    private static bool HasGrayMoonHook(string repoPath) =>
        Directory.Exists(Path.Combine(repoPath, ".git", "hooks"))
        && Directory.EnumerateFiles(Path.Combine(repoPath, ".git", "hooks"))
            .Any(file => File.ReadAllText(file).Contains(Marker, StringComparison.Ordinal));

    private bool IsWatched(string path) => _watchers.GetMonitored().Any(m => string.Equals(m.Path, GitChangesSnapshotCache.NormalizeKey(path), StringComparison.OrdinalIgnoreCase));

    private static DetachRepositoriesResult ResultFor(DetachRepositoriesResponse response, int repositoryId) =>
        Assert.Single(response.Results, r => r.RepositoryId == repositoryId);

    [Fact]
    public async Task Monitoring_reports_each_watched_folder_with_its_workspace_and_repository()
    {
        var a = await NewRepositoryAsync("a", 1);
        var other = await NewRepositoryAsync("other", 9, workspaceId: 99);

        var mine = _watchers.GetMonitored(WorkspaceId);
        var all = _watchers.GetMonitored();

        var entry = Assert.Single(mine);
        Assert.Equal(GitChangesSnapshotCache.NormalizeKey(a), entry.Path, ignoreCase: true);
        Assert.Equal(WorkspaceId, entry.WorkspaceId);
        Assert.Equal(1, entry.RepositoryId);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, m => m.WorkspaceId == 99 && string.Equals(m.Path, GitChangesSnapshotCache.NormalizeKey(other), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Detach_removes_the_hooks_releases_the_watchers_and_leaves_the_folders()
    {
        var a = await NewRepositoryAsync("a", 1);
        var b = await NewRepositoryAsync("b", 2);
        Assert.True(IsWatched(a));

        var response = await _command.ExecuteAsync(Request(workspaceRemoved: true, (1, "a"), (2, "b")));

        Assert.True(response.Success);
        Assert.All(response.Results, r => Assert.Equal("Removed", r.Outcome));
        Assert.False(HasGrayMoonHook(a));
        Assert.False(HasGrayMoonHook(b));
        Assert.False(IsWatched(a));
        Assert.False(IsWatched(b));
        Assert.Equal(2, response.ReleasedWatchers.Count);
        Assert.Empty(_watchers.GetMonitored(WorkspaceId));
        Assert.False(_registry.TryGet(a, out _, out _));
        Assert.True(File.Exists(Path.Combine(a, "README.md")), "the working tree is not touched");
        Assert.Equal([WorkspaceId], _capabilities.Invalidated);

        // Nothing is claimed afterwards, and the folder can be removed at once: no handle of the Worker's is left.
        Assert.False(_access.IsUnderRemoval(a));
        TestDirectory.Delete(a);
        Assert.False(Directory.Exists(a));
    }

    [Fact]
    public async Task Removing_a_workspace_also_releases_folders_it_watches_that_were_not_named()
    {
        await NewRepositoryAsync("a", 1);
        // A folder of the same Workspace that the request does not list (as a Feature worktree would be).
        var elsewhere = Path.Combine(_root, "features", "F-1", "a");
        GitScenarios.CommittedRepository(elsewhere);
        _registry.Register(elsewhere, WorkspaceId, 1);
        _watchers.Touch(elsewhere);

        var response = await _command.ExecuteAsync(Request(workspaceRemoved: true, (1, "a")));

        Assert.True(response.Success);
        Assert.False(IsWatched(elsewhere));
        Assert.Empty(_watchers.GetMonitored(WorkspaceId));
    }

    [Fact]
    public async Task A_repository_that_stays_and_another_workspace_keep_their_watchers_and_hooks()
    {
        var leaving = await NewRepositoryAsync("leaving", 1);
        var staying = await NewRepositoryAsync("staying", 2);
        var other = Path.Combine(_root, "other-ws", "x");
        GitScenarios.CommittedRepository(other);
        await _git.WriteSyncHooksAsync(other, 99, 5, CancellationToken.None);
        _registry.Register(other, 99, 5);
        _watchers.Touch(other);

        var response = await _command.ExecuteAsync(Request(workspaceRemoved: false, (1, "leaving")));

        Assert.True(response.Success);
        Assert.False(HasGrayMoonHook(leaving));
        Assert.False(IsWatched(leaving));
        Assert.True(HasGrayMoonHook(staying));
        Assert.True(IsWatched(staying));
        Assert.True(HasGrayMoonHook(other));
        Assert.True(IsWatched(other));
        Assert.Empty(_capabilities.Invalidated);
    }

    [Fact]
    public async Task A_source_folder_under_a_watched_workspace_root_can_be_deleted_once_it_is_detached()
    {
        // The Workspace repository's watcher is recursive from the Workspace folder, so it also sees every Source
        // folder beneath it, and it stays when one Source leaves. It must not keep that Source's folder locked.
        GitScenarios.CommittedRepository(WorkspacePath);
        _registry.Register(WorkspacePath, WorkspaceId, 1);
        _watchers.Touch(WorkspacePath);
        var source = await NewRepositoryAsync("source", 2);
        var request = Request(workspaceRemoved: false, (2, "source"));
        request.WorkspaceRepositoryName = "ws-root";

        var response = await _command.ExecuteAsync(request);

        Assert.True(response.Success);
        Assert.False(IsWatched(source));
        Assert.True(IsWatched(WorkspacePath), "the Workspace repository stays and keeps its watcher");
        TestDirectory.Delete(source);
        Assert.False(Directory.Exists(source), "the root watcher must not block deleting the folder that left");
        Assert.True(Directory.Exists(WorkspacePath));
    }

    [Fact]
    public async Task After_the_command_the_folder_can_be_watched_again_when_the_app_asks()
    {
        var a = await NewRepositoryAsync("a", 1);

        await _command.ExecuteAsync(Request(workspaceRemoved: false, (1, "a")));
        Assert.False(IsWatched(a));

        Assert.Equal(WatcherTouchResult.Created, _watchers.Touch(a));
        Assert.True(IsWatched(a));
    }

    [Fact]
    public async Task A_missing_folder_and_a_plain_folder_are_successes_and_an_invalid_name_fails_only_itself()
    {
        var a = await NewRepositoryAsync("a", 1);
        Directory.CreateDirectory(RepoPath("plain"));

        var response = await _command.ExecuteAsync(Request(workspaceRemoved: false, (1, "a"), (2, "gone"), (3, "plain"), (4, "..")));

        Assert.False(response.Success);
        Assert.Equal("Removed", ResultFor(response, 1).Outcome);
        Assert.Equal("NotFound", ResultFor(response, 2).Outcome);
        Assert.Equal("NotARepository", ResultFor(response, 3).Outcome);
        var invalid = ResultFor(response, 4);
        Assert.Equal("Failed", invalid.Outcome);
        Assert.Equal("InvalidName", invalid.FailureKind);
        Assert.False(HasGrayMoonHook(a));
    }

    [Fact]
    public async Task One_repository_that_fails_does_not_stop_the_others_and_names_its_file()
    {
        if (!OperatingSystem.IsWindows())
            return; // An open handle does not block a delete on Linux.

        var a = await NewRepositoryAsync("a", 1, watch: false);
        var b = await NewRepositoryAsync("b", 2, watch: false);
        var locked = Path.Combine(a, ".git", "hooks", "post-commit");

        DetachRepositoriesResponse response;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            response = await _command.ExecuteAsync(Request(workspaceRemoved: true, (1, "a"), (2, "b")));
        }

        Assert.False(response.Success);
        var failed = ResultFor(response, 1);
        Assert.Equal("Failed", failed.Outcome);
        Assert.Equal("HookNotDeleted", failed.FailureKind);
        Assert.Equal(locked, failed.FailedPath, ignoreCase: true);
        Assert.Equal("Removed", ResultFor(response, 2).Outcome);
        Assert.False(HasGrayMoonHook(b));
        Assert.True(HasGrayMoonHook(a));
        Assert.Empty(_capabilities.Invalidated);

        // Fix the cause, send it again: the job finishes.
        var retry = await _command.ExecuteAsync(Request(workspaceRemoved: true, (1, "a"), (2, "b")));
        Assert.True(retry.Success);
        Assert.Equal("NothingToRemove", ResultFor(retry, 2).Outcome);
        Assert.False(HasGrayMoonHook(a));
    }

    [Fact]
    public async Task The_workspace_repository_name_resolves_to_the_workspace_folder_itself()
    {
        GitScenarios.CommittedRepository(WorkspacePath);
        await _git.WriteSyncHooksAsync(WorkspacePath, WorkspaceId, 1, CancellationToken.None);
        var source = await NewRepositoryAsync("root-repo", 2, watch: false);

        // The Source folder that shares the Workspace repository's name is addressed by leaving the root name out.
        var request = Request(workspaceRemoved: false, (2, "root-repo"));
        var response = await _command.ExecuteAsync(request);

        Assert.True(response.Success);
        Assert.False(HasGrayMoonHook(source));
        Assert.True(HasGrayMoonHook(WorkspacePath));

        request.WorkspaceRepositoryName = "root-repo";
        var root = await _command.ExecuteAsync(request);
        Assert.True(root.Success);
        Assert.False(HasGrayMoonHook(WorkspacePath));
    }
}

[Trait("Category", "PullRequest")]
public sealed class WorkspaceFolderCommandsTests : IDisposable
{
    private readonly string _root = TestDirectory.Create("folder-commands");
    private readonly InspectWorkspaceFolderCommand _inspect;

    public WorkspaceFolderCommandsTests()
    {
        var inner = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(inner, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _inspect = new InspectWorkspaceFolderCommand(new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance));
    }

    public void Dispose() => TestDirectory.Delete(_root);

    [Fact]
    public async Task List_returns_the_folder_names_under_the_root_and_nothing_for_a_missing_root()
    {
        Directory.CreateDirectory(Path.Combine(_root, "beta"));
        Directory.CreateDirectory(Path.Combine(_root, "Alpha"));
        File.WriteAllText(Path.Combine(_root, "file.txt"), "x");
        var command = new ListWorkspaceRootFoldersCommand();

        var listed = await command.ExecuteAsync(new ListWorkspaceRootFoldersRequest { WorkspaceRoot = _root });
        var missing = await command.ExecuteAsync(new ListWorkspaceRootFoldersRequest { WorkspaceRoot = Path.Combine(_root, "nope") });

        Assert.Equal(["Alpha", "beta"], listed.Folders);
        Assert.Empty(missing.Folders);
    }

    [Fact]
    public async Task Inspect_reports_the_root_repository_its_children_and_the_definition_text()
    {
        var folder = Path.Combine(_root, "ws");
        GitScenarios.CommittedRepository(folder);
        GitConfigTestSupport.Git(folder, "remote add origin https://example.test/org/root.git");
        GitScenarios.CommittedRepository(Path.Combine(folder, "child"));
        GitConfigTestSupport.Git(Path.Combine(folder, "child"), "remote add origin https://example.test/org/child.git");
        Directory.CreateDirectory(Path.Combine(folder, "not-a-repo"));
        File.WriteAllText(Path.Combine(folder, "graymoon.json"), "{ \"x\": 1 }");

        var response = await _inspect.ExecuteAsync(new InspectWorkspaceFolderRequest { WorkspaceRoot = _root, WorkspaceName = "ws" });

        Assert.True(response.Exists);
        Assert.Equal("https://example.test/org/root.git", response.RootRepository?.OriginUrl);
        var child = Assert.Single(response.Repositories);
        Assert.Equal("child", child.Name);
        Assert.Equal("https://example.test/org/child.git", child.OriginUrl);
        Assert.True(response.DefinitionFound);
        Assert.Equal("{ \"x\": 1 }", response.DefinitionContent);
        Assert.Null(response.DefinitionError);
    }

    [Fact]
    public async Task Inspect_of_a_plain_folder_has_no_root_repository_and_a_missing_folder_does_not_exist()
    {
        var folder = Path.Combine(_root, "plain");
        GitScenarios.CommittedRepository(Path.Combine(folder, "child"));

        var plain = await _inspect.ExecuteAsync(new InspectWorkspaceFolderRequest { WorkspaceRoot = _root, WorkspaceName = "plain" });
        var missing = await _inspect.ExecuteAsync(new InspectWorkspaceFolderRequest { WorkspaceRoot = _root, WorkspaceName = "missing" });

        Assert.True(plain.Exists);
        Assert.Null(plain.RootRepository);
        Assert.Single(plain.Repositories);
        Assert.False(plain.DefinitionFound);
        Assert.False(missing.Exists);
        Assert.False(Directory.Exists(Path.Combine(_root, "missing")), "inspecting never creates the folder");
    }

    [Fact]
    public async Task Inspect_refuses_a_name_that_is_not_one_folder()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _inspect.ExecuteAsync(new InspectWorkspaceFolderRequest { WorkspaceRoot = _root, WorkspaceName = ".." }));
    }
}
