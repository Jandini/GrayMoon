using System.Net;
using System.Net.Sockets;
using System.Text;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Commands;
using GrayMoon.Worker.Hosted;
using GrayMoon.Worker.Jobs;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>Review 2026-11-10, finding 21: App-supplied relative paths go through <see cref="GitRepositoryPathValidator"/>.</summary>
public sealed class FileVersionCommandPathValidationTests : IDisposable
{
    private const string Pattern = "VERSION={@Repo}";
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-f1-fv-").FullName;
    private readonly string _repoPath;
    private readonly string _outsideFile;

    public FileVersionCommandPathValidationTests()
    {
        _repoPath = Path.Combine(_root, "ws", "Repo");
        Directory.CreateDirectory(_repoPath);
        // A real file outside the repository, reachable by every tricky path below.
        _outsideFile = Path.Combine(_root, "ws", "outside.env");
        File.WriteAllText(_outsideFile, "VERSION=old\n");
    }

    public void Dispose() => TestDirectory.Delete(_root);

    public static TheoryData<string> EscapingPaths() => new()
    {
        "../outside.env",
        "sub/../../outside.env",
        "..\\outside.env",
        "/outside.env",
        "{ROOTED}",
    };

    private string Expand(string path) => path == "{ROOTED}" ? _outsideFile : path;

    [Theory]
    [MemberData(nameof(EscapingPaths))]
    public async Task UpdateFileVersions_never_writes_a_file_outside_the_repository(string path)
    {
        var response = await new UpdateFileVersionsCommand().ExecuteAsync(new UpdateFileVersionsRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = "ws",
            RepositoryName = "Repo",
            FilePath = Expand(path),
            VersionPattern = Pattern,
            TokenValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["@Repo"] = "9.9.9" },
        });

        Assert.Equal(0, response.UpdatedCount);
        Assert.NotNull(response.ErrorMessage);
        Assert.Equal("VERSION=old\n", File.ReadAllText(_outsideFile));
    }

    [Theory]
    [MemberData(nameof(EscapingPaths))]
    public async Task CheckFileVersions_never_reads_a_file_outside_the_repository(string path)
    {
        var response = await new CheckFileVersionsCommand(NullLogger<CheckFileVersionsCommand>.Instance).ExecuteAsync(new CheckFileVersionsRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = "ws",
            Files =
            [
                new CheckFileVersionsItem
                {
                    RepositoryName = "Repo",
                    FilePath = Expand(path),
                    Pattern = Pattern,
                    ExpectedValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["@Repo"] = "9.9.9" },
                }
            ],
        });

        // Read, the outside file would report one matched, out-of-date line. Rejected, it reports as missing.
        var result = Assert.Single(response.Files!);
        Assert.True(result.FileMissing);
        Assert.Equal(0, result.TotalMatchedLines);
        Assert.Empty(result.OutOfDateLines!);
    }

    [Theory]
    [InlineData("versions.env")]
    [InlineData("sub dir/it's \"quoted\".env")]
    public async Task A_path_inside_the_repository_still_works_with_spaces_and_quotes(string path)
    {
        if (OperatingSystem.IsWindows())
            path = path.Replace("\"", "");
        var full = Path.Combine(_repoPath, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, "VERSION=old\n");

        var update = await new UpdateFileVersionsCommand().ExecuteAsync(new UpdateFileVersionsRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = "ws",
            RepositoryName = "Repo",
            FilePath = path,
            VersionPattern = Pattern,
            TokenValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["@Repo"] = "9.9.9" },
        });

        Assert.Equal(1, update.UpdatedCount);
        Assert.Contains("VERSION=9.9.9", await File.ReadAllTextAsync(full));
    }
}

/// <summary>Review 2026-11-10, findings 8 (Worker side) and 21: restore reports failures and never interpolates a path.</summary>
public sealed class DotnetRestoreCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-f1-restore-").FullName;
    private readonly string _repoPath;

    public DotnetRestoreCommandTests()
    {
        _repoPath = Path.Combine(_root, "ws", "Repo");
        Directory.CreateDirectory(_repoPath);
    }

    public void Dispose() => TestDirectory.Delete(_root);

    private Task<Jobs.Response.DotnetRestoreResponse> RunAsync(ScriptedCommandLine commandLine, params string[] projectPaths) =>
        new DotnetRestoreCommand(commandLine, NullLogger<DotnetRestoreCommand>.Instance).ExecuteAsync(new DotnetRestoreRequest
        {
            WorkspaceRoot = _root,
            WorkspaceName = "ws",
            RepositoryName = "Repo",
            ProjectPaths = [.. projectPaths],
        });

    [Fact]
    public async Task A_non_zero_restore_is_a_failed_response_with_the_error_tail()
    {
        var commandLine = new ScriptedCommandLine(_ => new CommandLineResult(1, "error NU1101: Unable to find package Acme.Api.", ""));

        var response = await RunAsync(commandLine, "src/App/App.csproj");

        Assert.False(response.Success);
        Assert.Contains("src/App/App.csproj", response.ErrorMessage);
        Assert.Contains("NU1101", response.ErrorMessage);
        Assert.Contains("exit code 1", response.ErrorMessage);
    }

    [Fact]
    public async Task A_restore_that_timed_out_is_reported_as_the_timeout()
    {
        var commandLine = new ScriptedCommandLine(_ => new CommandLineResult(-1, null, "Process timed out after 600 seconds and was killed."));

        var response = await RunAsync(commandLine, "App.csproj");

        Assert.False(response.Success);
        Assert.Contains("timed out", response.ErrorMessage);
    }

    [Fact]
    public async Task One_failed_project_does_not_stop_the_others_and_fails_the_response()
    {
        var commandLine = new ScriptedCommandLine(args =>
            args[^1].EndsWith("Bad.csproj", StringComparison.Ordinal) ? new CommandLineResult(1, "", "boom") : new CommandLineResult(0, "", ""));

        var response = await RunAsync(commandLine, "Bad.csproj", "Good.csproj");

        Assert.Equal(2, commandLine.Calls.Count);
        Assert.False(response.Success);
        Assert.Contains("Bad.csproj", response.ErrorMessage);
        Assert.DoesNotContain("Good.csproj", response.ErrorMessage);
    }

    [Fact]
    public async Task A_successful_restore_is_a_successful_response()
    {
        var response = await RunAsync(new ScriptedCommandLine(_ => new CommandLineResult(0, "", "")), "App.csproj");

        Assert.True(response.Success);
        Assert.Null(response.ErrorMessage);
    }

    [Theory]
    [InlineData("src/My App/It's \"odd\".csproj")]
    [InlineData("src/a b/c&d.csproj")]
    public async Task The_project_path_is_one_argument_whatever_characters_it_contains(string projectPath)
    {
        var commandLine = new ScriptedCommandLine(_ => new CommandLineResult(0, "", ""));

        var response = await RunAsync(commandLine, projectPath);

        Assert.True(response.Success);
        var call = Assert.Single(commandLine.Calls);
        Assert.Equal(["restore", "--force", "--no-cache", Path.GetFullPath(Path.Combine(_repoPath, projectPath))], call);
        Assert.Equal(0, commandLine.StringCalls);
    }

    [Theory]
    [InlineData("../Other/Other.csproj")]
    [InlineData("src/../../Other.csproj")]
    [InlineData("/etc/Other.csproj")]
    [InlineData("C:/Other/Other.csproj")]
    public async Task A_project_path_outside_the_repository_is_never_run_and_fails_the_response(string projectPath)
    {
        var commandLine = new ScriptedCommandLine(_ => new CommandLineResult(0, "", ""));

        var response = await RunAsync(commandLine, projectPath);

        Assert.Empty(commandLine.Calls);
        Assert.False(response.Success);
        Assert.Contains(projectPath, response.ErrorMessage);
    }

    private sealed class ScriptedCommandLine(Func<IReadOnlyList<string>, CommandLineResult> respond) : ICommandLineService
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public int StringCalls { get; private set; }

        public Task<CommandLineResult> RunAsync(string fileName, string arguments, string? workingDirectory = null, string? stdin = null,
            CancellationToken cancellationToken = default, bool streamStderrAsStdout = false, bool mirrorFailureOutputAsStderr = false, TimeSpan? timeout = null, TimeSpan? idleTimeout = null)
        {
            StringCalls++;
            return Task.FromResult(new CommandLineResult(0, "", ""));
        }

        public Task<CommandLineResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null, byte[]? stdinBytes = null,
            CancellationToken cancellationToken = default, bool streamStderrAsStdout = false, bool mirrorFailureOutputAsStderr = false, TimeSpan? timeout = null, TimeSpan? idleTimeout = null)
        {
            Calls.Add(arguments);
            return Task.FromResult(respond(arguments));
        }
    }
}

/// <summary>Review 2026-11-10, finding 22: which hook payload paths may be scanned.</summary>
public sealed class HookRepositoryPathPolicyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-f1-policy-").FullName;

    public void Dispose() => TestDirectory.Delete(_root);

    private static HookRepositoryPathPolicy Policy(params string[] roots)
    {
        var known = new KnownWorkspaceRoots();
        foreach (var root in roots)
            known.Add(root);
        return new HookRepositoryPathPolicy(known);
    }

    private string Folder(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static GitService CreateGitService()
    {
        var commandLine = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(commandLine, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        return new GitService(Options.Create(new WorkerOptions()), NullLogger<GitService>.Instance, runner, reader, new LibGit2SharpGitIgnoreService());
    }

    [Fact]
    public void A_repository_under_a_known_root_is_accepted()
    {
        var repo = Folder("roots", "ws", "Repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        Assert.Null(Policy(Path.Combine(_root, "roots")).GetRejectionReason(repo, 1, 2));
        // Git reports the top level with forward slashes.
        Assert.Null(Policy(Path.Combine(_root, "roots")).GetRejectionReason(repo.Replace('\\', '/'), 1, 2));
    }

    [Fact]
    public void A_repository_outside_the_known_roots_is_rejected()
    {
        var repo = Folder("elsewhere", "Repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        Assert.NotNull(Policy(Folder("roots")).GetRejectionReason(repo, 1, 2));
    }

    [Fact]
    public void A_sibling_folder_that_only_shares_the_root_prefix_is_rejected()
    {
        var repo = Folder("roots-evil", "Repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        Assert.NotNull(Policy(Folder("roots")).GetRejectionReason(repo, 1, 2));
    }

    [Fact]
    public void A_folder_without_git_metadata_is_rejected_even_under_a_known_root()
    {
        var folder = Folder("roots", "ws", "NotARepo");

        Assert.NotNull(Policy(Path.Combine(_root, "roots")).GetRejectionReason(folder, 1, 2));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("..")]
    [InlineData(@"\\attacker.example\share\repo")]
    [InlineData("//attacker.example/share/repo")]
    public void A_relative_missing_or_network_path_is_rejected(string path)
    {
        Assert.NotNull(Policy(Folder("roots")).GetRejectionReason(path, 1, 2));
    }

    [Fact]
    public void A_missing_directory_under_a_known_root_is_rejected()
    {
        var roots = Folder("roots");

        Assert.NotNull(Policy(roots).GetRejectionReason(Path.Combine(roots, "ws", "Gone"), 1, 2));
    }

    [Fact]
    public async Task A_repository_with_hooks_this_Worker_installed_is_accepted_before_any_root_is_known()
    {
        var repo = Folder("unknown", "Repo");
        GitScenarios.CommittedRepository(repo);
        await CreateGitService().WriteSyncHooksAsync(repo, workspaceId: 7, repositoryId: 42, CancellationToken.None);
        var policy = Policy();

        Assert.Null(policy.GetRejectionReason(repo, 7, 42));
        // The hook certifies its own ids only: another workspace or repository id is not vouched for.
        Assert.NotNull(policy.GetRejectionReason(repo, 7, 43));
        Assert.NotNull(policy.GetRejectionReason(repo, 8, 42));
    }

    [Fact]
    public async Task A_linked_worktree_of_a_repository_with_installed_hooks_is_accepted()
    {
        var main = Folder("unknown", "Main");
        GitScenarios.CommittedRepository(main);
        await CreateGitService().WriteSyncHooksAsync(main, workspaceId: 7, repositoryId: 42, CancellationToken.None);
        var worktree = Path.Combine(_root, "features", "f1", "Main");
        Directory.CreateDirectory(Path.GetDirectoryName(worktree)!);
        GitConfigTestSupport.Git(main, $"worktree add -b feature \"{worktree}\"");
        Assert.True(File.Exists(Path.Combine(worktree, ".git")));

        Assert.Null(Policy().GetRejectionReason(worktree, 7, 42));
    }

    [Fact]
    public void A_git_repository_without_GrayMoon_hooks_is_rejected_when_no_root_is_known()
    {
        var repo = Folder("unknown", "Plain");
        GitScenarios.CommittedRepository(repo);

        Assert.NotNull(Policy().GetRejectionReason(repo, 7, 42));
    }

    [Fact]
    public void Resolving_a_workspace_path_makes_its_root_known()
    {
        var root = Folder("learned");

        WorkerRepositoryPaths.GetWorkspacePath(root, "ws");

        Assert.True(KnownWorkspaceRoots.Shared.Contains(Path.Combine(root, "ws", "Repo")));
        Assert.False(KnownWorkspaceRoots.Shared.Contains(Path.Combine(_root, "never-named", "Repo")));
    }

    [Fact]
    public void The_known_root_list_is_bounded()
    {
        var known = new KnownWorkspaceRoots();
        for (var i = 0; i <= KnownWorkspaceRoots.MaxRoots; i++)
            known.Add(Path.Combine(_root, "many", $"r{i}"));

        Assert.True(known.Contains(Path.Combine(_root, "many", "r0", "Repo")));
        Assert.False(known.Contains(Path.Combine(_root, "many", $"r{KnownWorkspaceRoots.MaxRoots}", "Repo")));
    }
}

/// <summary>Review 2026-11-10, finding 22: the hook listener caps the body, checks the content type and vets the path.</summary>
public sealed class HookListenerHostedServiceTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("graymoon-f1-hook-").FullName;
    private readonly RecordingQueue _queue = new();
    private readonly HttpClient _http = new();
    private HookListenerHostedService _listener = null!;
    private string _repo = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        var roots = Path.Combine(_root, "roots");
        _repo = Path.Combine(roots, "ws", "Repo");
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        var known = new KnownWorkspaceRoots();
        known.Add(roots);

        // The port is free when probed; retry on the rare race with another process taking it first.
        for (var attempt = 0; ; attempt++)
        {
            _port = FreePort();
            _listener = new HookListenerHostedService(
                _queue,
                new NeverDefer(),
                Options.Create(new WorkerOptions { ListenPort = _port }),
                NullLogger<HookListenerHostedService>.Instance,
                new HookRepositoryPathPolicy(known));
            try
            {
                await _listener.StartAsync(CancellationToken.None);
                return;
            }
            catch (HttpListenerException) when (attempt < 5)
            {
            }
        }
    }

    public async Task DisposeAsync()
    {
        await _listener.StopAsync(CancellationToken.None);
        await _listener.DisposeAsync();
        _http.Dispose();
        TestDirectory.Delete(_root);
    }

    [Fact]
    public async Task Stopping_and_disposing_more_than_once_never_throws()
    {
        // On Linux a Stop followed by Close made the managed HttpListener rebind its port and throw
        // "Address already in use" out of shutdown; the listener is now closed exactly once.
        await _listener.StopAsync(CancellationToken.None);
        await _listener.StopAsync(CancellationToken.None);
        await _listener.DisposeAsync();
        await _listener.DisposeAsync();
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string Payload(string repositoryPath) =>
        $"{{\"repositoryId\":2,\"workspaceId\":1,\"repositoryPath\":\"{repositoryPath.Replace("\\", "\\\\")}\"}}";

    private Task<HttpResponseMessage> PostAsync(HttpContent content, string hook = "commit") =>
        _http.PostAsync($"http://127.0.0.1:{_port}/hook/{hook}", content);

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task A_hook_for_a_managed_repository_is_queued()
    {
        using var response = await PostAsync(Json(Payload(_repo)));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = Assert.IsType<NotifySyncJob>(Assert.Single(_queue.Jobs).NotifyJob);
        Assert.Equal(_repo, job.RepositoryPath);
        Assert.Equal(NotifyHookKind.Commit, job.HookKind);
    }

    [Fact]
    public async Task A_body_over_the_limit_gets_413_and_nothing_is_queued()
    {
        // The declared length alone is refused, before any body byte is read. See RawPostStatusLineAsync for why
        // this does not go through HttpClient.
        var statusLine = await RawPostStatusLineAsync("Content-Length: 1048576", []);

        Assert.StartsWith("HTTP/1.1 413", statusLine, StringComparison.Ordinal);
        Assert.Empty(_queue.Jobs);
    }

    /// <summary>
    /// Posts to /hook/commit over a plain socket and returns the status line. The listener answers 413 and closes
    /// without reading the rest of an oversized body; a client that is still uploading then fails with "Broken
    /// pipe" on Linux before it sees the answer, and HttpClient reports that as a failed request. So the over-limit
    /// tests send exactly the bytes the listener reads, and nothing after them.
    /// </summary>
    private async Task<string> RawPostStatusLineAsync(string bodyHeader, byte[] bodyBytes)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"POST /hook/commit HTTP/1.1\r\nHost: 127.0.0.1:{_port}\r\nContent-Type: application/json\r\n{bodyHeader}\r\n\r\n"));
        await stream.WriteAsync(bodyBytes);
        await stream.FlushAsync();

        using var reader = new StreamReader(stream, Encoding.ASCII);
        return await reader.ReadLineAsync() ?? string.Empty;
    }

    [Fact]
    public async Task A_chunked_body_over_the_limit_gets_413_and_nothing_is_queued()
    {
        // One chunk that ends exactly one byte past the limit, with the body left open: the listener has read
        // every byte that was sent when it answers.
        var body = Encoding.UTF8.GetBytes(Payload(_repo).PadRight(HookListenerHostedService.MaxBodyBytes + 1));
        var chunk = Encoding.ASCII.GetBytes($"{body.Length:X}\r\n").Concat(body).ToArray();

        var statusLine = await RawPostStatusLineAsync("Transfer-Encoding: chunked", chunk);

        Assert.StartsWith("HTTP/1.1 413", statusLine, StringComparison.Ordinal);
        Assert.Empty(_queue.Jobs);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    public async Task A_body_that_is_not_declared_as_json_gets_400_and_nothing_is_queued(string contentType)
    {
        // What a cross-site browser form or a "simple" fetch can send without a preflight.
        using var response = await PostAsync(new StringContent(Payload(_repo), Encoding.UTF8, contentType));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_queue.Jobs);
    }

    [Fact]
    public async Task A_path_outside_the_known_roots_gets_400_and_nothing_is_queued()
    {
        var outside = Path.Combine(_root, "elsewhere", "Repo");
        Directory.CreateDirectory(Path.Combine(outside, ".git"));

        using var response = await PostAsync(Json(Payload(outside)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_queue.Jobs);
    }

    [Fact]
    public async Task A_path_without_git_metadata_gets_400_and_nothing_is_queued()
    {
        var plain = Path.Combine(_root, "roots", "ws", "Plain");
        Directory.CreateDirectory(plain);

        using var response = await PostAsync(Json(Payload(plain)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_queue.Jobs);
    }

    [Fact]
    public async Task A_body_that_is_not_json_gets_400()
    {
        using var response = await PostAsync(Json("{not json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_queue.Jobs);
    }

    [Theory]
    [InlineData("application/json", true)]
    [InlineData("Application/JSON; charset=utf-8", true)]
    [InlineData("application/jsonp", false)]
    [InlineData("text/plain", false)]
    [InlineData(null, false)]
    public void Only_application_json_counts_as_json(string? contentType, bool expected)
    {
        Assert.Equal(expected, HookListenerHostedService.IsJsonContentType(contentType));
    }

    private sealed class RecordingQueue : IJobQueue
    {
        private readonly List<JobEnvelope> _jobs = [];

        public IReadOnlyList<JobEnvelope> Jobs { get { lock (_jobs) return [.. _jobs]; } }

        public ValueTask EnqueueAsync(JobEnvelope job, CancellationToken cancellationToken = default)
        {
            lock (_jobs) _jobs.Add(job);
            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<JobEnvelope> ReadAllAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public int Count { get { lock (_jobs) return _jobs.Count; } }
    }

    private sealed class NeverDefer : IWorktreeCreateHookDeferral
    {
        public IDisposable BeginCreate(string worktreePath) => throw new NotSupportedException();

        public bool TryDefer(INotifyJob job) => false;
    }
}
