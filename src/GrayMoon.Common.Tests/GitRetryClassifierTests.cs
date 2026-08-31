using GrayMoon.Common.Git;

namespace GrayMoon.Common.Tests;

public class GitRetryClassifierTests
{
    [Fact]
    public void Exit_zero_is_never_retryable()
    {
        Assert.False(GitRetryClassifier.IsRetryable(0, "RPC failed", "Connection timed out"));
    }

    [Theory]
    [InlineData(1, null, null)]
    [InlineData(1, "", "")]
    [InlineData(1, "   ", "")]
    [InlineData(-1, null, null)]
    public void Empty_or_unknown_output_is_not_retryable(int exitCode, string? stdout, string? stderr)
    {
        Assert.False(GitRetryClassifier.IsRetryable(exitCode, stdout, stderr));
    }

    [Fact]
    public void Dirty_worktree_overwrite_on_pull_is_not_retryable()
    {
        const string stderr =
            """
            From https://github.com/Consilio-SARB/auroraverityreview-ingestion-index-service
             * branch            main       -> FETCH_HEAD
            error: Your local changes to the following files would be overwritten by merge:
            	src/AuroraVerityReview.Ingestion.Index.Service/AuroraVerityReview.Ingestion.Index.Service.csproj
            	src/AuroraVerityReview.Ingestion.Index.Tests/AuroraVerityReview.Ingestion.Index.Tests.csproj
            Please commit your changes or stash them before you merge.
            Aborting
            """;

        Assert.False(GitRetryClassifier.IsRetryable(1, "Updating 1511b0b..a2ba085", stderr));
    }

    [Theory]
    [InlineData("error: Your local changes to the following files would be overwritten by checkout:")]
    [InlineData("error: The following untracked working tree files would be overwritten by merge:")]
    [InlineData("error: The following untracked working tree files would be overwritten by checkout:")]
    [InlineData("Please commit your changes or stash them before you merge.")]
    [InlineData("CONFLICT (content): Merge conflict in src/File.cs")]
    [InlineData("Automatic merge failed; fix conflicts and then commit the result.")]
    [InlineData("fatal: Not possible to fast-forward, aborting.")]
    [InlineData("fatal: refusing to merge unrelated histories")]
    [InlineData("error: You have unmerged files.")]
    [InlineData("! [rejected]        feature -> feature (non-fast-forward)")]
    [InlineData("error: failed to push some refs to 'origin'\nhint: Updates were rejected because the remote contains work that you do not have locally.\nhint: (e.g., 'git pull ...')\nhint: See the 'Note about fast-forwards' in 'git push --help' for details.\n! [rejected] main -> main (fetch first)")]
    [InlineData("GH006: Protected branch hook declined. Changes must be made through a pull request.")]
    [InlineData("remote: error: GH013: Repository rule violations found")]
    [InlineData("remote: This repository was archived so it is read-only.\nfatal: unable to access 'https://github.com/org/repo.git/': The requested URL returned error: 403")]
    [InlineData("fatal: Authentication failed for 'https://github.com/org/repo.git/'")]
    [InlineData("Permission denied (publickey).")]
    [InlineData("fatal: unable to access 'https://github.com/org/repo.git/': The requested URL returned error: 401")]
    [InlineData("fatal: unable to access 'https://github.com/org/repo.git/': The requested URL returned error: 404")]
    [InlineData("remote: Repository not found.")]
    [InlineData("fatal: Couldn't find remote ref refs/heads/missing")]
    [InlineData("fatal: unable to access 'https://github.com/org/repo.git/': SSL certificate problem: unable to get local issuer certificate")]
    [InlineData("fatal: unable to access 'https://git.example.com/repo.git/': certificate verify failed")]
    [InlineData("fatal: not a git repository (or any of the parent directories): .git")]
    [InlineData("fatal: destination path 'repo' already exists and is not an empty directory.")]
    [InlineData("error: pathspec 'nope' did not match any file(s) known to git")]
    public void Definitive_errors_are_not_retryable(string stderr)
    {
        Assert.False(GitRetryClassifier.IsRetryable(1, null, stderr));
    }

    [Theory]
    [InlineData("fatal: unable to access 'https://github.com/org/repo.git/': Could not resolve host: github.com")]
    [InlineData("fatal: unable to access 'https://github.com/org/repo.git/': Couldn't resolve host 'github.com'")]
    [InlineData("fatal: unable to access 'https://github.com/org/repo.git/': Temporary failure in name resolution")]
    [InlineData("ssh: Could not resolve hostname github.com: Name or service not known")]
    [InlineData("fatal: unable to access 'https://github.com/org/repo.git/': Failed to connect to github.com port 443")]
    [InlineData("fatal: unable to access 'https://github.com/org/repo.git/': Connection timed out after 30000 milliseconds")]
    [InlineData("error: RPC failed; curl 56 Recv failure: Connection reset by peer")]
    [InlineData("fatal: The remote end hung up unexpectedly")]
    [InlineData("error: RPC failed; result=22, HTTP code = 502")]
    [InlineData("fatal: unable to access 'https://github.com/org/repo.git/': The requested URL returned error: 502")]
    [InlineData("fatal: unable to access 'https://github.com/org/repo.git/': The requested URL returned error: 503")]
    [InlineData("fatal: unable to access 'https://github.com/org/repo.git/': The requested URL returned error: 504")]
    [InlineData("fatal: unable to access 'https://github.com/org/repo.git/': The requested URL returned error: 500")]
    [InlineData("fatal: unable to access 'https://github.com/org/repo.git/': The requested URL returned error: 429")]
    [InlineData("fatal: unable to access 'https://github.com/org/repo.git/': The requested URL returned error: 408")]
    [InlineData("error: RPC failed; HTTP 502 curl 22 The requested URL returned error: 502")]
    [InlineData("error: RPC failed; curl 28 Operation timed out after 300000 milliseconds")]
    [InlineData("error: RPC failed; curl 7 Failed to connect")]
    [InlineData("error: RPC failed; curl 35 SSL connect error")]
    [InlineData("error: RPC failed; curl 52 Empty reply from server")]
    [InlineData("error: RPC failed; curl 92 HTTP/2 stream 0 was not closed cleanly")]
    [InlineData("fatal: protocol error: bad pack header")]
    [InlineData("git fetch_pack: expected ACK/NAK, got")]
    [InlineData("fatal: Early EOF")]
    [InlineData("LibreSSL SSL_connect: SSL_ERROR_SYSCALL in connection to github.com:443")]
    [InlineData("TLS packet with unexpected length was received")]
    [InlineData("! [remote rejected] main -> main (failed to lock)")]
    [InlineData("remote error: Internal Server Error")]
    [InlineData("fatal: Unable to create '/repo/.git/index.lock': File exists.")]
    [InlineData("fatal: Unable to create '/repo/.git/refs/heads/main.lock': File exists.")]
    [InlineData("The process cannot access the file because it is being used by another process.")]
    [InlineData("Operation timed out after 180s.")]
    [InlineData("Connection refused")]
    [InlineData("Network is unreachable")]
    [InlineData("No route to host")]
    public void Transient_errors_are_retryable(string stderr)
    {
        Assert.True(GitRetryClassifier.IsRetryable(1, null, stderr));
    }

    [Fact]
    public void Process_timeout_exit_code_is_retryable_when_timeout_message_is_present()
    {
        Assert.True(GitRetryClassifier.IsRetryable(-1, null, "Operation timed out after 180s."));
    }
}
