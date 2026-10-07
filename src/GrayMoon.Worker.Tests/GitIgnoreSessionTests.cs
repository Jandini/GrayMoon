using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Tests;

public sealed class GitIgnoreSessionTests : IDisposable
{
    private readonly TempGitRepositoryFixture _repo = new();
    private readonly LibGit2SharpGitIgnoreService _service = new();

    public void Dispose() => _repo.Dispose();

    [Theory]
    [InlineData("")]
    [InlineData("../x")]
    [InlineData("a/../x")]
    [InlineData(".")]
    [InlineData("./obj")]
    [InlineData(".git")]
    [InlineData(".git/config")]
    [InlineData("/obj")]
    [InlineData("C:/obj")]
    [InlineData("obj\\h.csproj")]
    [InlineData("src//A")]
    public void Invalid_paths_are_rejected(string path)
    {
        _repo.CommitInitial();
        using var session = _service.Open(_repo.RepositoryPath);

        Assert.Throws<ArgumentException>(() => session.IsExcluded(path, GitPathKind.File));
    }

    [Fact]
    public void Disposed_session_throws()
    {
        _repo.CommitInitial();
        var session = _service.Open(_repo.RepositoryPath);
        session.Dispose();

        Assert.Throws<ObjectDisposedException>(() => session.IsExcluded("a.txt", GitPathKind.File));
    }

    [Fact]
    public void Open_non_repository_throws_GitIgnoreException()
    {
        using var dir = new TempDirectory();

        Assert.Throws<GitIgnoreException>(() => _service.Open(dir.Path));
    }

    [Fact]
    public void Tracked_file_matching_a_rule_is_not_excluded_even_when_deleted_from_disk()
    {
        _repo.WriteFile(".gitignore", "*.tmp\n");
        _repo.WriteFile("tracked.tmp", "x\n");
        _repo.RunGit("add", "-f", ".gitignore", "tracked.tmp");
        _repo.RunGit("commit", "-m", "init");
        _repo.DeleteFile("tracked.tmp");
        _repo.WriteFile("untracked.tmp", "x\n");

        using var session = _service.Open(_repo.RepositoryPath);

        Assert.False(session.IsExcluded("tracked.tmp", GitPathKind.File));
        Assert.True(session.IsExcluded("untracked.tmp", GitPathKind.File));
    }

    [Fact]
    public void Ignored_directory_with_tracked_content_is_not_excluded()
    {
        _repo.WriteFile(".gitignore", "bin/\n");
        _repo.WriteFile("bin/keep/Tool.csproj", "<Project />\n");
        _repo.WriteFile("bin/other/x.txt", "x\n");
        _repo.RunGit("add", "-f", ".gitignore", "bin/keep/Tool.csproj");
        _repo.RunGit("commit", "-m", "init");

        using var session = _service.Open(_repo.RepositoryPath);

        Assert.False(session.IsExcluded("bin", GitPathKind.Directory));
        Assert.False(session.IsExcluded("bin/keep", GitPathKind.Directory));
        Assert.True(session.IsExcluded("bin/other", GitPathKind.Directory));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Case_sensitivity_follows_core_ignorecase(bool ignoreCase)
    {
        _repo.RunGit("config", "core.ignorecase", ignoreCase ? "true" : "false");
        _repo.WriteFile(".gitignore", "obj/\n");
        _repo.RunGit("add", ".gitignore");
        _repo.RunGit("commit", "-m", "init");
        _repo.WriteFile("obj/a.txt", "x\n");

        using var session = _service.Open(_repo.RepositoryPath);

        Assert.True(session.IsExcluded("obj/a.txt", GitPathKind.File));
        Assert.Equal(ignoreCase, session.IsExcluded("OBJ/a.txt", GitPathKind.File));
    }

    [Fact]
    public void SelectStageable_splits_and_preserves_order()
    {
        _repo.WriteFile(".gitignore", ".work/\n*.tmp\n");
        _repo.WriteFile("tracked.tmp", "x\n");
        _repo.RunGit("add", "-f", ".gitignore", "tracked.tmp");
        _repo.RunGit("commit", "-m", "init");
        _repo.WriteFile("src/Lib/Lib.csproj", "<Project />\n");
        _repo.WriteFile(".work/Ignored.csproj", "<Project />\n");
        _repo.DeleteFile("tracked.tmp");

        using var session = _service.Open(_repo.RepositoryPath);
        var selection = session.SelectStageable(["src/Lib/Lib.csproj", ".work/Ignored.csproj", "tracked.tmp", "missing.txt", ".work"]);

        Assert.Equal(["src/Lib/Lib.csproj", "tracked.tmp", "missing.txt"], selection.Stageable);
        Assert.Equal([".work/Ignored.csproj", ".work"], selection.ExcludedUntracked);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("graymoon-nogit-").FullName;
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
