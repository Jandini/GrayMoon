using System.Threading;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Fallback;
using Polly.Retry;
using Polly.Timeout;

namespace GrayMoon.Agent.Services;

internal static class GitResiliencePipelines
{
    /// <summary>
    /// Bounds a single retry attempt so a hung process cannot consume the pipeline's entire retry budget
    /// stuck on one attempt. <see cref="GitProcessRunner"/> already passes the same
    /// <paramref name="attemptTimeout"/> down to <c>CommandLineService</c> for these commands, so in
    /// practice the process itself is killed and returns a normal failed result before this Polly-level
    /// timeout would ever fire - this is a defense-in-depth backstop, not the primary timeout mechanism.
    /// The fallback strategy converts a <see cref="TimeoutRejectedException"/> (raised only if this
    /// backstop itself trips) into the same synthetic failed-result shape <c>CommandLineService</c> uses,
    /// and the retry's <c>ShouldHandle</c> also treats that exception like a normal non-zero exit so the
    /// existing retry-on-bad-exit-code behavior is preserved rather than an unhandled exception reaching
    /// callers that only expect a result tuple back from <c>ExecuteAsync</c>.
    /// </summary>
    public static ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> CreateSafeDirectoryPipeline(ILogger logger, TimeSpan attemptTimeout)
        => new ResiliencePipelineBuilder<(int ExitCode, string? Stdout, string? Stderr)>()
            .AddFallback(CreateTimeoutFallbackOptions(attemptTimeout))
            .AddRetry(new RetryStrategyOptions<(int ExitCode, string? Stdout, string? Stderr)>
            {
                ShouldHandle = new PredicateBuilder<(int ExitCode, string? Stdout, string? Stderr)>()
                    .HandleResult(r => r.ExitCode != 0)
                    .Handle<TimeoutRejectedException>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(100),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    var exitCode = args.Outcome.Result.ExitCode;
                    logger.LogWarning("Git safe-directory retry {Attempt} (ExitCode={ExitCode})", args.AttemptNumber, exitCode);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(attemptTimeout)
            .Build();

    /// <summary>
    /// Clone is the one pipeline where <paramref name="attemptTimeout"/> may legitimately be
    /// <see cref="Timeout.InfiniteTimeSpan"/> (<see cref="GitProcessOptions.CloneTimeoutSeconds"/> = 0,
    /// the default) - an initial clone can take far longer than any other git operation, and killing it
    /// partway through wastes all the work done so far. Polly's <c>AddTimeout</c> rejects a non-positive
    /// duration, so the fallback/timeout strategies are only added when a finite timeout was configured;
    /// with no timeout strategy in the pipeline, a <see cref="TimeoutRejectedException"/> can never occur,
    /// so retry's extra <c>Handle&lt;TimeoutRejectedException&gt;()</c> clause is simply inert in that case.
    /// </summary>
    public static ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> CreateClonePipeline(ILogger logger, TimeSpan attemptTimeout)
    {
        var builder = new ResiliencePipelineBuilder<(int ExitCode, string? Stdout, string? Stderr)>();
        var hasTimeout = attemptTimeout != Timeout.InfiniteTimeSpan;

        if (hasTimeout)
            builder.AddFallback(CreateTimeoutFallbackOptions(attemptTimeout));

        builder.AddRetry(new RetryStrategyOptions<(int ExitCode, string? Stdout, string? Stderr)>
        {
            ShouldHandle = new PredicateBuilder<(int ExitCode, string? Stdout, string? Stderr)>()
                .HandleResult(r => r.ExitCode != 0)
                .Handle<TimeoutRejectedException>(),
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromMilliseconds(200),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            OnRetry = args =>
            {
                var exitCode = args.Outcome.Result.ExitCode;
                logger.LogWarning("Git clone retry {Attempt} (ExitCode={ExitCode})", args.AttemptNumber, exitCode);
                return ValueTask.CompletedTask;
            }
        });

        if (hasTimeout)
            builder.AddTimeout(attemptTimeout);

        return builder.Build();
    }

    public static ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> CreateFetchPipeline(ILogger logger, TimeSpan attemptTimeout)
        => new ResiliencePipelineBuilder<(int ExitCode, string? Stdout, string? Stderr)>()
            .AddFallback(CreateTimeoutFallbackOptions(attemptTimeout))
            .AddRetry(new RetryStrategyOptions<(int ExitCode, string? Stdout, string? Stderr)>
            {
                // Retry any non-zero exit code from git fetch (e.g., transient network failures).
                ShouldHandle = new PredicateBuilder<(int ExitCode, string? Stdout, string? Stderr)>()
                    .HandleResult(r => r.ExitCode != 0)
                    .Handle<TimeoutRejectedException>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(200),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    var exitCode = args.Outcome.Result.ExitCode;
                    logger.LogWarning("Git fetch retry {Attempt} (ExitCode={ExitCode})", args.AttemptNumber, exitCode);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(attemptTimeout)
            .Build();

    public static ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> CreatePullPipeline(ILogger logger, TimeSpan attemptTimeout)
        => new ResiliencePipelineBuilder<(int ExitCode, string? Stdout, string? Stderr)>()
            .AddFallback(CreateTimeoutFallbackOptions(attemptTimeout))
            .AddRetry(new RetryStrategyOptions<(int ExitCode, string? Stdout, string? Stderr)>
            {
                // Retry transient failures (non-zero exit) but not merge conflicts which are deterministic.
                ShouldHandle = new PredicateBuilder<(int ExitCode, string? Stdout, string? Stderr)>()
                    .HandleResult(r => r.ExitCode != 0 && !IsMergeConflict(r.Stdout, r.Stderr))
                    .Handle<TimeoutRejectedException>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(200),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    var exitCode = args.Outcome.Result.ExitCode;
                    logger.LogWarning("Git pull retry {Attempt} (ExitCode={ExitCode})", args.AttemptNumber, exitCode);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(attemptTimeout)
            .Build();

    public static ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> CreatePushPipeline(ILogger logger, TimeSpan attemptTimeout)
        => new ResiliencePipelineBuilder<(int ExitCode, string? Stdout, string? Stderr)>()
            .AddFallback(CreateTimeoutFallbackOptions(attemptTimeout))
            .AddRetry(new RetryStrategyOptions<(int ExitCode, string? Stdout, string? Stderr)>
            {
                // Retry any non-zero exit code from git push (e.g., transient network failures).
                ShouldHandle = new PredicateBuilder<(int ExitCode, string? Stdout, string? Stderr)>()
                    .HandleResult(r => r.ExitCode != 0)
                    .Handle<TimeoutRejectedException>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(200),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    var exitCode = args.Outcome.Result.ExitCode;
                    logger.LogWarning("Git push retry {Attempt} (ExitCode={ExitCode})", args.AttemptNumber, exitCode);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(attemptTimeout)
            .Build();

    public static ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> CreateLsRemotePipeline(ILogger logger, TimeSpan attemptTimeout)
        => new ResiliencePipelineBuilder<(int ExitCode, string? Stdout, string? Stderr)>()
            .AddFallback(CreateTimeoutFallbackOptions(attemptTimeout))
            .AddRetry(new RetryStrategyOptions<(int ExitCode, string? Stdout, string? Stderr)>
            {
                // Retry any non-zero exit code from git ls-remote (e.g., transient network failures).
                ShouldHandle = new PredicateBuilder<(int ExitCode, string? Stdout, string? Stderr)>()
                    .HandleResult(r => r.ExitCode != 0)
                    .Handle<TimeoutRejectedException>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(200),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    var exitCode = args.Outcome.Result.ExitCode;
                    logger.LogWarning("Git ls-remote retry {Attempt} (ExitCode={ExitCode})", args.AttemptNumber, exitCode);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(attemptTimeout)
            .Build();

    public static ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> CreateMinimalFetchPipeline(ILogger logger, TimeSpan attemptTimeout)
        => new ResiliencePipelineBuilder<(int ExitCode, string? Stdout, string? Stderr)>()
            .AddFallback(CreateTimeoutFallbackOptions(attemptTimeout))
            .AddRetry(new RetryStrategyOptions<(int ExitCode, string? Stdout, string? Stderr)>
            {
                // Retry any non-zero exit code from minimal git fetch (e.g., transient network failures).
                ShouldHandle = new PredicateBuilder<(int ExitCode, string? Stdout, string? Stderr)>()
                    .HandleResult(r => r.ExitCode != 0)
                    .Handle<TimeoutRejectedException>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(200),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    var exitCode = args.Outcome.Result.ExitCode;
                    logger.LogWarning("Git fetch (minimal) retry {Attempt} (ExitCode={ExitCode})", args.AttemptNumber, exitCode);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(attemptTimeout)
            .Build();

    /// <summary>
    /// Converts a <see cref="TimeoutRejectedException"/> that survives all retry attempts into the same
    /// synthetic failed-result shape <c>CommandLineService</c> returns on its own timeout, so
    /// <c>ExecuteAsync</c> callers always get back a result tuple - never an exception - regardless of
    /// which layer's timeout ultimately fired.
    /// </summary>
    private static FallbackStrategyOptions<(int ExitCode, string? Stdout, string? Stderr)> CreateTimeoutFallbackOptions(TimeSpan attemptTimeout)
        => new()
        {
            ShouldHandle = new PredicateBuilder<(int ExitCode, string? Stdout, string? Stderr)>().Handle<TimeoutRejectedException>(),
            FallbackAction = _ => Outcome.FromResultAsValueTask<(int ExitCode, string? Stdout, string? Stderr)>(
                (-1, null, $"Operation timed out after {attemptTimeout.TotalSeconds:0}s."))
        };

    internal static bool IsMergeConflict(string? stdout, string? stderr)
    {
        var combined = string.Concat(stdout ?? string.Empty, stderr ?? string.Empty);
        return combined.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("merge conflict", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Automatic merge failed", StringComparison.OrdinalIgnoreCase);
    }
}
