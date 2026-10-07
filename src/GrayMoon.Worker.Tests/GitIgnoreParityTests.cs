using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Services;
using LibGit2Sharp;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Oracle tests: <c>git check-ignore</c> is run here only, never in production code. Compared under Git's
/// definition (tracked paths are never ignored), which is what <see cref="IGitIgnoreSession.IsExcluded"/> implements.
/// </summary>
public sealed class GitIgnoreParityTests : IDisposable
{
    private readonly TempGitRepositoryFixture _repo = new();
    private readonly LibGit2SharpGitIgnoreService _service = new();
    private readonly List<string> _extraDirs = [];

    public void Dispose()
    {
        _repo.Dispose();
        foreach (var dir in _extraDirs)
        {
            try { ForceDelete(dir); } catch { }
        }
    }

    private static void ForceDelete(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, true);
    }

    private bool Oracle(TempGitRepositoryFixture repo, string path) => repo.RunGit("check-ignore", "-q", "--", path).ExitCode == 0;

    private void AssertParity(string repositoryPath, TempGitRepositoryFixture oracleRepo, params string[] paths)
    {
        using var session = _service.Open(repositoryPath);
        foreach (var path in paths)
        {
            var kind = Directory.Exists(Path.Combine(repositoryPath, path)) ? GitPathKind.Directory : GitPathKind.File;
            var expected = Oracle(oracleRepo, path);
            Assert.True(expected == session.IsExcluded(path, kind), $"Parity mismatch for '{path}' ({kind}): git={expected}");
        }
    }

    private void SeedRulesAndFiles()
    {
        _repo.WriteFile(".gitignore", "bin/\nobj/\n*.tmp\n/rootonly/\n**/gen/\ngenerated/*\n!generated/keep/\n*.Generated.csproj\n");
        _repo.WriteFile("src/.gitignore", "local/\n");
        _repo.WriteFile(".git/info/exclude", "excluded/\n");
        foreach (var f in new[]
        {
            "src/local/a.csproj", "src/gen/b.csproj", "generated/other/c.csproj", "generated/keep/d.csproj", "generated/x.csproj",
            "rootonly/e.csproj", "src/rootonly/f.csproj", "excluded/g.csproj", "src/A/A.Generated.csproj", "src/A/A.csproj",
            "obj/h.csproj", "src/A/obj/i.csproj", "Sp ace/ü.csproj", "x.tmp", "bin/z.csproj",
        })
        {
            _repo.WriteFile(f, "x\n");
        }
    }

    private static readonly string[] SeedPaths =
    [
        "bin", "bin/z.csproj", "obj", "obj/h.csproj", "src/A/obj", "src/A/obj/i.csproj", "src/local", "src/local/a.csproj",
        "src/gen", "src/gen/b.csproj", "generated", "generated/other", "generated/other/c.csproj", "generated/keep",
        "generated/keep/d.csproj", "generated/x.csproj", "rootonly", "rootonly/e.csproj", "src/rootonly", "src/rootonly/f.csproj",
        "excluded", "excluded/g.csproj", "src/A/A.Generated.csproj", "src/A/A.csproj", "x.tmp", "Sp ace/ü.csproj",
        "nonexistent.tmp", "nonexistent/obj",
    ];

    [Fact]
    public void Rules_match_git_for_files_and_directories()
    {
        SeedRulesAndFiles();

        AssertParity(_repo.RepositoryPath, _repo, SeedPaths);
    }

    [Fact]
    public void Negation_reincludes_only_the_negated_directory()
    {
        SeedRulesAndFiles();
        using var session = _service.Open(_repo.RepositoryPath);

        Assert.False(session.IsExcluded("generated", GitPathKind.Directory));
        Assert.False(session.IsExcluded("generated/keep", GitPathKind.Directory));
        Assert.True(session.IsExcluded("generated/other", GitPathKind.Directory));
    }

    [Fact]
    public void Core_excludesfile_matches_git()
    {
        var excludes = Path.Combine(_repo.RepositoryPath, ".git", "global-excludes");
        File.WriteAllText(excludes, "*.globalignored\n");
        _repo.RunGit("config", "core.excludesFile", excludes);
        _repo.WriteFile("a.globalignored", "x\n");
        _repo.WriteFile("b.txt", "x\n");

        AssertParity(_repo.RepositoryPath, _repo, "a.globalignored", "b.txt");
    }

    [Fact]
    public void Tracked_paths_match_git_and_raw_libgit2_diverges()
    {
        _repo.WriteFile(".gitignore", "*.tmp\n");
        _repo.WriteFile("tracked.tmp", "x\n");
        _repo.RunGit("add", "-f", ".gitignore", "tracked.tmp");
        _repo.RunGit("commit", "-m", "init");

        AssertParity(_repo.RepositoryPath, _repo, "tracked.tmp");

        // Documents why the index is consulted: libgit2 alone reports the tracked file as ignored.
        using var raw = new Repository(_repo.RepositoryPath);
        Assert.True(raw.Ignore.IsPathIgnored("tracked.tmp"));
        Assert.False(Oracle(_repo, "tracked.tmp"));
    }

    [Fact]
    public void Linked_worktree_uses_common_info_exclude_and_gitignore()
    {
        _repo.CommitInitial();
        _repo.WriteFile(".gitignore", "obj/\n");
        _repo.RunGit("add", ".gitignore");
        _repo.RunGit("commit", "-m", "ignore");
        _repo.WriteFile(".git/info/exclude", "excluded/\n");

        var wt = Path.Combine(Path.GetTempPath(), "graymoon-wt-" + Guid.NewGuid().ToString("N")[..8]);
        _extraDirs.Add(wt);
        Assert.Equal(0, _repo.RunGit("worktree", "add", "-b", "wt", wt).ExitCode);
        File.WriteAllText(Path.Combine(wt, "obj.txt"), "x");
        Directory.CreateDirectory(Path.Combine(wt, "obj"));
        File.WriteAllText(Path.Combine(wt, "obj", "q.csproj"), "x");
        Directory.CreateDirectory(Path.Combine(wt, "excluded"));
        File.WriteAllText(Path.Combine(wt, "excluded", "z.csproj"), "x");
        Assert.True(File.Exists(Path.Combine(wt, ".git")));

        var wtOracle = new WorktreeOracle(wt);
        using var session = _service.Open(wt);
        foreach (var (path, kind) in new[] { ("obj", GitPathKind.Directory), ("obj/q.csproj", GitPathKind.File), ("excluded", GitPathKind.Directory), ("excluded/z.csproj", GitPathKind.File), ("obj.txt", GitPathKind.File) })
        {
            var expected = wtOracle.Ignored(path);
            Assert.True(expected == session.IsExcluded(path, kind), $"Worktree mismatch for '{path}': git={expected}");
        }
    }

    private sealed class WorktreeOracle(string path)
    {
        public bool Ignored(string p)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = path, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "check-ignore", "-q", "--", p })
                psi.ArgumentList.Add(a);
            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
    }
}
