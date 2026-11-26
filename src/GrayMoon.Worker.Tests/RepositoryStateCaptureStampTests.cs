using GrayMoon.Abstractions.Notifications;
using GrayMoon.Abstractions.Workspaces;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Every snapshot the state probe returns says when it was read, on this Worker process's own counter. The App
/// orders two hook snapshots by that, not by which one arrived first.
/// </summary>
public sealed class RepositoryStateCaptureStampTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-capture-").FullName;

    public void Dispose() => TestDirectory.Delete(_root);

    [Fact]
    public async Task Two_captures_of_one_process_share_the_session_and_do_not_overlap_when_taken_one_after_the_other()
    {
        var repo = Path.Combine(_root, "repo");
        GitScenarios.CommittedRepository(repo);
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        var git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, reader, new LibGit2SharpGitIgnoreService());
        var probe = new RepositoryStateProbe(reader, new CountingCsProjFileService(), CapabilityTestDoubles.RealFactory(git));
        var options = new RepositoryStateProbeOptions { Capabilities = RepositoryOperationCapabilities.For(calculateVersion: false, discoverProjects: false) };

        var first = (await probe.CaptureAsync(repo, options)).Snapshot;
        var second = (await probe.CaptureAsync(repo, options)).Snapshot;

        Assert.Equal(RepositoryStateCaptureClock.Session, first.CaptureSession);
        Assert.Equal(first.CaptureSession, second.CaptureSession);
        Assert.True(first.CaptureStart < first.CaptureEnd);
        // The second was read entirely after the first: its interval starts after the first one ended.
        Assert.True(second.CaptureStart > first.CaptureEnd);
        Assert.True(first.IdentityProbed);
    }

    [Fact]
    public async Task A_capture_of_a_folder_that_is_not_there_carries_no_capture_evidence()
    {
        // Nothing was read, so there is nothing to order: the snapshot probes no group and claims no capture.
        var probe = new RepositoryStateProbe(null!, new CountingCsProjFileService(), null!);

        var snapshot = (await probe.CaptureAsync(Path.Combine(_root, "missing"), new RepositoryStateProbeOptions())).Snapshot;

        Assert.Null(snapshot.CaptureSession);
        Assert.False(snapshot.IdentityProbed);
    }
}
