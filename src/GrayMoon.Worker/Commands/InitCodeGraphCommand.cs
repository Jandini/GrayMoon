using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

/// <summary>
/// Gives a new Feature root a CodeGraph index when the Workspace root has one. Returns as soon as the build has
/// started; the build itself runs in the background (see <see cref="CodeGraphCli"/>).
/// </summary>
public sealed class InitCodeGraphCommand(CodeGraphCli codeGraph)
    : ICommandHandler<InitCodeGraphRequest, InitCodeGraphResponse>
{
    public Task<InitCodeGraphResponse> ExecuteAsync(InitCodeGraphRequest request, CancellationToken cancellationToken = default)
    {
        var sourceRoot = request.SourceRoot ?? throw new ArgumentException("sourceRoot required");
        var targetRoot = request.TargetRoot ?? throw new ArgumentException("targetRoot required");

        return Task.FromResult(new InitCodeGraphResponse { Outcome = Start(sourceRoot, targetRoot) });
    }

    private string Start(string sourceRoot, string targetRoot)
    {
        if (!CodeGraphCli.IsInitialized(sourceRoot))
            return "SourceNotInitialized";
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
