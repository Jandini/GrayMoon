namespace GrayMoon.App.Models;

/// <summary>
/// What the effective version of a repository in one context is (design "Effective Package Versions"). Stored as an
/// int on <see cref="WorkspaceRepositoryContextState.EffectiveVersionKind"/>; null there means never resolved.
/// </summary>
public enum EffectiveVersionKind
{
    /// <summary>The calculated GitVersion is the version dependents use.</summary>
    Own = 0,

    /// <summary>
    /// The repository is on its branch point and every required package is published at the parent branch version on
    /// its mapped connector, so dependents use that published parent version.
    /// </summary>
    InheritedVerified = 1,

    /// <summary>A registry lookup failed or was inconclusive. Bulk Update, Update and Push, Prepare Workspace and Push stop.</summary>
    Unresolved = 2,

    /// <summary>
    /// The repository is on its branch point and only version-file <c>{@Repo}</c> tokens use its version (no csproj
    /// consumer requires a package of it), so there is nothing GrayMoon can look up: dependents use the parent branch
    /// version without a registry check. Its code is the parent's, so nothing has to be built or pushed.
    /// Also a read only snapshot of a Feature (reason <see cref="EffectiveVersionReasonCodes.ReferenceSnapshot"/>): its
    /// worktree is detached, so dependents use the version of the branch it was taken from at its commit, as the
    /// Workspace does. Nothing is looked up, because a snapshot never publishes.
    /// </summary>
    InheritedUnverified = 3,
}

/// <summary>Reason codes stored in <see cref="WorkspaceRepositoryContextState.EffectiveVersionReasonCode"/>.</summary>
public static class EffectiveVersionReasonCodes
{
    public const string Verified = "Verified";
    public const string NotOnBaseline = "NotOnBaseline";
    public const string NoBaseline = "NoBaseline";
    public const string BaselineNotOnRemoteParent = "BaselineNotOnRemoteParent";
    public const string PolicyProtected = "PolicyProtected";
    public const string NoRequiredPackages = "NoRequiredPackages";
    public const string TokenConsumersOnly = "TokenConsumersOnly";
    public const string ReferenceSnapshot = "ReferenceSnapshot";
    public const string ParentVersionUnavailable = "ParentVersionUnavailable";
    public const string PackageNotPublished = "PackageNotPublished";
    public const string RegistryAuthenticationFailed = "RegistryAuthenticationFailed";
    public const string RegistryFailure = "RegistryFailure";
    public const string RegistryTimeout = "RegistryTimeout";
    public const string RegistryUnexpectedResponse = "RegistryUnexpectedResponse";

    /// <summary>Short text for a reason code, for a tooltip or a log line. Unknown codes are returned as they are.</summary>
    public static string Describe(string? reasonCode) => reasonCode switch
    {
        Verified => "every required package is published at the parent version",
        NotOnBaseline => "the branch has commits of its own",
        NoBaseline => "GrayMoon did not record where this branch started",
        BaselineNotOnRemoteParent => "the branch point is not on the remote parent branch",
        PolicyProtected => "the repository is protected (tag, read only, archived)",
        NoRequiredPackages => "no repository in this context uses its version",
        TokenConsumersOnly => "only version files use its version, so there is no package to check",
        ReferenceSnapshot => "a read only snapshot uses the version of the branch it was taken from",
        ParentVersionUnavailable => "the parent version could not be calculated",
        PackageNotPublished => "a required package is not published at the parent version",
        RegistryAuthenticationFailed => "the package registry refused the credentials",
        RegistryFailure => "the package registry failed",
        RegistryTimeout => "the package registry did not answer in time",
        RegistryUnexpectedResponse => "the package registry gave an unexpected answer",
        null or "" => "",
        _ => reasonCode,
    };
}

/// <summary>
/// The read rule for stored effective versions (invariant I10): an inheritance counts only while the stored HEAD is the
/// commit it was resolved for. Every reader goes through here.
/// </summary>
public static class EffectiveVersionRules
{
    /// <summary>True when the stored inheritance is valid for <paramref name="headCommit"/>.</summary>
    public static bool IsInherited(int? kind, string? effectiveVersion, string? effectiveVersionCommitSha, string? headCommit)
        => kind is (int)EffectiveVersionKind.InheritedVerified or (int)EffectiveVersionKind.InheritedUnverified
            && !string.IsNullOrWhiteSpace(effectiveVersion)
            && !string.IsNullOrWhiteSpace(headCommit)
            && string.Equals(effectiveVersionCommitSha?.Trim(), headCommit.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The version dependents use: the inherited version when it is valid, otherwise the calculated GitVersion.</summary>
    public static string? Expected(string? gitVersion, int? kind, string? effectiveVersion, string? effectiveVersionCommitSha, string? headCommit)
        => IsInherited(kind, effectiveVersion, effectiveVersionCommitSha, headCommit) ? effectiveVersion : gitVersion;

    /// <summary>The stored kind as an enum, or null when never resolved.</summary>
    public static EffectiveVersionKind? KindOf(int? kind)
        => kind is int k && Enum.IsDefined(typeof(EffectiveVersionKind), k) ? (EffectiveVersionKind)k : null;
}
