using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Jobs.Requests;
using GrayMoon.Worker.Jobs.Response;
using Microsoft.Extensions.Logging;

namespace GrayMoon.Worker.Commands;

/// <summary>Read-only: lists the processes keeping each requested path in use (the Remove Feature dialog check and "Refresh blockers").</summary>
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

        var results = await InspectAsync(lockInspector, paths, logger, cancellationToken);
        return new InspectPathLocksResponse { Success = true, Results = results };
    }

    /// <summary>
    /// One result per path in request order. A relative or missing path is reported without inspecting it; all existing paths
    /// are inspected together, so a process is attributed to the most specific of them. Never throws for an inspection failure.
    /// </summary>
    internal static async Task<List<InspectPathLocksResult>> InspectAsync(
        IFileLockInspector lockInspector,
        IReadOnlyList<string> paths,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var results = new InspectPathLocksResult[paths.Count];
        var existing = new List<int>();
        for (var i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                results[i] = new InspectPathLocksResult { Path = path, Exists = false, MayBeIncomplete = true, Diagnostic = "Not an absolute path." };
            else if (!Directory.Exists(path) && !File.Exists(path))
                results[i] = new InspectPathLocksResult { Path = path, Exists = false, BlockingProcesses = [] };
            else
                existing.Add(i);
        }

        if (existing.Count > 0)
        {
            try
            {
                var inspections = await lockInspector.InspectManyAsync(existing.Select(i => paths[i]).ToList(), cancellationToken);
                for (var k = 0; k < existing.Count; k++)
                {
                    var inspection = inspections[k];
                    results[existing[k]] = new InspectPathLocksResult
                    {
                        Path = paths[existing[k]],
                        Exists = true,
                        BlockingProcesses = BlockingProcessResponse.From(inspection.Processes),
                        MayBeIncomplete = inspection.MayBeIncomplete,
                        Diagnostic = inspection.Diagnostic,
                    };
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Lock inspection failed for {PathCount} path(s)", existing.Count);
                foreach (var i in existing)
                {
                    results[i] = new InspectPathLocksResult
                    {
                        Path = paths[i],
                        Exists = true,
                        BlockingProcesses = [],
                        MayBeIncomplete = true,
                        Diagnostic = "Could not find out which programs are using the folder.",
                    };
                }
            }
        }

        return [.. results];
    }
}
