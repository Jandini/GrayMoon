using GrayMoon.Abstractions.Worker;
using GrayMoon.App.Services.WorkspaceManifest;
using GrayMoon.Common.Manifest;
using Microsoft.Extensions.Logging.Abstractions;
using WorkspaceRepositoryTagPinChange = GrayMoon.Application.WorkspaceManifest.WorkspaceRepositoryTagPinChange;

namespace GrayMoon.App.Tests;

/// <summary>
/// A Workspace-wide checkout changes the tag pins of several repositories at once, each from its own scope. Every
/// change reads the Workspace definition, edits it and writes it back, so the changes must not overlap.
/// </summary>
public sealed class WorkspaceManifestTagPinConcurrencyTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task Two_pin_changes_for_one_Workspace_do_not_overwrite_each_other()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        var built = await fixture.CreateService().BuildFromDatabaseAsync(workspace.WorkspaceId);
        var pinned = built with
        {
            Repositories = built.Repositories.Select(repository => repository with { Tag = "v1.0.0", Commit = Sha }).ToList()
        };
        var bridge = new FileBridge(new WorkspaceManifestFile().Serialize(pinned));

        // Two services, as two scopes would have. The first one is held inside its read of the file.
        var first = CreateService(fixture, bridge).SetRepositoryTagPinsAsync(
            workspace.WorkspaceId, [new WorkspaceRepositoryTagPinChange("https://github.com/acme/Api.git", null, null)]);
        await bridge.FirstReadStarted.Task;
        var second = CreateService(fixture, bridge).SetRepositoryTagPinsAsync(
            workspace.WorkspaceId, [new WorkspaceRepositoryTagPinChange("https://github.com/acme/Web.git", null, null)]);

        // The second change waits for the first one instead of reading the file the first is about to replace.
        Assert.Equal(1, bridge.Reads);
        bridge.ReleaseFirstRead.SetResult();

        Assert.True((await first).Success);
        Assert.True((await second).Success);
        Assert.Equal(2, bridge.Reads);
        Assert.True(new WorkspaceManifestFile().TryParse(bridge.Content, out var result, out var error), error);
        Assert.All(result!.Repositories, repository =>
        {
            Assert.Null(repository.Tag);
            Assert.Null(repository.Commit);
        });
    }

    [Fact]
    public async Task Pin_changes_for_different_Workspaces_do_not_wait_for_each_other()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        var other = await fixture.AddWorkspaceAsync("other");
        var built = await fixture.CreateService().BuildFromDatabaseAsync(workspace.WorkspaceId);
        var bridge = new FileBridge(new WorkspaceManifestFile().Serialize(built));

        var held = CreateService(fixture, bridge).SetRepositoryTagPinsAsync(
            workspace.WorkspaceId, [new WorkspaceRepositoryTagPinChange("https://github.com/acme/Api.git", null, null)]);
        await bridge.FirstReadStarted.Task;

        // The fake path resolver reports the one Workspace repository for every Workspace, so this call reads the file too.
        var free = CreateService(fixture, bridge).SetRepositoryTagPinsAsync(
            other.WorkspaceId, [new WorkspaceRepositoryTagPinChange("https://github.com/acme/Api.git", null, null)]);

        Assert.True((await free).Success);
        Assert.Equal(2, bridge.Reads);
        bridge.ReleaseFirstRead.SetResult();
        Assert.True((await held).Success);
    }

    [Fact]
    public async Task A_failed_pin_change_releases_the_Workspace_for_the_next_one()
    {
        await using var fixture = await ManifestTestFixture.CreateAsync();
        var workspace = await fixture.SeedWorkspaceWithRootRepositoryAsync();
        var built = await fixture.CreateService().BuildFromDatabaseAsync(workspace.WorkspaceId);
        var bridge = new FileBridge(new WorkspaceManifestFile().Serialize(built));
        bridge.ReleaseFirstRead.SetResult();
        var service = CreateService(fixture, bridge);

        var failed = await service.SetRepositoryTagPinsAsync(
            workspace.WorkspaceId, [new WorkspaceRepositoryTagPinChange("https://github.com/acme/Missing.git", "v1.0.0", Sha)]);
        var next = await service.SetRepositoryTagPinsAsync(
            workspace.WorkspaceId, [new WorkspaceRepositoryTagPinChange("https://github.com/acme/Api.git", "v1.0.0", Sha)]);

        Assert.False(failed.Success);
        Assert.True(next.Success, next.Error);
        Assert.Contains("\"tag\": \"v1.0.0\"", bridge.Content);
    }

    private static WorkspaceManifestService CreateService(ManifestTestFixture fixture, IWorkerBridge bridge) => new(
        fixture.Factory,
        new WorkspaceManifestFile(),
        bridge,
        new FakeWorkspacePathResolver(fixture.Factory),
        new FakeFeatureContextResolver(),
        NullLogger<WorkspaceManifestService>.Instance);

    /// <summary>A Worker holding one file. The first read does not return until the test releases it.</summary>
    private sealed class FileBridge(string content) : IWorkerBridge
    {
        private readonly object _sync = new();
        private string _content = content;
        private int _reads;

        public TaskCompletionSource FirstReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Reads => Volatile.Read(ref _reads);

        public string Content
        {
            get
            {
                lock (_sync)
                    return _content;
            }
        }

        public bool IsWorkerConnected => true;

        public string? GetUnavailableReason() => null;

        public async Task<WorkerCommandResponse> SendCommandAsync(string command, object args, CancellationToken cancellationToken = default)
        {
            if (command == "GetFileContents")
            {
                string snapshot;
                lock (_sync)
                    snapshot = _content;
                if (Interlocked.Increment(ref _reads) == 1)
                {
                    FirstReadStarted.TrySetResult();
                    await ReleaseFirstRead.Task.WaitAsync(cancellationToken);
                }

                return new WorkerCommandResponse(true, new { content = snapshot }, null);
            }

            if (command == "WriteRepositoryFile")
            {
                var written = ManifestTestFixture.ToJson(args).GetProperty("content").GetString()!;
                lock (_sync)
                    _content = written;
            }

            return new WorkerCommandResponse(true, new { success = true, written = true }, null);
        }
    }
}
