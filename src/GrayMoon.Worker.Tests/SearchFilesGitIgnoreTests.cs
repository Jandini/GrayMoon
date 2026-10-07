using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Tests;

public sealed class SearchFilesGitIgnoreTests : IDisposable
{
    private readonly TempGitRepositoryFixture _repo = new();
    private readonly WorkspaceFileSearchService _service = new(new LibGit2SharpGitIgnoreService());

    public void Dispose() => _repo.Dispose();

    private async Task<List<string>> SearchAsync(string pattern = "*.txt")
    {
        var workspace = Path.GetDirectoryName(_repo.RepositoryPath)!;
        var results = await _service.SearchAsync(workspace, Path.GetFileName(_repo.RepositoryPath), pattern);
        return results.Select(r => r.FilePath).OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public async Task Excludes_git_ignored_files_and_directories_at_any_depth()
    {
        _repo.CommitInitial();
        _repo.WriteFile(".gitignore", "bin/\nsecret.txt\n");
        _repo.RunGit("add", ".gitignore");
        _repo.RunGit("commit", "-m", "rules");
        _repo.WriteFile("a.txt", "x");
        _repo.WriteFile("secret.txt", "x");
        _repo.WriteFile("src/bin/out.txt", "x");
        _repo.WriteFile("src/keep.txt", "x");

        Assert.Equal(["a.txt", "src/keep.txt"], await SearchAsync());
    }

    [Fact]
    public async Task No_longer_hides_a_non_ignored_directory_just_because_it_is_named_obj()
    {
        _repo.CommitInitial();
        _repo.WriteFile("obj/notes.txt", "x");

        Assert.Equal(["obj/notes.txt"], await SearchAsync());
    }

    [Fact]
    public async Task Includes_tracked_files_under_an_ignored_directory()
    {
        _repo.CommitInitial();
        _repo.WriteFile(".gitignore", "bin/\n");
        _repo.WriteFile("bin/tool.txt", "x");
        _repo.WriteFile("bin/other.txt", "x");
        _repo.RunGit("add", "-f", ".gitignore", "bin/tool.txt");
        _repo.RunGit("commit", "-m", "track");

        Assert.Equal(["bin/tool.txt"], await SearchAsync());
    }

    [Fact]
    public async Task Skips_nested_repositories_at_any_depth()
    {
        _repo.CommitInitial();
        _repo.WriteFile("a.txt", "x");
        _repo.WriteFile("deep/nested/n.txt", "x");
        Directory.CreateDirectory(Path.Combine(_repo.RepositoryPath, "deep", "nested", ".git"));

        Assert.Equal(["a.txt"], await SearchAsync());
    }
}
