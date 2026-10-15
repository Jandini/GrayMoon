using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GrayMoon.Common;
using GrayMoon.Common.Git;
using GrayMoon.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GrayMoon.Worker.Tests;

/// <summary>
/// Runs the installed reference-transaction hook against a real repository and a listener standing in for the Worker, to
/// prove the shell filter: a branch or remote-branch ref that is created or deleted reports, anything else does not.
/// The hook runs on every ref update, so a false positive here is a request per commit and a false negative is a branch
/// GrayMoon never learns about.
/// </summary>
public sealed class ReferenceTransactionHookTests : IDisposable
{
    private readonly TempGitRepositoryFixture _repo = new();
    private readonly HttpListener _listener;
    private readonly ConcurrentQueue<(string Path, string Body)> _posts = new();
    private readonly Task _listen;

    public ReferenceTransactionHookTests()
    {
        (_listener, var port) = StartListener();
        _listen = Task.Run(ListenAsync);

        var inner = new CommandLineService(NullLogger<CommandLineService>.Instance, Options.Create(new ProcessExecutionOptions()));
        var runner = new GitProcessRunner(inner, Options.Create(new GitProcessOptions()), NullLogger<GitProcessRunner>.Instance);
        var reader = new GitCliRepositoryReader(runner, NullLogger<GitCliRepositoryReader>.Instance);
        var git = new GitService(Options.Create(new WorkerOptions { ListenPort = port }), NullLogger<GitService>.Instance, runner, reader, new LibGit2SharpGitIgnoreService());

        // The first commit creates the unborn branch, which is itself a reported create, so hooks go in afterwards.
        _repo.CommitInitial();
        git.WriteSyncHooksAsync(_repo.RepositoryPath, 7, 9, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _listener.Close();
        try { _listen.GetAwaiter().GetResult(); } catch { /* listener closed */ }
        _repo.Dispose();
    }

    /// <summary>
    /// Picks a free port and binds the listener to it. Probing then releasing a port leaves a window in which another test class
    /// running in parallel (or any process) can take it, so a failed bind retries on a new port instead of failing the test.
    /// </summary>
    private static (HttpListener Listener, int Port) StartListener()
    {
        const int attempts = 20;
        for (var attempt = 1; ; attempt++)
        {
            var port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return (listener, port);
            }
            catch (HttpListenerException) when (attempt < attempts)
            {
                listener.Close();
            }
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task ListenAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
            _posts.Enqueue((context.Request.Url!.AbsolutePath, await reader.ReadToEndAsync()));
            context.Response.StatusCode = 202;
            context.Response.Close();
        }
    }

    /// <summary>Runs <paramref name="git"/> and returns the refs reports it produced. Hooks post synchronously, so none are in flight afterwards.</summary>
    private List<string> RefsReportsFrom(params string[] git)
    {
        _posts.Clear();
        var (exitCode, _, stderr) = _repo.RunGit(git);
        Assert.True(exitCode == 0, $"git {string.Join(' ', git)} failed: {stderr}");
        return _posts.Where(p => p.Path == "/hook/refs").Select(p => p.Body).ToList();
    }

    [Fact]
    public void Creating_a_branch_reports_with_the_repository_identity()
    {
        var reports = RefsReportsFrom("branch", "feature/x");

        var body = Assert.Single(reports);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(9, json.RootElement.GetProperty("repositoryId").GetInt32());
        Assert.Equal(7, json.RootElement.GetProperty("workspaceId").GetInt32());
        Assert.Equal(
            Path.GetFullPath(_repo.RepositoryPath).TrimEnd('\\', '/'),
            Path.GetFullPath(json.RootElement.GetProperty("repositoryPath").GetString()!).TrimEnd('\\', '/'),
            ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public void Deleting_a_branch_reports()
    {
        RefsReportsFrom("branch", "gone");

        Assert.Single(RefsReportsFrom("branch", "-d", "gone"));
    }

    [Fact]
    public void Renaming_a_branch_reports()
    {
        RefsReportsFrom("branch", "old-name");

        Assert.NotEmpty(RefsReportsFrom("branch", "-m", "old-name", "new-name"));
    }

    [Fact]
    public void Checking_out_a_new_branch_reports()
    {
        Assert.NotEmpty(RefsReportsFrom("checkout", "-b", "topic"));
    }

    [Fact]
    public void A_commit_does_not_report()
    {
        _repo.WriteFile("next.txt", "next\n");
        _repo.RunGit("add", "next.txt");

        Assert.Empty(RefsReportsFrom("commit", "-m", "next"));
    }

    [Fact]
    public void Moving_an_existing_branch_does_not_report()
    {
        RefsReportsFrom("branch", "mover");
        _repo.WriteFile("move.txt", "move\n");
        _repo.RunGit("add", "move.txt");
        RefsReportsFrom("commit", "-m", "move");

        // With the old value stated, as git itself does for commits, fetches and pushes, the move is a plain update.
        Assert.Empty(RefsReportsFrom("update-ref", "refs/heads/mover", "HEAD", RevParse("mover")));
    }

    private string RevParse(string rev) => _repo.RunGit("rev-parse", rev).Stdout.Trim();

    [Fact]
    public void Tags_and_the_stash_do_not_report()
    {
        Assert.Empty(RefsReportsFrom("tag", "v1.0.0"));
        Assert.Empty(RefsReportsFrom("update-ref", "refs/stash", "HEAD"));
    }

    [Fact]
    public void A_remote_branch_appearing_and_disappearing_reports_but_updating_it_does_not()
    {
        Assert.Single(RefsReportsFrom("update-ref", "refs/remotes/origin/topic", "HEAD"));

        _repo.WriteFile("remote.txt", "remote\n");
        _repo.RunGit("add", "remote.txt");
        RefsReportsFrom("commit", "-m", "remote");
        Assert.Empty(RefsReportsFrom("update-ref", "refs/remotes/origin/topic", "HEAD", RevParse("refs/remotes/origin/topic")));

        Assert.Single(RefsReportsFrom("update-ref", "-d", "refs/remotes/origin/topic"));
    }

    [Fact]
    public void The_remote_head_symref_does_not_report()
    {
        Assert.Empty(RefsReportsFrom("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main"));
    }
}
