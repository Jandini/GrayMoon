namespace GrayMoon.Worker.Models;

/// <summary>
/// Outcome of <c>IGitService.CreateBranchAsync</c>. <see cref="ResolvedStartRef"/> and <see cref="BaseCommitSha"/> are the
/// ref the branch was created from and the commit it points at; both are null when the branch already existed or the
/// repository has no commits, because then nothing was created from a commit.
/// </summary>
public sealed record CreateBranchResult(bool Success, string? ErrorMessage, bool AlreadyExisted, string? ResolvedStartRef = null, string? BaseCommitSha = null)
{
    /// <summary>The three values callers read before the baseline existed.</summary>
    public void Deconstruct(out bool success, out string? errorMessage, out bool alreadyExisted)
    {
        success = Success;
        errorMessage = ErrorMessage;
        alreadyExisted = AlreadyExisted;
    }
}
