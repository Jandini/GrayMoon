using System.Diagnostics;
using System.Text.RegularExpressions;

namespace GrayMoon.App.Services.Features;

/// <summary>
/// Shared log shape for Feature operations (Create, Remove, Repair, Rollback, Reconcile) so support can
/// reconstruct what happened to a Feature from the log alone. One Information line when an operation
/// starts and one when it ends, Debug per repository that worked, Warning per repository that failed.
/// Error text is passed through <see cref="Redact"/> so a Git error that echoes a clone URL never
/// writes credentials to the log.
/// </summary>
internal static partial class FeatureOperationLog
{
    public static long FeatureOperationStarted(
        this ILogger logger, string operation, int workspaceId, int? featureId, int? contextId, string? featureName)
    {
        logger.LogInformation(
            "Feature {Operation} started. WorkspaceId={WorkspaceId} FeatureId={FeatureId} ContextId={ContextId} FeatureName={FeatureName}",
            operation, workspaceId, featureId, contextId, featureName);
        return Stopwatch.GetTimestamp();
    }

    public static void FeatureOperationFinished(
        this ILogger logger,
        string operation,
        int workspaceId,
        int? featureId,
        int? contextId,
        string? featureName,
        long startedTimestamp,
        string outcome,
        string? error = null)
    {
        var durationMs = (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
        logger.LogInformation(
            "Feature {Operation} finished. WorkspaceId={WorkspaceId} FeatureId={FeatureId} ContextId={ContextId} FeatureName={FeatureName} DurationMs={DurationMs} Outcome={Outcome} Error={Error}",
            operation, workspaceId, featureId, contextId, featureName, durationMs, outcome, Redact(error));
    }

    public static void FeatureRepositoryFailed(
        this ILogger logger, string operation, int workspaceId, string? featureName, string? repository, string? error)
    {
        logger.LogWarning(
            "Feature {Operation} failed for a repository. WorkspaceId={WorkspaceId} FeatureName={FeatureName} Repository={Repository} Error={Error}",
            operation, workspaceId, featureName, repository, Redact(error));
    }

    public static void FeatureRepositoryDone(
        this ILogger logger, string operation, int workspaceId, string? featureName, string? repository, string outcome)
    {
        logger.LogDebug(
            "Feature {Operation} repository done. WorkspaceId={WorkspaceId} FeatureName={FeatureName} Repository={Repository} Outcome={Outcome}",
            operation, workspaceId, featureName, repository, outcome);
    }

    /// <summary>Removes <c>user:password@</c> / <c>token@</c> credentials from any URL in <paramref name="text"/>.</summary>
    internal static string? Redact(string? text)
        => string.IsNullOrEmpty(text) ? text : UrlCredentials().Replace(text, "://***@");

    [GeneratedRegex(@"://[^/\s@]+@")]
    private static partial Regex UrlCredentials();
}
