using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

/// <summary>
/// Removes a Feature root's CodeGraph index so nothing CodeGraph owns holds the folder when Remove Feature deletes its
/// worktrees. Stops an index build still running there, kills the MCP daemon (and any other CodeGraph process) an AI
/// tool left holding the index, then runs <c>codegraph uninit</c>. The Feature root is going away, so when uninit
/// fails or CodeGraph is not installed the <c>.codegraph</c> folder is simply deleted.
/// </summary>
public sealed class UninitCodeGraphCommand(CodeGraphCli codeGraph)
    : ICommandHandler<UninitCodeGraphRequest, UninitCodeGraphResponse>
{
    public async Task<UninitCodeGraphResponse> ExecuteAsync(UninitCodeGraphRequest request, CancellationToken cancellationToken = default)
    {
        var root = request.Root ?? throw new ArgumentException("root required");

        // A stopped build may already have created a partial index, so check for one only after it has stopped.
        var stoppedBuild = await codeGraph.StopInitAsync(root, cancellationToken);
        if (!CodeGraphCli.IsInitialized(root) && !(stoppedBuild && Directory.Exists(Path.Combine(root, CodeGraphCli.DataFolderName))))
            return new UninitCodeGraphResponse { Outcome = "NotInitialized" };

        await codeGraph.KillIndexProcessesAsync(root, cancellationToken);

        string? uninitError = null;
        if (codeGraph.ResolveExecutable() is { } executable)
        {
            var result = await codeGraph.UninitAsync(executable, root, cancellationToken);
            if (result.ExitCode == 0)
                return new UninitCodeGraphResponse { Outcome = "Removed" };

            uninitError = CodeGraphCli.FirstLine(result.Stderr) ?? CodeGraphCli.FirstLine(result.Stdout)
                ?? $"codegraph uninit exited with code {result.ExitCode}.";
        }

        if (await codeGraph.DeleteIndexAsync(root, cancellationToken))
            return new UninitCodeGraphResponse { Outcome = "Removed" };

        return new UninitCodeGraphResponse
        {
            Outcome = "Failed",
            Message = uninitError ?? "The CodeGraph index folder is still in use.",
        };
    }
}
