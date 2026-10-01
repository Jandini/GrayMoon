namespace GrayMoon.Agent.Models;

/// <summary>
/// What is left on disk after Git's own worktree removal, and whether GrayMoon's own residue cleanup
/// could run at all. <see cref="ResidueRemaining"/> stays true whenever a safety guard blocked
/// deletion, even when the folder itself could be removed safely by hand.
/// </summary>
/// <param name="ResidueRemaining">True when the worktree folder still has files left on disk after this call.</param>
/// <param name="ResidueFileCount">Count of files left behind; 0 when nothing is left.</param>
/// <param name="ResidueSampleFiles">Up to 5 relative paths of files left behind, for the Remove report.</param>
/// <param name="ResidueMessage">Null when there is no residue; otherwise why cleanup did not finish (a safety guard, a locked file, or similar).</param>
public sealed record WorktreeResidueResult(
    bool ResidueRemaining,
    int ResidueFileCount,
    IReadOnlyList<string> ResidueSampleFiles,
    string? ResidueMessage)
{
    /// <summary>No residue left behind.</summary>
    public static readonly WorktreeResidueResult None = new(false, 0, [], null);
}
