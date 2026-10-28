using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Services;
using GrayMoon.Worker.Services.GitChanges;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

[Trait("Category", "WatcherReliability")]
public sealed class EnsureGitChangesWatchersCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-ensure-").FullName;
    private readonly FakeRepositoryGitChangesService _service = new();
    private readonly GitStatusRefreshCoordinator _coordinator;
    private readonly GitRepositoryWatcherManager _manager;
    private readonly EnsureGitChangesWatchersCommand _command;

    public EnsureGitChangesWatchersCommandTests()
    {
        var options = new GitChangesOptions { WatcherDebounceMilliseconds = 50 };
        _coordinator = new GitStatusRefreshCoordinator(
            _service, new GitChangesSnapshotCache(), Options.Create(options), NullLogger<GitStatusRefreshCoordinator>.Instance);
        _manager = new GitRepositoryWatcherManager(
            _coordinator, new GitChangesSnapshotCache(), new GitChangesRepositoryRegistry(), Options.Create(options),
            NullLoggerFactory.Instance, NullLogger<GitRepositoryWatcherManager>.Instance,
            new RepositoryAccess(NullLogger<RepositoryAccess>.Instance));
        _command = new EnsureGitChangesWatchersCommand(
            new GitChangesRepositoryRegistry(), _manager, NullLogger<EnsureGitChangesWatchersCommand>.Instance);
    }

    public void Dispose()
    {
        _manager.Dispose();
        _coordinator.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    private EnsureGitChangesWatchersRequest Request(params (int Id, string? Name)[] repos) => new()
    {
        WorkspaceRoot = _root,
        WorkspaceName = "ws",
        WorkspaceId = 7,
        Repositories = repos.Select(r => new EnsureGitChangesWatchersRepository { RepositoryId = r.Id, RepositoryName = r.Name }).ToList(),
    };

    private void MakeRepo(string name) => Directory.CreateDirectory(Path.Combine(_root, "ws", name, ".git"));

    [Fact]
    public async Task Mixed_batch_gives_partial_results_and_renews_the_rest()
    {
        MakeRepo("a");
        MakeRepo("b");

        var response = await _command.ExecuteAsync(Request((1, "a"), (2, "gone"), (3, "b"), (4, null), (5, "..")));

        Assert.Equal(["Created", "Missing", "Created", "Invalid", "Invalid"], response.Results.Select(r => r.Status));
        Assert.Equal(2, _manager.ActiveWatcherCount);
        Assert.Equal(600, response.EffectiveGraceSeconds);
        Assert.False(string.IsNullOrEmpty(response.WorkerInstanceId));
    }

    [Fact]
    public async Task Repeated_ensure_renews_and_runs_no_git()
    {
        MakeRepo("a");
        await _command.ExecuteAsync(Request((1, "a")));
        await Task.Delay(400);
        var scans = _service.CallCount;

        var second = await _command.ExecuteAsync(Request((1, "a")));
        await Task.Delay(300);

        Assert.Equal("Renewed", second.Results.Single().Status);
        Assert.Equal(scans, _service.CallCount);
        Assert.Equal(1, _manager.ActiveWatcherCount);
    }

    [Fact]
    public async Task The_same_folder_listed_twice_is_renewed_once()
    {
        MakeRepo("a");

        var response = await _command.ExecuteAsync(Request((1, "a"), (2, "a")));

        Assert.Equal(["Created", "Renewed"], response.Results.Select(r => r.Status));
        Assert.Equal(1, _manager.ActiveWatcherCount);
    }

    [Fact]
    public async Task Workspace_role_repository_resolves_to_the_workspace_folder()
    {
        Directory.CreateDirectory(Path.Combine(_root, "ws", ".git"));
        var request = Request((9, "ws-repo"));
        request.WorkspaceRepositoryName = "ws-repo";

        var response = await _command.ExecuteAsync(request);

        Assert.Equal("Created", response.Results.Single().Status);
        Assert.True(_manager.TryGetCoverage(Path.Combine(_root, "ws"), out _));
    }

    [Fact]
    public async Task Same_worker_instance_id_across_calls()
    {
        MakeRepo("a");
        var first = await _command.ExecuteAsync(Request((1, "a")));
        var second = await _command.ExecuteAsync(Request((1, "a")));
        Assert.Equal(first.WorkerInstanceId, second.WorkerInstanceId);
    }
}
