using GrayMoon.Agent.Services;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Agent.Tests;

public sealed class CsProjFileServiceIgnoredDirTests : IDisposable
{
    private readonly TempGitRepositoryFixture _repo = new();
    private readonly CsProjFileService _service;

    public CsProjFileServiceIgnoredDirTests()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        _service = new CsProjFileService(new CsProjFileParser(), runner, NullLogger<CsProjFileService>.Instance);
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task FindAsync_does_not_return_csproj_under_gitignored_work_dir()
    {
        _repo.CommitInitial();
        _repo.WriteFile(".gitignore", ".work\n");
        _repo.RunGit("add", ".gitignore");
        _repo.RunGit("commit", "-m", "ignore .work");
        _repo.WriteFile("src/Lib/Lib.csproj", MinimalCsproj("Lib"));
        _repo.WriteFile(".work/Ignored.csproj", MinimalCsproj("Ignored"));

        var found = await _service.FindAsync(_repo.RepositoryPath, CancellationToken.None);

        var paths = found.Select(p => (p.ProjectPath ?? "").Replace('\\', '/')).ToList();
        Assert.Contains("src/Lib/Lib.csproj", paths);
        Assert.DoesNotContain(paths, p => p.Contains(".work", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FindAsync_still_returns_csproj_under_non_ignored_dirs()
    {
        _repo.CommitInitial();
        _repo.WriteFile("src/Lib/Lib.csproj", MinimalCsproj("Lib"));

        var found = await _service.FindAsync(_repo.RepositoryPath, CancellationToken.None);

        var path = Assert.Single(found).ProjectPath?.Replace('\\', '/');
        Assert.Equal("src/Lib/Lib.csproj", path);
    }

    private static string MinimalCsproj(string name) =>
        $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <RootNamespace>{name}</RootNamespace>
          </PropertyGroup>
        </Project>
        """;
}
