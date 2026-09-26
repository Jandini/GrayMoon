namespace GrayMoon.App.Services.Agent;

/// <summary>
/// Allowlisted Host prerequisite identifiers shared with GrayMoon.Desktop's InstallHostPrerequisites contract.
/// Duplicated intentionally - no production project reference between the repositories.
/// </summary>
public static class HostPrerequisiteIds
{
    public const string DotnetSdk = "dotnetSdk";
    public const string Git = "git";
    public const string GitVersion = "gitVersion";
}

/// <summary>User-visible install commands shown in the Host tab (and copied to the clipboard).</summary>
public static class HostPrerequisiteInstallCommands
{
    public const string DotnetSdk = "winget install Microsoft.DotNet.SDK.10 --source winget";
    public const string Git = "winget install -e --id Git.Git --source winget";
    public const string GitVersion = "dotnet tool install --global GitVersion.Tool --version 5.*";
}

/// <summary>Detected Host prerequisite versions from GetHostInfo.</summary>
public sealed record HostPrerequisiteVersions(
    string? DotnetSdkVersion,
    string? GitVersion,
    string? GitVersionToolVersion);

/// <summary>Pure helpers for Host tab missing-state, notes, and Install All payload.</summary>
public static class HostPrerequisiteState
{
    public static bool IsMissing(string? version) => string.IsNullOrWhiteSpace(version);

    public static bool AnyMissing(HostPrerequisiteVersions versions) =>
        IsMissing(versions.DotnetSdkVersion)
        || IsMissing(versions.GitVersion)
        || IsMissing(versions.GitVersionToolVersion);

    public static IReadOnlyList<string> GetMissingIds(HostPrerequisiteVersions versions)
    {
        var ids = new List<string>(3);
        if (IsMissing(versions.DotnetSdkVersion))
            ids.Add(HostPrerequisiteIds.DotnetSdk);
        if (IsMissing(versions.GitVersion))
            ids.Add(HostPrerequisiteIds.Git);
        if (IsMissing(versions.GitVersionToolVersion))
            ids.Add(HostPrerequisiteIds.GitVersion);
        return ids;
    }

    public static string Note(HostPrerequisiteVersions versions) =>
        AnyMissing(versions)
            ? "Install the missing prerequisites above."
            : "All prerequisites are installed.";

    public static string CommandFor(string id) => id switch
    {
        HostPrerequisiteIds.DotnetSdk => HostPrerequisiteInstallCommands.DotnetSdk,
        HostPrerequisiteIds.Git => HostPrerequisiteInstallCommands.Git,
        HostPrerequisiteIds.GitVersion => HostPrerequisiteInstallCommands.GitVersion,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown Host prerequisite id.")
    };

    public static string DisplayName(string id) => id switch
    {
        HostPrerequisiteIds.DotnetSdk => ".NET SDK",
        HostPrerequisiteIds.Git => "Git",
        HostPrerequisiteIds.GitVersion => "GitVersion",
        _ => id
    };
}
