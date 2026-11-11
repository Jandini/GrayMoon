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
/// Characterization of <c>InspectWorktree</c> when the inspected path is the MAIN checkout of a repository
/// (mainRepositoryPath == worktreePath), not a linked worktree. Asserts what the code does today
/// (read-only repositories plan, finding F13); it changes no behavior.
/// </summary>
public sealed class InspectWorktreeMainCheckoutTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-iwm-").FullName;
    private readonly InspectWorktreeCommand _inspect;

    public InspectWorktreeMainCheckoutTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        var worktrees = new GitWorktreeService(runner, reader, NullLogger<GitWorktreeService>.Instance, new RepositoryAccess(NullLogger<RepositoryAccess>.Instance));
        _inspect = new InspectWorktreeCommand(worktrees);
    }

    public void Dispose()
    {
        TestDirectory.Delete(_root);
    }

    [Fact]
    public async Task Clean_main_checkout_with_upstream_reports_registered_clean_and_zero_ahead()
    {
        var repo = PushedClone("clean");
        var head = Git(repo, "rev-parse HEAD").Trim();

        var result = await InspectMainAsync(repo);

        Assert.Null(result.Error);
        Assert.True(result.IsRegistered);
        Assert.True(result.Exists);
        Assert.False(result.IsLocked);
        Assert.Null(result.LockReason);
        Assert.Equal("main", result.Branch);
        Assert.Equal(head, result.HeadSha, StringComparer.OrdinalIgnoreCase);
        Assert.False(result.IsDirty);
        Assert.Equal(0, result.StagedCount);
        Assert.Equal(0, result.UnstagedCount);
        Assert.Equal(0, result.UntrackedCount);
        Assert.Equal(0, result.ConflictCount);
        Assert.True(result.HasUpstream);
        Assert.Equal(0, result.AheadOfUpstream);
        Assert.Equal(0, result.BehindUpstream);
        Assert.Equal(0, result.AheadOfDefault);
        Assert.Null(result.FeatureBranchExists);
    }

    [Fact]
    public async Task Dirty_main_checkout_reports_dirty_with_counts_and_keeps_upstream_facts()
    {
        var repo = PushedClone("dirty");
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "changed\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "staged.txt"), "s\n");
        Git(repo, "add staged.txt");
        await File.WriteAllTextAsync(Path.Combine(repo, "untracked.txt"), "u\n");

        var result = await InspectMainAsync(repo);

        Assert.Null(result.Error);
        Assert.True(result.IsRegistered);
        Assert.True(result.IsDirty);
        Assert.Equal(1, result.StagedCount);
        Assert.Equal(1, result.UnstagedCount);
        Assert.Equal(1, result.UntrackedCount);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal("main", result.Branch);
        Assert.True(result.HasUpstream);
        Assert.Equal(0, result.AheadOfUpstream);
        Assert.Equal(0, result.BehindUpstream);
    }

    [Fact]
    public async Task One_unpushed_commit_on_main_checkout_reports_ahead_of_upstream_one()
    {
        var repo = PushedClone("ahead");
        await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "1\n");
        Git(repo, "add a.txt");
        Git(repo, "commit -q -m local-only");

        var result = await InspectMainAsync(repo);

        Assert.Null(result.Error);
        Assert.False(result.IsDirty);
        Assert.Equal("main", result.Branch);
        Assert.True(result.HasUpstream);
        Assert.Equal(1, result.AheadOfUpstream);
        Assert.Equal(0, result.BehindUpstream);
        Assert.Equal(1, result.AheadOfDefault);
    }

    [Fact]
    public async Task Local_branch_without_upstream_reports_HasUpstream_false_and_null_counts()
    {
        var repo = PushedClone("noupstream");
        Git(repo, "checkout -q -b local-only");
        await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "1\n");
        Git(repo, "add a.txt");
        Git(repo, "commit -q -m local-only");

        var result = await InspectMainAsync(repo);

        Assert.Null(result.Error);
        Assert.True(result.IsRegistered);
        Assert.False(result.IsDirty);
        Assert.Equal("local-only", result.Branch);
        // No upstream: false (not null), and the upstream counts are null - the unpushed commit is only
        // visible through aheadOfDefault.
        Assert.False(result.HasUpstream);
        Assert.Null(result.AheadOfUpstream);
        Assert.Null(result.BehindUpstream);
        Assert.Equal(1, result.AheadOfDefault);
    }

    [Fact]
    public async Task Detached_head_reports_null_branch_and_unknown_upstream()
    {
        var repo = PushedClone("detached");
        var head = Git(repo, "rev-parse HEAD").Trim();
        Git(repo, "checkout -q --detach");

        var result = await InspectMainAsync(repo);

        Assert.Null(result.Error);
        Assert.True(result.IsRegistered);
        Assert.True(result.Exists);
        Assert.Null(result.Branch);
        Assert.Equal(head, result.HeadSha, StringComparer.OrdinalIgnoreCase);
        Assert.False(result.IsDirty);
        // Detached: the upstream probe is skipped, so all three are null ("unknown"), not false / 0.
        Assert.Null(result.HasUpstream);
        Assert.Null(result.AheadOfUpstream);
        Assert.Null(result.BehindUpstream);
        Assert.Equal(0, result.AheadOfDefault);
    }

    [Fact]
    public async Task Tag_checkout_reports_the_same_as_detached_head_and_no_tag_field()
    {
        var repo = PushedClone("tag");
        var head = Git(repo, "rev-parse HEAD").Trim();
        Git(repo, "tag v1.0.0");
        Git(repo, "checkout -q v1.0.0");

        var result = await InspectMainAsync(repo);

        Assert.Null(result.Error);
        Assert.True(result.IsRegistered);
        Assert.Null(result.Branch);
        Assert.Equal(head, result.HeadSha, StringComparer.OrdinalIgnoreCase);
        Assert.False(result.IsDirty);
        Assert.Null(result.HasUpstream);
        Assert.Null(result.AheadOfUpstream);
        Assert.Null(result.BehindUpstream);
        Assert.Equal(0, result.AheadOfDefault);
    }

    [Fact]
    public async Task Detached_head_with_a_local_commit_is_visible_only_through_AheadOfDefault()
    {
        var repo = PushedClone("detached-ahead");
        Git(repo, "checkout -q --detach");
        await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "1\n");
        Git(repo, "add a.txt");
        Git(repo, "commit -q -m detached-commit");

        var result = await InspectMainAsync(repo);

        Assert.Null(result.Error);
        Assert.Null(result.Branch);
        Assert.Null(result.HasUpstream);
        Assert.Null(result.AheadOfUpstream);
        Assert.Equal(1, result.AheadOfDefault);
    }

    [Fact]
    public async Task Without_default_branch_in_request_AheadOfDefault_is_null()
    {
        var repo = PushedClone("nodefault");

        var result = await _inspect.ExecuteAsync(new InspectWorktreeRequest
        {
            MainRepositoryPath = repo,
            WorktreePath = repo,
        });

        Assert.Null(result.Error);
        Assert.True(result.HasUpstream);
        Assert.Equal(0, result.AheadOfUpstream);
        Assert.Null(result.AheadOfDefault);
    }

    private Task<InspectWorktreeResponse> InspectMainAsync(string repo) =>
        _inspect.ExecuteAsync(new InspectWorktreeRequest
        {
            MainRepositoryPath = repo,
            WorktreePath = repo,
            DefaultBranch = "main",
        });

    /// <summary>A clone on <c>main</c> with one commit pushed to its bare origin and the upstream configured.</summary>
    private string PushedClone(string name) =>
        GitScenarios.ClonedRepository(Path.Combine(_root, name), push: true);

    private static string Git(string repo, string args) => GitConfigTestSupport.Git(repo, args);
}
