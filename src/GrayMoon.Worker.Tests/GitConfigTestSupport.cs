using System.Diagnostics;
using GrayMoon.Common;

namespace GrayMoon.Worker.Tests;

/// <summary>Real-git helpers for the Git configuration tests; native git is used only as an independent oracle.</summary>
internal static class GitConfigTestSupport
{
    public static string CreateRepo(string root, string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        Git(path, "init");
        Git(path, "config user.email test@example.com");
        Git(path, "config user.name Test");
        Git(path, "checkout -B main");
        File.WriteAllText(Path.Combine(path, "README.md"), "x\n");
        Git(path, "add README.md");
        Git(path, "commit -m init");
        return path;
    }

    /// <summary>Runs git and returns stdout; <paramref name="allowFailure"/> returns an empty string on a non-zero exit.</summary>
    public static string Git(string workingDirectory, string args, bool allowFailure = false)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            if (allowFailure)
                return "";
            throw new InvalidOperationException($"git {args} failed: {stderr.Result}");
        }

        return stdout.Result;
    }

    /// <summary>What native <c>git config --get &lt;key&gt;</c> prints (trimmed), or null when it exits non-zero.</summary>
    public static string? NativeGet(string workingDirectory, string key)
    {
        var output = Git(workingDirectory, $"config --get {key}", allowFailure: true).Trim();
        return output.Length == 0 ? null : output;
    }

    public static string ForGitConfig(string path) => path.Replace('\\', '/');
}

/// <summary>Wraps a command line service and records every argument string, to prove which git processes ran.</summary>
internal sealed class CountingCommandLine(ICommandLineService inner) : ICommandLineService
{
    public List<string> Calls { get; } = [];

    public Task<CommandLineResult> RunAsync(string fileName, string arguments, string? workingDirectory = null, string? stdin = null,
        CancellationToken cancellationToken = default, bool streamStderrAsStdout = false, bool mirrorFailureOutputAsStderr = false, TimeSpan? timeout = null)
    {
        lock (Calls)
            Calls.Add(arguments);
        return inner.RunAsync(fileName, arguments, workingDirectory, stdin, cancellationToken, streamStderrAsStdout, mirrorFailureOutputAsStderr, timeout);
    }

    public Task<CommandLineResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null, byte[]? stdinBytes = null,
        CancellationToken cancellationToken = default, bool streamStderrAsStdout = false, bool mirrorFailureOutputAsStderr = false, TimeSpan? timeout = null)
    {
        lock (Calls)
            Calls.Add(string.Join(' ', arguments));
        return inner.RunAsync(fileName, arguments, workingDirectory, stdinBytes, cancellationToken, streamStderrAsStdout, mirrorFailureOutputAsStderr, timeout);
    }
}
