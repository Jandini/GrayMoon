using System.ComponentModel;
using System.Diagnostics;
using GrayMoon.Worker.Abstractions;
using GrayMoon.Worker.Models;
using GrayMoon.Worker.Platform.Windows;

namespace GrayMoon.Worker.Services;

/// <summary>Ends a process with <see cref="Process.Kill()"/> (TerminateProcess on Windows) after re-checking its start time.</summary>
public sealed class ProcessTerminator : IProcessTerminator
{
    public string Terminate(int processId, DateTime startTimeUtc, TimeSpan waitForExit)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return TerminateProcessOutcome.AlreadyExited;
        }

        using (process)
        {
            try
            {
                // Last identity check right before ending it: the id must still belong to the same process.
                var actualStart = ReadStartTimeUtc(process);
                if (actualStart is null || actualStart.Value != startTimeUtc)
                    return TerminateProcessOutcome.StartTimeChanged;

                process.Kill();
                return process.WaitForExit(waitForExit) ? TerminateProcessOutcome.Killed : TerminateProcessOutcome.Failed;
            }
            catch (InvalidOperationException)
            {
                return TerminateProcessOutcome.AlreadyExited;
            }
            catch (Win32Exception)
            {
                return TerminateProcessOutcome.AccessDenied;
            }
            catch (NotSupportedException)
            {
                return TerminateProcessOutcome.Failed;
            }
        }
    }

    public bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static DateTime? ReadStartTimeUtc(Process process)
    {
        if (OperatingSystem.IsWindows())
            return WindowsProcessInspector.TryGetStartTimeUtc(process.Id);

        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }
}
