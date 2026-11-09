namespace GrayMoon.Worker.Tests;

/// <summary>What the first commit of a scenario looks like. Two scenarios with the same spec are the same repository.</summary>
internal sealed record CommitSpec(
    string File = "README.md",
    string Content = "x\n",
    string Message = "init",
    string UserName = "Test",
    string UserEmail = "test@example.com");

/// <summary>
/// The git repositories the Worker tests keep building by hand, built once per test run and copied per test through
/// <see cref="GitTemplateCache"/>. New tests should start from one of these instead of running <c>git init</c>,
/// <c>config</c> and <c>commit</c> themselves: it keeps a test's setup to a file copy, so the suite stays fast as it
/// grows. Add a scenario here when a setup shape appears in a second test class.
/// </summary>
internal static class GitScenarios
{
    /// <summary>A repository on <c>main</c> with one commit and a configured identity, created at <paramref name="path"/>.</summary>
    public static void CommittedRepository(string path, CommitSpec? spec = null)
    {
        spec ??= new CommitSpec();
        GitTemplateCache.CopyTo($"committed-repository|{spec}", path, template =>
        {
            Git(template, "init --initial-branch=main");
            Identify(template, spec);
            Commit(template, spec);
        });
    }

    /// <summary>
    /// <c>origin.git</c> (bare) and a clone at <c>ws/repo</c> under <paramref name="root"/>, with the identity set.
    /// <paramref name="commit"/> adds the first commit, and <paramref name="push"/> publishes it with an upstream.
    /// Returns the clone's path.
    /// </summary>
    public static string ClonedRepository(string root, CommitSpec? spec = null, bool commit = true, bool push = false)
    {
        spec ??= new CommitSpec();
        GitTemplateCache.CopyTo($"cloned-repository|{spec}|{commit}|{push}", root, template =>
        {
            var origin = Path.Combine(template, "origin.git");
            var workspace = Path.Combine(template, "ws");
            Directory.CreateDirectory(origin);
            Directory.CreateDirectory(workspace);
            Git(origin, "init --bare --initial-branch=main");
            Git(workspace, $"clone \"{origin}\" repo");
            var repo = Path.Combine(workspace, "repo");
            Identify(repo, spec);
            if (commit)
                Commit(repo, spec);
            if (push)
                Git(repo, "push -u origin main");
        });

        return Path.Combine(root, "ws", "repo");
    }

    /// <summary>
    /// A bare <c>origin.git</c> whose default branch is <paramref name="defaultBranch"/>, with one commit on it, plus the
    /// <c>seed</c> working copy that pushed it, so a test can add commits to the remote. Returns the remote's path.
    /// </summary>
    public static string SeededRemote(string root, string defaultBranch = "main")
    {
        GitTemplateCache.CopyTo($"seeded-remote|{defaultBranch}", root, template =>
        {
            var origin = Path.Combine(template, "origin.git");
            var seed = Path.Combine(template, "seed");
            Directory.CreateDirectory(origin);
            Directory.CreateDirectory(seed);
            var spec = new CommitSpec(Content: "init\n", UserName: "T", UserEmail: "t@example.com");
            Git(origin, $"init --bare --initial-branch={defaultBranch}");
            Git(seed, $"init --initial-branch={defaultBranch}");
            Identify(seed, spec);
            Git(seed, $"remote add origin \"{origin}\"");
            Commit(seed, spec);
            Git(seed, $"push origin {defaultBranch}");
        });

        return Path.Combine(root, "origin.git");
    }

    /// <summary>
    /// A bare remote with one commit pushed to <c>main</c> from a separate <c>source</c> working copy that has no
    /// remote of its own, the shape the attach and discard tests clone from. Returns the remote's path.
    /// </summary>
    public static string BareRemote(string root, bool withCommit)
    {
        GitTemplateCache.CopyTo($"bare-remote|{withCommit}", root, template =>
        {
            var bare = Path.Combine(template, "remote.git");
            Directory.CreateDirectory(bare);
            Git(bare, "init --bare --initial-branch=main");
            if (!withCommit)
                return;

            var source = Path.Combine(template, "source");
            Directory.CreateDirectory(source);
            Git(source, "init --initial-branch=main");
            File.WriteAllText(Path.Combine(source, "README.md"), "from remote\n");
            Git(source, "add --all");
            Git(source, "-c user.name=\"GrayMoon Test\" -c user.email=graymoon-test@example.com -c commit.gpgsign=false commit -m \"Initial commit\"");
            Git(source, $"push \"{bare}\" main");
        });

        return Path.Combine(root, "remote.git");
    }

    private static void Identify(string repo, CommitSpec spec)
    {
        Git(repo, $"config user.email {spec.UserEmail}");
        Git(repo, $"config user.name {spec.UserName}");
    }

    private static void Commit(string repo, CommitSpec spec)
    {
        File.WriteAllText(Path.Combine(repo, spec.File), spec.Content);
        Git(repo, "add -A");
        Git(repo, $"commit -q -m \"{spec.Message}\"");
    }

    private static void Git(string workingDirectory, string args) => GitConfigTestSupport.Git(workingDirectory, args);
}
