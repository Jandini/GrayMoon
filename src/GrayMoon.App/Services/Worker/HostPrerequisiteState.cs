namespace GrayMoon.App.Services.Worker;

/// <summary>
/// Allowlisted Host prerequisite identifiers shared with GrayMoon.Desktop's InstallHostPrerequisites contract.
/// Duplicated intentionally - no production project reference between the repositories.
/// </summary>
public static class HostPrerequisiteIds
{
    public const string DotnetSdk = "dotnetSdk";
    public const string Git = "git";
    public const string GitVersion = "gitVersion";
    public const string CursorAgent = "cursorAgent";
}

/// <summary>User-visible install commands shown in the Host tab (and copied to the clipboard).</summary>
public static class HostPrerequisiteInstallCommands
{
    public const string DotnetSdk = "winget install Microsoft.DotNet.SDK.10 --source winget";
    public const string Git = "winget install -e --id Git.Git --source winget";
    public const string GitVersion = "dotnet tool install --global GitVersion.Tool --version 5.*";
    public const string CursorAgent = "irm 'https://cursor.com/install?win32=true' | iex";
}

/// <summary>Detected Host prerequisite versions from GetHostInfo.</summary>
public sealed record HostPrerequisiteVersions(
    string? DotnetSdkVersion,
    string? GitVersion,
    string? GitVersionToolVersion,
    string? CursorAgentVersion = null);

/// <summary>
/// Pure helpers for Host tab missing-state, notes, and Install All payload. <see cref="AnyMissing"/> and
/// <see cref="GetMissingIds"/> describe what was probed; attention decisions use
/// <see cref="AnyRequiredMissing"/> against the workspaces' <see cref="HostPrerequisiteRequirements"/>.
/// </summary>
public static class HostPrerequisiteState
{
    public static bool IsMissing(string? version) => string.IsNullOrWhiteSpace(version);

    public static bool AnyMissing(HostPrerequisiteVersions versions) =>
        IsMissing(versions.DotnetSdkVersion)
        || IsMissing(versions.GitVersion)
        || IsMissing(versions.GitVersionToolVersion)
        || IsMissing(versions.CursorAgentVersion);

    public static IReadOnlyList<string> GetMissingIds(HostPrerequisiteVersions versions)
    {
        var ids = new List<string>(4);
        if (IsMissing(versions.DotnetSdkVersion))
            ids.Add(HostPrerequisiteIds.DotnetSdk);
        if (IsMissing(versions.GitVersion))
            ids.Add(HostPrerequisiteIds.Git);
        if (IsMissing(versions.GitVersionToolVersion))
            ids.Add(HostPrerequisiteIds.GitVersion);
        if (IsMissing(versions.CursorAgentVersion))
            ids.Add(HostPrerequisiteIds.CursorAgent);
        return ids;
    }

    /// <summary>
    /// True only when a prerequisite some workspace actually needs is missing. This, not
    /// <see cref="AnyMissing"/>, decides whether the host needs attention.
    /// </summary>
    public static bool AnyRequiredMissing(HostPrerequisiteVersions versions, HostPrerequisiteRequirements requirements) =>
        GetMissingIds(versions).Any(requirements.IsRequired);

    /// <summary>Missing prerequisites that no workspace needs: shown and installable, but informational.</summary>
    public static IReadOnlyList<string> GetMissingOptionalIds(
        HostPrerequisiteVersions versions,
        HostPrerequisiteRequirements requirements) =>
        GetMissingIds(versions).Where(id => !requirements.IsRequired(id)).ToList();

    public static string Note(HostPrerequisiteVersions versions) => Note(versions, HostPrerequisiteRequirements.All);

    public static string Note(HostPrerequisiteVersions versions, HostPrerequisiteRequirements requirements)
    {
        if (AnyRequiredMissing(versions, requirements))
            return "Install the missing prerequisites above.";

        var optional = GetMissingOptionalIds(versions, requirements);
        if (optional.Count == 0)
            return "All prerequisites are installed.";

        var sentences = new List<string> { "All required prerequisites are installed." };
        var unusedTools = optional.Where(id => id != HostPrerequisiteIds.CursorAgent).ToList();
        if (unusedTools.Count > 0)
        {
            var names = string.Join(" and ", unusedTools.Select(DisplayName));
            var verb = unusedTools.Count == 1 ? "is" : "are";
            sentences.Add($"{names} {verb} optional: no workspace uses {(unusedTools.Count == 1 ? "it" : "them")}.");
        }

        if (optional.Contains(HostPrerequisiteIds.CursorAgent))
            sentences.Add($"{DisplayName(HostPrerequisiteIds.CursorAgent)} is optional: it is only needed to write AI text with Cursor.");

        return string.Join(' ', sentences);
    }

    public static string CommandFor(string id) => id switch
    {
        HostPrerequisiteIds.DotnetSdk => HostPrerequisiteInstallCommands.DotnetSdk,
        HostPrerequisiteIds.Git => HostPrerequisiteInstallCommands.Git,
        HostPrerequisiteIds.GitVersion => HostPrerequisiteInstallCommands.GitVersion,
        HostPrerequisiteIds.CursorAgent => HostPrerequisiteInstallCommands.CursorAgent,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown Host prerequisite id.")
    };

    public static string DisplayName(string id) => id switch
    {
        HostPrerequisiteIds.DotnetSdk => ".NET SDK",
        HostPrerequisiteIds.Git => "Git",
        HostPrerequisiteIds.GitVersion => "GitVersion",
        HostPrerequisiteIds.CursorAgent => "Cursor Agent",
        _ => id
    };
}
