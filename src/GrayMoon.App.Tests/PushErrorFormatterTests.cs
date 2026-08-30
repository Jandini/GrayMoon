using GrayMoon.App.Services.Workspaces;

namespace GrayMoon.App.Tests;

public sealed class PushErrorFormatterTests
{
    [Fact]
    public void Format_archived_403_is_read_only_message()
    {
        const string raw =
            "remote: This repository was archived so it is read-only.\n" +
            "fatal: unable to access 'https://github.com/org/repo.git/': The requested URL returned error: 403";

        var message = PushErrorFormatter.Format(raw);

        Assert.Equal("Push rejected: this repository is archived on GitHub and is read-only.", message);
        Assert.True(PushErrorFormatter.IsArchivedRejection(raw));
    }

    [Fact]
    public void Format_non_fast_forward_asks_to_pull()
    {
        const string raw =
            "! [rejected]        feature -> feature (non-fast-forward)\n" +
            "error: failed to push some refs";

        var message = PushErrorFormatter.Format(raw);

        Assert.Equal("Push rejected: remote has new commits. Fetching latest state - pull and retry.", message);
        Assert.True(PushErrorFormatter.IsNonFastForwardRejection(raw));
    }

    [Fact]
    public void Format_protected_branch_mentions_pull_request()
    {
        const string raw = "GH006: Protected branch hook declined. Changes must be made through a pull request.";

        var message = PushErrorFormatter.Format(raw);

        Assert.StartsWith("Push rejected: the remote branch is protected.", message);
        Assert.True(PushErrorFormatter.IsProtectedBranchRejection(raw));
    }

    [Fact]
    public void Format_null_is_generic_failure()
    {
        Assert.Equal("Push failed", PushErrorFormatter.Format(null));
    }
}
