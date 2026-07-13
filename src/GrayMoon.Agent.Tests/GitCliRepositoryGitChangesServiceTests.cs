using GrayMoon.Agent.Abstractions;
using GrayMoon.Agent.Services;
using GrayMoon.Agent.Services.GitChanges;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.Agent.Tests;

public sealed class GitCliRepositoryGitChangesServiceTests : IDisposable
{
    private readonly TempGitRepositoryFixture _repo = new();
    private readonly GitCliRepositoryGitChangesService _service;

    public GitCliRepositoryGitChangesServiceTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance);
        var runner = new GitProcessRunner(commandLine, NullLogger<GitProcessRunner>.Instance);
        _service = new GitCliRepositoryGitChangesService(runner, NullLogger<GitCliRepositoryGitChangesService>.Instance);
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task Repository_with_no_commits_reports_unborn_branch()
    {
        var result = await _service.GetStatusAsync(_repo.RepositoryPath, 1, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Snapshot!.IsUnbornBranch);
        Assert.Null(result.Snapshot.HeadCommit);
    }

    [Fact]
    public async Task Modified_file_appears_as_unstaged_change()
    {
        _repo.CommitInitial("file.txt", "original\n");
        _repo.WriteFile("file.txt", "changed\n");

        var result = await _service.GetStatusAsync(_repo.RepositoryPath, 1, CancellationToken.None);

        var change = Assert.Single(result.Snapshot!.Changes);
        Assert.Equal("file.txt", change.Path);
        Assert.Equal(GitChangeKind.None, change.IndexChange);
        Assert.Equal(GitChangeKind.Modified, change.WorktreeChange);
    }

    [Fact]
    public async Task Staged_file_appears_as_staged_change()
    {
        _repo.CommitInitial("file.txt", "original\n");
        _repo.WriteFile("file.txt", "changed\n");
        _repo.RunGit("add", "file.txt");

        var result = await _service.GetStatusAsync(_repo.RepositoryPath, 1, CancellationToken.None);

        var change = Assert.Single(result.Snapshot!.Changes);
        Assert.Equal(GitChangeKind.Modified, change.IndexChange);
        Assert.Equal(GitChangeKind.None, change.WorktreeChange);
    }

    [Fact]
    public async Task File_with_both_staged_and_unstaged_modification()
    {
        _repo.CommitInitial("file.txt", "original\n");
        _repo.WriteFile("file.txt", "staged change\n");
        _repo.RunGit("add", "file.txt");
        _repo.WriteFile("file.txt", "staged change\nplus unstaged\n");

        var result = await _service.GetStatusAsync(_repo.RepositoryPath, 1, CancellationToken.None);

        var change = Assert.Single(result.Snapshot!.Changes);
        Assert.Equal(GitChangeKind.Modified, change.IndexChange);
        Assert.Equal(GitChangeKind.Modified, change.WorktreeChange);
    }

    [Fact]
    public async Task Untracked_file_appears_as_untracked_change()
    {
        _repo.CommitInitial();
        _repo.WriteFile("new.txt", "hello\n");

        var result = await _service.GetStatusAsync(_repo.RepositoryPath, 1, CancellationToken.None);

        var change = Assert.Single(result.Snapshot!.Changes);
        Assert.Equal("new.txt", change.Path);
        Assert.False(change.IsTracked);
        Assert.Equal(GitChangeKind.Untracked, change.WorktreeChange);
    }

    [Fact]
    public async Task Deleted_file_appears_as_change()
    {
        _repo.CommitInitial("file.txt", "content\n");
        _repo.DeleteFile("file.txt");

        var result = await _service.GetStatusAsync(_repo.RepositoryPath, 1, CancellationToken.None);

        var change = Assert.Single(result.Snapshot!.Changes);
        Assert.Equal(GitChangeKind.Deleted, change.WorktreeChange);
    }

    [Fact]
    public async Task Rename_is_detected_after_staging()
    {
        _repo.CommitInitial("old.txt", "content that persists so the rename similarity is detected\n");
        _repo.RunGit("mv", "old.txt", "new.txt");

        var result = await _service.GetStatusAsync(_repo.RepositoryPath, 1, CancellationToken.None);

        var change = Assert.Single(result.Snapshot!.Changes);
        Assert.Equal("new.txt", change.Path);
        Assert.Equal("old.txt", change.OriginalPath);
        Assert.Equal(GitChangeKind.Renamed, change.IndexChange);
    }

    [Fact]
    public async Task Conflict_state_marks_entry_as_conflicted()
    {
        _repo.CommitInitial("file.txt", "base\n");
        _repo.RunGit("checkout", "-b", "feature");
        _repo.WriteFile("file.txt", "feature change\n");
        _repo.RunGit("commit", "-am", "feature change");
        _repo.RunGit("checkout", "main");
        _repo.WriteFile("file.txt", "main change\n");
        _repo.RunGit("commit", "-am", "main change");
        _repo.RunGit("merge", "feature");

        var result = await _service.GetStatusAsync(_repo.RepositoryPath, 1, CancellationToken.None);

        var change = Assert.Single(result.Snapshot!.Changes);
        Assert.True(change.IsConflicted);
        Assert.True(result.Snapshot.IsMerging);
    }

    [Fact]
    public async Task Stage_explicit_path_moves_file_from_changed_to_staged()
    {
        _repo.CommitInitial("file.txt", "original\n");
        _repo.WriteFile("file.txt", "changed\n");

        var stageResult = await _service.StageAsync(
            _repo.RepositoryPath,
            new GitStageOperationRequest(GitChangeOperationScope.ExplicitPaths, ["file.txt"]),
            2,
            CancellationToken.None);

        Assert.True(stageResult.Success);
        var change = Assert.Single(stageResult.Snapshot!.Changes);
        Assert.Equal(GitChangeKind.Modified, change.IndexChange);
        Assert.Equal(GitChangeKind.None, change.WorktreeChange);
    }

    [Fact]
    public async Task Stage_folder_stages_all_descendants_including_untracked()
    {
        _repo.CommitInitial();
        _repo.WriteFile("src/a.txt", "a\n");
        _repo.WriteFile("src/b.txt", "b\n");

        var stageResult = await _service.StageAsync(
            _repo.RepositoryPath,
            new GitStageOperationRequest(GitChangeOperationScope.Folder, ["src"]),
            2,
            CancellationToken.None);

        Assert.True(stageResult.Success);
        Assert.Equal(2, stageResult.Snapshot!.Changes.Count);
        Assert.All(stageResult.Snapshot.Changes, c => Assert.Equal(GitChangeKind.Added, c.IndexChange));
    }

    [Fact]
    public async Task Stage_repository_stages_every_changed_file()
    {
        _repo.CommitInitial("tracked.txt", "original\n");
        _repo.WriteFile("tracked.txt", "changed\n");
        _repo.WriteFile("untracked.txt", "new\n");

        var stageResult = await _service.StageAsync(
            _repo.RepositoryPath,
            new GitStageOperationRequest(GitChangeOperationScope.Repository, []),
            2,
            CancellationToken.None);

        Assert.True(stageResult.Success);
        Assert.Equal(2, stageResult.Snapshot!.Changes.Count);
        Assert.All(stageResult.Snapshot.Changes, c => Assert.Equal(GitChangeKind.None, c.WorktreeChange));
    }

    [Fact]
    public async Task Unstage_explicit_path_moves_file_back_to_changed()
    {
        _repo.CommitInitial("file.txt", "original\n");
        _repo.WriteFile("file.txt", "changed\n");
        _repo.RunGit("add", "file.txt");

        var unstageResult = await _service.UnstageAsync(
            _repo.RepositoryPath,
            new GitStageOperationRequest(GitChangeOperationScope.ExplicitPaths, ["file.txt"]),
            2,
            CancellationToken.None);

        Assert.True(unstageResult.Success);
        var change = Assert.Single(unstageResult.Snapshot!.Changes);
        Assert.Equal(GitChangeKind.None, change.IndexChange);
        Assert.Equal(GitChangeKind.Modified, change.WorktreeChange);
    }

    [Fact]
    public async Task Unstage_repository_clears_all_staged_changes()
    {
        _repo.CommitInitial("a.txt", "a\n");
        _repo.WriteFile("a.txt", "a changed\n");
        _repo.WriteFile("b.txt", "b\n");
        _repo.RunGit("add", "--all");

        var unstageResult = await _service.UnstageAsync(
            _repo.RepositoryPath,
            new GitStageOperationRequest(GitChangeOperationScope.Repository, []),
            2,
            CancellationToken.None);

        Assert.True(unstageResult.Success);
        Assert.All(unstageResult.Snapshot!.Changes, c => Assert.Equal(GitChangeKind.None, c.IndexChange));
    }

    [Fact]
    public async Task Unstage_on_unborn_branch_falls_back_to_reset_and_succeeds()
    {
        _repo.WriteFile("a.txt", "a\n");
        _repo.RunGit("add", "--all");

        var unstageResult = await _service.UnstageAsync(
            _repo.RepositoryPath,
            new GitStageOperationRequest(GitChangeOperationScope.Repository, []),
            2,
            CancellationToken.None);

        Assert.True(unstageResult.Success);
        var change = Assert.Single(unstageResult.Snapshot!.Changes);
        Assert.Equal(GitChangeKind.None, change.IndexChange);
        Assert.Equal(GitChangeKind.Untracked, change.WorktreeChange);
    }

    [Fact]
    public async Task Commit_staged_creates_a_commit_and_clears_staged_changes()
    {
        _repo.CommitInitial("file.txt", "original\n");
        _repo.WriteFile("file.txt", "changed\n");
        _repo.RunGit("add", "file.txt");

        var commitResult = await _service.CommitAsync(
            _repo.RepositoryPath,
            new GitCommitOperationRequest("Update file.txt", StageAllFirst: false),
            2,
            CancellationToken.None);

        Assert.True(commitResult.Success);
        Assert.False(string.IsNullOrWhiteSpace(commitResult.CommitSha));
        Assert.Empty(commitResult.Snapshot!.Changes);
    }

    [Fact]
    public async Task Commit_all_stages_everything_then_commits()
    {
        _repo.CommitInitial("tracked.txt", "original\n");
        _repo.WriteFile("tracked.txt", "changed\n");
        _repo.WriteFile("untracked.txt", "new\n");

        var commitResult = await _service.CommitAsync(
            _repo.RepositoryPath,
            new GitCommitOperationRequest("Commit everything", StageAllFirst: true),
            2,
            CancellationToken.None);

        Assert.True(commitResult.Success);
        Assert.Empty(commitResult.Snapshot!.Changes);

        Assert.Equal("changed\n", _repo.ReadFile("tracked.txt"));
        Assert.Equal("new\n", _repo.ReadFile("untracked.txt"));
    }

    [Fact]
    public async Task Commit_on_unborn_branch_creates_the_first_commit()
    {
        _repo.WriteFile("first.txt", "hello\n");

        var commitResult = await _service.CommitAsync(
            _repo.RepositoryPath,
            new GitCommitOperationRequest("First commit", StageAllFirst: true),
            2,
            CancellationToken.None);

        Assert.True(commitResult.Success);
        Assert.False(commitResult.Snapshot!.IsUnbornBranch);
        Assert.NotNull(commitResult.Snapshot.HeadCommit);
    }

    [Fact]
    public async Task Commit_with_nothing_staged_fails_with_stable_error_code()
    {
        _repo.CommitInitial();

        var commitResult = await _service.CommitAsync(
            _repo.RepositoryPath,
            new GitCommitOperationRequest("Nothing to commit", StageAllFirst: false),
            2,
            CancellationToken.None);

        Assert.False(commitResult.Success);
        Assert.Equal("NothingStaged", commitResult.ErrorCode);
    }

    [Fact]
    public async Task Stage_rejects_path_traversal()
    {
        _repo.CommitInitial();

        var stageResult = await _service.StageAsync(
            _repo.RepositoryPath,
            new GitStageOperationRequest(GitChangeOperationScope.ExplicitPaths, ["../outside.txt"]),
            2,
            CancellationToken.None);

        Assert.False(stageResult.Success);
        Assert.Equal("InvalidPath", stageResult.ErrorCode);
    }

    [Fact]
    public async Task GetDiff_unstaged_compares_index_to_working_tree()
    {
        _repo.CommitInitial("file.txt", "line one\n");
        _repo.WriteFile("file.txt", "line one\nline two\n");

        var diff = await _service.GetDiffAsync(
            _repo.RepositoryPath,
            new GitDiffRequest("file.txt", GitDiffComparison.Unstaged),
            CancellationToken.None);

        Assert.Equal(GitDiffContentState.Normal, diff.State);
        Assert.Equal("line one\n", diff.OriginalContent);
        Assert.Equal("line one\nline two\n", diff.ModifiedContent);
    }

    [Fact]
    public async Task GetDiff_staged_compares_head_to_index()
    {
        _repo.CommitInitial("file.txt", "line one\n");
        _repo.WriteFile("file.txt", "line one\nline two\n");
        _repo.RunGit("add", "file.txt");

        var diff = await _service.GetDiffAsync(
            _repo.RepositoryPath,
            new GitDiffRequest("file.txt", GitDiffComparison.Staged),
            CancellationToken.None);

        Assert.Equal(GitDiffContentState.Normal, diff.State);
        Assert.Equal("line one\n", diff.OriginalContent);
        Assert.Equal("line one\nline two\n", diff.ModifiedContent);
    }

    [Fact]
    public async Task GetDiff_new_untracked_file_has_empty_original()
    {
        _repo.CommitInitial();
        _repo.WriteFile("new.txt", "brand new content\n");

        var diff = await _service.GetDiffAsync(
            _repo.RepositoryPath,
            new GitDiffRequest("new.txt", GitDiffComparison.Unstaged),
            CancellationToken.None);

        Assert.Equal(GitDiffContentState.NewFile, diff.State);
        Assert.Equal(string.Empty, diff.OriginalContent);
        Assert.Equal("brand new content\n", diff.ModifiedContent);
    }

    [Fact]
    public async Task GetDiff_deleted_file_has_empty_modified()
    {
        _repo.CommitInitial("file.txt", "content\n");
        _repo.DeleteFile("file.txt");

        var diff = await _service.GetDiffAsync(
            _repo.RepositoryPath,
            new GitDiffRequest("file.txt", GitDiffComparison.Unstaged),
            CancellationToken.None);

        Assert.Equal(GitDiffContentState.DeletedFile, diff.State);
        Assert.Equal("content\n", diff.OriginalContent);
        Assert.Equal(string.Empty, diff.ModifiedContent);
    }

    [Fact]
    public async Task GetDiff_rejects_path_traversal()
    {
        _repo.CommitInitial();

        var diff = await _service.GetDiffAsync(
            _repo.RepositoryPath,
            new GitDiffRequest("../outside.txt", GitDiffComparison.Unstaged),
            CancellationToken.None);

        Assert.Equal(GitDiffContentState.Error, diff.State);
        Assert.NotNull(diff.ErrorMessage);
    }
}
