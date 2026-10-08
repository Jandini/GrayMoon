using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace GrayMoon.Worker.Services;

/// <summary>Where Git runs hooks for a repository, as <c>git rev-parse --git-path hooks</c> answered.</summary>
internal sealed record GitHooksLocation(string? Directory, string? OutsideGitDirHooksPath);

internal enum GitHooksLocationSource
{
    /// <summary>Answered from the cache: no process was started.</summary>
    Cache,

    /// <summary>Native git answered and the answer was cached for the next call.</summary>
    NativeCached,

    /// <summary>Native git answered; the layout is one the cache will not vouch for (see <see cref="GitHooksLocationCache"/>).</summary>
    NativeUncacheable,
}

/// <summary>
/// Remembers, per repository, the hooks location native git resolved, so an unchanged repository does not start
/// <c>git rev-parse --git-path hooks</c> on every Sync. Native git stays authoritative: the cache only replays
/// an answer git already gave, and only while a file-system fingerprint of everything that decides that answer
/// is unchanged. The fingerprint holds the git directory and common directory (so a removed and re-added
/// worktree, or a re-cloned repository, never reuses an answer), the size and write time of every config file
/// git reads for <c>core.hooksPath</c> (local, worktree, global, XDG, system) and the environment variables that
/// move or override those files.
/// <para>
/// Anything the fingerprint cannot vouch for goes to git every time: <c>GIT_DIR</c> or <c>GIT_COMMON_DIR</c> set,
/// a layout <see cref="GitDirectoryLocator.TryReadGitDirFromWorkTree"/> does not recognise, a config file that is
/// unreadable, or one containing an <c>include</c>/<c>includeIf</c> directive (the included file, or the
/// condition, could change the answer without touching a file we stamp). Failed resolutions are never cached.
/// </para>
/// </summary>
internal sealed class GitHooksLocationCache
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // Environment that redirects or overrides git's config files. Anything else starting with GIT_CONFIG is
    // included as well (GIT_CONFIG_COUNT/KEY_n/VALUE_n, GIT_CONFIG_PARAMETERS, GIT_CONFIG_GLOBAL, ...).
    private static readonly string[] FingerprintedEnvironment =
        ["HOME", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "XDG_CONFIG_HOME", "GIT_WORK_TREE", "PATH"];

    private static readonly Lazy<string[]> SystemConfigCandidates = new(FindSystemConfigCandidates);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(PathComparer);

    private sealed record Entry(string Fingerprint, GitHooksLocation Location);

    internal int Count => _entries.Count;

    /// <summary>Returns the cached location when the fingerprint still matches, otherwise asks <paramref name="resolveNatively"/>.</summary>
    public async Task<(GitHooksLocation Location, GitHooksLocationSource Source)> ResolveAsync(
        string repoPath, Func<Task<GitHooksLocation>> resolveNatively)
    {
        var key = NormalizeKey(repoPath);
        var before = TryComputeFingerprint(repoPath);
        if (before is null)
        {
            _entries.TryRemove(key, out _);
            return (await resolveNatively(), GitHooksLocationSource.NativeUncacheable);
        }

        if (_entries.TryGetValue(key, out var entry) && string.Equals(entry.Fingerprint, before, StringComparison.Ordinal))
            return (entry.Location, GitHooksLocationSource.Cache);

        var location = await resolveNatively();

        // Only keep an answer for the state it was asked in: if the repository changed while git ran, the
        // next call must ask again.
        if (location.Directory is not null && string.Equals(TryComputeFingerprint(repoPath), before, StringComparison.Ordinal))
        {
            _entries[key] = new Entry(before, location);
            return (location, GitHooksLocationSource.NativeCached);
        }

        _entries.TryRemove(key, out _);
        return (location, GitHooksLocationSource.NativeUncacheable);
    }

    private static string NormalizeKey(string repoPath)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoPath)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return repoPath; }
    }

    /// <summary>A digest of everything the answer depends on, or null when this layout must always ask git.</summary>
    private static string? TryComputeFingerprint(string repoPath)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GIT_COMMON_DIR")))
            return null;

        var gitDir = GitDirectoryLocator.TryReadGitDirFromWorkTree(repoPath);
        if (gitDir is null)
            return null;

        try
        {
            var commonDir = ReadCommonDir(gitDir);
            var sb = new StringBuilder();
            sb.Append("repo=").Append(NormalizeKey(repoPath)).Append('\n');
            sb.Append("gitdir=").Append(gitDir).Append('\n');
            sb.Append("common=").Append(commonDir).Append('\n');

            foreach (var configPath in ConfigFiles(commonDir, gitDir))
            {
                if (!TryStamp(configPath, sb))
                    return null;
            }

            foreach (var name in FingerprintedEnvironment)
                sb.Append("env:").Append(name).Append('=').Append(Environment.GetEnvironmentVariable(name)).Append('\n');
            foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
            {
                if (variable.Key is string k && k.StartsWith("GIT_CONFIG", StringComparison.OrdinalIgnoreCase))
                    sb.Append("env:").Append(k).Append('=').Append(variable.Value).Append('\n');
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The common git directory: <c>commondir</c> (relative to the git dir) for a linked worktree, else the git dir itself.</summary>
    private static string ReadCommonDir(string gitDir)
    {
        var commonDirFile = Path.Combine(gitDir, "commondir");
        if (!File.Exists(commonDirFile))
            return gitDir;

        var target = File.ReadAllText(commonDirFile).Trim();
        if (target.Length == 0)
            return gitDir;
        return Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(gitDir, target));
    }

    private static IEnumerable<string> ConfigFiles(string commonDir, string gitDir)
    {
        yield return Path.Combine(commonDir, "config");
        yield return Path.Combine(gitDir, "config.worktree");

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home))
            home = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrEmpty(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var globalOverride = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        if (!string.IsNullOrEmpty(globalOverride))
        {
            yield return globalOverride;
        }
        else
        {
            if (!string.IsNullOrEmpty(home))
                yield return Path.Combine(home, ".gitconfig");
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrEmpty(xdg))
                yield return Path.Combine(xdg, "git", "config");
            else if (!string.IsNullOrEmpty(home))
                yield return Path.Combine(home, ".config", "git", "config");
        }

        var systemOverride = Environment.GetEnvironmentVariable("GIT_CONFIG_SYSTEM");
        if (!string.IsNullOrEmpty(systemOverride))
        {
            yield return systemOverride;
        }
        else if (Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM") is null)
        {
            foreach (var candidate in SystemConfigCandidates.Value)
                yield return candidate;
        }
    }

    /// <summary>
    /// Appends the file's existence, size and write time. False when the file cannot be vouched for: it exists
    /// but cannot be read, or it holds an include directive.
    /// </summary>
    private static bool TryStamp(string path, StringBuilder sb)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            sb.Append("file:").Append(path).Append("|absent\n");
            return true;
        }

        var text = File.ReadAllText(path);
        if (ContainsIncludeDirective(text))
            return false;

        sb.Append("file:").Append(path).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks)
            .Append('|').Append(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))).Append('\n');
        return true;
    }

    private static bool ContainsIncludeDirective(string configText)
    {
        // Deliberately loose: "[include", "[includeIf" in any case, with any spacing. A false positive only
        // costs one git call per Sync for that repository.
        foreach (var rawLine in configText.Split('\n'))
        {
            var line = rawLine.TrimStart();
            if (!line.StartsWith('['))
                continue;
            var section = line[1..].TrimStart();
            if (section.StartsWith("include", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string[] FindSystemConfigCandidates()
    {
        var candidates = new List<string>();
        try
        {
            var exeName = OperatingSystem.IsWindows() ? "git.exe" : "git";
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string exe;
                try { exe = Path.Combine(dir.Trim().Trim('"'), exeName); }
                catch (ArgumentException) { continue; }
                if (!File.Exists(exe))
                    continue;

                // Git for Windows keeps it at <root>\etc\gitconfig (older: <root>\mingw64\etc\gitconfig) with git.exe
                // in <root>\cmd or <root>\mingw64\bin; Unix builds use <prefix>/etc/gitconfig.
                var binDir = Path.GetDirectoryName(Path.GetFullPath(exe));
                var root = binDir is null ? null : Path.GetDirectoryName(binDir);
                var rootParent = root is null ? null : Path.GetDirectoryName(root);
                foreach (var baseDir in new[] { root, rootParent })
                {
                    if (baseDir is null)
                        continue;
                    candidates.Add(Path.Combine(baseDir, "etc", "gitconfig"));
                    candidates.Add(Path.Combine(baseDir, "mingw64", "etc", "gitconfig"));
                }
                break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Unknown system config location: the remaining stamps still cover local, worktree and global config.
        }

        if (!OperatingSystem.IsWindows())
            candidates.Add("/etc/gitconfig");
        return candidates.Distinct(PathComparer).ToArray();
    }
}
