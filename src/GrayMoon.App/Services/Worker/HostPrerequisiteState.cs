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
    public const string Claude = "claudeCli";
    public const string Codex = "codexCli";
    public const string CursorAgent = "cursorAgent";
}

/// <summary>User-visible install commands shown in the Host tab (and copied to the clipboard).</summary>
public static class HostPrerequisiteInstallCommands
{
    public const string DotnetSdk = "winget install Microsoft.DotNet.SDK.10 --source winget";
    public const string Git = "winget install -e --id Git.Git --source winget";
    public const string GitVersion = "dotnet tool install --global GitVersion.Tool --version 5.*";
    public const string Claude = "irm 'https://claude.ai/install.ps1' | iex";
    public const string Codex = "irm 'https://chatgpt.com/codex/install.ps1' | iex";
    public const string CursorAgent = "irm 'https://cursor.com/install?win32=true' | iex";
}

/// <summary>Detected Host prerequisite versions from GetHostInfo.</summary>
public sealed record HostPrerequisiteVersions(
    string? DotnetSdkVersion,
    string? GitVersion,
    string? GitVersionToolVersion,
    string? CursorAgentVersion = null,
    string? ClaudeVersion = null,
    string? CodexVersion = null);

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
        || IsMissing(versions.ClaudeVersion)
        || IsMissing(versions.CodexVersion)
        || IsMissing(versions.CursorAgentVersion);

    public static IReadOnlyList<string> GetMissingIds(HostPrerequisiteVersions versions)
    {
        var ids = new List<string>(6);
        if (IsMissing(versions.DotnetSdkVersion))
            ids.Add(HostPrerequisiteIds.DotnetSdk);
        if (IsMissing(versions.GitVersion))
            ids.Add(HostPrerequisiteIds.Git);
        if (IsMissing(versions.GitVersionToolVersion))
            ids.Add(HostPrerequisiteIds.GitVersion);
        if (IsMissing(versions.ClaudeVersion))
            ids.Add(HostPrerequisiteIds.Claude);
        if (IsMissing(versions.CodexVersion))
            ids.Add(HostPrerequisiteIds.Codex);
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
        var unusedTools = optional.Where(id => !IsAiCli(id)).ToList();
        if (unusedTools.Count > 0)
        {
            var names = string.Join(" and ", unusedTools.Select(DisplayName));
            var verb = unusedTools.Count == 1 ? "is" : "are";
            sentences.Add($"{names} {verb} optional: no workspace uses {(unusedTools.Count == 1 ? "it" : "them")}.");
        }

        foreach (var id in optional.Where(IsAiCli))
            sentences.Add(AiCliNote(id));

        return string.Join(' ', sentences);
    }

    /// <summary>Claude, Codex and Cursor Agent are for writing AI text. They are never required by a Workspace profile.</summary>
    private static bool IsAiCli(string id) =>
        id is HostPrerequisiteIds.Claude or HostPrerequisiteIds.Codex or HostPrerequisiteIds.CursorAgent;

    private static string AiCliNote(string id) => id switch
    {
        HostPrerequisiteIds.Claude => "Claude is optional: it is only needed to write AI text with Claude.",
        HostPrerequisiteIds.Codex => "Codex is optional: it is only needed to write AI text with Codex.",
        HostPrerequisiteIds.CursorAgent => "Cursor Agent is optional: it is only needed to write AI text with Cursor.",
        _ => $"{DisplayName(id)} is optional: it is only needed to write AI text.",
    };

    public static string CommandFor(string id) => id switch
    {
        HostPrerequisiteIds.DotnetSdk => HostPrerequisiteInstallCommands.DotnetSdk,
        HostPrerequisiteIds.Git => HostPrerequisiteInstallCommands.Git,
        HostPrerequisiteIds.GitVersion => HostPrerequisiteInstallCommands.GitVersion,
        HostPrerequisiteIds.Claude => HostPrerequisiteInstallCommands.Claude,
        HostPrerequisiteIds.Codex => HostPrerequisiteInstallCommands.Codex,
        HostPrerequisiteIds.CursorAgent => HostPrerequisiteInstallCommands.CursorAgent,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown Host prerequisite id.")
    };

    public static string DisplayName(string id) => id switch
    {
        HostPrerequisiteIds.DotnetSdk => ".NET SDK",
        HostPrerequisiteIds.Git => "Git",
        HostPrerequisiteIds.GitVersion => "GitVersion",
        HostPrerequisiteIds.Claude => "Claude",
        HostPrerequisiteIds.Codex => "Codex",
        HostPrerequisiteIds.CursorAgent => "Cursor Agent",
        _ => id
    };
}
