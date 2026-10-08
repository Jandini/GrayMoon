using System.Security.Cryptography;
using System.Text;
using GrayMoon.Worker.Abstractions;
using LibGit2Sharp;

namespace GrayMoon.Worker.Services;

/// <summary>
/// The inputs a GitVersion run depends on, as separate digests so a cache miss can name the first one that
/// changed. Computed in process (LibGit2Sharp plus file reads): no git or GitVersion process is started.
/// </summary>
internal sealed record GitVersionInputFingerprint(
    string Head,
    string HeadIdentity,
    string Refs,
    string Config,
    string Tool,
    string Invocation)
{
    private static readonly string[] ConfigFileNames =
    [
        "GitVersion.yml", "GitVersion.yaml", ".GitVersion.yml", ".GitVersion.yaml", "gitversion.yml", ".gitversion.yml",
    ];

    /// <summary>The first input that differs from <paramref name="previous"/>, as a telemetry reason; null when equal.</summary>
    public string? FirstDifference(GitVersionInputFingerprint previous)
    {
        if (!string.Equals(Invocation, previous.Invocation, StringComparison.Ordinal)) return "INVOCATION_CHANGED";
        if (!string.Equals(Tool, previous.Tool, StringComparison.Ordinal)) return "TOOL_CHANGED";
        if (!string.Equals(Config, previous.Config, StringComparison.Ordinal)) return "CONFIG_CHANGED";
        if (!string.Equals(Head, previous.Head, StringComparison.Ordinal)) return "HEAD_CHANGED";
        if (!string.Equals(HeadIdentity, previous.HeadIdentity, StringComparison.Ordinal)) return "HEAD_IDENTITY_CHANGED";
        if (!string.Equals(Refs, previous.Refs, StringComparison.Ordinal)) return "REFS_CHANGED";
        return null;
    }

    /// <summary>
    /// Null when the inputs cannot be vouched for (unborn HEAD, unreadable repository, GitVersion tool not
    /// locatable, a config that reads working-tree state): the caller then runs GitVersion as it always did.
    /// Working-tree edits are deliberately not an input; GrayMoon consumes InformationalVersion and the branch
    /// name only, which do not move with them (see GitVersionResultCacheTests), unless the config asks for
    /// <c>UncommittedChanges</c> in its format, which is refused above.
    /// </summary>
    public static GitVersionInputFingerprint? TryCompute(string repoPath, RepositoryVersionOptions options)
    {
        try
        {
            using var repository = new Repository(repoPath);
            if (repository.Info.IsBare)
                return null;

            var tip = repository.Head.Tip;
            if (tip is null)
                return null;

            var config = TryHashConfig(repoPath);
            var tool = TryResolveTool(repoPath);
            if (config is null || tool is null)
                return null;

            var head = repository.Refs["HEAD"];
            var identity = head is SymbolicReference symbolic ? "ref:" + symbolic.TargetIdentifier : "detached";

            return new GitVersionInputFingerprint(
                tip.Sha,
                identity,
                HashRefs(repository),
                config,
                tool,
                HashInvocation(options));
        }
        catch (Exception ex) when (ex is LibGit2SharpException or DllNotFoundException or TypeInitializationException
                                       or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string HashRefs(Repository repository)
    {
        var lines = new List<string>();
        foreach (var reference in repository.Refs)
        {
            var name = reference.CanonicalName;
            if (!name.StartsWith("refs/", StringComparison.Ordinal) || name == "refs/stash")
                continue;
            lines.Add(name + "\0" + reference.TargetIdentifier);
        }

        lines.Sort(StringComparer.Ordinal);

        // A shallow boundary changes what GitVersion can count without touching any ref.
        lines.Add("shallow\0" + HashFile(Path.Combine(CommonDir(repository.Info.Path), "shallow")));
        return Sha(string.Join('\n', lines));
    }

    private static string CommonDir(string gitDir)
    {
        var commonDirFile = Path.Combine(gitDir, "commondir");
        if (!File.Exists(commonDirFile))
            return gitDir;
        var target = File.ReadAllText(commonDirFile).Trim();
        return target.Length == 0 ? gitDir : Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(gitDir, target));
    }

    private static string? TryHashConfig(string repoPath)
    {
        var sb = new StringBuilder();
        foreach (var name in ConfigFileNames)
        {
            var path = Path.Combine(repoPath, name);
            if (!File.Exists(path))
                continue;
            var text = File.ReadAllText(path);
            if (text.Contains("UncommittedChanges", StringComparison.OrdinalIgnoreCase))
                return null;
            sb.Append(name).Append('\0').Append(text).Append('\n');
        }

        return Sha(sb.ToString());
    }

    /// <summary>
    /// Identity of the GitVersion that would run: the pinned local tool (manifest content) when the repository
    /// has a tool manifest, otherwise the global <c>dotnet-gitversion</c> found on PATH together with the tool
    /// versions installed beside it. Mirrors <c>GitService.GetGitVersionInvocation</c>.
    /// </summary>
    private static string? TryResolveTool(string repoPath)
    {
        var manifests = new[]
        {
            Path.Combine(repoPath, "dotnet-tools.json"),
            Path.Combine(repoPath, ".config", "dotnet-tools.json"),
        }.Where(File.Exists).ToList();
        if (manifests.Count > 0)
            return Sha("manifest\n" + string.Join('\n', manifests.Select(m => m + "\0" + File.ReadAllText(m))));

        var candidates = OperatingSystem.IsWindows()
            ? new[] { "dotnet-gitversion.exe", "dotnet-gitversion.cmd", "dotnet-gitversion.bat" }
            : new[] { "dotnet-gitversion" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in candidates)
            {
                string exe;
                try { exe = Path.Combine(dir.Trim().Trim('"'), candidate); }
                catch (ArgumentException) { continue; }

                var info = new FileInfo(exe);
                if (!info.Exists)
                    continue;

                // The global tool shim does not change when the tool is updated; the installed versions beside it do.
                var store = Path.Combine(info.DirectoryName!, ".store", "gitversion.tool");
                var versions = Directory.Exists(store)
                    ? string.Join(',', Directory.EnumerateDirectories(store).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal))
                    : "";
                return Sha($"global\n{info.FullName}\0{info.Length}\0{info.LastWriteTimeUtc.Ticks}\0{versions}");
            }
        }

        return null;
    }

    private static string HashInvocation(RepositoryVersionOptions options)
    {
        var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Where(e => e.Key is string k && k.StartsWith("GITVERSION", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Key + "=" + e.Value)
            .OrderBy(s => s, StringComparer.Ordinal);
        return Sha($"nonormalize={options.NonNormalize}\nsha={options.CommitSha?.Trim()}\n{string.Join('\n', env)}");
    }

    private static string HashFile(string path)
        => File.Exists(path) ? Sha(File.ReadAllText(path)) : "none";

    private static string Sha(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
