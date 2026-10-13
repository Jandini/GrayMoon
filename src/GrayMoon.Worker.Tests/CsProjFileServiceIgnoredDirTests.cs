using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrayMoon.Worker.Tests;

public sealed class CsProjFileServiceIgnoredDirTests : IDisposable
{
    private readonly TempGitRepositoryFixture _repo = new();
    private readonly CsProjFileService _service = new(new CsProjFileParser(), new LibGit2SharpGitIgnoreService(), NullLogger<CsProjFileService>.Instance);

    public void Dispose() => _repo.Dispose();

    private void CommitGitIgnore(string rules)
    {
        _repo.CommitInitial();
        _repo.WriteFile(".gitignore", rules);
        _repo.RunGit("add", ".gitignore");
        _repo.RunGit("commit", "-m", "ignore rules");
    }

    private async Task<List<string>> FindPathsAsync()
    {
        var found = await _service.FindAsync(_repo.RepositoryPath, CancellationToken.None);
        return found.Select(p => (p.ProjectPath ?? "").Replace('\\', '/')).ToList();
    }

    [Fact]
    public async Task FindAsync_does_not_return_csproj_under_gitignored_work_dir()
    {
        CommitGitIgnore(".work\n");
        _repo.WriteFile("src/Lib/Lib.csproj", MinimalCsproj("Lib"));
        _repo.WriteFile(".work/Ignored.csproj", MinimalCsproj("Ignored"));

        var paths = await FindPathsAsync();

        Assert.Contains("src/Lib/Lib.csproj", paths);
        Assert.DoesNotContain(paths, p => p.Contains(".work", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FindAsync_still_returns_csproj_under_non_ignored_dirs()
    {
        _repo.CommitInitial();
        _repo.WriteFile("src/Lib/Lib.csproj", MinimalCsproj("Lib"));

        var path = Assert.Single(await FindPathsAsync());

        Assert.Equal("src/Lib/Lib.csproj", path);
    }

    [Fact]
    public async Task FindAsync_excludes_nested_ignored_directory_below_a_non_ignored_top_level_dir()
    {
        CommitGitIgnore("src/Generated/\n");
        _repo.WriteFile("root.csproj", MinimalCsproj("Root"));
        _repo.WriteFile("src/A/A.csproj", MinimalCsproj("A"));
        _repo.WriteFile("src/Generated/B.csproj", MinimalCsproj("B"));

        var paths = await FindPathsAsync();

        Assert.Equal(["root.csproj", "src/A/A.csproj"], paths);
    }

    [Fact]
    public async Task FindAsync_excludes_individually_ignored_csproj_files()
    {
        CommitGitIgnore("*.Generated.csproj\n");
        _repo.WriteFile("src/A/A.csproj", MinimalCsproj("A"));
        _repo.WriteFile("src/A/A.Generated.csproj", MinimalCsproj("AGen"));

        Assert.Equal(["src/A/A.csproj"], await FindPathsAsync());
    }

    [Fact]
    public async Task FindAsync_honors_negation_reinclusion()
    {
        CommitGitIgnore("generated/*\n!generated/keep/\n");
        _repo.WriteFile("generated/keep/D.csproj", MinimalCsproj("D"));
        _repo.WriteFile("generated/other/C.csproj", MinimalCsproj("C"));

        Assert.Equal(["generated/keep/D.csproj"], await FindPathsAsync());
    }

    [Fact]
    public async Task FindAsync_returns_tracked_csproj_inside_ignore_matching_directory()
    {
        CommitGitIgnore("bin/\n");
        _repo.WriteFile("bin/Tool/Tool.csproj", MinimalCsproj("Tool"));
        _repo.WriteFile("bin/Other/Other.csproj", MinimalCsproj("Other"));
        _repo.RunGit("add", "-f", "bin/Tool/Tool.csproj");
        _repo.RunGit("commit", "-m", "track tool");

        Assert.Equal(["bin/Tool/Tool.csproj"], await FindPathsAsync());
    }

    [Fact]
    public async Task FindAsync_does_not_descend_into_nested_repositories()
    {
        _repo.CommitInitial();
        _repo.WriteFile("src/A/A.csproj", MinimalCsproj("A"));
        _repo.WriteFile("sibling/Sib.csproj", MinimalCsproj("Sib"));
        Directory.CreateDirectory(Path.Combine(_repo.RepositoryPath, "sibling", ".git"));

        Assert.Equal(["src/A/A.csproj"], await FindPathsAsync());
    }

    [Fact]
    public async Task FindAsync_skips_top_level_sibling_repositories_listed_in_the_workspace_definition()
    {
        _repo.CommitInitial();
        _repo.WriteFile("graymoon.json", """{ "repositories": [ { "name": "Repo.A" }, { "name": "repo.b" } ] }""");
        _repo.WriteFile("tools/Ws/Ws.csproj", MinimalCsproj("Ws"));
        // Neither sibling is cloned or Git-ignored here: only the definition keeps them out.
        _repo.WriteFile("Repo.A/src/A.csproj", MinimalCsproj("A"));
        _repo.WriteFile("Repo.B/src/B.csproj", MinimalCsproj("B"));
        // A listed name deeper in the tree is not a sibling repository.
        _repo.WriteFile("tools/Repo.A/Nested.csproj", MinimalCsproj("Nested"));

        Assert.Equal(["tools/Repo.A/Nested.csproj", "tools/Ws/Ws.csproj"], await FindPathsAsync());
    }

    [Fact]
    public async Task FindAsync_keeps_the_nested_repository_guard_for_unlisted_folders_of_a_workspace_repository()
    {
        _repo.CommitInitial();
        _repo.WriteFile("graymoon.json", """{ "repositories": [ { "name": "listed" } ] }""");
        _repo.WriteFile("Root.csproj", MinimalCsproj("Root"));
        _repo.WriteFile("unlisted/U.csproj", MinimalCsproj("U"));
        Directory.CreateDirectory(Path.Combine(_repo.RepositoryPath, "unlisted", ".git"));

        Assert.Equal(["Root.csproj"], await FindPathsAsync());
    }

    [Fact]
    public async Task FindAsync_with_an_unreadable_workspace_definition_falls_back_to_the_other_guards()
    {
        CommitGitIgnore("/ignored-sibling/\n");
        _repo.WriteFile("graymoon.json", "{ not json");
        _repo.WriteFile("Root.csproj", MinimalCsproj("Root"));
        _repo.WriteFile("ignored-sibling/I.csproj", MinimalCsproj("I"));
        _repo.WriteFile("nested-sibling/N.csproj", MinimalCsproj("N"));
        Directory.CreateDirectory(Path.Combine(_repo.RepositoryPath, "nested-sibling", ".git"));

        Assert.Equal(["Root.csproj"], await FindPathsAsync());
    }

    [Fact]
    public async Task GetProjectPathsAsync_throws_instead_of_returning_empty_when_not_a_repository()
    {
        using var plain = new TempFolder();
        File.WriteAllText(Path.Combine(plain.Path, "X.csproj"), MinimalCsproj("X"));

        await Assert.ThrowsAsync<ProjectDiscoveryException>(() => _service.GetProjectPathsAsync(plain.Path));
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("graymoon-plain-").FullName;
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
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
