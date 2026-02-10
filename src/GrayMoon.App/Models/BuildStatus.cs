namespace GrayMoon.App.Models;

/// <summary>CI build status for a branch (from GitHub commit statuses + check runs).</summary>
public enum BuildStatus
{
    /// <summary>No CI status reported to GitHub for this branch.</summary>
    None,

    /// <summary>All reported statuses/checks succeeded.</summary>
    Success,

    /// <summary>At least one status or check failed.</summary>
    Failure,

    /// <summary>At least one status or check is pending / in progress.</summary>
    Pending
}
