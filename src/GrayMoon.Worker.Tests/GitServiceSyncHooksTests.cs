using System.Text;
using GrayMoon.Worker.Services;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

[Trait("Category", "PullRequest")]
public sealed class GitServiceSyncHooksTests : IDisposable
{
    private const string Marker = "# Created by GrayMoon.Agent";
    private const string BackupSuffix = ".replaced-by-graymoon";

    private static readonly string[] GrayMoonHooks = ["post-commit", "post-checkout", "post-merge", "pre-push"];

    private readonly TempGitRepositoryFixture _repo = new();
    private readonly CapturingLogger<GitService> _logger = new();
    private readonly GitService _git;
    private GitCliRepositoryReader _reader = null!;
    private readonly List<string> _cleanup = [];

    public GitServiceSyncHooksTests()
    {
        var inner = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(inner, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _git = new GitService(Options.Create(new WorkerOptions()), _logger, runner, _reader, new LibGit2SharpGitIgnoreService());
    }

    public void Dispose()
    {
        _repo.Dispose();
        foreach (var path in _cleanup)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    foreach (var file in new DirectoryInfo(path).GetFiles("*", SearchOption.AllDirectories))
                        file.Attributes = FileAttributes.Normal;
                    Directory.Delete(path, true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private string HooksDir => Path.Combine(_repo.RepositoryPath, ".git", "hooks");

    private string HookPath(string name) => Path.Combine(HooksDir, name);

    private Task SyncAsync(string? repoPath = null) =>
        _git.WriteSyncHooksAsync(repoPath ?? _repo.RepositoryPath, 1, 2, CancellationToken.None);

    private static void AssertIsGrayMoonHook(string path, string endpoint)
    {
        var text = File.ReadAllText(path);
        Assert.StartsWith("#!/bin/sh\n" + Marker + " at ", text);
        Assert.Contains("/hook/" + endpoint + "\"", text);
        Assert.DoesNotContain('\r', text);
    }

    private string NewOutsideDirectory()
    {
        var dir = Directory.CreateTempSubdirectory("graymoon-hooks-outside-").FullName;
        _cleanup.Add(dir);
        return dir;
    }

    private static string ForGitConfig(string path) => path.Replace('\\', '/');

    private static void MakeExecutable(string path) => SetMode(path, (UnixFileMode)0b111_101_101);

    private static UnixFileMode? GetMode(string path)
    {
        if (OperatingSystem.IsWindows())
            return null;
        return File.GetUnixFileMode(path);
    }

    private static void SetMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
            return;
        File.SetUnixFileMode(path, mode);
    }

    [Fact]
    public async Task Missing_hooks_are_written_and_post_update_is_not()
    {
        await SyncAsync();

        AssertIsGrayMoonHook(HookPath("post-commit"), "commit");
        AssertIsGrayMoonHook(HookPath("post-checkout"), "checkout");
        AssertIsGrayMoonHook(HookPath("post-merge"), "merge");
        AssertIsGrayMoonHook(HookPath("pre-push"), "push");
        Assert.False(File.Exists(HookPath("post-update")));
        Assert.Empty(Directory.GetFiles(HooksDir, "*" + BackupSuffix + "*"));
    }

    [Fact]
    public async Task Foreign_hook_is_renamed_with_identical_bytes_and_graymoon_hook_takes_its_place()
    {
        var foreign = Encoding.UTF8.GetBytes("#!/bin/sh\necho team secret scan\nexit 0\n");
        File.WriteAllBytes(HookPath("pre-push"), foreign);
        MakeExecutable(HookPath("pre-push"));
        var modeBefore = GetMode(HookPath("pre-push"));

        await SyncAsync();

        var backup = HookPath("pre-push" + BackupSuffix);
        Assert.True(File.Exists(backup));
        Assert.Equal(foreign, File.ReadAllBytes(backup));
        if (modeBefore is not null)
            Assert.Equal(modeBefore, GetMode(backup));
        AssertIsGrayMoonHook(HookPath("pre-push"), "push");
        AssertIsGrayMoonHook(HookPath("post-commit"), "commit");

        var warning = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(_repo.RepositoryPath, warning.Message);
        Assert.Contains("pre-push", warning.Message);
        Assert.Contains("pre-push" + BackupSuffix, warning.Message);
        Assert.Contains("no longer runs", warning.Message);
    }

    [Fact]
    public async Task Second_foreign_hook_uses_timestamped_name_and_nothing_is_overwritten()
    {
        var first = Encoding.UTF8.GetBytes("#!/bin/sh\necho first\n");
        var second = Encoding.UTF8.GetBytes("#!/bin/sh\necho second\n");
        File.WriteAllBytes(HookPath("pre-push"), first);
        await SyncAsync();

        File.WriteAllBytes(HookPath("pre-push"), second);
        await SyncAsync();

        Assert.Equal(first, File.ReadAllBytes(HookPath("pre-push" + BackupSuffix)));
        var stamped = Directory.GetFiles(HooksDir, "pre-push" + BackupSuffix + "-*");
        var stampedFile = Assert.Single(stamped);
        Assert.Matches(@"^pre-push\.replaced-by-graymoon-\d{14}$", Path.GetFileName(stampedFile));
        Assert.Equal(second, File.ReadAllBytes(stampedFile));
        AssertIsGrayMoonHook(HookPath("pre-push"), "push");
    }

    [Fact]
    public async Task Release_0_1_0_hooks_are_recognised_and_updated_in_place()
    {
        const string oldPrePush =
            "#!/bin/sh\n# Created by GrayMoon.Agent at 2025-01-01 10:00:00Z\n" +
            "curl -s --connect-timeout 1 --max-time 2 -X POST \"http://127.0.0.1:9191/hook/push\"     -H \"Content-Type: application/json\" -d '{\"repositoryId\":2,\"workspaceId\":1,\"repositoryPath\":\"C:\\\\old\\\\repo\"}' || true\n";
        const string oldPostCheckout =
            "#!/bin/sh\n# Created by GrayMoon.Agent at 2025-01-01 10:00:00Z\n" +
            "[ \"$3\" = \"1\" ] && curl -s --connect-timeout 1 --max-time 2 -X POST \"http://127.0.0.1:9191/hook/checkout\" -H \"Content-Type: application/json\" -d '{\"repositoryId\":2,\"workspaceId\":1,\"repositoryPath\":\"C:\\\\old\\\\repo\"}' || true\n";
        File.WriteAllText(HookPath("pre-push"), oldPrePush, new UTF8Encoding(false));
        File.WriteAllText(HookPath("post-checkout"), oldPostCheckout, new UTF8Encoding(false));

        await SyncAsync();

        var prePush = File.ReadAllText(HookPath("pre-push"));
        Assert.NotEqual(oldPrePush, prePush);
        Assert.Contains("git rev-parse --show-toplevel", prePush);
        AssertIsGrayMoonHook(HookPath("pre-push"), "push");
        AssertIsGrayMoonHook(HookPath("post-checkout"), "checkout");
        Assert.Empty(Directory.GetFiles(HooksDir, "*" + BackupSuffix + "*"));
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Current_hook_with_unchanged_content_is_not_rewritten()
    {
        await SyncAsync();
        var before = new Dictionary<string, (string Text, DateTime Written)>();
        var old = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        foreach (var name in GrayMoonHooks)
        {
            File.SetLastWriteTimeUtc(HookPath(name), old);
            before[name] = (File.ReadAllText(HookPath(name)), File.GetLastWriteTimeUtc(HookPath(name)));
        }

        await Task.Delay(1100);
        await SyncAsync();

        foreach (var name in GrayMoonHooks)
        {
            Assert.Equal(before[name].Written, File.GetLastWriteTimeUtc(HookPath(name)));
            Assert.Equal(before[name].Text, File.ReadAllText(HookPath(name)));
        }
    }

    [Fact]
    public async Task Graymoon_hook_with_different_port_is_rewritten()
    {
        await SyncAsync();
        var text = File.ReadAllText(HookPath("post-merge"));
        File.WriteAllText(HookPath("post-merge"), text.Replace(":9191/", ":9999/"), new UTF8Encoding(false));

        await SyncAsync();

        Assert.Contains(":9191/hook/merge", File.ReadAllText(HookPath("post-merge")));
    }

    [Fact]
    public async Task Custom_hooks_path_outside_git_dir_writes_and_renames_nothing()
    {
        var outside = NewOutsideDirectory();
        var foreign = Encoding.UTF8.GetBytes("#!/bin/sh\necho husky\n");
        File.WriteAllBytes(Path.Combine(outside, "pre-push"), foreign);
        _repo.RunGit("config", "core.hooksPath", ForGitConfig(outside));

        await SyncAsync();

        Assert.Equal(foreign, File.ReadAllBytes(Path.Combine(outside, "pre-push")));
        Assert.Equal("pre-push", Path.GetFileName(Assert.Single(Directory.GetFiles(outside))));
        Assert.All(Directory.GetFiles(HooksDir), f => Assert.EndsWith(".sample", f));

        var warning = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(ForGitConfig(outside), warning.Message.Replace('\\', '/'));
        Assert.Contains("next Sync", warning.Message);
    }

    [Fact]
    public async Task Custom_hooks_path_inside_git_dir_is_treated_like_the_normal_folder()
    {
        var inside = Path.Combine(_repo.RepositoryPath, ".git", "my-hooks");
        Directory.CreateDirectory(inside);
        _repo.RunGit("config", "core.hooksPath", ForGitConfig(inside));

        await SyncAsync();

        AssertIsGrayMoonHook(Path.Combine(inside, "pre-push"), "push");
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Linked_worktree_writes_hooks_into_the_primary_checkouts_common_hooks_folder()
    {
        _repo.CommitInitial();
        var worktree = Path.Combine(NewOutsideDirectory(), "wt");
        var add = _repo.RunGit("worktree", "add", "-b", "feature/hooks", worktree);
        Assert.Equal(0, add.ExitCode);

        await SyncAsync(worktree);

        AssertIsGrayMoonHook(HookPath("pre-push"), "push");
        AssertIsGrayMoonHook(HookPath("post-checkout"), "checkout");
        Assert.False(Directory.Exists(Path.Combine(worktree, ".git")));
    }

    [Fact]
    public async Task Existing_graymoon_post_update_is_kept_and_a_missing_one_is_not_created()
    {
        var content = "#!/bin/sh\n" + Marker + " at 2025-01-01 10:00:00Z\ncurl old\n";
        File.WriteAllText(HookPath("post-update"), content, new UTF8Encoding(false));
        var old = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(HookPath("post-update"), old);

        await SyncAsync();

        Assert.Equal(content, File.ReadAllText(HookPath("post-update")));
        Assert.Equal(old, File.GetLastWriteTimeUtc(HookPath("post-update")));
        Assert.Empty(Directory.GetFiles(HooksDir, "post-update*" + BackupSuffix + "*"));
    }

    [Fact]
    public async Task Foreign_post_update_is_untouched()
    {
        var foreign = Encoding.UTF8.GetBytes("#!/bin/sh\necho mine\n");
        File.WriteAllBytes(HookPath("post-update"), foreign);

        await SyncAsync();

        Assert.Equal(foreign, File.ReadAllBytes(HookPath("post-update")));
        Assert.Empty(Directory.GetFiles(HooksDir, "post-update" + BackupSuffix + "*"));
    }

    [Fact]
    public async Task Rename_failure_leaves_the_original_in_place_and_does_not_throw()
    {
        var foreign = Encoding.UTF8.GetBytes("#!/bin/sh\necho keep me\n");
        File.WriteAllBytes(HookPath("pre-push"), foreign);

        FileStream? lockHandle = null;
        UnixFileMode? dirMode = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                lockHandle = new FileStream(HookPath("pre-push"), FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            else
            {
                dirMode = GetMode(HooksDir);
                SetMode(HooksDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                var probe = Path.Combine(HooksDir, "probe");
                try
                {
                    File.WriteAllText(probe, "x");
                    File.Delete(probe);
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            await SyncAsync();
        }
        finally
        {
            lockHandle?.Dispose();
            if (dirMode is not null)
                SetMode(HooksDir, dirMode.Value);
        }

        Assert.Equal(foreign, File.ReadAllBytes(HookPath("pre-push")));
        Assert.False(File.Exists(HookPath("pre-push" + BackupSuffix)));
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("pre-push"));
    }

    [Fact]
    public async Task Rename_failure_for_one_hook_does_not_stop_the_other_hooks()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var foreign = Encoding.UTF8.GetBytes("#!/bin/sh\necho keep me\n");
        File.WriteAllBytes(HookPath("pre-push"), foreign);

        using (new FileStream(HookPath("pre-push"), FileMode.Open, FileAccess.Read, FileShare.Read))
            await SyncAsync();

        Assert.Equal(foreign, File.ReadAllBytes(HookPath("pre-push")));
        AssertIsGrayMoonHook(HookPath("post-commit"), "commit");
        AssertIsGrayMoonHook(HookPath("post-checkout"), "checkout");
        AssertIsGrayMoonHook(HookPath("post-merge"), "merge");
    }
}
