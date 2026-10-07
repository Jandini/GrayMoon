using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Commands;

/// <summary>Read-only: lists the processes keeping each requested path in use ("Refresh blockers" in Remove Feature).</summary>
public sealed class InspectPathLocksCommand(IFileLockInspector lockInspector, ILogger<InspectPathLocksCommand> logger)
    : ICommandHandler<InspectPathLocksRequest, InspectPathLocksResponse>
{
    /// <summary>Upper bound on paths per request; a Feature has one worktree per repository.</summary>
    internal const int MaxPaths = 100;

    public async Task<InspectPathLocksResponse> ExecuteAsync(InspectPathLocksRequest request, CancellationToken cancellationToken = default)
    {
        var paths = request.Paths ?? [];
        if (paths.Count == 0)
            return new InspectPathLocksResponse { Success = false, ErrorMessage = "paths required" };
        if (paths.Count > MaxPaths)
            return new InspectPathLocksResponse { Success = false, ErrorMessage = $"At most {MaxPaths} paths can be inspected at once." };

        var results = new List<InspectPathLocksResult>(paths.Count);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                results.Add(new InspectPathLocksResult { Path = path, Exists = false, MayBeIncomplete = true, Diagnostic = "Not an absolute path." });
                continue;
            }

            if (!Directory.Exists(path) && !File.Exists(path))
            {
                results.Add(new InspectPathLocksResult { Path = path, Exists = false, BlockingProcesses = [] });
                continue;
            }

            try
            {
                var inspection = await lockInspector.InspectAsync(path, cancellationToken);
                results.Add(new InspectPathLocksResult
                {
                    Path = path,
                    Exists = true,
                    BlockingProcesses = BlockingProcessResponse.From(inspection.Processes),
                    MayBeIncomplete = inspection.MayBeIncomplete,
                    Diagnostic = inspection.Diagnostic,
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Lock inspection failed for {Path}", path);
                results.Add(new InspectPathLocksResult
                {
                    Path = path,
                    Exists = true,
                    BlockingProcesses = [],
                    MayBeIncomplete = true,
                    Diagnostic = "Could not find out which programs are using the folder.",
                });
            }
        }

        return new InspectPathLocksResponse { Success = true, Results = results };
    }
}
