using GrayMoon.Common.Git;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

/// <summary>
/// Makes the Workspace root folder itself a Git working tree for <see cref="AttachWorkspaceRepositoryRequest.CloneUrl"/>.
/// An empty root is cloned into in place. A non-empty root without Git is initialized, given an origin remote and
/// checked out on the remote default branch (or left on an unborn <c>main</c> when the remote is empty); files already
/// in the folder are never overwritten, and those that differ from the repository are reported as kept. A root that
/// already has a matching origin and a commit checked out is left alone; one whose earlier attach stopped before its
/// checkout is finished as a fresh attach. Never writes <c>graymoon.json</c> or <c>.gitignore</c> and never commits.
/// </summary>
public sealed class AttachWorkspaceRepositoryCommand(
    IGitService git,
    IGitRepositoryReader reader,
    IRepositoryConfigurationInitializer? configuration = null)
    : ICommandHandler<AttachWorkspaceRepositoryRequest, AttachWorkspaceRepositoryResponse>
{
    private const string FallbackUnbornBranch = "main";

    public async Task<AttachWorkspaceRepositoryResponse> ExecuteAsync(AttachWorkspaceRepositoryRequest request, CancellationToken cancellationToken = default)
    {
        var workspaceName = request.WorkspaceName ?? throw new ArgumentException("workspaceName required");
        var cloneUrl = request.CloneUrl ?? throw new ArgumentException("cloneUrl required");
        var workspaceRoot = request.WorkspaceRoot ?? throw new ArgumentException("workspaceRoot required");
        var bearerToken = request.BearerToken;

        var path = WorkerRepositoryPaths.GetWorkspacePath(workspaceRoot, workspaceName);

        // D14 step 1: a restore must never touch an existing, populated folder.
        if (request.RequireEmptyRoot && Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
        {
            return Fail("The folder already exists and is not empty.");
        }

        Directory.CreateDirectory(path);

        if (WorkerRepositoryPaths.HasGitMetadata(path))
        {
            var origin = await reader.GetRemoteOriginUrlAsync(path, cancellationToken);
            if (string.IsNullOrWhiteSpace(origin) || !RepositoryUrlIdentity.RepositoryUrlsEqual(origin, cloneUrl))
            {
                return Fail("Root already has a different Git repository");
            }

            // A first run that hit a checkout collision leaves .git with a matching origin and an unborn HEAD.
            // Finish the default-branch checkout now so a retry does not report success on an unborn HEAD.
            if (string.IsNullOrWhiteSpace(await reader.GetHeadCommitAsync(path, cancellationToken)))
            {
                var (refetchOk, refetchError) = await git.FetchAsync(path, includeTags: true, bearerToken, cancellationToken);
                if (!refetchOk)
                    return Fail(refetchError ?? "Git fetch failed.");

                var existingDefault = await FindFetchedDefaultBranchAsync(path, bearerToken, cancellationToken);
                if (!string.IsNullOrWhiteSpace(existingDefault))
                {
                    var (reCheckoutError, reKept) = await CheckoutKeepingFilesAsync(path, existingDefault, cancellationToken);
                    if (reCheckoutError is not null)
                        return Fail(reCheckoutError);

                    // An attach that stopped before its checkout is finished here, so this is a fresh attach:
                    // the App writes graymoon.json and .gitignore as it would have the first time.
                    await FinishAsync(request, path, cancellationToken);
                    return await SuccessAsync(path, cancellationToken, reKept);
                }
            }

            await FinishAsync(request, path, cancellationToken);
            var adopted = await SuccessAsync(path, cancellationToken);
            adopted.AdoptedExisting = true;
            return adopted;
        }

        if (!Directory.EnumerateFileSystemEntries(path).Any())
        {
            var cloned = await git.CloneIntoAsync(path, cloneUrl, bearerToken, cancellationToken);
            if (!cloned.Success || !WorkerRepositoryPaths.HasGitMetadata(path))
            {
                RemoveFailedCloneIntoContents(path);
                var message = cloned.Success
                    ? "Git clone reported success but did not create a repository."
                    : cloned.ErrorMessage ?? "Git clone failed.";
                return Fail(message);
            }

            var branchError = await EnsureRequestedBranchAsync(request, path, cancellationToken);
            if (branchError is not null)
                return Fail(branchError);

            await FinishAsync(request, path, cancellationToken);
            return await SuccessAsync(path, cancellationToken);
        }

        var (initOk, initError) = await git.InitAsync(path, cancellationToken);
        if (!initOk)
            return Fail(initError ?? "Git init failed.");

        // The default-branch checkout below can need paths over 260 characters on Windows.
        configuration?.EnsureWindowsLongPaths(path, cancellationToken);

        var (remoteOk, remoteError) = await git.AddRemoteAsync(path, "origin", cloneUrl, cancellationToken);
        if (!remoteOk)
            return Fail(remoteError ?? "Git remote add failed.");

        var (fetchOk, fetchError) = await git.FetchAsync(path, includeTags: true, bearerToken, cancellationToken);
        if (!fetchOk)
            return Fail(fetchError ?? "Git fetch failed.");

        var defaultBranch = string.IsNullOrWhiteSpace(request.BranchName)
            ? await FindFetchedDefaultBranchAsync(path, bearerToken, cancellationToken)
            : request.BranchName.Trim();

        if (!string.IsNullOrWhiteSpace(request.BranchName))
        {
            var requestedError = ValidateRequestedBranchName(defaultBranch!);
            if (requestedError is null
                && await reader.RevParseAsync(path, $"refs/remotes/origin/{defaultBranch}", cancellationToken) is null)
            {
                requestedError = BranchMissingMessage(defaultBranch!);
            }

            if (requestedError is not null)
                return Fail(requestedError);
        }

        IReadOnlyList<string> keptFiles = [];
        if (!string.IsNullOrWhiteSpace(defaultBranch))
        {
            (var checkoutError, keptFiles) = await CheckoutKeepingFilesAsync(path, defaultBranch, cancellationToken);
            if (checkoutError is not null)
                return Fail(checkoutError);
        }
        else
        {
            var (headOk, headError) = await git.SetUnbornHeadAsync(path, FallbackUnbornBranch, cancellationToken);
            if (!headOk)
                return Fail(headError ?? "Git symbolic-ref failed.");
        }

        await FinishAsync(request, path, cancellationToken);
        return await SuccessAsync(path, cancellationToken, keptFiles);
    }

    /// <summary>
    /// Checks out <paramref name="branch"/> in a folder that may already hold files. Git refuses a normal checkout
    /// when a file of the repository is already there untracked; the folder's files are then kept and the
    /// repository is checked out around them, so they show as modified instead of stopping the attach.
    /// </summary>
    private async Task<(string? Error, IReadOnlyList<string> KeptFiles)> CheckoutKeepingFilesAsync(string path, string branch, CancellationToken cancellationToken)
    {
        var (checkoutOk, checkoutError) = await git.CheckoutTrackingAsync(path, branch, cancellationToken);
        if (checkoutOk)
            return (null, []);

        var (keptOk, _, kept) = await git.CheckoutTrackingKeepingFilesAsync(path, branch, cancellationToken);
        // When that fails too, the first message is the one that says what is wrong.
        return keptOk ? (null, kept) : (CheckoutFailedPrefix + (checkoutError ?? "Git checkout failed."), []);
    }

    /// <summary>Says why git ran a checkout at all, in front of git's own message.</summary>
    internal const string CheckoutFailedPrefix =
        "The Workspace folder becomes the working copy of this repository, so its files are checked out into it. Git could not do that: ";

    private static string BranchMissingMessage(string branch) =>
        $"Branch '{branch}' was not found on the remote. It may have been deleted; choose another branch.";

    /// <summary>The branch name ends up in a git argument string, so only plain ref names are accepted.</summary>
    private static string? ValidateRequestedBranchName(string branch) =>
        OriginDefaultRef.IsPlainRefName(branch)
        && !branch.Contains("..", StringComparison.Ordinal)
        && !branch.Contains("@{", StringComparison.Ordinal)
        && !branch.StartsWith("refs/", StringComparison.Ordinal)
            ? null
            : $"'{branch}' is not a valid branch name.";

    /// <summary>
    /// After a clone (which checks out the remote default), switches to the requested remote branch and verifies HEAD
    /// is on it. Returns an error message, or null when no branch was requested or HEAD is on the requested branch.
    /// </summary>
    private async Task<string?> EnsureRequestedBranchAsync(AttachWorkspaceRepositoryRequest request, string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.BranchName))
            return null;

        var branch = request.BranchName.Trim();
        if (ValidateRequestedBranchName(branch) is { } invalid)
            return invalid;

        if (!string.Equals(await reader.GetCurrentBranchNameAsync(path, cancellationToken), branch, StringComparison.Ordinal))
        {
            if (await reader.RevParseAsync(path, $"refs/remotes/origin/{branch}", cancellationToken) is null)
                return BranchMissingMessage(branch);

            var (ok, error) = await git.CheckoutTrackingAsync(path, branch, cancellationToken);
            if (!ok)
                return error ?? "Git checkout failed.";
        }

        return string.Equals(await reader.GetCurrentBranchNameAsync(path, cancellationToken), branch, StringComparison.Ordinal)
            ? null
            : $"The Workspace repository could not be checked out on branch '{branch}'.";
    }

    // A remote that is empty can still name an unborn HEAD branch; only a branch the fetch actually brought
    // down counts as "the default exists".
    private async Task<string?> FindFetchedDefaultBranchAsync(string path, string? bearerToken, CancellationToken cancellationToken)
    {
        var defaultBranch = await git.GetRemoteDefaultBranchAsync(path, bearerToken, cancellationToken);
        if (!string.IsNullOrWhiteSpace(defaultBranch)
            && await reader.RevParseAsync(path, $"refs/remotes/origin/{defaultBranch}", cancellationToken) is null)
        {
            return null;
        }

        return defaultBranch;
    }

    private async Task FinishAsync(AttachWorkspaceRepositoryRequest request, string path, CancellationToken cancellationToken)
    {
        await git.AddSafeDirectoryAsync(path, cancellationToken);
        configuration?.EnsureWindowsLongPaths(path, cancellationToken);
        await git.WriteSyncHooksAsync(path, request.WorkspaceId, request.RepositoryId, cancellationToken);
    }

    private async Task<AttachWorkspaceRepositoryResponse> SuccessAsync(string path, CancellationToken cancellationToken, IReadOnlyList<string>? keptFiles = null)
    {
        var branch = await reader.GetCurrentBranchNameAsync(path, cancellationToken);
        var head = await reader.GetHeadCommitAsync(path, cancellationToken);
        return new AttachWorkspaceRepositoryResponse
        {
            Success = true,
            Branch = string.IsNullOrWhiteSpace(branch) ? null : branch,
            IsUnborn = string.IsNullOrWhiteSpace(head),
            KeptFiles = keptFiles is { Count: > 0 } ? keptFiles.Take(AttachWorkspaceRepositoryResponse.MaxKeptFiles).ToList() : null,
            KeptFileCount = keptFiles?.Count ?? 0,
        };
    }

    private static AttachWorkspaceRepositoryResponse Fail(string message) =>
        new() { Success = false, ErrorMessage = message };

    /// <summary>
    /// A clone into an empty workspace root that did not produce a repository may leave a partial <c>.git</c>.
    /// Remove only that residue so a retry can clone again. Never removes other files and never removes the root.
    /// </summary>
    private static void RemoveFailedCloneIntoContents(string path)
    {
        if (!Directory.Exists(path))
            return;

        var gitDir = Path.Combine(path, ".git");
        if (!Directory.Exists(gitDir) && !File.Exists(gitDir))
            return;

        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            if (!string.Equals(Path.GetFileName(entry), ".git", StringComparison.OrdinalIgnoreCase))
                return;
        }

        try
        {
            if (Directory.Exists(gitDir))
            {
                foreach (var file in Directory.EnumerateFiles(gitDir, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(gitDir, recursive: true);
            }
            else if (File.Exists(gitDir))
            {
                File.SetAttributes(gitDir, FileAttributes.Normal);
                File.Delete(gitDir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The caller still reports the clone failure. The root and any other files stay.
        }
    }
}
