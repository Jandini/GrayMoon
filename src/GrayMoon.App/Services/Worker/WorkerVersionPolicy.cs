using GrayMoon.Abstractions.Worker;

namespace GrayMoon.App.Services.Worker;

/// <summary>
/// The App and the Worker are a matched pair: a Worker with a different GrayMoon version is the wrong Worker, not an
/// alternate capability set. This is the one place that defines what "the same version" means and which commands
/// still run while the versions differ.
/// </summary>
/// <remarks>
/// Comparison contract: the normalized product SemVer must be equal. Normalization trims whitespace, drops a leading
/// <c>v</c>, drops SemVer build metadata (everything after <c>+</c>) and ignores case. The pre-release label is part
/// of the product version, so two CI builds from different commits (for example <c>0.2.0-feedback.5</c> and
/// <c>0.2.0-feedback.6</c>) are different versions. GitVersion is configured with
/// <c>assembly-informational-format: '{SemVer}'</c> and both projects set
/// <c>IncludeSourceRevisionInInformationalVersion=false</c>, so today no build metadata is emitted; stripping it only
/// guards against a pipeline that starts appending a commit hash.
/// </remarks>
public static class WorkerVersionPolicy
{
    /// <summary>
    /// Commands that run while the versions differ: the Worker update itself and the read-only diagnostics the
    /// Worker page shows. The Worker update must keep working against any older Worker, so the
    /// <see cref="WorkerHubMethods.SelfUpdate"/> request shape (<c>installUrl</c>) must never change.
    /// </summary>
    private static readonly HashSet<string> CommandsAllowedOnVersionMismatch = new(StringComparer.Ordinal)
    {
        WorkerHubMethods.SelfUpdate,
        WorkerHubMethods.GetHostInfo,
    };

    public static string? Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var normalized = version.Trim();
        if (normalized.Length > 1 && (normalized[0] == 'v' || normalized[0] == 'V') && char.IsDigit(normalized[1]))
            normalized = normalized[1..];

        var plus = normalized.IndexOf('+');
        if (plus >= 0)
            normalized = normalized[..plus];

        normalized = normalized.Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    /// <summary>True when both versions are known and name the same GrayMoon product version.</summary>
    public static bool AreSameProductVersion(string? a, string? b)
    {
        var left = Normalize(a);
        var right = Normalize(b);
        return left is not null && right is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAllowedOnVersionMismatch(string command) => CommandsAllowedOnVersionMismatch.Contains(command);

    public static string MismatchMessage(string? workerVersion, string? appVersion) =>
        $"The GrayMoon Worker version ({workerVersion ?? "unknown"}) does not match this GrayMoon version ({appVersion ?? "unknown"}). Update the Worker.";
}
