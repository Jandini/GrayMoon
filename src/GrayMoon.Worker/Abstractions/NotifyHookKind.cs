namespace GrayMoon.Worker.Abstractions;

/// <summary>Identifies which git hook triggered a notify job, controlling what git operations the handler performs.</summary>
public enum NotifyHookKind
{
    /// <summary>post-commit / post-update: re-runs GitVersion and commit counts. No fetch needed.</summary>
    Commit = 0,
    /// <summary>post-checkout: runs GitVersion and git fetch in parallel, then commit counts.</summary>
    Checkout = 1,
    /// <summary>post-merge: re-runs GitVersion and commit counts. No fetch needed.</summary>
    Merge = 2,
    /// <summary>pre-push: re-runs GitVersion and commit counts. No fetch - uses current local state only.</summary>
    Push = 3,
    /// <summary>reference-transaction: a branch was created or deleted. Re-lists local and remote branches only; nothing about the checked-out state is read.</summary>
    Refs = 4
}
