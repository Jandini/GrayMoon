using GrayMoon.Agent.Commands;
using GrayMoon.Agent.Jobs.Requests;
using GrayMoon.Agent.Services;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
namespace GrayMoon.Agent.Tests;
public sealed class CheckFileVersionsCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-cfv-").FullName;
    private readonly CheckFileVersionsCommand _command;
    public CheckFileVersionsCommandTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var git = new GitService(Options.Create(new AgentOptions()), NullLogger<GitService>.Instance, runner);
        _command = new CheckFileVersionsCommand(git, NullLogger<CheckFileVersionsCommand>.Instance);
    }
    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best-effort */ }
    }
    [Fact]
    public async Task Compares_all_three_token_kinds_independently()
    {
        var workspaceName = "ws";
        var repoName = "Repo";
        var repoPath = Path.Combine(_root, workspaceName, repoName);
        Directory.CreateDirectory(repoPath);
        var filePath = "versions.env";
        await File.WriteAllTextAsync(Path.Combine(repoPath, filePath), """
            VERSION=2.8.1-alpha.5
            BRANCH=wrong
            COMMIT=a351169a63ad799f1f24b0897a1ada4dafd07449
            """);
        var response = await _command.ExecuteAsync(new CheckFileVersionsRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = workspaceName,
            Files =
            [
                new CheckFileVersionsItem
                {
                    RepositoryName = repoName,
                    FilePath = filePath,
                    Pattern = """
                        VERSION={@Repo}
                        BRANCH={@Repo:branch}
                        COMMIT={@Repo:commit}
                        """,
                    ExpectedValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["@Repo"] = "2.8.1-alpha.5",
                        ["@Repo:branch"] = "feature/search",
                        ["@Repo:commit"] = "a351169a63ad799f1f24b0897a1ada4dafd07449",
                    }
                }
            ]
        });
        var file = Assert.Single(response.Files!);
        Assert.Equal(3, file.TotalMatchedLines);
        var stale = Assert.Single(file.OutOfDateLines!);
        Assert.Equal("@Repo:branch", stale.TokenName);
        Assert.Equal("wrong", stale.CurrentValue);
        Assert.Equal("feature/search", stale.ExpectedValue);
    }
}
