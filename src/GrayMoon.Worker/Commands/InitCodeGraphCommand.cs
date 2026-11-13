using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

/// <summary>
/// Starts a CodeGraph index for a directory and returns as soon as the build has started. The build itself runs in
/// the background (see <see cref="CodeGraphCli"/>). The directory is a Feature root or the Workspace folder.
/// </summary>
public sealed class InitCodeGraphCommand(CodeGraphCli codeGraph)
    : ICommandHandler<InitCodeGraphRequest, InitCodeGraphResponse>
{
    public Task<InitCodeGraphResponse> ExecuteAsync(InitCodeGraphRequest request, CancellationToken cancellationToken = default)
    {
        var targetRoot = request.TargetRoot ?? throw new ArgumentException("targetRoot required");

        return Task.FromResult(new InitCodeGraphResponse { Outcome = Start(targetRoot) });
    }

    private string Start(string targetRoot)
    {
        if (!Directory.Exists(targetRoot))
            return "TargetMissing";
        if (codeGraph.IsInitRunning(targetRoot))
            return "AlreadyRunning";
        if (CodeGraphCli.IsInitialized(targetRoot))
            return "AlreadyInitialized";

        var executable = codeGraph.ResolveExecutable();
        if (executable is null)
            return "NotInstalled";

        return codeGraph.TryStartInit(executable, targetRoot) ? "Started" : "AlreadyRunning";
    }
}
