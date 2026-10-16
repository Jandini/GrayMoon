using GrayMoon.Common;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using GrayMoon.Worker.Services;

namespace GrayMoon.Worker.Commands;

/// <summary>
/// Streams the output of the background CodeGraph index build in a Feature root as this command's output, so the App
/// can show it in the loading overlay. Returns when the build ends or after a bounded wait (<c>Running</c>); cancelling
/// the command (the overlay's Background button) only stops the watch, never the build.
/// </summary>
public sealed class WatchCodeGraphInitCommand(CodeGraphCli codeGraph)
    : ICommandHandler<WatchCodeGraphInitRequest, WatchCodeGraphInitResponse>
{
    public async Task<WatchCodeGraphInitResponse> ExecuteAsync(WatchCodeGraphInitRequest request, CancellationToken cancellationToken = default)
    {
        var root = request.Root ?? throw new ArgumentException("root required");

        var (outcome, next) = await codeGraph.WatchInitAsync(
            root, Math.Max(0, request.From), CommandLineStreamAmbient.Current.Value, cancellationToken);
        return new WatchCodeGraphInitResponse { Outcome = outcome, Next = next };
    }
}
