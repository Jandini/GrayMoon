namespace GrayMoon.Agent.Abstractions;

/// <summary>Identifies which git hook triggered a notify job, controlling what git operations the handler performs.</summary>
public enum NotifyHookKind
{
    /// <summary>post-commit / post-update: re-runs GitVersion and commit counts. No fetch needed.</summary>
    Commit = 0,
    /// <summary>post-checkout: runs GitVersion and git fetch in parallel, then commit counts.</summary>
    Checkout = 1,
    /// <summary>post-merge: re-runs GitVersion and commit counts. No fetch needed.</summary>
    Merge = 2
}
