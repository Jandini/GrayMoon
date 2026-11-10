using System.Reflection;
using GrayMoon.Common.Git;
using GrayMoon.Common.Manifest;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Services;
using GrayMoon.Worker.Services.GitChanges;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Review 2026-11-10, open items after the fix plan: a repository name from the App is one folder name, the last
/// two unvalidated request paths are validated, and a write never follows a link out of the repository.
/// </summary>
public sealed class WorkerRepositoryNameAndLinkValidationTests : IDisposable
{
    private const string Csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <ItemGroup>
            <PackageReference Include="Some.Package" Version="1.0.0" />
          </ItemGroup>
        </Project>
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-names-").FullName;
    private readonly string _repoPath;
    private readonly string _outsideDir;
    private readonly string _outsideCsproj;

    public WorkerRepositoryNameAndLinkValidationTests()
    {
        _repoPath = Path.Combine(_root, "ws", "Repo");
        Directory.CreateDirectory(_repoPath);
        // A sibling folder with a real project, reachable by "..", a rooted path or a link.
        _outsideDir = Path.Combine(_root, "ws", "Outside");
        Directory.CreateDirectory(_outsideDir);
        _outsideCsproj = Path.Combine(_outsideDir, "O.csproj");
        File.WriteAllText(_outsideCsproj, Csproj);
    }

    public void Dispose() => TestDirectory.Delete(_root);

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../Other")]
    [InlineData("..\\Other")]
    [InlineData("Org/Repo")]
    [InlineData("/etc")]
    [InlineData("C:")]
    [InlineData("C:\\Windows")]
    [InlineData("Repo.")]
    [InlineData("Repo ")]
    [InlineData(" Repo")]
    [InlineData("Re\u0001po")]
    [InlineData("Re*po")]
    public void A_repository_name_that_is_not_one_folder_name_is_refused(string name)
    {
        Assert.NotNull(WorkerRepositoryPaths.GetRepositoryNameRejection(name));
        Assert.Throws<ArgumentException>(() => WorkerRepositoryPaths.Resolve(Path.Combine(_root, "ws"), name, null));
        // Not even as the Workspace repository, where the name is not combined into the path.
        Assert.Throws<ArgumentException>(() => WorkerRepositoryPaths.Resolve(Path.Combine(_root, "ws"), name, name));
    }

    [Theory]
    [InlineData("GrayMoon")]
    [InlineData("GrayMoon.Desktop")]
    [InlineData("my-repo_1")]
    [InlineData(".github")]
    [InlineData("Repo With Space")]
    public void An_ordinary_repository_name_is_accepted(string name)
    {
        Assert.Null(WorkerRepositoryPaths.GetRepositoryNameRejection(name));
        Assert.Equal(Path.Combine(_root, "ws", name), WorkerRepositoryPaths.Resolve(Path.Combine(_root, "ws"), name, null));
    }

    [Fact]
    public async Task A_traversal_repository_name_never_writes_outside_the_workspace_folder()
    {
        // Before the check, ".." resolved to the folder above the workspace and the file was written there.
        await Assert.ThrowsAsync<ArgumentException>(() => new WriteRepositoryFileCommand().ExecuteAsync(new WriteRepositoryFileRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = "ws",
            RepositoryName = "..",
            FilePath = "escaped.txt",
            Content = "x",
        }));

        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
    }

    public static TheoryData<string> EscapingProjectPaths() => new()
    {
        "../Outside/O.csproj",
        "sub/../../Outside/O.csproj",
        "..\\Outside\\O.csproj",
        "{ROOTED}",
    };

    private string Expand(string path) => path == "{ROOTED}" ? _outsideCsproj : path;

    private static SyncRepositoryDependenciesCommand SyncCommand() => new(
        new CsProjFileService(new CsProjFileParser(), new LibGit2SharpGitIgnoreService(), new WorkspaceManifestFile(), NullLogger<CsProjFileService>.Instance),
        NullLogger<SyncRepositoryDependenciesCommand>.Instance);

    private SyncRepositoryDependenciesRequest SyncRequest(params string[] projectPaths) => new()
    {
        WorkspaceRoot = _root,
        WorkspaceName = "ws",
        RepositoryName = "Repo",
        ProjectUpdates = projectPaths.Select(p => new SyncRepositoryDependenciesProjectUpdate
        {
            ProjectPath = p,
            PackageUpdates = [new PackageVersionUpdate { PackageId = "Some.Package", NewVersion = "9.9.9" }],
        }).ToList(),
    };

    [Theory]
    [MemberData(nameof(EscapingProjectPaths))]
    public async Task SyncRepositoryDependencies_never_rewrites_a_project_outside_the_repository(string path)
    {
        var inside = Path.Combine(_repoPath, "In.csproj");
        await File.WriteAllTextAsync(inside, Csproj);

        // The good project is listed first: one bad path refuses the request before anything is written.
        await Assert.ThrowsAsync<ArgumentException>(() => SyncCommand().ExecuteAsync(SyncRequest("In.csproj", Expand(path))));

        Assert.Equal(Csproj, await File.ReadAllTextAsync(_outsideCsproj));
        Assert.Equal(Csproj, await File.ReadAllTextAsync(inside));
    }

    [Fact]
    public async Task SyncRepositoryDependencies_still_updates_a_project_inside_the_repository()
    {
        Directory.CreateDirectory(Path.Combine(_repoPath, "src", "In"));
        var inside = Path.Combine(_repoPath, "src", "In", "In.csproj");
        await File.WriteAllTextAsync(inside, Csproj);

        var response = await SyncCommand().ExecuteAsync(SyncRequest("src/In/In.csproj"));

        Assert.Equal(1, response.UpdatedCount);
        Assert.Contains("Version=\"9.9.9\"", await File.ReadAllTextAsync(inside));
    }

    [Theory]
    [MemberData(nameof(EscapingProjectPaths))]
    public async Task ResolveGeneratedPackageReferences_never_reads_a_project_outside_the_repository(string path)
    {
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "In.csproj"), Csproj);

        var response = await new ResolveGeneratedPackageReferencesCommand(new CsProjFileParser()).ExecuteAsync(new ResolveGeneratedPackageReferencesRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = "ws",
            Files =
            [
                new ResolveGeneratedPackageReferencesItem { RepositoryName = "Repo", FilePath = "In.csproj", Pattern = "Version=\"{Other}\" />" },
                new ResolveGeneratedPackageReferencesItem { RepositoryName = "Repo", FilePath = Expand(path), Pattern = "Version=\"{Other}\" />" },
            ],
        });

        // The inside file proves the pattern resolves a package; the outside one is answered like a missing file.
        Assert.Equal(2, response.Files!.Count);
        Assert.Equal("Some.Package", Assert.Single(response.Files[0].Packages).PackageName);
        Assert.Empty(response.Files[1].Packages);
    }

    [Fact]
    public async Task StageAndCommit_refuses_an_escaping_path_before_it_reaches_git()
    {
        var git = CallRecorder<IGitService>.Create(out var gitCalls);
        var reader = CallRecorder<IGitRepositoryReader>.Create(out _);
        using var coordinator = new GitStatusRefreshCoordinator(
            new FakeRepositoryGitChangesService { Delay = TimeSpan.Zero }, new GitChangesSnapshotCache(),
            Options.Create(new GitChangesOptions()), NullLogger<GitStatusRefreshCoordinator>.Instance);

        var response = await new StageAndCommitCommand(git, reader, coordinator).ExecuteAsync(new StageAndCommitRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = "ws",
            RepositoryName = "Repo",
            CommitMessage = "msg",
            PathsToStage = ["../Outside/O.csproj"],
        });

        Assert.False(response.Success);
        Assert.NotNull(response.ErrorMessage);
        Assert.DoesNotContain(nameof(IGitService.StageAndCommitAsync), gitCalls);
    }

    [Fact]
    public void A_link_that_leaves_the_repository_is_refused_and_one_that_stays_inside_is_not()
    {
        Directory.CreateDirectory(Path.Combine(_repoPath, "real"));
        if (!TryCreateDirectoryLink(Path.Combine(_repoPath, "out"), _outsideDir)
            || !TryCreateDirectoryLink(Path.Combine(_repoPath, "in"), Path.Combine(_repoPath, "real")))
        {
            return; // The OS or the account cannot create links; nothing to test here.
        }

        Assert.NotNull(RepositoryPathContainment.GetEscapeReason(_repoPath, Path.Combine(_repoPath, "out", "O.csproj")));
        Assert.NotNull(RepositoryPathContainment.GetEscapeReason(_repoPath, Path.Combine(_repoPath, "out", "new", "file.txt")));
        Assert.Null(RepositoryPathContainment.GetEscapeReason(_repoPath, Path.Combine(_repoPath, "in", "file.txt")));
        Assert.Null(RepositoryPathContainment.GetEscapeReason(_repoPath, Path.Combine(_repoPath, "real", "file.txt")));
        Assert.Null(RepositoryPathContainment.GetEscapeReason(_repoPath, Path.Combine(_repoPath, "not", "there", "yet.txt")));
    }

    [Fact]
    public void A_repository_reached_through_a_link_is_still_its_own_inside()
    {
        // A workspace folder that is itself a junction (a short path onto another drive, for example).
        var alias = Path.Combine(_root, "alias");
        if (!TryCreateDirectoryLink(alias, Path.Combine(_root, "ws")))
            return;

        var repoThroughLink = Path.Combine(alias, "Repo");
        Assert.Null(RepositoryPathContainment.GetEscapeReason(repoThroughLink, Path.Combine(repoThroughLink, "src", "a.txt")));
    }

    [Fact]
    public async Task Writes_through_a_link_that_leaves_the_repository_are_refused()
    {
        if (!TryCreateDirectoryLink(Path.Combine(_repoPath, "out"), _outsideDir))
            return;
        var versionFile = Path.Combine(_outsideDir, "versions.env");
        await File.WriteAllTextAsync(versionFile, "VERSION=old\n");

        var write = await new WriteRepositoryFileCommand().ExecuteAsync(new WriteRepositoryFileRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = "ws",
            RepositoryName = "Repo",
            FilePath = "out/written.txt",
            Content = "x",
        });
        var update = await new UpdateFileVersionsCommand().ExecuteAsync(new UpdateFileVersionsRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = "ws",
            RepositoryName = "Repo",
            FilePath = "out/versions.env",
            VersionPattern = "VERSION={@Repo}",
            TokenValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["@Repo"] = "9.9.9" },
        });
        await Assert.ThrowsAsync<ArgumentException>(() => SyncCommand().ExecuteAsync(SyncRequest("out/O.csproj")));

        Assert.False(write.Success);
        Assert.NotNull(write.ErrorMessage);
        Assert.False(File.Exists(Path.Combine(_outsideDir, "written.txt")));
        Assert.Equal(0, update.UpdatedCount);
        Assert.NotNull(update.ErrorMessage);
        Assert.Equal("VERSION=old\n", await File.ReadAllTextAsync(versionFile));
        Assert.Equal(Csproj, await File.ReadAllTextAsync(_outsideCsproj));
    }

    /// <summary>A symbolic link, or on Windows without the privilege for one, a junction. False when neither works.</summary>
    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }

        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", link, target },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            process.WaitForExit(10_000);
            // A junction needs no privilege, so on Windows the link tests always run.
            Assert.True(process.HasExited && process.ExitCode == 0 && Directory.Exists(link), "mklink /J failed for '" + link + "'.");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>A stand-in for a wide interface: records the method names called and returns default results.</summary>
    public class CallRecorder<T> : DispatchProxy where T : class
    {
        private readonly List<string> _calls = [];

        public static T Create(out IReadOnlyList<string> calls)
        {
            var proxy = Create<T, CallRecorder<T>>();
            calls = ((CallRecorder<T>)(object)proxy)._calls;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            lock (_calls)
                _calls.Add(targetMethod!.Name);
            var type = targetMethod.ReturnType;
            if (type == typeof(Task))
                return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var result = type.GetGenericArguments()[0];
                var value = result.IsValueType ? Activator.CreateInstance(result) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(result).Invoke(null, [value]);
            }

            return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
        }
    }
}
