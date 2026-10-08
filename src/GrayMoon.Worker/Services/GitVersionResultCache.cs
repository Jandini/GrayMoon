using System.Collections.Concurrent;
using System.Diagnostics;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Models;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Services;

/// <summary>
/// GrayMoon's own cache of successful GitVersion results. A hit means the version inputs
/// (<see cref="GitVersionInputFingerprint"/>) are exactly what they were when GitVersion last succeeded for the
/// same repository and invocation, so GitVersion is not started at all. It never reads GitVersion's private
/// cache. Bounded, in memory, thread safe; identical concurrent requests share one GitVersion run.
/// Only successes are kept: a failed run is never replayed, and the next request runs GitVersion again.
/// </summary>
public sealed class GitVersionResultCache(ILogger<GitVersionResultCache>? logger = null, int maxEntries = 256)
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly ConcurrentDictionary<Key, Entry> _entries = new();
    private readonly ConcurrentDictionary<(Key Key, string Inputs), TaskCompletionSource<RepositoryVersionResult>> _inFlight = new();
    private long _clock;

    private readonly record struct Key(string Path, bool NonNormalize, string? CommitSha);

    private sealed class Entry(GitVersionInputFingerprint fingerprint, GitVersionResult result, long stamp)
    {
        public GitVersionInputFingerprint Fingerprint { get; } = fingerprint;
        public GitVersionResult Result { get; } = result;
        public long LastUsed { get; set; } = stamp;
    }

    internal int Count => _entries.Count;

    /// <summary>Returns the remembered result when the inputs are unchanged, otherwise runs <paramref name="run"/>.</summary>
    public async Task<RepositoryVersionResult> GetOrRunAsync(
        string repoPath,
        RepositoryVersionOptions options,
        Func<CancellationToken, Task<RepositoryVersionResult>> run,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var fingerprint = GitVersionInputFingerprint.TryCompute(repoPath, options);
            if (fingerprint is null)
            {
                logger?.LogDebug("GitVersion cache bypass for {RepoPath}: inputs cannot be fingerprinted", repoPath);
                return await TimedRunAsync(repoPath, run, ct);
            }

            var key = MakeKey(repoPath, options);
            var inputs = fingerprint.Head + fingerprint.HeadIdentity + fingerprint.Refs + fingerprint.Config + fingerprint.Tool + fingerprint.Invocation;

            string reason;
            if (_entries.TryGetValue(key, out var entry))
            {
                var difference = fingerprint.FirstDifference(entry.Fingerprint);
                if (difference is null)
                {
                    entry.LastUsed = Interlocked.Increment(ref _clock);
                    logger?.LogDebug("GitVersion cache hit for {RepoPath}", repoPath);
                    return new RepositoryVersionResult(Probed: true, Copy(entry.Result), null);
                }

                reason = difference;
            }
            else
            {
                reason = _entries.Keys.Any(k => PathComparer.Equals(k.Path, key.Path)) ? "INVOCATION_CHANGED" : "NO_PREVIOUS_SUCCESS";
            }

            var completion = new TaskCompletionSource<RepositoryVersionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var flightKey = (key, inputs);
            if (!_inFlight.TryAdd(flightKey, completion))
            {
                // An identical request is already running GitVersion: share its answer.
                if (!_inFlight.TryGetValue(flightKey, out var running))
                    continue;
                try
                {
                    return Copy(await running.Task.WaitAsync(ct));
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < 3)
                {
                    continue; // the request that was running it was cancelled, not this one
                }
            }

            logger?.LogDebug("GitVersion cache miss for {RepoPath}: {Reason}", repoPath, reason);
            try
            {
                var result = await TimedRunAsync(repoPath, run, ct);
                if (result.Probed && result.Error is null && result.Result is not null)
                {
                    // Keep the answer only for the state it was asked in.
                    var after = GitVersionInputFingerprint.TryCompute(repoPath, options);
                    if (after is not null && after.FirstDifference(fingerprint) is null)
                        Store(key, fingerprint, result.Result);
                    else
                        _entries.TryRemove(key, out _);
                }
                else
                {
                    _entries.TryRemove(key, out _);
                }

                completion.TrySetResult(result);
                return Copy(result);
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled();
                throw;
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
                throw;
            }
            finally
            {
                _inFlight.TryRemove(flightKey, out _);
            }
        }
    }

    private async Task<RepositoryVersionResult> TimedRunAsync(
        string repoPath, Func<CancellationToken, Task<RepositoryVersionResult>> run, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return await run(ct);
        }
        finally
        {
            logger?.LogDebug("GitVersion executed in {ElapsedMs}ms for {RepoPath}", sw.ElapsedMilliseconds, repoPath);
        }
    }

    private void Store(Key key, GitVersionInputFingerprint fingerprint, GitVersionResult result)
    {
        _entries[key] = new Entry(fingerprint, Copy(result), Interlocked.Increment(ref _clock));
        while (_entries.Count > maxEntries)
        {
            var oldest = _entries.OrderBy(e => e.Value.LastUsed).Select(e => e.Key).First();
            _entries.TryRemove(oldest, out _);
        }
    }

    private static Key MakeKey(string repoPath, RepositoryVersionOptions options)
    {
        string path;
        try { path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoPath)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { path = repoPath; }
        if (OperatingSystem.IsWindows())
            path = path.ToUpperInvariant();
        return new Key(path, options.NonNormalize, string.IsNullOrWhiteSpace(options.CommitSha) ? null : options.CommitSha.Trim());
    }

    // The result is a mutable class; callers never share an instance with the cache or with each other.
    private static GitVersionResult Copy(GitVersionResult r) => new()
    {
        InformationalVersion = r.InformationalVersion,
        BranchName = r.BranchName,
        EscapedBranchName = r.EscapedBranchName,
    };

    private static RepositoryVersionResult Copy(RepositoryVersionResult r)
        => r.Result is null ? r : r with { Result = Copy(r.Result) };
}
