namespace GrayMoon.Common.Git;

/// <summary>
/// Allowlist of git failures that are worth retrying: transport, DNS, rate-limit, server 5xx,
/// and short-lived lock contention. Everything else (dirty worktree, conflicts, auth, missing
/// refs, protected branches, non-fast-forward) is definitive and must fail immediately.
/// </summary>
public static class GitRetryClassifier
{
    /// <summary>
    /// Known-transient substrings from git/curl/libcurl and from
    /// <c>CommandLineService</c>'s synthetic timeout message. HTTP status codes are handled
    /// separately by <see cref="HasTransientHttpStatus"/> so 401/403/404 are never treated as
    /// retryable just because a nearby 5xx pattern is too loose.
    /// </summary>
    private static readonly string[] TransientMarkers =
    [
        // DNS / name resolution
        "Could not resolve host",
        "Couldn't resolve host",
        "Temporary failure in name resolution",
        "Name or service not known",
        "Unable to look up",

        // TCP / connectivity
        "Failed to connect",
        "Connection timed out",
        "Connection reset by peer",
        "Connection refused",
        "Network is unreachable",
        "No route to host",

        // HTTP / pack transport (Chromium depot_tools GIT_TRANSIENT_ERRORS)
        "The remote end hung up unexpectedly",
        "RPC failed",
        "Early EOF",
        "empty reply from server",
        "protocol error: bad pack header",
        "expected ACK/NAK",
        "curl 7",
        "curl 28",
        "curl 35",
        "curl 52",
        "curl 56",
        "curl 92",

        // TLS transport errors (not certificate-policy failures)
        "SSL_ERROR_SYSCALL",
        "TLS packet with unexpected length",
        "Recv failure",

        // Remote backend flake / remote lock
        "failed to lock",
        "remote error: Internal Server Error",

        // Local lock contention (index.lock, refs.lock, packed-refs.lock)
        "index.lock",
        ".lock': File exists",
        "being used by another process",

        // CommandLineService / Polly timeout fallback
        "Operation timed out after",
    ];

    /// <summary>
    /// Returns true only when a later identical command can reasonably succeed without changing
    /// repository state. Exit 0 and empty/unknown output are never retryable.
    /// </summary>
    public static bool IsRetryable(int exitCode, string? stdout, string? stderr)
    {
        if (exitCode == 0)
            return false;

        var text = string.Concat(stdout ?? string.Empty, stderr ?? string.Empty);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (ContainsAny(text, TransientMarkers))
            return true;

        return HasTransientHttpStatus(text);
    }

    private static bool ContainsAny(string text, string[] markers)
    {
        foreach (var marker in markers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Transient HTTP statuses git actually emits: 408, 429, and 5xx. 401/403/404 are auth or
    /// missing-resource and must not retry.
    /// </summary>
    private static bool HasTransientHttpStatus(string text)
    {
        return ContainsTransientStatusAfterPrefix(text, "returned error: ")
            || ContainsTransientStatusAfterPrefix(text, "HTTP code = ")
            || ContainsTransientStatusAfterPrefix(text, "HTTP ");
    }

    private static bool ContainsTransientStatusAfterPrefix(string text, string prefix)
    {
        var start = 0;
        while (start < text.Length)
        {
            var index = text.IndexOf(prefix, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;

            var digitsStart = index + prefix.Length;
            if (TryReadStatusCode(text, digitsStart, out var status) && IsTransientHttpStatus(status))
                return true;

            start = digitsStart;
        }

        return false;
    }

    private static bool TryReadStatusCode(string text, int start, out int status)
    {
        status = 0;
        if (start >= text.Length || !char.IsAsciiDigit(text[start]))
            return false;

        var value = 0;
        var i = start;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            value = (value * 10) + (text[i] - '0');
            if (value > 599)
                return false;
            i++;
        }

        if (i - start != 3)
            return false;

        status = value;
        return true;
    }

    private static bool IsTransientHttpStatus(int status)
        => status is 408 or 429 or (>= 500 and <= 599);
}
