using System.Reflection;
using GrayMoon.Agent.Abstractions;

namespace GrayMoon.Agent.Jobs;

/// <summary>Helpers for extracting workspace context from a job envelope.</summary>
public static class JobEnvelopeExtensions
{
    /// <summary>Returns the workspace ID for the job if known; otherwise null (e.g. GetHostInfo).</summary>
    public static int? TryGetWorkspaceId(this JobEnvelope envelope)
    {
        if (envelope.Kind == JobKind.Notify && envelope.NotifyJob != null)
            return envelope.NotifyJob.WorkspaceId;

        if (envelope.Kind == JobKind.Command && envelope.CommandJob?.Request != null)
        {
            var type = envelope.CommandJob.Request.GetType();
            var prop = type.GetProperty("WorkspaceId", typeof(int));
            if (prop?.GetValue(envelope.CommandJob.Request) is int wid)
                return wid;
        }

        return null;
    }
}
