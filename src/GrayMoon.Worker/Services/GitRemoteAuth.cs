using System.Text;
using System.Text.RegularExpressions;

namespace GrayMoon.Worker.Services;

/// <summary>
/// The single place that knows how a connector token becomes git authentication. Applied by
/// <see cref="GitProcessRunner.RunRemoteAsync"/> for every remote operation (clone, fetch, pull, push, ls-remote).
/// The credential is the same Basic <c>x-access-token:&lt;token&gt;</c> header in both transports; only the
/// delivery differs: <c>GIT_CONFIG_*</c> environment variables (git 2.31+, keeps the secret off the command
/// line) or, for older git, <c>-c</c> arguments.
/// </summary>
internal static class GitRemoteAuth
{
    /// <summary><c>GIT_CONFIG_COUNT</c>/<c>GIT_CONFIG_KEY_n</c>/<c>GIT_CONFIG_VALUE_n</c> were added in git 2.31.</summary>
    internal static readonly Version MinimumEnvConfigVersion = new(2, 31);

    private static readonly Regex GitVersionRegex = new(@"git version (\d+)\.(\d+)", RegexOptions.Compiled);

    private static readonly Regex AuthFailureRegex = new(
        @"authentication failed|could not read (username|password)|terminal prompts disabled|invalid username or password|bad credentials|http(?:s)? (?:401|403)|error: 40[13]|requested url returned error: 40[13]|repository not found|remote: permission to .* denied|access denied",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static string BuildAuthHeaderValue(string bearerToken)
    {
        var credentials = "x-access-token:" + bearerToken;
        return "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
    }

    /// <summary>
    /// Config entries that make git authenticate with the token and never fall back to a credential helper or prompt.
    /// </summary>
    private static IReadOnlyList<(string Key, string Value)> BuildConfigEntries(string bearerToken) =>
    [
        ("core.askpass", "true"),
        ("credential.helper", ""),
        ("http.extraHeader", BuildAuthHeaderValue(bearerToken)),
    ];

    /// <summary>Environment transport (git 2.31+). Appends after any <c>GIT_CONFIG_COUNT</c> entries already in the process environment.</summary>
    internal static IReadOnlyDictionary<string, string> BuildEnvironment(string bearerToken, int existingConfigCount = 0)
    {
        var entries = BuildConfigEntries(bearerToken);
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_CONFIG_COUNT"] = (existingConfigCount + entries.Count).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        for (var i = 0; i < entries.Count; i++)
        {
            var index = (existingConfigCount + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            env["GIT_CONFIG_KEY_" + index] = entries[i].Key;
            env["GIT_CONFIG_VALUE_" + index] = entries[i].Value;
        }
        return env;
    }

    /// <summary>Command-line transport for git older than 2.31. Returns the global <c>-c</c> options to place before the subcommand.</summary>
    internal static string BuildArgumentPrefix(string bearerToken)
    {
        var escaped = BuildAuthHeaderValue(bearerToken).Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"-c core.askpass=true -c credential.helper= -c \"http.extraHeader={escaped}\"";
    }

    internal static int ReadExistingConfigCount()
        => int.TryParse(Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT"), out var n) && n > 0 ? n : 0;

    /// <summary>Parses the <c>git --version</c> output (e.g. <c>git version 2.43.0.windows.1</c>); null when unrecognised.</summary>
    internal static Version? ParseGitVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;
        var m = GitVersionRegex.Match(output);
        return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : null;
    }

    internal static bool SupportsEnvConfig(Version? gitVersion)
        => gitVersion != null && gitVersion >= MinimumEnvConfigVersion;

    /// <summary>True when git output indicates the remote rejected or lacked credentials.</summary>
    internal static bool IsAuthFailure(string? output)
        => !string.IsNullOrWhiteSpace(output) && AuthFailureRegex.IsMatch(output);

    internal const string AuthFailureHint =
        "Git authentication failed. Check the connector token: it may be missing, expired, or lack access to this repository.";

    /// <summary>
    /// Prefixes <paramref name="output"/> with <see cref="AuthFailureHint"/> when it looks like an authentication
    /// failure, so the UI shows an actionable message above the raw git output.
    /// </summary>
    internal static string? WithAuthHint(string? output)
        => IsAuthFailure(output) ? AuthFailureHint + "\n" + output : output;
}
