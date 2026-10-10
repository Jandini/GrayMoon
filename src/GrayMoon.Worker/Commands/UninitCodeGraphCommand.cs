using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

/// <summary>
/// Removes a Feature root's CodeGraph index with <c>codegraph uninit</c>, after stopping an index build still running
/// there, so nothing CodeGraph owns holds the folder when Remove Feature deletes its worktrees.
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

        var executable = codeGraph.ResolveExecutable();
        if (executable is null)
            return new UninitCodeGraphResponse { Outcome = "NotInstalled", Message = "CodeGraph is not installed on the Worker host." };

        var result = await codeGraph.UninitAsync(executable, root, cancellationToken);
        if (result.ExitCode == 0)
            return new UninitCodeGraphResponse { Outcome = "Removed" };

        return new UninitCodeGraphResponse
        {
            Outcome = "Failed",
            Message = CodeGraphCli.FirstLine(result.Stderr) ?? CodeGraphCli.FirstLine(result.Stdout) ?? $"codegraph uninit exited with code {result.ExitCode}.",
        };
    }
}
