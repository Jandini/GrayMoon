using System.Text;
using GrayMoon.Common;
using GrayMoon.Common.Ai;
using GrayMoon.Worker.Services.GitEvidence;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.Worker.Tests;

public sealed class GitEvidenceCollectorTests : IDisposable
{
    private readonly TempGitRepositoryFixture _repo = new();
    private readonly CountingRunner _runner = new(new BoundedProcessRunner(NullLogger<BoundedProcessRunner>.Instance));
    private readonly GitEvidenceCollector _collector;

    public GitEvidenceCollectorTests() => _collector = new GitEvidenceCollector(_runner);

    public void Dispose() => _repo.Dispose();

    private static GitEvidenceRepositoryScope Scope(string? head = null, string? target = null) =>
        new() { RepositoryId = 7, RepositoryName = "repo", HeadBranch = head, BaseBranch = target };

    private Task<RepositoryGitEvidence> CommitEvidence(GitCommitEvidenceMode mode, GitEvidenceLimits? limits = null) =>
        _collector.CollectCommitAsync(_repo.RepositoryPath, Scope(), mode, limits ?? new GitEvidenceLimits(), CancellationToken.None);

    private Task<RepositoryGitEvidence> PullRequestEvidence(string head, string target, GitEvidenceLimits? limits = null) =>
        _collector.CollectPullRequestAsync(_repo.RepositoryPath, Scope(head, target), limits ?? new GitEvidenceLimits(), CancellationToken.None);

    private string Head() => _repo.RunGit("rev-parse", "HEAD").Stdout.Trim();

    private void Commit(string path, string content, string message)
    {
        _repo.WriteFile(path, content);
        _repo.RunGit("add", "--all");
        _repo.RunGit("commit", "-m", message);
    }

    /// <summary>Publishes the current HEAD as origin/<paramref name="branch"/> without a network: a remote-tracking ref.</summary>
    private void Publish(string branch) => _repo.RunGit("update-ref", $"refs/remotes/origin/{branch}", Head());

    [Fact]
    public async Task Staged_mode_reads_only_the_index_even_in_a_partly_staged_file()
    {
        _repo.CommitInitial("src/Service.cs", "class Service\n{\n}\n");
        _repo.WriteFile("src/Service.cs", "class Service\n{\n    void Staged() { }\n}\n");
        _repo.RunGit("add", "src/Service.cs");
        _repo.WriteFile("src/Service.cs", "class Service\n{\n    void Staged() { }\n    void NotStaged() { }\n}\n");
        _repo.WriteFile("src/Other.cs", "unstaged only\n");

        var evidence = await CommitEvidence(GitCommitEvidenceMode.Staged);

        Assert.True(evidence.Success);
        var file = Assert.Single(evidence.Files);
        Assert.Equal("src/Service.cs", file.Path);
        Assert.Contains("+    void Staged() { }", file.Patch);
        Assert.DoesNotContain("NotStaged", file.Patch);
        Assert.Equal(GitEvidenceFileCategory.Source, file.Category);
        Assert.Equal("Initial commit", Assert.Single(evidence.RecentSubjects));
        Assert.Equal(Head(), evidence.HeadOid);
    }

    [Fact]
    public async Task All_mode_describes_the_final_working_tree_once_and_includes_untracked_files()
    {
        _repo.CommitInitial("src/Service.cs", "one\n");
        _repo.WriteFile("src/Service.cs", "two\n");
        _repo.RunGit("add", "src/Service.cs");
        _repo.WriteFile("src/Service.cs", "three\n");
        _repo.WriteFile("src/New.cs", "class New { }\n");
        _repo.WriteFile(".gitignore", "ignored/\n");
        _repo.WriteFile("ignored/secret.txt", "never listed\n");

        var evidence = await CommitEvidence(GitCommitEvidenceMode.All);

        var service = Assert.Single(evidence.Files, f => f.Path == "src/Service.cs");
        // HEAD to working tree: "one" -> "three", never the intermediate staged "two".
        Assert.Contains("-one", service.Patch);
        Assert.Contains("+three", service.Patch);
        Assert.DoesNotContain("two", service.Patch);
        Assert.Equal(1, service.Added);
        Assert.Equal(1, service.Deleted);

        var added = Assert.Single(evidence.Files, f => f.Path == "src/New.cs");
        Assert.True(added.IsUntracked);
        Assert.Equal(GitEvidenceChange.Added, added.Change);
        Assert.Contains("+class New { }", added.Patch);

        Assert.DoesNotContain(evidence.Files, f => f.Path.StartsWith("ignored/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unborn_branch_compares_with_the_empty_tree()
    {
        _repo.WriteFile("README.md", "hello\n");
        _repo.RunGit("add", "README.md");

        var evidence = await CommitEvidence(GitCommitEvidenceMode.Staged);

        Assert.True(evidence.Success);
        Assert.True(evidence.IsUnbornHead);
        var file = Assert.Single(evidence.Files);
        Assert.Equal(GitEvidenceChange.Added, file.Change);
        Assert.Contains("+hello", file.Patch);
    }

    [Fact]
    public async Task Rename_delete_binary_and_paths_with_spaces_are_reported_without_guessing()
    {
        _repo.WriteFile("docs/old name.md", string.Join('\n', Enumerable.Range(0, 20).Select(i => $"line {i}")) + "\n");
        _repo.WriteFile("gone.txt", "bye\n");
        File.WriteAllBytes(Path.Combine(_repo.RepositoryPath, "image.png"), [0x89, 0x50, 0x00, 0x01]);
        _repo.RunGit("add", "--all");
        _repo.RunGit("commit", "-m", "Initial");

        _repo.RunGit("mv", "docs/old name.md", "docs/new name.md");
        _repo.WriteFile("docs/new name.md", string.Join('\n', Enumerable.Range(0, 20).Select(i => i == 3 ? "changed" : $"line {i}")) + "\n");
        _repo.RunGit("rm", "-q", "gone.txt");
        File.WriteAllBytes(Path.Combine(_repo.RepositoryPath, "image.png"), [0x89, 0x50, 0x00, 0x02]);
        _repo.RunGit("add", "--all");

        var evidence = await CommitEvidence(GitCommitEvidenceMode.Staged);

        var renamed = Assert.Single(evidence.Files, f => f.Path == "docs/new name.md");
        Assert.Equal(GitEvidenceChange.Renamed, renamed.Change);
        Assert.Equal("docs/old name.md", renamed.OldPath);
        Assert.Contains("+changed", renamed.Patch);

        Assert.Equal(GitEvidenceChange.Deleted, Assert.Single(evidence.Files, f => f.Path == "gone.txt").Change);

        var binary = Assert.Single(evidence.Files, f => f.Path == "image.png");
        Assert.True(binary.IsBinary);
        Assert.Null(binary.Patch);
    }

    [Fact]
    public async Task Sensitive_files_are_listed_but_never_read()
    {
        _repo.CommitInitial();
        _repo.WriteFile("deploy/server.pem", "-----BEGIN PRIVATE KEY-----\nabc\n");
        _repo.WriteFile(".env", "API_KEY=supersecretvalue\n");

        var evidence = await CommitEvidence(GitCommitEvidenceMode.All);

        Assert.All(evidence.Files.Where(f => f.Path is "deploy/server.pem" or ".env"), f =>
        {
            Assert.True(f.IsSensitive);
            Assert.Null(f.Patch);
        });
        Assert.Equal(2, evidence.Files.Count(f => f.IsSensitive));
    }

    [Fact]
    public async Task Huge_change_is_bounded_at_collection_time()
    {
        _repo.CommitInitial("src/Big.cs", "start\n");
        var big = new StringBuilder();
        for (var i = 0; i < 100_000; i++)
            big.Append("    var value").Append(i).Append(" = ").Append(i).Append(";\n");
        _repo.WriteFile("src/Big.cs", big.ToString());
        _repo.WriteFile("src/Small.cs", "class Small { }\n");
        _repo.RunGit("add", "--all");

        var limits = new GitEvidenceLimits { MaxPatchBytesPerFile = 1_000, MaxPatchBytesPerRepository = 4_000 };
        var evidence = await CommitEvidence(GitCommitEvidenceMode.Staged, limits);

        var file = Assert.Single(evidence.Files, f => f.Path == "src/Big.cs");
        Assert.Equal(100_000, file.Added);
        Assert.NotNull(file.Patch);
        Assert.True(Encoding.UTF8.GetByteCount(file.Patch!) <= 1_000);
        Assert.True(file.PatchTruncated);
        // The big file's diff process was stopped near the cap, never read to the end (about 3 MB).
        Assert.True(_runner.MaxStdoutBytesSeen < 64_000, $"read {_runner.MaxStdoutBytesSeen} bytes");
        Assert.Contains("class Small", Assert.Single(evidence.Files, f => f.Path == "src/Small.cs").Patch);
    }

    [Fact]
    public async Task Pull_request_compares_with_the_selected_parent_branch_not_the_default()
    {
        // main -- M1 -- M2
        //   \
        //    release/2.0 -- R1 -- R2
        //                          \
        //                           feature/BAM-2874 -- F1 -- F2
        _repo.CommitInitial("base.txt", "base\n");
        _repo.RunGit("checkout", "-q", "-b", "release/2.0");
        Commit("release.txt", "R1\n", "R1 release work");
        Commit("release.txt", "R1\nR2\n", "R2 release work");
        Publish("release/2.0");
        _repo.RunGit("checkout", "-q", "-b", "feature/BAM-2874");
        Commit("src/Feature.cs", "F1\n", "F1 feature start");
        Commit("src/Feature.cs", "F1\nF2\n", "F2 feature finish");
        var featureHead = Head();
        _repo.RunGit("checkout", "-q", "main");
        Commit("main.txt", "M1\n", "M1 main work");
        Commit("main.txt", "M1\nM2\n", "M2 main work");
        Publish("main");
        _repo.RunGit("checkout", "-q", "feature/BAM-2874");

        var evidence = await PullRequestEvidence("feature/BAM-2874", "release/2.0");

        Assert.True(evidence.Success, evidence.ErrorMessage);
        Assert.Equal("origin/release/2.0", evidence.BaseRef);
        Assert.Equal(featureHead, evidence.HeadOid);
        Assert.Equal(2, evidence.CommitCount);
        Assert.Equal(["F1 feature start", "F2 feature finish"], evidence.Commits.Select(c => c.Subject));
        Assert.Equal(featureHead, evidence.Commits[^1].Oid);
        Assert.All(evidence.Commits, c => Assert.Equal((1, 1), (c.FilesChanged, c.Added)));
        Assert.Equal(0, evidence.CommitsOmitted);
        var file = Assert.Single(evidence.Files);
        Assert.Equal("src/Feature.cs", file.Path);
        Assert.DoesNotContain(evidence.Files, f => f.Path is "release.txt" or "main.txt");

        // Against main the same branch would also carry R1/R2; the selected base decides, nothing else.
        var againstMain = await PullRequestEvidence("feature/BAM-2874", "main");
        Assert.Equal(4, againstMain.CommitCount);
        Assert.Contains(againstMain.Files, f => f.Path == "release.txt");
    }

    [Fact]
    public async Task Pull_request_history_of_a_long_branch_keeps_the_oldest_and_newest_halves()
    {
        _repo.CommitInitial("base.txt", "base\n");
        Publish("main");
        _repo.RunGit("checkout", "-q", "-b", "feature/long");
        var content = new StringBuilder();
        for (var i = 1; i <= 7; i++)
        {
            content.Append(i).Append('\n');
            Commit("src/Long.cs", content.ToString(), $"Step {i}");
        }

        var evidence = await PullRequestEvidence("feature/long", "main", new GitEvidenceLimits { MaxHistoryCommits = 4 });

        Assert.True(evidence.Success, evidence.ErrorMessage);
        Assert.Equal(7, evidence.CommitCount);
        Assert.Equal(["Step 1", "Step 2", "Step 6", "Step 7"], evidence.Commits.Select(c => c.Subject));
        Assert.Equal(3, evidence.CommitsOmitted);
    }

    [Fact]
    public async Task Pull_request_excludes_target_only_commits_after_the_parent_advanced()
    {
        _repo.CommitInitial("base.txt", "base\n");
        _repo.RunGit("checkout", "-q", "-b", "develop");
        Publish("develop");
        _repo.RunGit("checkout", "-q", "-b", "feature/x");
        Commit("src/X.cs", "x\n", "Add X");
        _repo.RunGit("checkout", "-q", "develop");
        Commit("parent-only.txt", "later\n", "Parent moved on");
        Publish("develop");
        _repo.RunGit("checkout", "-q", "feature/x");

        var evidence = await PullRequestEvidence("feature/x", "develop");

        Assert.Equal(1, evidence.CommitCount);
        Assert.Equal("src/X.cs", Assert.Single(evidence.Files).Path);
    }

    [Fact]
    public async Task Pull_request_ignores_uncommitted_changes()
    {
        _repo.CommitInitial("base.txt", "base\n");
        Publish("main");
        _repo.RunGit("checkout", "-q", "-b", "feature/y");
        Commit("committed.txt", "in the branch\n", "Commit");
        _repo.WriteFile("dirty.txt", "not committed\n");
        _repo.WriteFile("base.txt", "edited but not committed\n");

        var evidence = await PullRequestEvidence("feature/y", "main");

        Assert.Equal("committed.txt", Assert.Single(evidence.Files).Path);
    }

    [Fact]
    public async Task Missing_target_is_an_error_never_a_fallback_to_another_branch()
    {
        _repo.CommitInitial();
        Publish("main");
        _repo.RunGit("checkout", "-q", "-b", "feature/z");
        Commit("z.txt", "z\n", "Z");

        var evidence = await PullRequestEvidence("feature/z", "release/9.9");

        Assert.False(evidence.Success);
        Assert.Equal(GitEvidenceErrorCodes.TargetBranchNotFound, evidence.ErrorCode);
        Assert.Empty(evidence.Files);
    }

    [Fact]
    public async Task Missing_source_unrelated_histories_and_no_commits_are_explicit_errors()
    {
        _repo.CommitInitial();
        Publish("main");

        Assert.Equal(GitEvidenceErrorCodes.SourceBranchNotFound, (await PullRequestEvidence("feature/none", "main")).ErrorCode);
        Assert.Equal(GitEvidenceErrorCodes.NoCommitsAhead, (await PullRequestEvidence("main", "main")).ErrorCode);

        _repo.RunGit("checkout", "-q", "--orphan", "island");
        _repo.RunGit("rm", "-rq", "--cached", ".");
        Commit("island.txt", "x\n", "Unrelated root");
        Assert.Equal(GitEvidenceErrorCodes.NoMergeBase, (await PullRequestEvidence("island", "main")).ErrorCode);
    }

    [Fact]
    public async Task A_tag_named_like_the_target_branch_is_never_used()
    {
        _repo.CommitInitial("base.txt", "base\n");
        _repo.RunGit("tag", "release");
        _repo.RunGit("checkout", "-q", "-b", "feature/t");
        Commit("t.txt", "t\n", "T");

        // No origin/release branch exists, only a tag called release: that is an error, not the tag.
        var evidence = await PullRequestEvidence("feature/t", "release");

        Assert.Equal(GitEvidenceErrorCodes.TargetBranchNotFound, evidence.ErrorCode);
    }

    [Theory]
    [InlineData("-c")]
    [InlineData("--upload-pack=evil")]
    [InlineData("main..HEAD")]
    [InlineData("a b")]
    [InlineData("HEAD~1")]
    [InlineData("x@{1}")]
    public async Task Branch_names_that_could_be_options_or_revision_expressions_are_rejected(string name)
    {
        _repo.CommitInitial();
        var evidence = await PullRequestEvidence(name, "main");
        Assert.Equal(GitEvidenceErrorCodes.InvalidBranchName, evidence.ErrorCode);
    }

    [Fact]
    public async Task Evidence_collection_never_changes_the_repository()
    {
        _repo.CommitInitial("a.txt", "a\n");
        _repo.WriteFile("a.txt", "b\n");
        _repo.RunGit("add", "a.txt");
        _repo.WriteFile("a.txt", "c\n");
        _repo.WriteFile("new.txt", "n\n");
        var statusBefore = _repo.RunGit("status", "--porcelain=v2").Stdout;
        var indexBefore = File.ReadAllBytes(Path.Combine(_repo.RepositoryPath, ".git", "index"));

        await CommitEvidence(GitCommitEvidenceMode.All);
        await CommitEvidence(GitCommitEvidenceMode.Staged);

        Assert.Equal(statusBefore, _repo.RunGit("status", "--porcelain=v2").Stdout);
        Assert.Equal(indexBefore, File.ReadAllBytes(Path.Combine(_repo.RepositoryPath, ".git", "index")));
        Assert.All(_runner.Calls, args => Assert.DoesNotContain(args, a => a is "add" or "commit" or "fetch" or "reset" or "checkout" or "stash"));
    }

    [Fact]
    public async Task Cancellation_stops_collection()
    {
        _repo.CommitInitial();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _collector.CollectCommitAsync(_repo.RepositoryPath, Scope(), GitCommitEvidenceMode.All, new GitEvidenceLimits(), cts.Token));
    }

    /// <summary>Records every git argument list and the largest stdout actually read.</summary>
    private sealed class CountingRunner(IBoundedProcessRunner inner) : IBoundedProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public int MaxStdoutBytesSeen { get; private set; }

        public async Task<BoundedProcessResult> RunAsync(BoundedProcessRequest request, CancellationToken cancellationToken)
        {
            lock (Calls)
                Calls.Add(request.Arguments);
            var result = await inner.RunAsync(request, cancellationToken);
            lock (Calls)
                MaxStdoutBytesSeen = Math.Max(MaxStdoutBytesSeen, result.Stdout.Length);
            return result;
        }
    }
}
