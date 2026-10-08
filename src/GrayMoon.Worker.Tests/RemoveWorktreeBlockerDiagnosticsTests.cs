using System.Text.Json;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Remove Feature blocker diagnostics in <see cref="RemoveGitWorktreeCommand"/>: lock inspection runs only on the failure path
/// (in use / access denied / files left behind), never on a clean removal or a logical Git refusal, and an inspection failure
/// never hides the removal outcome. Uses real git like <see cref="RemoveWorktreeResidueTests"/>.
/// </summary>
public sealed class RemoveWorktreeBlockerDiagnosticsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-wtb-").FullName;
    private readonly GitCliRepositoryReader _reader;
    private readonly GitWorktreeService _worktrees;
    private readonly CreateGitWorktreeCommand _create;

    public RemoveWorktreeBlockerDiagnosticsTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _worktrees = new GitWorktreeService(runner, _reader, NullLogger<GitWorktreeService>.Instance);
        var git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, _reader, new LibGit2SharpGitIgnoreService());
        _create = new CreateGitWorktreeCommand(git, _worktrees);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Clean_removal_does_not_inspect_locks()
    {
        var inspector = new RecordingInspector();
        var remove = NewRemove(inspector);
        var (mainPath, worktreePath, featureRoot, storageRoot) = await CreateWorktreeAsync("clean");

        var result = await remove.ExecuteAsync(new RemoveGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            FeatureRootPath = featureRoot,
            FeatureStorageRoot = storageRoot,
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(inspector.Calls);
        Assert.Null(result.BlockingProcesses);
        Assert.Null(result.FailureKind);
    }

    [Fact]
    public async Task Logical_refusal_does_not_inspect_locks_and_reports_failure_kind()
    {
        var inspector = new RecordingInspector();
        var remove = NewRemove(inspector);
        var (mainPath, worktreePath, _, _) = await CreateWorktreeAsync("locked");
        await RunGitAsync(mainPath, $"worktree lock --reason testing \"{worktreePath}\"");

        var result = await remove.ExecuteAsync(new RemoveGitWorktreeRequest { MainRepositoryPath = mainPath, WorktreePath = worktreePath });

        Assert.False(result.Success);
        Assert.Equal(nameof(WorktreeRemovalFailureKind.WorktreeLocked), result.FailureKind);
        Assert.Empty(inspector.Calls);
        Assert.Null(result.BlockingProcesses);
    }

    [Fact]
    public async Task Uncommitted_changes_refusal_does_not_inspect_locks()
    {
        var inspector = new RecordingInspector();
        var remove = NewRemove(inspector);
        var (mainPath, worktreePath, _, _) = await CreateWorktreeAsync("dirty");
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "README.md"), "changed\n");

        var result = await remove.ExecuteAsync(new RemoveGitWorktreeRequest { MainRepositoryPath = mainPath, WorktreePath = worktreePath });

        Assert.False(result.Success);
        Assert.Equal(nameof(WorktreeRemovalFailureKind.UncommittedChanges), result.FailureKind);
        Assert.Empty(inspector.Calls);
    }

    [Fact]
    public async Task Residue_left_by_an_open_file_runs_lock_inspection_and_returns_blockers()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var inspector = new RecordingInspector(new FileLockInspectionResult(
            [new BlockingProcessInfo(4242, "Claude", @"C:\tools\claude.exe", null, BlockingProcessKind.Console, BlockingProcessReason.WorkingDirectory)],
            MayBeIncomplete: false,
            Diagnostic: null));
        var remove = NewRemove(inspector);
        var (mainPath, worktreePath, featureRoot, storageRoot) = await CreateWorktreeAsync("residue");
        var lockedFilePath = Path.Combine(worktreePath, "locked.bin");
        await File.WriteAllTextAsync(lockedFilePath, "locked\n");

        RemoveGitWorktreeResponse result;
        using (new FileStream(lockedFilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await remove.ExecuteAsync(new RemoveGitWorktreeRequest
            {
                MainRepositoryPath = mainPath,
                WorktreePath = worktreePath,
                Force = true,
                FeatureRootPath = featureRoot,
                FeatureStorageRoot = storageRoot,
            });
        }

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.ResidueRemaining);
        Assert.Equal(worktreePath, Assert.Single(inspector.Calls));
        var blocker = Assert.Single(result.BlockingProcesses!);
        Assert.Equal(4242, blocker.ProcessId);
        Assert.Equal("Claude", blocker.ProcessName);
        Assert.Equal(nameof(BlockingProcessReason.WorkingDirectory), blocker.Reason);
        Assert.Equal(nameof(BlockingProcessKind.Console), blocker.Kind);
    }

    [Fact]
    public async Task Inspection_failure_keeps_the_removal_outcome()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var remove = NewRemove(new ThrowingInspector());
        var (mainPath, worktreePath, featureRoot, storageRoot) = await CreateWorktreeAsync("throwing");
        var lockedFilePath = Path.Combine(worktreePath, "locked.bin");
        await File.WriteAllTextAsync(lockedFilePath, "locked\n");

        RemoveGitWorktreeResponse result;
        using (new FileStream(lockedFilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await remove.ExecuteAsync(new RemoveGitWorktreeRequest
            {
                MainRepositoryPath = mainPath,
                WorktreePath = worktreePath,
                Force = true,
                FeatureRootPath = featureRoot,
                FeatureStorageRoot = storageRoot,
            });
        }

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.ResidueRemaining);
        Assert.NotNull(result.ResidueMessage);
        Assert.Null(result.BlockingProcesses);
        Assert.True(result.BlockersMayBeIncomplete);
        Assert.NotNull(result.BlockersDiagnostic);
    }

    [Fact]
    public void Old_app_reading_new_response_json_ignores_blocker_fields()
    {
        var response = new RemoveGitWorktreeResponse
        {
            Success = true,
            ResidueRemaining = true,
            BlockingProcesses = [new BlockingProcessResponse { ProcessId = 1, ProcessName = "x", Kind = "Console", Reason = "OpenFile" }],
        };

        var json = JsonSerializer.Serialize(response, WorkerJsonOptions.SerializerOptions);

        Assert.Contains("\"blockingProcesses\"", json);
        Assert.Contains("\"processId\":1", json);
        Assert.Contains("\"reason\":\"OpenFile\"", json);
    }

    [Fact]
    public async Task InspectPathLocks_reports_missing_and_relative_paths_without_inspecting()
    {
        var inspector = new RecordingInspector();
        var command = new InspectPathLocksCommand(inspector, NullLogger<InspectPathLocksCommand>.Instance);
        var missing = Path.Combine(_root, "does-not-exist");

        var result = await command.ExecuteAsync(new InspectPathLocksRequest { Paths = [missing, "relative\\path"] });

        Assert.True(result.Success);
        Assert.Equal(2, result.Results!.Count);
        Assert.False(result.Results[0].Exists);
        Assert.Empty(result.Results[0].BlockingProcesses!);
        Assert.False(result.Results[1].Exists);
        Assert.True(result.Results[1].MayBeIncomplete);
        Assert.Empty(inspector.Calls);
    }

    [Fact]
    public async Task InspectPathLocks_inspects_existing_paths_in_request_order()
    {
        var inspector = new RecordingInspector(new FileLockInspectionResult(
            [new BlockingProcessInfo(7, "dotnet", null, null, BlockingProcessKind.Console, BlockingProcessReason.OpenFile)],
            MayBeIncomplete: true,
            Diagnostic: "Only the first 4000 files were checked."));
        var command = new InspectPathLocksCommand(inspector, NullLogger<InspectPathLocksCommand>.Instance);
        var a = Directory.CreateDirectory(Path.Combine(_root, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(_root, "b")).FullName;

        var result = await command.ExecuteAsync(new InspectPathLocksRequest { Paths = [a, b] });

        Assert.True(result.Success);
        Assert.Equal([a, b], inspector.Calls);
        Assert.All(result.Results!, r =>
        {
            Assert.True(r.Exists);
            Assert.True(r.MayBeIncomplete);
            Assert.Equal(7, Assert.Single(r.BlockingProcesses!).ProcessId);
        });
    }

    [Fact]
    public async Task InspectPathLocks_requires_paths()
    {
        var command = new InspectPathLocksCommand(new RecordingInspector(), NullLogger<InspectPathLocksCommand>.Instance);

        var result = await command.ExecuteAsync(new InspectPathLocksRequest());

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private RemoveGitWorktreeCommand NewRemove(IFileLockInspector inspector) =>
        new(_worktrees, inspector, NullLogger<RemoveGitWorktreeCommand>.Instance);

    private async Task<(string MainPath, string WorktreePath, string FeatureRoot, string StorageRoot)> CreateWorktreeAsync(string name)
    {
        var mainPath = Path.Combine(_root, "main-" + name);
        Directory.CreateDirectory(mainPath);
        await InitGitWithCommitAsync(mainPath);
        var head = await _reader.GetHeadCommitAsync(mainPath, CancellationToken.None);

        var storageRoot = Path.Combine(_root, "features");
        var featureRoot = Path.Combine(storageRoot, name + "-feat");
        var worktreePath = Path.Combine(featureRoot, "main-" + name);
        var created = await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            BranchName = name + "-feat",
            BaseCommitSha = head,
        });
        Assert.True(created.Success, created.ErrorMessage);
        return (mainPath, worktreePath, featureRoot, storageRoot);
    }

    private static async Task InitGitWithCommitAsync(string repoPath)
    {
        await RunGitAsync(repoPath, "init");
        await RunGitAsync(repoPath, "config user.email test@example.com");
        await RunGitAsync(repoPath, "config user.name Test");
        await RunGitAsync(repoPath, "checkout -B main");
        await File.WriteAllTextAsync(Path.Combine(repoPath, "README.md"), "x\n");
        await RunGitAsync(repoPath, "add README.md");
        await RunGitAsync(repoPath, "commit -m init");
    }

    private static async Task RunGitAsync(string repoPath, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git");
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {args} failed: {await p.StandardError.ReadToEndAsync()}");
    }

    private sealed class RecordingInspector(FileLockInspectionResult? result = null) : IFileLockInspector
    {
        public List<string> Calls { get; } = [];

        public Task<FileLockInspectionResult> InspectAsync(string path, CancellationToken cancellationToken = default)
        {
            lock (Calls)
                Calls.Add(path);
            return Task.FromResult(result ?? FileLockInspectionResult.Empty);
        }
    }

    private sealed class ThrowingInspector : IFileLockInspector
    {
        public Task<FileLockInspectionResult> InspectAsync(string path, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Restart Manager exploded");
    }
}
