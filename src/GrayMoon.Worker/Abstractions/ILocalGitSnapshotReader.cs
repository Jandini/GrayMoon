using GrayMoon.Worker.Models;

namespace GrayMoon.Worker.Abstractions;

/// <summary>
/// Reads everything sync needs to know about a repository's local refs and commit graph in one in-process pass:
/// one repository open per call, no git process, no lock, no network and no writes. Fetch, origin/HEAD repair,
/// hooks and every mutation stay on the Git CLI (<see cref="IGitService"/>); the single-purpose CLI reads
/// (<see cref="IGitRepositoryReader"/>) stay for everything else and are sync's explicit fallback.
/// </summary>
public interface ILocalGitSnapshotReader
{
    /// <summary>
    /// Opens the work tree at <paramref name="repositoryPath"/> (a linked worktree included), reads the snapshot
    /// and disposes the repository before returning; nothing in the result refers back to it. Synchronous and
    /// CPU/IO bound: callers run it off the calling thread. <paramref name="ct"/> is checked between steps (a
    /// single graph walk cannot be interrupted). Throws <see cref="LocalGitReadException"/> when the repository
    /// cannot be read.
    /// </summary>
    LocalGitSnapshot Read(string repositoryPath, LocalGitSnapshotRequest request, CancellationToken ct);
}

/// <summary>What the snapshot needs from the caller.</summary>
/// <param name="DivergenceBaseBranch">
/// The Feature's parent branch from the sync request (with or without <c>origin/</c>), or null for the special
/// Workspace. It decides both comparisons that depend on it: the "vs default" counts are taken against
/// <c>origin/&lt;base&gt;</c>, and a branch without a usable upstream counts outgoing commits against the local
/// <c>&lt;base&gt;</c> branch when it exists.
/// </param>
public sealed record LocalGitSnapshotRequest(string? DivergenceBaseBranch);

/// <summary>The local repository could not be read in process. Sync falls back to the Git CLI reads.</summary>
public sealed class LocalGitReadException : Exception
{
    public LocalGitReadException(string repositoryPath, string message, Exception? innerException = null)
        : base($"{message} (repository: {repositoryPath})", innerException)
    {
        RepositoryPath = repositoryPath;
    }

    public string RepositoryPath { get; }
}
