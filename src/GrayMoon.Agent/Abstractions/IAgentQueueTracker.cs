using GrayMoon.Agent.Jobs;

namespace GrayMoon.Agent.Abstractions;

/// <summary>Implemented by the tracked job queue. Called when a job has actually completed (success or failure) so pending count is updated after completion.</summary>
public interface IAgentQueueTracker
{
    void ReportJobCompleted(JobEnvelope envelope);
}
