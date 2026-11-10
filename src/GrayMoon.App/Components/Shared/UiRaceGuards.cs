using GrayMoon.App.Services.Queries;

namespace GrayMoon.App.Components.Shared;

/// <summary>
/// One generation number and one <see cref="CancellationTokenSource"/> per load of a modal or panel. Starting a
/// new load cancels the previous one, and a result that comes back for an older generation is dropped instead
/// of being merged into what the user is looking at now (reopening a modal on another item, typing a new search).
/// </summary>
internal sealed class LoadGeneration : IDisposable
{
    private int _generation;
    private CancellationTokenSource? _cts;

    public int Current => Volatile.Read(ref _generation);

    /// <summary>Starts a new load: the previous one is cancelled and its results no longer apply.</summary>
    public (int Generation, CancellationToken Token) Begin()
    {
        var generation = Interlocked.Increment(ref _generation);
        var cts = new CancellationTokenSource();
        CancelAndDispose(Interlocked.Exchange(ref _cts, cts));
        return (generation, cts.Token);
    }

    /// <summary>Ends the current load without starting another (the modal closed, the component was disposed).</summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        CancelAndDispose(Interlocked.Exchange(ref _cts, null));
    }

    public bool IsCurrent(int generation) => generation == Current;

    public void Dispose() => Invalidate();

    private static void CancelAndDispose(CancellationTokenSource? cts)
    {
        if (cts == null) return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
        cts.Dispose();
    }
}

/// <summary>
/// The keyboard path of a modal's default action obeys the same rule as its button: nothing is submitted while
/// a submit is already running, and a key held down (auto-repeat) submits once.
/// </summary>
internal static class ModalSubmitGuard
{
    public static bool CanSubmit(bool isPlainEnter, bool isKeyRepeat, bool isSubmitting)
        => isPlainEnter && !isKeyRepeat && !isSubmitting;
}

/// <summary>The workflow run a live terminal polls.</summary>
internal readonly record struct LiveTerminalTarget(string ConnectorName, string Owner, string RepositoryName, long RunId)
{
    public bool IsPollable => RunId > 0;
}

internal static class LiveTerminalPolling
{
    /// <summary>Wait after a failed poll before asking again (the "waiting for jobs" interval).</summary>
    public const int RetryDelayAfterFailureMs = 8000;

    /// <summary>
    /// True when the loops have to be (re)started or stopped: nothing is polled yet and there is a run to poll,
    /// or the parameters now name another run than the one being polled.
    /// </summary>
    public static bool ShouldRestart(LiveTerminalTarget? polled, LiveTerminalTarget next) =>
        polled is { } current ? current != next : next.IsPollable;
}

/// <summary>What <c>LoadingOverlay</c> has to tell the matrix canvas after a render.</summary>
internal readonly record struct MatrixOverlayInterop(bool SetDim, bool Start, bool Stop)
{
    public bool Any => SetDim || Start || Stop;
}

/// <summary>
/// Decides the matrix-canvas JS calls from the state change since the last render, so a render that changed
/// nothing (a terminal line, a progress message) makes no interop call at all.
/// </summary>
internal static class MatrixOverlayTransition
{
    public static MatrixOverlayInterop Decide(
        bool wasVisible, bool wasMatrix, int? lastDimPercent,
        bool isVisible, bool useMatrix, int dimPercent)
    {
        var showing = useMatrix && isVisible;
        var wasShowing = wasMatrix && wasVisible;
        var start = showing && !wasShowing;
        var stop = !showing && wasShowing;
        // The dim is read by the canvas while it runs, so it is sent when the rain starts and when the value changes.
        var setDim = useMatrix && (start || lastDimPercent != dimPercent);
        return new MatrixOverlayInterop(setDim, start, stop);
    }
}

/// <summary>
/// Worktree occupancy is keyed by local branch name. A remote-tracking name (<c>origin/x</c>) selected on the
/// Remotes tab checks out local branch <c>x</c>, so that is the key to look up.
/// </summary>
internal static class BranchOccupancyKey
{
    private const string OriginPrefix = "origin/";

    public static string For(string branch) =>
        branch.StartsWith(OriginPrefix, StringComparison.OrdinalIgnoreCase)
            ? branch[OriginPrefix.Length..]
            : branch;
}

/// <summary>
/// Repository sets the notification popup acts on and warns about. They are computed from every repository of
/// the selected context (the snapshot the Repositories page uses), never from the popup's listed rows, which
/// stop at <c>WorkspacePendingActionsService.MaxListedActionRepos</c>, and never from the Workspace link
/// columns when a Feature is selected.
/// </summary>
internal static class NotificationPanelRepoSets
{
    public sealed record DefaultBranchRepo(string RepositoryName, string DefaultBranchName);

    private static bool IsOnTag(WorkspaceRepositoryLinkListItemDto link) => !string.IsNullOrWhiteSpace(link.CheckedOutTag);

    private static bool IsOnDefaultBranch(WorkspaceRepositoryLinkListItemDto link) =>
        !string.IsNullOrWhiteSpace(link.DefaultBranchName)
        && string.Equals(link.BranchName, link.DefaultBranchName, StringComparison.Ordinal);

    /// <summary>Repositories Update will commit to: not on a tag, with a dependency or version-file update pending.</summary>
    public static HashSet<int> NeedingUpdate(IEnumerable<WorkspaceRepositoryLinkListItemDto> links) =>
        links.Where(l => !IsOnTag(l) && ((l.UnmatchedDeps ?? 0) > 0 || (l.OutOfDateFileRepos ?? 0) > 0))
            .Select(l => l.RepositoryId)
            .ToHashSet();

    /// <summary>Repositories Pull will touch: not on a tag, with incoming commits.</summary>
    public static List<int> WithIncoming(IEnumerable<WorkspaceRepositoryLinkListItemDto> links) =>
        links.Where(l => !IsOnTag(l) && (l.IncomingCommits ?? 0) > 0)
            .Select(l => l.RepositoryId)
            .ToList();

    /// <summary>Repositories whose Update commit would land on the default branch.</summary>
    public static List<DefaultBranchRepo> UpdateOnDefaultBranch(IEnumerable<WorkspaceRepositoryLinkListItemDto> links) =>
        links.Where(l => !IsOnTag(l) && (l.UnmatchedDeps ?? 0) > 0 && IsOnDefaultBranch(l))
            .Select(l => new DefaultBranchRepo(l.RepositoryName, l.DefaultBranchName!))
            .ToList();

    /// <summary>Repositories whose Push would go to the default branch.</summary>
    public static List<DefaultBranchRepo> PushOnDefaultBranch(IEnumerable<WorkspaceRepositoryLinkListItemDto> links) =>
        links.Where(l => !IsOnTag(l) && (l.OutgoingCommits ?? 0) > 0 && IsOnDefaultBranch(l))
            .Select(l => new DefaultBranchRepo(l.RepositoryName, l.DefaultBranchName!))
            .ToList();

    /// <summary>Repositories Undo Push Commits would rewind, with the number of commits each loses.</summary>
    public static List<(string RepoName, int OutgoingCommits)> WithOutgoing(IEnumerable<WorkspaceRepositoryLinkListItemDto> links) =>
        links.Where(l => !IsOnTag(l) && (l.OutgoingCommits ?? 0) > 0)
            .Select(l => (l.RepositoryName, l.OutgoingCommits ?? 0))
            .ToList();
}
