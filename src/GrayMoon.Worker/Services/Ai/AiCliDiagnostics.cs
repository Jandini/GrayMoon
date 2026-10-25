using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GrayMoon.Common;
using GrayMoon.Common.Ai;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Services.Ai;

/// <summary>
/// How one AI CLI process finished, for correlated Worker logs. Never includes prompts, answers, diffs or full paths.
/// </summary>
internal static partial class AiCliDiagnostics
{
    internal const string ClassificationNormalSuccess = "NormalSuccess";
    internal const string ClassificationRecoveredNonZeroExit = "RecoveredNonZeroExit";
    internal const string ClassificationProviderError = "ProviderError";
    internal const string ClassificationInvalidOutput = "InvalidOutput";
    internal const string ClassificationStartFailure = "StartFailure";
    internal const string ClassificationTimeout = "Timeout";
    internal const string ClassificationCancelled = "Cancelled";
    internal const string ClassificationTruncated = "Truncated";

    internal const string StderrWindowsArgumentTreatedAsCommand = "WindowsArgumentTreatedAsCommand";
    internal const string StderrAuthentication = "Authentication";
    internal const string StderrUnsupportedOption = "UnsupportedOption";
    internal const string StderrUpdateInstallSuspected = "UpdateInstallSuspected";
    internal const string StderrOther = "Other";
    internal const string StderrEmpty = "Empty";

    [GeneratedRegex(@"'(?:-p|--[a-z0-9-]+)' is not recognized as an internal or external command", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsArgumentAsCommand();

    [GeneratedRegex(@"(?i)(not logged in|log ?in|logged out|unauthori[sz]ed|authenticat|\b401\b|invalid api key|api key|credential|session expired|token expired)", RegexOptions.CultureInvariant)]
    private static partial Regex AuthPattern();

    [GeneratedRegex(@"(?i)(unknown option|unexpected argument|unrecognized (option|argument)|invalid value for|unknown feature|unknown command)", RegexOptions.CultureInvariant)]
    private static partial Regex UnsupportedOptionPattern();

    [GeneratedRegex(@"(?i)(promote-staged|update (is )?available|install(ation)? (corrupt|incomplete|failed)|self[- ]?update|staged version)", RegexOptions.CultureInvariant)]
    private static partial Regex UpdateInstallPattern();

    internal static string ClassifyCompletion(
        BoundedProcessResult result,
        AiCliOutcome outcome,
        bool cancelled = false)
    {
        if (cancelled)
            return ClassificationCancelled;
        if (result.StartFailure is not null)
            return ClassificationStartFailure;
        if (result.TimedOut)
            return ClassificationTimeout;
        if (result.StdoutTruncated)
            return ClassificationTruncated;
        if (outcome.Error == AiGenerationErrorKind.None && outcome.Warning is { Code: AiGenerationWarningCodes.ProviderExitedAfterResult })
            return ClassificationRecoveredNonZeroExit;
        if (outcome.Error == AiGenerationErrorKind.None)
            return ClassificationNormalSuccess;
        if (outcome.Error == AiGenerationErrorKind.InvalidOutput)
            return ClassificationInvalidOutput;
        return ClassificationProviderError;
    }

    internal static string ClassifyStderr(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return StderrEmpty;
        var sample = stderr.Length > 2_048 ? stderr[..2_048] : stderr;
        if (WindowsArgumentAsCommand().IsMatch(sample))
            return StderrWindowsArgumentTreatedAsCommand;
        if (AuthPattern().IsMatch(sample))
            return StderrAuthentication;
        if (UnsupportedOptionPattern().IsMatch(sample))
            return StderrUnsupportedOption;
        if (UpdateInstallPattern().IsMatch(sample))
            return StderrUpdateInstallSuspected;
        return StderrOther;
    }

    internal static (string Name, string Kind, string? LauncherChain) DescribeExecutable(string executablePath)
    {
        var name = Path.GetFileName(executablePath);
        var extension = Path.GetExtension(executablePath);
        var kind = extension.ToLowerInvariant() switch
        {
            ".exe" => "exe",
            ".cmd" => "cmd",
            ".bat" => "bat",
            _ => string.IsNullOrEmpty(extension) ? "other" : "other",
        };
        var chain = kind is "cmd" or "bat" ? "WindowsBatchShim" : null;
        return (name, kind, chain);
    }

    /// <summary>Safe path category and a short content hash so logs can correlate installs without user profile paths.</summary>
    internal static (string PathCategory, string PathHash) PathFingerprint(string executablePath)
    {
        var category = "Other";
        try
        {
            var full = Path.GetFullPath(executablePath);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            if (!string.IsNullOrEmpty(localAppData) && full.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase))
                category = "LocalAppData";
            else if (!string.IsNullOrEmpty(programFiles) && full.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase))
                category = "ProgramFiles";
            else if (!string.IsNullOrEmpty(programFilesX86) && full.StartsWith(programFilesX86, StringComparison.OrdinalIgnoreCase))
                category = "ProgramFilesX86";
            else if (!string.IsNullOrEmpty(userProfile) && full.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
                category = "UserProfile";

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..12];
            return (category, hash);
        }
        catch
        {
            return (category, "unavailable");
        }
    }

    /// <summary>
    /// Best-effort, read-only look at Cursor's Windows launcher files next to a resolved <c>agent.cmd</c>.
    /// Never throws into the generation path; never reads file bodies into logs (hashes and timestamps only).
    /// </summary>
    internal static void TryLogCursorLauncherSnapshot(ILogger logger, string executablePath, string operationId)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                return;

            var directory = Path.GetDirectoryName(executablePath);
            if (string.IsNullOrEmpty(directory))
                return;

            var agentCmd = Path.Combine(directory, "agent.cmd");
            var ps1 = Path.Combine(directory, "cursor-agent.ps1");
            var promote = Path.Combine(directory, "versions", "promote-staged.cmd");
            var versionsDir = Path.Combine(directory, "versions");

            var agentExists = File.Exists(agentCmd);
            var ps1Exists = File.Exists(ps1);
            var promoteExists = File.Exists(promote);
            var versionsCount = -1;
            if (Directory.Exists(versionsDir))
            {
                try
                {
                    versionsCount = Directory.EnumerateFileSystemEntries(versionsDir).Take(64).Count();
                }
                catch
                {
                    versionsCount = -1;
                }
            }

            logger.LogWarning(
                "AI CLI Cursor launcher snapshot. OperationId={OperationId} AgentCmdExists={AgentCmdExists} AgentCmdModifiedUtc={AgentCmdModifiedUtc} AgentCmdHash={AgentCmdHash} Ps1Exists={Ps1Exists} Ps1ModifiedUtc={Ps1ModifiedUtc} Ps1Hash={Ps1Hash} PromoteStagedExists={PromoteStagedExists} PromoteStagedModifiedUtc={PromoteStagedModifiedUtc} PromoteStagedHash={PromoteStagedHash} VersionsEntryCount={VersionsEntryCount}",
                operationId,
                agentExists,
                SafeLastWriteUtc(agentCmd),
                SafeContentHash(agentCmd),
                ps1Exists,
                SafeLastWriteUtc(ps1),
                SafeContentHash(ps1),
                promoteExists,
                SafeLastWriteUtc(promote),
                SafeContentHash(promote),
                versionsCount);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "AI CLI Cursor launcher snapshot skipped. OperationId={OperationId}", operationId);
        }
    }

    private static string? SafeLastWriteUtc(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path).ToString("O") : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeContentHash(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var info = new FileInfo(path);
            if (info.Length is 0 or > 256_000)
                return info.Length > 256_000 ? "too-large" : "empty";
            var bytes = File.ReadAllBytes(path);
            return Convert.ToHexString(SHA256.HashData(bytes))[..16];
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Bounded, secret-scrubbed stderr sample for anomaly logs only. Prefer categories over this string.</summary>
    internal static string? RedactedStderrSample(string? stderr, int maxChars = 200)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return null;
        var line = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
        if (EvidenceRedactor.LooksSecret(line))
            return EvidenceRedactor.Placeholder;
        line = line.Replace("\r", "");
        return line.Length <= maxChars ? line : line[..maxChars] + "...";
    }
}
