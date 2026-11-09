namespace GrayMoon.Worker.Tests;

/// <summary>
/// The scenarios are shared between tests by copying, so a copy must never reach back into the template: a test that
/// pushes to its remote would otherwise change what every later test starts from.
/// </summary>
public sealed class GitTemplateCacheTests : IDisposable
{
    private readonly string _first = Directory.CreateTempSubdirectory("graymoon-template-test-").FullName;
    private readonly string _second = Directory.CreateTempSubdirectory("graymoon-template-test-").FullName;

    public void Dispose()
    {
        foreach (var root in new[] { _first, _second })
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of a temp directory.
            }
        }
    }

    [Fact]
    public void A_seeded_remote_copy_points_its_seed_at_its_own_remote()
    {
        var origin = GitScenarios.SeededRemote(_first);

        var url = GitConfigTestSupport.Git(Path.Combine(_first, "seed"), "remote get-url origin").Trim();

        Assert.Equal(Path.GetFullPath(origin), Path.GetFullPath(url));
    }

    [Fact]
    public void A_push_in_one_copy_is_not_seen_by_another_copy_of_the_same_scenario()
    {
        var firstOrigin = GitScenarios.SeededRemote(_first);
        var secondOrigin = GitScenarios.SeededRemote(_second);
        var seed = Path.Combine(_first, "seed");
        File.WriteAllText(Path.Combine(seed, "extra.txt"), "x\n");
        GitConfigTestSupport.Git(seed, "add -A");
        GitConfigTestSupport.Git(seed, "commit -q -m extra");
        GitConfigTestSupport.Git(seed, "push origin main");

        var firstCount = GitConfigTestSupport.Git(firstOrigin, "rev-list --count main").Trim();
        var secondCount = GitConfigTestSupport.Git(secondOrigin, "rev-list --count main").Trim();

        Assert.Equal("2", firstCount);
        Assert.Equal("1", secondCount);
    }

    [Fact]
    public void A_cloned_repository_copy_has_its_own_origin()
    {
        var repo = GitScenarios.ClonedRepository(_first, push: true);

        var url = GitConfigTestSupport.Git(repo, "remote get-url origin").Trim();

        Assert.Equal(Path.GetFullPath(Path.Combine(_first, "origin.git")), Path.GetFullPath(url));
    }
}
