using System.Diagnostics;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// A real, throwaway git repository on disk for integration tests. Uses local (not global) git
/// identity config so tests never depend on - or pollute - a developer's global git configuration.
/// </summary>
public sealed class TempGitRepositoryFixture : IDisposable
{
    public string RepositoryPath { get; }

    public TempGitRepositoryFixture()
    {
        RepositoryPath = Directory.CreateTempSubdirectory("graymoon-git-changes-test-").FullName;

        // The same five setup processes for every instance, so run them once and copy the result.
        GitTemplateCache.CopyTo("temp-git-repository", RepositoryPath, template =>
        {
            Run(template, "init", "--initial-branch=main");
            Run(template, "config", "user.name", "GrayMoon Test");
            Run(template, "config", "user.email", "graymoon-test@example.com");
            Run(template, "config", "commit.gpgsign", "false");

            // Force off regardless of the developer/CI machine's global or system git config, so line-ending
            // content assertions in these tests are deterministic on every platform.
            Run(template, "config", "core.autocrlf", "false");
        });
    }

    public string WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(RepositoryPath, relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    public void DeleteFile(string relativePath) => File.Delete(Path.Combine(RepositoryPath, relativePath));

    public string ReadFile(string relativePath) => File.ReadAllText(Path.Combine(RepositoryPath, relativePath));

    public (int ExitCode, string Stdout, string Stderr) RunGit(params string[] args) => Run(RepositoryPath, args);

    private static (int ExitCode, string Stdout, string Stderr) Run(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git process.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, stdout, stderr);
    }

    /// <summary>Commits an initial file so the repository has a HEAD/first commit.</summary>
    public void CommitInitial(string relativePath = "README.md", string content = "initial\n")
    {
        WriteFile(relativePath, content);
        RunGit("add", "--all");
        RunGit("commit", "-m", "Initial commit");
    }

    public void Dispose()
    {
        try
        {
            var directory = new DirectoryInfo(RepositoryPath);
            if (directory.Exists)
            {
                foreach (var file in directory.GetFiles("*", SearchOption.AllDirectories))
                {
                    file.Attributes = FileAttributes.Normal;
                }

                directory.Delete(true);
            }
        }
        catch
        {
            // Best-effort cleanup; a leftover temp directory is not fatal for a test run.
        }
    }
}
