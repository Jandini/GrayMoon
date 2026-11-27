using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Services;
using LibGit2Sharp;

namespace GrayMoon.Worker.AotProbe;

/// <summary>
/// Runs every LibGit2Sharp-backed class of the Worker against fixture repositories and checks each answer against
/// the git CLI. Run once on the normal runtime and once as a Native AOT exe with the same <c>--root</c>: the
/// canonical files of the two runs must be identical.
/// </summary>
internal static class Program
{
    private static readonly List<string> Canonical = [];
    private static int _passed;
    private static int _failed;
    private static long _gitClock = 1767225600; // 2026-01-01T00:00:00Z; each git call moves it one minute

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string? root = null, canonicalFile = null;
        var loadOnly = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--root" && i + 1 < args.Length) root = args[++i];
            else if (args[i] == "--canonical" && i + 1 < args.Length) canonicalFile = args[++i];
            else if (args[i] == "--load-only") loadOnly = true;
        }

        if (args.Contains("--version-only"))
        {
            Console.WriteLine(GlobalSettings.Version.InformationalVersion);
            return 0;
        }

        Console.WriteLine($"runtime:{RuntimeInformation.FrameworkDescription} {RuntimeInformation.RuntimeIdentifier}");
        Console.WriteLine($"dynamic code supported: {RuntimeFeature.IsDynamicCodeSupported} (False means Native AOT)");
        Console.WriteLine($"base directory: {AppContext.BaseDirectory}");

        if (loadOnly)
        {
            // What the Worker does at start. Without the native library this must report, not crash.
            var damage = new WorkerInstallProbe().Check();
            Console.WriteLine(damage is null ? "LOAD OK" : $"LOAD DAMAGED: {damage}");
            return damage is null ? 0 : 3;
        }

        if (args.Contains("--diagnose") && root is not null)
        {
            Diagnose(Path.GetFullPath(root));
            return 0;
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            Console.Error.WriteLine("usage: graymoon-libgit2-aot-probe --root <empty folder> [--canonical <file>] | --load-only");
            return 2;
        }

        root = Path.GetFullPath(root);
        DeleteTree(root);
        Directory.CreateDirectory(root);

        Section("native library", () => NativeLibraryChecks());
        Fixture? fixture = null;
        Section("fixture", () => fixture = Fixture.Build(root));
        if (fixture is not null)
        {
            Section("snapshot reader", () => SnapshotChecks(fixture));
            Section("ignore service", () => IgnoreChecks(fixture));
            Section("config reader", () => ConfigChecks(fixture));
            Section("configuration initializer", () => InitializerChecks(fixture));
            Section("gitversion fingerprint", () => FingerprintChecks(fixture));
            Section("load and handle release", () => StressChecks(fixture));
        }

        Console.WriteLine();
        Console.WriteLine($"RESULT: {_passed} passed, {_failed} failed");
        if (canonicalFile is not null)
            File.WriteAllLines(canonicalFile, Canonical, new UTF8Encoding(false));
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>Raw LibGit2Sharp calls on an existing repository, each printed with its result or exception.</summary>
    private static void Diagnose(string repositoryPath)
    {
        void Show(string name, Func<string?> read)
        {
            try { Console.WriteLine($"{name}: {read() ?? "<null>"}"); }
            catch (Exception ex) { Console.WriteLine($"{name}: THROWS {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}"); }
        }

        Show("version", () => GlobalSettings.Version.InformationalVersion);
        Show("native module after reading the version", LoadedNativeLibrary);
        Show("install probe", () => new WorkerInstallProbe().Check() ?? "no damage");
        using var repository = new Repository(repositoryPath);
        Show("native module after opening a repository", LoadedNativeLibrary);
        // Each of these returns its answer through a libgit2 buffer (git_buf).
        Show("buffer: discover", () => Repository.Discover(Path.Combine(repositoryPath, "src")));
        Show("buffer: prettify message", () => Commit.PrettifyMessage("subject   \n\n\n# comment\nbody\n", '#'));
        Show("buffer: describe", () => repository.Describe(repository.Head.Tip, new DescribeOptions { Strategy = DescribeStrategy.Tags }));
        Show("buffer: features (no buffer, for contrast)", () => GlobalSettings.Version.Features.ToString());
        foreach (var level in new[] { ConfigurationLevel.Local, ConfigurationLevel.Global, ConfigurationLevel.Xdg, ConfigurationLevel.System, ConfigurationLevel.ProgramData })
            Show($"has config {level}", () => repository.Config.HasConfig(level).ToString());
        Show("user.name effective", () => repository.Config.Get<string>("user.name")?.Value);
        Show("user.name global", () => repository.Config.Get<string>("user.name", ConfigurationLevel.Global)?.Value);
        Show("config entries by level", () => string.Join(",", repository.Config.GroupBy(e => e.Level).Select(g => $"{g.Key}={g.Count()}")));
        Show("global search paths", () => string.Join(";", GlobalSettings.GetConfigSearchPaths(ConfigurationLevel.Global)));
        Show("head name", () => repository.Head.CanonicalName);
        Show("head is tracking", () => repository.Head.IsTracking.ToString());
        Show("head remote name", () => repository.Head.RemoteName);
        Show("head upstream canonical name", () => repository.Head.UpstreamBranchCanonicalName);
        Show("head tracked branch", () => repository.Head.TrackedBranch?.CanonicalName);
        Show("branches[head] tracked branch", () => repository.Branches[repository.Refs.Head.TargetIdentifier]?.TrackedBranch?.CanonicalName);
        Show("tracking details", () => $"{repository.Head.TrackingDetails.AheadBy}/{repository.Head.TrackingDetails.BehindBy}");
        Show("remotes", () => string.Join(",", repository.Network.Remotes.Select(r => $"{r.Name}={r.Url}")));
    }

    private static string? LoadedNativeLibrary()
    {
        _ = GlobalSettings.Version;
        using var process = Process.GetCurrentProcess();
        return process.Modules.Cast<ProcessModule>().FirstOrDefault(m => m.ModuleName.StartsWith("git2", StringComparison.OrdinalIgnoreCase))?.FileName;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Checks
    // ------------------------------------------------------------------------------------------------------------

    private static void NativeLibraryChecks()
    {
        Check("install probe reports no damage", () => new WorkerInstallProbe().Check() ?? "null", "null");
        Record("libgit2 version", () => GlobalSettings.Version.InformationalVersion);
        Record("libgit2 features", () => GlobalSettings.Version.Features.ToString());
        CheckThat("native library path is resolvable or default", () =>
        {
            var path = GlobalSettings.NativeLibraryPath;
            return path is null || Directory.Exists(path) ? "ok" : $"missing folder {path}";
        });
    }

    private static void SnapshotChecks(Fixture f)
    {
        var reader = new LibGit2SharpLocalGitSnapshotReader();
        var work = f.Work;

        void Scenario(string name, string repo, string? divergenceBase, string? upstream, string? noUpstreamCompare)
        {
            LocalGitSnapshot? s = null;
            CheckThat($"snapshot[{name}] reads", () =>
            {
                s = reader.Read(repo, new LocalGitSnapshotRequest(divergenceBase), CancellationToken.None);
                return "ok";
            });
            if (s is null)
                return;

            var headCli = GitTry(repo, "rev-parse", "--verify", "-q", "HEAD");
            var hasHead = headCli.Code == 0;
            Check($"snapshot[{name}] head", () => s.HeadSha ?? "null", hasHead ? headCli.Out : "null");
            Check($"snapshot[{name}] unborn", () => s.IsHeadUnborn.ToString(), (!hasHead).ToString());
            Check($"snapshot[{name}] current branch", () => s.CurrentBranch ?? "", Git(repo, "branch", "--show-current"));
            Check($"snapshot[{name}] tags newest first", () => string.Join(",", s.Refs.Tags),
                string.Join(",", Lines(Git(repo, "tag", "--sort=-creatordate"))));
            Check($"snapshot[{name}] local branches", () => Sorted(s.Refs.LocalBranches),
                Sorted(Lines(Git(repo, "for-each-ref", "--format=%(refname)", "refs/heads")).Select(r => r["refs/heads/".Length..])));
            Check($"snapshot[{name}] remote branches", () => Sorted(s.Refs.RemoteBranches),
                Sorted(Lines(Git(repo, "for-each-ref", "--format=%(refname)", "refs/remotes/origin"))
                    .Select(r => r["refs/remotes/origin/".Length..]).Where(r => r != "HEAD")));

            var describe = GitTry(repo, "describe", "--tags", "--exact-match", "HEAD");
            var detached = GitTry(repo, "symbolic-ref", "-q", "HEAD").Code != 0;
            Check($"snapshot[{name}] checked out tag", () => s.CheckedOutTag ?? "null",
                detached && hasHead && describe.Code == 0 ? describe.Out : "null");

            var originHead = GitTry(repo, "symbolic-ref", "-q", "refs/remotes/origin/HEAD");
            Check($"snapshot[{name}] default origin ref", () => s.Refs.DefaultOriginRef ?? "null",
                originHead.Code == 0 ? originHead.Out["refs/remotes/".Length..] : "null");

            if (hasHead && originHead.Code == 0)
            {
                var divergenceRef = divergenceBase is null ? originHead.Out["refs/remotes/".Length..] : "origin/" + StripOrigin(divergenceBase);
                var counts = Git(repo, "rev-list", "--left-right", "--count", $"HEAD...refs/remotes/{divergenceRef}").Split('\t');
                Check($"snapshot[{name}] ahead/behind of {divergenceRef}", () => $"{s.DefaultAhead}/{s.DefaultBehind}", $"{counts[0]}/{counts[1]}");
            }
            else
            {
                Check($"snapshot[{name}] ahead/behind unknown", () => $"{s.DefaultAhead}/{s.DefaultBehind}", "/");
            }

            if (upstream is not null)
            {
                var counts = Git(repo, "rev-list", "--left-right", "--count", $"HEAD...{upstream}").Split('\t');
                Check($"snapshot[{name}] upstream counts", () => Counts(s.CurrentBranchCounts), $"{counts[0]}/{counts[1]}/True/True");
            }
            else if (noUpstreamCompare is not null)
            {
                var outgoing = Git(repo, "rev-list", "--count", $"{noUpstreamCompare}..HEAD");
                Check($"snapshot[{name}] counts without upstream", () => Counts(s.CurrentBranchCounts), $"{outgoing}//False/True");
            }
            else
            {
                Check($"snapshot[{name}] no counts", () => Counts(s.CurrentBranchCounts), s.CurrentBranch is null ? "null" : "//False/False");
            }
        }

        Git(work, "checkout", "-q", "feature/one");
        Scenario("branch with upstream", work, null, "refs/remotes/origin/feature/one", null);
        Scenario("branch with upstream, base dup", work, "dup", "refs/remotes/origin/feature/one", null);
        Git(work, "checkout", "-q", "local-only");
        Scenario("branch without upstream", work, null, null, "refs/remotes/origin/main");
        Scenario("branch without upstream, local base", work, "origin/feature/one", null, "refs/heads/feature/one");
        Git(work, "checkout", "-q", f.UnicodeBranch);
        Scenario("unicode branch", work, null, "refs/remotes/origin/" + f.UnicodeBranch, null);
        Git(work, "checkout", "-q", "--detach", "refs/tags/v1.1.0");
        Scenario("detached on annotated tag", work, null, null, null);
        Git(work, "checkout", "-q", "--detach", "refs/tags/v1.0.0");
        Scenario("detached on commit with two tags", work, null, null, null);
        Git(work, "checkout", "-q", "--detach", "refs/remotes/origin/main");
        Scenario("detached without tag", work, null, null, null);
        Git(work, "checkout", "-q", "feature/one");
        Scenario("unborn head", f.Unborn, null, null, null);
        // "git worktree add -b <branch> <path> origin/main" makes the new branch track origin/main.
        Scenario("linked worktree", f.Worktree, null, "refs/remotes/origin/main", null);

        CheckThrows<LocalGitReadException>("snapshot of a bare repository is refused", () => reader.Read(f.Origin, new LocalGitSnapshotRequest(null), default), "no work tree");
        CheckThrows<LocalGitReadException>("snapshot of a folder that is no repository", () => reader.Read(f.NotARepository, new LocalGitSnapshotRequest(null), default), "Could not read");
        CheckThrows<LocalGitReadException>("snapshot of a missing folder", () => reader.Read(Path.Combine(f.Root, "missing"), new LocalGitSnapshotRequest(null), default), "Could not read");
        CheckThrows<OperationCanceledException>("snapshot honours a cancelled token", () =>
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            reader.Read(work, new LocalGitSnapshotRequest(null), cts.Token);
        }, null);

        // The exception LibGit2Sharp itself builds from a native error code.
        CheckThrows<RepositoryNotFoundException>("native error code maps to RepositoryNotFoundException", () =>
        {
            using var repository = new Repository(f.NotARepository);
        }, null);
        Check("index entry count", () =>
        {
            using var repository = new Repository(work);
            return repository.Index.Count.ToString();
        }, Lines(Git(work, "ls-files")).Count.ToString());
    }

    private static void IgnoreChecks(Fixture f)
    {
        var service = new LibGit2SharpGitIgnoreService();
        var work = f.Work;
        string[] files =
        [
            "bin/x.dll", "a.log", "keep.log", "secret.txt", "sub/secret.txt", "build-out/f.txt", "src/x.tmp",
            "src/important.tmp", "tracked.log", "src/app.cs", "new.txt", f.UnicodeIgnoredFile, f.UnicodeFile, "local-excluded.txt",
        ];
        foreach (var file in files.Where(file => !File.Exists(Path.Combine(work, file))))
            WriteFile(work, file, "probe\n");
        File.AppendAllText(Path.Combine(work, ".git", "info", "exclude"), "local-excluded.txt\n");
        string[] directories = ["bin", "build-out", "src", "docs", "sub"];

        using (var session = service.Open(work))
        {
            foreach (var file in files)
                Check($"ignore file {file}", () => session.IsExcluded(file, GitPathKind.File).ToString(), (GitTry(work, "check-ignore", "-q", file).Code == 0).ToString());
            foreach (var directory in directories)
                Check($"ignore directory {directory}", () => session.IsExcluded(directory, GitPathKind.Directory).ToString(), (GitTry(work, "check-ignore", "-q", directory + "/").Code == 0).ToString());

            var all = files.Concat(directories).ToList();
            var expectedStageable = all.Where(path => GitTry(work, "check-ignore", "-q", Directory.Exists(Path.Combine(work, path)) ? path + "/" : path).Code != 0);
            Check("select stageable", () => string.Join(",", session.SelectStageable(all).Stageable), string.Join(",", expectedStageable));
            CheckThat("a path outside the repository is refused", () =>
            {
                try { session.IsExcluded("../outside.txt", GitPathKind.File); return "no exception"; }
                catch (Exception ex) when (ex is ArgumentException or GitIgnoreException) { return "ok"; }
            });
        }

        CheckThrows<GitIgnoreException>("ignore session on a bare repository is refused", () => service.Open(f.Origin).Dispose(), "no work tree");
        CheckThrows<GitIgnoreException>("ignore session on a folder that is no repository", () => service.Open(f.NotARepository).Dispose(), "Could not open");
        CheckThat("worktree ignore session", () =>
        {
            WriteFile(f.Worktree, "wt.log", "x\n");
            using var session = service.Open(f.Worktree);
            return session.IsExcluded("wt.log", GitPathKind.File) && !session.IsExcluded("src/app.cs", GitPathKind.File) ? "ok" : "wrong answer";
        });
    }

    private static void ConfigChecks(Fixture f)
    {
        var work = f.Work;
        Git(work, "config", "--local", "core.hooksPath", f.HooksPathValue);
        Git(work, "config", "--local", "include.path", "extra.cfg");
        File.WriteAllText(Path.Combine(work, ".git", "extra.cfg"), "[graymoon]\n\tprobe = from include\n");
        Git(work, "config", "--local", "--add", "graymoon.multi", "first");
        Git(work, "config", "--local", "--add", "graymoon.multi", "last");
        Git(work, "config", "--local", "graymoon.empty", "");

        foreach (var key in new[] { "remote.origin.url", "core.hooksPath", "graymoon.probe", "graymoon.multi", "graymoon.empty", "graymoon.unset", "user.name", "core.bare", "branch.feature/one.merge" })
        {
            var cli = GitTry(work, "config", "--get", key);
            Check($"config {key}", () =>
            {
                var (handled, value) = LibGit2SharpConfigReader.TryGetString(work, key, null, CancellationToken.None);
                return $"{handled}:{value ?? "<unset>"}";
            }, $"True:{(cli.Code == 0 ? cli.Out : "<unset>")}");
        }

        // The layers above the repository. LibGit2Sharp finds each file through a libgit2 buffer (git_config_find_*).
        Check("config: global layer is loaded", () =>
        {
            using var repository = new Repository(work);
            return repository.Config.HasConfig(ConfigurationLevel.Global).ToString();
        }, (GitTry(work, "config", "--global", "--list").Code == 0).ToString());
        Check("config: system layer is loaded", () =>
        {
            using var repository = new Repository(work);
            return repository.Config.HasConfig(ConfigurationLevel.System).ToString();
        }, (GitTry(work, "config", "--system", "--list").Code == 0).ToString());
        Check("buffer result: repository discovery", () => Repository.Discover(Path.Combine(work, "src")) is { } gitDir ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(gitDir)) : "null",
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(work, Git(work, "rev-parse", "--git-dir")))));
        Check("buffer result: upstream of the checked-out branch", () =>
        {
            using var repository = new Repository(work);
            return repository.Head.TrackedBranch?.CanonicalName ?? "null";
        }, Git(work, "rev-parse", "--symbolic-full-name", "@{upstream}"));

        Check("config unset hooks path in a clone", () => Handled(LibGit2SharpConfigReader.TryGetString(f.Unborn, "core.hooksPath", null, default)),
            "True:" + (GitTry(f.Unborn, "config", "--get", "core.hooksPath") is { Code: 0 } r ? r.Out : "<unset>"));
        Check("config in a linked worktree is left to git", () => Handled(LibGit2SharpConfigReader.TryGetString(f.Worktree, "remote.origin.url", null, default)), "False:<unset>");
        Check("config with includeIf is left to git", () => Handled(LibGit2SharpConfigReader.TryGetString(f.IncludeIf, "remote.origin.url", null, default)), "False:<unset>");
        Check("config in a folder that is no repository", () => Handled(LibGit2SharpConfigReader.TryGetString(f.NotARepository, "remote.origin.url", null, default)), "False:<unset>");
        Check("config with a redirecting environment is left to git", () => Handled(LibGit2SharpConfigReader.TryGetString(work, "remote.origin.url", null, name => name == "GIT_CONFIG_GLOBAL" ? "x" : null, default)), "False:<unset>");
    }

    private static void InitializerChecks(Fixture f)
    {
        var first = new RepositoryConfigurationInitializer(isWindows: true);
        Check("long paths: first call writes", () => first.EnsureWindowsLongPaths(f.LongPaths, default).ToString(), "Configured");
        Check("long paths: value on disk", () => Git(f.LongPaths, "config", "--local", "--get", "core.longpaths"), "true");
        Check("long paths: remembered", () => first.EnsureWindowsLongPaths(f.LongPaths, default).ToString(), "AlreadyConfigured");
        Check("long paths: new instance reads it", () => new RepositoryConfigurationInitializer(isWindows: true).EnsureWindowsLongPaths(f.LongPaths, default).ToString(), "AlreadyConfigured");
        Check("long paths: false is overwritten", () =>
        {
            Git(f.LongPaths, "config", "--local", "core.longpaths", "false");
            var result = new RepositoryConfigurationInitializer(isWindows: true).EnsureWindowsLongPaths(f.LongPaths, default);
            return $"{result}:{Git(f.LongPaths, "config", "--local", "--get", "core.longpaths")}";
        }, "Configured:true");
        Check("long paths: worktree writes the shared config", () =>
        {
            var result = new RepositoryConfigurationInitializer(isWindows: true).EnsureWindowsLongPaths(f.LongPathsWorktree, default);
            return $"{result}:{Git(f.LongPathsMain, "config", "--local", "--get", "core.longpaths")}";
        }, "Configured:true");
        Check("long paths: folder that is no repository", () => new RepositoryConfigurationInitializer(isWindows: true).EnsureWindowsLongPaths(f.NotARepository, default).ToString(), "Failed");
        Check("long paths: locked config file", () =>
        {
            var lockFile = Path.Combine(f.Locked, ".git", "config.lock");
            File.WriteAllText(lockFile, "");
            try
            {
                var result = new RepositoryConfigurationInitializer(isWindows: true).EnsureWindowsLongPaths(f.Locked, default);
                return $"{result}:{GitTry(f.Locked, "config", "--local", "--get", "core.longpaths").Code}";
            }
            finally
            {
                File.Delete(lockFile);
            }
        }, "Failed:1");
        Check("long paths: not on Windows", () => new RepositoryConfigurationInitializer(isWindows: false).EnsureWindowsLongPaths(f.LongPaths, default).ToString(), "NotApplicable");
    }

    private static void FingerprintChecks(Fixture f)
    {
        var work = f.Work;
        var options = RepositoryVersionOptions.Default;
        GitVersionInputFingerprint? before = null;
        CheckThat("fingerprint computes", () => (before = GitVersionInputFingerprint.TryCompute(work, options)) is null ? "null" : "ok");
        if (before is null)
            return;

        Check("fingerprint head", () => before.Head, Git(work, "rev-parse", "HEAD"));
        Check("fingerprint head identity", () => before.HeadIdentity, "ref:" + Git(work, "symbolic-ref", "HEAD"));
        Record("fingerprint refs digest", () => before.Refs);
        Record("fingerprint config digest", () => before.Config);
        Record("fingerprint tool digest", () => before.Tool);
        Record("fingerprint invocation digest", () => before.Invocation);
        Check("fingerprint is stable", () => GitVersionInputFingerprint.TryCompute(work, options)?.FirstDifference(before) ?? "equal", "equal");
        Check("fingerprint sees a new tag", () =>
        {
            Git(work, "tag", "probe-fingerprint-tag");
            return GitVersionInputFingerprint.TryCompute(work, options)?.FirstDifference(before) ?? "equal";
        }, "REFS_CHANGED");
        Check("fingerprint sees a detached head", () =>
        {
            Git(work, "tag", "-d", "probe-fingerprint-tag");
            Git(work, "checkout", "-q", "--detach", "HEAD");
            try { return GitVersionInputFingerprint.TryCompute(work, options)?.FirstDifference(before) ?? "equal"; }
            finally { Git(work, "checkout", "-q", "feature/one"); }
        }, "HEAD_IDENTITY_CHANGED");
        Check("fingerprint of an unborn head", () => GitVersionInputFingerprint.TryCompute(f.Unborn, options) is null ? "null" : "value", "null");
        Check("fingerprint of a bare repository", () => GitVersionInputFingerprint.TryCompute(f.Origin, options) is null ? "null" : "value", "null");
        Check("fingerprint of a folder that is no repository", () => GitVersionInputFingerprint.TryCompute(f.NotARepository, options) is null ? "null" : "value", "null");
        CheckThat("fingerprint of a linked worktree", () => GitVersionInputFingerprint.TryCompute(f.Worktree, options) is { } w && w.Head == Git(f.Worktree, "rev-parse", "HEAD") ? "ok" : "wrong or null");
    }

    private static void StressChecks(Fixture f)
    {
        const int Threads = 8;
        const int Iterations = 150;
        var repo = f.Stress;
        var reader = new LibGit2SharpLocalGitSnapshotReader();
        var ignore = new LibGit2SharpGitIgnoreService();
        var expected = Describe(reader.Read(repo, new LocalGitSnapshotRequest(null), default));
        var expectedUrl = Git(repo, "config", "--get", "remote.origin.url");

        Check($"{Threads} threads x {Iterations} reads give one answer", () =>
        {
            var wrong = 0;
            var errors = new List<string>();
            var threads = Enumerable.Range(0, Threads).Select(_ => new Thread(() =>
            {
                try
                {
                    for (var i = 0; i < Iterations; i++)
                    {
                        if (Describe(reader.Read(repo, new LocalGitSnapshotRequest(null), default)) != expected)
                            Interlocked.Increment(ref wrong);
                        using (var session = ignore.Open(repo))
                        {
                            if (!session.IsExcluded("a.log", GitPathKind.File) || session.IsExcluded("src/app.cs", GitPathKind.File))
                                Interlocked.Increment(ref wrong);
                        }

                        if (LibGit2SharpConfigReader.TryGetString(repo, "remote.origin.url", null, default) != (true, expectedUrl))
                            Interlocked.Increment(ref wrong);
                        if (GitVersionInputFingerprint.TryCompute(repo, RepositoryVersionOptions.Default) is null)
                            Interlocked.Increment(ref wrong);
                        if (i % 25 == 0)
                            GC.Collect();
                    }
                }
                catch (Exception ex)
                {
                    lock (errors) errors.Add($"{ex.GetType().Name}: {ex.Message}");
                }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());
            return errors.Count > 0 ? errors[0] : $"wrong={wrong}";
        }, "wrong=0");

        Check("finalizers run without a crash", () =>
        {
            // Handles that are dropped without Dispose are freed by LibGit2Sharp's finalizers.
            for (var i = 0; i < 50; i++)
                _ = new Repository(repo).Head.Tip.Sha;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return "ok";
        }, "ok");

        Check("no handle is left on the repository", () =>
        {
            DeleteTree(repo);
            return Directory.Exists(repo) ? "folder still there" : "deleted";
        }, "deleted");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Fixture
    // ------------------------------------------------------------------------------------------------------------

    private sealed record Fixture(
        string Root, string Origin, string Work, string Unborn, string Worktree, string NotARepository, string IncludeIf,
        string LongPaths, string LongPathsMain, string LongPathsWorktree, string Locked, string Stress)
    {
        public string UnicodeBranch => "\u00FCn\u00EF/br\u00E5nch";
        public string UnicodeFile => "docs/\u017Elu\u0165ou\u010Dk\u00FD k\u016F\u0148.txt";
        public string UnicodeIgnoredFile => "docs/nov\u00FD soubor.log";
        public string HooksPathValue => "C:/hooks dir/\u017E";

        public static Fixture Build(string root)
        {
            string P(string name) => Path.Combine(root, name);
            var f = new Fixture(root, P("origin.git"), P("work"), P("unborn"), P("wt"), P("not-a-repo"), P("include-if"),
                P("long-paths"), P("long-paths-main"), P("long-paths-wt"), P("locked"), P("stress"));
            var seed = P("seed");

            Git(root, "init", "-q", "--bare", "-b", "main", f.Origin);
            Git(root, "init", "-q", "-b", "main", seed);
            WriteFile(seed, ".gitignore", "bin/\n*.log\n!keep.log\n/secret.txt\nbuild-*/\n");
            WriteFile(seed, "src/app.cs", "class A {}\n");
            WriteFile(seed, "src/.gitignore", "*.tmp\n!important.tmp\n");
            WriteFile(seed, "dotnet-tools.json", "{ \"version\": 1, \"isRoot\": true, \"tools\": {} }\n");
            WriteFile(seed, "GitVersion.yml", "mode: ContinuousDelivery\n");
            WriteFile(seed, "tracked.log", "tracked although the pattern ignores it\n");
            Git(seed, "add", "-A");
            Git(seed, "add", "-f", "tracked.log");
            Git(seed, "commit", "-q", "-m", "c1");
            WriteFile(seed, f.UnicodeFile, "unicode\n");
            Commit(seed, "c2");
            Git(seed, "tag", "v1.0.0");
            Git(seed, "branch", "dup");
            Git(seed, "tag", "-a", "dup", "-m", "annotated tag with a branch of the same name");
            WriteFile(seed, "src/b.cs", "class B {}\n");
            Commit(seed, "c3");
            Git(seed, "tag", "-a", "v1.1.0", "-m", "release 1.1.0");
            Git(seed, "tag", "v1.1.0-light");
            Git(seed, "branch", f.UnicodeBranch);
            Git(seed, "checkout", "-q", "-b", "feature/one");
            WriteFile(seed, "src/f1.cs", "class F1 {}\n");
            Commit(seed, "f1");
            WriteFile(seed, "src/f2.cs", "class F2 {}\n");
            Commit(seed, "f2");
            Git(seed, "checkout", "-q", "main");
            Git(seed, "remote", "add", "origin", f.Origin);
            Git(seed, "push", "-q", "origin", "--all");
            Git(seed, "push", "-q", "origin", "--tags");

            Git(root, "clone", "-q", f.Origin, f.Work);
            Git(f.Work, "checkout", "-q", "feature/one");
            WriteFile(f.Work, "src/w1.cs", "class W1 {}\n");
            Commit(f.Work, "w1");
            Git(f.Work, "checkout", "-q", f.UnicodeBranch);
            Git(f.Work, "checkout", "-q", "--no-track", "-b", "local-only", "main");
            WriteFile(f.Work, "src/l1.cs", "class L1 {}\n");
            Commit(f.Work, "l1");
            WriteFile(f.Work, "src/l2.cs", "class L2 {}\n");
            Commit(f.Work, "l2");

            // The remote moves on after the clone, so the clone is both ahead and behind.
            WriteFile(seed, "src/c4.cs", "class C4 {}\n");
            Commit(seed, "c4");
            WriteFile(seed, "src/c5.cs", "class C5 {}\n");
            Commit(seed, "c5");
            Git(seed, "tag", "-a", "v2.0.0", "-m", "release 2.0.0");
            Git(seed, "checkout", "-q", "feature/one");
            WriteFile(seed, "src/f3.cs", "class F3 {}\n");
            Commit(seed, "f3");
            Git(seed, "push", "-q", "origin", "--all");
            Git(seed, "push", "-q", "origin", "--tags");
            Git(f.Work, "fetch", "-q", "--tags", "origin");
            Git(f.Work, "checkout", "-q", "feature/one");
            Git(f.Work, "worktree", "add", "-q", "-b", "wt-branch", f.Worktree, "refs/remotes/origin/main");

            Git(root, "init", "-q", "-b", "main", f.Unborn);
            Directory.CreateDirectory(f.NotARepository);
            File.WriteAllText(Path.Combine(f.NotARepository, "file.txt"), "no repository here\n");

            Git(root, "clone", "-q", f.Origin, f.IncludeIf);
            Git(f.IncludeIf, "config", "--local", "includeIf.gitdir:/nowhere/.path", "nothing.cfg");
            Git(root, "clone", "-q", f.Origin, f.LongPaths);
            Git(root, "clone", "-q", f.Origin, f.LongPathsMain);
            Git(f.LongPathsMain, "worktree", "add", "-q", "-b", "lp-wt", f.LongPathsWorktree, "refs/remotes/origin/main");
            Git(root, "clone", "-q", f.Origin, f.Locked);
            Git(root, "clone", "-q", f.Origin, f.Stress);
            WriteFile(f.Stress, "a.log", "ignored\n");

            // Clones made by a current git may already carry the setting; the initializer checks start without it.
            foreach (var repo in new[] { f.LongPaths, f.LongPathsMain, f.Locked })
                GitTry(repo, "config", "--local", "--unset-all", "core.longpaths");
            return f;
        }

        private static void Commit(string repo, string message)
        {
            Git(repo, "add", "-A");
            Git(repo, "commit", "-q", "-m", message);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------------------

    private static string Describe(LocalGitSnapshot s)
        => $"{s.HeadSha}|{s.CurrentBranch}|{s.CheckedOutTag}|{s.DefaultAhead}/{s.DefaultBehind}|{Counts(s.CurrentBranchCounts)}|{string.Join(",", s.Refs.Tags)}|{Sorted(s.Refs.LocalBranches)}|{Sorted(s.Refs.RemoteBranches)}";

    private static string Counts(CommitCountsProbeResult? c) => c is null ? "null" : $"{c.Outgoing}/{c.Incoming}/{c.HasUpstream}/{c.CountsProbed}";

    private static string Handled((bool Handled, string? Value) result) => $"{result.Handled}:{result.Value ?? "<unset>"}";

    private static string Sorted(IEnumerable<string> values) => string.Join(",", values.OrderBy(v => v, StringComparer.Ordinal));

    private static List<string> Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string StripOrigin(string name) => name.StartsWith("origin/", StringComparison.Ordinal) ? name["origin/".Length..] : name;

    private static void Section(string name, Action body)
    {
        Console.WriteLine();
        Console.WriteLine($"== {name}");
        try
        {
            body();
        }
        catch (Exception ex)
        {
            Fail($"{name}: section stopped", $"{ex.GetType().FullName}: {ex.Message}");
        }
    }

    /// <summary>The library's answer must equal the answer of the git CLI (or a fixed expectation).</summary>
    private static void Check(string name, Func<string> actual, string expected)
    {
        string value;
        try
        {
            value = actual();
        }
        catch (Exception ex)
        {
            Fail(name, $"{ex.GetType().FullName}: {ex.Message}");
            return;
        }

        if (value == expected) Pass(name, value);
        else Fail(name, $"got <{value}> expected <{expected}>");
    }

    private static void CheckThat(string name, Func<string> actual) => Check(name, actual, "ok");

    /// <summary>No expectation of its own: the value goes to the canonical file, which the two runs must agree on.</summary>
    private static void Record(string name, Func<string> actual)
    {
        try
        {
            var value = actual();
            Pass(name, string.IsNullOrEmpty(value) ? "<empty>" : value);
        }
        catch (Exception ex)
        {
            Fail(name, $"{ex.GetType().FullName}: {ex.Message}");
        }
    }

    private static void CheckThrows<TException>(string name, Action action, string? messagePart) where TException : Exception
    {
        try
        {
            action();
            Fail(name, "no exception");
        }
        catch (TException ex) when (messagePart is null || ex.Message.Contains(messagePart, StringComparison.OrdinalIgnoreCase))
        {
            Pass(name, typeof(TException).Name);
        }
        catch (Exception ex)
        {
            Fail(name, $"{ex.GetType().FullName}: {ex.Message}");
        }
    }

    private static void Pass(string name, string value)
    {
        _passed++;
        Canonical.Add($"{name} = {value}");
        Console.WriteLine($"[PASS] {name} = {value}");
    }

    private static void Fail(string name, string detail)
    {
        _failed++;
        Canonical.Add($"{name} = FAILED {detail}");
        Console.WriteLine($"[FAIL] {name}: {detail}");
    }

    private static void WriteFile(string repo, string relativePath, string content)
    {
        var path = Path.Combine(repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static string Git(string workingDirectory, params string[] arguments)
    {
        var result = GitTry(workingDirectory, arguments);
        if (result.Code != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({result.Code}) in {workingDirectory}: {result.Error}");
        return result.Out;
    }

    private static (int Code, string Out, string Error) GitTry(string workingDirectory, params string[] arguments)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var setting in new[] { "core.autocrlf=false", "core.quotepath=false", "commit.gpgsign=false", "tag.gpgsign=false", "advice.detachedHead=false", "protocol.file.allow=always", "gc.auto=0" })
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(setting);
        }

        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        // Fixed identity and a clock that moves one minute per call: every run builds the same commits.
        var date = $"@{_gitClock} +0000";
        _gitClock += 60;
        psi.Environment["GIT_AUTHOR_NAME"] = "GrayMoon Probe";
        psi.Environment["GIT_AUTHOR_EMAIL"] = "probe@graymoon.invalid";
        psi.Environment["GIT_AUTHOR_DATE"] = date;
        psi.Environment["GIT_COMMITTER_NAME"] = "GrayMoon Probe";
        psi.Environment["GIT_COMMITTER_EMAIL"] = "probe@graymoon.invalid";
        psi.Environment["GIT_COMMITTER_DATE"] = date;
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git could not be started");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Trim(), error.Result.Trim());
    }

    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
            return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
