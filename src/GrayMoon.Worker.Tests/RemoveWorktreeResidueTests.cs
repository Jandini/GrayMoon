using System.Text.Json;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// D1: Worker-side residue cleanup after <c>git worktree remove</c>. Guard tests exercise
/// <see cref="GitWorktreeService.ValidateResidueRemovalGuards"/> directly (pure, no git needed); the rest use
/// real git, matching the pattern in <see cref="GitWorktreeCommandTests"/>.
/// </summary>
public sealed class RemoveWorktreeResidueTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-wtr-").FullName;
    private readonly GitService _git;
    private GitCliRepositoryReader _reader = null!;
    private GitWorktreeService _worktrees = null!;
    private readonly CreateGitWorktreeCommand _create;
    private readonly RemoveGitWorktreeCommand _remove;

    public RemoveWorktreeResidueTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _worktrees = new GitWorktreeService(runner, _reader, NullLogger<GitWorktreeService>.Instance);
        _git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, _reader, new LibGit2SharpGitIgnoreService());
        _create = new CreateGitWorktreeCommand(_git, _worktrees);
        _remove = new RemoveGitWorktreeCommand(_worktrees);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best-effort */ }
    }

    // ---- Safety guards (pure, one test per guard) --------------------------------------------------

    [Fact]
    public void Guard_fails_when_worktree_path_is_outside_the_feature_storage_root()
    {
        var storageRoot = Path.Combine(_root, "guard1", "features");
        var featureRoot = Path.Combine(storageRoot, "FeatureA");
        var outsidePath = Path.Combine(_root, "guard1", "elsewhere", "repo");

        var failure = GitWorktreeService.ValidateResidueRemovalGuards(
            worktreePath: outsidePath,
            mainRepositoryPath: Path.Combine(_root, "guard1", "main"),
            featureRootPath: featureRoot,
            featureStorageRoot: storageRoot,
            isRegisteredWorktree: false);

        Assert.NotNull(failure);
        Assert.Contains("not under", failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Guard_fails_when_worktree_path_is_the_primary_checkout()
    {
        var storageRoot = Path.Combine(_root, "guard2", "features");
        var featureRoot = Path.Combine(storageRoot, "FeatureA");
        var worktreePath = Path.Combine(featureRoot, "repo");

        var failure = GitWorktreeService.ValidateResidueRemovalGuards(
            worktreePath: worktreePath,
            mainRepositoryPath: worktreePath,
            featureRootPath: featureRoot,
            featureStorageRoot: storageRoot,
            isRegisteredWorktree: false);

        Assert.NotNull(failure);
        Assert.Contains("primary", failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Guard_fails_when_path_is_still_a_registered_worktree()
    {
        var storageRoot = Path.Combine(_root, "guard3", "features");
        var featureRoot = Path.Combine(storageRoot, "FeatureA");
        var worktreePath = Path.Combine(featureRoot, "repo");

        var failure = GitWorktreeService.ValidateResidueRemovalGuards(
            worktreePath: worktreePath,
            mainRepositoryPath: Path.Combine(_root, "guard3", "main"),
            featureRootPath: featureRoot,
            featureStorageRoot: storageRoot,
            isRegisteredWorktree: true);

        Assert.NotNull(failure);
        Assert.Contains("registered", failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Guard_fails_when_worktree_folder_has_a_top_level_git_directory()
    {
        var storageRoot = Path.Combine(_root, "guard4", "features");
        var featureRoot = Path.Combine(storageRoot, "FeatureA");
        var worktreePath = Path.Combine(featureRoot, "repo");
        Directory.CreateDirectory(Path.Combine(worktreePath, ".git"));

        var failure = GitWorktreeService.ValidateResidueRemovalGuards(
            worktreePath: worktreePath,
            mainRepositoryPath: Path.Combine(_root, "guard4", "main"),
            featureRootPath: featureRoot,
            featureStorageRoot: storageRoot,
            isRegisteredWorktree: false);

        Assert.NotNull(failure);
        Assert.Contains(".git", failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Guard_fails_when_worktree_path_is_too_shallow_under_the_storage_root()
    {
        var storageRoot = Path.Combine(_root, "guard5", "features");
        var worktreePath = Path.Combine(storageRoot, "OnlyOneLevel");

        var failure = GitWorktreeService.ValidateResidueRemovalGuards(
            worktreePath: worktreePath,
            mainRepositoryPath: Path.Combine(_root, "guard5", "main"),
            featureRootPath: Path.Combine(storageRoot, "FeatureA"),
            featureStorageRoot: storageRoot,
            isRegisteredWorktree: false);

        Assert.NotNull(failure);
        Assert.Contains("shallow", failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Guard_passes_for_a_well_formed_feature_repo_path()
    {
        var storageRoot = Path.Combine(_root, "guard6", "features");
        var featureRoot = Path.Combine(storageRoot, "FeatureA");
        var worktreePath = Path.Combine(featureRoot, "repo");

        var failure = GitWorktreeService.ValidateResidueRemovalGuards(
            worktreePath: worktreePath,
            mainRepositoryPath: Path.Combine(_root, "guard6", "main"),
            featureRootPath: featureRoot,
            featureStorageRoot: storageRoot,
            isRegisteredWorktree: false);

        Assert.Null(failure);
    }

    // ---- Residue cleanup, real git -----------------------------------------------------------------

    [Fact]
    public async Task Leftover_files_are_removed_when_worktree_already_unregistered()
    {
        var mainPath = Path.Combine(_root, "main1");
        Directory.CreateDirectory(mainPath);
        await InitGitWithCommitAsync(mainPath);
        var head = await _reader.GetHeadCommitAsync(mainPath, CancellationToken.None);

        var storageRoot = Path.Combine(_root, "features");
        var featureRoot = Path.Combine(storageRoot, "orphan-feat");
        var worktreePath = Path.Combine(featureRoot, "main1");
        var created = await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            BranchName = "orphan-feat",
            BaseCommitSha = head,
        });
        Assert.True(created.Success, created.ErrorMessage);
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "build.log"), "output\n");

        // Simulate the worktree having become unregistered outside GrayMoon (for example a crash
        // between an earlier remove's git step and its own cleanup), leaving the folder behind.
        UnregisterWorktreeAdminFolder(mainPath);
        Assert.True(Directory.Exists(worktreePath));

        var result = await _remove.ExecuteAsync(new RemoveGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            FeatureRootPath = featureRoot,
            FeatureStorageRoot = storageRoot,
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.AlreadyRemoved);
        Assert.False(result.ResidueRemaining);
        Assert.Equal(0, result.ResidueFileCount);
        Assert.Null(result.ResidueMessage);
        Assert.False(Directory.Exists(worktreePath));
        // The Feature root had only this one repo folder, so it is now empty and removed too.
        Assert.False(Directory.Exists(featureRoot));
    }

    [Fact]
    public async Task Locked_file_produces_residue_remaining_with_sample()
    {
        var mainPath = Path.Combine(_root, "main2");
        Directory.CreateDirectory(mainPath);
        await InitGitWithCommitAsync(mainPath);
        var head = await _reader.GetHeadCommitAsync(mainPath, CancellationToken.None);

        var storageRoot = Path.Combine(_root, "features");
        var featureRoot = Path.Combine(storageRoot, "locked-feat");
        var worktreePath = Path.Combine(featureRoot, "main2");
        var created = await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            BranchName = "locked-feat",
            BaseCommitSha = head,
        });
        Assert.True(created.Success, created.ErrorMessage);

        // The file to be locked lives in its own subfolder so that, on the Linux branch below, only
        // that subfolder's write permission is removed; README.md and the .git file next to it in
        // worktreePath must still be deletable, matching the single-residue-file assertion below.
        var lockedDir = Path.Combine(worktreePath, "locked-dir");
        Directory.CreateDirectory(lockedDir);
        var lockedFilePath = Path.Combine(lockedDir, "locked.bin");
        await File.WriteAllTextAsync(lockedFilePath, "locked\n");

        RemoveGitWorktreeResponse result;
        if (OperatingSystem.IsWindows())
        {
            // Windows blocks deleting a file while another handle still has it open.
            using var openHandle = new FileStream(lockedFilePath, FileMode.Open, FileAccess.Read, FileShare.None);
            result = await _remove.ExecuteAsync(new RemoveGitWorktreeRequest
            {
                MainRepositoryPath = mainPath,
                WorktreePath = worktreePath,
                Force = true,
                FeatureRootPath = featureRoot,
                FeatureStorageRoot = storageRoot,
            });
        }
        else
        {
            // Linux lets a process unlink a file it still has open (the inode survives until the last
            // handle closes), so an open handle proves nothing there. Unix instead requires write
            // access on the containing directory to remove an entry from it, so removing the write
            // bit on lockedDir produces a genuine, equivalent "cannot delete this" failure.
            var originalMode = File.GetUnixFileMode(lockedDir);
            File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                result = await _remove.ExecuteAsync(new RemoveGitWorktreeRequest
                {
                    MainRepositoryPath = mainPath,
                    WorktreePath = worktreePath,
                    Force = true,
                    FeatureRootPath = featureRoot,
                    FeatureStorageRoot = storageRoot,
                });
            }
            finally
            {
                File.SetUnixFileMode(lockedDir, originalMode);
            }
        }

        // Git unregisters the worktree even when it cannot finish deleting the folder on disk; the
        // call is still reported as a successful remove, with the leftover file called out honestly.
        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.ResidueRemaining);
        Assert.Equal(1, result.ResidueFileCount);
        Assert.NotNull(result.ResidueSampleFiles);
        Assert.Contains("locked.bin", Assert.Single(result.ResidueSampleFiles!));
        Assert.NotNull(result.ResidueMessage);
    }

    [Fact]
    public async Task Feature_root_is_removed_only_when_it_becomes_empty()
    {
        var mainPath = Path.Combine(_root, "main3");
        Directory.CreateDirectory(mainPath);
        await InitGitWithCommitAsync(mainPath);
        var head = await _reader.GetHeadCommitAsync(mainPath, CancellationToken.None);

        var storageRoot = Path.Combine(_root, "features");
        var featureRoot = Path.Combine(storageRoot, "two-repo-feat");
        var worktreePathA = Path.Combine(featureRoot, "repoA");
        var worktreePathB = Path.Combine(featureRoot, "repoB");
        foreach (var (path, branch) in new[] { (worktreePathA, "two-repo-a"), (worktreePathB, "two-repo-b") })
        {
            var created = await _create.ExecuteAsync(new CreateGitWorktreeRequest
            {
                MainRepositoryPath = mainPath,
                WorktreePath = path,
                BranchName = branch,
                BaseCommitSha = head,
            });
            Assert.True(created.Success, created.ErrorMessage);
        }

        var firstRemove = await _remove.ExecuteAsync(new RemoveGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePathA,
            FeatureRootPath = featureRoot,
            FeatureStorageRoot = storageRoot,
        });
        Assert.True(firstRemove.Success, firstRemove.ErrorMessage);
        Assert.False(firstRemove.ResidueRemaining);
        Assert.True(Directory.Exists(featureRoot), "Feature root still has repoB's worktree and must stay.");

        var secondRemove = await _remove.ExecuteAsync(new RemoveGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePathB,
            FeatureRootPath = featureRoot,
            FeatureStorageRoot = storageRoot,
        });
        Assert.True(secondRemove.Success, secondRemove.ErrorMessage);
        Assert.False(secondRemove.ResidueRemaining);
        Assert.False(Directory.Exists(featureRoot), "Feature root is now empty and should be removed.");
    }

    [Fact]
    public async Task Create_into_an_existing_empty_folder_succeeds()
    {
        var mainPath = Path.Combine(_root, "main4");
        Directory.CreateDirectory(mainPath);
        await InitGitWithCommitAsync(mainPath);
        var head = await _reader.GetHeadCommitAsync(mainPath, CancellationToken.None);

        var worktreePath = Path.Combine(_root, "features", "empty-feat", "main4");
        Directory.CreateDirectory(worktreePath);

        var created = await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            BranchName = "empty-feat",
            BaseCommitSha = head,
        });

        Assert.True(created.Success, created.ErrorMessage);
        Assert.False(created.AlreadyExisted);
    }

    [Fact]
    public async Task Create_into_an_existing_non_empty_folder_still_fails()
    {
        var mainPath = Path.Combine(_root, "main5");
        Directory.CreateDirectory(mainPath);
        await InitGitWithCommitAsync(mainPath);
        var head = await _reader.GetHeadCommitAsync(mainPath, CancellationToken.None);

        var worktreePath = Path.Combine(_root, "features", "nonempty-feat", "main5");
        Directory.CreateDirectory(worktreePath);
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "already-here.txt"), "x\n");

        var created = await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            BranchName = "nonempty-feat",
            BaseCommitSha = head,
        });

        Assert.False(created.Success);
        Assert.Equal("PathExists", created.ErrorCode);
    }

    [Fact]
    public async Task Junction_inside_worktree_is_removed_without_entering_it_and_target_survives()
    {
        var mainPath = Path.Combine(_root, "main6");
        Directory.CreateDirectory(mainPath);
        await InitGitWithCommitAsync(mainPath);
        var head = await _reader.GetHeadCommitAsync(mainPath, CancellationToken.None);

        var storageRoot = Path.Combine(_root, "features");
        var featureRoot = Path.Combine(storageRoot, "junction-feat");
        var worktreePath = Path.Combine(featureRoot, "main6");
        var created = await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            BranchName = "junction-feat",
            BaseCommitSha = head,
        });
        Assert.True(created.Success, created.ErrorMessage);

        var targetDir = Path.Combine(_root, "outside-target");
        Directory.CreateDirectory(targetDir);
        var targetFile = Path.Combine(targetDir, "kept.txt");
        await File.WriteAllTextAsync(targetFile, "keep me\n");
        var junctionPath = Path.Combine(worktreePath, "link-out");
        await CreateJunctionAsync(junctionPath, targetDir);

        UnregisterWorktreeAdminFolder(mainPath);

        var result = await _remove.ExecuteAsync(new RemoveGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            FeatureRootPath = featureRoot,
            FeatureStorageRoot = storageRoot,
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(result.ResidueRemaining);
        Assert.False(Directory.Exists(worktreePath));
        Assert.True(File.Exists(targetFile), "The junction target must survive cleanup.");
        Assert.Equal("keep me\n".ReplaceLineEndings(), (await File.ReadAllTextAsync(targetFile)).ReplaceLineEndings());
    }

    // ---- D5: locked worktrees -------------------------------------------------------------------------

    [Fact]
    public async Task Locked_worktree_is_removed_when_unlock_is_true()
    {
        var mainPath = Path.Combine(_root, "main8");
        Directory.CreateDirectory(mainPath);
        await InitGitWithCommitAsync(mainPath);
        var head = await _reader.GetHeadCommitAsync(mainPath, CancellationToken.None);

        var worktreePath = Path.Combine(_root, "features", "locked-unlock-feat", "main8");
        var created = await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            BranchName = "locked-unlock-feat",
            BaseCommitSha = head,
        });
        Assert.True(created.Success, created.ErrorMessage);
        await RunGitAsync(mainPath, $"worktree lock --reason testing \"{worktreePath}\"");

        var result = await _remove.ExecuteAsync(new RemoveGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            Unlock = true,
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(Directory.Exists(worktreePath));
        var (listOk, worktrees, _, _) = await _worktrees.ListWorktreesAsync(mainPath, CancellationToken.None);
        Assert.True(listOk);
        Assert.DoesNotContain(worktrees, w => string.Equals(
            Path.GetFullPath(w.WorktreePath ?? ""), Path.GetFullPath(worktreePath), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Locked_worktree_remove_fails_without_unlock_and_stays_locked()
    {
        var mainPath = Path.Combine(_root, "main9");
        Directory.CreateDirectory(mainPath);
        await InitGitWithCommitAsync(mainPath);
        var head = await _reader.GetHeadCommitAsync(mainPath, CancellationToken.None);

        var worktreePath = Path.Combine(_root, "features", "locked-no-unlock-feat", "main9");
        var created = await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            BranchName = "locked-no-unlock-feat",
            BaseCommitSha = head,
        });
        Assert.True(created.Success, created.ErrorMessage);
        await RunGitAsync(mainPath, $"worktree lock --reason testing \"{worktreePath}\"");

        var result = await _remove.ExecuteAsync(new RemoveGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
        });

        Assert.False(result.Success);
        Assert.True(Directory.Exists(worktreePath));
        var (listOk, worktrees, _, _) = await _worktrees.ListWorktreesAsync(mainPath, CancellationToken.None);
        Assert.True(listOk);
        var still = worktrees.Single(w => string.Equals(
            Path.GetFullPath(w.WorktreePath ?? ""), Path.GetFullPath(worktreePath), StringComparison.OrdinalIgnoreCase));
        Assert.True(still.IsLocked);
    }

    // ---- Worker compatibility -----------------------------------------------------------------------

    [Fact]
    public void Old_app_request_without_feature_storage_fields_still_deserializes()
    {
        const string oldShapeJson = """{"mainRepositoryPath":"C:\\repo","worktreePath":"C:\\repo\\wt","force":true}""";

        var request = JsonSerializer.Deserialize<RemoveGitWorktreeRequest>(oldShapeJson, WorkerJsonOptions.SerializerOptions);

        Assert.NotNull(request);
        Assert.Equal("C:\\repo", request!.MainRepositoryPath);
        Assert.True(request.Force);
        Assert.Null(request.FeatureRootPath);
        Assert.Null(request.FeatureStorageRoot);
        Assert.False(request.Unlock);
    }

    [Fact]
    public async Task Old_app_request_without_feature_storage_fields_leaves_leftover_folder_in_place()
    {
        // Regression guard: an old App that does not send featureStorageRoot gets today's behaviour -
        // no residue deletion, only an honest report.
        var mainPath = Path.Combine(_root, "main7");
        Directory.CreateDirectory(mainPath);
        await InitGitWithCommitAsync(mainPath);
        var head = await _reader.GetHeadCommitAsync(mainPath, CancellationToken.None);

        var worktreePath = Path.Combine(_root, "features", "old-app-feat", "main7");
        var created = await _create.ExecuteAsync(new CreateGitWorktreeRequest
        {
            MainRepositoryPath = mainPath,
            WorktreePath = worktreePath,
            BranchName = "old-app-feat",
            BaseCommitSha = head,
        });
        Assert.True(created.Success, created.ErrorMessage);
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "build.log"), "output\n");
        UnregisterWorktreeAdminFolder(mainPath);

        // Equivalent to deserializing an old App's request JSON, which simply has no
        // featureRootPath/featureStorageRoot properties at all.
        var request = new RemoveGitWorktreeRequest { MainRepositoryPath = mainPath, WorktreePath = worktreePath };

        var result = await _remove.ExecuteAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.ResidueRemaining);
        Assert.NotNull(result.ResidueMessage);
        Assert.True(Directory.Exists(worktreePath), "Without the Feature storage fields, nothing should be deleted.");
    }

    [Fact]
    public void Old_worker_response_json_without_residue_fields_still_deserializes()
    {
        const string oldShapeJson = """{"success":true,"errorCode":null,"errorMessage":null,"alreadyRemoved":false}""";

        var response = JsonSerializer.Deserialize<RemoveGitWorktreeResponse>(oldShapeJson, WorkerJsonOptions.SerializerOptions);

        Assert.NotNull(response);
        Assert.True(response!.Success);
        Assert.False(response.AlreadyRemoved);
        Assert.False(response.ResidueRemaining);
        Assert.Equal(0, response.ResidueFileCount);
        Assert.Null(response.ResidueSampleFiles);
        Assert.Null(response.ResidueMessage);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static void UnregisterWorktreeAdminFolder(string mainRepositoryPath)
    {
        var adminRoot = Path.Combine(mainRepositoryPath, ".git", "worktrees");
        var entry = Directory.GetDirectories(adminRoot).Single();
        Directory.Delete(entry, true);
    }

    private static async Task CreateJunctionAsync(string linkPath, string targetPath)
    {
        if (OperatingSystem.IsWindows())
        {
            // NTFS junctions have no managed .NET API; mklink /J is the standard way to create one.
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("Failed to start cmd");
            await p.WaitForExitAsync();
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"mklink /J failed: {await p.StandardError.ReadToEndAsync()}");
            return;
        }

        // Linux has no junction concept, only symlinks. A directory symlink exercises the exact same
        // GitService code path as a Windows junction: both are reported with FileAttributes.ReparsePoint,
        // and the residue walk skips the link itself without entering it on either platform.
        Directory.CreateSymbolicLink(linkPath, targetPath);
        await Task.CompletedTask;
    }

    private static async Task InitGitWithCommitAsync(string repoPath)
    {
        await RunGitAsync(repoPath, "init");
        await RunGitAsync(repoPath, "config user.email test@example.com");
        await RunGitAsync(repoPath, "config user.name Test");
        // Ensure default branch is main for stable assertions across Git versions.
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
}
