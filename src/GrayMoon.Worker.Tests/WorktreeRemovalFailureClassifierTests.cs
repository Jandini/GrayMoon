using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Tests;

public sealed class WorktreeRemovalFailureClassifierTests
{
    [Theory]
    [InlineData("error: failed to delete 'C:/f/repo': Permission denied", WorktreeRemovalFailureKind.PathInUse)]
    [InlineData("error: failed to delete 'C:/f/repo': Directory not empty", WorktreeRemovalFailureKind.PathInUse)]
    [InlineData("The process cannot access the file because it is being used by another process.", WorktreeRemovalFailureKind.PathInUse)]
    [InlineData("rm: cannot remove 'x': Device or resource busy", WorktreeRemovalFailureKind.PathInUse)]
    [InlineData("Unlink of file 'bin/app.dll' failed. Should I try again? (y/n) n", WorktreeRemovalFailureKind.PathInUse)]
    [InlineData("fatal: unable to access 'C:/f/repo/.git': Permission denied", WorktreeRemovalFailureKind.AccessDenied)]
    [InlineData("Access is denied.", WorktreeRemovalFailureKind.AccessDenied)]
    [InlineData("fatal: 'C:/f/repo' contains modified or untracked files, use --force to delete it", WorktreeRemovalFailureKind.UncommittedChanges)]
    [InlineData("fatal: cannot remove a locked working tree, lock reason: testing", WorktreeRemovalFailureKind.WorktreeLocked)]
    [InlineData("fatal: 'C:/f/repo' is not a working tree", WorktreeRemovalFailureKind.GitRefusal)]
    [InlineData("fatal: validation failed, cannot remove working tree: '.git' is not a file", WorktreeRemovalFailureKind.GitRefusal)]
    [InlineData("something unexpected happened", WorktreeRemovalFailureKind.Unknown)]
    public void Classifies_git_messages(string message, WorktreeRemovalFailureKind expected)
    {
        Assert.Equal(expected, WorktreeRemovalFailureClassifier.Classify("GitFailed", message));
    }

    [Theory]
    [InlineData("RepositoryNotFound")]
    [InlineData("InvalidWorktreePath")]
    [InlineData("CannotRemovePrimary")]
    public void Logical_worker_error_codes_are_git_refusals_even_with_in_use_words(string code)
    {
        Assert.Equal(WorktreeRemovalFailureKind.GitRefusal, WorktreeRemovalFailureClassifier.Classify(code, "Permission denied"));
    }

    [Fact]
    public void No_code_and_no_message_is_no_failure()
    {
        Assert.Equal(WorktreeRemovalFailureKind.None, WorktreeRemovalFailureClassifier.Classify(null, null));
    }
}
