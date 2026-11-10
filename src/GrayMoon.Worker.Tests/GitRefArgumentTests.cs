using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// A ref, branch or revision name reaches git as one argument, after <c>--end-of-options</c> (or <c>--</c>): a name
/// that starts with <c>-</c> is a name, and text after a space in a name is not a further argument. Real git.
/// </summary>
public sealed class GitRefArgumentTests : IDisposable
{
    private readonly string _root = TestDirectory.Create("ref-args");
    private readonly GitCliRepositoryReader _reader;
    private readonly GitWorktreeService _worktrees;
    private readonly RepositoryAccess _access = new(NullLogger<RepositoryAccess>.Instance);

    public GitRefArgumentTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        _worktrees = new GitWorktreeService(runner, _reader, NullLogger<GitWorktreeService>.Instance, _access);
    }

    public void Dispose() => TestDirectory.Delete(_root);

    [Fact]
    public async Task RefExists_finds_a_branch_whose_name_starts_with_a_dash()
    {
        var repo = NewRepository();
        GitConfigTestSupport.Git(repo, "update-ref refs/heads/-foo HEAD");

        Assert.True(await _reader.RefExistsAsync(repo, "-foo", CancellationToken.None));
        Assert.False(await _reader.RefExistsAsync(repo, "-bar", CancellationToken.None));
    }

    [Fact]
    public async Task RefExists_does_not_read_text_after_a_space_as_an_option()
    {
        var repo = NewRepository();

        // As one command line this was "rev-parse --verify --quiet main --quiet", which succeeds.
        Assert.False(await _reader.RefExistsAsync(repo, "main --quiet", CancellationToken.None));
        Assert.True(await _reader.RefExistsAsync(repo, "main", CancellationToken.None));
    }

    [Fact]
    public async Task GetUpstreamRef_does_not_read_text_after_a_space_as_an_option()
    {
        var repo = NewRepository();

        // As one command line the second --format won, and the answer was "refs/heads/main".
        var upstream = await _reader.GetUpstreamRefAsync(repo, "main --format=%(refname)", CancellationToken.None);

        Assert.Null(upstream);
    }

    [Fact]
    public async Task InspectWorktree_does_not_report_a_feature_branch_that_only_exists_as_injected_arguments()
    {
        var repo = NewRepository();
        var inspect = new InspectWorktreeCommand(_worktrees);

        var injected = await inspect.ExecuteAsync(new InspectWorktreeRequest
        {
            MainRepositoryPath = repo,
            WorktreePath = repo,
            DefaultBranch = "main",
            FeatureBranch = "main --quiet",
        });
        var real = await inspect.ExecuteAsync(new InspectWorktreeRequest
        {
            MainRepositoryPath = repo,
            WorktreePath = repo,
            DefaultBranch = "main",
            FeatureBranch = "main",
        });

        Assert.Null(injected.Error);
        Assert.False(injected.FeatureBranchExists);
        Assert.Null(injected.FeatureBranchSha);
        Assert.True(real.FeatureBranchExists);
        Assert.False(string.IsNullOrWhiteSpace(real.FeatureBranchSha));
    }

    /// <summary>
    /// A leftover-folder sweep (or a removal) claims the Feature root, re-checks it, then deletes it. A worktree added
    /// under that root in between would be deleted with it, so the add is refused while the claim is held.
    /// </summary>
    [Fact]
    public async Task CreateWorktree_is_refused_while_its_feature_root_is_being_removed()
    {
        var repo = NewRepository();
        var head = await _reader.GetHeadCommitAsync(repo, CancellationToken.None);
        var featureRoot = Path.Combine(_root, "features", "login");
        var worktreePath = Path.Combine(featureRoot, "repo");
        Directory.CreateDirectory(featureRoot);

        (bool Success, GitWorktreeInfo? Worktree, bool AlreadyExisted, string? ErrorCode, string? ErrorMessage) during;
        using (await _access.AcquireExclusiveAsync([featureRoot], CancellationToken.None))
        {
            during = await _worktrees.CreateWorktreeAsync(repo, worktreePath, "login", head!, CancellationToken.None);
            Assert.False(Directory.Exists(worktreePath), "nothing may appear in a folder that is being deleted");
        }

        var after = await _worktrees.CreateWorktreeAsync(repo, worktreePath, "login", head!, CancellationToken.None);

        Assert.False(during.Success);
        Assert.Equal("PathUnderRemoval", during.ErrorCode);
        Assert.True(after.Success, after.ErrorMessage);
    }

    /// <summary>The other order: a removal that starts during an add waits for it, so its re-check sees the new worktree.</summary>
    [Fact]
    public async Task A_removal_claim_waits_for_a_worktree_add_in_progress()
    {
        var featureRoot = Path.Combine(_root, "features", "login");
        var worktreePath = Path.Combine(featureRoot, "repo");
        var addLease = _access.TryAcquireShared(worktreePath, RepositoryAccessKind.Mutating);
        Assert.NotNull(addLease);

        var claim = _access.AcquireExclusiveAsync([featureRoot], CancellationToken.None);
        await Task.Delay(200);
        Assert.False(claim.IsCompleted, "the claim must wait for the add");

        addLease!.Dispose();
        using var scope = await claim.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private string NewRepository()
    {
        var repo = Path.Combine(_root, "repo");
        GitScenarios.CommittedRepository(repo);
        return repo;
    }
}
