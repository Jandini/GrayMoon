using GrayMoon.Agent.Commands;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Services;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
namespace GrayMoon.Agent.Tests;
public sealed class UpdateFileVersionsCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-ufv-").FullName;
    private readonly UpdateFileVersionsCommand _command;
    public UpdateFileVersionsCommandTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var git = new GitService(Options.Create(new AgentOptions()), NullLogger<GitService>.Instance, runner);
        _command = new UpdateFileVersionsCommand(git);
    }
    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best-effort */ }
    }
    [Fact]
    public async Task Substitutes_gitversion_branch_and_commit_preserving_prefix_suffix()
    {
        var workspaceName = "ws";
        var repoName = "Repo";
        var repoPath = Path.Combine(_root, workspaceName, repoName);
        Directory.CreateDirectory(repoPath);
        var filePath = "versions.env";
        var fullPath = Path.Combine(repoPath, filePath);
        await File.WriteAllTextAsync(fullPath, """
            VERSION=old
            BRANCH=old-branch
            COMMIT=oldsha
            WRAP="old"
            """);
        var response = await _command.ExecuteAsync(new UpdateFileVersionsRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = workspaceName,
            RepositoryName = repoName,
            FilePath = filePath,
            VersionPattern = """
                VERSION={@Repo}
                BRANCH={@Repo:branch}
                COMMIT={@Repo:commit}
                WRAP="{@Repo}"
                """,
            TokenValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["@Repo"] = "2.8.1-alpha.5",
                ["@Repo:branch"] = "feature/search",
                ["@Repo:commit"] = "a351169a63ad799f1f24b0897a1ada4dafd07449",
            }
        });
        Assert.Equal(4, response.UpdatedCount);
        var text = await File.ReadAllTextAsync(fullPath);
        Assert.Contains("VERSION=2.8.1-alpha.5", text);
        Assert.Contains("BRANCH=feature/search", text);
        Assert.Contains("COMMIT=a351169a63ad799f1f24b0897a1ada4dafd07449", text);
        Assert.Contains("WRAP=\"2.8.1-alpha.5\"", text);
    }
    [Fact]
    public async Task Skips_unresolved_token_without_destroying_existing_value()
    {
        var workspaceName = "ws";
        var repoName = "Repo";
        var repoPath = Path.Combine(_root, workspaceName, repoName);
        Directory.CreateDirectory(repoPath);
        var filePath = "versions.env";
        var fullPath = Path.Combine(repoPath, filePath);
        await File.WriteAllTextAsync(fullPath, "COMMIT=keep-me\nVERSION=old\n");
        var response = await _command.ExecuteAsync(new UpdateFileVersionsRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = workspaceName,
            RepositoryName = repoName,
            FilePath = filePath,
            VersionPattern = """
                COMMIT={@Repo:commit}
                VERSION={@Repo}
                """,
            TokenValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["@Repo"] = "1.0.0",
            }
        });
        Assert.Equal(1, response.UpdatedCount);
        var text = await File.ReadAllTextAsync(fullPath);
        Assert.Contains("COMMIT=keep-me", text);
        Assert.Contains("VERSION=1.0.0", text);
    }
    [Fact]
    public async Task Legacy_token_without_at_still_resolves_gitversion()
    {
        var workspaceName = "ws";
        var repoName = "Repo";
        var repoPath = Path.Combine(_root, workspaceName, repoName);
        Directory.CreateDirectory(repoPath);
        var filePath = "v.txt";
        var fullPath = Path.Combine(repoPath, filePath);
        await File.WriteAllTextAsync(fullPath, "V=0\n");
        var response = await _command.ExecuteAsync(new UpdateFileVersionsRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = workspaceName,
            RepositoryName = repoName,
            FilePath = filePath,
            VersionPattern = "V={Repo}",
            TokenValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["@Repo"] = "9.9.9",
            }
        });
        Assert.Equal(1, response.UpdatedCount);
        Assert.Contains("V=9.9.9", await File.ReadAllTextAsync(fullPath));
    }
}
