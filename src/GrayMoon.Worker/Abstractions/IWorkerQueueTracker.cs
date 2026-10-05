using GrayMoon.Worker.Jobs;

namespace GrayMoon.Worker.Abstractions;

/// <summary>Implemented by the tracked job queue. Called when a job has actually completed (success or failure) so pending count is updated after completion.</summary>
public interface IWorkerQueueTracker
{
    void ReportJobCompleted(JobEnvelope envelope);
}
