using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// SearchFiles scoped to the Workspace repository (its working tree is the workspace folder itself) versus a
/// Source repository (a subfolder), including the nested-repository exclusion.
/// </summary>
public sealed class SearchFilesWorkspaceRepositoryTests : IDisposable
{
    private const string WorkspaceFolder = "ws";
    private const string WorkspaceRepositoryName = "ws-repo";
    private const string SourceRepositoryName = "src-repo";

    private readonly string _workspaceRoot = Directory.CreateTempSubdirectory("graymoon-search-files-test-").FullName;
    private readonly SearchFilesCommand _command;

    public SearchFilesWorkspaceRepositoryTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        var git = new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, reader, new LibGit2SharpGitIgnoreService());
        _command = new SearchFilesCommand(new WorkspaceFileSearchService(new LibGit2SharpGitIgnoreService()));

        // Workspace repository: the workspace folder itself.
        var workspacePath = Path.Combine(_workspaceRoot, WorkspaceFolder);
        GitInit(workspacePath);
        File.WriteAllText(Path.Combine(workspacePath, "root.txt"), "root");
        Directory.CreateDirectory(Path.Combine(workspacePath, "plain-dir"));
        File.WriteAllText(Path.Combine(workspacePath, "plain-dir", "inner.txt"), "inner");

        // Source repository nested inside the Workspace repository's working tree.
        var sourcePath = Path.Combine(workspacePath, SourceRepositoryName);
        GitInit(sourcePath);
        File.WriteAllText(Path.Combine(sourcePath, "nested.txt"), "nested");
        Directory.CreateDirectory(Path.Combine(sourcePath, "docs"));
        File.WriteAllText(Path.Combine(sourcePath, "docs", "deep.txt"), "deep");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspaceRoot, true);
        }
        catch
        {
            // Best-effort cleanup; a leftover temp directory is not fatal for a test run.
        }
    }

    [Fact]
    public async Task Search_scoped_to_workspace_repository_finds_root_files()
    {
        var response = await _command.ExecuteAsync(NewRequest(WorkspaceRepositoryName, WorkspaceRepositoryName));

        var paths = response.Files.Select(f => f.FilePath).OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.Equal(["plain-dir/inner.txt", "root.txt"], paths);
        Assert.All(response.Files, f => Assert.Equal(WorkspaceRepositoryName, f.RepositoryName));
    }

    [Fact]
    public async Task Search_scoped_to_workspace_repository_skips_nested_source_repos()
    {
        var response = await _command.ExecuteAsync(NewRequest(WorkspaceRepositoryName, WorkspaceRepositoryName));

        Assert.DoesNotContain(response.Files, f => f.FilePath.Contains("nested.txt"));
        Assert.DoesNotContain(response.Files, f => f.FilePath.Contains("deep.txt"));
    }

    [Fact]
    public async Task Search_scoped_to_source_repository_resolves_subfolder()
    {
        var response = await _command.ExecuteAsync(NewRequest(SourceRepositoryName, WorkspaceRepositoryName));

        var paths = response.Files.Select(f => f.FilePath).OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.Equal(["docs/deep.txt", "nested.txt"], paths);
        Assert.All(response.Files, f => Assert.Equal(SourceRepositoryName, f.RepositoryName));
    }

    [Fact]
    public async Task Search_without_workspace_repository_name_treats_repository_name_as_subfolder()
    {
        // An App that predates Workspace repositories never sends workspaceRepositoryName: today's behaviour.
        var response = await _command.ExecuteAsync(NewRequest(WorkspaceRepositoryName, workspaceRepositoryName: null));

        Assert.Empty(response.Files);
    }

    private static void GitInit(string path)
    {
        Directory.CreateDirectory(path);
        var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = path, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("init");
        psi.ArgumentList.Add("-q");
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private SearchFilesRequest NewRequest(string repositoryName, string? workspaceRepositoryName) => new()
    {
        WorkspaceRoot = _workspaceRoot,
        WorkspaceName = WorkspaceFolder,
        RepositoryName = repositoryName,
        SearchPattern = "*.txt",
        WorkspaceRepositoryName = workspaceRepositoryName
    };
}
