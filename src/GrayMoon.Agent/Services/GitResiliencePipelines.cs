using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace GrayMoon.Agent.Services;

internal static class GitResiliencePipelines
{
    public static ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> CreateSafeDirectoryPipeline(ILogger logger)
        => new ResiliencePipelineBuilder<(int ExitCode, string? Stdout, string? Stderr)>()
            .AddRetry(new RetryStrategyOptions<(int ExitCode, string? Stdout, string? Stderr)>
            {
                ShouldHandle = new PredicateBuilder<(int ExitCode, string? Stdout, string? Stderr)>().HandleResult(r => r.ExitCode != 0),
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
            .Build();

    public static ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> CreateClonePipeline(ILogger logger)
        => new ResiliencePipelineBuilder<(int ExitCode, string? Stdout, string? Stderr)>()
            .AddRetry(new RetryStrategyOptions<(int ExitCode, string? Stdout, string? Stderr)>
            {
                ShouldHandle = new PredicateBuilder<(int ExitCode, string? Stdout, string? Stderr)>().HandleResult(r => r.ExitCode != 0),
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
            })
            .Build();

    public static ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> CreateFetchPipeline(ILogger logger)
        => new ResiliencePipelineBuilder<(int ExitCode, string? Stdout, string? Stderr)>()
            .AddRetry(new RetryStrategyOptions<(int ExitCode, string? Stdout, string? Stderr)>
            {
                // Retry any non-zero exit code from git fetch (e.g., transient network failures).
                ShouldHandle = new PredicateBuilder<(int ExitCode, string? Stdout, string? Stderr)>().HandleResult(r => r.ExitCode != 0),
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
            .Build();

    public static ResiliencePipeline<(int ExitCode, string? Stdout, string? Stderr)> CreatePullPipeline(ILogger logger)
        => new ResiliencePipelineBuilder<(int ExitCode, string? Stdout, string? Stderr)>()
            .AddRetry(new RetryStrategyOptions<(int ExitCode, string? Stdout, string? Stderr)>
            {
                // Retry any non-zero exit code from git pull (e.g., transient network failures).
                ShouldHandle = new PredicateBuilder<(int ExitCode, string? Stdout, string? Stderr)>().HandleResult(r => r.ExitCode != 0),
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
            .Build();
}

